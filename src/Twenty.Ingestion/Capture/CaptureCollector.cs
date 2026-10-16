using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Twenty.Ingestion.Capture;

public sealed record CaptureOptions
{
    public string Collector { get; init; } = "local";
    public string UserAgent { get; init; } = "Twenty-collector/1.0 (+https://github.com/Jellman86/twenty)";
    public TimeSpan PerHostInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>First pause after a block (403, 429 or a bot challenge); doubles on each repeat.</summary>
    public TimeSpan BlockedPause { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan FailurePause { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan MaximumPause { get; init; } = TimeSpan.FromDays(7);
}

public sealed record CaptureOutcome(string SourceId, string Result, string? Code = null, CaptureObservation? Observation = null);

/// <summary>
/// Fetches due sources one request at a time. Unchanged content is confirmed with conditional requests.
/// A block or challenge pauses the source and is never retried from elsewhere; a validation failure stores nothing.
/// </summary>
public sealed class CaptureCollector(HttpClient http, CaptureStore store, CaptureRole role, CaptureOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> nextByHost = new(StringComparer.OrdinalIgnoreCase);

    public static HttpClient CreateHttpClient(Uri? proxy = null) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseCookies = false,
        MaxConnectionsPerServer = 1, ConnectTimeout = TimeSpan.FromSeconds(20),
        Proxy = proxy is null ? null : new WebProxy(proxy), UseProxy = proxy is not null,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public bool Due(CaptureSource source, CaptureStatus status, DateTimeOffset now) =>
        status.PausedUntil is not { } paused || paused <= now
            ? status.LastSuccessAt is not { } last || now - last >= source.Cadence
            : false;

    public async Task<IReadOnlyList<CaptureOutcome>> RunDue(IEnumerable<CaptureSource> sources, CancellationToken ct)
    {
        var outcomes = new List<CaptureOutcome>();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            var status = store.Status(source.Id);
            if (!Due(source, status, time.GetUtcNow())) { outcomes.Add(new(source.Id, "not-due")); continue; }
            outcomes.Add(await Collect(source, status, ct));
        }
        return outcomes;
    }

    public async Task<CaptureOutcome> Collect(CaptureSource source, CaptureStatus status, CancellationToken ct)
    {
        var attempted = time.GetUtcNow();
        try
        {
            var url = source.Url;
            if (source.Discovers)
            {
                var page = await Get(source, url, null, expectHtml: true, ct);
                if (page.Bytes is null) throw new CaptureException("discovery-page-unchanged");
                var match = source.DiscoverLink!.Match(Encoding.UTF8.GetString(page.Bytes));
                if (!match.Success) throw new CaptureException("discovery-link-missing");
                var href = WebUtility.HtmlDecode(match.Groups["url"].Value);
                url = new Uri(href.StartsWith("//", StringComparison.Ordinal) ? "https:" + href : href);
                Allowed(source, url);
            }
            // Only reuse validators for the URL they came from; a discovered link may change between runs.
            var previous = status.LastSha256 is { } sha && store.HasObject(sha) ? status : null;
            var response = await Get(source, url, previous, expectHtml: false, ct);
            var now = time.GetUtcNow();
            CaptureObservation observation;
            if (response.Bytes is null)
            {
                observation = new(Guid.NewGuid().ToString("N"), source.Id, url.AbsoluteUri, previous!.LastSha256!, store.Bytes(previous.LastSha256!).LongLength,
                    response.ContentType, now, options.Collector, NotModified: true, previous.ETag, previous.LastModified);
                store.Record(observation, null, role == CaptureRole.Remote);
            }
            else
            {
                if (source.Validate(response.Bytes) is { } invalid) throw new CaptureException("invalid-" + invalid);
                observation = new(Guid.NewGuid().ToString("N"), source.Id, url.AbsoluteUri, CaptureStore.Hash(response.Bytes), response.Bytes.LongLength,
                    response.ContentType, now, options.Collector, NotModified: false, response.ETag, response.LastModified);
                store.Record(observation, response.Bytes, role == CaptureRole.Remote);
            }
            store.SaveStatus(status with { LastAttemptAt = attempted, LastSuccessAt = now, LastSha256 = observation.Sha256,
                ETag = observation.ETag, LastModified = observation.LastModified, ConsecutiveFailures = 0, PausedUntil = null, LastError = null,
                Observations = status.Observations + 1 });
            return new(source.Id, observation.NotModified ? "unchanged" : "captured", Observation: observation);
        }
        catch (Exception error) when (error is CaptureException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            var code = error switch { CaptureException c => c.Code, HttpRequestException => "transport-failed", _ => "request-timeout" };
            var failures = status.ConsecutiveFailures + 1;
            var blocked = code is "blocked" or "rate-limited" or "challenge";
            var basePause = blocked ? options.BlockedPause : options.FailurePause;
            var pause = TimeSpan.FromTicks(Math.Min(options.MaximumPause.Ticks, basePause.Ticks * (1L << Math.Min(failures - 1, 10))));
            store.SaveStatus(status with { LastAttemptAt = attempted, ConsecutiveFailures = failures, PausedUntil = time.GetUtcNow() + pause, LastError = code });
            return new(source.Id, blocked ? "paused" : "failed", code);
        }
    }

    private sealed record Response(byte[]? Bytes, string ContentType, string? ETag, DateTimeOffset? LastModified);

    private async Task<Response> Get(CaptureSource source, Uri url, CaptureStatus? previous, bool expectHtml, CancellationToken ct)
    {
        for (var redirects = 0; ; redirects++)
        {
            Allowed(source, url);
            await Pace(url, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
            if (previous?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
            if (previous?.LastModified is { } modified) request.Headers.IfModifiedSince = modified;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.RequestTimeout);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 3 || response.Headers.Location is not { } location) throw new CaptureException("redirect-limit");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            if (response.Headers.TryGetValues("cf-mitigated", out var mitigated) && mitigated.Contains("challenge")) throw new CaptureException("challenge");
            if (status == 304) return previous is null ? throw new CaptureException("unexpected-not-modified") : new(null, "", null, null);
            if (status == 429) throw new CaptureException("rate-limited");
            if (status is 401 or 403) throw new CaptureException("blocked");
            if (status != 200) throw new CaptureException("http-" + status);
            if (response.Content.Headers.ContentLength > CaptureStore.MaximumCaptureBytes) throw new CaptureException("response-too-large");
            var bytes = await ReadBounded(response, timeout.Token);
            var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "application/octet-stream";
            // A challenge or consent page served with 200 in place of a file means the source is unavailable.
            if (!expectHtml && contentType == "text/html") throw new CaptureException("challenge");
            return new(bytes, contentType, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
        }
    }

    private static async Task<byte[]> ReadBounded(HttpResponseMessage response, CancellationToken ct)
    {
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > CaptureStore.MaximumCaptureBytes) throw new CaptureException("response-too-large");
        }
        return output.ToArray();
    }

