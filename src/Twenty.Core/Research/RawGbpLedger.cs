using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawQuantityPosition(decimal Quantity, Denomination Denomination, DateTimeOffset OpenedAt);
public sealed record RawLedgerEntry(string Id, DateTimeOffset At, string Kind, string? ListingId, decimal Quantity,
    decimal? RawPrice, Denomination? Denomination, decimal FeeGbp, decimal CashChangeGbp, string? FxId,
    decimal GbpPerLocalUnit, decimal CashAfterGbp)
{
    public string? ActionId { get; init; }
    public string? ActionObservationId { get; init; }
    public DateTimeOffset? ActionEffectiveAt { get; init; }
    public decimal? SplitRatio { get; init; }
}
public sealed record RawPositionValue(string ListingId, decimal Quantity, string PriceId, string? FxId,
    decimal RawPrice, Denomination Denomination, decimal GbpPerLocalUnit, decimal ValueGbp);
public sealed record RawGbpValuation(DateTimeOffset At, FxPurpose FxPurpose, decimal CashGbp, decimal EquityGbp,
    ImmutableArray<RawPositionValue> Positions);

/// <summary>Offline, single-owner decimal reference for explicit raw fills and GBP cash.
/// Supplied fill prices are execution assumptions, never inferred from an adjusted bar.
/// This is not an order adapter, a historical execution simulator or a return-index portfolio.
/// Exact split quantities can be settled explicitly; cash/stock distributions remain refused.</summary>
public sealed class RawGbpLedger
{
    private ImmutableDictionary<string, RawQuantityPosition> holdings = ImmutableDictionary.Create<string, RawQuantityPosition>(StringComparer.Ordinal);
    private ImmutableHashSet<string> ids = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    private ImmutableDictionary<(string ListingId, string ActionId), CorporateAction> bookedActions =
        ImmutableDictionary<(string ListingId, string ActionId), CorporateAction>.Empty;
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
    private void CheckBookedActions(DataSlice market)
    {
        foreach (var (key, original) in bookedActions)
        {
            var current = market.EffectiveActions(key.ListingId).SingleOrDefault(a => a.ActionId == key.ActionId);
            if (current is null) throw new ResearchException("raw-ledger-action-evidence");
            // An identical economic vintage need not block every subsequent capture.
            // Keep the original receipt; changed/withdrawn terms need explicit reconstruction.
            if (current with { Id = original.Id, Evidence = original.Evidence } != original)
                throw new ResearchException("raw-ledger-action-revised");
        }
    }
    private static bool IsNeutralAction(CorporateAction action) =>
        action.Action is CorporateActionKind.SymbolChange or CorporateActionKind.Split or CorporateActionKind.Dividend &&
        action.TerminalOutcome == TerminalOutcome.NotApplicable && action.SuccessorListingId is null &&
        action.CashPerShare is null or 0 && action.SplitRatio is null or 1 &&
        (action.NewSymbol is null || action.Action == CorporateActionKind.SymbolChange);
    private void CheckHoldings(DataSlice market, CorporateAction? settling = null)
    {
        CheckBookedActions(market);
        foreach (var (id, position) in holdings)
        {
            if (market.EffectiveActions(id).Any(a => a.EffectiveAt > position.OpenedAt &&
                (settling is null || a.EffectiveAt <= settling.EffectiveAt) && a != settling &&
                !bookedActions.ContainsKey((a.ListingId, a.ActionId)) && !IsNeutralAction(a))) throw new ResearchException("raw-ledger-action-unsupported");
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
    public void ApplySplit(string id, DataSlice market, string listingId, string actionId)
    {
        ArgumentNullException.ThrowIfNull(market);
        CheckEvent(id, market.DecisionAt); CheckBookedActions(market);
        var action = market.EffectiveActions(listingId).SingleOrDefault(a => a.ActionId == actionId);
        if (action is not { Action: CorporateActionKind.Split, SplitRatio: > 0 }) throw new ResearchException("raw-ledger-action");
        if (action.CashPerShare is not (null or 0) || action.SuccessorListingId is not null ||
            action.TerminalOutcome != TerminalOutcome.NotApplicable || action.NewSymbol is not null)
            throw new ResearchException("raw-ledger-action-complex");
        var key = (listingId, actionId);
        if (bookedActions.ContainsKey(key)) throw new ResearchException("raw-ledger-action-already-booked");
        if (!holdings.TryGetValue(listingId, out var position) || position.OpenedAt >= action.EffectiveAt)
            throw new ResearchException("raw-ledger-action-position");
        // A late disclosure cannot reinterpret a fill that used the old share count.
        // Previously booked earlier splits are compared by effective time, not receipt time.
        if (Entries.Any(e => e.ListingId == listingId &&
            ((e.Kind is "buy" or "sell") && e.At >= action.EffectiveAt || e.Kind == "split" && e.ActionEffectiveAt >= action.EffectiveAt)))
            throw new ResearchException("raw-ledger-action-history");
        CheckHoldings(market, action);
        var quantity = position.Quantity * action.SplitRatio.Value;
        if (quantity <= 0) throw new ResearchException("raw-ledger-quantity");
        var next = holdings.SetItem(listingId, position with { Quantity = quantity });
        var actions = bookedActions.Add(key, action);
        Record(new(id, market.DecisionAt.ToUniversalTime(), "split", listingId, quantity - position.Quantity,
            null, position.Denomination, 0, 0, null, 1, CashGbp)
        { ActionId = action.ActionId, ActionObservationId = action.Id, ActionEffectiveAt = action.EffectiveAt.ToUniversalTime(), SplitRatio = action.SplitRatio });
        holdings = next; bookedActions = actions;
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
