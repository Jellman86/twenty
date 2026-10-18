using System.Collections.Immutable;

namespace Twenty.Core.Research;

/// <summary>One node of a regression tree. Feature -1 is a leaf carrying Value; otherwise the row
/// follows Left when its feature value is at most Threshold. Nodes are stored flat with every
/// child after its parent, so a validated snapshot cannot contain a cycle.</summary>
public sealed record TreeNode(int Feature, double Threshold, int Left, int Right, double Value);
public sealed record BoostedTreeSnapshot(double InitialValue, double LearningRate,
    ImmutableArray<int> TreeRoots, ImmutableArray<TreeNode> Nodes);

/// <summary>Deterministic gradient-boosted regression trees: the nonlinear challenger to
/// <see cref="RidgeModel"/> motivated by Gu, Kelly and Xiu (2020) (literature review P01).
/// Hyperparameters are fixed constants declared before any run, not config fields, so the
/// hypothesis cannot be silently retuned per trial. No randomness, subsampling or scaling:
/// exhaustive splits are searched in feature order and the first best split wins ties.</summary>
public sealed class BoostedTreeModel
{
    public const int MaximumTrees = 60, MaximumDepth = 3, MinimumLeafRows = 20;
    public const double LearningRate = 0.1;
    private const int Features = 5;
    private const double MinimumGain = 1e-12;

    public BoostedTreeSnapshot Snapshot { get; }
    private BoostedTreeModel(BoostedTreeSnapshot snapshot) => Snapshot = snapshot;

    public static BoostedTreeModel Restore(BoostedTreeSnapshot snapshot)
    {
        if (snapshot is null || snapshot.TreeRoots.IsDefault || snapshot.Nodes.IsDefault ||
            !double.IsFinite(snapshot.InitialValue) || !double.IsFinite(snapshot.LearningRate) ||
            snapshot.LearningRate is <= 0 or > 1 || snapshot.TreeRoots.Length > 256 ||
            snapshot.Nodes.Length > 32768 || (snapshot.TreeRoots.Length == 0) != (snapshot.Nodes.Length == 0))
            throw new ResearchException("tree-snapshot");
        for (var i = 0; i < snapshot.TreeRoots.Length; i++)
            if (snapshot.TreeRoots[i] < 0 || snapshot.TreeRoots[i] >= snapshot.Nodes.Length ||
                i > 0 && snapshot.TreeRoots[i] <= snapshot.TreeRoots[i - 1])
                throw new ResearchException("tree-snapshot");
        for (var i = 0; i < snapshot.Nodes.Length; i++)
        {
            var node = snapshot.Nodes[i];
            if (node is null || !double.IsFinite(node.Threshold) || !double.IsFinite(node.Value) ||
                node.Feature == -1 && (node.Left != -1 || node.Right != -1) ||
                node.Feature is >= 0 and < Features && (node.Left <= i || node.Right <= i ||
                    node.Left >= snapshot.Nodes.Length || node.Right >= snapshot.Nodes.Length || node.Left == node.Right) ||
                node.Feature < -1 || node.Feature >= Features)
                throw new ResearchException("tree-snapshot");
        }
        return new(snapshot);
    }

