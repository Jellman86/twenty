using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawRebalanceQuote(Listing Listing, PriceBar Price, SourceSnapshot PriceSource,
    FxRate? Fx, SourceSnapshot? FxSource);
public sealed record RawRebalanceTarget(string ListingId, decimal Weight, decimal CurrentQuantity,
    decimal TargetQuantity, decimal TargetValueGbp, RawRebalanceQuote Quote)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public decimal? FundedTargetQuantity { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public decimal PendingSuccessorValueGbp { get; init; }
}
public sealed record RawRebalancePlan(string Version, string UniverseId, RawGbpValuation Valuation,
    DateTimeOffset NextDecisionAt, long MaximumPriceAgeTicks, long MaximumFxAgeTicks, decimal CashReserveGbp, decimal FeePerFillGbp, decimal FeeBudgetGbp,
    decimal InvestableEquityGbp, decimal ProjectedCashAfterFeesGbp,
    ImmutableSortedDictionary<string, decimal> Weights, ImmutableArray<RawRebalanceTarget> Targets,
    ImmutableArray<PlannedOrder> Orders, ImmutableArray<string> Limitations)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RawRebalanceClaimAssumptions? ClaimAssumptions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public decimal? ExpectedTargetEquityGbp { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public decimal ClaimCashReserveGbp { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public decimal ReserveShortfallGbp { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public decimal DeferredOrderValueGbp { get; init; }
}

/// <summary>Pure decision-time conversion of supplied targets into share intents over a single-owner
/// ledger. It neither selects a strategy nor executes, persists, or certifies the resulting trades.</summary>
public static class RawRebalancePlanner
{
    public const string Version = "raw-rebalance-v1";
    public const string ClaimsVersion = "raw-rebalance-claims-v1";
    public const int MaximumPendingClaims = 8;
    public static RawRebalancePlan PlanWithReceivables(RawGbpLedger ledger, DataSlice market, string universeId,
        IReadOnlyDictionary<string, decimal> weights, DateTimeOffset nextDecisionAt, decimal cashReserveGbp, decimal feePerFillGbp,
        TimeSpan maximumPriceAge, TimeSpan maximumFxAge, ImmutableArray<RawDividendMarkAssumption> dividends,
        ImmutableArray<RawTerminalMarkAssumption> terminals) =>
        RawClaimRebalancePlanner.Plan(ledger, market, universeId, weights, nextDecisionAt, cashReserveGbp, feePerFillGbp,
            maximumPriceAge, maximumFxAge, new(dividends, terminals));
    public const int MaximumTargets = 100;
    public const int MaximumSubjects = 200;
    public static RawRebalancePlan Plan(RawGbpLedger ledger, DataSlice market, string universeId,
        IReadOnlyDictionary<string, decimal> weights, DateTimeOffset nextDecisionAt,
        decimal cashReserveGbp, decimal feePerFillGbp, TimeSpan maximumPriceAge, TimeSpan maximumFxAge)
    {
        ArgumentNullException.ThrowIfNull(ledger); ArgumentNullException.ThrowIfNull(market); ArgumentNullException.ThrowIfNull(weights);
        if (string.IsNullOrWhiteSpace(universeId) || universeId.Length > 200 || nextDecisionAt <= market.DecisionAt ||
            cashReserveGbp < 0 || feePerFillGbp < 0 || maximumPriceAge < TimeSpan.Zero || maximumFxAge < TimeSpan.Zero)
            throw new ResearchException("raw-rebalance-policy");
        // Snapshot caller enumeration before consulting the single-owner ledger.
        var input = weights.Take(MaximumTargets + 1).ToArray();
        if (input.Length > MaximumTargets || input.Any(w => string.IsNullOrWhiteSpace(w.Key) || w.Key.Length > 200 || w.Value is <= 0 or > 1) ||
            input.Select(w => w.Key).Distinct(StringComparer.Ordinal).Count() != input.Length || input.Sum(w => w.Value) > 1)
            throw new ResearchException("raw-rebalance-weights");
        var frozen = input.ToImmutableSortedDictionary(w => w.Key, w => w.Value, StringComparer.Ordinal);
        var subjects = ledger.Holdings.Keys.Concat(frozen.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Take(MaximumSubjects + 1).ToArray();
        if (subjects.Length > MaximumSubjects) throw new ResearchException("raw-rebalance-subjects");
        var eligible = market.Universe(universeId).Select(l => l.ListingId).ToHashSet(StringComparer.Ordinal);
        if (frozen.Keys.Any(id => !eligible.Contains(id))) throw new ResearchException("raw-rebalance-ineligible");
        try { return Build(); }
        catch (OverflowException) { throw new ResearchException("raw-rebalance-arithmetic"); }

        RawRebalancePlan Build()
        {
            // Ordinary valuation refuses unresolved actions and every unsettled claim.
            // Accrual estimates are never admitted as spendable rebalance funding.
            var value = ledger.Value(market, maximumPriceAge, maximumFxAge, FxPurpose.Executable);
            var positions = value.Positions.ToDictionary(p => p.ListingId, StringComparer.Ordinal);
            var beforeFees = value.EquityGbp - cashReserveGbp;
            if (beforeFees < 0) throw new ResearchException("raw-rebalance-reserve");
            // Reserving fees must not manufacture a sale when no trade was needed.
            var unchanged = subjects.All(id => (!positions.ContainsKey(id) || frozen.ContainsKey(id)) &&
                beforeFees * frozen.GetValueOrDefault(id) == (positions.GetValueOrDefault(id)?.ValueGbp ?? 0));
            var feeBudget = unchanged ? 0 : subjects.Length * feePerFillGbp;
            var investable = beforeFees - feeBudget;
            if (investable < 0) throw new ResearchException("raw-rebalance-reserve");
            var listings = market.ListingsAt(market.DecisionAt).ToDictionary(l => l.ListingId, StringComparer.Ordinal);
            var targets = ImmutableArray.CreateBuilder<RawRebalanceTarget>(); var orders = new List<PlannedOrder>();
            var projectedCash = value.CashGbp;
            foreach (var id in subjects)
            {
                if (!listings.TryGetValue(id, out var listing) || listing.Status != ListingStatus.Active)
                    throw new ResearchException("raw-rebalance-listing");
                if (market.EffectiveActions(id).Any(a => a.Action is CorporateActionKind.Delisting or CorporateActionKind.Merger &&
                    a.TerminalOutcome != TerminalOutcome.NotApplicable)) throw new ResearchException("raw-rebalance-terminal");
                var price = market.Prices(id, PriceBasis.Raw).Where(p => p.SessionCloseAt <= market.DecisionAt)
                    .OrderByDescending(p => p.SessionCloseAt).FirstOrDefault();
                if (price is null || market.DecisionAt - price.SessionCloseAt > maximumPriceAge)
                    throw new ResearchException("raw-rebalance-price");
                if (price.Denomination != listing.Denomination) throw new ResearchException("raw-rebalance-units");
                var fx = listing.Denomination.Currency == "GBP" ? null : market.Fx(listing.Denomination.Currency, "GBP", FxPurpose.Executable, maximumFxAge);
                var unitValue = listing.Denomination.ToMajor(price.Close) * (fx?.QuoteUnitsPerBase ?? 1);
                if (unitValue <= 0) throw new ResearchException("raw-rebalance-arithmetic");
                var weight = frozen.GetValueOrDefault(id); var targetValue = investable * weight;
                var currentQuantity = positions.GetValueOrDefault(id)?.Quantity ?? 0;
                var targetQuantity = unchanged ? currentQuantity : targetValue / unitValue;
                if (targetValue > 0 && targetQuantity <= 0) throw new ResearchException("raw-rebalance-arithmetic");
                var quote = new RawRebalanceQuote(listing, price, market.Sources.Single(s => s.Id == price.Evidence.SourceId),
                    fx, fx is null ? null : market.Sources.Single(s => s.Id == fx.Evidence.SourceId));
                targets.Add(new(id, weight, currentQuantity, targetQuantity, targetValue, quote));
                var change = targetQuantity - currentQuantity;
                if (change == 0) continue;
                orders.Add(new(id, change > 0, 0, market.DecisionAt.ToUniversalTime(), nextDecisionAt.ToUniversalTime())
                { Quantity = Math.Abs(change), FxObservationId = fx?.Id });
            }
            var targetRows = targets.ToImmutable();
            var orderedOrders = orders.OrderBy(o => o.Buy).ThenBy(o => o.ListingId, StringComparer.Ordinal).ToImmutableArray();
            var quotes = targetRows.ToDictionary(t => t.ListingId, t => t.Quote, StringComparer.Ordinal);
            foreach (var order in orderedOrders)
            {
                var quote = quotes[order.ListingId];
                var signedQuantity = order.Buy ? order.Quantity!.Value : -order.Quantity!.Value;
                // Match ledger arithmetic and the declared execution order, including
                // funding a sale's own fee before relying on any later sale proceeds.
                projectedCash += -signedQuantity * quote.Price.Denomination.ToMajor(quote.Price.Close) *
                    (quote.Fx?.QuoteUnitsPerBase ?? 1) - feePerFillGbp;
                if (projectedCash < 0) throw new ResearchException("raw-rebalance-cash");
            }
            // Do not silently borrow to hide a decimal rounding boundary.
            if (projectedCash < cashReserveGbp) throw new ResearchException("raw-rebalance-rounding");
            return new(Version, universeId, value, nextDecisionAt.ToUniversalTime(), maximumPriceAge.Ticks, maximumFxAge.Ticks, cashReserveGbp, feePerFillGbp, feeBudget,
                investable, projectedCash, frozen, targetRows, orderedOrders,
                ["Offline conversion of supplied positive target weights, not a registered strategy or eligibility certification.",
                 "Targets use known raw closes and executable-purpose FX; exact decision FX identifiers remain pinned at later execution.",
                 "One fee per current/target subject is reserved before sizing; unused fee budget stays cash. Claims must be settled.",
                 "Sells precede buys in ordinal listing order. Same-open cash settlement is a modelling assumption, not a broker guarantee.",
                 "Opening gaps, spread, quantity steps, minimums, volume, stale FX or unavailable sale proceeds can refuse execution; no funding or target-weight guarantee is made.",
                 "This plan does not persist or bind its original weights into a replay archive; registered admission and source-wide action scheduling remain separate work."]);
        }
    }
}
