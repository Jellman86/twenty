using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

public class SelectionDiagnosticsTests
{
    // Deterministic authored pseudo-noise; not market data. Range about +/-4% monthly.
    private static double Noise(int i, int seed)
    {
        var v = Math.Sin(i * 12.9898 + seed * 78.233) * 43758.5453;
        return (v - Math.Floor(v) - .5) * .08;
    }
    private static (string, double[]) Candidate(string id, Func<int, double> value, int months = 96) =>
        (id, Enumerable.Range(0, months).Select(value).ToArray());
    private static DiagnosticReturns Stream(string id, double[] values) =>
        new(id, new string('a', 64), Enumerable.Range(0, values.Length).Select(i => new DateOnly(2000, 1, 1).AddMonths(i)).ToImmutableArray(),
            values.ToImmutableArray(), SearchDiagnostics.Comparators.ToImmutableDictionary(s => s, _ => new double[values.Length].ToImmutableArray()));

    [Fact]
    public void NormalMathMatchesIndependentReferenceValues()
    {
        // Reference values from Python's statistics.NormalDist, an independent implementation.
        Assert.Equal(.5, NormalDistribution.Cdf(0), 12);
        Assert.Equal(.975, NormalDistribution.Cdf(1.9599639845400536), 9);
        Assert.Equal(1.9599639845400536, NormalDistribution.InverseCdf(.975), 9);
        Assert.Equal(1.2815515655446008, NormalDistribution.InverseCdf(.9), 9);
        Assert.Equal(2.3263478740408408, NormalDistribution.InverseCdf(.99), 9);
        Assert.Equal(0, NormalDistribution.InverseCdf(.5), 12);
        foreach (var p in new[] { .01, .1, .9, .99, .999 })
            Assert.InRange(Math.Abs(NormalDistribution.Cdf(NormalDistribution.InverseCdf(p)) - p), 0, 1e-9);
        Assert.Equal(-NormalDistribution.InverseCdf(.9), NormalDistribution.InverseCdf(.1), 9);
    }

    [Fact]
    public void ExpectedMaximumSharpeMatchesThePublishedExtremeValueFormula()
    {
        // Bailey & Lopez de Prado (2014), eq. (2)/Snippet 1, evaluated independently in Python.
        Assert.Equal(0, SelectionDiagnostics.ExpectedMaximumSharpe(1, 1));
        // The asymptotic approximation is negative near one trial; the floored threshold must never inflate.
        Assert.Equal(0, SelectionDiagnostics.ExpectedMaximumSharpe(1, 1.25));
        Assert.Equal(.5197553442792137, SelectionDiagnostics.ExpectedMaximumSharpe(1, 2), 9);
        Assert.Equal(1.5745983013449716, SelectionDiagnostics.ExpectedMaximumSharpe(1, 10), 9);
        Assert.Equal(2.530602893201142, SelectionDiagnostics.ExpectedMaximumSharpe(1, 100), 9);
        Assert.Equal(2 * 1.5745983013449716, SelectionDiagnostics.ExpectedMaximumSharpe(2, 10), 9);
        Assert.True(SelectionDiagnostics.ExpectedMaximumSharpe(1, 1000) > SelectionDiagnostics.ExpectedMaximumSharpe(1, 100));
    }

    [Fact]
    public void ProbabilisticSharpeMatchesIndependentReferenceValues()
    {
        Assert.Equal(.9413200020162517, SelectionDiagnostics.ProbabilisticSharpe(.15, 0, 120, -.5, 4), 9);
        Assert.Equal(.8517498094833261, SelectionDiagnostics.ProbabilisticSharpe(.15, .05, 120, -.5, 4), 9);
        Assert.True(SelectionDiagnostics.ProbabilisticSharpe(.15, 0, 240, -.5, 4) >
            SelectionDiagnostics.ProbabilisticSharpe(.15, 0, 60, -.5, 4));
    }

