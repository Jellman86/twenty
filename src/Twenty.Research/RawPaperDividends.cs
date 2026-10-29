using System.Collections.Immutable;
using Twenty.Core.Data;
using Twenty.Core.Research;
namespace Twenty.Research;

// Payment amounts are per cutoff share; the fixed GBP fee is per entitled account.
public sealed record RawPaperDividendTerms(string ListingId, string ActionId, string ObservationId,
    RawDividendEvidence Evidence, RawStudyDividendDelivery Delivery)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RawStudyClaimMarkPolicy? MarkPolicy { get; init; }
}

public sealed partial class ResearchStore
{
    private sealed record PaperActionEvent(CorporateAction Action, DateTimeOffset At, RawReplayOperation Operation, string Key);

    private static PaperActionEvent[] PaperDividendEvents(DatasetFile file, string? mode,
        ImmutableArray<RawPaperDividendTerms> terms,
        ImmutableArray<DateTimeOffset> decisions, string datasetSha)
    {
        if (!RawPaperContract.IsDividendMode(mode))
        {
            // An explicitly supplied catalog must never be ignored, even if empty.
            if (!terms.IsDefault) throw new ResearchException("raw-paper-dividend-policy");
            return [];
        }
        var observed = file.Observations.OfType<CorporateAction>().ToArray();
        if (terms.IsDefault || terms.Length != observed.Length || terms.Length > RawEventSchedule.MaximumActions ||
            terms.Any(t => t is null || t.Evidence is null || t.Delivery is null) ||
            terms.GroupBy(t => (t.ListingId, t.ActionId)).Any(g => g.Count() != 1) ||
            terms.Select(t => t.Delivery.SourcePaymentId).Distinct(StringComparer.Ordinal).Count() != terms.Length)
            throw new ResearchException("raw-paper-dividend-policy");
        var events = new List<PaperActionEvent>();
        foreach (var term in terms)
        {
            var matches = observed.Where(a => a.ListingId == term.ListingId && a.ActionId == term.ActionId && a.Id == term.ObservationId).ToArray();
            if (matches.Length != 1) throw new ResearchException("raw-paper-dividend-policy");
            var action = matches[0];
            if (action.Evidence.AvailableAt is not { } available) throw new ResearchException("raw-paper-dividend-policy");
            if (mode == RawPaperContract.DividendMode && term.MarkPolicy is not null) throw new ResearchException("raw-paper-dividend-policy");
            var key = RawHash(System.Text.Encoding.UTF8.GetBytes(action.Id));
            var capture = new RawSchedulePoint("paper-income-" + key, 0, datasetSha);
            try { RawStudyActionTerms.Validate(new RawStudyDividendSchedule(term.ListingId, term.ActionId, term.ObservationId,
                capture, term.Evidence, term.Delivery) { MarkPolicy = term.MarkPolicy }, action, datasetSha); }
            catch (ResearchException) { throw new ResearchException("raw-paper-dividend-policy"); }
            var captured = new[] { action.EffectiveAt, available, term.Evidence.AvailableAt }.Max().ToUniversalTime();
            var paid = new[] { captured, term.Delivery.PaidAt, term.Delivery.AvailableAt }.Max().ToUniversalTime();
            // Only the claims mode can value an unpaid monthly boundary, under an already available policy.
            var firstPending = decisions.Skip(RawUniverseDecision.HistoryDecisions - 1).FirstOrDefault(at => at >= captured && at < paid);
            if (mode == RawPaperContract.DividendClaimsMode)
            {
                if (captured <= decisions[^1] && paid > decisions[^1]) throw new ResearchException("raw-paper-dividend-final-unsettled");
                if (firstPending != default && (term.MarkPolicy is not { } mark || mark.AvailableAt > firstPending))
                    throw new ResearchException("raw-paper-dividend-mark-coverage");
            }
            else if (firstPending != default) throw new ResearchException("raw-paper-dividend-unsettled");
            var p = term.Delivery;
            events.Add(new(action, captured, new RawReplayDividendCapture(term.ListingId, term.ActionId, term.Evidence), "income-capture-" + key));
            events.Add(new(action, paid, new RawReplayDividendPayment(term.ListingId, term.ActionId,
                new(p.SourcePaymentId, p.SourceReference, p.ContentSha256, p.PaidAt, p.AvailableAt, p.Currency,
                    p.GrossPerShare, p.WithheldPerShare, p.FixedFeeGbp, p.FxObservationId)), "income-payment-" + key));
        }
        if (observed.GroupBy(a => a.EffectiveAt).Any(g => g.Count() != 1) ||
            events.GroupBy(e => e.At).Any(g => g.Select(e => e.Action.Id).Distinct(StringComparer.Ordinal).Count() != 1))
            throw new ResearchException("raw-paper-dividend-ambiguous");
        return events.OrderBy(e => e.At).ThenBy(e => e.Operation is RawReplayDividendCapture ? 0 : 1).ToArray();
    }

    // Project frozen per-share terms into the existing reference policy seam; these points are not replay events.
    private static IReadOnlyDictionary<(string ListingId, string ActionId), RawActionSchedule> PaperDividendClaimCatalog(RawPaperPolicy policy) =>
        policy.Dividends.ToDictionary(t => (t.ListingId, t.ActionId), t => (RawActionSchedule)new RawStudyDividendSchedule(
            t.ListingId, t.ActionId, t.ObservationId, new("paper-claim-policy", 0, policy.DatasetSha256), t.Evidence, t.Delivery) { MarkPolicy = t.MarkPolicy });

    private static RawRebalanceClaimAssumptions PaperClaims(RawGbpLedger ledger,
        IReadOnlyDictionary<(string ListingId, string ActionId), RawActionSchedule> catalog, DateTimeOffset at, string? mode)
    {
        try { return RawStudyClaimMarks.ForLedger(ledger, catalog, at); }
        catch (ResearchException e) when (e.Code.StartsWith("raw-study-claim-", StringComparison.Ordinal))
        {
            var prefix = mode == RawPaperContract.TerminalClaimsMode ? "raw-paper-terminal-" : "raw-paper-dividend-";
            throw new ResearchException(e.Code == "raw-study-claim-capacity" ? prefix + "claim-capacity" :
                e.Code == "raw-study-claim-arithmetic" ? "raw-paper-arithmetic" : prefix + "mark-coverage");
        }
    }
}
