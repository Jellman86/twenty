using System.Collections.Immutable;

namespace Twenty.Core.Research;

public sealed record StudyCandidate(string Id, ExperimentConfig Config);
public sealed record StudyPlan(string Version, string DatasetSha256, bool Synthetic, string Purpose, string CodeSha256,
    string AccountInitialisation, string SelectionRule, DateOnly DevelopmentStart, DateOnly DevelopmentEnd, DateOnly ReservedStart, DateOnly ReservedEnd,
    ResearchCosts BaseCosts, ResearchCosts StressCosts, ImmutableArray<StudyCandidate> Candidates);
public sealed record StudyCandidateScore(string CandidateId, string BaseRunId, string StressRunId, decimal BaseEndingGbp,
    decimal StressEndingGbp, decimal BaseComparatorGbp, decimal StressComparatorGbp, decimal WorstMarginGbp);

/// <summary>A fixed first study, not an optimised family or a universal investment approval threshold.</summary>
public static class StudyProtocol
{
    public const string Version = "contribution-study-v1";
    public static readonly ImmutableArray<string> Hypotheses = ["ridge-relative-return", "momentum", "breakout", "upside-tail"];
    public static StudyPlan Declare(MonthlyPanel panel, decimal monthly, ResearchCosts costs, string purpose, string codeSha)
    {
        if (purpose != (panel.Synthetic ? "engineering" : "research")) throw new ResearchException("study-purpose-provenance");
        costs.Validate();
        var stress = costs with { FxBps = costs.FxBps * 2, HalfSpreadBps = costs.HalfSpreadBps * 2, SlippageBps = costs.SlippageBps * 2, BuyTaxBps = costs.BuyTaxBps * 2 };
        stress.Validate();
        const int train = 60, holdout = 24;
        var start = MonthlyPanel.Warmup + train + 6;
        var developmentEnd = panel.Dates.Length - 2 - holdout;
        if (developmentEnd - start < 24) throw new ResearchException("study-development-window-too-short");
        var candidates = (from h in Hypotheses from n in new[] { 1, 2, 4, 8 }.Where(n => n <= panel.Assets.Length)
            select new StudyCandidate(h + ":" + n, new(MonthlyGbp: monthly, TopK: n, MaximumWeight: 1m / n,
                TrainMonths: train, RefitMonths: 12, HoldoutMonths: holdout, RidgeAlpha: 10, Hypothesis: h,
                AllocationPolicy: "contribution-only", DevelopmentStart: panel.Dates[start], DevelopmentEnd: panel.Dates[developmentEnd]))).ToImmutableArray();
        foreach (var candidate in candidates) candidate.Config.Validate(panel);
        return new(Version, panel.Sha256, panel.Synthetic, purpose, codeSha, "Empty portfolio at each development/final window; expanding training; refit at window start and every 12 months; one monthly selection/execution gap before the final window.",
            "Maximise the minimum base/stress ending-wealth margin over the greatest of benchmark, all-asset equal targets and cash; ordinal candidate ID breaks ties. Positive real-data margin nominates only for final research.",
            panel.Dates[start], panel.Dates[developmentEnd], panel.Dates[panel.Dates.Length - 1 - holdout], panel.Dates[^1], costs, stress, candidates);
    }
    public static StudyCandidateScore Score(string candidateId, string baseRun, string stressRun, BacktestReport baseline, BacktestReport stress)
    {
        if (baseline.EvaluationStart != stress.EvaluationStart || baseline.EvaluationEnd != stress.EvaluationEnd || baseline.Config != stress.Config || baseline.Synthetic != stress.Synthetic ||
            baseline.Summaries.Length != 5 || stress.Summaries.Length != 5) throw new ResearchException("study-paired-report-contract");
        var contributions = baseline.Summaries.Select(s => s.ContributionsGbp).Concat(stress.Summaries.Select(s => s.ContributionsGbp)).Distinct().ToArray();
        if (contributions.Length != 1) throw new ResearchException("study-contribution-mismatch");
        static decimal Equity(BacktestReport r, string name) => r.Summaries.Single(s => s.Strategy == name).EndingEquityGbp;
        static decimal Reference(BacktestReport r) => new[] { "benchmark", "equal_weight", "cash" }.Max(s => Equity(r, s));
        var a = Equity(baseline, "candidate"); var b = Equity(stress, "candidate"); var ar = Reference(baseline); var br = Reference(stress);
        return new(candidateId, baseRun, stressRun, a, b, ar, br, Math.Min(a - ar, b - br));
    }
    public static StudyCandidateScore Choose(IEnumerable<StudyCandidateScore> scores) => scores.OrderByDescending(s => s.WorstMarginGbp)
        .ThenBy(s => s.CandidateId, StringComparer.Ordinal).First();
}
