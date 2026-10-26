using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawStudyClaimMarkPolicy(string? Currency, decimal ExpectedWithheldPerShare, decimal ExpectedFeeGbp,
    DateTimeOffset AvailableAt, string SourceReference, string ContentSha256, string AllocationRule);

public static class RawStudyClaimMarks
{
    public const string AllocationRule = "raw-study-claim-mark-per-share-fixed-gbp-fee-v1";
    public static RawStudyClaimMarkPolicy? Policy(RawActionSchedule entry) => entry switch
    { RawStudyDividendSchedule d => d.MarkPolicy, RawStudyTerminalSchedule t => t.MarkPolicy, _ => null };

    public static void Validate(RawActionSchedule entry, CorporateAction observed)
    {
        if (Policy(entry) is not { } policy) return;
        if (policy.AllocationRule != AllocationRule || policy.AvailableAt == default || string.IsNullOrWhiteSpace(policy.SourceReference) ||
            policy.SourceReference.Length > 200 || policy.ContentSha256 is not { Length: 64 } || !policy.ContentSha256.All(char.IsAsciiHexDigitLower) ||
            policy.ExpectedWithheldPerShare < 0 || policy.ExpectedFeeGbp < 0 ||
            (observed.Action == CorporateActionKind.Dividend || observed.TerminalOutcome == TerminalOutcome.Cash
                ? policy.Currency != observed.CashCurrency || policy.ExpectedWithheldPerShare > observed.CashPerShare
                : observed.TerminalOutcome != TerminalOutcome.Stock || policy.Currency is not null || policy.ExpectedWithheldPerShare != 0))
            throw new ResearchException("raw-study-claim-policy");
    }

    // Only declared timing/policy metadata is consulted. No price, ownership or future return is computed.
    public static void ValidateCoverage(RawUniverseStudyPlan plan, RawActionSchedule entry, CorporateAction observed)
    {
        var proofAt = entry switch { RawStudyDividendSchedule d => d.Evidence.AvailableAt, RawStudyTerminalSchedule t => t.Evidence.AvailableAt, _ => (DateTimeOffset?)null };
        if (proofAt is null || observed.TerminalOutcome == TerminalOutcome.Zero) return;
        var capture = new[] { observed.EffectiveAt, observed.Evidence.AvailableAt, proofAt.Value }.Max();
        var delivered = entry switch
        {
            RawStudyDividendSchedule { Delivery: { } d } => new[] { capture, d.PaidAt, d.AvailableAt }.Max(),
            RawStudyTerminalSchedule { Delivery: { } t } => new[] { capture, t.SettledAt, t.AvailableAt }.Max(),
            _ => DateTimeOffset.MaxValue
        };
        foreach (var (start, end, decisions) in new[] { (plan.DevelopmentStart, plan.DevelopmentEnd, plan.DevelopmentDecisions),
                     (plan.ReservedStart, plan.ReservedEnd, plan.FinalDecisions) })
        {
            if (observed.EffectiveAt < start || observed.EffectiveAt > end || observed.Evidence.AvailableAt > end) continue;
            var first = decisions.Skip(RawUniverseDecision.HistoryDecisions).FirstOrDefault(at => at >= capture && at < delivered);
            if (first != default && (Policy(entry) is not { } policy || policy.AvailableAt > first))
                throw new ResearchException("raw-study-claim-policy-coverage");
        }
    }

    public static RawRebalanceClaimAssumptions ForLedger(RawGbpLedger ledger,
        IReadOnlyDictionary<(string ListingId, string ActionId), RawActionSchedule> catalog, DateTimeOffset at)
    {
        var dividends = ledger.DividendEntitlements.Where(c => c.PaidAt is null).ToArray();
        var terminals = ledger.TerminalEntitlements.Where(c => c.SettledAt is null).ToArray();
        if ((long)dividends.Length + terminals.Length > RawRebalancePlanner.MaximumPendingClaims)
            throw new ResearchException("raw-study-claim-capacity");
        RawStudyClaimMarkPolicy Get(string listing, string action, string missing)
        {
            if (!catalog.TryGetValue((listing, action), out var entry) || Policy(entry) is not { } policy) throw new ResearchException(missing);
            if (policy.AvailableAt > at) throw new ResearchException("raw-study-claim-policy-unavailable");
            return policy;
        }
        // The sole per-share -> total seam. Ledger/planner/replay only receive these total-amount assumptions.
        return new(dividends.Select(c =>
        {
            var p = Get(c.ListingId, c.ActionId, "raw-ledger-dividend-unpaid");
            return new RawDividendMarkAssumption(c.ListingId, c.ActionId, p.Currency!, p.ExpectedWithheldPerShare * c.Quantity,
                p.ExpectedFeeGbp, p.AvailableAt, p.SourceReference, p.ContentSha256);
        }).ToImmutableArray(), terminals.Select(c =>
        {
            var p = Get(c.Action.ListingId, c.Action.ActionId, "raw-ledger-terminal-unsettled");
            return new RawTerminalMarkAssumption(c.Action.ListingId, c.Action.ActionId, p.Currency, p.ExpectedWithheldPerShare * c.Quantity,
                p.ExpectedFeeGbp, p.AvailableAt, p.SourceReference, p.ContentSha256);
        }).ToImmutableArray());
    }
}
