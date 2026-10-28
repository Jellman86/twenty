using System.Collections.Immutable;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
namespace Twenty.Research;

public sealed record RawPaperPolicy(string DatasetSha256, string UniverseId, DateTimeOffset FirstDecisionAt,
    decimal MonthlyGbp, UniverseBacktestConfig Config, RawStudyExecutionPolicy Execution,
    ImmutableArray<RawStudyCalendarPin> Calendars, RawReplayBuildIdentity Build, ImmutableArray<DateTimeOffset> Decisions)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ActionMode { get; init; }
}

/// <summary>Metadata-only binding for an authored, complete offline paper batch.
/// Ledger arithmetic and strategy allocation must still be independently replayed.</summary>
public static class RawPaperContract
{
    public const string Version = "raw-paper-batches-v1";
    public const string SplitVersion = "raw-paper-split-batches-v1";
    public const string SplitMode = "pure-splits-v1";
    public static RawReplayDocument ValidateAppend(RawPaperPolicy policy, byte[]? previousBytes, byte[] nextBytes)
    {
        if (policy is null || policy.ActionMode is not (null or SplitMode) || policy.Execution is null || policy.Config is null || policy.FirstDecisionAt == default ||
            policy.MonthlyGbp is < 1 or > 1000 || policy.Config.MonthlyGbp != policy.MonthlyGbp ||
            policy.Decisions.IsDefault || policy.Decisions.Length is < 14 or > 133 || policy.FirstDecisionAt != policy.Decisions[RawUniverseDecision.HistoryDecisions - 1] ||
            policy.Calendars.IsDefaultOrEmpty || policy.Calendars.Length > 16 || policy.Calendars.Any(p => p is null))
            throw new ResearchException("raw-paper-policy");
        var next = Read(nextBytes);
        var prior = previousBytes is null ? null : Read(previousBytes);
        Header(policy, next);
        var start = prior?.Events.Length ?? 0;
        if (prior is not null)
        {
            Header(policy, prior);
            if (next.Events.Length <= start || !Bytes(prior.Events).AsSpan().SequenceEqual(Bytes(next.Events.Take(start).ToImmutableArray())))
                throw new ResearchException("raw-paper-history");
            var oldTables = prior.Calendars.IsDefault ? [] : prior.Calendars;
            var newTables = next.Calendars.IsDefault ? [] : next.Calendars;
            if (oldTables.Any(t => !newTables.Any(c => c.Sha256 == t.Sha256))) throw new ResearchException("raw-paper-history");
        }
        // The split mode is additionally regenerated at service admission from the frozen originals.
        // Ordinary archive replay still enforces that no action interrupts ordered fills.
        var batch = next.Events.Skip(start).Where(e => policy.ActionMode != SplitMode || e.Operation is not RawReplaySplit).ToArray();
        if (batch.Length < 4 || batch.Any(e => e is null) || batch[0].Operation is not RawReplayDeposit deposit ||
            batch[1].Operation is not RawReplayMark open || batch[2].Operation is not RawReplayRebalanceDecision decision ||
            batch[^1].Operation is not RawReplayMark close || !Empty(open) || !Empty(close))
            throw new ResearchException("raw-paper-batch");
        var at = batch[0].At;
        var gridIndex = (prior?.Events.Count(e => e.Operation is RawReplayRebalanceDecision) ?? 0) + RawUniverseDecision.HistoryDecisions - 1;
        if (gridIndex + 1 >= policy.Decisions.Length || at != policy.Decisions[gridIndex] || batch[^1].At != policy.Decisions[gridIndex + 1] || at != (prior?.Events[^1].At ?? policy.FirstDecisionAt) || batch[1].At != at || batch[2].At != at ||
            deposit.AmountGbp != policy.MonthlyGbp || decision.Plan is not { } plan || plan.Orders.IsDefault ||
            batch.Length != 4 + plan.Orders.Length || batch[^1].At != plan.NextDecisionAt || !Monthly(at, plan.NextDecisionAt))
            throw new ResearchException("raw-paper-batch");
        var execution = policy.Execution;
        var binding = decision.UniverseStrategy;
        if (binding?.Spec is not { } spec || spec.Strategy != "momentum" || spec.Version != RawUniverseDecision.Version ||
            spec.UniverseId != policy.UniverseId || spec.Config != policy.Config || spec.MaximumPriceAgeDays != execution.MaximumPriceAgeDays ||
            spec.Decisions.IsDefaultOrEmpty || !spec.Decisions.SequenceEqual(policy.Decisions.Skip(gridIndex - RawUniverseDecision.HistoryDecisions + 1).Take(RawUniverseDecision.HistoryDecisions)) || spec.Decisions[^1] != at || plan.UniverseId != policy.UniverseId ||
            plan.Version != RawRebalancePlanner.Version || plan.ClaimAssumptions is not null ||
            plan.CashReserveGbp != execution.CashReserveGbp || plan.FeePerFillGbp != execution.FeePerFillGbp ||
            plan.MaximumPriceAgeTicks != next.MaximumPriceAgeTicks || plan.MaximumFxAgeTicks != next.MaximumFxAgeTicks)
            throw new ResearchException("raw-paper-policy");
        // All-or-refuse batches share one reviewed execution calendar and one booking instant.
        string? calendar = null; DateTimeOffset? booked = null;
        for (var i = 0; i < plan.Orders.Length; i++)
        {
            var item = batch[i + 3];
            if (item.Operation is not RawReplayModelledFill fill || fill.Calendar is not null || fill.CalendarSha256 is null ||
                fill.Rebalance != new RawRebalanceOrderReference(batch[2].Id, i) || fill.Order != plan.Orders[i] ||
                fill.Policy != execution.Fills || fill.FeeGbp != execution.FeePerFillGbp || item.At <= at || item.At > plan.NextDecisionAt ||
                calendar is not null && calendar != fill.CalendarSha256 || booked is not null && booked != item.At)
                throw new ResearchException("raw-paper-batch");
            calendar = fill.CalendarSha256; booked = item.At;
        }
        return next;
    }
    private static bool Empty(RawReplayMark mark) => !mark.Dividends.IsDefault && mark.Dividends.IsEmpty && !mark.Terminals.IsDefault && mark.Terminals.IsEmpty;
    private static bool Monthly(DateTimeOffset from, DateTimeOffset to)
    {
        from = from.ToUniversalTime(); to = to.ToUniversalTime();
        return (to.Year - from.Year) * 12 + to.Month - from.Month == 1 && (to - from).TotalDays is >= 20 and <= 45;
    }
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, RawReplayJson.Options);
    private static RawReplayDocument Read(byte[] bytes)
    {
        if (bytes is null || bytes.Length is 0 or > RawReplayArchive.MaximumRequestBytes) throw new ResearchException("raw-paper-size");
        try
        {
            var document = JsonSerializer.Deserialize<RawReplayDocument>(bytes, RawReplayJson.Options) ?? throw new JsonException();
            if (!Bytes(document).AsSpan().SequenceEqual(bytes)) throw new ResearchException("raw-paper-canonical");
            return document;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException) { throw new ResearchException("raw-paper-request-contract"); }
    }
    private static void Header(RawPaperPolicy policy, RawReplayDocument doc)
    {
        if (doc.Version != RawReplayArchive.Version || doc.EngineVersion != RawEventReplay.Version ||
            doc.Build != policy.Build || doc.Build != RawReplayBuildIdentity.Current || doc.Study is not null || doc.Schedule is not null ||
            doc.FxPurpose != FxPurpose.Executable || doc.MaximumPriceAgeTicks != TimeSpan.FromDays(policy.Execution.MaximumPriceAgeDays).Ticks ||
            doc.MaximumFxAgeTicks != TimeSpan.FromDays(policy.Execution.MaximumFxAgeDays).Ticks ||
            doc.Events.IsDefaultOrEmpty || doc.Events.Length > 10000 || doc.Events.Any(e => e is null || e.DatasetSha256 != policy.DatasetSha256))
            throw new ResearchException("raw-paper-request-contract");
        var tables = doc.Calendars.IsDefault ? [] : doc.Calendars;
        if (tables.Any(c => c is null || !policy.Calendars.Any(p => p.CalendarId == c.Reference.Id && p.Venue == c.Venue && p.Sha256 == c.Sha256)) ||
            tables.Select(c => c.Sha256).Distinct(StringComparer.Ordinal).Count() != tables.Length)
            throw new ResearchException("raw-paper-calendar");
    }
}
