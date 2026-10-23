using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public abstract record RawReplayOperation;
public sealed record RawReplayDeposit(decimal AmountGbp) : RawReplayOperation;
// The name deliberately preserves the weaker evidence contract of a caller-supplied fill.
public sealed record RawReplayAssumedFill(string ListingId, decimal SignedQuantity, decimal RawPrice,
    PriceBasis Basis, decimal FeeGbp) : RawReplayOperation;
public sealed record RawReplayModelledFill(PlannedOrder Order, FillPolicy Policy, ExecutionFill Fill, decimal FeeGbp, ExecutionCalendar Calendar) : RawReplayOperation;
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
/// This does not discover, schedule, authenticate or persist events, nor generate orders.</summary>
public static class RawEventReplay
{
    public const string Version = "raw-event-replay-v1";
    public static RawEventReplayResult Run(IReadOnlyList<RawReplayItem> events, TimeSpan maximumPriceAge,
        TimeSpan maximumFxAge, FxPurpose purpose)
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
        foreach (var item in input)
        {
            switch (item.Operation)
            {
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
                    if (item.Sequence > 0 && input[item.Sequence - 1].Market.DecisionAt > execution.Timing.Session.OpenAt)
                        throw new ResearchException("raw-replay-execution-time");
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
        }
        return new(Version, ledger.Entries, marks.ToImmutable(), ledger.Holdings.ToImmutableSortedDictionary(StringComparer.Ordinal), ledger.DividendEntitlements,
            ledger.TerminalEntitlements, ledger.CashGbp, ledger.ContributionsGbp, ledger.FeesGbp,
            ["Offline replay of supplied events; event completeness, source authenticity and legal entitlement are not established.",
             "Assumed fills remain caller assertions; modelled fills retain the separate execution/calendar/FX validation contract.",
             "Marks include explicit estimated receivables, which never fund trades or become delivered shares before settlement.",
             "Booking times and explicit sequence order govern replay; no implicit sorting, action scheduling or retroactive restatement occurs.",
             "No durable restart, registered strategy, achievable return, ISA eligibility, broker execution or training is provided."]);
    }
}
