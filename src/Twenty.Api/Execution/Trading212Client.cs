using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Twenty.Core.Execution;
using Twenty.Api.Research;
using Twenty.Core.Research;

namespace Twenty.Api.Execution;

public enum BrokerEnvironment { Demo, Live }
public sealed record BrokerSummary(long Id, string Currency, BrokerCash Cash);
public sealed record BrokerCash(decimal AvailableToTrade, decimal ReservedForOrders);
public sealed record BrokerOrder(long Id, string Ticker, string Status, decimal Quantity, decimal FilledQuantity,
    DateTimeOffset? CreatedAt = null, decimal? LimitPrice = null, string? Type = null, string? Side = null,
    string? Currency = null, string? InitiatedFrom = null, string? Strategy = null, string? TimeInForce = null);
public sealed record SubmissionResult(string Outcome, BrokerOrder? Order, string? ErrorCode);

// Deliberately has no retry policy. The upstream POST has no idempotency key.
// The caller must durably mark submitting BEFORE invoking PlaceLimit and reconcile unknown outcomes.
// This transport is not a strategy approval, a scheduler, or a complete live execution coordinator.
public sealed class Trading212Client : IBrokerGateway
{
    private readonly HttpClient client;
    private readonly string authorization;
    private readonly Uri origin;
    private readonly SemaphoreSlim serial = new(1, 1);
    private DateTimeOffset nextRequest;
    private readonly TimeSpan operationTimeout;
    private readonly BrokerEvidenceArchive? evidence;
    private long? lastObservedAccountId;
    private static readonly HttpRequestOptionsKey<string> IntentContext = new("TwentyLocalIntentId");
    public BrokerEnvironment Environment { get; }
    public Trading212Client(HttpClient client, BrokerEnvironment environment, string key, string secret, TimeSpan? operationTimeout = null, BrokerEvidenceArchive? evidence = null)
    {
        if (environment is not (BrokerEnvironment.Demo or BrokerEnvironment.Live)) throw new ResearchException("broker-environment");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret) || key.Contains(':') || key.Length > 4096 || secret.Length > 4096)
            throw new ResearchException("broker-credentials");
        this.evidence = evidence;
        this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(15);
        if (this.operationTimeout < TimeSpan.FromMilliseconds(10) || this.operationTimeout > TimeSpan.FromMinutes(1)) throw new ResearchException("broker-timeout-config");
        this.client = client; Environment = environment;
        origin = new Uri(environment == BrokerEnvironment.Demo ? "https://demo.trading212.com" : "https://live.trading212.com");
        authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":" + secret));
    }
    public async Task<BrokerSummary> Summary(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(operationTimeout); ct = deadline.Token;
        using var response = await Send(HttpMethod.Get, "/api/v0/equity/account/summary", null, ct);
        if (!response.IsSuccessStatusCode) throw new ResearchException("broker-read-unavailable");
        var summary = await Read<BrokerSummary>(response, ct);
        if (summary.Id <= 0 || string.IsNullOrWhiteSpace(summary.Currency) || summary.Cash is null || summary.Cash.AvailableToTrade < 0)
            throw new ResearchException("broker-schema");
        lastObservedAccountId = summary.Id;
        return summary; // Does NOT prove ISA type. Account-summary schema contains no such field.
    }
    public async Task<SubmissionResult> PlaceLimit(BuyIntent intent, CancellationToken ct, string? localIntentId = null)
    {
        if (intent.Quantity <= 0 || intent.LimitPrice <= 0 || intent.QuoteCurrency is not ("GBP" or "GBX") || string.IsNullOrWhiteSpace(intent.BrokerTicker))
            throw new ResearchException("broker-order-inputs");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(operationTimeout); ct = deadline.Token;
        try
        {
            using var response = await Send(HttpMethod.Post, "/api/v0/equity/orders/limit",
                new { ticker = intent.BrokerTicker, quantity = intent.Quantity, limitPrice = intent.LimitPrice, timeValidity = "DAY" }, ct, localIntentId);
            if (!response.IsSuccessStatusCode) await Capture(response, ct);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new("rejected", null, "broker-rejected-" + (int)response.StatusCode);
            if (!response.IsSuccessStatusCode) return new("unknown", null, "broker-submission-unknown");
            var order = await Read<BrokerOrder>(response, ct);
            if (order.Id <= 0 || order.Ticker != intent.BrokerTicker || order.Quantity != intent.Quantity || order.FilledQuantity < 0 || order.FilledQuantity > intent.Quantity || string.IsNullOrWhiteSpace(order.Status))
                return new("unknown", null, "broker-response-mismatch");
            return new("acknowledged", order, null); // Acknowledgement is not a settled fill.
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or ResearchException or IOException)
        { return new("unknown", null, "broker-submission-unknown"); }
    }
    public async Task<BrokerOrder?> PendingOrder(long id, CancellationToken ct)
    {
        if (id <= 0) throw new ResearchException("broker-order-inputs");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(operationTimeout); ct = deadline.Token;
        using var response = await Send(HttpMethod.Get, $"/api/v0/equity/orders/{id}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // Requires history + fills reconciliation; never infer cancelled/filled.
        if (!response.IsSuccessStatusCode) throw new ResearchException("broker-read-unavailable");
        var order = await Read<BrokerOrder>(response, ct);
        if (order.Id != id) throw new ResearchException("broker-response-mismatch");
        return order;
    }
    public async Task<bool> RequestCancel(long id, CancellationToken ct)
    {
        if (id <= 0) throw new ResearchException("broker-order-inputs");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(operationTimeout); ct = deadline.Token;
        using var response = await Send(HttpMethod.Delete, $"/api/v0/equity/orders/{id}", null, ct);
        return response.IsSuccessStatusCode; // Accepted cancellation still needs final fill/status reconciliation.
    }
    public async Task<BrokerPosition[]> Positions(CancellationToken ct)
    {
        var positions = await Get<BrokerPosition[]>("/api/v0/equity/positions", ct);
        if (positions.Length > 10000 || positions.Any(p => p?.Instrument is null || string.IsNullOrWhiteSpace(p.Instrument.Ticker) || p.Quantity < 0) ||
            positions.Select(p => p.Instrument.Ticker).Distinct(StringComparer.Ordinal).Count() != positions.Length)
            throw new ResearchException("broker-positions-schema");
        return positions;
    }
    public async Task<BrokerOrder[]> PendingOrders(CancellationToken ct)
    {
        var orders = await Get<BrokerOrder[]>("/api/v0/equity/orders", ct);
        if (orders.Length > 10000 || orders.Any(o => o is null || o.Id <= 0 || string.IsNullOrWhiteSpace(o.Ticker)) || orders.Select(o => o.Id).Distinct().Count() != orders.Length)
            throw new ResearchException("broker-orders-schema");
        return orders;
    }
    public async Task<BrokerHistorySnapshot> History(CancellationToken ct)
    {
        var items = new List<BrokerHistoryItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var path = "/api/v0/equity/history/orders?limit=50";
        for (var page = 1; page <= 8; page++)
        {
            ValidateHistoryPath(path);
            if (!seen.Add(path)) throw new ResearchException("broker-pagination-cycle");
            var response = await Get<BrokerHistoryPage>(path, ct);
            if (response.Items is null || response.Items.Length > 50 || response.Items.Any(i => i?.Order is null || i.Order.Id <= 0))
                throw new ResearchException("broker-history-schema");
            items.AddRange(response.Items);
            if (response.NextPagePath is null) return new(items.ToArray(), true, page);
            ValidateHistoryPath(response.NextPagePath);
            path = response.NextPagePath;
        }
        return new(items.ToArray(), false, 8); // Reaching the bound is never evidence that an absent order did not execute.
    }
    public static void ValidateHistoryPath(string path)
    {
        const string allowed = "/api/v0/equity/history/orders";
        if (path is null || path.Length > 512 || !(path == allowed || path.StartsWith(allowed + "?", StringComparison.Ordinal)) || path.Contains('#'))
            throw new ResearchException("broker-pagination-origin");
        if (path == allowed) return;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in path[(allowed.Length + 1)..].Split('&'))
        {
            var parts = pair.Split('=');
            if (parts.Length != 2 || !keys.Add(parts[0])) throw new ResearchException("broker-pagination-query");
            var valid = parts[0] switch
            {
                "cursor" => long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var cursor) && cursor >= 0,
                "limit" => int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limit) && limit is >= 1 and <= 50,
                "ticker" => parts[1].Length is > 0 and <= 80 && parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'),
                _ => false
            };
            if (!valid) throw new ResearchException("broker-pagination-query");
        }
    }
    private async Task<T> Get<T>(string path, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(operationTimeout);
        using var response = await Send(HttpMethod.Get, path, null, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new ResearchException("broker-read-unavailable");
        return await Read<T>(response, deadline.Token);
    }
    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, CancellationToken ct, string? localIntentId = null)
    {
        await serial.WaitAsync(ct);
        try
        {
            var delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            nextRequest = DateTimeOffset.UtcNow.AddSeconds(5); // Deliberately below endpoint budgets; other apps share the account limit.
            using var request = new HttpRequestMessage(method, new Uri(origin, path));
            if (localIntentId is not null) request.Options.Set(IntentContext, localIntentId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (evidence is not null)
                evidence.Record(Environment, method.Method, path, null, request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct), true, localIntentId, lastObservedAccountId);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.RequestMessage ??= request;
            if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0" &&
                response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var seconds))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (seconds >= now && seconds <= now + 3600) nextRequest = DateTimeOffset.FromUnixTimeSeconds(seconds).AddSeconds(1);
            }
            return response;
        }
        finally { serial.Release(); }
    }
    private async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var bytes = await Capture(response, ct);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        CheckDuplicateFields(document.RootElement);
        var options = new JsonSerializerOptions(ResearchStore.Json) { RespectRequiredConstructorParameters = true, MaxDepth = 32 };
        return JsonSerializer.Deserialize<T>(bytes, options) ?? throw new ResearchException("broker-schema");
    }
    private static void CheckDuplicateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new ResearchException("broker-schema-duplicate"); CheckDuplicateFields(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckDuplicateFields(item);
    }
    private async Task<byte[]> Capture(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new ResearchException("broker-response-limit");
        using var buffer = new MemoryStream(); var complete = false;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var chunk = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(chunk, ct)) > 0)
            { if (buffer.Length + count > 2 * 1024 * 1024) throw new ResearchException("broker-response-limit"); buffer.Write(chunk, 0, count); }
            complete = true; return buffer.ToArray();
        }
        finally
        {
            evidence?.Record(Environment, response.RequestMessage?.Method.Method ?? "UNKNOWN", response.RequestMessage?.RequestUri?.PathAndQuery ?? "unknown",
                (int)response.StatusCode, buffer.ToArray(), complete, response.RequestMessage?.Options.TryGetValue(IntentContext, out var intentId) == true ? intentId : null, lastObservedAccountId);
        }
    }
}
