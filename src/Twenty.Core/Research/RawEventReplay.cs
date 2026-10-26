using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public abstract record RawReplayOperation;
public sealed record RawReplayDeposit(decimal AmountGbp) : RawReplayOperation;
// The name deliberately preserves the weaker evidence contract of a caller-supplied fill.
public sealed record RawReplayAssumedFill(string ListingId, decimal SignedQuantity, decimal RawPrice,
    PriceBasis Basis, decimal FeeGbp) : RawReplayOperation;
public sealed record RawRebalanceOrderReference(string DecisionId, int OrderIndex);
public sealed record RawReplayRebalanceDecision(RawRebalancePlan Plan) : RawReplayOperation
{
    public RawUniverseStrategyBinding? UniverseStrategy { get; init; }
}
public sealed record RawReplayModelledFill(PlannedOrder Order, FillPolicy Policy, ExecutionFill Fill, decimal FeeGbp, ExecutionCalendar? Calendar) : RawReplayOperation
{
    public RawRebalanceOrderReference? Rebalance { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CalendarSha256 { get; init; }
}
public sealed record RawReplaySplit(string ListingId, string ActionId) : RawReplayOperation;
public sealed record RawReplayDividendCapture(string ListingId, string ActionId, RawDividendEvidence Evidence) : RawReplayOperation;
public sealed record RawReplayDividendPayment(string ListingId, string ActionId, RawDividendPayment Payment) : RawReplayOperation;
public sealed record RawReplayTerminalCapture(string ListingId, string ActionId, RawTerminalEvidence Evidence) : RawReplayOperation;
public sealed record RawReplayTerminalSettlement(string ListingId, string ActionId, RawTerminalSettlement Settlement) : RawReplayOperation;
public sealed record RawReplayMark(ImmutableArray<RawDividendMarkAssumption> Dividends,
    ImmutableArray<RawTerminalMarkAssumption> Terminals) : RawReplayOperation;
public sealed record RawReplayItem(string Id, int Sequence, DataSlice Market, RawReplayOperation Operation);
public sealed record RawReplayValuation(string Id, int Sequence, RawGbpClaimValuation Valuation);
public sealed record RawEventReplayResult(string Version, ImmutableArray<RawLedgerEntry> Entries,
    ImmutableArray<RawReplayValuation> Marks, ImmutableSortedDictionary<string, RawQuantityPosition> Holdings,
    ImmutableArray<RawDividendEntitlement> DividendEntitlements, ImmutableArray<RawTerminalEntitlement> TerminalEntitlements,
    decimal CashGbp, decimal ContributionsGbp, decimal FeesGbp, ImmutableArray<string> Limitations);

/// <summary>Bounded offline replay of explicit, caller-ordered events into a new private ledger.
/// Sequence is contiguous from zero and orders ties; markets retain their own booking/as-of times.
/// Bound rebalance decisions rederive supplied intentions; this does not select strategies,
/// discover actions, schedule, authenticate or persist events.</summary>
public static class RawEventReplay
{
    public const string Version = "raw-event-replay-v3";
    public static RawEventReplayResult Run(IReadOnlyList<RawReplayItem> events, TimeSpan maximumPriceAge,
        TimeSpan maximumFxAge, FxPurpose purpose) => RunWithUniverse(events, maximumPriceAge, maximumFxAge, purpose, null);

