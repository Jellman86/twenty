using System.Text;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed record RawStudyStartRequest(string RequestId, string PlanSha256);

public sealed partial class ResearchStore
{
    private static bool RawReservationActive(RawResearchStudy study) => study.Status is "registered" or "development" or "development-complete" or "candidate-frozen";
    private static bool RawTrialStateValid(RawResearchStudy study, RawStudyTrialEntry row)
    {
        if (row.Status is not ("planned" or "started" or "succeeded" or "failed" or "interrupted" or "cancelled")) return false;
        if ((study.StartRequestId is null) != (study.StartRequestedAt is null) ||
            study.StartRequestId is not null && !Guid.TryParseExact(study.StartRequestId, "D", out _)) return false;
        if (study.StartRequestedAt is null) return study.Status is "registered" or "cancelled" &&
            row.Status == (study.Status == "registered" ? "planned" : "cancelled") && row.QueuedAt is null && row.StartedAt is null && row.ReportSha256 is null;
        if (row.QueuedAt != study.StartRequestedAt || row.QueuedAt < row.CreatedAt ||
            row.StartedAt < row.QueuedAt || row.FinishedAt < row.StartedAt) return false;
        if (row.Status == "planned" && (row.StartedAt is not null || row.FinishedAt is not null)) return false;
        if (row.Status is "started" or "succeeded" or "failed" or "interrupted" && (row.StartedAt is null || row.WorkerCommit is null)) return false;
        if (row.Status == "started" && row.FinishedAt is not null || row.Status is "succeeded" or "failed" or "interrupted" or "cancelled" && row.FinishedAt is null) return false;
        if ((row.Status == "succeeded") != (row.ReportSha256 is not null) || row.ReportSha256 is not null && !IsSha256(row.ReportSha256)) return false;
        return study.Status switch
        {
            "development" => row.Status is "planned" or "started" or "succeeded",
            "development-complete" or "candidate-frozen" or "no-candidate" => row.Status == "succeeded",
            "failed" => row.Status is "succeeded" or "failed" or "interrupted" or "cancelled",
            "cancelled" => row.Status is "succeeded" or "failed" or "interrupted" or "cancelled" || row.Status == "started" && row.CancelRequested,
            _ => false
        };
    }
    private void SaveRawFamily(RawResearchStudy study, IEnumerable<RawStudyTrialEntry> trials, string kind)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        foreach (var row in trials)
        {
            using var update = db.CreateCommand(); update.Transaction = tx;
            update.CommandText = "UPDATE raw_trials SET json=$json,status=$status WHERE id=$id";
            update.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(RawBytes(row))); update.Parameters.AddWithValue("$status", row.Status);
            update.Parameters.AddWithValue("$id", row.Id); if (update.ExecuteNonQuery() != 1) throw new ResearchException("raw-trial-integrity");
        }
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        var bytes = RawBytes(study); cmd.CommandText = "UPDATE raw_studies SET json=$json,sha256=$sha WHERE id=$id";
        cmd.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(bytes)); cmd.Parameters.AddWithValue("$sha", RawHash(bytes));
        cmd.Parameters.AddWithValue("$id", study.Id); if (cmd.ExecuteNonQuery() != 1) throw new ResearchException("raw-study-integrity");
        cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO raw_study_events(study_id,at,kind) VALUES($id,$at,$kind)";
        cmd.Parameters.AddWithValue("$id", study.Id); cmd.Parameters.AddWithValue("$at", clock.GetUtcNow().ToString("O"));
        cmd.Parameters.AddWithValue("$kind", kind); cmd.ExecuteNonQuery(); tx.Commit();
    }
    private static void CurrentRawPlan(RawResearchStudy study)
    {
        _ = RawUniverseStudyProtocol.Decode(Convert.FromBase64String(study.PlanBase64));
        if (study.Plan.CodeSha256 != ResearchIdentity.Current.Sha256 || study.Plan.IdentityScheme != ResearchIdentity.Scheme)
            throw new ResearchException("study-code-changed");
    }
    public RawResearchStudy StartRawStudy(string id, RawStudyStartRequest request)
    {
        if (request is null || !Guid.TryParseExact(request.RequestId, "D", out _)) throw new ResearchException("raw-study-request");
        lock (gate)
        {
            var study = RawStudies().SingleOrDefault(s => s.Id == id) ?? throw new ResearchException("study-not-found");
            if (request.PlanSha256 != study.PlanSha256) throw new ResearchException("raw-study-plan-integrity");
            if (study.StartRequestId == request.RequestId) return study;
            if (study.StartRequestId is not null) throw new ResearchException("raw-study-already-started");
            if (study.Status != "registered") throw new ResearchException("raw-study-state");
            CurrentRawPlan(study);
            var trials = ListRawTrials().Where(t => t.StudyId == id).ToArray();
            study = study with { Status = "development", StartRequestId = request.RequestId, StartRequestedAt = clock.GetUtcNow() };
            SaveRawFamily(study, trials.Select(t => t with { QueuedAt = study.StartRequestedAt }), "development-start-requested");
            return study;
        }
    }
    private string RawReportPath(string id) => Path.Combine(Root, "runs", id, "raw-study-report.json");
    public byte[] RawTrialReportBytes(string id)
    {
        lock (gate)
        {
            var row = ListRawTrials().SingleOrDefault(t => t.Id == id) ?? throw new ResearchException("raw-trial-not-found");
            if (row.Status != "succeeded" || row.ReportSha256 is null) throw new ResearchException("raw-study-report-unavailable");
            var bytes = DatasetArchive.ReadBounded(RawReportPath(id));
            if (bytes.Length > RawStudyDevelopmentRun.MaximumReportBytes || RawHash(bytes) != row.ReportSha256) throw new ResearchException("raw-study-report-integrity");
            var report = JsonSerializer.Deserialize<RawStudyDevelopmentReport>(bytes, RawReplayJson.Options) ?? throw new ResearchException("raw-study-report-integrity");
            var study = RawStudies().Single(s => s.Id == row.StudyId);
            if (report.PlanSha256 != study.PlanSha256 || report.TrialId != row.TrialId || report.DatasetSha256 != row.DatasetSha256)
                throw new ResearchException("raw-study-report-integrity");
            return bytes;
        }
    }
    private void FinishRawFailure(string id, string status, string code)
    {
        lock (gate)
        {
            var rows = ListRawTrials(); var row = rows.Single(t => t.Id == id); var study = RawStudies().Single(s => s.Id == row.StudyId);
            var at = clock.GetUtcNow();
            var cancelled = study.Status == "cancelled";
            SaveRawFamily(study with { Status = cancelled ? "cancelled" : "failed" }, rows.Where(t => t.StudyId == study.Id).Select(t =>
                t.Id == id ? t with { Status = cancelled ? "cancelled" : status, FinishedAt = at, ErrorCode = code } :
                t.Status == "planned" ? t with { Status = "cancelled", FinishedAt = at, ErrorCode = "family-stopped" } : t), "development-" + status);
        }
    }
    private void InterruptStaleRawRuns()
    {
        foreach (var row in ListRawTrials().Where(t => t.Status == "started")) FinishRawFailure(row.Id, "interrupted", "process-restarted");
    }
    public bool ProcessNextRawStudyRun(string commit, CancellationToken ct = default)
    {
        if (!TryBeginComputation()) return false;
        try
        {
            RawStudyTrialEntry row; RawResearchStudy study;
            lock (gate)
            {
                var pending = ListRawTrials();
                if (pending.Any(t => t.Status == "started")) return false;
                var next = pending.Where(t => t.Status == "planned" && t.QueuedAt is not null).OrderBy(t => t.QueuedAt)
                    .ThenBy(t => t.CreatedAt).ThenBy(t => t.TrialId, StringComparer.Ordinal).FirstOrDefault();
                if (next is null) return false;
                if (ListRuns().Any(r => r.Status is "queued" or "planned" && r.CreatedAt <= next.QueuedAt) ||
                    ListUniverseRuns().Any(r => r.Status == "planned" && r.CreatedAt <= next.QueuedAt)) return false;
                study = RawStudies().Single(s => s.Id == next.StudyId);
                row = next with { Status = "started", StartedAt = clock.GetUtcNow(), WorkerCommit = commit };
                SaveRawFamily(study, [row], "trial-started");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                CurrentRawPlan(study); _ = RawTrialRequestBytes(row.Id);
                if (!row.Synthetic) { lock (gate) { var window = RawDevelopmentWindow(study.Plan); GuardStudyReservations(window.Start, window.End, study.Id); } }
                var originals = study.Originals.Select(o => (Original: o, Bytes: RawStudyOriginalBytes(study.Id, o.OriginalSha256))).ToArray();
                var dataset = originals.Single(i => i.Original.Kind == "dataset").Bytes;
                var actionBytes = originals.Single(i => i.Original.Kind == "actions").Bytes;
                var calendars = originals.Where(i => i.Original.Kind == "calendar").Select(i => RawStudyEvidence.Calendar(i.Bytes))
                    .ToDictionary(c => c.Reference.Id, StringComparer.Ordinal);
                void Checkpoint()
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    lock (gate) if (ListRawTrials().Single(t => t.Id == row.Id).CancelRequested) deadline.Cancel();
                    deadline.Token.ThrowIfCancellationRequested();
                }
                Checkpoint();
                var report = RawStudyDevelopmentRun.Run(study.Plan, row.TrialId, dataset, calendars, actionBytes, deadline.Token, Checkpoint);
                var bytes = RawStudyDevelopmentRun.EncodeReport(report);
                _ = RawStudyDevelopmentRun.Verify(bytes, sha => sha == row.DatasetSha256 ? dataset : throw new ResearchException("raw-replay-dataset-hash"), deadline.Token);
                lock (gate)
                {
                    Checkpoint(); var current = ListRawTrials().Single(t => t.Id == row.Id);
                    var parent = RawStudies().Single(s => s.Id == study.Id);
                    if (parent.Status != "development") throw new OperationCanceledException();
                    EnsureArtifactCapacity(bytes); ImmutableWrite(RawReportPath(row.Id), bytes);
                    current = current with { Status = "succeeded", FinishedAt = clock.GetUtcNow(), ReportSha256 = RawHash(bytes) };
                    if (ListRawTrials().Where(t => t.StudyId == study.Id && t.Id != row.Id).All(t => t.Status == "succeeded")) parent = parent with { Status = "development-complete" };
                    SaveRawFamily(parent, [current], "trial-succeeded");
                }
            }
            catch (OperationCanceledException) { FinishRawFailure(row.Id, "interrupted", "cancelled-deadline-or-shutdown"); }
            catch (Exception error) when (error is ResearchException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or OverflowException or Microsoft.Data.Sqlite.SqliteException)
            { FinishRawFailure(row.Id, "failed", error is ResearchException re ? re.Code : "raw-study-execution-failed"); }
            return true;
        }
        finally { EndComputation(); }
    }
}