    [Fact]
    public void OnePersistentCandidateAmongNoiseHasLowOverfittingProbability()
    {
        var family = Enumerable.Range(1, 8).Select(s => Candidate($"noise{s}", i => Noise(i, s))).ToList();
        family.Add(Candidate("persistent", i => .006 + Noise(i, 99) * .25));
        var result = SelectionDiagnostics.Build(family, family.Count);
        Assert.NotNull(result.Pbo); Assert.NotNull(result.DeflatedSharpe);
        Assert.Equal(16, result.Pbo!.Blocks); Assert.Equal(12870, result.Pbo.Combinations);
        Assert.InRange(result.Pbo.ProbabilityOfBacktestOverfitting, 0, .05);
        Assert.Equal(0, result.Pbo.FractionWinnersNegativeOutOfSample);
        Assert.True(result.Pbo.MedianOutOfSampleWinnerMonthlySharpe > 0);
        Assert.Equal("persistent", result.DeflatedSharpe!.WinnerRunId);
        Assert.True(result.DeflatedSharpe.ProbabilisticSharpeVsZero > .99);
    }

    [Fact]
    public void PureNoiseFamilyHasOverfittingProbabilityNearOneHalf()
    {
        var family = Enumerable.Range(1, 10).Select(s => Candidate($"noise{s}", i => Noise(i, s * 3))).ToList();
        var result = SelectionDiagnostics.Build(family, family.Count);
        Assert.NotNull(result.Pbo);
        Assert.InRange(result.Pbo!.ProbabilityOfBacktestOverfitting, .25, .75);
        Assert.InRange(result.Pbo.MedianLogit, -1, 1);
    }

    [Fact]
    public void DeflationIncreasesWithTrialCountAndCorrelationAdjustmentSitsBetween()
    {
        // Strongly correlated family: one persistent stream plus rescaled copies of shared noise.
        var family = new List<(string, double[])> { Candidate("persistent", i => .006 + Noise(i, 99) * .25) };
        family.AddRange(Enumerable.Range(1, 6).Select(s => Candidate($"clone{s}", i => .002 + Noise(i, 99) * .25 + Noise(i, s) * .02)));
        var result = SelectionDiagnostics.Build(family, 40);
        var dsr = result.DeflatedSharpe!;
        Assert.Equal(3, dsr.Sensitivities.Length);
        var byBasis = dsr.Sensitivities.ToDictionary(s => s.Basis);
        Assert.Equal(family.Count, byBasis["family-as-independent"].Trials);
        Assert.Equal(40, byBasis["registered-snapshot-as-independent"].Trials);
        var adjusted = byBasis["family-correlation-adjusted"];
        Assert.InRange(adjusted.Trials, 1, family.Count);
        Assert.True(adjusted.ExpectedMaximumMonthlySharpe <= byBasis["family-as-independent"].ExpectedMaximumMonthlySharpe + 1e-12);
        Assert.True(adjusted.DeflatedSharpeRatio >= byBasis["family-as-independent"].DeflatedSharpeRatio - 1e-12);
        Assert.True(byBasis["registered-snapshot-as-independent"].DeflatedSharpeRatio <= byBasis["family-as-independent"].DeflatedSharpeRatio + 1e-12);
        Assert.All(dsr.Sensitivities, s => Assert.True(s.DeflatedSharpeRatio <= dsr.ProbabilisticSharpeVsZero + 1e-12));
        Assert.NotNull(dsr.AverageCorrelation); Assert.InRange(dsr.AverageCorrelation!.Value, 0, 1);
        Assert.Equal(dsr.MonthlySharpe * Math.Sqrt(12), dsr.AnnualisedSharpe, 12);
    }

