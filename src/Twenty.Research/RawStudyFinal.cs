using System.Globalization;
using System.Text;
using System.Text.Json;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed partial class ResearchStore
{
    private static bool IsRawFinalTrial(RawResearchStudy study, RawStudyTrialEntry row) =>
        row.Id == study.FinalBaseTrialId || row.Id == study.FinalStressTrialId;
    private static (DateTimeOffset Start, DateTimeOffset End) RawFinalWindow(RawUniverseStudyPlan plan) =>
        (new(Math.Max(0, plan.FinalDecisions[0].UtcTicks - TimeSpan.FromDays(plan.BaseExecution.MaximumPriceAgeDays).Ticks), TimeSpan.Zero), plan.ReservedEnd);
    private static void ValidateRawFinalMetadata(RawResearchStudy study)
    {
        var spent = study.FinalRequestId is not null;
        var complete = study.Status is "engineering-complete" or "research-complete" or "research-rejected";
        if (spent != (study.FinalRequestedAt is not null) || spent != (study.FinalBaseTrialId is not null) ||
            spent != (study.FinalStressTrialId is not null) ||
            spent && (!Guid.TryParseExact(study.FinalRequestId, "D", out _) || !Guid.TryParseExact(study.FinalBaseTrialId, "N", out _) ||
                !Guid.TryParseExact(study.FinalStressTrialId, "N", out _) || study.FinalBaseTrialId == study.FinalStressTrialId ||
                study.SelectedCandidateId is null || !study.Plan.Candidates.Any(c => c.Id == study.SelectedCandidateId) || study.SelectionReportSha256 is null ||
                study.FinalRequestedAt < study.SelectionRequestedAt || study.Status is not ("final-evaluation" or "final-failed" or "cancelled" or "engineering-complete" or "research-complete" or "research-rejected")) ||
            !spent && study.Status is "final-evaluation" or "final-failed" or "engineering-complete" or "research-complete" or "research-rejected" ||
            complete != (study.FinalWorstMarginGbp is not null) || complete != (study.FinalPassed is not null) ||
            complete != (study.FinalBaseReportSha256 is not null) || complete != (study.FinalStressReportSha256 is not null) ||
            complete && (!IsSha256(study.FinalBaseReportSha256!) || !IsSha256(study.FinalStressReportSha256!) ||
                study.FinalPassed != (study.FinalWorstMarginGbp > 0) ||
                study.Status != (study.Plan.Synthetic ? "engineering-complete" : study.FinalPassed == true ? "research-complete" : "research-rejected")))
            throw new ResearchException("raw-study-final-integrity");
    }
    private void ValidateRawFinalClaim(RawResearchStudy study)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT c.request_id,c.start_date,c.end_date,c.synthetic,c.at,w.start_at,w.end_at FROM study_holdout_claims c LEFT JOIN universe_holdout_windows w ON w.study_id=c.study_id WHERE c.study_id=$id";
        cmd.Parameters.AddWithValue("$id", study.Id); using var reader = cmd.ExecuteReader();
        if (!reader.Read()) { if (study.FinalRequestId is not null) throw new ResearchException("raw-study-final-claim-integrity"); return; }
        if (study.FinalRequestId is null || reader.GetString(0) != study.FinalRequestId ||
            reader.GetString(1) != Day(study.Plan.ReservedStart).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ||
            reader.GetString(2) != CeilingDay(study.Plan.ReservedEnd).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ||
            reader.GetInt64(3) != (study.Plan.Synthetic ? 1 : 0) || reader.GetString(4) != study.FinalRequestedAt!.Value.ToString("O") ||
            reader.IsDBNull(5) || reader.IsDBNull(6) || reader.GetString(5) != study.Plan.ReservedStart.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ||
            reader.GetString(6) != study.Plan.ReservedEnd.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            throw new ResearchException("raw-study-final-claim-integrity");
    }
    public RawResearchStudy EvaluateRawStudy(string id, StudyEvaluationRequest request, string commit)
    {
        if (request is null || !Guid.TryParseExact(request.RequestId, "D", out _)) throw new ResearchException("study-evaluation-request");
        lock (gate)
        {
            var study = RawStudies().SingleOrDefault(s => s.Id == id) ?? throw new ResearchException("study-not-found");
            if (request.PlanSha256 != study.PlanSha256) throw new ResearchException("raw-study-plan-integrity");
            _ = ListRawTrials();
            if (study.FinalRequestId == request.RequestId) return study;
            if (study.FinalRequestId is not null) throw new ResearchException("study-holdout-spent");
            if (study.Status != "candidate-frozen" || study.SelectedCandidateId is null) throw new ResearchException("study-candidate-not-frozen");
            CurrentRawPlan(study); _ = RawStudySelectionBytes(id);
            if (!study.Plan.Synthetic)
            {
                GuardPriorReservedExposure(study.Plan.ReservedStart, study.Plan.ReservedEnd, id);
                var window = RawFinalWindow(study.Plan); GuardStudyReservations(window.Start, window.End, id);
            }
            // This operation reads only retained metadata; original market values are opened by the worker after commit.
            var at = clock.GetUtcNow(); var windowFinal = RawFinalWindow(study.Plan);
            var rows = new[] { "base", "stress" }.Select(cost =>
            {
                var trialId = "final:" + study.SelectedCandidateId + ":" + cost;
                var bytes = RawBytes(new RawStudyTrialRequest(id, study.PlanSha256, trialId, "final"));
                var row = new RawStudyTrialEntry(Guid.NewGuid().ToString("N"), id, trialId, study.SelectedCandidateId, cost,
                    "planned", at, study.Plan.DatasetSha256, study.Plan.Synthetic, study.Plan.CodeSha256, RawHash(bytes), windowFinal.Start, windowFinal.End)
                    { QueuedAt = at };
                return (Row: row, Bytes: bytes);
            }).ToArray();
            using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT study_id FROM study_holdout_claims WHERE request_id=$request"; cmd.Parameters.AddWithValue("$request", request.RequestId);
            if (cmd.ExecuteScalar() is not null) throw new ResearchException("request-id-conflict");
            cmd.CommandText = "INSERT INTO study_holdout_claims VALUES($study,$request,$start,$end,$synthetic,$at)";
            cmd.Parameters.AddWithValue("$study", id); cmd.Parameters.AddWithValue("$start", Day(study.Plan.ReservedStart).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$end", CeilingDay(study.Plan.ReservedEnd).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$synthetic", study.Plan.Synthetic ? 1 : 0); cmd.Parameters.AddWithValue("$at", at.ToString("O")); cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO universe_holdout_windows VALUES($study,$from,$until)";
            cmd.Parameters.AddWithValue("$from", study.Plan.ReservedStart.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$until", study.Plan.ReservedEnd.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); cmd.ExecuteNonQuery();
            foreach (var item in rows)
            {
                using var insert = db.CreateCommand(); insert.Transaction = tx; insert.CommandText = "INSERT INTO raw_trials VALUES($id,$study,$json,'planned')";
                insert.Parameters.AddWithValue("$id", item.Row.Id); insert.Parameters.AddWithValue("$study", id);
                insert.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(RawBytes(item.Row))); insert.ExecuteNonQuery();
                EnsureArtifactCapacity(item.Bytes); var path = Path.Combine(Root, "runs", item.Row.Id); Directory.CreateDirectory(path);
                ImmutableWrite(Path.Combine(path, "raw-study-request.json"), item.Bytes);
            }
            study = study with { Status = "final-evaluation", FinalRequestId = request.RequestId, FinalRequestedAt = at,
                FinalBaseTrialId = rows[0].Row.Id, FinalStressTrialId = rows[1].Row.Id };
            WriteRawFamily(db, tx, study, [], "holdout-spent-before-work"); tx.Commit(); return study;
        }
    }
    private RawResearchStudy CompleteRawFinal(RawResearchStudy study, RawStudyTrialEntry current, RawStudyDevelopmentReport report)
    {
        var other = ListRawTrials().Single(t => t.StudyId == study.Id && IsRawFinalTrial(study, t) && t.Id != current.Id);
        if (other.Status != "succeeded") return study;
        var otherReport = JsonSerializer.Deserialize<RawStudyDevelopmentReport>(RawTrialReportBytes(other.Id), RawReplayJson.Options) ?? throw new ResearchException("raw-study-report-integrity");
        static decimal Margin(RawStudyDevelopmentReport value) => value.Strategies.Single(s => s.Strategy == "momentum").EndingEquityGbp -
            Math.Max(value.Strategies.Single(s => s.Strategy == "equal_weight").EndingEquityGbp, value.Strategies.Single(s => s.Strategy == "cash").EndingEquityGbp);
        var worst = Math.Min(Margin(report), Margin(otherReport)); var passed = worst > 0;
        return study with { Status = study.Plan.Synthetic ? "engineering-complete" : passed ? "research-complete" : "research-rejected",
            FinalBaseReportSha256 = current.Id == study.FinalBaseTrialId ? current.ReportSha256 : other.ReportSha256,
            FinalStressReportSha256 = current.Id == study.FinalStressTrialId ? current.ReportSha256 : other.ReportSha256,
            FinalWorstMarginGbp = worst, FinalPassed = passed };
    }
}
