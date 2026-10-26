using System.Collections.Immutable;

namespace Twenty.Core.Research;

public sealed record DiagnosticReturns(string RunId, string ReportSha256, ImmutableArray<DateOnly> PeriodEnds,
    ImmutableArray<double> Candidate, ImmutableDictionary<string, ImmutableArray<double>> Comparators);
public sealed record ActiveReturnInterval(string RunId, string Comparator, double AnnualisedMeanActiveReturn,
    double PointwiseLower, double PointwiseUpper, double SimultaneousLower, double SimultaneousUpper);
public sealed record BlockSensitivity(int ExpectedBlockMonths, double ExpectedBlocksPerPath, double SimultaneousRadius,
    ImmutableArray<ActiveReturnInterval> Intervals);
public sealed record SearchDiagnosticReport(string MethodVersion, string Statistic, int Replicates, int Seed,
    DateOnly FirstPeriodEnd, DateOnly LastPeriodEnd, int Months, int Candidates, int Comparisons,
    ImmutableArray<BlockSensitivity> Sensitivities, ImmutableArray<string> Limitations, SelectionDiagnostic? Selection = null);

/// <summary>Paired, joint stationary resampling of already recorded development returns plus scoped
/// CSCV/deflated-Sharpe selection estimates. Never fits a model or reads reserved outcomes.</summary>
public static class SearchDiagnostics
{
    public const string Version = "stationary-active-mean-v2";
    public const int Replicates = 2000, Seed = 212, MaximumCandidates = 64;
    public static readonly ImmutableArray<string> Comparators = ["benchmark", "momentum", "equal_weight", "cash"];
    public static readonly ImmutableArray<int> Blocks = [3, 6, 12];

    public static SearchDiagnosticReport Build(IReadOnlyList<DiagnosticReturns> candidates, CancellationToken ct = default, int registeredTrials = 0)
        => BuildCore(candidates, Comparators, Version, ct, registeredTrials);

    public const string UniverseVersion = "stationary-universe-active-mean-v1";
    public static readonly ImmutableArray<string> UniverseComparators = ["equal_weight", "cash"];
    public static SearchDiagnosticReport BuildUniverse(IReadOnlyList<DiagnosticReturns> candidates, CancellationToken ct = default, int registeredTrials = 0)
    {
        var result = BuildCore(candidates, UniverseComparators, UniverseVersion, ct, registeredTrials);
        return result with { Limitations = result.Limitations.Add(
            "Changing-universe candidates are recorded momentum baselines versus their own equal-weight and zero-interest cash paths. No external benchmark is supplied. Local-currency returns have no FX conversion; GBP notionals do not establish achievable GBP wealth.") };
    }

    public const string RawVersion = "stationary-raw-active-mean-v1";
    public static SearchDiagnosticReport BuildRaw(IReadOnlyList<DiagnosticReturns> candidates, CancellationToken ct = default, int registeredTrials = 0)
    {
        var result = BuildCore(candidates, UniverseComparators, RawVersion, ct, registeredTrials);
        return result with { Limitations = result.Limitations.AddRange(new[] {
            "Recorded raw momentum candidates use their own equal-weight and zero-interest cash comparators. Monthly returns use expected equity in GBP, including separately declared pending-claim estimates; successful stage ends are settled. No external market benchmark is supplied.",
            "This diagnostic covers one upfront registered candidate family and one cost scenario. Other studies, opposite costs and all final trials remain disclosed but excluded; this is not whole-programme search correction or investment promotion.",
            "Raw FX, order funding, estimated deductions, successor availability and modelled execution retain their original report assumptions. Integrity and paired resampling do not establish genuine provenance, settlement certainty or achievable returns." }) };
    }

