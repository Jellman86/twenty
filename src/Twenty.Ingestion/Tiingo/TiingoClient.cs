using System.Globalization;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;

namespace Twenty.Ingestion.Tiingo;

public sealed record TiingoHistoryRequest(string Symbol, DateOnly From, DateOnly To);
public sealed record TiingoHistoryResult(string Symbol, int Rows, DateOnly? First, DateOnly? Last, string CaptureId, string Sha256);
public sealed record TiingoBar(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close,
    decimal AdjClose, decimal Volume, decimal DivCash, decimal SplitFactor);
public sealed record TiingoQuota(DateTimeOffset LastAttemptAt, string Month, string Day, int DayRequests, long MonthBytes, ImmutableArray<string> Symbols);

/// <summary>Explicit, bounded read-only requests. Captured response bodies exclude request/authentication headers.</summary>
public sealed partial class TiingoClient(HttpClient http, CaptureStore store, string token, TimeProvider? clock = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    public const long MonthlyByteLimit = 800_000_000, MaximumResponseBytes = 8 * 1024 * 1024;
    public const int DailyRequestLimit = 900, MonthlySymbolLimit = 500, MinimumRequestSeconds = 90;
    [GeneratedRegex("^[A-Z][A-Z0-9.-]{0,19}$", RegexOptions.CultureInvariant)] private static partial Regex SymbolPattern();
    public static string? ReadToken(string? value, string? file)
    {
        if (!string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(file)) throw new ResearchException("tiingo-secret-configuration-conflict");
        if (!string.IsNullOrWhiteSpace(file))
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 4096) throw new ResearchException("tiingo-secret-file-invalid");
            value = File.ReadAllText(file);
        }
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length is < 16 or > 256 || value.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ResearchException("tiingo-secret-invalid");
        return value;
    }
    public object Status() { var s = store.Status("tiingo-api"); var quota = store.PrivateState<TiingoQuota>("tiingo-quota");
        return new { configured = true, s.LastAttemptAt, s.LastSuccessAt, s.PausedUntil, s.LastError,
        minimumRequestIntervalSeconds = MinimumRequestSeconds, monthlySymbolLimit = MonthlySymbolLimit, dailyRequestLimit = DailyRequestLimit,
        monthlyByteLimit = MonthlyByteLimit, quota = quota is null ? null : new { quota.Month, quota.Day, quota.DayRequests, quota.MonthBytes, uniqueSymbols = quota.Symbols.Length }, automaticBackfill = false }; }

    public async Task<bool> Check(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var bytes = await Get(new Uri("https://api.tiingo.com/api/test/"), null, ct);
            using var json = JsonDocument.Parse(bytes);
            var valid = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String && message.GetString() == "You successfully sent a request";
            if (!valid) throw new ResearchException("tiingo-auth-response-invalid");
            Success(); return true;
        }
        catch (JsonException) { throw new ResearchException("tiingo-auth-response-invalid"); }
        finally { gate.Release(); }
    }
    public async Task<TiingoHistoryResult> Capture(TiingoHistoryRequest request, CancellationToken ct)
    {
        var symbol = request.Symbol?.ToUpperInvariant() ?? "";
        if (!SymbolPattern().IsMatch(symbol) || request.From < new DateOnly(1960, 1, 1) || request.To < request.From ||
            request.To > DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime) || request.To.DayNumber - request.From.DayNumber > 20000)
            throw new ResearchException("tiingo-history-request");
        await gate.WaitAsync(ct);
        try
        {
            var uri = new Uri("https://api.tiingo.com/tiingo/daily/" + Uri.EscapeDataString(symbol) + "/prices?startDate=" + request.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                "&endDate=" + request.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var bytes = await Get(uri, symbol, ct);
            var dates = ValidateHistory(bytes, request.From, request.To);
            var observation = new CaptureObservation(Guid.NewGuid().ToString("N"), "tiingo-history-" + symbol.ToLowerInvariant().Replace('.', '-'),
                uri.AbsoluteUri, CaptureStore.Hash(bytes), bytes.Length, "application/json", time.GetUtcNow(), "tiingo-read-only", false);
            store.Record(observation, bytes, false); Success();
            return new(symbol, dates.Count, dates.FirstOrDefault() is var first && first != default ? first : null,
                dates.LastOrDefault() is var last && last != default ? last : null, observation.Id, observation.Sha256);
        }
        finally { gate.Release(); }
    }
    public static IReadOnlyList<DateOnly> ValidateHistory(byte[] bytes, DateOnly from, DateOnly to)
    {
        var bars = ParseHistory(bytes);
        if (bars.Any(b => b.Date < from || b.Date > to)) throw new ResearchException("tiingo-history-date");
        return bars.Select(b => b.Date).ToList();
    }
    /// <summary>Parses captured history bytes with the same fail-closed row rules as capture validation, minus the request window.</summary>
    public static ImmutableArray<TiingoBar> ParseHistory(byte[] bytes)
    {
        if (bytes.Length is 0 or > 8 * 1024 * 1024) throw new ResearchException("tiingo-response-size");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 20000) throw new ResearchException("tiingo-history-shape");
            var bars = ImmutableArray.CreateBuilder<TiingoBar>();
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != row.EnumerateObject().Count() ||
                    !DateTimeOffset.TryParse(row.GetProperty("date").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant) ||
                    instant.Offset != TimeSpan.Zero || instant.TimeOfDay != TimeSpan.Zero) throw new ResearchException("tiingo-history-row");
                var date = DateOnly.FromDateTime(instant.UtcDateTime);
                if (bars.Count > 0 && date <= bars[^1].Date) throw new ResearchException("tiingo-history-date");
                decimal Number(string name) => row.GetProperty(name).TryGetDecimal(out var value) ? value : throw new ResearchException("tiingo-history-number");
                var open = Number("open"); var high = Number("high"); var low = Number("low"); var close = Number("close");
                var adjClose = Number("adjClose"); var volume = Number("volume"); var divCash = Number("divCash"); var splitFactor = Number("splitFactor");
                if (open <= 0 || low <= 0 || high < low || open < low || open > high || close < low || close > high ||
                    adjClose <= 0 || volume < 0 || divCash < 0 || splitFactor <= 0) throw new ResearchException("tiingo-history-number");
                bars.Add(new(date, open, high, low, close, adjClose, volume, divCash, splitFactor));
            }
            return bars.ToImmutable();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new ResearchException("tiingo-history-shape"); }
    }

    private async Task<byte[]> Get(Uri uri, string? symbol, CancellationToken ct)
    {
        var now = time.GetUtcNow(); var status = store.Status("tiingo-api");
        if (status.PausedUntil > now) throw new ResearchException("tiingo-source-paused");
        var month = now.ToString("yyyyMM", CultureInfo.InvariantCulture); var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        store.UpdatePrivateState<TiingoQuota>("tiingo-quota", previous =>
        {
            if (previous is not null && now < previous.LastAttemptAt.AddSeconds(MinimumRequestSeconds)) throw new ResearchException("tiingo-request-not-due");
            var symbols = previous?.Month == month ? previous.Symbols : [];
            var bytes = previous?.Month == month ? previous.MonthBytes : 0;
            var requests = previous?.Day == day ? previous.DayRequests : 0;
            if (requests >= DailyRequestLimit) throw new ResearchException("tiingo-daily-request-limit");
            if (bytes + MaximumResponseBytes > MonthlyByteLimit) throw new ResearchException("tiingo-monthly-byte-limit");
            if (symbol is not null && !symbols.Contains(symbol))
            {
                if (symbols.Length >= MonthlySymbolLimit) throw new ResearchException("tiingo-monthly-symbol-limit");
                symbols = symbols.Add(symbol);
            }
            return new(now, month, day, requests + 1, bytes + MaximumResponseBytes, symbols);
        }); // Reserve worst-case bytes before sending; failures/crashes keep the reservation.
        store.SaveStatus(status with { LastAttemptAt = now }); // Failed/ambiguous requests consume quota too; survives restart.
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        request.Headers.Accept.ParseAdd("application/json"); request.Content = new ByteArrayContent([]); request.Content.Headers.ContentType = new("application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode switch { HttpStatusCode.Unauthorized => "tiingo-auth-rejected", HttpStatusCode.Forbidden => "tiingo-permission-denied",
                    HttpStatusCode.TooManyRequests => "tiingo-rate-limited", HttpStatusCode.NotFound => "tiingo-symbol-unavailable", _ => "tiingo-read-unavailable" };
                store.SaveStatus(store.Status("tiingo-api") with { LastError = code, PausedUntil = now.AddHours(response.StatusCode == HttpStatusCode.TooManyRequests ? 24 : 1) });
                throw new ResearchException(code);
            }
            if (response.Headers.Location is not null || response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new ResearchException("tiingo-response-size-or-redirect");
            using var input = await response.Content.ReadAsStreamAsync(deadline.Token); using var output = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
            { if (output.Length + count > 8 * 1024 * 1024) throw new ResearchException("tiingo-response-size"); output.Write(buffer, 0, count); }
            var result = output.ToArray();
            store.UpdatePrivateState<TiingoQuota>("tiingo-quota", q => q!.Month == month ? q with { MonthBytes = q.MonthBytes - MaximumResponseBytes + result.Length } : q);
            return result;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        { throw new ResearchException("tiingo-read-unavailable"); }
    }
    private void Success() => store.SaveStatus(store.Status("tiingo-api") with { LastSuccessAt = time.GetUtcNow(), LastError = null, PausedUntil = null });
}
