using System.Collections.Immutable;
using System.Globalization;

namespace Twenty.Core.Research;

public sealed record ExperimentConfig(decimal MonthlyGbp = 20m, int TopK = 3, decimal MaximumWeight = 0.5m,
    int TrainMonths = 60, int RefitMonths = 12, int HoldoutMonths = 24, double RidgeAlpha = 10,
    string Hypothesis = "ridge-relative-return", bool EvaluateHoldout = false, int ForwardYears = 10, int Seed = 212, string AllocationPolicy = "full-rebalance", DateOnly? DevelopmentStart = null, DateOnly? DevelopmentEnd = null, string SleeveSpec = "")
{
    public void Validate(MonthlyPanel panel)
    {
        if (MonthlyGbp is < 1 or > 1000 || TopK is < 1 or > 10 || MaximumWeight is <= 0 or > 1 || TrainMonths is < 24 or > 240 ||
            RefitMonths is < 1 or > 24 || HoldoutMonths is < 12 or > 120 || !double.IsFinite(RidgeAlpha) || RidgeAlpha is < 0.01 or > 1000 ||
            AllocationPolicy is not ("full-rebalance" or "contribution-only") || Hypothesis is not ("ridge-relative-return" or "momentum" or "breakout" or "upside-tail" or "core-satellite" or "sleeve-blend") || ForwardYears is < 1 or > 30 || Seed is < 0 or > 1000000 ||
            panel.Dates.Length - 1 - HoldoutMonths <= MonthlyPanel.Warmup + TrainMonths + LabelHorizon ||
            DevelopmentStart is { } date && (panel.Dates.IndexOf(date) < MonthlyPanel.Warmup + TrainMonths + LabelHorizon || panel.Dates.IndexOf(date) >= panel.Dates.Length - 1 - HoldoutMonths))
            throw new ResearchException("invalid-experiment-config");
        if (DevelopmentEnd is { } end && (panel.Dates.IndexOf(end) <= (DevelopmentStart is { } start ? panel.Dates.IndexOf(start) : MonthlyPanel.Warmup + TrainMonths + LabelHorizon) ||
            panel.Dates.IndexOf(end) > panel.Dates.Length - 1 - HoldoutMonths)) throw new ResearchException("invalid-experiment-config");
        if (Hypothesis == "sleeve-blend") Backtest.ParseSleeves(SleeveSpec);
        else if (!string.IsNullOrEmpty(SleeveSpec)) throw new ResearchException("invalid-experiment-config");
    }
    public int LabelHorizon => Hypothesis == "upside-tail" ? 6 : 1;
}
public sealed record EquityObservation(DateOnly DecisionDate, DateOnly PeriodEnd, string Strategy, PortfolioStep State);
public sealed record Prediction(DateOnly DecisionDate, DateOnly FeatureCutoff, string Symbol, double Score, decimal TargetWeight);
public sealed record TrainingFold(DateOnly DecisionDate, DateOnly LastLabelEnd, int TrainingMonths, int TrainingRows, RidgeSnapshot Model);
public sealed record StrategySummary(string Strategy, decimal EndingEquityGbp, decimal ContributionsGbp, decimal ProfitGbp,
    decimal TradingCostGbp, decimal TradedNotionalGbp, decimal MaxDrawdown, double AnnualisedTimeWeightedReturn,
    double AnnualisedVolatility, double PositiveMonthFraction);
public sealed record ForwardPoint(int Year, decimal ContributionsGbp, double LowerBalanceGbp, double MedianBalanceGbp, double UpperBalanceGbp);
public sealed record ForwardScenarios(string Method, int Paths, int BlockMonths, int Seed, bool Synthetic, ImmutableArray<ForwardPoint> Points);
public sealed record BacktestReport(string EngineVersion, string DatasetSha256, bool Synthetic, string Source, string FeatureVersion,
    ExperimentConfig Config, ResearchCosts Costs, string Scope, DateOnly EvaluationStart, DateOnly EvaluationEnd, DateOnly ReservedPeriodStart,
    ImmutableArray<StrategySummary> Summaries, ImmutableArray<EquityObservation> Equity, ImmutableArray<Prediction> Predictions,
    ImmutableArray<TrainingFold> Folds, ForwardScenarios Forward, ImmutableArray<string> Limitations, bool ResearchOnly = false, string? ResearchOnlyReason = null);

