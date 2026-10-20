using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Tiingo;
using Twenty.Ingestion.Capture;

namespace Twenty.Ingestion.Listings;

public sealed record ActionSource(string Id, string Url, DateTimeOffset RetrievedAt, string ContentSha256);
public sealed record ActionAssertion(string Kind, string DateBasis, DateOnly From, DateOnly To, decimal Value, string SourceId);
public sealed record ActionReferences(string SchemaVersion, string Symbol, DateOnly From, DateOnly To,
    ImmutableArray<ActionSource> Sources, ImmutableArray<ActionAssertion> Actions);
public sealed record ActionMatch(string Kind, DateOnly VendorDate, decimal Value, string DateBasis,
    DateOnly ReferenceFrom, DateOnly ReferenceTo, string SourceId);
public sealed record ActionIssue(string Code, string Kind, DateOnly From, DateOnly To, decimal Value);
public sealed record CorporateActionAuditReport(string AuditVersion, string Symbol, DateOnly From, DateOnly To,
    string HistorySha256, string HistoryCaptureId, DateTimeOffset HistoryCapturedAt, string ReferenceSha256, ImmutableArray<ActionSource> Sources,
    int ReferenceActions, int VendorActions, ImmutableArray<ActionMatch> Matches, ImmutableArray<ActionIssue> Issues,
    ImmutableArray<string> Limitations)
{
    public bool Valid => Issues.IsEmpty;
}

