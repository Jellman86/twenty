namespace Twenty.Core.Research;

/// <summary>Offline final-stage calculation. The service must separately commit its permanent
/// claim and exact trial pair before invoking this reference; this helper grants no registry authority.</summary>
public static class RawStudyFinalRun
{
    public const string Version = "raw-study-final-v2";
    internal static RawStudyTrial? Trial(RawUniverseStudyPlan plan, string trialId) =>
        plan.Candidates.SelectMany(c => new[] { "base", "stress" }.Select(cost =>
            new RawStudyTrial("final:" + c.Id + ":" + cost, c.Id, cost))).SingleOrDefault(t => t.Id == trialId);

    public static RawStudyDevelopmentReport Run(RawUniverseStudyPlan plan, string candidateId, string costScenario, byte[] datasetBytes,
        IReadOnlyDictionary<string, ExecutionCalendar> calendars, byte[] actionCatalogBytes, CancellationToken ct = default, Action? checkpoint = null)
    {
        _ = RawUniverseStudyProtocol.Encode(plan);
        var trialId = "final:" + candidateId + ":" + costScenario;
        if (Trial(plan, trialId) is null) throw new ResearchException("raw-study-final-trial");
        return RawStudyDevelopmentRun.RunStage(plan, trialId, datasetBytes, calendars, actionCatalogBytes, true, ct, checkpoint);
    }
}
