using System.Collections.Immutable;
using Twenty.Core.Data;
using Twenty.Core.Research;
namespace Twenty.Research;

// Cash/stock amounts are per retired cutoff share; the fixed GBP fee is per entitled account.
public sealed record RawPaperTerminalTerms(string ListingId, string ActionId, string ObservationId,
    RawTerminalEvidence Evidence, RawStudyTerminalDelivery? Delivery)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RawStudyClaimMarkPolicy? MarkPolicy { get; init; }
}

public sealed partial class ResearchStore
{
    private static PaperActionEvent[] PaperTerminalEvents(DatasetFile file, string? mode,
        ImmutableArray<RawPaperTerminalTerms> terms, ImmutableArray<DateTimeOffset> decisions, string datasetSha)
    {
        if (!RawPaperContract.IsTerminalMode(mode))
        {
            if (!terms.IsDefault) throw new ResearchException("raw-paper-terminal-policy");
            return [];
        }
        var observed = file.Observations.OfType<CorporateAction>().ToArray();
        if (terms.IsDefault || terms.Length != observed.Length || terms.Length > RawEventSchedule.MaximumActions ||
            terms.Any(t => t is null || t.Evidence is null) ||
            terms.GroupBy(t => (t.ListingId, t.ActionId)).Any(g => g.Count() != 1) ||
            observed.GroupBy(a => a.ListingId).Any(g => g.Count() != 1)) throw new ResearchException("raw-paper-terminal-policy");
        var ids = terms.Where(t => t.Delivery is not null).Select(t => t.Delivery!.SourceSettlementId).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new ResearchException("raw-paper-terminal-policy");
        var events = new List<PaperActionEvent>();
        foreach (var term in terms)
        {
            var matches = observed.Where(a => a.ListingId == term.ListingId && a.ActionId == term.ActionId && a.Id == term.ObservationId).ToArray();
            if (matches.Length != 1) throw new ResearchException("raw-paper-terminal-policy");
            var action = matches[0];
            if (mode == RawPaperContract.TerminalMode && term.MarkPolicy is not null) throw new ResearchException("raw-paper-terminal-policy");
            if (action.Evidence.AvailableAt is not { } available ||
                (action.TerminalOutcome == TerminalOutcome.Zero ? term.Delivery is not null : term.Delivery is null))
                throw new ResearchException("raw-paper-terminal-policy");
            var key = RawHash(System.Text.Encoding.UTF8.GetBytes(action.Id));
            try { RawStudyActionTerms.Validate(new RawStudyTerminalSchedule(term.ListingId, term.ActionId, term.ObservationId,
                new("paper-terminal-" + key, 0, datasetSha), term.Evidence, term.Delivery) { MarkPolicy = term.MarkPolicy }, action, datasetSha); }
            catch (ResearchException) { throw new ResearchException("raw-paper-terminal-policy"); }
            var capture = new[] { action.EffectiveAt, available, term.Evidence.AvailableAt }.Max().ToUniversalTime();
            events.Add(new(action, capture, new RawReplayTerminalCapture(term.ListingId, term.ActionId, term.Evidence), "terminal-capture-" + key));
            if (term.Delivery is not { } p) continue;
            var settled = new[] { capture, p.SettledAt, p.AvailableAt }.Max().ToUniversalTime();
            var firstPending = decisions.Skip(RawUniverseDecision.HistoryDecisions - 1).FirstOrDefault(at => at >= capture && at < settled);
            if (mode == RawPaperContract.TerminalClaimsMode)
            {
                if (capture <= decisions[^1] && settled > decisions[^1]) throw new ResearchException("raw-paper-terminal-final-unsettled");
                if (firstPending != default && (term.MarkPolicy is not { } mark || mark.AvailableAt > firstPending))
                    throw new ResearchException("raw-paper-terminal-mark-coverage");
            }
            else if (firstPending != default) throw new ResearchException("raw-paper-terminal-unsettled");
            events.Add(new(action, settled, new RawReplayTerminalSettlement(term.ListingId, term.ActionId,
                new(p.SourceSettlementId, p.SourceReference, p.ContentSha256, p.SettledAt, p.AvailableAt, p.Currency,
                    p.GrossPerShare, p.WithheldPerShare, p.FixedFeeGbp, p.FxObservationId, p.SuccessorListingId, p.SuccessorSharesPerShare)), "terminal-settlement-" + key));
        }
        if (observed.GroupBy(a => a.EffectiveAt).Any(g => g.Count() != 1) ||
            events.GroupBy(e => e.At).Any(g => g.Select(e => e.Action.Id).Distinct(StringComparer.Ordinal).Count() != 1))
            throw new ResearchException("raw-paper-terminal-ambiguous");
        return events.OrderBy(e => e.At).ThenBy(e => e.Operation is RawReplayTerminalCapture ? 0 : 1).ToArray();
    }

    private static IReadOnlyDictionary<(string ListingId, string ActionId), RawActionSchedule> PaperTerminalClaimCatalog(RawPaperPolicy policy) =>
        policy.Terminals.ToDictionary(t => (t.ListingId, t.ActionId), t => (RawActionSchedule)new RawStudyTerminalSchedule(
            t.ListingId, t.ActionId, t.ObservationId, new("paper-terminal-claim-policy", 0, policy.DatasetSha256), t.Evidence, t.Delivery) { MarkPolicy = t.MarkPolicy });
}
