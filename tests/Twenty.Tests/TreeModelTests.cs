using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

public class TreeModelTests
{
    private static MonthlyPanel Panel() => MonthlyPanel.Read(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.csv")),
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.metadata.json")));

    // y = sign(x0 * x1): a pure interaction a linear model cannot represent.
    // Remaining features cycle on coprime periods of the row counter, independent of the quadrant.
    private static (double[][] X, double[] Y) InteractionData()
    {
        var x = new List<double[]>(); var y = new List<double>(); var i = 0;
        for (var a = -9; a <= 9; a++) for (var b = -9; b <= 9; b++, i++)
        {
            if (a == 0 || b == 0) continue;
            x.Add([a / 10d, b / 10d, (i * 29 % 13) / 12d - 0.5, (i * 17 % 7) / 6d - 0.5, (i * 11 % 5) / 4d - 0.5]);
            y.Add(Math.Sign(a * b) * 0.02);
        }
        return (x.ToArray(), y.ToArray());
    }

    [Fact]
    public void TreeRecoversInteractionThatLinearModelCannotRepresent()
    {
        var (x, y) = InteractionData();
        var tree = BoostedTreeModel.Fit(x, y);
        var treeScores = tree.Predict(x);
        var treeAccuracy = treeScores.Zip(y).Count(p => Math.Sign(p.First) == Math.Sign(p.Second)) / (double)y.Length;
        var ridge = RidgeModel.Fit(x, y);
        var ridgeScores = ridge.Predict(x);
        var ridgeAccuracy = ridgeScores.Zip(y).Count(p => Math.Sign(p.First) == Math.Sign(p.Second)) / (double)y.Length;
        Assert.True(treeAccuracy > 0.9, $"tree accuracy {treeAccuracy}");
        Assert.True(treeAccuracy > ridgeAccuracy + 0.3, $"tree {treeAccuracy} vs ridge {ridgeAccuracy}");
    }

    [Fact]
    public void FittingIsDeterministicAndSnapshotRestoresIdenticalScores()
    {
        var (x, y) = InteractionData();
        var first = BoostedTreeModel.Fit(x, y);
        var second = BoostedTreeModel.Fit(x, y);
        Assert.Equal(first.Predict(first20(x)), second.Predict(first20(x)));
        Assert.Equal(first.Snapshot.Nodes.Length, second.Snapshot.Nodes.Length);
        Assert.Equal<int>(first.Snapshot.TreeRoots, second.Snapshot.TreeRoots);
        for (var i = 0; i < first.Snapshot.Nodes.Length; i++) Assert.Equal(first.Snapshot.Nodes[i], second.Snapshot.Nodes[i]);
        var restored = BoostedTreeModel.Restore(first.Snapshot);
        Assert.Equal(first.Predict(x), restored.Predict(x));
        static double[][] first20(double[][] rows) => rows.Take(20).ToArray();
    }

    [Fact]
    public void DeclaredHyperparametersAreFixedInTheSnapshot()
    {
        var (x, y) = InteractionData();
        var snapshot = BoostedTreeModel.Fit(x, y).Snapshot;
        Assert.Equal(BoostedTreeModel.LearningRate, snapshot.LearningRate);
        Assert.InRange(snapshot.TreeRoots.Length, 1, BoostedTreeModel.MaximumTrees);
        Assert.True(snapshot.Nodes.Length <= BoostedTreeModel.MaximumTrees * 15);
        Assert.Equal(y.Average(), snapshot.InitialValue);
    }

    [Fact]
    public void ConstantTargetOrFeaturesPredictTheTrainingMean()
    {
        var constantY = new double[50]; Array.Fill(constantY, 0.01);
        var varied = Enumerable.Range(0, 50).Select(i => new[] { i / 10d, 0d, 0d, 0d, 0d }).ToArray();
        Assert.All(BoostedTreeModel.Fit(varied, constantY).Predict(varied), p => Assert.Equal(0.01, p, 12));
        var flat = Enumerable.Range(0, 50).Select(_ => new[] { 1d, 2d, 3d, 4d, 5d }).ToArray();
        var targets = Enumerable.Range(0, 50).Select(i => i / 100d).ToArray();
        Assert.All(BoostedTreeModel.Fit(flat, targets).Predict(flat), p => Assert.Equal(targets.Average(), p, 12));
    }

    [Fact]
    public void InvalidTrainingAndPredictionInputsFailClosed()
    {
        var (x, y) = InteractionData();
        Assert.Equal("tree-training-shape", Assert.Throws<ResearchException>(() =>
            BoostedTreeModel.Fit(x.Select(r => r.Take(4).ToArray()).ToArray(), y)).Code);
        Assert.Equal("tree-training-shape", Assert.Throws<ResearchException>(() =>
            BoostedTreeModel.Fit(x, y.Take(y.Length - 1).ToArray())).Code);
        var nan = x.Select(r => r.ToArray()).ToArray(); nan[0][0] = double.NaN;
        Assert.Equal("tree-training-shape", Assert.Throws<ResearchException>(() => BoostedTreeModel.Fit(nan, y)).Code);
        var infinite = y.ToArray(); infinite[0] = double.PositiveInfinity;
        Assert.Equal("tree-training-shape", Assert.Throws<ResearchException>(() => BoostedTreeModel.Fit(x, infinite)).Code);
        Assert.Equal("tree-training-shape", Assert.Throws<ResearchException>(() =>
            BoostedTreeModel.Fit([[0, 0, 0, 0, 0]], [0.1])).Code);
        var model = BoostedTreeModel.Fit(x, y);
        Assert.Equal("tree-prediction-shape", Assert.Throws<ResearchException>(() => model.Predict([[1, 2, 3]])).Code);
        Assert.Equal("tree-prediction-shape", Assert.Throws<ResearchException>(() => model.Predict([[double.NaN, 0, 0, 0, 0]])).Code);
    }

    [Fact]
    public void CorruptSnapshotsAreRejectedOnRestore()
    {
        var (x, y) = InteractionData();
        var good = BoostedTreeModel.Fit(x, y).Snapshot;
        void Rejects(BoostedTreeSnapshot candidate) =>
            Assert.Equal("tree-snapshot", Assert.Throws<ResearchException>(() => BoostedTreeModel.Restore(candidate)).Code);
        Rejects(good with { LearningRate = 0 });
        Rejects(good with { LearningRate = 1.5 });
        Rejects(good with { InitialValue = double.NaN });
        Rejects(good with { TreeRoots = [good.Nodes.Length] });
        Rejects(good with { Nodes = good.Nodes.SetItem(0, good.Nodes[0] with { Left = 0 }) });
        Rejects(good with { Nodes = good.Nodes.SetItem(0, good.Nodes[0] with { Right = good.Nodes.Length }) });
        Rejects(good with { Nodes = good.Nodes.SetItem(0, good.Nodes[0] with { Feature = 5 }) });
        Rejects(good with { Nodes = good.Nodes.SetItem(0, good.Nodes[0] with { Threshold = double.PositiveInfinity }) });
        Rejects(good with { Nodes = good.Nodes.SetItem(IndexOfLeaf(good), good.Nodes[IndexOfLeaf(good)] with { Left = 1 }) });
        Rejects(new BoostedTreeSnapshot(0, 0.1, [], good.Nodes));
        Assert.NotNull(BoostedTreeModel.Restore(good));
        static int IndexOfLeaf(BoostedTreeSnapshot s) => s.Nodes.Select((n, i) => (n, i)).First(p => p.n.Feature == -1).i;
    }

    [Fact]
    public void TreeBacktestPreservesChronologyCapsAndContributions()
    {
        var panel = Panel();
        var report = Backtest.Run(panel, new(Hypothesis: "tree-relative-return"), new());
        Assert.Equal(1, report.Config.LabelHorizon);
        Assert.NotEmpty(report.Folds);
        Assert.All(report.Folds, fold => { Assert.True(fold.LastLabelEnd < fold.DecisionDate); Assert.NotNull(fold.BoostedTree); });
        Assert.All(report.Predictions, p => Assert.True(p.FeatureCutoff < p.DecisionDate));
        Assert.All(report.Summaries, s => Assert.Equal(report.Summaries[0].ContributionsGbp, s.ContributionsGbp));
        Assert.Equal(report.Summaries.Single(s => s.Strategy == "cash").EndingEquityGbp, report.Summaries[0].ContributionsGbp);
        Assert.All(report.Predictions.GroupBy(p => p.DecisionDate), d =>
        { Assert.True(d.Sum(p => p.TargetWeight) <= 1); Assert.All(d, p => Assert.True(p.TargetWeight <= .5m)); });
        Assert.Equal(panel.Dates[^25], report.EvaluationEnd);
        var first = panel.Dates.IndexOf(report.EvaluationStart);
        var expected = BoostedTreeModel.Restore(report.Folds[0].BoostedTree!).Predict(panel.Features(first));
        Assert.Equal(expected, report.Predictions.Where(p => p.DecisionDate == report.EvaluationStart)
            .OrderBy(p => panel.Assets.IndexOf(p.Symbol)).Select(p => p.Score));
        var ridge = Backtest.Run(panel, new(), new());
        Assert.All(ridge.Folds, fold => Assert.Null(fold.BoostedTree));
    }

    [Fact]
    public void FutureDataPerturbationCannotChangeTreeDevelopmentRun()
    {
        var csv = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.csv"));
        var metadata = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.metadata.json"));
        var panel = Panel(); var config = new ExperimentConfig(Hypothesis: "tree-relative-return");
        var original = Backtest.Run(panel, config, new());
        var cutoff = panel.Dates[^25];
        var lines = Encoding.UTF8.GetString(csv).Split('\n').Select(line =>
        {
            var cells = line.TrimEnd('\r').Split(',');
            return cells.Length == 3 && DateOnly.TryParse(cells[0], out var date) && date > cutoff
                ? cells[0] + "," + cells[1] + ",999999" : line.TrimEnd('\r');
        });
        var changed = Backtest.Run(MonthlyPanel.Read(Encoding.UTF8.GetBytes(string.Join('\n', lines)), metadata), config, new());
        Assert.Equal<StrategySummary>(original.Summaries, changed.Summaries);
        Assert.Equal<Prediction>(original.Predictions, changed.Predictions);
    }

    [Fact]
    public void TreeReportRoundTripsAndLegacyFoldJsonStillLoads()
    {
        var panel = Panel();
        var report = Backtest.Run(panel, new(Hypothesis: "tree-relative-return"), new());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ResearchStore.Json);
        var loaded = JsonSerializer.Deserialize<BacktestReport>(bytes, ResearchStore.Json)!;
        Assert.Equal(report.Folds.Length, loaded.Folds.Length);
        Assert.Equal<TreeNode>(report.Folds[0].BoostedTree!.Nodes, loaded.Folds[0].BoostedTree!.Nodes);
        var features = panel.Features(panel.Dates.IndexOf(report.EvaluationStart));
        Assert.Equal(BoostedTreeModel.Restore(report.Folds[0].BoostedTree!).Predict(features),
            BoostedTreeModel.Restore(loaded.Folds[0].BoostedTree!).Predict(features));
        // Reports stored before this field existed carry no boostedTree property.
        var node = JsonNode.Parse(bytes)!;
        foreach (var fold in node["folds"]!.AsArray()) fold!.AsObject().Remove("boostedTree");
        var legacy = JsonSerializer.Deserialize<BacktestReport>(node.ToJsonString(), ResearchStore.Json)!;
        Assert.All(legacy.Folds, fold => Assert.Null(fold.BoostedTree));
        Assert.Equal<StrategySummary>(report.Summaries, legacy.Summaries);
    }

