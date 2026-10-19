using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;

namespace Twenty.Ingestion.Listings;

public sealed record ListingIdentityExclusion(string Symbol, string Reason);
public sealed record ListingIdentityResult(DatasetFile Dataset, ImmutableArray<ListingIdentityExclusion> Excluded,
    ImmutableArray<string> IncludedSymbols);

/// <summary>The identity-evidence contract that turns per-source observed listing intervals
/// into v1 Listing/Membership observations for a *declared symbol sample*. Identity is
/// asserted only on two-source agreement: a Nasdaq-listed directory presence and a Tiingo
/// segment covering it, agreeing on venue (NASDAQ→XNAS only in v1), asset class and USD
/// denomination. A delisted identity requires a Tiingo producer end *and* a later Nasdaq
/// observation without the symbol; producer-ended-but-still-listed, single-source present
/// claims, historical Tiingo segments our Nasdaq captures cannot corroborate, and every
/// conflict are excluded with a recorded reason, never guessed. Company/security identity
/// is 1:1 with the listing (no multi-class or successor resolution), and all evidence is
/// available only at capture time: producer history becomes *effective* history but can
/// never be presented as point-in-time knowledge from before collection began.</summary>
public static class ListingIdentity
{
    public const string Version = "listing-identity-v1";
    public const string UniverseId = "declared-sample";
    public const string EvidenceSourceId = "listing-intervals";
    private const int MaximumSymbols = 500;
    /// <summary>Tiingo's producer end date is its last-data date, not a delisting date:
    /// active tickers end at the capture frontier (genuinely 2026-10-16 for a 2026-10-18
    /// capture). A segment counts as ended only when its end precedes the Tiingo capture
    /// by more than this coverage-lag tolerance.</summary>
    public const int CoverageLagDays = 30;

