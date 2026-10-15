using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Twenty.Core.Execution;
using Twenty.Core.Research;

namespace Twenty.Api.Execution;

public enum BrokerEnvironment { Demo, Live }
public sealed record BrokerSummary(long Id, string Currency, BrokerCash Cash);
public sealed record BrokerCash(decimal AvailableToTrade, decimal ReservedForOrders);
public sealed record BrokerOrder(long Id, string Ticker, string Status, decimal Quantity, decimal FilledQuantity);
public sealed record SubmissionResult(string Outcome, BrokerOrder? Order, string? ErrorCode);

// Deliberately has no retry policy. The upstream POST has no idempotency key.
// The caller must durably mark submitting BEFORE invoking PlaceLimit and reconcile unknown outcomes.
// This transport is not a strategy approval, a scheduler, or a complete live execution coordinator.
public sealed class Trading212Client
{
    private readonly HttpClient client;
    private readonly string authorization;
    private readonly Uri origin;
    private readonly SemaphoreSlim serial = new(1, 1);
    private DateTimeOffset nextRequest;
    private readonly TimeSpan operationTimeout;
    public BrokerEnvironment Environment { get; }
    public Trading212Client(HttpClient client, BrokerEnvironment environment, string key, string secret, TimeSpan? operationTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret) || key.Contains(':') || key.Length > 4096 || secret.Length > 4096)
            throw new ResearchException("broker-credentials");
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
        return summary; // Does NOT prove ISA type. Account-summary schema contains no such field.
    }
    public async Task<SubmissionResult> PlaceLimit(BuyIntent intent, CancellationToken ct)
    {
        if (intent.Quantity <= 0 || intent.LimitPrice <= 0 || intent.QuoteCurrency is not ("GBP" or "GBX") || string.IsNullOrWhiteSpace(intent.BrokerTicker))
            throw new ResearchException("broker-order-inputs");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(operationTimeout); ct = deadline.Token;
        try
        {
            using var response = await Send(HttpMethod.Post, "/api/v0/equity/orders/limit",
                new { ticker = intent.BrokerTicker, quantity = intent.Quantity, limitPrice = intent.LimitPrice, timeValidity = "DAY" }, ct);
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
    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try
        {
            var delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            nextRequest = DateTimeOffset.UtcNow.AddSeconds(5); // Deliberately below endpoint budgets; other apps share the account limit.
            using var request = new HttpRequestMessage(method, new Uri(origin, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
            if (body is not null) request.Content = JsonContent.Create(body);
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        finally { serial.Release(); }
    }
    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new ResearchException("broker-response-limit");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        { if (buffer.Length + count > 2 * 1024 * 1024) throw new ResearchException("broker-response-limit"); buffer.Write(chunk, 0, count); }
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), Research.ResearchStore.Json) ?? throw new ResearchException("broker-schema");
    }
}
