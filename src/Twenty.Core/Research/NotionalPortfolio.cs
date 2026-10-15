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
    public PortfolioStep Step(decimal monthly, IReadOnlyDictionary<string, decimal> weights, IReadOnlyDictionary<string, decimal> returns, ResearchCosts costs)
    {
        costs.Validate();
        if (monthly < 0 || monthly > 100000 || weights.Any(x => x.Value < 0) || weights.Values.Sum() > 1m || returns.Any(x => x.Value < -1m) ||
            weights.Keys.Concat(holdings.Keys).Any(s => !returns.ContainsKey(s))) throw new ResearchException("invalid-portfolio-step");
        // Compute privately before committing: invalid data/arithmetic cannot half-mutate a portfolio.
        var next = new Dictionary<string, decimal>(holdings, StringComparer.Ordinal);
        var cash = Cash + monthly;
        var before = cash + next.Values.Sum();
        var target = weights.ToDictionary(x => x.Key, x => before * x.Value, StringComparer.Ordinal);
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
        // Only decimal residue at the precision limit may be normalised; no economic borrowing.
        if (cash < -0.00000000000000000001m) throw new ResearchException("portfolio-borrowing");
        cash = Math.Max(0, cash);
        next = next.Where(x => x.Value > 0).ToDictionary(x => x.Key, x => x.Value * (1 + returns[x.Key]), StringComparer.Ordinal);
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
