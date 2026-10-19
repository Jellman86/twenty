using System.Collections.Immutable;
using System.Numerics;

namespace Twenty.Core.Research;

/// <summary>Standard-normal CDF and inverse. The CDF is 0.5*erfc(-z/sqrt(2)) with erfc evaluated
/// through the regularised incomplete gamma function at a=1/2 (power series below x=1.5, Lentz
/// continued fraction above it). The inverse uses Acklam's rational
/// approximation polished by one Halley step against that CDF. Both are deterministic; tests pin
/// them to Python's independent statistics.NormalDist values within 1e-9.</summary>
public static class NormalDistribution
{
    public static double Cdf(double z)
    {
        if (double.IsNaN(z)) throw new ResearchException("normal-input");
        if (z < -10) return 0;
        if (z > 10) return 1;
        var x = z * z / 2;
        var gamma = x < 1.5 ? GammaSeries(x) : 1 - GammaFraction(x);
        return z >= 0 ? .5 + .5 * gamma : .5 - .5 * gamma;
    }
    // log Gamma(1/2) = log(sqrt(pi)); a is only ever 1/2 in this class.
    private static readonly double LogGammaHalf = .5 * Math.Log(Math.PI);
    private static double GammaSeries(double x)
    {
        if (x == 0) return 0;
        var ap = .5; var term = 2d; var sum = term;
        for (var i = 0; i < 500; i++) { term *= x / ++ap; sum += term; if (Math.Abs(term) < Math.Abs(sum) * 1e-15) break; }
        return sum * Math.Exp(-x + .5 * Math.Log(x) - LogGammaHalf);
    }
    private static double GammaFraction(double x)
    {
        var b = x + .5; var c = 1e300; var d = 1 / b; var h = d;
        for (var i = 1; i <= 500; i++)
        {
            var an = -i * (i - .5);
            b += 2; d = an * d + b; if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = b + an / c; if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d; var delta = d * c; h *= delta;
            if (Math.Abs(delta - 1) < 1e-15) break;
        }
        return Math.Exp(-x + .5 * Math.Log(x) - LogGammaHalf) * h;
    }
    public static double InverseCdf(double p)
    {
        if (!double.IsFinite(p) || p is <= 0 or >= 1) throw new ResearchException("normal-input");
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        double z;
        if (p < .02425) { var q = Math.Sqrt(-2 * Math.Log(p)); z = (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
        else if (p <= .97575) { var q = p - .5; var r = q * q; z = (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1); }
        else { var q = Math.Sqrt(-2 * Math.Log(1 - p)); z = -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
        var error = Cdf(z) - p;
        var density = Math.Exp(-z * z / 2) / Math.Sqrt(2 * Math.PI);
        return z - error / density * 1 / (1 + z * error / (2 * density));
    }
}

public sealed record PboDiagnostic(int Blocks, int Combinations, double ProbabilityOfBacktestOverfitting,
    double MedianLogit, double MedianInSampleWinnerMonthlySharpe, double MedianOutOfSampleWinnerMonthlySharpe,
    double FractionWinnersNegativeOutOfSample);
public sealed record SharpeTrialSensitivity(string Basis, double Trials, double ExpectedMaximumMonthlySharpe, double DeflatedSharpeRatio);
public sealed record DeflatedSharpeDiagnostic(string WinnerRunId, double MonthlySharpe, double AnnualisedSharpe,
    double Skewness, double Kurtosis, double TrialSharpeStandardDeviation, double? AverageCorrelation,
    double ProbabilisticSharpeVsZero, ImmutableArray<SharpeTrialSensitivity> Sensitivities);
public sealed record SelectionDiagnostic(string Statistic, PboDiagnostic? Pbo, DeflatedSharpeDiagnostic? DeflatedSharpe,
    ImmutableArray<string> Limitations);

/// <summary>Scoped selection-bias estimates over already recorded, calendar-aligned candidate net returns:
/// CSCV probability of backtest overfitting (Bailey, Borwein, Lopez de Prado and Zhu) and the deflated
/// Sharpe ratio of the family's full-window Sharpe winner (Bailey and Lopez de Prado). Sharpe here is
/// mean/sample-sd of monthly net period returns against Twenty's zero-interest cash. Deterministic; no
/// resampling, refit or reserved-data read. Trial counts are declared sensitivities, never discovered truth.</summary>
public static class SelectionDiagnostics
{
    public const int Blocks = 16, MinimumMonths = 48, MinimumCandidates = 2;
    private const double EulerMascheroni = 0.5772156649, DegenerateSharpe = 1e15;

    public static double ExpectedMaximumSharpe(double trialSharpeSd, double trials)
    {
        if (!double.IsFinite(trialSharpeSd) || trialSharpeSd < 0 || !double.IsFinite(trials) || trials < 1)
            throw new ResearchException("selection-trials");
        if (trials <= 1 || trialSharpeSd == 0) return 0;
        var maxZ = (1 - EulerMascheroni) * NormalDistribution.InverseCdf(1 - 1 / trials)
            + EulerMascheroni * NormalDistribution.InverseCdf(1 - 1 / (trials * Math.E));
        // The extreme-value approximation undershoots below zero for tiny trial counts; under the
        // zero-skill null the expected maximum cannot be below the single-trial mean of zero, and a
        // negative threshold would inflate rather than deflate. Floor at zero.
        return Math.Max(0, trialSharpeSd * maxZ);
    }
    public static double ProbabilisticSharpe(double monthlySharpe, double threshold, int months, double skewness, double kurtosis)
    {
        var varianceFactor = 1 - skewness * monthlySharpe + (kurtosis - 1) / 4 * monthlySharpe * monthlySharpe;
        if (months < 2 || !double.IsFinite(monthlySharpe) || !double.IsFinite(threshold) || !double.IsFinite(varianceFactor) || varianceFactor <= 0)
            throw new ResearchException("selection-moments");
        return NormalDistribution.Cdf((monthlySharpe - threshold) * Math.Sqrt(months - 1) / Math.Sqrt(varianceFactor));
    }

    public static SelectionDiagnostic Build(IReadOnlyList<(string RunId, double[] Returns)> candidates, int registeredTrials, CancellationToken ct = default)
    {
        var ordered = candidates.OrderBy(c => c.RunId, StringComparer.Ordinal).ToArray();
        var limitations = ImmutableArray.CreateBuilder<string>();
        const string statistic = "CSCV PBO (16 blocks, monthly Sharpe ranking) and deflated Sharpe of the full-window Sharpe winner; monthly units unless labelled annualised (x sqrt(12))";
        if (ordered.Length < MinimumCandidates)
        {
            limitations.Add("Selection diagnostics require at least two candidates on a common window; a single recorded stream has no selection to deflate.");
            return new(statistic, null, null, limitations.ToImmutable());
        }
        var months = ordered[0].Returns.Length;
        if (ordered.Any(c => c.Returns.Length != months || c.Returns.Any(v => !double.IsFinite(v))))
            throw new ResearchException("selection-return-stream");
        if (months < MinimumMonths)
        {
            limitations.Add($"Selection diagnostics require at least {MinimumMonths} common months so each CSCV training/testing half holds at least {MinimumMonths / 2} observations; this family has {months}.");
            return new(statistic, null, null, limitations.ToImmutable());
        }
        var pbo = ComputePbo(ordered, months, ct);
        var deflated = ComputeDeflated(ordered, months, registeredTrials, limitations);
        limitations.Add("CSCV-PBO and the deflated Sharpe are scoped estimates over this recorded compatible family only. Searches outside it (other datasets, engine versions, workspaces, manual inspection) are not corrected by either number.");
        limitations.Add("Sharpe ranking inside CSCV holds recorded account paths fixed and assumes the statistic is estimable on subsamples; it does not refit models, so fitted-model and data uncertainty are excluded.");
        limitations.Add("Deflated-Sharpe trial counts are declared assumptions. The correlation-adjusted count uses the paper's average-correlation interpolation, whose correlation estimate can itself be overfit on short samples; correlated hypotheses make every count approximate.");
        limitations.Add("Neither probability is a calibrated chance of future profit, a promotion threshold or a repair for survivorship, point-in-time or execution defects.");
        return new(statistic, pbo, deflated, limitations.ToImmutable());
    }

    private static double Sharpe(double sum, double sumSquares, int count)
    {
        var mean = sum / count;
        var variance = (sumSquares - sum * sum / count) / (count - 1);
        if (variance <= 1e-30) return mean == 0 ? 0 : Math.Sign(mean) * DegenerateSharpe;
        return mean / Math.Sqrt(variance);
    }

    private static PboDiagnostic ComputePbo((string RunId, double[] Returns)[] candidates, int months, CancellationToken ct)
    {
        var n = candidates.Length;
        var blockSizes = Enumerable.Range(0, Blocks).Select(b => months / Blocks + (b < months % Blocks ? 1 : 0)).ToArray();
        var blockStart = new int[Blocks];
        for (var b = 1; b < Blocks; b++) blockStart[b] = blockStart[b - 1] + blockSizes[b - 1];
        var sums = new double[n, Blocks]; var squares = new double[n, Blocks];
        for (var c = 0; c < n; c++)
            for (var b = 0; b < Blocks; b++)
                for (var t = blockStart[b]; t < blockStart[b] + blockSizes[b]; t++)
                { sums[c, b] += candidates[c].Returns[t]; squares[c, b] += candidates[c].Returns[t] * candidates[c].Returns[t]; }
        var totalSum = new double[n]; var totalSquares = new double[n];
        for (var c = 0; c < n; c++) for (var b = 0; b < Blocks; b++) { totalSum[c] += sums[c, b]; totalSquares[c] += squares[c, b]; }
        var logits = new List<double>(); var winnerIn = new List<double>(); var winnerOut = new List<double>(); var negativeOut = 0;
        var inSharpe = new double[n]; var outSharpe = new double[n];
        for (var mask = 0; mask < 1 << Blocks; mask++)
        {
            if (BitOperations.PopCount((uint)mask) != Blocks / 2) continue;
            ct.ThrowIfCancellationRequested();
            var count = 0;
            for (var b = 0; b < Blocks; b++) if ((mask & 1 << b) != 0) count += blockSizes[b];
            for (var c = 0; c < n; c++)
            {
                double s = 0, q = 0;
                for (var b = 0; b < Blocks; b++) if ((mask & 1 << b) != 0) { s += sums[c, b]; q += squares[c, b]; }
                inSharpe[c] = Sharpe(s, q, count);
                outSharpe[c] = Sharpe(totalSum[c] - s, totalSquares[c] - q, months - count);
            }
            var winner = 0;
            for (var c = 1; c < n; c++) if (inSharpe[c] > inSharpe[winner]) winner = c;
            // Fractional (average) rank, 1 = worst out-of-sample, n = best; ties share their average rank.
            var below = outSharpe.Count(v => v < outSharpe[winner]);
            var tied = outSharpe.Count(v => v == outSharpe[winner]);
            var rank = below + (tied + 1) / 2d;
            var omega = rank / (n + 1);
            logits.Add(Math.Log(omega / (1 - omega)));
            winnerIn.Add(inSharpe[winner]); winnerOut.Add(outSharpe[winner]);
            if (outSharpe[winner] < 0) negativeOut++;
        }
        static double Median(List<double> values) { values.Sort(); return values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2; }
        var probability = logits.Count(l => l < 0) / (double)logits.Count;
        return new(Blocks, logits.Count, probability, Median([.. logits]), Median(winnerIn), Median(winnerOut), negativeOut / (double)logits.Count);
    }

    private static DeflatedSharpeDiagnostic? ComputeDeflated((string RunId, double[] Returns)[] candidates, int months,
        int registeredTrials, ImmutableArray<string>.Builder limitations)
    {
        var stats = candidates.Select(c =>
        {
            var mean = c.Returns.Average();
            var m2 = c.Returns.Average(v => (v - mean) * (v - mean));
            var m3 = c.Returns.Average(v => Math.Pow(v - mean, 3));
            var m4 = c.Returns.Average(v => Math.Pow(v - mean, 4));
            var sharpe = Sharpe(c.Returns.Sum(), c.Returns.Sum(v => v * v), months);
            return (c.RunId, Sharpe: sharpe, Skewness: m2 <= 1e-30 ? 0 : m3 / Math.Pow(m2, 1.5), Kurtosis: m2 <= 1e-30 ? 3 : m4 / (m2 * m2), Variance: m2);
        }).ToArray();
        var winner = stats[0];
        foreach (var s in stats.Skip(1)) if (s.Sharpe > winner.Sharpe) winner = s;
        var trialMean = stats.Average(s => s.Sharpe);
        var trialSd = Math.Sqrt(stats.Sum(s => (s.Sharpe - trialMean) * (s.Sharpe - trialMean)) / (stats.Length - 1));
        if (winner.Variance <= 1e-30 || Math.Abs(winner.Sharpe) >= DegenerateSharpe || trialSd <= 1e-15 ||
            !double.IsFinite(trialSd * trialMean * winner.Skewness * winner.Kurtosis))
        {
            limitations.Add("Deflated Sharpe is unavailable: the winning stream has no usable return variance or every candidate has the same Sharpe, so trial dispersion cannot be estimated.");
            return null;
        }
        double psr;
        try { psr = ProbabilisticSharpe(winner.Sharpe, 0, months, winner.Skewness, winner.Kurtosis); }
        catch (ResearchException) { limitations.Add("Deflated Sharpe is unavailable: the winner's moments make the Sharpe standard-error factor non-positive."); return null; }
        // Average pairwise correlation and the paper's implied-independent-trials interpolation.
        var correlations = new List<double>();
        for (var i = 0; i < candidates.Length; i++) for (var j = i + 1; j < candidates.Length; j++)
        {
            var a = candidates[i].Returns; var b = candidates[j].Returns;
            var ma = a.Average(); var mb = b.Average();
            var cov = a.Zip(b).Sum(p => (p.First - ma) * (p.Second - mb));
            var va = a.Sum(v => (v - ma) * (v - ma)); var vb = b.Sum(v => (v - mb) * (v - mb));
            if (va > 1e-30 && vb > 1e-30) correlations.Add(cov / Math.Sqrt(va * vb));
        }
        // Null when no pair has two varying streams: an unestimated correlation must not read as a measured zero.
        double? averageCorrelation = correlations.Count > 0 ? Math.Clamp(correlations.Average(), 0, 1) : null;
        var sensitivities = ImmutableArray.CreateBuilder<SharpeTrialSensitivity>();
        void Add(string basis, double trials)
        {
            var threshold = ExpectedMaximumSharpe(trialSd, trials);
            sensitivities.Add(new(basis, trials, threshold, ProbabilisticSharpe(winner.Sharpe, threshold, months, winner.Skewness, winner.Kurtosis)));
        }
        Add("family-as-independent", candidates.Length);
        if (averageCorrelation is { } rho) Add("family-correlation-adjusted", rho + (1 - rho) * candidates.Length);
        if (registeredTrials > candidates.Length) Add("registered-snapshot-as-independent", registeredTrials);
        return new(winner.RunId, winner.Sharpe, winner.Sharpe * Math.Sqrt(12), winner.Skewness, winner.Kurtosis,
            trialSd, averageCorrelation, psr, sensitivities.ToImmutable());
    }
}
