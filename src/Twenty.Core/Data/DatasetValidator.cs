using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Twenty.Core.Data;

public static partial class DatasetValidator
{
    private static readonly HashSet<string> SupportedCurrencies = ["GBP", "USD", "EUR", "CAD", "AUD", "CHF", "JPY", "SEK", "NOK", "DKK", "HKD", "SGD", "NZD", "ZAR"];
    public const string SchemaVersion = "twenty.dataset.v1";
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}$")]
    private static partial Regex Identifier();
    [GeneratedRegex("^[a-f0-9]{64}$")]
    private static partial Regex Hash();
    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex Currency();

    public static DataValidation Validate(DatasetFile file)
    {
        var issues = ImmutableArray.CreateBuilder<DataIssue>();
        void Check(bool condition, string code, string path, string message)
        {
            if (!condition) issues.Add(new(code, path, message));
        }
        bool Id(string? value) => value is not null && Identifier().IsMatch(value);
        bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048;
        bool Digest(string? value) => value is not null && Hash().IsMatch(value);
        bool MoneyCode(string? value) => value is not null && Currency().IsMatch(value) && SupportedCurrencies.Contains(value);
        void Denom(Denomination d, string path) => Check(MoneyCode(d.Currency) && Enum.IsDefined(d.Unit) &&
            (d.Unit != QuoteUnit.Pence || d.Currency == "GBP"), "price-unit", path, "Use an explicit supported currency and quote unit; represent GBX as GBP/pence.");
        void Interval(DateTimeOffset from, DateTimeOffset? until, string path) =>
            Check(from != default && (until is null || until > from), "effective-interval", path, "Effective intervals must be non-empty and half-open.");

        if (file is null || file.Manifest is null)
            return new([new("manifest-missing", "manifest", "A manifest is required.")]);
        var m = file.Manifest;
        Check(m.SchemaVersion == SchemaVersion, "schema-version", "manifest.schemaVersion", "Unsupported dataset schema version.");
        Check(Id(m.DatasetId) && Id(m.FeatureSchemaVersion), "manifest-id", "manifest", "Dataset and feature schema versions must have explicit identifiers.");
        Check(!m.Sources.IsDefaultOrEmpty && !m.Calendars.IsDefaultOrEmpty && !m.Universes.IsDefaultOrEmpty && !m.Features.IsDefault,
            "manifest-collections", "manifest", "Sources, calendars and the declared feature list are required.");
        Check(!file.Observations.IsDefaultOrEmpty, "observations", "observations", "An explicit non-empty observation collection is required.");
        if (m.Sources.IsDefault || m.Calendars.IsDefault || m.Universes.IsDefault || m.Features.IsDefault || file.Observations.IsDefault)
            return new(issues.ToImmutable());

        if (m.Sources.Any(x => x is null) || m.Calendars.Any(x => x is null) || m.Features.Any(x => x is null) || m.Universes.Any(x => x is null) ||
            file.Observations.Any(x => x is null || x.Evidence is null || (x is Listing l && l.Denomination is null) || (x is PriceBar b && b.Denomination is null)))
            return new([new("null-record", "dataset", "Null collection entries, evidence or denominations are not supported.")]);
        Check(m.CreatedAt != default, "manifest-time", "manifest.createdAt", "Snapshot creation time is required.");
        foreach (var source in m.Sources)
        {
            var p = "manifest.sources";
            var locationValid = Uri.TryCreate(source.Location, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "urn" || (uri.Scheme == "https" && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == ""));
            Check(Id(source.Id) && locationValid && Text(source.LicenceReference) && Digest(source.ContentSha256) &&
                Id(source.ParserVersion) && Text(source.RevisionPolicy), "source-provenance", p,
                "Source identity, credential-free location, licence, hash, parser and revision policy are required.");
            Check(source.RetrievedAt != default && source.RetrievedAt <= m.CreatedAt && (source.FirstSeenAt is null || source.FirstSeenAt <= source.RetrievedAt),
                "source-time", p, "First-seen/retrieval timestamps must be consistent with snapshot creation.");
        }
        foreach (var calendar in m.Calendars)
        {
            Check(Id(calendar.Id) && Id(calendar.Version) && Digest(calendar.ContentSha256), "calendar-reference", "manifest.calendars", "Calendar identity, version and hash are required.");
            try { TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone); }
            catch (TimeZoneNotFoundException) { Check(false, "calendar-timezone", "manifest.calendars", "Calendar timezone is not recognised."); }
            catch (InvalidTimeZoneException) { Check(false, "calendar-timezone", "manifest.calendars", "Calendar timezone is invalid."); }
            catch (ArgumentException) { Check(false, "calendar-timezone", "manifest.calendars", "Calendar timezone is required."); }
        }
        foreach (var universe in m.Universes)
            Check(Id(universe.Id) && Enum.IsDefined(universe.Kind) && Text(universe.AuthorityReference), "universe-definition", "manifest.universes", "Universes require an explicit scope and eligibility authority reference.");
        foreach (var feature in m.Features)
            Check(Id(feature.Name) && Id(feature.Version) && Text(feature.Input) && Text(feature.AvailabilityRule),
                "feature-definition", "manifest.features", "Feature definitions need versioned inputs and availability rules.");
        Check(m.Sources.Select(x => x.Id).Distinct().Count() == m.Sources.Length &&
            m.Calendars.Select(x => x.Id).Distinct().Count() == m.Calendars.Length &&
            m.Features.Select(x => x.Name).Distinct().Count() == m.Features.Length &&
            m.Universes.Select(x => x.Id).Distinct().Count() == m.Universes.Length,
            "manifest-duplicate", "manifest", "Manifest identities must be unique.");
        var sources = m.Sources.Select(x => x.Id).ToHashSet();
        var universes = m.Universes.Select(x => x.Id).ToHashSet();
        var calendars = m.Calendars.Select(x => x.Id).ToHashSet();
        var listingIds = file.Observations.OfType<Listing>().Select(x => x.ListingId).ToHashSet();
        var companyIds = file.Observations.OfType<Listing>().Select(x => x.CompanyId).ToHashSet();
        Check(file.Observations.Select(x => x.Id).Distinct().Count() == file.Observations.Length,
            "duplicate-record", "observations", "Observation IDs must be globally unique.");

        for (var i = 0; i < file.Observations.Length; i++)
        {
            var r = file.Observations[i];
            var p = $"observations[{i}]";
            var e = r.Evidence;
            Check(Id(r.Id) && sources.Contains(e.SourceId), "record-provenance", p, "Record identity and a known source are required.");
            Check(e.AvailableAt is not null && e.AvailableAt != default(DateTimeOffset) && e.PublishedAt is not null && e.PublishedAt != default(DateTimeOffset) &&
                Enum.IsDefined(e.AvailabilityEvidence) && e.AvailabilityEvidence != AvailabilityEvidence.Unknown && Enum.IsDefined(e.Precision),
                "availability-missing", p, "Publication, availability, precision and availability evidence are mandatory.");
            Check(e.PublishedAt <= e.AvailableAt, "availability-order", p, "Information cannot be available before publication.");
            Check((e.AvailabilityEvidence != AvailabilityEvidence.ConservativePolicy && e.Precision != TimePrecision.Day) || Text(e.PolicyReference),
                "availability-policy", p, "Conservative or date-precision availability requires a documented policy.");
            var source = m.Sources.FirstOrDefault(s => s.Id == e.SourceId);
            if (source is not null)
                Check(e.PublishedAt <= source.RetrievedAt && (e.AvailabilityEvidence == AvailabilityEvidence.ConservativePolicy || e.AvailableAt <= source.RetrievedAt) &&
                    (e.AvailabilityEvidence != AvailabilityEvidence.ObservedPublication ||
                        (source.FirstSeenAt is not null && source.FirstSeenAt != default(DateTimeOffset) && source.FirstSeenAt <= e.AvailableAt)),
                    "availability-evidence", p, "Publication must precede retrieval; observed availability cannot precede nondefault first-seen evidence.");
            switch (r)
            {
                case Listing x:
                    Check(Id(x.SecurityId) && Id(x.ListingId) && Id(x.CompanyId) && Text(x.Symbol) && Id(x.Venue) && calendars.Contains(x.CalendarId) && Enum.IsDefined(x.Status) && Enum.IsDefined(x.AssetClass),
                        "listing-identity", p, "Listing identity, symbol, venue, calendar and status must be explicit.");
                    Check(!x.ProviderIds.IsDefaultOrEmpty && x.ProviderIds.All(v => v is not null && Id(v.Namespace) && Text(v.Value)) &&
                        x.ProviderIds.Select(v => v?.Namespace).Distinct().Count() == x.ProviderIds.Length,
                        "provider-mapping", p, "Listings need unambiguous source-namespaced identifiers.");
                    Denom(x.Denomination, p); Interval(x.EffectiveFrom, x.EffectiveUntil, p); break;
                case Membership x:
                    Check(universes.Contains(x.UniverseId) && listingIds.Contains(x.ListingId), "membership-identity", p, "Membership needs a universe and known listing.");
                    Interval(x.EffectiveFrom, x.EffectiveUntil, p); break;
                case PriceBar x:
                    Check(listingIds.Contains(x.ListingId) && Enum.IsDefined(x.Basis), "price-identity", p, "Prices need a known listing and adjustment basis.");
                    Denom(x.Denomination, p);
                    Check(x.SessionCloseAt != default && x.SessionCloseAt <= e.PublishedAt,
                        "price-time", p, "A completed bar must not be published before session close.");
                    Check(x.Low > 0 && x.Low <= x.Open && x.Low <= x.Close && x.High >= x.Open && x.High >= x.Close && x.High >= x.Low && x.Volume >= 0,
                        "price-range", p, "OHLC and volume are inconsistent."); break;
                case FundamentalFact x:
                    Check(companyIds.Contains(x.CompanyId) && Id(x.Accession) && Id(x.Concept) && Text(x.Unit) &&
                        (x.PeriodStart is null || x.PeriodStart <= x.PeriodEnd) &&
                        (e.PublishedAt is null || x.PeriodEnd <= DateOnly.FromDateTime(e.PublishedAt.Value.UtcDateTime)),
                        "fundamental-context", p, "An observed fundamental needs a company, accession, concept, unit and completed period."); break;
                case Filing x:
                    Check(companyIds.Contains(x.CompanyId) && Id(x.Accession) && Text(x.Form) && Digest(x.DocumentSha256) && x.AcceptedAt != default && x.AcceptedAt <= e.PublishedAt &&
                        (x.AmendsAccession is null || (Id(x.AmendsAccession) && x.AmendsAccession != x.Accession)),
                        "filing-context", p, "Filing identity, original hash, acceptance/availability and amendment reference must be coherent."); break;
                case FxRate x:
                    Check(MoneyCode(x.BaseCurrency) && MoneyCode(x.QuoteCurrency) && x.BaseCurrency != x.QuoteCurrency &&
                        x.QuoteUnitsPerBase > 0 && Enum.IsDefined(x.Purpose) && x.ObservedAt != default && x.ObservedAt <= e.PublishedAt,
                        "fx-context", p, "FX requires explicit distinct currencies, positive rate, purpose and observation time."); break;
                case CorporateAction x:
                    Check(x.EffectiveAt != default && listingIds.Contains(x.ListingId) && Id(x.ActionId) && Enum.IsDefined(x.Action) && Enum.IsDefined(x.TerminalOutcome),
                        "action-identity", p, "Corporate action needs an identity, known listing and supported kind/outcome.");
                    Check(x.CashPerShare is null || (x.CashPerShare >= 0 && MoneyCode(x.CashCurrency)), "action-cash", p, "Cash distributions need an explicit currency and non-negative amount.");
                    Check(x.Action != CorporateActionKind.Split || x.SplitRatio > 0, "split-ratio", p, "Splits need a positive new-shares/old-shares ratio.");
                    Check(x.Action != CorporateActionKind.Dividend || x.CashPerShare is not null, "dividend-cash", p, "Dividends need an explicit cash amount.");
                    Check(x.Action != CorporateActionKind.SymbolChange || Text(x.NewSymbol), "symbol-change", p, "Symbol changes need the new symbol.");
                    Check(x.SuccessorListingId is null || listingIds.Contains(x.SuccessorListingId), "action-successor", p, "A successor must identify a known listing.");
                    Check(x.Action != CorporateActionKind.Delisting || x.TerminalOutcome != TerminalOutcome.NotApplicable,
                        "terminal-outcome", p, "Delistings must distinguish known cash, zero, stock and missing outcomes.");
                    Check(x.TerminalOutcome != TerminalOutcome.Cash || x.CashPerShare is not null, "terminal-cash", p, "A known cash outcome needs its distribution.");
                    Check(x.TerminalOutcome != TerminalOutcome.Zero || x.CashPerShare is null or 0, "terminal-zero", p, "A zero outcome cannot carry positive cash.");
                    Check(x.TerminalOutcome != TerminalOutcome.Stock || (x.SuccessorListingId is not null && x.SplitRatio > 0),
                        "terminal-stock", p, "A stock outcome needs a successor listing and a positive exchange ratio."); break;
            }
        }
        foreach (var listing in file.Observations.OfType<Listing>().GroupBy(x => x.ListingId))
            Check(listing.Select(x => (x.SecurityId, x.CompanyId, x.Venue, x.AssetClass)).Distinct().Count() == 1,
                "listing-identity-change", "observations", "A listing ID cannot be reassigned to a different security, issuer, venue or asset class.");
        // Each logical observation has one complete, linear as-reported revision chain.
        foreach (var group in file.Observations.GroupBy(x => x.SeriesKey()))
        {
            Observation? prior = null;
            foreach (var r in group.OrderBy(x => x.Evidence.AvailableAt).ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                Check(prior is null ? r.Evidence.RevisionOf is null : r.Evidence.RevisionOf == prior.Id && r.Evidence.AvailableAt > prior.Evidence.AvailableAt,
                    "revision-chain", "observations", "Revisions must retain the original, advance availability and link the preceding version of the same observation.");
                prior = r;
            }
        }
        return new(issues.ToImmutable());
    }
}
