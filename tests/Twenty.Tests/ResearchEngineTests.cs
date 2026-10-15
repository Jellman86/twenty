using System.Text;
using System.Text.Json;
using Twenty.Core.Research;

namespace Twenty.Tests;

public class ResearchEngineTests
{
    private static byte[] Csv() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.csv"));
    private static byte[] Metadata() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.metadata.json"));
    private static MonthlyPanel Panel() => MonthlyPanel.Read(Csv(), Metadata());
    [Fact]
    public void NativeFeaturesAndRidgeMatchIndependentPythonReference()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "parity.json")));
        var root = json.RootElement; var panel = Panel();
        Assert.Equal(root.GetProperty("datasetSha256").GetString(), panel.Sha256);
        var t = root.GetProperty("featureDecisionIndex").GetInt32();
        var features = panel.Features(t); var expected = root.GetProperty("features");
        for (var i = 0; i < features.Length; i++) for (var j = 0; j < 5; j++) Assert.InRange(Math.Abs(features[i][j] - expected[i][j].GetDouble()), 0, 1e-12);
        var train = Enumerable.Range(13, t - 14).ToArray();
        var model = RidgeModel.Fit(train.SelectMany(panel.Features).ToArray(), train.SelectMany(panel.Labels).ToArray());
        var scores = model.Predict(features);
        for (var i = 0; i < scores.Length; i++) Assert.InRange(Math.Abs(scores[i] - root.GetProperty("scores")[i].GetDouble()), 0, 1e-12);
    }
    [Fact]
    public void DecimalAccountingAgreesWithPythonSharedGoldenSteps()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "parity.json")));
        var portfolio = new NotionalPortfolio();
        foreach (var step in json.RootElement.GetProperty("steps").EnumerateArray())
        {
            var weights = step.GetProperty("weights").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDecimal());
            var returns = step.GetProperty("returns").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDecimal());
            var actual = portfolio.Step(step.GetProperty("monthly").GetDecimal(), weights, returns, new());
            var expected = step.GetProperty("expected");
            foreach (var pair in new[] { (actual.EquityGbp,"equity_gbp"), (actual.CashGbp,"cash_gbp"), (actual.PeriodCostGbp,"period_cost_gbp"),
                         (actual.ContributionsGbp,"contributions_gbp"), (actual.UnitPrice,"unit_price"),(actual.MaxDrawdown,"max_drawdown") })
                Assert.InRange(Math.Abs((double)pair.Item1 - expected.GetProperty(pair.Item2).GetDouble()), 0, 1e-8);
            Assert.True(actual.CashGbp >= 0);
        }
    }
    [Fact]
    public void InvalidAccountingInputsNeverHalfMutatePortfolio()
    {
        var portfolio = new NotionalPortfolio();
        portfolio.Step(20, new Dictionary<string, decimal> { ["A"] = 1 }, new Dictionary<string, decimal> { ["A"] = 0 }, new());
        var before = portfolio.Equity;
        Assert.Throws<ResearchException>(() => portfolio.Step(20, new Dictionary<string, decimal> { ["B"] = 1 }, new Dictionary<string, decimal> { ["B"] = 0 }, new()));
        Assert.Equal(before, portfolio.Equity); Assert.Equal(20, portfolio.Contributions);
    }
    [Fact]
    public void MinimumsLeaveCashAndDepositsDoNotRepairDrawdown()
    {
        var p = new NotionalPortfolio();
        var one = p.Step(20, new Dictionary<string, decimal> { ["A"] = .01m }, new Dictionary<string, decimal> { ["A"] = 0 }, new());
        Assert.Empty(one.Trades); Assert.Equal(20, one.CashGbp);
        p = new();
        p.Step(20, new Dictionary<string, decimal> { ["A"] = 1 }, new Dictionary<string, decimal> { ["A"] = -.5m }, new(0,0,0,0,1));
        var two = p.Step(100, new Dictionary<string, decimal>(), new Dictionary<string, decimal> { ["A"] = 0 }, new(0,0,0,0,1));
        Assert.Equal(-.5m, two.MaxDrawdown); Assert.Equal(.5m, two.UnitPrice);
    }
    [Fact]
    public void BacktestPreservesChronologyContributionsAndReservedData()
    {
        var panel = Panel(); var report = Backtest.Run(panel, new(), new());
        Assert.True(report.Synthetic); Assert.Equal(5, report.Summaries.Length);
        Assert.All(report.Folds, fold => Assert.True(fold.LastLabelEnd < fold.DecisionDate));
        Assert.All(report.Predictions, prediction => Assert.True(prediction.FeatureCutoff < prediction.DecisionDate));
        Assert.All(report.Summaries, s => Assert.Equal(report.Summaries[0].ContributionsGbp, s.ContributionsGbp));
        Assert.Equal(panel.Dates[^25], report.EvaluationEnd);
        Assert.Equal(report.Summaries.Single(s => s.Strategy == "cash").EndingEquityGbp, report.Summaries[0].ContributionsGbp);
        var decisions = report.Predictions.GroupBy(p => p.DecisionDate);
        Assert.All(decisions, d => { Assert.True(d.Sum(p => p.TargetWeight) <= 1); Assert.All(d, p => Assert.True(p.TargetWeight <= .5m)); });
    }
    [Fact]
    public void FutureDataPerturbationCannotChangeDevelopmentRun()
    {
        var panel = Panel(); var original = Backtest.Run(panel, new(), new());
        var cutoff = panel.Dates[^25];
        var lines = Encoding.UTF8.GetString(Csv()).Split('\n').Select(line =>
        {
            var cells = line.TrimEnd('\r').Split(',');
            return cells.Length == 3 && DateOnly.TryParse(cells[0], out var date) && date > cutoff
                ? cells[0] + "," + cells[1] + ",999999" : line.TrimEnd('\r');
        });
        var changed = Backtest.Run(MonthlyPanel.Read(Encoding.UTF8.GetBytes(string.Join('\n', lines)), Metadata()), new(), new());
        Assert.Equal<StrategySummary>(original.Summaries, changed.Summaries); Assert.Equal<Prediction>(original.Predictions, changed.Predictions);
        Assert.Equal<ForwardPoint>(original.Forward.Points, changed.Forward.Points);
    }
    [Fact]
    public void DuplicateAndIncompletePanelsFail()
    {
        var text = Encoding.UTF8.GetString(Csv());
        Assert.Throws<ResearchException>(() => MonthlyPanel.Read(Encoding.UTF8.GetBytes(text + text.Split('\n')[1] + "\n"), Metadata()));
        Assert.Throws<ResearchException>(() => MonthlyPanel.Read(Encoding.UTF8.GetBytes(string.Join('\n', text.Split('\n').Where((_, i) => i != 1))), Metadata()));
    }
    [Fact]
    public void ForwardPathsAreSeededConditionalScenariosWithConsistentQuantiles()
    {
        var a = Backtest.Run(Panel(), new(), new()); var b = Backtest.Run(Panel(), new(), new());
        Assert.Equal<ForwardPoint>(a.Forward.Points, b.Forward.Points);
        Assert.All(a.Forward.Points, p => Assert.True(p.LowerBalanceGbp <= p.MedianBalanceGbp && p.MedianBalanceGbp <= p.UpperBalanceGbp));
        Assert.Equal(2400m, a.Forward.Points[^1].ContributionsGbp);
    }
    [Fact]
    public void CancellationAndInvalidConfigurationStopExperiments()
    {
        using var ct = new CancellationTokenSource(); ct.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Backtest.Run(Panel(), new(), new(), ct.Token));
        Assert.Throws<ResearchException>(() => Backtest.Run(Panel(), new(TopK:0), new()));
    }
    [Fact]
    public void UpsideLabelsAreSixMonthOutcomesPurgedBeforeEachTrainingDecision()
    {
        var panel = MonthlyPanel.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "stress.csv")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "stress.metadata.json")));
        var report = Backtest.Run(panel, new(Hypothesis: "upside-tail"), new());
        Assert.Equal(6, report.Config.LabelHorizon);
        Assert.All(report.Folds, fold => Assert.True(fold.LastLabelEnd < fold.DecisionDate));
        Assert.All(report.Predictions, p => Assert.True(p.FeatureCutoff < p.DecisionDate));
        Assert.Equal(panel.Dates[79], report.EvaluationStart);
    }

    [Fact]
    public void BreakoutUsesOnlyPreviouslyAvailableReturnIndices()
    {
        var panel = MonthlyPanel.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.csv")),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "demo.metadata.json")));
        var scores = panel.BreakoutScores(74);
        for (var a = 0; a < panel.Assets.Length; a++)
        {
            var symbol = panel.Assets[a];
            var expected = (double)(panel.Index(73, symbol) / Enumerable.Range(61, 12).Max(t => panel.Index(t, symbol)) - 1);
            Assert.Equal(expected, scores[a]);
        }
        var report = Backtest.Run(panel, new(Hypothesis: "breakout"), new());
        Assert.All(report.Predictions.Where(p => p.Score <= 0), p => Assert.Equal(0, p.TargetWeight));
    }

}