    private async Task Pace(Uri url, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (nextByHost.TryGetValue(url.Host, out var next) && next > now) await Task.Delay(next - now, time, ct);
        nextByHost[url.Host] = time.GetUtcNow() + options.PerHostInterval;
    }

    private static void Allowed(CaptureSource source, Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length > 0 || !source.AllowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
            throw new CaptureException("url-not-allowed");
    }
}

/// <summary>Sends a remote collector's queued captures to the receiving Twenty instance and drops them once acknowledged.</summary>
public sealed class CaptureShipper(HttpClient http, CaptureStore store, Uri endpoint, string token)
{
    public const string ObservationHeader = "X-Twenty-Capture";

    public async Task<(int Shipped, string? Error)> ShipAll(CancellationToken ct)
    {
        var shipped = 0;
        foreach (var observation in store.Outbox())
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Twenty-Request", "1");
            request.Headers.TryAddWithoutValidation(ObservationHeader, Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(observation,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));
            request.Content = new ByteArrayContent(store.Bytes(observation.Sha256));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            try
            {
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return (shipped, "ship-http-" + (int)response.StatusCode);
            }
            catch (HttpRequestException) { return (shipped, "ship-transport-failed"); }
            store.Shipped(observation);
            shipped++;
        }
        return (shipped, null);
    }
}
