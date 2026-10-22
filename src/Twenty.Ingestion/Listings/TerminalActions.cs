using System.Collections.Immutable;
using System.Globalization;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;

namespace Twenty.Ingestion.Listings;

public sealed record TerminalActionOmission(string Symbol, string Reason);
public sealed record TerminalActionResult(DatasetFile Dataset, int ActionsAdded, ImmutableArray<TerminalActionOmission> Omitted);

/// <summary>Mints the reviewed terminal action a changing-universe panel settles from. One accepted
/// disposition with a supported consideration becomes one CorporateActionKind.Delisting carrying that
/// consideration and TerminalOutcome.Cash. Nothing else is asserted: a refused subject gets no action
/// at all, so its exit stays unexplained and the consuming panel fails closed instead of being handed
/// a terminal outcome nobody established. Availability is the latest retrieval of the cited source
/// documents: their publication vintage is not in the reviewed bundle and an earlier availability is
/// never assumed.</summary>
public static class TerminalActions
{
    public const string Version = "terminal-actions-v1";
    public const string SourceId = "terminal-actions";
    public const string SourceLocation = "urn:twenty:terminal-actions";
    public const string AvailabilityPolicy =
        "The reviewed terminal sources record no publication vintage. A terminal action is published and "
        + "available at the latest retrieval instant of the sources cited for it; an earlier publication is never assumed.";

