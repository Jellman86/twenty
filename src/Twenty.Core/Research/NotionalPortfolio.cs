using System.Collections.Immutable;

namespace Twenty.Core.Research;

public sealed record ResearchCosts(decimal FxBps = 15m, decimal HalfSpreadBps = 5m, decimal SlippageBps = 5m, decimal BuyTaxBps = 0m, decimal MinimumOrderGbp = 1m)
{
    public decimal SellRate => (FxBps + HalfSpreadBps + SlippageBps) / 10000m;
    public decimal BuyRate => SellRate + BuyTaxBps / 10000m;
    public void Validate()
    {
        if (FxBps is < 0 or > 5000 || HalfSpreadBps is < 0 or > 5000 || SlippageBps is < 0 or > 5000 || BuyTaxBps is < 0 or > 5000 || MinimumOrderGbp < 0 || MinimumOrderGbp > 10000 || SellRate >= 1 || BuyRate >= 1)
            throw new ResearchException("invalid-costs");
    }
}
public sealed record NotionalTrade(string Symbol, string Side, decimal NotionalGbp, decimal CostGbp);
public sealed record PortfolioStep(decimal EquityGbp, decimal CashGbp, decimal ContributionsGbp, decimal PeriodReturn,
    decimal PeriodCostGbp, decimal TradedNotionalGbp, decimal UnitPrice, decimal MaxDrawdown, ImmutableArray<NotionalTrade> Trades);

