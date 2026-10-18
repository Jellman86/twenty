using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Twenty.Core.Research;

public sealed record ForwardSignal(string Id, string RunId, string ReportSha256, string ModelSha256, string DatasetSha256,
    string FeatureVersion, string Hypothesis, DateOnly DecisionDate, DateOnly FeatureCutoff, DateTimeOffset GeneratedAt,
    bool Synthetic, decimal MonthlyGbp, decimal MaximumWeight, ImmutableArray<Prediction> Predictions, DateTimeOffset InputObservedAt);

public static class ForwardSignalBuilder
{
    // Score a frozen model. Never refit on new labels or use the held-out report to pick a replacement.
    public static ForwardSignal Build(string runId, string reportHash, BacktestReport frozen, MonthlyPanel trainingPanel,
        MonthlyPanel latestPanel, DateTimeOffset now, DateTimeOffset? inputObservedAt = null)
    {
        if (trainingPanel.ResearchOnly || latestPanel.ResearchOnly) throw new ResearchException("signal-research-only-panel");
        now = now.ToUniversalTime();
        var observedAt = inputObservedAt ?? now;
        if (observedAt > now) throw new ResearchException("signal-input-availability");
        if (frozen.FeatureVersion != MonthlyPanel.FeatureVersion || frozen.DatasetSha256 != trainingPanel.Sha256 ||
            frozen.Folds.IsDefaultOrEmpty || !trainingPanel.Assets.SequenceEqual(latestPanel.Assets) || trainingPanel.Benchmark != latestPanel.Benchmark)
            throw new ResearchException("signal-model-universe");
        if (reportHash.Length != 64 || reportHash.Any(c => !char.IsAsciiHexDigitLower(c))) throw new ResearchException("signal-report-hash");
        var cutoff = latestPanel.Dates[^1];
        var decision = new DateOnly(cutoff.Year, cutoff.Month, 1).AddMonths(1);
        if (decision.Year != now.Year || decision.Month != now.Month || cutoff >= DateOnly.FromDateTime(now.UtcDateTime))
            throw new ResearchException("signal-current-panel-required");
        var snapshot = frozen.Folds[^1].Model;
        if (frozen.Folds[^1].DecisionDate >= decision || frozen.Folds[^1].LastLabelEnd >= decision)
            throw new ResearchException("signal-model-timing");
        var features = latestPanel.Features(latestPanel.Dates.Length);
        var scores = frozen.Config.Hypothesis switch
        {
            "momentum" => features.Select(x => x[2]).ToArray(),
            "breakout" => latestPanel.BreakoutScores(latestPanel.Dates.Length),
            "ridge-relative-return" or "upside-tail" => RidgeModel.Restore(snapshot).Predict(features),
            _ => throw new ResearchException("signal-hypothesis")
        };
        var weights = Backtest.Rank(scores, latestPanel.Assets, frozen.Config.TopK, frozen.Config.MaximumWeight);
        if (frozen.Config.Hypothesis == "breakout") weights = weights.Where(x => scores[latestPanel.Assets.IndexOf(x.Key)] > 0).ToDictionary();
        var predictions = latestPanel.Assets.Select((s, i) => new Prediction(decision, cutoff, s, scores[i], weights.GetValueOrDefault(s))).ToImmutableArray();
        string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
        var modelHash = Hash(new { snapshot, frozen.Config, frozen.FeatureVersion });
        var id = Hash(new { runId, reportHash, modelHash, latestPanel.Sha256, decision, predictions });
        return new(id, runId, reportHash, modelHash, latestPanel.Sha256, frozen.FeatureVersion, frozen.Config.Hypothesis,
            decision, cutoff, now, frozen.Synthetic || latestPanel.Synthetic, frozen.Config.MonthlyGbp, frozen.Config.MaximumWeight, predictions, observedAt);
    }
}