    [Fact]
    public void MomentsCaptureNegativeSkewAndFatTails()
    {
        var crashy = Candidate("crashy", i => i % 10 == 9 ? -.05 : .02);
        var steady = Candidate("steady", i => .001 + Noise(i, 7) * .15);
        var result = SelectionDiagnostics.Build([crashy, steady], 2);
        Assert.NotNull(result.DeflatedSharpe);
        Assert.Equal("crashy", result.DeflatedSharpe!.WinnerRunId);
        Assert.True(result.DeflatedSharpe.Skewness < -1);
        Assert.True(result.DeflatedSharpe.Kurtosis > 3);
    }

    [Fact]
    public void UndefinedCorrelationIsNullNotMeasuredZero()
    {
        var family = new List<(string, double[])>
        {
            Candidate("varying", i => .004 + Noise(i, 5) * .2),
            Candidate("cash", _ => 0d)
        };
        var dsr = SelectionDiagnostics.Build(family, family.Count).DeflatedSharpe!;
        Assert.Null(dsr.AverageCorrelation);
        Assert.DoesNotContain(dsr.Sensitivities, s => s.Basis == "family-correlation-adjusted");
    }

    [Fact]
    public void SelectionIsUnavailableWithReasonsForSingleOrShortFamilies()
    {
        var single = SelectionDiagnostics.Build([Candidate("only", i => Noise(i, 1))], 1);
        Assert.Null(single.Pbo); Assert.Null(single.DeflatedSharpe);
        Assert.Contains(single.Limitations, l => l.Contains("two candidates"));
        var shortFamily = SelectionDiagnostics.Build(
            [Candidate("a", i => Noise(i, 1), 36), Candidate("b", i => Noise(i, 2), 36)], 2);
        Assert.Null(shortFamily.Pbo); Assert.Null(shortFamily.DeflatedSharpe);
        Assert.Contains(shortFamily.Limitations, l => l.Contains("48"));
    }

    [Fact]
    public void SearchReportCarriesSelectionAndStaysOrderInvariant()
    {
        var a = Stream("a", Enumerable.Range(0, 96).Select(i => .005 + Noise(i, 5) * .3).ToArray());
        var b = Stream("b", Enumerable.Range(0, 96).Select(i => Noise(i, 6)).ToArray());
        var forward = SearchDiagnostics.Build([a, b]);
        var reversed = SearchDiagnostics.Build([b, a]);
        Assert.Equal("stationary-active-mean-v2", forward.MethodVersion);
        Assert.NotNull(forward.Selection); Assert.NotNull(forward.Selection!.Pbo); Assert.NotNull(forward.Selection.DeflatedSharpe);
        Assert.Equal(JsonSerializer.Serialize(forward), JsonSerializer.Serialize(reversed));
        var registered = SearchDiagnostics.Build([a, b], default, 25);
        Assert.Contains(registered.Selection!.DeflatedSharpe!.Sensitivities, s => s.Trials == 25);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SearchDiagnostics.Build([a, b], cancel.Token));
    }

    [Fact]
    public void ReportWithoutSelectionFieldStillLoads()
    {
        var a = Stream("a", Enumerable.Range(0, 96).Select(i => .005 + Noise(i, 5) * .3).ToArray());
        var b = Stream("b", Enumerable.Range(0, 96).Select(i => Noise(i, 6)).ToArray());
        var report = SearchDiagnostics.Build([a, b]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ResearchStore.Json);
        var loaded = JsonSerializer.Deserialize<SearchDiagnosticReport>(bytes, ResearchStore.Json)!;
        Assert.Equal(report.Selection!.Pbo!.ProbabilityOfBacktestOverfitting, loaded.Selection!.Pbo!.ProbabilityOfBacktestOverfitting);
        var node = JsonNode.Parse(bytes)!;
        node.AsObject().Remove("selection");
        var legacy = JsonSerializer.Deserialize<SearchDiagnosticReport>(node.ToJsonString(), ResearchStore.Json)!;
        Assert.Null(legacy.Selection); Assert.Equal(report.Months, legacy.Months);
    }
}
