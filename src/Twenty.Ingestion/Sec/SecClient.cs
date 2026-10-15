using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Twenty.Ingestion.Sec;

public interface ISecClock
{
    DateTimeOffset UtcNow { get; }
    TimeSpan MonotonicNow { get; }
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}
public sealed class SecSystemClock : ISecClock
{
    public static readonly SecSystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeSpan MonotonicNow => TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
public sealed class SecRequestGate(ISecClock clock)
{
    public static readonly SecRequestGate Shared = new(SecSystemClock.Instance);
    private readonly SemaphoreSlim gate = new(1, 1);
    private TimeSpan next;
    public async Task<IDisposable> Acquire(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (next > clock.MonotonicNow) await clock.Delay(next - clock.MonotonicNow, ct);
            next = clock.MonotonicNow + TimeSpan.FromMilliseconds(500); // Shared across both SEC origins and retries.
            return new Lease(gate);
        }
        catch { gate.Release(); throw; }
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
public sealed class SecClient
{
    private readonly HttpClient http;
    private readonly SecArchive archive;
    private readonly SecLimits limits;
    private readonly string userAgent;
    private readonly ISecClock clock;
    private readonly SecRequestGate gate;
    private readonly SemaphoreSlim serial = new(1, 1);
    public int Requests { get; private set; }
    public SecClient(HttpClient http, SecArchive archive, string contact, SecLimits? limits = null,
        ISecClock? clock = null, SecRequestGate? gate = null)
    {
        this.http = http; this.archive = archive; this.limits = limits ?? new(); this.limits.Validate();
        userAgent = SecIdentity.UserAgent(contact);
        this.clock = clock ?? SecSystemClock.Instance; this.gate = gate ?? SecRequestGate.Shared;
    }
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        MaxConnectionsPerServer = 1, ConnectTimeout = TimeSpan.FromSeconds(10), UseCookies = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<SecPayload> Fetch(Uri uri, bool json, Action<byte[], string, DateTimeOffset> validate, CancellationToken ct)
    {
        SecIdentity.AllowedUri(uri);
        await serial.WaitAsync(ct);
        try { return await FetchSerial(uri, json, validate, ct); }
        finally { serial.Release(); }
    }
    private async Task<SecPayload> FetchSerial(Uri uri, bool json, Action<byte[], string, DateTimeOffset> validate, CancellationToken ct)
    {
        var cached = archive.Cached(uri);
        if (cached is not null && cached.CheckedAt > clock.UtcNow) throw new SecCollectionException("archive-clock");
        if (cached is not null && clock.UtcNow - cached.CheckedAt < limits.CacheLifetime)
            return ValidateCached(uri, cached, validate);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (Requests >= limits.MaximumRequests) throw new SecCollectionException("request-budget-exhausted");
            using var access = await gate.Acquire(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(json ? "application/json" : "text/html"));
            if (cached?.Receipt.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
            if (cached?.Receipt.LastModified is { } modified) request.Headers.IfModifiedSince = modified;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limits.RequestTimeout);
            Requests++;
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.RequestMessage?.RequestUri is { } final && final != uri)
                    throw new SecCollectionException("redirect-refused");
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (cached is null) throw new SecCollectionException("unexpected-not-modified");
                    var payload = ValidateCached(uri, cached, validate);
                    archive.Checked(uri, cached.Receipt, clock.UtcNow);
                    return payload;
                }
                var status = (int)response.StatusCode;
                if (status == 429 || status is 500 or 502 or 503 or 504)
                {
                    if (attempt == 2) throw new SecCollectionException("sec-retries-exhausted");
                    var retry = response.Headers.RetryAfter;
                    var wait = retry?.Delta ?? (retry?.Date is { } date ? date - clock.UtcNow :
                        TimeSpan.FromSeconds(status == 429 ? 15 : Math.Pow(2, attempt + 1)));
                    if (wait > TimeSpan.FromSeconds(60)) throw new SecCollectionException("sec-retry-after-limit");
                    await clock.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(500), ct);
                    continue;
                }
                if (status != 200) throw new SecCollectionException(status == 403 ? "sec-access-denied" : "sec-http-status");
                if (response.Content.Headers.ContentLength > limits.MaximumResponseBytes)
                    throw new SecCollectionException("response-size-limit");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var output = new MemoryStream();
                var block = new byte[8192];
                while (true)
                {
                    var count = await input.ReadAsync(block.AsMemory(0, (int)Math.Min(block.Length,
                        limits.MaximumResponseBytes + 1L - output.Length)), timeout.Token);
                    if (count == 0) break;
                    output.Write(block, 0, count);
                    if (output.Length > limits.MaximumResponseBytes) throw new SecCollectionException("response-size-limit");
                }
                var bytes = output.ToArray();
                var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                var now = clock.UtcNow;
                try
                {
                    if (json && contentType != "application/json") throw new SecCollectionException("sec-json-content-type");
                    validate(bytes, contentType, now);
                }
                catch (SecCollectionException error)
                {
                    archive.Quarantine(uri, bytes, error.Code, now);
                    throw;
                }
                var receipt = archive.Store(uri, bytes, contentType, response.Headers.ETag?.ToString(),
                    response.Content.Headers.LastModified, now);
                return new(bytes, receipt);
            }
            catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt == 2) throw new SecCollectionException("sec-transport-exhausted");
                await clock.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct);
            }
        }
        throw new SecCollectionException("sec-retries-exhausted");
    }
    private SecPayload ValidateCached(Uri uri, SecCacheEntry cached, Action<byte[], string, DateTimeOffset> validate)
    {
        var bytes = archive.Bytes(cached.Receipt);
        try { validate(bytes, cached.Receipt.ContentType, cached.Receipt.FirstSeenAt); }
        catch (SecCollectionException error) { archive.Quarantine(uri, bytes, error.Code, clock.UtcNow); throw; }
        return new(bytes, cached.Receipt);
    }
}