/// <summary>Fractional GBP exposure accounting against total-return indices, not share quantities/broker fills.</summary>
public sealed class NotionalPortfolio
{
    private Dictionary<string, decimal> holdings = new(StringComparer.Ordinal);
    public decimal Cash { get; private set; }
    public decimal Contributions { get; private set; }
    public decimal CostsPaid { get; private set; }
    public decimal UnitPrice { get; private set; } = 1m;
    public decimal HighWater { get; private set; } = 1m;
    public decimal MaxDrawdown { get; private set; }
    public decimal Equity => Cash + holdings.Values.Sum();
    public IReadOnlyDictionary<string, decimal> Holdings => holdings.ToImmutableDictionary(StringComparer.Ordinal);
    public PortfolioStep Step(decimal monthly, IReadOnlyDictionary<string, decimal> weights, IReadOnlyDictionary<string, decimal> returns, ResearchCosts costs,
        bool contributionOnly = false, int maximumHoldings = 100, decimal maximumWeight = 1m, IReadOnlySet<string>? terminalExits = null)
    {
        costs.Validate();
        if (contributionOnly && (maximumHoldings is < 1 or > 100 || maximumWeight is <= 0 or > 1 || weights.Values.Any(w => w > maximumWeight)))
            throw new ResearchException("invalid-contribution-policy");
        if (monthly < 0 || monthly > 100000 || weights.Any(x => x.Value < 0) || weights.Values.Sum() > 1m || returns.Any(x => x.Value < -1m) ||
            weights.Keys.Concat(holdings.Keys).Any(s => !returns.ContainsKey(s))) throw new ResearchException("invalid-portfolio-step");
        // Compute privately before committing: invalid data/arithmetic cannot half-mutate a portfolio.
        var next = new Dictionary<string, decimal>(holdings, StringComparer.Ordinal);
        var cash = Cash + monthly;
        var before = cash + next.Values.Sum();
        var target = weights.ToDictionary(x => x.Key, x => before * x.Value, StringComparer.Ordinal);
        if (contributionOnly)
        {
            // Retain all existing exposure. This is the notional research analogue of the buy-only broker policy.
            target = new Dictionary<string, decimal>(next, StringComparer.Ordinal);
            var floor = before - cash * costs.BuyRate / (1 + costs.BuyRate);
            var slots = Math.Max(0, maximumHoldings - next.Values.Count(v => v > 0));
            var remaining = cash;
            // Purchase cap only: a holding drifted above maximumWeight gets no further buys
            // (its budget below is zero), but contributions still buy underweight names.
            // Cash is already in NAV, so idling it would not reduce the drifted weight either.
            var eligible = new List<KeyValuePair<string, decimal>>();
            foreach (var item in weights.Where(w => w.Value > 0))
            {
                if (next.GetValueOrDefault(item.Key) <= 0)
                { if (slots == 0) continue; slots--; }
                eligible.Add(item);
            }
            foreach (var (symbol, weight) in eligible.OrderByDescending(w => w.Value).ThenBy(w => w.Key, StringComparer.Ordinal))
            {
                var budget = Math.Min(remaining, Math.Max(0, floor * Math.Min(weight, maximumWeight) - next.GetValueOrDefault(symbol)));
                var notional = budget / (1 + costs.BuyRate);
                if (notional <= 0 || notional < costs.MinimumOrderGbp) continue;
                target[symbol] = next.GetValueOrDefault(symbol) + notional;
                remaining -= notional * (1 + costs.BuyRate);
            }
        }
        var trades = ImmutableArray.CreateBuilder<NotionalTrade>();
        foreach (var (symbol, held) in next.ToArray())
        {
            var amount = Math.Max(0m, held - target.GetValueOrDefault(symbol));
            if (amount > 0m && amount >= costs.MinimumOrderGbp)
            {
                var fee = amount * costs.SellRate;
                next[symbol] -= amount; cash += amount - fee;
                trades.Add(new(symbol, "sell", amount, fee));
            }
        }
        var wanted = target.Select(x => (x.Key, Amount: Math.Max(0m, x.Value - next.GetValueOrDefault(x.Key))))
            .Where(x => x.Amount > 0 && x.Amount >= costs.MinimumOrderGbp).ToArray();
        var needed = wanted.Sum(x => x.Amount) * (1 + costs.BuyRate);
        var scale = needed > 0 ? Math.Min(1m, cash / needed) : 0m;
        foreach (var (symbol, desired) in wanted)
        {
            var amount = desired * scale;
            if (amount < costs.MinimumOrderGbp) continue;
            var fee = amount * costs.BuyRate;
            cash -= amount + fee; next[symbol] = next.GetValueOrDefault(symbol) + amount;
            trades.Add(new(symbol, "buy", amount, fee));
        }
        // Cash reserved for a scaled buy that fell below the minimum funds the remaining
        // unmet targets instead of idling; this matters most at very small notionals.
        for (var pass = 0; pass <= wanted.Length; pass++)
        {
            var progressed = false;
            foreach (var (symbol, _) in wanted)
            {
                var unmet = Math.Max(0m, target.GetValueOrDefault(symbol) - next.GetValueOrDefault(symbol));
                var amount = Math.Min(unmet, cash / (1 + costs.BuyRate));
                if (amount < costs.MinimumOrderGbp || amount <= 0) continue;
                var fee = amount * costs.BuyRate;
                cash -= amount + fee; next[symbol] = next.GetValueOrDefault(symbol) + amount;
                trades.Add(new(symbol, "buy", amount, fee)); progressed = true;
            }
            if (!progressed) break;
        }
        // Only decimal residue at the precision limit may be normalised; no economic borrowing.
        if (cash < -0.00000000000000000001m) throw new ResearchException("portfolio-borrowing");
        cash = Math.Max(0, cash);
        next = next.Where(x => x.Value > 0).ToDictionary(x => x.Key, x => x.Value * (1 + returns[x.Key]), StringComparer.Ordinal)
            .Where(x => x.Value > 0).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        // Terminal returns (zero or cash) are realized outcomes, not continuing positions:
        // move their proceeds into cash and drop the phantom holding so the next step's
        // return lookup cannot fail on a name that has left the universe. Zero proceeds
        // contribute nothing; cash proceeds are the realized value already computed.
        if (terminalExits is not null)
        {
            var cashFromTerminal = 0m;
            foreach (var symbol in terminalExits)
                if (next.TryGetValue(symbol, out var proceeds))
                {
                    cashFromTerminal += proceeds;
                    next.Remove(symbol);
                }
            cash += cashFromTerminal;
        }
        var equity = cash + next.Values.Sum();
        var periodReturn = before > 0 ? equity / before - 1 : 0;
        var unit = UnitPrice * (1 + periodReturn);
        var high = Math.Max(HighWater, unit);
        var drawdown = Math.Min(MaxDrawdown, unit / high - 1);
        holdings = next; Cash = cash; Contributions += monthly; CostsPaid += trades.Sum(x => x.CostGbp);
        UnitPrice = unit; HighWater = high; MaxDrawdown = drawdown;
        return new(equity, cash, Contributions, periodReturn, trades.Sum(x => x.CostGbp), trades.Sum(x => x.NotionalGbp), unit, drawdown, trades.ToImmutable());
    }
}
