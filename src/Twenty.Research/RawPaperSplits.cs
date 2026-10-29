using Twenty.Core.Data;
using Twenty.Core.Research;
namespace Twenty.Research;

public sealed partial class ResearchStore
{
    private static CorporateAction[] ValidatePaperActions(DatasetFile file, string? mode, string datasetSha)
    {
        var actions = file.Observations.OfType<CorporateAction>().ToArray();
        if (mode is null)
        {
            if (actions.Length != 0) throw new ResearchException("raw-paper-actions-unsupported");
            return [];
        }
        if (RawPaperContract.IsTerminalMode(mode))
        {
            if (actions.Length > RawEventSchedule.MaximumActions) throw new ResearchException("raw-paper-capacity");
            if (actions.Any(a => a.Action is not (CorporateActionKind.Delisting or CorporateActionKind.Merger))) throw new ResearchException("raw-paper-actions-unsupported");
            return actions;
        }
        if (RawPaperContract.IsDividendMode(mode))
        {
            if (actions.Length > RawEventSchedule.MaximumActions) throw new ResearchException("raw-paper-capacity");
            if (actions.Any(a => a.Action != CorporateActionKind.Dividend)) throw new ResearchException("raw-paper-actions-unsupported");
            if (actions.GroupBy(a => (a.ListingId, a.ActionId)).Any(g => g.Count() != 1)) throw new ResearchException("raw-paper-dividend-policy");
            return actions;
        }
        if (mode != RawPaperContract.SplitMode) throw new ResearchException("raw-paper-policy");
        if (actions.Length > RawEventSchedule.MaximumActions) throw new ResearchException("raw-paper-capacity");
        if (actions.GroupBy(a => (a.ListingId, a.ActionId)).Any(g => g.Count() != 1)) throw new ResearchException("raw-paper-split-vintage");
        for (var i = 0; i < actions.Length; i++)
        {
            var action = actions[i];
            if (action.Action != CorporateActionKind.Split) throw new ResearchException("raw-paper-actions-unsupported");
            try { RawStudyActionTerms.Validate(new RawSplitSchedule(action.ListingId, action.ActionId, action.Id,
                new("paper-split-" + i, i, datasetSha)), action, datasetSha); }
            catch (ResearchException) { throw new ResearchException("raw-paper-actions-unsupported"); }
            if (action.Evidence.AvailableAt is null) throw new ResearchException("raw-paper-split-vintage");
        }
        // The reference refuses overlapping unbooked actions. Do not invent a same-instant action order.
        if (actions.GroupBy(a => a.EffectiveAt).Any(g => g.Count() != 1) || actions.GroupBy(SplitBooking).Any(g => g.Count() != 1))
            throw new ResearchException("raw-paper-split-ambiguous");
        return actions.OrderBy(SplitBooking).ThenBy(a => a.Id, StringComparer.Ordinal).ToArray();
    }
    private static DateTimeOffset SplitBooking(CorporateAction action) =>
        (action.Evidence.AvailableAt!.Value > action.EffectiveAt ? action.Evidence.AvailableAt.Value : action.EffectiveAt).ToUniversalTime();
}
