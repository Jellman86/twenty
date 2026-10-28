using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawRebalanceClaimAssumptions(ImmutableArray<RawDividendMarkAssumption> Dividends,
    ImmutableArray<RawTerminalMarkAssumption> Terminals);

internal static class RawClaimRebalancePlanner
{
    public static RawRebalancePlan Plan(RawGbpLedger ledger, DataSlice market, string universeId,
        IReadOnlyDictionary<string, decimal> weights, DateTimeOffset nextDecisionAt, decimal cashReserveGbp, decimal feePerFillGbp,
        TimeSpan maximumPriceAge, TimeSpan maximumFxAge, RawRebalanceClaimAssumptions assumptions)
    {
        ArgumentNullException.ThrowIfNull(ledger); ArgumentNullException.ThrowIfNull(market); ArgumentNullException.ThrowIfNull(weights);
        if (string.IsNullOrWhiteSpace(universeId) || universeId.Length > 200 || nextDecisionAt <= market.DecisionAt ||
            cashReserveGbp < 0 || feePerFillGbp < 0 || maximumPriceAge < TimeSpan.Zero || maximumFxAge < TimeSpan.Zero)
            throw new ResearchException("raw-rebalance-policy");
        if (assumptions.Dividends.IsDefault || assumptions.Terminals.IsDefault ||
            (long)assumptions.Dividends.Length + assumptions.Terminals.Length > RawRebalancePlanner.MaximumPendingClaims)
            throw new ResearchException("raw-rebalance-claims-capacity");
        var input = weights.Take(RawRebalancePlanner.MaximumTargets + 1).ToArray();
        if (input.Length > RawRebalancePlanner.MaximumTargets || input.Any(w => string.IsNullOrWhiteSpace(w.Key) || w.Key.Length > 200 || w.Value is <= 0 or > 1) ||
            input.Select(w => w.Key).Distinct(StringComparer.Ordinal).Count() != input.Length || input.Sum(w => w.Value) > 1)
            throw new ResearchException("raw-rebalance-weights");
        var frozen = input.ToImmutableSortedDictionary(w => w.Key, w => w.Value, StringComparer.Ordinal);
        var subjects = ledger.Holdings.Keys.Concat(frozen.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Take(RawRebalancePlanner.MaximumSubjects + 1).ToArray();
        if (subjects.Length > RawRebalancePlanner.MaximumSubjects) throw new ResearchException("raw-rebalance-subjects");
        var eligible = market.Universe(universeId).Select(l => l.ListingId).ToHashSet(StringComparer.Ordinal);
        if (frozen.Keys.Any(id => !eligible.Contains(id))) throw new ResearchException("raw-rebalance-ineligible");
        try { return Build(); }
        catch (OverflowException) { throw new ResearchException("raw-rebalance-arithmetic"); }

        RawRebalancePlan Build()
        {
            var claims = ledger.ValueWithReceivables(market, maximumPriceAge, maximumFxAge, FxPurpose.Executable,
                assumptions.Dividends, assumptions.Terminals);
            if (claims.ExpectedEquityGbp < 0) throw new ResearchException("raw-rebalance-claim-equity");
            var value = claims.HoldingsAndCash;
            var positions = value.Positions.ToDictionary(p => p.ListingId, StringComparer.Ordinal);
            var pendingStock = claims.TerminalReceivables.Where(c => c.Successor is not null).GroupBy(c => c.Successor!.ListingId)
                .ToDictionary(g => g.Key, g => g.Sum(c => c.Successor!.ValueGbp), StringComparer.Ordinal);
            // Fees already reduce expected equity. This additional amount is a cash funding floor,
            // not another subtraction from equity. Stock cannot pay its own cash delivery fee.
            var claimReserve = claims.DividendReceivables.Sum(c => Math.Max(0, -c.ExpectedValueGbp)) +
                claims.TerminalReceivables.Sum(c => c.Successor is not null ? c.Assumption.ExpectedFeeGbp : Math.Max(0, -c.ExpectedValueGbp));
            var reserve = cashReserveGbp + claimReserve;
            var beforeFees = Math.Max(0, claims.ExpectedEquityGbp - cashReserveGbp);
            var unchanged = subjects.All(id => (!positions.ContainsKey(id) || frozen.ContainsKey(id)) &&
                Math.Max(0, beforeFees * frozen.GetValueOrDefault(id) - pendingStock.GetValueOrDefault(id)) == (positions.GetValueOrDefault(id)?.ValueGbp ?? 0));
            var feeBudget = unchanged ? 0 : subjects.Length * feePerFillGbp;
            var targetEquity = Math.Max(0, beforeFees - feeBudget);
            var liquidBudget = Math.Max(0, value.EquityGbp - reserve - feeBudget);
            var listings = market.ListingsAt(market.DecisionAt).ToDictionary(l => l.ListingId, StringComparer.Ordinal);
            var targets = subjects.Select(id =>
            {
                if (!listings.TryGetValue(id, out var listing) || listing.Status != ListingStatus.Active) throw new ResearchException("raw-rebalance-listing");
                if (market.EffectiveActions(id).Any(a => a.Action is CorporateActionKind.Delisting or CorporateActionKind.Merger &&
                    a.TerminalOutcome != TerminalOutcome.NotApplicable)) throw new ResearchException("raw-rebalance-terminal");
                var price = market.Prices(id, PriceBasis.Raw).Where(p => p.SessionCloseAt <= market.DecisionAt).OrderByDescending(p => p.SessionCloseAt).FirstOrDefault();
                if (price is null || market.DecisionAt - price.SessionCloseAt > maximumPriceAge) throw new ResearchException("raw-rebalance-price");
                if (price.Denomination != listing.Denomination) throw new ResearchException("raw-rebalance-units");
                RawPriceShareUnits.Validate(market, price);
                var fx = listing.Denomination.Currency == "GBP" ? null : market.Fx(listing.Denomination.Currency, "GBP", FxPurpose.Executable, maximumFxAge);
                var unit = price.Denomination.ToMajor(price.Close) * (fx?.QuoteUnitsPerBase ?? 1);
                if (unit <= 0) throw new ResearchException("raw-rebalance-arithmetic");
                var quote = new RawRebalanceQuote(listing, price, market.Sources.Single(s => s.Id == price.Evidence.SourceId),
                    fx, fx is null ? null : market.Sources.Single(s => s.Id == fx.Evidence.SourceId));
                var targetValue = targetEquity * frozen.GetValueOrDefault(id);
                var currentQuantity = positions.GetValueOrDefault(id)?.Quantity ?? 0;
                var deliveredTargetValue = Math.Max(0, targetValue - pendingStock.GetValueOrDefault(id));
                var targetQuantity = unchanged ? currentQuantity : deliveredTargetValue / unit;
                if (deliveredTargetValue > 0 && targetQuantity <= 0) throw new ResearchException("raw-rebalance-arithmetic");
                return new RawRebalanceTarget(id, frozen.GetValueOrDefault(id), currentQuantity,
                    targetQuantity, targetValue, quote)
                    { PendingSuccessorValueGbp = pendingStock.GetValueOrDefault(id) };
            }).ToArray();
            var funded = targets.ToDictionary(t => t.ListingId, t => t.CurrentQuantity, StringComparer.Ordinal);
            var orders = ImmutableArray.CreateBuilder<PlannedOrder>(); var cash = value.CashGbp;
            decimal Unit(RawRebalanceTarget t) => t.Quote.Price.Denomination.ToMajor(t.Quote.Price.Close) * (t.Quote.Fx?.QuoteUnitsPerBase ?? 1);
            decimal Cost(RawRebalanceTarget t, decimal quantity) => quantity * t.Quote.Price.Denomination.ToMajor(t.Quote.Price.Close) * (t.Quote.Fx?.QuoteUnitsPerBase ?? 1);
            void Order(RawRebalanceTarget target, bool buy, decimal quantity)
            {
                orders.Add(new(target.ListingId, buy, 0, market.DecisionAt.ToUniversalTime(), nextDecisionAt.ToUniversalTime())
                    { Quantity = quantity, FxObservationId = target.Quote.Fx?.Id });
                funded[target.ListingId] += buy ? quantity : -quantity;
            }
            // Order priority is frozen: ordinal sales, then ordinal cash-capped purchases.
            foreach (var target in targets.Where(t => t.TargetQuantity < t.CurrentQuantity))
            {
                var quantity = target.CurrentQuantity - target.TargetQuantity; var after = cash + (Cost(target, quantity) - feePerFillGbp);
                if (after < 0) continue; // The sale cannot borrow its fee from a later sale or claim.
                cash = after; Order(target, false, quantity);
            }
            foreach (var target in targets.Where(t => t.TargetQuantity > t.CurrentQuantity))
            {
                var available = Math.Max(0, cash - reserve - feePerFillGbp);
                if (available == 0) continue;
                var quantity = Math.Min(target.TargetQuantity - target.CurrentQuantity, available / Unit(target));
                if (quantity <= 0) continue;
                quantity = RawQuantityRounding.Fit(quantity, q => cash + (-Cost(target, q) - feePerFillGbp) >= reserve);
                var after = cash + (-Cost(target, quantity) - feePerFillGbp);
                if (after < 0 || after < Math.Min(cash, reserve)) throw new ResearchException("raw-rebalance-rounding");
                cash = after; Order(target, true, quantity);
            }
            var deferred = targets.Sum(t => Math.Abs(t.TargetQuantity - funded[t.ListingId]) * Unit(t));
            return new(RawRebalancePlanner.ClaimsVersion, universeId, value, nextDecisionAt.ToUniversalTime(), maximumPriceAge.Ticks,
                maximumFxAge.Ticks, cashReserveGbp, feePerFillGbp, feeBudget, liquidBudget, cash, frozen,
                targets.Select(t => t with { FundedTargetQuantity = funded[t.ListingId] }).ToImmutableArray(), orders.ToImmutable(),
                ["Expected equity sets target proportions. Pending successor value is credited to its identity before calculating desired delivered quantities.",
                 "Only actual cash and ordered sale proceeds fund orders. Positive cash claims and undelivered shares never become buying power.",
                 "Ordinal sales precede ordinal cash-capped buys. Unfunded target changes and reserve shortfall are explicit deferrals, not fills or borrowing.",
                 "Expected negative claim values and stock-delivery fees impose a cash funding floor; estimates do not guarantee later payment funding.",
                 "Every emitted order still requires the separate raw-price/session/FX/minimum/participation/quantity-step execution checks. No execution guarantee or broker authority is granted."])
                { ClaimAssumptions = assumptions, ExpectedTargetEquityGbp = targetEquity, ClaimCashReserveGbp = claimReserve,
                  ReserveShortfallGbp = Math.Max(0, reserve - cash), DeferredOrderValueGbp = deferred };
        }
    }
}