public static class Backtest
{
    public const string Version = "native-monthly-v3";
    public static readonly ImmutableArray<string> Strategies = ["candidate", "momentum", "equal_weight", "benchmark", "cash"];
    public static Dictionary<string, decimal> Rank(double[] scores, ImmutableArray<string> assets, int topK, decimal cap)
    {
        if (scores.Length != assets.Length || scores.Any(v => !double.IsFinite(v))) throw new ResearchException("ranking-scores");
        var count = Math.Min(topK, assets.Length);
        var weight = Math.Min(EqualFraction(count), cap);
        return Enumerable.Range(0, assets.Length).OrderByDescending(i => scores[i]).ThenBy(i => assets[i], StringComparer.Ordinal).Take(count)
            .ToDictionary(i => assets[i], _ => weight, StringComparer.Ordinal);
    }
    public static readonly ImmutableArray<string> Sleeves = ["benchmark", "equal_weight", "cash", "momentum", "breakout", "core-satellite", "ridge-relative-return"];
    /// <summary>Parse "benchmark:0.7,core-satellite:0.3". Fractions must be positive, named once, and sum to 1.
    /// Upside-tail is excluded: its six-month label horizon needs its own training contract.</summary>
    public static ImmutableArray<(string Sleeve, decimal Fraction)> ParseSleeves(string spec)
    {
        var parsed = ImmutableArray.CreateBuilder<(string, decimal)>();
        foreach (var token in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(':');
            if (parts.Length != 2 || !Sleeves.Contains(parts[0].Trim()) || parsed.Any(p => p.Item1 == parts[0].Trim()) ||
                !decimal.TryParse(parts[1].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var fraction) || fraction is <= 0 or > 1)
                throw new ResearchException("invalid-experiment-config");
            parsed.Add((parts[0].Trim(), fraction));
        }
        if (parsed.Count == 0 || Math.Abs(parsed.Sum(p => p.Item2) - 1m) > 0.000001m) throw new ResearchException("invalid-experiment-config");
        return parsed.ToImmutable();
    }
    /// <summary>Blend declared sleeve weights for one decision. Selection assets are capped at
    /// MaximumWeight; the benchmark sleeve is capped only by its own allocated fraction.</summary>
    public static Dictionary<string, decimal> BlendWeights(ExperimentConfig config, ImmutableArray<string> assets, string benchmark,
        IReadOnlyDictionary<string, double[]> scoresBySleeve)
    {
        var total = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (sleeve, fraction) in ParseSleeves(config.SleeveSpec))
        {
            var sleeveWeights = sleeve switch
            {
                "benchmark" => new Dictionary<string, decimal> { [benchmark] = 1m },
                "cash" => new Dictionary<string, decimal>(),
                "equal_weight" => assets.ToDictionary(s => s, _ => EqualFraction(assets.Length), StringComparer.Ordinal),
                "momentum" or "core-satellite" => CandidateWeights(config with { Hypothesis = sleeve }, Scores(scoresBySleeve, "momentum"), assets),
                "breakout" => CandidateWeights(config with { Hypothesis = sleeve }, Scores(scoresBySleeve, "breakout"), assets),
                "ridge-relative-return" => CandidateWeights(config with { Hypothesis = sleeve }, Scores(scoresBySleeve, "ridge-relative-return"), assets),
                _ => throw new ResearchException("invalid-experiment-config")
            };
            foreach (var (symbol, weight) in sleeveWeights) total[symbol] = total.GetValueOrDefault(symbol) + fraction * weight;
        }
        return total.Where(x => x.Value > 0)
            .ToDictionary(x => x.Key, x => x.Key == benchmark ? x.Value : Math.Min(x.Value, config.MaximumWeight), StringComparer.Ordinal);
        static double[] Scores(IReadOnlyDictionary<string, double[]> all, string key) =>
            all.TryGetValue(key, out var scores) ? scores : throw new ResearchException("sleeve-scores");
    }
    /// <summary>Candidate target weights for one decision. Core-satellite keeps half in an
    /// equal-weight core over every asset and half in the ranked satellite, capped per name.</summary>
    public static Dictionary<string, decimal> CandidateWeights(ExperimentConfig config, double[] scores, ImmutableArray<string> assets)
    {
        var ranked = Rank(scores, assets, config.TopK, config.MaximumWeight);
        if (config.Hypothesis == "breakout")
            return ranked.Where(x => scores[assets.IndexOf(x.Key)] > 0).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        if (config.Hypothesis != "core-satellite") return ranked;
        var core = EqualFraction(assets.Length) / 2;
        return assets.ToDictionary(s => s, s => Math.Min(core + ranked.GetValueOrDefault(s) / 2, config.MaximumWeight), StringComparer.Ordinal);
    }
    public static BacktestReport Run(MonthlyPanel panel, ExperimentConfig config, ResearchCosts costs, CancellationToken ct = default, Action<int>? progress = null)
    {
        config.Validate(panel); costs.Validate();
        var first = config.DevelopmentStart is { } date ? panel.Dates.IndexOf(date) : MonthlyPanel.Warmup + config.TrainMonths + config.LabelHorizon;
        var reserved = panel.Dates.Length - 1 - config.HoldoutMonths;
        var start = config.EvaluateHoldout ? reserved : first;
        var stop = config.EvaluateHoldout ? panel.Dates.Length - 1 : config.DevelopmentEnd is { } end ? panel.Dates.IndexOf(end) : reserved;
        var portfolios = Strategies.ToDictionary(s => s, _ => new NotionalPortfolio());
        var equity = ImmutableArray.CreateBuilder<EquityObservation>();
        var predictions = ImmutableArray.CreateBuilder<Prediction>();
        var folds = ImmutableArray.CreateBuilder<TrainingFold>();
        RidgeModel? model = null;
        for (var t = start; t < stop; t++)
        {
            ct.ThrowIfCancellationRequested();
            if ((t - start) % config.RefitMonths == 0)
            {
                var training = Enumerable.Range(MonthlyPanel.Warmup, t - config.LabelHorizon - MonthlyPanel.Warmup).ToArray();
                var x = new List<double[]>(); var y = new List<double>();
                foreach (var i in training) { ct.ThrowIfCancellationRequested(); x.AddRange(panel.Features(i)); y.AddRange(config.Hypothesis == "upside-tail" ? panel.UpsideLabels(i) : panel.Labels(i)); }
                if (config.Hypothesis == "upside-tail" && (y.Count(v => v == 1) < 5 || y.Count(v => v == 0) < 5))
                    throw new ResearchException("tail-insufficient-examples");
                model = RidgeModel.Fit(x.ToArray(), y.ToArray(), config.RidgeAlpha);
                folds.Add(new(panel.Dates[t], panel.Dates[training[^1] + config.LabelHorizon], training.Length, x.Count, model.Snapshot));
            }
            var features = panel.Features(t);
            Dictionary<string, decimal> candidate; double[] predictionScores;
            if (config.Hypothesis == "sleeve-blend")
            {
                var momentumScores = features.Select(x => x[2]).ToArray();
                candidate = BlendWeights(config, panel.Assets, panel.Benchmark, new Dictionary<string, double[]>
                {
                    ["momentum"] = momentumScores, ["breakout"] = panel.BreakoutScores(t), ["ridge-relative-return"] = model!.Predict(features)
                });
                predictionScores = panel.Assets.Select(a => (double)candidate.GetValueOrDefault(a)).ToArray();
            }
            else
            {
                predictionScores = config.Hypothesis switch
                {
                    "momentum" or "core-satellite" => features.Select(x => x[2]).ToArray(),
                    "breakout" => panel.BreakoutScores(t),
                    _ => model!.Predict(features)
                };
                candidate = CandidateWeights(config, predictionScores, panel.Assets);
            }
            var allocations = new Dictionary<string, Dictionary<string, decimal>>
            {
                ["candidate"] = candidate,
                ["momentum"] = Rank(features.Select(x => x[2]).ToArray(), panel.Assets, config.TopK, config.MaximumWeight),
                ["equal_weight"] = panel.Assets.ToDictionary(s => s, _ => EqualFraction(panel.Assets.Length)),
                ["benchmark"] = new() { [panel.Benchmark] = 1m }, ["cash"] = new()
            };
            for (var a = 0; a < panel.Assets.Length; a++) predictions.Add(new(panel.Dates[t], panel.Dates[t - 1], panel.Assets[a], predictionScores[a], candidate.GetValueOrDefault(panel.Assets[a])));
            var returns = panel.Returns(t);
            foreach (var strategy in Strategies)
            {
                var slots = strategy == "candidate" && config.Hypothesis == "sleeve-blend" ? Math.Min(100, panel.Assets.Length + 1)
                    : strategy == "candidate" && config.Hypothesis == "core-satellite" || strategy == "equal_weight" ? panel.Assets.Length
                    : strategy is "candidate" or "momentum" ? config.TopK : 1;
                // Blend weights are already capped per selection asset; the step cap must not strangle the benchmark sleeve.
                var cap = strategy == "candidate" && config.Hypothesis == "sleeve-blend" ? 1m
                    : strategy is "candidate" or "momentum" ? config.MaximumWeight : strategy == "equal_weight" ? EqualFraction(panel.Assets.Length) : 1m;
                equity.Add(new(panel.Dates[t], panel.Dates[t + 1], strategy, portfolios[strategy].Step(config.MonthlyGbp, allocations[strategy], returns, costs,
                    config.AllocationPolicy == "contribution-only", slots, cap)));
            }
            progress?.Invoke((t - start + 1) * 100 / (stop - start));
        }
        var summaries = Strategies.Select(s =>
        {
            var p = portfolios[s];
            var observations = equity.Where(r => r.Strategy == s).ToArray();
            var returns = observations.Select(r => (double)r.State.PeriodReturn).ToArray();
            var mean = returns.Average();
            var vol = returns.Length > 1 ? Math.Sqrt(returns.Sum(r => Math.Pow(r - mean, 2)) / (returns.Length - 1) * 12) : 0;
            return new StrategySummary(s, p.Equity, p.Contributions, p.Equity - p.Contributions, p.CostsPaid,
                observations.Sum(r => r.State.TradedNotionalGbp), p.MaxDrawdown, Math.Pow((double)p.UnitPrice, 12.0 / returns.Length) - 1, vol,
                returns.Count(r => r > 0) / (double)returns.Length);
        }).ToImmutableArray();
        var forward = ForwardProjection.Build(equity.Where(x => x.Strategy == "candidate").Select(x => (double)x.State.PeriodReturn).ToArray(), config, panel.Synthetic, ct);
        return new(Version, panel.Sha256, panel.Synthetic, panel.Source, MonthlyPanel.FeatureVersion, config, costs,
            config.EvaluateHoldout ? "reserved_holdout" : "development_walk_forward", panel.Dates[start], panel.Dates[stop], panel.Dates[reserved],
            summaries, equity.ToImmutable(), predictions.ToImmutable(), folds.ToImmutable(), forward,
            ["Fixed-universe GBP total-return indices; missing delistings/eligibility can bias results. No point-in-time provider coverage is certified.",
             "Fractional GBP notionals, flat per-side costs and cash at zero interest approximate execution; these are not broker quantities/fills.",
             "Concentration and hypothesis settings are research choices, not optimised or approved for real investing.",
             config.AllocationPolicy == "contribution-only" ? "Contribution-only policy retains holdings, respects occupied slots and cannot sell to follow rotating ranks; unused cash remains uninvested." : "Full-rebalance research sells holdings to follow new targets; this report cannot approve the initial buy-only broker coordinator.",
             "Conditional bootstrap scenarios assume the sampled return regime repeats; bands are not calibrated investment probabilities.",
             panel.ResearchOnly ? panel.ResearchOnlyReason! : "Synthetic inputs demonstrate software only; no alpha, millionaire probability or live-trading approval is established."], panel.ResearchOnly, panel.ResearchOnlyReason);
    }
    private static decimal EqualFraction(int count) => decimal.Truncate(1m / count * 1000000000000000000000000m) / 1000000000000000000000000m;
}
public static class ForwardProjection
{
    public static ForwardScenarios Build(double[] returns, ExperimentConfig config, bool synthetic, CancellationToken ct)
    {
        const int paths = 300, block = 6;
        var random = new Random(config.Seed);
        var balances = Enumerable.Range(0, config.ForwardYears + 1).Select(_ => new double[paths]).ToArray();
        for (var p = 0; p < paths; p++)
        {
            ct.ThrowIfCancellationRequested(); var balance = 0d; var start = 0;
            for (var month = 0; month < config.ForwardYears * 12; month++)
            {
                if (month % block == 0) start = random.Next(returns.Length);
                var rate = returns[(start + month % block) % returns.Length];
                balance = (balance + (double)config.MonthlyGbp) * (1 + rate);
                if (!double.IsFinite(balance) || balance > 1e24) throw new ResearchException("forward-numeric-range");
                if ((month + 1) % 12 == 0) balances[(month + 1) / 12][p] = balance;
            }
        }
        var points = Enumerable.Range(0, config.ForwardYears + 1).Select(year =>
        {
            Array.Sort(balances[year]);
            return new ForwardPoint(year, year * 12 * config.MonthlyGbp, balances[year][29], balances[year][149], balances[year][269]);
        }).ToImmutableArray();
        return new($"Circular 6-month block resampling of candidate net {(config.EvaluateHoldout ? "reserved-evaluation" : "development")} returns; nominal GBP; 10th/50th/90th path quantiles", paths, block, config.Seed, synthetic, points);
    }
}
