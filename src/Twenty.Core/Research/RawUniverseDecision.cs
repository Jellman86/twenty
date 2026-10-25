using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawUniverseStrategySpec(string Version, string UniverseId, ImmutableArray<DateTimeOffset> Decisions,
    int MaximumPriceAgeDays, UniverseBacktestConfig Config, string Strategy);
public sealed record RawUniverseMomentumScore(string ListingId, double? Score);
public sealed record RawUniverseAllocation(string Version, string PanelSha256, DateTimeOffset DecisionAt,
    ImmutableArray<string> HoldableListingIds, ImmutableArray<string> UnpricedListingIds,
    ImmutableArray<RawUniverseMomentumScore> MomentumScores, ImmutableSortedDictionary<string, decimal> Weights,
    ImmutableArray<string> Limitations);
public sealed record RawUniverseStrategyBinding(RawUniverseStrategySpec Spec, RawUniverseAllocation Allocation);

/// <summary>Decision-only bridge from the existing baseline family to raw-share intentions.
/// No future return period, selection outcome or study admission is needed or granted.</summary>
public static class RawUniverseDecision
{
    public const string Version = "raw-universe-decision-v1";
    public const int HistoryDecisions = UniverseBacktest.MomentumPeriods + 2;
    public const int MaximumListings = 1000;

    internal static void Validate(RawUniverseStrategySpec spec)
    {
        if (spec is null || spec.Version != Version || string.IsNullOrWhiteSpace(spec.UniverseId) || spec.UniverseId.Length > 128 ||
            spec.Decisions.IsDefault || spec.Decisions.Length != HistoryDecisions || spec.MaximumPriceAgeDays is < 1 or > 92 ||
            spec.Config is null || !UniverseBacktest.Strategies.Contains(spec.Strategy))
            throw new ResearchException("universe-decision-spec");
        try { ChangingUniversePanel.ValidateDecisions(spec.Decisions, TimeSpan.FromDays(spec.MaximumPriceAgeDays)); spec.Config.Validate(); }
        catch (ResearchException) { throw new ResearchException("universe-decision-spec"); }
    }

    public static RawUniverseAllocation Allocate(PointInTimeDataset dataset, RawUniverseStrategySpec spec)
    {
        ArgumentNullException.ThrowIfNull(dataset); Validate(spec);
        // The final month is the decision itself. There is deliberately no period
        // beginning at that instant, so future realized coverage cannot gate buys.
        var panel = ChangingUniversePanel.FromDataset(dataset, spec.UniverseId, spec.Decisions,
            TimeSpan.FromDays(spec.MaximumPriceAgeDays));
        if (panel.HasGaps) throw new ResearchException("universe-decision-history-gaps");
        var current = panel.Months[^1];
        if (current.EligibleListingIds.Length > MaximumListings) throw new ResearchException("universe-decision-capacity");
        var holdable = current.EligibleListingIds.Except(current.UnpricedListingIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        if (holdable.IsEmpty) throw new ResearchException("universe-decision-empty");
        ImmutableArray<RawUniverseMomentumScore> scores;
        try { scores = holdable.Select(id => new RawUniverseMomentumScore(id, UniverseBacktest.TrailingMomentum(panel, HistoryDecisions - 1, id))).ToImmutableArray(); }
        catch (OverflowException) { throw new ResearchException("universe-decision-arithmetic"); }
        var ranked = scores.Where(s => s.Score is not null).ToArray();
        var weights = spec.Strategy switch
        {
            "equal_weight" => holdable.ToDictionary(id => id, _ => UniverseBacktest.EqualFraction(holdable.Length), StringComparer.Ordinal),
            "momentum" when ranked.Length > 0 => Backtest.Rank(ranked.Select(s => s.Score!.Value).ToArray(),
                ranked.Select(s => s.ListingId).ToImmutableArray(), spec.Config.TopK, spec.Config.MaximumWeight),
            _ => new Dictionary<string, decimal>(StringComparer.Ordinal)
        };
        return new(Version, panel.Sha256, current.DecisionAt.ToUniversalTime(), holdable, current.UnpricedListingIds,
            scores, weights.ToImmutableSortedDictionary(StringComparer.Ordinal),
            ["Decision-only adjusted-return signals use eleven completed period-vintage returns, skipping the latest period; no future return or future eligible count is consulted.",
             "Current unpriced names and incomplete momentum histories are disclosed. Historical gaps refuse the decision; this is a conservative coverage rule, not evidence of an edge.",
             "Weights use the existing ordinal ranking and caps. MonthlyGbp is retained configuration metadata; deposits, raw prices, FX, fees and execution are separate replay inputs.",
             "This adapter does not admit a study, select a candidate, spend a final evaluation, authenticate source data or certify broker/ISA eligibility."]);
    }
}