    internal static RawEventReplayResult RunWithUniverse(IReadOnlyList<RawReplayItem> events, TimeSpan maximumPriceAge,
        TimeSpan maximumFxAge, FxPurpose purpose, IReadOnlyDictionary<string, RawUniverseAllocation>? universeAllocations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count is 0 or > 10000 || maximumPriceAge < TimeSpan.Zero || maximumFxAge < TimeSpan.Zero || !Enum.IsDefined(purpose))
            throw new ResearchException("raw-replay-input");
        var input = events.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < input.Length; i++)
        {
            var item = input[i];
            if (item is null || item.Sequence != i || item.Market is null || item.Operation is null ||
                string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 128 || !ids.Add(item.Id))
                throw new ResearchException("raw-replay-input");
            if (item.Market.DecisionAt == default || i > 0 && item.Market.DecisionAt < input[i - 1].Market.DecisionAt)
                throw new ResearchException("raw-replay-time");
        }
        if (input[^1].Operation is not RawReplayMark) throw new ResearchException("raw-replay-final-mark");
        var ledger = new RawGbpLedger(); var marks = ImmutableArray.CreateBuilder<RawReplayValuation>();
        RawRebalancePlan? pending = null; string? decisionId = null; var nextOrder = 0;
        foreach (var item in input)
        {
            ct.ThrowIfCancellationRequested();
            // A bound batch is deliberately all-or-refuse. Intervening deposits,
            // actions and skipped/reordered fills cannot change its funding premise.
            if (pending is not null || item.Operation is RawReplayModelledFill { Rebalance: not null })
            {
                if (pending is null || item.Operation is not RawReplayModelledFill linked ||
                    linked.Rebalance is not { } reference || reference.DecisionId != decisionId || reference.OrderIndex != nextOrder ||
                    linked.Order != pending.Orders[nextOrder] || linked.FeeGbp != pending.FeePerFillGbp)
                    throw new ResearchException("raw-replay-rebalance-order");
            }
            switch (item.Operation)
            {
                case RawReplayRebalanceDecision decision:
                    var supplied = decision.Plan;
                    if (supplied is null || supplied.Weights is null || supplied.Orders.IsDefault || supplied.Targets.IsDefault ||
                        supplied.MaximumPriceAgeTicks < 0 || supplied.MaximumFxAgeTicks < 0)
                        throw new ResearchException("raw-replay-rebalance-plan");
                    if (decision.UniverseStrategy is { } strategy)
                    {
                        // Only the archive has the original dataset needed to rebuild
                        // historical vintages. Direct slice-only replay must refuse.
                        if (universeAllocations is null || !universeAllocations.TryGetValue(item.Id, out var allocation))
                            throw new ResearchException("raw-replay-universe-context");
                        if (allocation.DecisionAt != item.Market.DecisionAt || strategy.Spec.UniverseId != supplied.UniverseId ||
                            !allocation.Weights.SequenceEqual(supplied.Weights))
                            throw new ResearchException("raw-replay-universe-binding");
                    }
                    RawRebalancePlan recomputed;
                    if (supplied.Version == RawRebalancePlanner.ClaimsVersion && supplied.ClaimAssumptions is { } assumptions)
                        recomputed = RawRebalancePlanner.PlanWithReceivables(ledger, item.Market, supplied.UniverseId, supplied.Weights,
                            supplied.NextDecisionAt, supplied.CashReserveGbp, supplied.FeePerFillGbp,
                            TimeSpan.FromTicks(supplied.MaximumPriceAgeTicks), TimeSpan.FromTicks(supplied.MaximumFxAgeTicks), assumptions.Dividends, assumptions.Terminals);
                    else if (supplied.Version == RawRebalancePlanner.Version && supplied.ClaimAssumptions is null)
                        recomputed = RawRebalancePlanner.Plan(ledger, item.Market, supplied.UniverseId, supplied.Weights,
                            supplied.NextDecisionAt, supplied.CashReserveGbp, supplied.FeePerFillGbp,
                            TimeSpan.FromTicks(supplied.MaximumPriceAgeTicks), TimeSpan.FromTicks(supplied.MaximumFxAgeTicks));
                    else throw new ResearchException("raw-replay-rebalance-plan");
                    if (!RawReplayJson.Write(supplied, RawReplayArchive.MaximumRequestBytes).AsSpan()
                        .SequenceEqual(RawReplayJson.Write(recomputed, RawReplayArchive.MaximumRequestBytes)))
                        throw new ResearchException("raw-replay-rebalance-plan");
                    if (supplied.Orders.Length > 0) { pending = supplied; decisionId = item.Id; nextOrder = 0; }
                    break;
                case RawReplayDeposit deposit:
                    ledger.Deposit(item.Id, item.Market.DecisionAt, deposit.AmountGbp); break;
                case RawReplayAssumedFill fill:
                    ledger.Fill(item.Id, item.Market, fill.ListingId, fill.SignedQuantity, fill.RawPrice, fill.Basis,
                        fill.FeeGbp, maximumFxAge); break;
                case RawReplayModelledFill modelled when modelled.Order is not null && modelled.Policy is not null && modelled.Fill is not null && modelled.Calendar is not null:
                    var plan = ExecutionPlanner.Plan(item.Market, [modelled.Order], modelled.Policy, modelled.Calendar, maximumFxAge);
                    if (plan.Fills.Length != 1 || plan.Rejections.Length != 0 || plan.Fills[0] != modelled.Fill)
                        throw new ResearchException("raw-replay-execution");
                    var execution = modelled.Fill;
                    if (item.Sequence > 0)
                    {
                        var previous = input[item.Sequence - 1];
                        // Consecutive fills retain execution order, rather than treating
                        // the prior receipt's later booking as an intervening cash event.
                        // Marks and every non-modelled operation still impose booking time.
                        var previousAt = previous.Operation is RawReplayModelledFill prior
                            ? prior.Fill.Timing.Session.OpenAt : previous.Market.DecisionAt;
                        if (previousAt > execution.Timing.Session.OpenAt) throw new ResearchException("raw-replay-execution-time");
                    }
                    ledger.Fill(item.Id, item.Market, execution.ListingId, execution.Buy ? execution.Quantity : -execution.Quantity,
                        execution.FillPrice, PriceBasis.Raw, modelled.FeeGbp, maximumFxAge, execution, modelled.Calendar); break;
                case RawReplaySplit split:
                    ledger.ApplySplit(item.Id, item.Market, split.ListingId, split.ActionId); break;
                case RawReplayDividendCapture capture:
                    ledger.CaptureDividend(item.Id, item.Market, capture.ListingId, capture.ActionId, capture.Evidence); break;
                case RawReplayDividendPayment payment:
                    ledger.PayDividend(item.Id, item.Market, payment.ListingId, payment.ActionId, payment.Payment, maximumFxAge); break;
                case RawReplayTerminalCapture capture:
                    ledger.CaptureTerminal(item.Id, item.Market, capture.ListingId, capture.ActionId, capture.Evidence); break;
                case RawReplayTerminalSettlement settlement:
                    ledger.SettleTerminal(item.Id, item.Market, settlement.ListingId, settlement.ActionId, settlement.Settlement, maximumFxAge); break;
                case RawReplayMark mark:
                    marks.Add(new(item.Id, item.Sequence, ledger.ValueWithReceivables(item.Market,
                        maximumPriceAge, maximumFxAge, purpose, mark.Dividends, mark.Terminals))); break;
                default: throw new ResearchException("raw-replay-operation");
            }
            if (item.Operation is RawReplayModelledFill { Rebalance: not null } && pending is not null && ++nextOrder == pending.Orders.Length)
                pending = null;
        }
        return new(Version, ledger.Entries, marks.ToImmutable(), ledger.Holdings.ToImmutableSortedDictionary(StringComparer.Ordinal), ledger.DividendEntitlements,
            ledger.TerminalEntitlements, ledger.CashGbp, ledger.ContributionsGbp, ledger.FeesGbp,
            ["Offline replay of supplied events; event completeness, source authenticity and legal entitlement are not established.",
             "Assumed fills remain caller assertions; modelled fills retain the separate execution/calendar/FX validation contract.",
             "Marks include explicit estimated receivables, which never fund trades or become delivered shares before settlement.",
             "Booking times and explicit sequence order govern replay; no implicit sorting, action scheduling or retroactive restatement occurs.",
             "Bound rebalance decisions recompute supplied weights against the replayed ledger; every resulting order must execute once in sequence without intervening operations. Starting assumed fills and weights remain caller assertions.",
             "This engine has no persistence; the separate verified archive can re-execute a complete replay. No registered strategy, achievable return, ISA eligibility, broker execution or training is provided."]);
    }
}
