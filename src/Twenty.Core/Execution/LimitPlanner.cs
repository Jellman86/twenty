using System.Collections.Immutable;
using Twenty.Core.Research;

namespace Twenty.Core.Execution;

public sealed record InstrumentRule(string Symbol, string BrokerTicker, string QuoteCurrency, decimal Ask, decimal LimitPrice,
    decimal QuantityStep, decimal MinimumNotionalGbp, decimal MaximumFeeBps, DateTimeOffset ObservedAt, bool Eligible);
public sealed record BuyIntent(string Symbol, string BrokerTicker, decimal Quantity, decimal LimitPrice, string QuoteCurrency,
    decimal MaximumDebitGbp, decimal MaximumFeeBps);
public sealed record BuyPlan(ImmutableArray<BuyIntent> Orders, decimal CashRemainingGbp, ImmutableArray<string> Notes);

public static class LimitPlanner
{
    // Contribution-only purchases within a dedicated sleeve. Existing holdings are never sold here.
    // Quotes and rules must be sourced independently: Trading 212 metadata does not supply all of them.
    public static BuyPlan Plan(decimal cash, IReadOnlyDictionary<string, decimal> quantities,
        IReadOnlyDictionary<string, decimal> pendingQuantities, IReadOnlyDictionary<string, decimal> targets,
        IReadOnlyDictionary<string, InstrumentRule> rules, decimal maximumWeight, DateTimeOffset now)
    {
        if (cash is < 0 or > 1000000 || maximumWeight is <= 0 or > 1 || targets.Count > 10 ||
            targets.Values.Any(v => v < 0 || v > maximumWeight) || targets.Values.Sum() > 1 ||
            quantities.Values.Concat(pendingQuantities.Values).Any(v => v < 0)) throw new ResearchException("execution-allocation");
        foreach (var symbol in targets.Keys.Concat(quantities.Keys).Concat(pendingQuantities.Keys).Distinct())
        {
            if (!rules.TryGetValue(symbol, out var r) || !r.Eligible || r.Symbol != symbol || string.IsNullOrWhiteSpace(r.BrokerTicker) ||
                r.QuoteCurrency is not ("GBP" or "GBX") || r.Ask is <= 0 or > 1000000000 || r.LimitPrice < r.Ask || r.LimitPrice > r.Ask * 1.02m ||
                r.QuantityStep is < 0.00000001m or > 100000 || r.MinimumNotionalGbp is < 0 or > 1000 || r.MaximumFeeBps is < 0 or > 1000 ||
                r.ObservedAt > now || now - r.ObservedAt > TimeSpan.FromMinutes(5)) throw new ResearchException("execution-inputs");
        }
        var usedRules = targets.Keys.Concat(quantities.Keys).Concat(pendingQuantities.Keys).Distinct().Select(s => rules[s]).ToArray();
        if (usedRules.Select(r => r.BrokerTicker).Distinct(StringComparer.Ordinal).Count() != usedRules.Length)
            throw new ResearchException("execution-duplicate-listing");
        decimal Price(string symbol) => rules[symbol].Ask / (rules[symbol].QuoteCurrency == "GBX" ? 100 : 1);
        var exposures = quantities.Keys.Concat(pendingQuantities.Keys).Distinct().ToDictionary(s => s,
            s => (quantities.GetValueOrDefault(s) + pendingQuantities.GetValueOrDefault(s)) * Price(s));
        var nav = cash + exposures.Values.Sum(); // cash excludes pending debits; pending share exposure is included above.
        // Bound post-fill NAV after both fees and paying a limit above the observed ask.
        // Applying the largest debit-to-mark loss to all spendable cash is conservative.
        var lossFraction = usedRules.Length == 0 ? 0 : usedRules.Max(r => 1 - r.Ask / (r.LimitPrice * (1 + r.MaximumFeeBps / 10000)));
        var navFloor = nav - cash * lossFraction;
        var remaining = cash;
        var orders = ImmutableArray.CreateBuilder<BuyIntent>(); var notes = ImmutableArray.CreateBuilder<string>();
        foreach (var symbol in exposures.Keys.Where(s => exposures[s] > navFloor * maximumWeight))
            notes.Add($"{symbol}: existing or worst-case post-cost exposure exceeds the cap; retain cash. This purchase policy cannot sell to repair drift.");
        if (notes.Count > 0) return new([], cash, notes.ToImmutable());
        foreach (var (symbol, weight) in targets.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
        {
            var rule = rules[symbol]; var limitGbp = rule.LimitPrice / (rule.QuoteCurrency == "GBX" ? 100 : 1);
            var gap = Math.Max(0, navFloor * weight - exposures.GetValueOrDefault(symbol));
            var budget = Math.Min(remaining, gap);
            var quantity = decimal.Floor(budget / (limitGbp * (1 + rule.MaximumFeeBps / 10000)) / rule.QuantityStep) * rule.QuantityStep;
            var notional = quantity * limitGbp;
            if (quantity <= 0 || notional < rule.MinimumNotionalGbp) { if (gap > 0) notes.Add($"{symbol}: below the verified minimum after rounding; retain cash."); continue; }
            var reserve = notional * (1 + rule.MaximumFeeBps / 10000);
            if (reserve > remaining || exposures.GetValueOrDefault(symbol) + notional > navFloor * maximumWeight) throw new ResearchException("execution-budget");
            orders.Add(new(symbol, rule.BrokerTicker, quantity, rule.LimitPrice, rule.QuoteCurrency, reserve, rule.MaximumFeeBps));
            remaining -= reserve;
        }
        return new(orders.ToImmutable(), remaining, notes.ToImmutable());
    }
}

public sealed record OrderProgress(decimal Requested, decimal Filled, decimal DebitGbp, decimal ReservedGbp, string Status)
{
    public OrderProgress Reconcile(decimal cumulativeFilled, decimal cumulativeDebit, string status)
    {
        if (cumulativeFilled < Filled || cumulativeFilled > Requested || cumulativeDebit < DebitGbp ||
            cumulativeDebit > ReservedGbp || status is not ("pending" or "partially_filled" or "filled" or "cancelled" or "rejected") ||
            (status == "filled" && cumulativeFilled != Requested) || (status == "rejected" && cumulativeFilled != 0) ||
            (cumulativeFilled > 0 && cumulativeDebit <= 0) || (cumulativeFilled == 0 && cumulativeDebit != 0) ||
            (Status is "filled" or "cancelled" or "rejected" && (status != Status || cumulativeFilled != Filled || cumulativeDebit != DebitGbp)))
            throw new ResearchException("fill-reconciliation");
        return this with { Filled = cumulativeFilled, DebitGbp = cumulativeDebit, Status = status };
    }
    public decimal RemainingReserve => Status is "filled" or "cancelled" or "rejected" ? 0 : ReservedGbp - DebitGbp;
}
