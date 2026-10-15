using System.Collections.Immutable;

namespace Twenty.Core.Data;

public sealed class PointInTimeDataset
{
    private readonly DatasetFile file;
    public DatasetManifest Manifest => file.Manifest;
    public PointInTimeDataset(DatasetFile file)
    {
        var validation = DatasetValidator.Validate(file);
        if (!validation.IsValid) throw new InvalidDatasetException(validation);
        this.file = file;
    }
    public DataSlice AsOf(DateTimeOffset decisionAt)
    {
        if (decisionAt == default || decisionAt > file.Manifest.CreatedAt)
            throw new InvalidDatasetException(new([new("snapshot-cutoff", "selection", "The decision exceeds the archived snapshot creation time.")]));
        var records = file.Observations.Where(x => x.Evidence.AvailableAt <= decisionAt)
            .GroupBy(x => x.SeriesKey())
            .Select(g => g.OrderByDescending(x => x.Evidence.AvailableAt).First())
            .OrderBy(x => x.Id, StringComparer.Ordinal).ToImmutableArray();
        return new(decisionAt, records, file.Manifest.Calendars, file.Manifest.Universes);
    }
}

public sealed class DataSlice
{
    private readonly ImmutableArray<CalendarReference> calendars;
    private readonly ImmutableArray<UniverseDefinition> universes;
    public DateTimeOffset DecisionAt { get; }
    public ImmutableArray<Observation> Observations { get; }
    internal DataSlice(DateTimeOffset decisionAt, ImmutableArray<Observation> observations, ImmutableArray<CalendarReference> calendars, ImmutableArray<UniverseDefinition> universes)
    {
        this.calendars = calendars;
        this.universes = universes;
        DecisionAt = decisionAt;
        Observations = observations;
    }
    private static bool Contains(DateTimeOffset from, DateTimeOffset? until, DateTimeOffset time) =>
        from <= time && (until is null || time < until);
    private static void Fail(string code, string message) =>
        throw new InvalidDatasetException(new([new(code, "selection", message)]));

    public ImmutableArray<Listing> ListingsAt(DateTimeOffset effectiveAt)
    {
        if (effectiveAt > DecisionAt) Fail("future-effective-query", "The effective query time exceeds the decision time.");
        var rows = Observations.OfType<Listing>().Where(x => Contains(x.EffectiveFrom, x.EffectiveUntil, effectiveAt)).ToImmutableArray();
        if (rows.GroupBy(x => x.ListingId).Any(g => g.Count() != 1))
            Fail("ambiguous-listing", "Multiple known listing assertions overlap at the requested time.");
        return rows;
    }

    public ImmutableArray<Listing> Universe(string universeId)
    {
        if (!universes.Any(x => x.Id == universeId)) Fail("universe-undefined", "The requested universe is not declared in the snapshot.");
        var memberships = Observations.OfType<Membership>().Where(x => x.UniverseId == universeId &&
            Contains(x.EffectiveFrom, x.EffectiveUntil, DecisionAt)).ToArray();
        if (memberships.GroupBy(x => x.ListingId).Any(g => g.Count() != 1))
            Fail("ambiguous-membership", "Multiple known eligibility assertions overlap at the decision time.");
        var listings = ListingsAt(DecisionAt).ToDictionary(x => x.ListingId);
        var result = ImmutableArray.CreateBuilder<Listing>();
        foreach (var member in memberships.Where(x => x.Eligible))
        {
            if (!listings.TryGetValue(member.ListingId, out var listing))
                Fail("unresolved-membership", "An eligible membership has no effective known listing.");
            else if (listing.Status == ListingStatus.Active) result.Add(listing);
        }
        return result.OrderBy(x => x.ListingId, StringComparer.Ordinal).ToImmutableArray();
    }

    public Listing ResolveListing(string sourceNamespace, string value, string? venue = null)
    {
        var matches = ListingsAt(DecisionAt).Where(x => (venue is null || x.Venue == venue) &&
            x.ProviderIds.Any(id => id.Namespace == sourceNamespace && id.Value == value)).ToArray();
        if (matches.Length != 1)
            Fail("ambiguous-provider-mapping", "The known provider identifier does not resolve to one listing; supply verified identity/venue evidence.");
        return matches[0];
    }

    public ImmutableArray<PriceBar> Prices(string listingId, PriceBasis basis)
    {
        if (!Enum.IsDefined(basis)) Fail("price-basis", "An explicit supported adjustment basis is required.");
        var rows = Observations.OfType<PriceBar>().Where(x => x.ListingId == listingId && x.Basis == basis)
            .OrderBy(x => x.SessionCloseAt).ToImmutableArray();
        foreach (var row in rows)
        {
            var listing = ListingsAt(row.SessionCloseAt).SingleOrDefault(x => x.ListingId == listingId);
            if (listing is null || listing.Denomination != row.Denomination)
                Fail("price-listing-units", "A price's currency/unit does not match its known effective listing.");
            var calendar = calendars.Single(x => x.Id == listing!.CalendarId);
            var localClose = TimeZoneInfo.ConvertTime(row.SessionCloseAt, TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone));
            if (row.SessionDate != DateOnly.FromDateTime(localClose.DateTime))
                Fail("price-session-date", "Session date must match the close date in the listing's exchange timezone.");
        }
        return rows;
    }

    public ImmutableArray<FundamentalFact> Fundamentals(string companyId) =>
        Observations.OfType<FundamentalFact>().Where(x => x.CompanyId == companyId).ToImmutableArray();

    public ImmutableArray<CorporateAction> EffectiveActions(string listingId) =>
        Observations.OfType<CorporateAction>().Where(x => x.ListingId == listingId && x.EffectiveAt <= DecisionAt)
            .OrderBy(x => x.EffectiveAt).ToImmutableArray();

    public (decimal Amount, string Currency) TerminalDistribution(string actionId)
    {
        var rows = Observations.OfType<CorporateAction>().Where(x => x.ActionId == actionId && x.EffectiveAt <= DecisionAt).ToArray();
        if (rows.Length != 1 || rows[0].Action != CorporateActionKind.Delisting)
            Fail("terminal-unavailable", "A unique known effective delisting is required.");
        var action = rows[0];
        if (action.TerminalOutcome == TerminalOutcome.Cash)
            return (action.CashPerShare!.Value, action.CashCurrency!);
        if (action.TerminalOutcome == TerminalOutcome.Zero)
        {
            var listing = ListingsAt(action.EffectiveAt).SingleOrDefault(x => x.ListingId == action.ListingId);
            if (listing is not null) return (0, listing.Denomination.Currency);
        }
        Fail("terminal-missing", "A missing terminal outcome cannot become a zero loss, zero return or cash distribution.");
        return default; // Unreachable; keeps the throwing helper's control flow explicit.
    }

    public FxRate Fx(string baseCurrency, string quoteCurrency, FxPurpose purpose, TimeSpan maximumAge)
    {
        if (maximumAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        var row = Observations.OfType<FxRate>().Where(x => x.BaseCurrency == baseCurrency &&
            x.QuoteCurrency == quoteCurrency && x.Purpose == purpose && x.ObservedAt <= DecisionAt)
            .OrderByDescending(x => x.ObservedAt).FirstOrDefault();
        if (row is null || DecisionAt - row.ObservedAt > maximumAge)
            Fail("fx-unavailable", "No available rate matches the explicit direction, purpose and maximum age.");
        return row!;
    }
}