    public static BoostedTreeModel Fit(double[][] x, double[] y, CancellationToken ct = default)
    {
        if (x.Length < 2 || x.Length != y.Length || x.Any(r => r.Length != Features || r.Any(v => !double.IsFinite(v))) ||
            y.Any(v => !double.IsFinite(v))) throw new ResearchException("tree-training-shape");
        var initial = y.Average();
        var fitted = new double[y.Length]; Array.Fill(fitted, initial);
        var nodes = new List<TreeNode>(); var roots = new List<int>();
        var rows = Enumerable.Range(0, y.Length).ToArray();
        for (var tree = 0; tree < MaximumTrees; tree++)
        {
            ct.ThrowIfCancellationRequested();
            var residual = y.Select((target, i) => target - fitted[i]).ToArray();
            if (residual.Sum(r => r * r) <= 1e-15) break;
            var root = nodes.Count;
            var leaves = new List<(int Node, int[] Rows)>();
            Build(x, residual, rows, 0, nodes, leaves);
            if (nodes[root].Feature == -1) { nodes.RemoveRange(root, nodes.Count - root); break; }
            roots.Add(root);
            foreach (var (leaf, members) in leaves)
                foreach (var member in members) fitted[member] += LearningRate * nodes[leaf].Value;
        }
        if (nodes.Any(n => !double.IsFinite(n.Threshold) || !double.IsFinite(n.Value)))
            throw new ResearchException("tree-numeric-range");
        var snapshot = new BoostedTreeSnapshot(initial, LearningRate, roots.ToImmutableArray(), nodes.ToImmutableArray());
        return Restore(snapshot);
    }

    // Appends the node for these rows (patching child indices after recursion), records its leaves
    // for the boosting update, and returns the node index. Leaf value is the mean residual.
    private static int Build(double[][] x, double[] residual, int[] rows, int depth,
        List<TreeNode> nodes, List<(int Node, int[] Rows)> leaves)
    {
        var index = nodes.Count;
        nodes.Add(new(-1, 0, -1, -1, rows.Average(r => residual[r])));
        if (depth < MaximumDepth && rows.Length >= 2 * MinimumLeafRows &&
            BestSplit(x, residual, rows) is { } split)
        {
            var left = Build(x, residual, split.Left, depth + 1, nodes, leaves);
            var right = Build(x, residual, split.Right, depth + 1, nodes, leaves);
            nodes[index] = new(split.Feature, split.Threshold, left, right, 0);
        }
        else leaves.Add((index, rows));
        return index;
    }

    private static (int Feature, double Threshold, int[] Left, int[] Right)? BestSplit(double[][] x, double[] residual, int[] rows)
    {
        var total = rows.Sum(r => residual[r]); var totalSquare = rows.Sum(r => residual[r] * residual[r]);
        var nodeError = totalSquare - total * total / rows.Length;
        (int Feature, double Threshold, int[] Left, int[] Right)? best = null; var bestGain = MinimumGain;
        for (var feature = 0; feature < Features; feature++)
        {
            var ordered = rows.OrderBy(r => x[r][feature]).ThenBy(r => r).ToArray();
            double leftSum = 0, leftSquare = 0;
            for (var i = 0; i < ordered.Length - 1; i++)
            {
                var value = residual[ordered[i]]; leftSum += value; leftSquare += value * value;
                var leftCount = i + 1; var rightCount = ordered.Length - leftCount;
                if (x[ordered[i]][feature] == x[ordered[i + 1]][feature] || leftCount < MinimumLeafRows || rightCount < MinimumLeafRows) continue;
                var rightSum = total - leftSum; var rightSquare = totalSquare - leftSquare;
                var gain = nodeError - (leftSquare - leftSum * leftSum / leftCount + rightSquare - rightSum * rightSum / rightCount);
                if (gain > bestGain)
                {
                    bestGain = gain;
                    best = (feature, x[ordered[i]][feature] + (x[ordered[i + 1]][feature] - x[ordered[i]][feature]) / 2,
                        ordered.Take(leftCount).ToArray(), ordered.Skip(leftCount).ToArray());
                }
            }
        }
        return best;
    }

    public double[] Predict(double[][] x)
    {
        return x.Select(row =>
        {
            if (row.Length != Features || row.Any(v => !double.IsFinite(v))) throw new ResearchException("tree-prediction-shape");
            var score = Snapshot.InitialValue;
            foreach (var root in Snapshot.TreeRoots)
            {
                var node = Snapshot.Nodes[root];
                while (node.Feature >= 0) node = Snapshot.Nodes[row[node.Feature] <= node.Threshold ? node.Left : node.Right];
                score += Snapshot.LearningRate * node.Value;
            }
            return score;
        }).ToArray();
    }
}
