using System.Collections.Immutable;

namespace Twenty.Core.Research;

public sealed record RidgeSnapshot(double Alpha, ImmutableArray<double> Mean, ImmutableArray<double> Scale, ImmutableArray<double> Weights, double TargetMean);
public sealed class RidgeModel
{
    public RidgeSnapshot Snapshot { get; }
    private RidgeModel(RidgeSnapshot snapshot) => Snapshot = snapshot;
    public static RidgeModel Restore(RidgeSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Mean.IsDefault || snapshot.Scale.IsDefault || snapshot.Weights.IsDefault ||
            snapshot.Mean.Length != 5 || snapshot.Scale.Length != 5 || snapshot.Weights.Length != 5 ||
            snapshot.Mean.Concat(snapshot.Scale).Concat(snapshot.Weights).Any(v => !double.IsFinite(v)) ||
            snapshot.Scale.Any(v => v <= 0) || !double.IsFinite(snapshot.TargetMean) || !double.IsFinite(snapshot.Alpha) || snapshot.Alpha <= 0)
            throw new ResearchException("ridge-snapshot");
        return new(snapshot);
    }
    public static RidgeModel Fit(double[][] x, double[] y, double alpha = 10)
    {
        if (x.Length < 2 || x.Length != y.Length || x.Any(r => r.Length != 5 || r.Any(v => !double.IsFinite(v))) ||
            y.Any(v => !double.IsFinite(v)) || !double.IsFinite(alpha) || alpha <= 0) throw new ResearchException("ridge-training-shape");
        var mean = Enumerable.Range(0, 5).Select(j => x.Average(r => r[j])).ToArray();
        var scale = Enumerable.Range(0, 5).Select(j => Math.Sqrt(x.Average(r => Math.Pow(r[j] - mean[j], 2)))).Select(s => s < 1e-12 ? 1 : s).ToArray();
        var target = y.Average();
        var matrix = new double[5, 6];
        for (var j = 0; j < 5; j++) matrix[j, j] = alpha;
        for (var i = 0; i < x.Length; i++)
        {
            var z = Enumerable.Range(0, 5).Select(j => (x[i][j] - mean[j]) / scale[j]).ToArray();
            for (var a = 0; a < 5; a++)
            {
                matrix[a, 5] += z[a] * (y[i] - target);
                for (var b = 0; b < 5; b++) matrix[a, b] += z[a] * z[b];
            }
        }
        for (var j = 0; j < 5; j++)
        {
            var pivot = Enumerable.Range(j, 5 - j).MaxBy(i => Math.Abs(matrix[i, j]));
            if (Math.Abs(matrix[pivot, j]) < 1e-14) throw new ResearchException("ridge-singular");
            for (var k = j; k <= 5; k++) (matrix[j, k], matrix[pivot, k]) = (matrix[pivot, k], matrix[j, k]);
            var divisor = matrix[j, j];
            for (var k = j; k <= 5; k++) matrix[j, k] /= divisor;
            for (var i = 0; i < 5; i++) if (i != j)
            {
                var factor = matrix[i, j];
                for (var k = j; k <= 5; k++) matrix[i, k] -= factor * matrix[j, k];
            }
        }
        var weights = Enumerable.Range(0, 5).Select(j => matrix[j, 5]).ToImmutableArray();
        if (weights.Any(v => !double.IsFinite(v))) throw new ResearchException("ridge-numeric-range");
        return new(new(alpha, mean.ToImmutableArray(), scale.ToImmutableArray(), weights, target));
    }
    public double[] Predict(double[][] x) => x.Select(row => row.Length != 5 || row.Any(v => !double.IsFinite(v))
        ? throw new ResearchException("ridge-prediction-shape")
        : row.Select((v, j) => (v - Snapshot.Mean[j]) / Snapshot.Scale[j] * Snapshot.Weights[j]).Sum() + Snapshot.TargetMean).ToArray();
}
