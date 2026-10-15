using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Twenty.Core.Data;

public enum AvailabilityEvidence { Unknown, ArchivedPublication, ObservedPublication, ConservativePolicy }
public enum TimePrecision { Second, Day }
public enum QuoteUnit { Major, Pence }
public enum UniverseKind { Research, StocksIsa }
public enum AssetClass { Stock, Etf }
public enum ListingStatus { Active, Suspended, Delisted }
public enum PriceBasis { Raw, SplitAdjusted, TotalReturnAdjusted }
public enum CorporateActionKind { Split, Dividend, SymbolChange, Delisting, Merger, SpinOff }
public enum TerminalOutcome { NotApplicable, Cash, Zero, Missing }
public enum FxPurpose { Reference, Indicative, Executable }

public sealed record SourceSnapshot(string Id, string Location, string LicenceReference,
    DateTimeOffset RetrievedAt, DateTimeOffset? FirstSeenAt, string ContentSha256,
    string ParserVersion, string RevisionPolicy);
public sealed record CalendarReference(string Id, string TimeZone, string Version, string ContentSha256);
public sealed record UniverseDefinition(string Id, UniverseKind Kind, string AuthorityReference);
public sealed record ProviderIdentifier(string Namespace, string Value);
public sealed record FeatureDefinition(string Name, string Version, string Input, string AvailabilityRule);
public sealed record DatasetManifest(string SchemaVersion, string DatasetId, DateTimeOffset CreatedAt,
    string FeatureSchemaVersion, ImmutableArray<FeatureDefinition> Features,
    ImmutableArray<SourceSnapshot> Sources, ImmutableArray<CalendarReference> Calendars,
    ImmutableArray<UniverseDefinition> Universes, bool Synthetic);
public sealed record Evidence(string SourceId, DateTimeOffset? PublishedAt, DateTimeOffset? AvailableAt,
    AvailabilityEvidence AvailabilityEvidence, TimePrecision Precision, string? PolicyReference, string? RevisionOf);
public sealed record Denomination(string Currency, QuoteUnit Unit)
{
    public decimal ToMajor(decimal value) => Unit switch
    {
        QuoteUnit.Major => value,
        QuoteUnit.Pence when Currency == "GBP" => value / 100m,
        _ => throw new InvalidOperationException("Unsupported price denomination.")
    };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Listing), "listing")]
[JsonDerivedType(typeof(Membership), "membership")]
[JsonDerivedType(typeof(PriceBar), "priceBar")]
[JsonDerivedType(typeof(CorporateAction), "corporateAction")]
[JsonDerivedType(typeof(FundamentalFact), "fundamentalFact")]
[JsonDerivedType(typeof(Filing), "filing")]
[JsonDerivedType(typeof(FxRate), "fxRate")]
public abstract record Observation(string Id, Evidence Evidence)
{
    // Keys are typed tuples: delimiters inside source identifiers cannot cause collisions.
    internal abstract object SeriesKey();
}

public sealed record Listing(string Id, Evidence Evidence, string SecurityId, string ListingId,
    string CompanyId, string Symbol, string Venue, AssetClass AssetClass, ImmutableArray<ProviderIdentifier> ProviderIds,
    Denomination Denomination, string CalendarId,
    DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveUntil, ListingStatus Status)
    : Observation(Id, Evidence)
{
    internal override object SeriesKey() => (typeof(Listing), ListingId, EffectiveFrom);
}
public sealed record Membership(string Id, Evidence Evidence, string UniverseId, string ListingId,
    DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveUntil, bool Eligible)
    : Observation(Id, Evidence)
{
    internal override object SeriesKey() => (typeof(Membership), UniverseId, ListingId, EffectiveFrom);
}
public sealed record PriceBar(string Id, Evidence Evidence, string ListingId, DateOnly SessionDate,
    DateTimeOffset SessionCloseAt, Denomination Denomination, PriceBasis Basis,
    decimal Open, decimal High, decimal Low, decimal Close, decimal Volume)
    : Observation(Id, Evidence)
{
    internal override object SeriesKey() => (typeof(PriceBar), ListingId, SessionDate, Basis);
}
public sealed record CorporateAction(string Id, Evidence Evidence, string ListingId, string ActionId,
    CorporateActionKind Action, DateTimeOffset EffectiveAt, decimal? SplitRatio,
    decimal? CashPerShare, string? CashCurrency, string? NewSymbol, string? SuccessorListingId,
    TerminalOutcome TerminalOutcome)
    : Observation(Id, Evidence)
{
    internal override object SeriesKey() => (typeof(CorporateAction), ListingId, ActionId);
}
public sealed record FundamentalFact(string Id, Evidence Evidence, string CompanyId, string Accession,
    string Concept, string Unit, DateOnly? PeriodStart, DateOnly PeriodEnd, decimal Value)
    : Observation(Id, Evidence)
{
    internal override object SeriesKey() => (typeof(FundamentalFact), CompanyId, Concept, Unit, PeriodStart, PeriodEnd);
}
public sealed record Filing(string Id, Evidence Evidence, string CompanyId, string Accession, string Form,
    DateOnly PeriodEnd, DateTimeOffset AcceptedAt, string DocumentSha256, string? AmendsAccession)
    : Observation(Id, Evidence)
{
    internal override object SeriesKey() => (typeof(Filing), CompanyId, Accession);
}
public sealed record FxRate(string Id, Evidence Evidence, string BaseCurrency, string QuoteCurrency,
    decimal QuoteUnitsPerBase, DateTimeOffset ObservedAt, FxPurpose Purpose)
    : Observation(Id, Evidence)
{
    internal override object SeriesKey() => (typeof(FxRate), BaseCurrency, QuoteCurrency, ObservedAt, Purpose);
    public decimal Convert(decimal amount, string currency) => currency == BaseCurrency
        ? amount * QuoteUnitsPerBase
        : throw new InvalidOperationException("FX input currency does not match the explicit base currency.");
}
public sealed record DatasetFile(DatasetManifest Manifest, ImmutableArray<Observation> Observations);
public sealed record DataIssue(string Code, string Path, string Message);
public sealed record DataValidation(ImmutableArray<DataIssue> Issues)
{
    public bool IsValid => Issues.IsEmpty;
}
public sealed class InvalidDatasetException(DataValidation validation)
    : Exception("Dataset failed validation; inspect the structured issues.")
{
    public DataValidation Validation { get; } = validation;
}