    [Fact]
    public void SleeveBlendAcceptsTheTreeSleeveAndFitsItChronologically()
    {
        var assets = ImmutableArray.Create("A", "B", "C", "D");
        var config = new ExperimentConfig(Hypothesis: "sleeve-blend", TopK: 2, MaximumWeight: .5m,
            SleeveSpec: "benchmark:0.5,tree-relative-return:0.5");
        var weights = Backtest.BlendWeights(config, assets, "BM", new Dictionary<string, double[]> { ["tree-relative-return"] = [4, 3, 2, 1] });
        Assert.Equal(.5m, weights["BM"]); Assert.Equal(.25m, weights["A"]); Assert.Equal(.25m, weights["B"]);
        var report = Backtest.Run(Panel(), config, new());
        Assert.All(report.Folds, fold => { Assert.NotNull(fold.BoostedTree); Assert.True(fold.LastLabelEnd < fold.DecisionDate); });
        var noTree = Backtest.Run(Panel(), new(Hypothesis: "sleeve-blend", SleeveSpec: "benchmark:1"), new());
        Assert.All(noTree.Folds, fold => Assert.Null(fold.BoostedTree));
    }

    [Fact]
    public void TreeIsNotPartOfTheFrozenFirstStudyFamily()
    {
        Assert.DoesNotContain("tree-relative-return", StudyProtocol.Hypotheses);
        Assert.Equal("contribution-study-v1", StudyProtocol.Version);
    }
}
