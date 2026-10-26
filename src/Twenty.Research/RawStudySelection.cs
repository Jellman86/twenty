using System.Collections.Immutable;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed record RawStudySelectionRequest(string RequestId,string PlanSha256);
public sealed record RawStudySelectionInput(string RegistryTrialId,string TrialId,string RequestSha256,string ReportSha256);
public sealed record RawStudySelectionArtifact(string Version,string StudyId,string RequestId,DateTimeOffset At,string WorkerCommit,
    ImmutableArray<RawStudySelectionInput> Inputs,RawStudySelectionResult Result);

public sealed partial class ResearchStore
{
    private string RawSelectionPath(string sha) => Path.Combine(Root,"raw-study-selections",sha+".json");
    public byte[] RawStudySelectionBytes(string id)
    {
        lock(gate)
        {
            var study=RawStudies().SingleOrDefault(s=>s.Id==id)??throw new ResearchException("study-not-found");
            if(study.SelectionReportSha256 is null) throw new ResearchException("raw-study-selection-unavailable");
            var bytes=DatasetArchive.ReadBounded(RawSelectionPath(study.SelectionReportSha256));
            if(bytes.Length>MaximumStudyPayload || RawHash(bytes)!=study.SelectionReportSha256) throw new ResearchException("raw-study-selection-integrity");
            var artifact=JsonSerializer.Deserialize<RawStudySelectionArtifact>(bytes,RawReplayJson.Options)??throw new ResearchException("raw-study-selection-integrity");
            var inputs=ListRawTrials().Where(t=>t.StudyId==id).OrderBy(t=>t.TrialId,StringComparer.Ordinal)
                .Select(t=>new RawStudySelectionInput(t.Id,t.TrialId,t.RequestSha256,t.ReportSha256!)).ToImmutableArray();
            if(artifact.Version!=RawStudySelection.Version || artifact.StudyId!=id || artifact.RequestId!=study.SelectionRequestId || artifact.At!=study.SelectionRequestedAt ||
                artifact.Result.PlanSha256!=study.PlanSha256 || artifact.Result.SelectedCandidateId!=study.SelectedCandidateId ||
                !inputs.SequenceEqual(artifact.Inputs)) throw new ResearchException("raw-study-selection-integrity");
            return bytes;
        }
    }
    public RawResearchStudy SelectRawStudy(string id,RawStudySelectionRequest request,string commit)
    {
        if(request is null || !Guid.TryParseExact(request.RequestId,"D",out _)) throw new ResearchException("raw-study-request");
        lock(gate)
        {
            var study=RawStudies().SingleOrDefault(s=>s.Id==id)??throw new ResearchException("study-not-found");
            if(request.PlanSha256!=study.PlanSha256) throw new ResearchException("raw-study-plan-integrity");
            if(study.SelectionRequestId==request.RequestId) { _=RawStudySelectionBytes(id);return study; }
            if(study.SelectionRequestId is not null) throw new ResearchException("raw-study-selection-frozen");
            if(study.Status!="development-complete") throw new ResearchException("raw-study-selection-not-ready");
            CurrentRawPlan(study);
            // These exact immutable artifacts were independently replayed before worker publication.
            // Selection is metadata/wealth arithmetic and opens no original dataset or final-window observation.
            var rows=ListRawTrials().Where(t=>t.StudyId==id).OrderBy(t=>t.TrialId,StringComparer.Ordinal).ToArray();
            if(rows.Length!=8 || rows.Any(t=>t.Status!="succeeded")) throw new ResearchException("raw-study-selection-family");
            var observations=new List<RawStudySelectionObservation>();
            foreach(var row in rows)
            {
                _=RawTrialRequestBytes(row.Id);
                var report=JsonSerializer.Deserialize<RawStudyDevelopmentReport>(RawTrialReportBytes(row.Id),RawReplayJson.Options)??throw new ResearchException("raw-study-report-integrity");
                if(report.Version!=RawStudyDevelopmentRun.Version || report.Strategies.IsDefault ||
                    !report.Strategies.Select(s=>s.Strategy).Order(StringComparer.Ordinal).SequenceEqual(new[]{"cash","equal_weight","momentum"}) ||
                    report.Strategies.Any(s=>s.Months.Length!=study.Plan.DevelopmentContributions.Length || s.ContributionsGbp!=study.Plan.DevelopmentContributions.Sum(c=>c.AmountGbp)))
                    throw new ResearchException("raw-study-report-integrity");
                decimal Equity(string strategy)=>report.Strategies.Single(s=>s.Strategy==strategy).EndingEquityGbp;
                observations.Add(new(row.TrialId,row.ReportSha256!,Equity("momentum"),Equity("equal_weight"),Equity("cash")));
            }
            var result=RawStudySelection.Evaluate(study.Plan,observations);var at=clock.GetUtcNow();
            var artifact=new RawStudySelectionArtifact(RawStudySelection.Version,id,request.RequestId,at,commit,
                rows.Select(t=>new RawStudySelectionInput(t.Id,t.TrialId,t.RequestSha256,t.ReportSha256!)).ToImmutableArray(),result);
            var bytes=RawBytes(artifact);if(bytes.Length>MaximumStudyPayload) throw new ResearchException("study-payload-capacity");
            EnsureArtifactCapacity(bytes);var sha=RawHash(bytes);Directory.CreateDirectory(Path.GetDirectoryName(RawSelectionPath(sha))!);ImmutableWrite(RawSelectionPath(sha),bytes);
            study=study with { Status=result.SelectedCandidateId is null?"no-candidate":"candidate-frozen",SelectionRequestId=request.RequestId,
                SelectionRequestedAt=at,SelectionReportSha256=sha,SelectedCandidateId=result.SelectedCandidateId };
            SaveRawFamily(study,[],"development-selection-frozen");return study;
        }
    }
}