/// <summary>Offline comparison of declared issuer/exchange assertions with captured vendor
/// actions. Source hashes bind the reviewed documents; they do not authenticate assertions
/// or establish historical availability. No price series or action observation is changed.</summary>
public static class CorporateActionAudit
{
    public const string Version = "corporate-action-audit-v1";
    public static CorporateActionAuditReport Build(CaptureStore store, byte[] reference, Func<string, byte[]> sourceBytes)
    {
        var refs = ReadReferences(reference);
        var captures = TiingoHistoryIdentity.Observations(store, refs.Symbol);
        if (captures.Count == 0) throw new ResearchException("action-history-not-captured");
        var latestAt = captures.Max(c => c.ObservedAt);
        var latest = captures.Where(c => c.ObservedAt == latestAt).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        if (latest.Select(c => c.Sha256).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new ResearchException("action-history-capture-conflict");
        return Run(latest[0], store.Bytes(latest[0].Sha256), reference, sourceBytes);
    }

    public static CorporateActionAuditReport Run(CaptureObservation capture, byte[] history, byte[] reference, Func<string, byte[]> sourceBytes)
    {
        var refs = ReadReferences(reference);
        if (!TiingoHistoryIdentity.Matches(capture, refs.Symbol) || capture.Sha256 != Hash(history) || capture.Bytes != history.Length ||
            string.IsNullOrWhiteSpace(capture.Id) || capture.ObservedAt == default)
            throw new ResearchException("action-history-integrity");
        return Audit(capture, history, reference, refs, sourceBytes);
    }

    private static ActionReferences ReadReferences(byte[] reference)
    {
        if (reference.Length is 0 or > 1024 * 1024) throw new ResearchException("action-reference-size");
        var refs = JsonSerializer.Deserialize<ActionReferences>(reference, DatasetJson.Options)
            ?? throw new ResearchException("action-reference-shape");
        if (refs.SchemaVersion != "action-reference.v1" || string.IsNullOrWhiteSpace(refs.Symbol) ||
            refs.Symbol.Length > 20 || refs.Symbol.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c) && c is not ('.' or '-')) ||
            refs.From == default || refs.To < refs.From || refs.Sources.IsDefaultOrEmpty || refs.Sources.Length > 100 ||
            refs.Actions.IsDefaultOrEmpty || refs.Actions.Length > 1000 ||
            refs.Sources.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || s.RetrievedAt == default ||
                s.ContentSha256 is not { Length: 64 } || s.ContentSha256.Any(c => !char.IsAsciiHexDigitLower(c)) ||
                !Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) ||
            refs.Sources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != refs.Sources.Length)
            throw new ResearchException("action-reference-shape");
        return refs;
    }

    private static CorporateActionAuditReport Audit(CaptureObservation capture, byte[] history, byte[] reference,
        ActionReferences refs, Func<string, byte[]> sourceBytes)
    {
        var sourceIds = refs.Sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (refs.Actions.Any(a => a is null || a.Kind is not ("split" or "dividend") || a.Value <= 0 ||
            a.Kind == "split" && a.Value == 1 || a.From < refs.From || a.To > refs.To || a.To < a.From ||
            !sourceIds.Contains(a.SourceId) || a.DateBasis is not ("exact-ex-date" or "declaration-record-window") ||
            a.DateBasis == "exact-ex-date" && a.From != a.To ||
            a.DateBasis == "declaration-record-window" && (a.Kind != "dividend" || a.To.DayNumber - a.From.DayNumber > 45)))
            throw new ResearchException("action-reference-assertion");
        // Non-overlap makes matching one-to-one without a greedy amount-based choice.
        foreach (var group in refs.Actions.GroupBy(a => a.Kind))
        {
            var ordered = group.OrderBy(a => a.From).ToArray();
            if (ordered.Zip(ordered.Skip(1)).Any(p => p.First.To >= p.Second.From))
                throw new ResearchException("action-reference-overlap");
        }
        foreach (var source in refs.Sources)
        {
            var payload = sourceBytes(source.ContentSha256);
            if (payload.Length is 0 or > 8 * 1024 * 1024 || Hash(payload) != source.ContentSha256)
                throw new ResearchException("action-source-integrity");
        }
        var bars = TiingoClient.ParseHistory(history);
        if (bars.IsEmpty || bars[0].Date > refs.From || bars[^1].Date < refs.To)
            throw new ResearchException("action-history-coverage");
        var events = bars.Where(b => b.Date >= refs.From && b.Date <= refs.To)
            .SelectMany(b => new[] { (Kind: "split", b.Date, Value: b.SplitFactor), (Kind: "dividend", b.Date, Value: b.DivCash) }
                .Where(e => e.Kind == "split" ? e.Value != 1 : e.Value > 0)).ToArray();
        var matches = ImmutableArray.CreateBuilder<ActionMatch>();
        var issues = ImmutableArray.CreateBuilder<ActionIssue>();
        foreach (var assertion in refs.Actions.OrderBy(a => a.From).ThenBy(a => a.Kind, StringComparer.Ordinal))
        {
            var candidates = events.Where(e => e.Kind == assertion.Kind && e.Date >= assertion.From && e.Date <= assertion.To).ToArray();
            if (candidates.Length != 1 || candidates[0].Value != assertion.Value)
            {
                issues.Add(new(candidates.Length == 0 ? "reference-action-missing" : candidates.Length > 1
                    ? "reference-action-ambiguous" : "reference-action-value", assertion.Kind, assertion.From, assertion.To, assertion.Value));
                continue;
            }
            matches.Add(new(assertion.Kind, candidates[0].Date, assertion.Value, assertion.DateBasis,
                assertion.From, assertion.To, assertion.SourceId));
        }
        foreach (var entry in events)
            if (!refs.Actions.Any(a => a.Kind == entry.Kind && a.From <= entry.Date && entry.Date <= a.To))
                issues.Add(new("vendor-action-unreferenced", entry.Kind, entry.Date, entry.Date, entry.Value));
        return new(Version, refs.Symbol, refs.From, refs.To, Hash(history), capture.Id, capture.ObservedAt, Hash(reference), refs.Sources,
            refs.Actions.Length, events.Length, matches.ToImmutable(), issues.ToImmutable(),
            ["Declared sample only. Symbol association and first-observation time follow the recorded capture metadata, not a retroactive availability claim.",
             "Exact-ex-date assertions compare dates and amounts; declaration-record windows compare one action amount within issuer declaration/record bounds, not an independently verified ex-date.",
             "Hashed documents bind manually reviewed assertions, not source authenticity or historical data availability. The audit does not certify price adjustments, other symbols, terminal outcomes or investment returns."]);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
