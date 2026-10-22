using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Tiingo;

namespace Twenty.Ingestion.Listings;

public sealed record TerminalSource(string Id, string Url, DateTimeOffset RetrievedAt, string ContentSha256, string Representation);
public sealed record TerminalConsideration(string Kind, decimal? CashPerShare, string? Currency,
    string? SuccessorSymbol, decimal? SharesPerShare, decimal? RightsPerShare);
public sealed record TerminalSubject(string Symbol, string EventKind, DateOnly EventDate, DateOnly? LastTradingDate,
    DateOnly? SuspensionDate, TerminalConsideration Consideration, ImmutableArray<string> SourceIds);
public sealed record TerminalReferences(string SchemaVersion, ImmutableArray<TerminalSource> Sources,
    ImmutableArray<TerminalSubject> Subjects);
public sealed record TerminalHistoryResult(TerminalSubject Reference, string HistorySha256, string HistoryCaptureId,
    DateTimeOffset HistoryCapturedAt, DateOnly RequestedFrom, DateOnly RequestedTo, int Bars,
    DateOnly? FirstBar, DateOnly? LastBar, int PostLastTradingBars, ImmutableArray<DateOnly> PostLastTradingDates,
    bool TerminalDateChecked, ImmutableArray<string> Issues);
public sealed record TerminalHistoryReport(string AuditVersion, string ReferenceSha256, ImmutableArray<TerminalSource> Sources,
    ImmutableArray<TerminalHistoryResult> Subjects, ImmutableArray<string> Limitations)
{
    public bool Valid => Subjects.All(s => s.Issues.IsEmpty);
}

/// <summary>Offline, manually sourced terminal-date comparison. Never mints a terminal action,
/// infers proceeds from a close, repairs history or establishes historical availability.</summary>
public static class TerminalHistoryAudit
{
    public const string Version = "terminal-history-audit-v1";

