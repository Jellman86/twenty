using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

/// <summary>One decision point. Valuation happens at the decision; the trades that decision wants
/// are priced and booked from a later slice, because a fill at the next session's open cannot be
/// booked before that session happened. Orders are derived from the valuation at that decision, so
/// a rebalance can size itself against what the portfolio actually holds.</summary>
public sealed record RawExecutionStep(DataSlice DecisionMarket, DataSlice FillMarket,
    Func<RawGbpValuation, IReadOnlyList<PlannedOrder>> Orders);

public sealed record RawExecutionMonth(DateTimeOffset DecisionAt, decimal EquityGbp, decimal CashGbp,
    ImmutableArray<ExecutionFill> Fills, ImmutableArray<ExecutionRejection> Rejections);
public sealed record RawExecutionResult(string Version, decimal OpeningCashGbp, decimal ClosingEquityGbp,
    ImmutableArray<RawExecutionMonth> Months, ImmutableArray<string> Limitations,
    RawGbpValuation ClosingValuation, ImmutableArray<RawLedgerEntry> Entries);

/// <summary>Settles desired trades through <see cref="ExecutionPlanner"/> into a
/// <see cref="RawGbpLedger"/>, so what a strategy wanted becomes what it could actually have done.
/// It is deliberately separate from the notional engine: that one is a research accounting unit and
/// is never relabelled as executable. Valuation is raw closes with the caller's explicit FX purpose.</summary>
public static class RawExecutionRun
{
    public const string Version = "raw-execution-v2";

    public static RawExecutionResult Run(IReadOnlyList<RawExecutionStep> steps, decimal openingCashGbp,
        FillPolicy policy, decimal feeGbp, TimeSpan maximumPriceAge, TimeSpan maximumFxAge,
        FxPurpose purpose = FxPurpose.Executable)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (steps.Count == 0 || openingCashGbp <= 0 || feeGbp < 0 ||
            maximumPriceAge <= TimeSpan.Zero || maximumFxAge < TimeSpan.Zero || !Enum.IsDefined(purpose))
            throw new ResearchException("invalid-execution-run");
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] is null || steps[i].DecisionMarket is null || steps[i].FillMarket is null || steps[i].Orders is null)
                throw new ResearchException("invalid-execution-run");
            if (steps[i].FillMarket.DecisionAt < steps[i].DecisionMarket.DecisionAt)
                throw new ResearchException("raw-execution-time");
            // A ledger is strictly ordered: a later decision cannot be booked before an earlier one.
            if (i > 0 && (steps[i].DecisionMarket.DecisionAt <= steps[i - 1].DecisionMarket.DecisionAt ||
                steps[i].DecisionMarket.DecisionAt < steps[i - 1].FillMarket.DecisionAt))
                throw new ResearchException("raw-execution-time");
        }

        var ledger = new RawGbpLedger();
        ledger.Deposit("raw-execution-opening", steps[0].DecisionMarket.DecisionAt, openingCashGbp);
        var months = ImmutableArray.CreateBuilder<RawExecutionMonth>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            // Value at the decision, before it: the trades this decision wants are filled later,
            // at the next session's open, so the equity it reports is what it was deciding with.
            var value = ledger.Value(step.DecisionMarket, maximumPriceAge, maximumFxAge, purpose);
            var orders = step.Orders(value) ?? throw new ResearchException("invalid-execution-run");
            if (orders.Any(o => o is null)) throw new ResearchException("invalid-execution-run");
            if (orders.Any(o => o.DecisionAt != value.At ||
                i + 1 < steps.Count && o.NextDecisionAt > steps[i + 1].DecisionMarket.DecisionAt))
                throw new ResearchException("raw-execution-order-time");
            var plan = ExecutionPlanner.Plan(step.FillMarket, orders, policy);
            for (var f = 0; f < plan.Fills.Length; f++)
            {
                var fill = plan.Fills[f];
                ledger.Fill($"raw-execution-{i}-{f}", step.FillMarket, fill.ListingId, fill.Buy ? fill.Quantity : -fill.Quantity,
                    fill.FillPrice, PriceBasis.Raw, feeGbp, maximumFxAge);
            }
            months.Add(new(value.At, value.EquityGbp, value.CashGbp, plan.Fills, plan.Rejections));
        }
        var closing = ledger.Value(steps[^1].FillMarket, maximumPriceAge, maximumFxAge, purpose);
        return new(Version, openingCashGbp, closing.EquityGbp,
            months.ToImmutable(),
            ["Equity is raw closes marked with the caller's explicit FX purpose, not a total-return index and not achievable GBP wealth.",
             "Fills use the first observed later raw session's open; an exchange-session completeness proof is not supplied.",
             "Booking time and executable FX come from FillMarket, not independently established opening-auction execution evidence.",
             "Partial fills, order books, intraday timing, security-level taxes and settlement cycles are not modelled.",
             "Planner refusals are returned without booking; funding or holdings violations abort the run rather than returning a partial result.",
             "Terminal settlement and corporate-action scheduling are not integrated; affected holdings fail closed.",
             "This is an offline reference over supplied market data. It makes no claim that a real order would have filled, and no broker, venue or ISA eligibility is established."], closing, ledger.Entries);
    }
}