    public static ListingIdentityResult Convert(ImmutableArray<ListingInterval> nasdaqListed,
        ImmutableArray<ListingInterval> tiingo, IReadOnlyCollection<string> symbols,
        DateTimeOffset nasdaqObservedAt, DateTimeOffset tiingoObservedAt, DateTimeOffset createdAt,
        string datasetId, ImmutableArray<SourceSnapshot> sources)
    {
        var observedAt = nasdaqObservedAt > tiingoObservedAt ? nasdaqObservedAt : tiingoObservedAt;
        var required = new[] { "nasdaq-listed", "tiingo-tickers", EvidenceSourceId };
        var byId = sources.ToDictionary(s => s.Id);
        if (symbols is null || symbols.Count is 0 or > MaximumSymbols || symbols.Any(string.IsNullOrWhiteSpace) ||
            symbols.Distinct(StringComparer.Ordinal).Count() != symbols.Count || createdAt < observedAt ||
            required.Any(id => !byId.ContainsKey(id)) || byId[EvidenceSourceId].FirstSeenAt is null ||
            byId[EvidenceSourceId].RetrievedAt < observedAt)
            throw new ResearchException("listing-identity-sources");
        var evidence = new Evidence(EvidenceSourceId, observedAt, observedAt,
            AvailabilityEvidence.ObservedPublication, TimePrecision.Second, null, null);
        var endedBefore = DateOnly.FromDateTime(tiingoObservedAt.UtcDateTime).AddDays(-CoverageLagDays);
        bool Ended(ListingInterval s) => s.ProducerEnd is { } e && e < endedBefore;
        var observations = ImmutableArray.CreateBuilder<Observation>();
        var excluded = ImmutableArray.CreateBuilder<ListingIdentityExclusion>();
        var included = ImmutableArray.CreateBuilder<string>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols.Order(StringComparer.Ordinal))
        {
            var nas = nasdaqListed.Where(i => i.Symbol == symbol).ToArray();
            var tii = tiingo.Where(i => i.Symbol == symbol).OrderBy(i => i.ProducerStart).ToArray();
            var present = nas.Where(i => i.Status == "present-at-final-snapshot").ToArray();
            if (present.Length > 1) { excluded.Add(new(symbol, "nasdaq-ambiguous-segments")); continue; }
            ListingInterval? segment = null;
            var delisted = false;
            if (present.Length == 1)
            {
                var nasDate = DateOnly.FromDateTime(present[0].LastSeen.UtcDateTime);
                var covering = tii.Where(s => s.ProducerStart is { } st && st <= nasDate && !Ended(s)).ToArray();
                if (covering.Length > 1) { excluded.Add(new(symbol, "tiingo-ambiguous-segments")); continue; }
                if (covering.Length == 0)
                {
                    excluded.Add(new(symbol,
                        tii.Any(Ended) ? "producer-ended-but-listed" :
                        tii.Any(s => s.ProducerStart is null) ? "no-producer-start" :
                        tii.Length == 0 ? "single-source-present" : "tiingo-not-covering"));
                    continue;
                }
                segment = covering[0];
                var conflict = Conflict(segment, present[0]);
                if (conflict is not null) { excluded.Add(new(symbol, conflict)); continue; }
                foreach (var other in tii.Where(s => s != segment && Ended(s) && s.ProducerEnd < segment.ProducerStart))
                    excluded.Add(new(symbol, "historical-segment-uncorroborated"));
            }
            else
            {
                if (tii.Length == 0) { excluded.Add(new(symbol, nas.Length == 0 ? "not-observed" : "single-source-present")); continue; }
                var open = tii.Where(s => !Ended(s)).ToArray();
                if (open.Length > 0)
                {
                    segment = open[^1];
                    excluded.Add(new(symbol,
                        segment.Exchange != "NASDAQ" ? "venue-conflict" :
                        MapAsset(segment.AssetType) is null ? "asset-class-unmapped" :
                        segment.PriceCurrency != "USD" ? "currency-unexpected" : "single-source-present"));
                    continue;
                }
                segment = tii.MaxBy(s => s.ProducerEnd)!;
                if (segment.ProducerStart is null) { excluded.Add(new(symbol, "no-producer-start")); continue; }
                var segConflict = segment.Exchange != "NASDAQ" ? "venue-conflict" :
                    MapAsset(segment.AssetType) is null ? "asset-class-unmapped" :
                    segment.PriceCurrency != "USD" ? "currency-unexpected" : null;
                if (segConflict is not null) { excluded.Add(new(symbol, segConflict)); continue; }
                if (DateOnly.FromDateTime(nasdaqObservedAt.UtcDateTime) <= segment.ProducerEnd)
                { excluded.Add(new(symbol, "ended-unconfirmed")); continue; }
                delisted = true;
                foreach (var other in tii.Where(s => s != segment))
                    excluded.Add(new(symbol, "historical-segment-uncorroborated"));
            }

            var start = segment.ProducerStart!.Value;
            var slug = Slug(symbol) + "-" + start.ToString("yyyyMMdd");
            var listingId = "listing-" + slug;
            if (!usedIds.Add(listingId)) { excluded.Add(new(symbol, "id-collision")); continue; }
            var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var until = delisted ? new DateTimeOffset(segment.ProducerEnd!.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : (DateTimeOffset?)null;
            var assetClass = MapAsset(segment.AssetType)!.Value;
            Listing Listing(string suffix, DateTimeOffset f, DateTimeOffset? u, ListingStatus status) => new(
                listingId + suffix, evidence, "security-" + slug, listingId, "company-" + slug, symbol, "XNAS",
                assetClass, [new("nasdaq.symbol", symbol), new("tiingo.ticker", symbol)],
                new("USD", QuoteUnit.Major), "ny-calendar", f, u, status);
            observations.Add(Listing("-active", from, until, ListingStatus.Active));
            if (delisted) observations.Add(Listing("-delisted", until!.Value, null, ListingStatus.Delisted));
            observations.Add(new Membership(listingId + "-member", evidence, UniverseId, listingId, from, until, true));
            if (delisted) observations.Add(new Membership(listingId + "-exit", evidence, UniverseId, listingId, until!.Value, null, false));
            included.Add(symbol);
        }
        if (observations.Count == 0) throw new ResearchException("listing-identity-empty");
        var manifest = new DatasetManifest(DatasetValidator.SchemaVersion, datasetId, createdAt, "features.v1",
            [new("identity-only", "v1", "Listing directories; no price features in this dataset", "Available at Twenty capture time only")],
            sources,
            [new("ny-calendar", "America/New_York", "unverified-v1",
                System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("ny-calendar|America/New_York|unverified-v1"))))],
            [new(UniverseId, UniverseKind.Research, "Declared operator sample under two-source identity contract " + Version +
                "; not an index, eligibility or identity authority")],
            Synthetic: false);
        var dataset = new DatasetFile(manifest, observations.ToImmutable());
        var validation = DatasetValidator.Validate(dataset);
        if (!validation.IsValid) throw new InvalidDatasetException(validation);
        return new(dataset, excluded.ToImmutable(), included.ToImmutable());
    }

    private static string? Conflict(ListingInterval tiingoSegment, ListingInterval nasdaq)
    {
        if (tiingoSegment.Exchange != "NASDAQ") return "venue-conflict";
        var asset = MapAsset(tiingoSegment.AssetType);
        if (asset is null || MapNasdaqAsset(nasdaq.AssetType) is null) return "asset-class-unmapped";
        if (asset != MapNasdaqAsset(nasdaq.AssetType)) return "asset-class-conflict";
        return tiingoSegment.PriceCurrency != "USD" ? "currency-unexpected" : null;
    }

    private static AssetClass? MapAsset(string producer) => producer switch
    {
        "Stock" => AssetClass.Stock,
        "ETF" => AssetClass.Etf,
        _ => null
    };
    private static AssetClass? MapNasdaqAsset(string assetType) => assetType switch
    {
        "stock" => AssetClass.Stock,
        "etf" => AssetClass.Etf,
        _ => null
    };

    private static string Slug(string symbol)
    {
        var slug = string.Concat(symbol.Trim().ToLowerInvariant()
            .Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' ? c : '-'))
            .Trim('.', '-', '_');
        return slug.Length > 0 && char.IsAsciiLetterOrDigit(slug[0]) ? slug : "s" + slug;
    }

    /// <summary>Builds an identity dataset from a capture store and returns the interval
    /// artifact identity the evidence is bound to. Source snapshots in the manifest carry
    /// the latest capture hash per directory source plus the interval artifact hash.</summary>
    public static (ListingIdentityResult Result, string IntervalId) Build(CaptureStore store,
        IReadOnlyCollection<string> symbols, DateTimeOffset createdAt, string datasetId)
    {
        var files = ListingIntervals.Build(store, ["nasdaq-listed", "tiingo-tickers"]);
        var nas = ListingIntervals.LoadSource(store, "nasdaq-listed");
        var tii = ListingIntervals.LoadSource(store, "tiingo-tickers");
        var observedLatest = nas.LatestObservedAt > tii.LatestObservedAt ? nas.LatestObservedAt : tii.LatestObservedAt;
        var observedEarliest = nas.EarliestObservedAt < tii.EarliestObservedAt ? nas.EarliestObservedAt : tii.EarliestObservedAt;
        SourceSnapshot Source(string id, ListingIntervals.ListingSourceData data, string location, string licence) => new(
            id, location, licence, data.LatestObservedAt, data.EarliestObservedAt, data.LatestSha256,
            ListingIntervals.Version, "Producer directory as captured; absence is not asserted as delisting");
        ImmutableArray<SourceSnapshot> sources =
        [
            Source("nasdaq-listed", nas, "https://www.nasdaqtrader.com/dynamic/symdir/nasdaqlisted.txt",
                "Nasdaq Trader symbol directory as captured; research use"),
            Source("tiingo-tickers", tii, "urn:twenty:capture:tiingo-tickers",
                "Tiingo supported-tickers directory as captured under the configured subscription; not redistributed"),
            new(EvidenceSourceId, "urn:twenty:listing-intervals:" + files.Id,
                "Derived by Twenty from the two directory captures above", observedLatest, observedEarliest,
                System.Convert.ToHexStringLower(SHA256.HashData(files.Csv)), Version,
                "Rebuild supersedes; prior datasets immutable")
        ];
        return (Convert(nas.Intervals, tii.Intervals, symbols, nas.LatestObservedAt, tii.LatestObservedAt,
            createdAt, datasetId, sources), files.Id);
    }
}