    public static TerminalHistoryReport Build(CaptureStore store, byte[] reference, Func<string, byte[]> sourceBytes)
    {
        var refs = ReadReferences(reference);
        foreach (var source in refs.Sources)
        {
            var payload = sourceBytes(source.ContentSha256);
            if (payload.Length is 0 or > 8 * 1024 * 1024 || CaptureStore.Hash(payload) != source.ContentSha256)
                throw new ResearchException("terminal-source-integrity");
        }
        var results = ImmutableArray.CreateBuilder<TerminalHistoryResult>();
        foreach (var subject in refs.Subjects)
        {
            var captures = TiingoHistoryIdentity.Observations(store, subject.Symbol, "terminal-history-request");
            if (captures.Count == 0) throw new ResearchException("terminal-history-not-captured");
            var latestAt = captures.Max(c => c.ObservedAt);
            var latest = captures.Where(c => c.ObservedAt == latestAt).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
            // Equal payloads with different request windows are still conflicting evidence.
            if (latest.Select(c => (c.Sha256, c.Url, c.Bytes)).Distinct().Count() != 1)
                throw new ResearchException("terminal-history-capture-conflict");
            var capture = latest[0];
            if (!TiingoHistoryIdentity.Matches(capture, subject.Symbol) || capture.Bytes is < 0 or > 8 * 1024 * 1024 ||
                capture.Sha256 is not { Length: 64 } || capture.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)) ||
                !Guid.TryParseExact(capture.Id, "N", out _) || capture.ObservedAt == default)
                throw new ResearchException("terminal-history-integrity");
            var history = store.Bytes(capture.Sha256);
            if (capture.Bytes != history.Length) throw new ResearchException("terminal-history-integrity");
            var (from, to) = RequestWindow(capture.Url, subject.Symbol);
            var bars = TiingoClient.ParseHistory(history);
            var issues = ImmutableArray.CreateBuilder<string>();
            if (bars.IsEmpty) issues.Add("history-empty");
            if (bars.Any(b => b.Date < from || b.Date > to || b.Date > DateOnly.FromDateTime(capture.ObservedAt.UtcDateTime)))
                issues.Add("history-outside-request-or-capture-date");
            var terminal = subject.EventKind == "completed-merger";
            if (!terminal) issues.Add("not-a-terminal-event");
            if (to < subject.EventDate) issues.Add("request-does-not-reach-event");
            if (from > subject.EventDate) issues.Add("request-starts-after-event");
            var post = subject.LastTradingDate is { } last ? bars.Where(b => b.Date > last).Select(b => b.Date).ToArray() : [];
            var checkedDate = false;
            if (terminal)
            {
                if (subject.LastTradingDate is not { } lastDate) issues.Add("last-trading-date-unverified");
                else
                {
                    var covers = from <= lastDate && to >= lastDate;
                    if (!covers) issues.Add("request-does-not-cover-last-trading-date");
                    else if (!bars.Any(b => b.Date == lastDate)) issues.Add("last-trading-bar-missing");
                    if (post.Length > 0) issues.Add("post-last-trading-bars");
                    checkedDate = covers && bars.Any(b => b.Date == lastDate) && post.Length == 0 &&
                        !issues.Contains("history-outside-request-or-capture-date");
                }
                if (subject.Consideration.Kind != "cash-only") issues.Add("consideration-not-supported");
            }
            results.Add(new(subject, capture.Sha256, capture.Id, capture.ObservedAt, from, to, bars.Length,
                bars.IsEmpty ? null : bars[0].Date, bars.IsEmpty ? null : bars[^1].Date, post.Length,
                post.Take(100).ToImmutableArray(), checkedDate, issues.ToImmutable()));
        }
        return new(Version, CaptureStore.Hash(reference), refs.Sources, results.ToImmutable(),
            ["Declared symbols and captured request windows only. Producer coverage ends, merger completion, last trading and suspension dates are distinct.",
             "Source hashes bind manually reviewed assertions and the declared original-response or web-tool-extraction representation; they do not authenticate sources or establish historical availability. Updated notice headlines are not availability timestamps.",
             "Post-last-trading rows require investigation, not automatic deletion. A clean date comparison does not certify session completeness, raw/adjusted prices, terminal proceeds, payment timing, taxes, FX or investment returns.",
             "Cash amounts describe stated common-share entitlements, not actual account receipts. Mixed stock/cash and contingent rights are unsupported. No dataset, action, model trial, holdout or broker state is changed."]);
    }

    /// <summary>Reads a reviewed reference bundle with the same fail-closed shape rules the audit applies.</summary>
    public static TerminalReferences ReadReferences(byte[] bytes)
    {
        if (bytes.Length is 0 or > 1024 * 1024) throw new ResearchException("terminal-reference-size");
        var refs = JsonSerializer.Deserialize<TerminalReferences>(bytes, DatasetJson.Options)
            ?? throw new ResearchException("terminal-reference-shape");
        if (refs.SchemaVersion != "terminal-reference.v1" || refs.Sources.IsDefaultOrEmpty || refs.Sources.Length > 100 ||
            refs.Subjects.IsDefaultOrEmpty || refs.Subjects.Length > 50 || refs.Sources.Any(s => s is null ||
                string.IsNullOrWhiteSpace(s.Id) || s.Id.Length > 64 || s.RetrievedAt == default ||
                s.ContentSha256 is not { Length: 64 } || s.ContentSha256.Any(c => !char.IsAsciiHexDigitLower(c)) ||
                s.Representation is not ("original-response" or "web-tool-extraction") ||
                !Uri.TryCreate(s.Url, UriKind.Absolute, out var u) || u.Scheme != "https" || !string.IsNullOrEmpty(u.UserInfo)) ||
            refs.Sources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != refs.Sources.Length)
            throw new ResearchException("terminal-reference-shape");
        var ids = refs.Sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (refs.Subjects.Any(s => s is null || !Symbol(s.Symbol) || s.EventDate == default ||
            s.EventKind is not ("completed-merger" or "continued-listing-notice") ||
            s.LastTradingDate == default(DateOnly) || s.SuspensionDate == default(DateOnly) ||
            s.LastTradingDate > s.EventDate || s.SuspensionDate <= s.LastTradingDate ||
            s.SourceIds.IsDefaultOrEmpty || s.SourceIds.Length > 10 || s.SourceIds.Any(id => !ids.Contains(id)) ||
            s.SourceIds.Distinct(StringComparer.Ordinal).Count() != s.SourceIds.Length || !Consideration(s)) ||
            refs.Subjects.Select(s => s.Symbol).Distinct(StringComparer.Ordinal).Count() != refs.Subjects.Length)
            throw new ResearchException("terminal-reference-subject");
        return refs;
    }

    private static bool Consideration(TerminalSubject subject)
    {
        var c = subject.Consideration;
        if (c is null) return false;
        if (subject.EventKind == "continued-listing-notice")
            return subject.LastTradingDate is null && subject.SuspensionDate is null && c.Kind == "none" &&
                c.CashPerShare is null && c.Currency is null && c.SuccessorSymbol is null && c.SharesPerShare is null && c.RightsPerShare is null;
        if (c.CashPerShare is not > 0 || c.Currency is not { Length: 3 } || c.Currency.Any(ch => !char.IsAsciiLetterUpper(ch))) return false;
        return c.Kind switch
        {
            "cash-only" => c.SuccessorSymbol is null && c.SharesPerShare is null && c.RightsPerShare is null,
            "cash-and-stock" => Symbol(c.SuccessorSymbol) && c.SuccessorSymbol != subject.Symbol && c.SharesPerShare is > 0 && c.RightsPerShare is null,
            "cash-and-cvr" => c.SuccessorSymbol is null && c.SharesPerShare is null && c.RightsPerShare is > 0,
            _ => false
        };
    }

    private static bool Symbol(string? symbol) => symbol is { Length: >= 1 and <= 20 } && char.IsAsciiLetterUpper(symbol[0]) &&
        symbol.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '.' or '-');

    private static (DateOnly From, DateOnly To) RequestWindow(string url, string symbol)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "api.tiingo.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != $"/tiingo/daily/{symbol}/prices") throw new ResearchException("terminal-history-request");
        var parts = uri.Query.TrimStart('?').Split('&');
        if (parts.Length != 2 || !parts[0].StartsWith("startDate=", StringComparison.Ordinal) || !parts[1].StartsWith("endDate=", StringComparison.Ordinal) ||
            !DateOnly.TryParseExact(parts[0][10..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
            !DateOnly.TryParseExact(parts[1][8..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) || to < from)
            throw new ResearchException("terminal-history-request");
        return (from, to);
    }
}