    private static SearchDiagnosticReport BuildCore(IReadOnlyList<DiagnosticReturns> candidates, ImmutableArray<string> comparators,
        string version, CancellationToken ct, int registeredTrials)
    {
        if (candidates.Count is < 1 or > MaximumCandidates || candidates.Select(c => c.RunId).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            throw new ResearchException("diagnostic-candidate-count");
        foreach (var c in candidates)
        {
            if (c.PeriodEnds.Length is < 24 or > 600 || c.Candidate.Length != c.PeriodEnds.Length ||
                c.Comparators.Count != comparators.Length || comparators.Any(s => !c.Comparators.ContainsKey(s)) ||
                c.Comparators.Values.Any(v => v.Length != c.PeriodEnds.Length) ||
                c.Candidate.Concat(c.Comparators.Values.SelectMany(v => v)).Any(v => !double.IsFinite(v) || v < -1 || v > 1e12))
                throw new ResearchException("diagnostic-return-stream");
            for (var i = 1; i < c.PeriodEnds.Length; i++)
                if (c.PeriodEnds[i].Year * 12 + c.PeriodEnds[i].Month - c.PeriodEnds[i - 1].Year * 12 - c.PeriodEnds[i - 1].Month != 1)
                    throw new ResearchException("diagnostic-month-grid");
        }
        var start = candidates.Max(c => c.PeriodEnds[0]); var end = candidates.Min(c => c.PeriodEnds[^1]);
        var dates = candidates[0].PeriodEnds.Where(d => d >= start && d <= end).ToArray();
        if (dates.Length < 24) throw new ResearchException("diagnostic-common-window-too-short");
        var columns = new List<(string Run, string Comparator, double[] Values)>();
        var aligned = new List<(string RunId, double[] Returns)>();
        foreach (var c in candidates.OrderBy(c => c.RunId, StringComparer.Ordinal))
        {
            var offset = c.PeriodEnds.IndexOf(dates[0]);
            if (offset < 0 || !c.PeriodEnds.Skip(offset).Take(dates.Length).SequenceEqual(dates)) throw new ResearchException("diagnostic-month-grid");
            aligned.Add((c.RunId, Enumerable.Range(offset, dates.Length).Select(i => c.Candidate[i]).ToArray()));
            foreach (var name in comparators)
                columns.Add((c.RunId, name, Enumerable.Range(offset, dates.Length).Select(i => c.Candidate[i] - c.Comparators[name][i]).ToArray()));
        }
        var means = columns.Select(c => c.Values.Average()).ToArray();
        var sensitivities = ImmutableArray.CreateBuilder<BlockSensitivity>();
        foreach (var block in Blocks)
        {
            // The same indices apply to every strategy and comparator. Independent per-stock/path resampling would destroy their dependence.
            var random = new Random(Seed);
            var samples = columns.Select(_ => new double[Replicates]).ToArray();
            var maxima = new double[Replicates]; var counts = new int[dates.Length];
            for (var b = 0; b < Replicates; b++)
            {
                ct.ThrowIfCancellationRequested(); Array.Clear(counts);
                var index = random.Next(dates.Length); counts[index]++;
                for (var t = 1; t < dates.Length; t++)
                {
                    index = random.NextDouble() < 1d / block ? random.Next(dates.Length) : (index + 1) % dates.Length;
                    counts[index]++;
                }
                for (var k = 0; k < columns.Count; k++)
                {
                    var sum = 0d;
                    for (var t = 0; t < dates.Length; t++) sum += counts[t] * columns[k].Values[t];
                    var mean = sum / dates.Length;
                    samples[k][b] = 12 * mean;
                    maxima[b] = Math.Max(maxima[b], 12 * Math.Abs(mean - means[k]));
                }
            }
            var radius = Quantile(maxima, .95);
            var rows = columns.Select((c, k) => new ActiveReturnInterval(c.Run, c.Comparator, means[k] * 12,
                Quantile(samples[k], .025), Quantile(samples[k], .975), means[k] * 12 - radius, means[k] * 12 + radius)).ToImmutableArray();
            sensitivities.Add(new(block, 1 + (dates.Length - 1d) / block, radius, rows));
        }
        var selection = SelectionDiagnostics.Build(aligned, registeredTrials > 0 ? registeredTrials : candidates.Count, ct);
        return new(version, "12 × arithmetic mean of paired net monthly return differences; percentage points, not compound returns or Sharpe",
            Replicates, Seed, dates[0], dates[^1], dates.Length, candidates.Count, columns.Count, sensitivities.ToImmutable(),
            ["Stationary-bootstrap approximation for a fixed recorded candidate family under weak dependence/stationarity; regime changes and limited history can invalidate coverage.",
             "Pointwise percentile ranges do not correct selection. Simultaneous ranges use the 95th percentile of the maximum absolute centred mean deviation over every included candidate/comparator.",
             "Expected block lengths 3, 6 and 12 months are fixed sensitivities, not optimised choices; short histories contain few expected blocks. Monte Carlo quantiles are approximate.",
             "The common calendar window can discard earlier observations. Recorded account paths keep their original portfolio history; cash exposure and earlier contributions can differ.",
             "Resampling existing returns holds fitted models and the research universe fixed; it does not measure refit, data, delisting, execution or full research-programme uncertainty.",
             "These are retrospective diagnostics, not a preregistered study, a future-wealth probability or approval to trade. The selection section adds scoped CSCV-PBO and deflated-Sharpe estimates under its own stated assumptions; neither corrects searches outside this recorded family."], selection);
    }
    private static double Quantile(double[] values, double probability)
    {
        Array.Sort(values);
        var position = probability * (values.Length - 1); var lower = (int)position;
        return values[lower] + (values[Math.Min(lower + 1, values.Length - 1)] - values[lower]) * (position - lower);
    }
}
