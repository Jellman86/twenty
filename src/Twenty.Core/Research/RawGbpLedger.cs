using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawQuantityPosition(decimal Quantity, Denomination Denomination, DateTimeOffset OpenedAt);
public sealed record RawLedgerEntry(string Id, DateTimeOffset At, string Kind, string? ListingId, decimal Quantity,
    decimal? RawPrice, Denomination? Denomination, decimal FeeGbp, decimal CashChangeGbp, string? FxId,
    decimal GbpPerLocalUnit, decimal CashAfterGbp);
public sealed record RawPositionValue(string ListingId, decimal Quantity, string PriceId, string? FxId,
    decimal RawPrice, Denomination Denomination, decimal GbpPerLocalUnit, decimal ValueGbp);
public sealed record RawGbpValuation(DateTimeOffset At, FxPurpose FxPurpose, decimal CashGbp, decimal EquityGbp,
    ImmutableArray<RawPositionValue> Positions);

/// <summary>Offline, single-owner decimal reference for explicit raw fills and GBP cash.
/// Supplied fill prices are execution assumptions, never inferred from an adjusted bar.
/// This is not an order adapter, a historical execution simulator or a return-index portfolio.
/// Corporate-action intervals are refused until a separate settlement contract exists.</summary>
public sealed class RawGbpLedger
{
    private ImmutableDictionary<string, RawQuantityPosition> holdings = ImmutableDictionary.Create<string, RawQuantityPosition>(StringComparer.Ordinal);
    private ImmutableHashSet<string> ids = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    private DateTimeOffset? lastAt;
    public decimal CashGbp { get; private set; }
    public decimal ContributionsGbp { get; private set; }
    public decimal FeesGbp { get; private set; }
    public ImmutableDictionary<string, RawQuantityPosition> Holdings => holdings;
    public ImmutableArray<RawLedgerEntry> Entries { get; private set; } = [];

    private void CheckTime(DateTimeOffset at)
    {
        if (at == default || lastAt is { } last && at < last) throw new ResearchException("raw-ledger-time");
    }
    private void CheckEvent(string id, DateTimeOffset at)
    {
        CheckTime(at);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || ids.Contains(id) || Entries.Length >= 10000)
            throw new ResearchException("raw-ledger-event");
    }
    private static void CheckAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) throw new ResearchException("raw-ledger-age");
    }
    private static Listing ListingAt(DataSlice market, string id) => market.ListingsAt(market.DecisionAt).SingleOrDefault(l => l.ListingId == id)
        ?? throw new ResearchException("raw-ledger-listing");
    private void CheckHoldings(DataSlice market)
    {
        foreach (var (id, position) in holdings)
        {
            if (market.EffectiveActions(id).Any(a => a.EffectiveAt > position.OpenedAt && a.Action switch
                {
                    CorporateActionKind.SymbolChange => false,
                    CorporateActionKind.Split => a.SplitRatio != 1,
                    CorporateActionKind.Dividend => a.CashPerShare != 0,
                    _ => true
                })) throw new ResearchException("raw-ledger-action-unsupported");
            if (ListingAt(market, id).Denomination != position.Denomination) throw new ResearchException("raw-ledger-units");
        }
    }
    private static (decimal Rate, string? Id) Conversion(DataSlice market, string currency, FxPurpose purpose, TimeSpan maximumAge)
    {
        CheckAge(maximumAge);
        if (!Enum.IsDefined(purpose)) throw new ResearchException("raw-ledger-fx-purpose");
        if (currency == "GBP") return (1, null);
        var fx = market.Fx(currency, "GBP", purpose, maximumAge);
        return (fx.QuoteUnitsPerBase, fx.Id);
    }
    private void Record(RawLedgerEntry entry)
    {
        Entries = Entries.Add(entry); ids = ids.Add(entry.Id); lastAt = entry.At;
    }
    public void Deposit(string id, DateTimeOffset at, decimal amountGbp)
    {
        CheckEvent(id, at);
        if (amountGbp <= 0) throw new ResearchException("raw-ledger-deposit");
        var cash = CashGbp + amountGbp; var contributions = ContributionsGbp + amountGbp;
        Record(new(id, at.ToUniversalTime(), "deposit", null, 0, null, null, 0, amountGbp, null, 1, cash));
        CashGbp = cash; ContributionsGbp = contributions;
    }
    public void Fill(string id, DataSlice market, string listingId, decimal signedQuantity, decimal rawPrice,
        PriceBasis basis, decimal feeGbp, TimeSpan maximumFxAge)
    {
        ArgumentNullException.ThrowIfNull(market);
        CheckEvent(id, market.DecisionAt); CheckAge(maximumFxAge);
        if (basis != PriceBasis.Raw) throw new ResearchException("raw-ledger-basis");
        if (signedQuantity == 0 || rawPrice <= 0 || feeGbp < 0) throw new ResearchException("raw-ledger-fill");
        CheckHoldings(market);
        var listing = ListingAt(market, listingId);
        if (listing.Status != ListingStatus.Active) throw new ResearchException("raw-ledger-listing");
        var held = holdings.GetValueOrDefault(listingId);
        var quantity = (held?.Quantity ?? 0) + signedQuantity;
        if (quantity < 0) throw new ResearchException("raw-ledger-quantity");
        var (rate, fxId) = Conversion(market, listing.Denomination.Currency, FxPurpose.Executable, maximumFxAge);
        var change = -signedQuantity * listing.Denomination.ToMajor(rawPrice) * rate - feeGbp;
        var cash = CashGbp + change; var fees = FeesGbp + feeGbp;
        if (cash < 0) throw new ResearchException("raw-ledger-cash");
        var next = quantity == 0 ? holdings.Remove(listingId) : holdings.SetItem(listingId,
            new(quantity, listing.Denomination, held?.OpenedAt ?? market.DecisionAt.ToUniversalTime()));
        Record(new(id, market.DecisionAt.ToUniversalTime(), signedQuantity > 0 ? "buy" : "sell", listingId,
            signedQuantity, rawPrice, listing.Denomination, feeGbp, change, fxId, rate, cash));
        holdings = next; CashGbp = cash; FeesGbp = fees;
    }
    public RawGbpValuation Value(DataSlice market, TimeSpan maximumPriceAge, TimeSpan maximumFxAge, FxPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(market);
        CheckTime(market.DecisionAt); CheckAge(maximumPriceAge); CheckAge(maximumFxAge);
        if (!Enum.IsDefined(purpose)) throw new ResearchException("raw-ledger-fx-purpose");
        CheckHoldings(market);
        var values = ImmutableArray.CreateBuilder<RawPositionValue>();
        foreach (var (id, position) in holdings.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var price = market.Prices(id, PriceBasis.Raw).Where(p => p.SessionCloseAt <= market.DecisionAt)
                .OrderByDescending(p => p.SessionCloseAt).FirstOrDefault();
            if (price is null || market.DecisionAt - price.SessionCloseAt > maximumPriceAge)
                throw new ResearchException("raw-ledger-price");
            if (price.Denomination != position.Denomination) throw new ResearchException("raw-ledger-units");
            var (rate, fxId) = Conversion(market, position.Denomination.Currency, purpose, maximumFxAge);
            values.Add(new(id, position.Quantity, price.Id, fxId, price.Close, price.Denomination, rate,
                position.Quantity * price.Denomination.ToMajor(price.Close) * rate));
        }
        return new(market.DecisionAt.ToUniversalTime(), purpose, CashGbp, CashGbp + values.Sum(x => x.ValueGbp), values.ToImmutable());
    }
}
