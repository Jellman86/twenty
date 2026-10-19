using System.Collections.Immutable;

namespace Twenty.Core.Research;

public sealed record UniverseBacktestConfig(decimal MonthlyGbp = 20m, int TopK = 3, decimal MaximumWeight = 0.5m)
{
    public void Validate()
    {
        if (MonthlyGbp is < 1 or > 1000 || TopK is < 1 or > 100 || MaximumWeight is <= 0 or > 1)
            throw new ResearchException("invalid-universe-backtest-config");
    }
}
public sealed record UniverseBacktestReport(string EngineVersion, string PanelSha256, string DatasetId, string UniverseId,
    bool Synthetic, UniverseBacktestConfig Config, ResearchCosts Costs, DateTimeOffset EvaluationStart, DateTimeOffset EvaluationEnd,
    ImmutableArray<StrategySummary> Summaries, ImmutableArray<EquityObservation> Equity, ImmutableArray<string> Limitations);

/// <summary>Monthly £20-style notional backtest over a changing-universe panel. Strategies are
/// deliberately non-learned v1 baselines: equal-weight over every priced eligible listing,
/// 12-1 momentum ranked by compounded trailing period returns, and cash. A panel with gaps
/// (unexplained exits or missing prices) is refused outright: there is no honest way to step
/// a portfolio through a month in which a possible holding has no return.</summary>
public static class UniverseBacktest
{
    public const string Version = "changing-universe-backtest-v1";
    public const int MomentumPeriods = 11; // compounded returns, skipping the most recent period (12-1 style)
    public static readonly ImmutableArray<string> Strategies = ["equal_weight", "momentum", "cash"];

    public static UniverseBacktestReport Run(ChangingUniversePanel panel, UniverseBacktestConfig config, ResearchCosts costs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(panel);
        config.Validate(); costs.Validate();
        if (panel.HasGaps) throw new ResearchException("universe-backtest-gaps");
        var first = MomentumPeriods + 1; // first period index with a complete skipped trailing window
        if (panel.Periods.Length <= first) throw new ResearchException("universe-backtest-history");
        var portfolios = Strategies.ToDictionary(s => s, _ => new NotionalPortfolio());
        var equity = ImmutableArray.CreateBuilder<EquityObservation>();
        for (var p = first; p < panel.Periods.Length; p++)
        {
            ct.ThrowIfCancellationRequested();
            var period = panel.Periods[p];
            var returns = period.Returns.Where(r => r.Return is not null)
                .ToDictionary(r => r.ListingId, r => r.Return!.Value, StringComparer.Ordinal);
            // Names holdable at this decision: eligible then, bearing a realized return, and
            // actually observable then. A late-published first price can compute an ex-post
            // return while the panel recorded the listing as unpriced at the decision; buying
            // it would spend knowledge nobody had. With gaps refused, an eligible name held
            // from the previous period is always priced at this decision (its previous period
            // would otherwise have gapped), so this gate only ever refuses fresh buys.
            var unpriced = panel.Months[p].UnpricedListingIds;
            var holdable = panel.Months[p].EligibleListingIds.Where(returns.ContainsKey)
                .Where(id => !unpriced.Contains(id))
                .Order(StringComparer.Ordinal).ToImmutableArray();
            if (holdable.IsEmpty) throw new ResearchException("universe-backtest-empty-month");
            var momentum = holdable.Select(id => (Id: id, Score: TrailingMomentum(panel, p, id)))
                .Where(x => x.Score is not null).ToArray();
            var allocations = new Dictionary<string, Dictionary<string, decimal>>
            {
                ["equal_weight"] = holdable.ToDictionary(s => s, _ => EqualFraction(holdable.Length), StringComparer.Ordinal),
                ["momentum"] = momentum.Length == 0 ? new()
                    : Backtest.Rank(momentum.Select(x => x.Score!.Value).ToArray(),
                        momentum.Select(x => x.Id).ToImmutableArray(), config.TopK, config.MaximumWeight),
                ["cash"] = new()
            };
            foreach (var strategy in Strategies)
            {
                var slots = strategy == "equal_weight" ? holdable.Length : strategy == "momentum" ? config.TopK : 1;
                var cap = strategy == "momentum" ? config.MaximumWeight : strategy == "equal_weight" ? EqualFraction(holdable.Length) : 1m;
                equity.Add(new(DateOnly.FromDateTime(period.From.UtcDateTime), DateOnly.FromDateTime(period.To.UtcDateTime),
                    strategy, portfolios[strategy].Step(config.MonthlyGbp, allocations[strategy], returns, costs, false, slots, cap,
                        period.Returns.Where(r => r.Status is UniverseReturnStatus.TerminalZero or UniverseReturnStatus.TerminalCash or UniverseReturnStatus.TerminalStock)
                            .Select(r => r.ListingId).ToImmutableHashSet(StringComparer.Ordinal))));
            }
        }
        var summaries = Strategies.Select(s =>
        {
            var portfolio = portfolios[s];
            var observations = equity.Where(r => r.Strategy == s).ToArray();
            var returns = observations.Select(r => (double)r.State.PeriodReturn).ToArray();
            var mean = returns.Average();
            var vol = returns.Length > 1 ? Math.Sqrt(returns.Sum(r => Math.Pow(r - mean, 2)) / (returns.Length - 1) * 12) : 0;
            return new StrategySummary(s, portfolio.Equity, portfolio.Contributions, portfolio.Equity - portfolio.Contributions,
                portfolio.CostsPaid, observations.Sum(r => r.State.TradedNotionalGbp), portfolio.MaxDrawdown,
                Math.Pow((double)portfolio.UnitPrice, 12.0 / returns.Length) - 1, vol,
                returns.Count(r => r > 0) / (double)returns.Length);
        }).ToImmutableArray();
        return new(Version, panel.Sha256, panel.DatasetId, panel.UniverseId, panel.Synthetic, config, costs,
            panel.Periods[first].From, panel.Periods[^1].To, summaries, equity.ToImmutable(),
            ["Returns are local-currency ratios of adjusted closes with no FX conversion; GBP notionals are an accounting unit only, not achievable GBP wealth.",
             "Terminal returns cover only zero, simple cash and simple stock-merger proceeds; spinoffs, partial cash/stock mixes and suspensions are unmodelled, and gap panels are refused.",
             "Full-rebalance fractional notionals with flat per-side costs approximate execution; these are not broker quantities, fills or ISA eligibility evidence.",
             "Momentum ranks require a complete priced trailing window; entrants are excluded until one exists, which is a disclosed selection rule, not evidence of an edge.",
             "A listing with no observable price at the decision (for example a late-published first bar) is not buyable at that decision, even though its realized period return remains in the panel."]);
    }

    // Compounded return over the MomentumPeriods periods ending one period before p.
    // Any non-priced entry (entered, terminal, missing) in the window makes the score unavailable.
    private static double? TrailingMomentum(ChangingUniversePanel panel, int p, string listing)
    {
        var compounded = 1m;
        for (var q = p - MomentumPeriods - 1; q < p - 1; q++)
        {
            var entry = panel.Periods[q].Returns.FirstOrDefault(r => r.ListingId == listing);
            if (entry is not { Status: UniverseReturnStatus.Priced, Return: { } value }) return null;
            compounded *= 1m + value;
        }
        return (double)(compounded - 1m);
    }

    private static decimal EqualFraction(int count) => decimal.Truncate(1m / count * 1000000000000000000000000m) / 1000000000000000000000000m;
}