    public static TerminalActionResult Convert(DatasetFile dataset, byte[] reference, TerminalDispositionReport dispositions,
        string datasetId, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(dispositions);
        if (string.IsNullOrWhiteSpace(datasetId)) throw new ResearchException("terminal-actions-dataset-id");
        if (dataset.Manifest.Sources.Any(s => s.Id == SourceId)) throw new ResearchException("terminal-actions-source-conflict");
        // A dataset that already settles a delisting is being converted twice; never merge two
        // terminal claims about one listing into an ambiguous pair.
        if (dataset.Observations.OfType<CorporateAction>().Any(a => a.Action == CorporateActionKind.Delisting))
            throw new ResearchException("terminal-actions-duplicate");
        var input = DatasetValidator.Validate(dataset);
        if (!input.IsValid) throw new InvalidDatasetException(input);
        if (dispositions.Version != TerminalDispositions.Version || dispositions.Subjects.IsDefault ||
            dispositions.Subjects.Length > 50 || !Digest(dispositions.ReferenceSha256) ||
            dispositions.Accepted != dispositions.Subjects.Count(s => s.Outcome == "accepted") ||
            dispositions.Refused != dispositions.Subjects.Count(s => s.Outcome == "refused") ||
            dispositions.Subjects.Any(s => s is null || string.IsNullOrWhiteSpace(s.Symbol) ||
                s.Outcome is not ("accepted" or "refused") || s.RefusedRows.IsDefault || s.LastTradingSourceIds.IsDefault))
            throw new ResearchException("terminal-actions-shape");
        // The dispositions were reached from one reviewed bundle; a different one is a different run.
        if (CaptureStore.Hash(reference) != dispositions.ReferenceSha256)
            throw new ResearchException("terminal-actions-reference-conflict");
        var refs = TerminalHistoryAudit.ReadReferences(reference);
        var reviewed = refs.Subjects.ToDictionary(s => s.Symbol, StringComparer.Ordinal);
        var listings = dataset.Observations.OfType<Listing>().GroupBy(l => l.ListingId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.Ordinal);
        var calendars = dataset.Manifest.Calendars.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var used = ImmutableArray.CreateBuilder<CorporateAction>();
        var omitted = ImmutableArray.CreateBuilder<TerminalActionOmission>();
        var latestRetrieval = DateTimeOffset.MinValue;
        foreach (var subject in dispositions.Subjects.OrderBy(s => s.Symbol, StringComparer.Ordinal))
        {
            var symbol = subject.Symbol;
            // A refused subject yields no assertion whatsoever: minting an unexplained or zero
            // outcome here would turn an evidence gap into a claim about what shareholders received.
            if (subject.Outcome != "accepted")
            { omitted.Add(new(symbol, "disposition-refused")); continue; }
            if (subject.FinalSession is not { } final)
            { omitted.Add(new(symbol, "final-session-missing")); continue; }
            if (!reviewed.TryGetValue(symbol, out var claim))
            { omitted.Add(new(symbol, "reference-missing")); continue; }
            if (claim.EventKind != "completed-merger" || claim.LastTradingDate != final)
            { omitted.Add(new(symbol, "event-not-terminal")); continue; }
            var consideration = claim.Consideration;
            // Only a cash-only common-share entitlement maps onto a modelled terminal outcome.
            // Cash-plus-stock, contingent value rights and anything else need a settlement path
            // this contract does not have, so they stay refused rather than approximated.
            if (consideration is null || consideration.Kind != "cash-only" || consideration.CashPerShare is not > 0 ||
                consideration.Currency is not { Length: 3 } || consideration.SharesPerShare is not null ||
                consideration.RightsPerShare is not null)
            { omitted.Add(new(symbol, "consideration-not-supported")); continue; }
            var cited = claim.SourceIds.Concat(subject.LastTradingSourceIds).Distinct(StringComparer.Ordinal).ToArray();
            var citedSources = cited.Select(id => refs.Sources.FirstOrDefault(s => s.Id == id)).ToArray();
            if (citedSources.Any(s => s is null))
            { omitted.Add(new(symbol, "source-missing")); continue; }
            var available = citedSources.Max(s => s!.RetrievedAt);
            var match = listings.Values.Where(rows => Names(rows).Contains(symbol, StringComparer.OrdinalIgnoreCase))
                .Select(rows => rows[0]).ToArray();
            if (match.Length == 0)
            { omitted.Add(new(symbol, "listing-missing")); continue; }
            if (match.Length > 1)
            { omitted.Add(new(symbol, "listing-ambiguous")); continue; }
            var listing = match[0];
            if (listing.Venue != NasdaqSessions.Venue || !calendars.TryGetValue(listing.CalendarId, out var calendar) ||
                calendar.TimeZone != NasdaqSessions.TimeZone)
            { omitted.Add(new(symbol, "calendar-unsupported")); continue; }
            // The final session has to be a reviewed trading session: a stated last trading date on
            // a weekend or an unsupported year contradicts the calendar and cannot be timestamped.
            if (NasdaqSessions.CloseAt(final) is not { } closeAt)
            { omitted.Add(new(symbol, "final-session-not-in-calendar")); continue; }
            if (available > createdAt) throw new ResearchException("terminal-actions-created-at");
            var day = final.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            used.Add(new($"{listing.ListingId}-delisting-{day}",
                new Evidence(SourceId, available, available, AvailabilityEvidence.ConservativePolicy,
                    TimePrecision.Second, AvailabilityPolicy, null),
                listing.ListingId, $"{listing.ListingId}-delisting", CorporateActionKind.Delisting, closeAt,
                null, consideration.CashPerShare, consideration.Currency, null, null, TerminalOutcome.Cash));
            if (available > latestRetrieval) latestRetrieval = available;
        }
        if (used.Count == 0 && omitted.Count == 0) throw new ResearchException("terminal-actions-empty");
        var manifest = dataset.Manifest;
        var sources = manifest.Sources.Add(new SourceSnapshot(SourceId, SourceLocation,
            "Reviewed terminal outcomes from private issuer, exchange and SEC documents; not redistributed",
            latestRetrieval == DateTimeOffset.MinValue ? createdAt : latestRetrieval, null,
            TerminalDispositions.ContentHash(dispositions), Version, "Rebuild supersedes; prior datasets immutable"));
        var output = new DatasetFile(manifest with { DatasetId = datasetId, CreatedAt = createdAt, Sources = sources },
            dataset.Observations.AddRange(used.ToImmutable().Cast<Observation>()));
        var validation = DatasetValidator.Validate(output);
        if (!validation.IsValid) throw new InvalidDatasetException(validation);
        return new(output, used.Count, omitted.ToImmutable());
    }

    // A listing is reached by its symbol or its producer ticker, as the price converter matches them.
    private static string[] Names(ImmutableArray<Listing> rows) =>
        [.. rows.Select(r => r.Symbol).Concat(rows.SelectMany(r => r.ProviderIds)
            .Where(p => p is not null && p.Namespace == "tiingo.ticker").Select(p => p.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
}
