using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed record StudyRequest(string RequestId, string DatasetId, string Name, string Purpose, decimal MonthlyGbp, ResearchCosts Costs);
public sealed record StudyEvaluationRequest(string RequestId, string PlanSha256);
public sealed record StudyRuns(string CandidateId, string BaseRunId, string StressRunId);
public sealed record PriorStudyTrial(string RunId, string DatasetId, string ConfigSha256, string Status, string? ReportSha256);
public sealed record ResearchStudy(string Id, string RequestId, string RequestSha256, string Name, string Status, DateTimeOffset CreatedAt,
    StudyPlan Plan, string PlanSha256, ImmutableArray<StudyRuns> DevelopmentRuns, ImmutableArray<PriorStudyTrial> PriorTrials,
    ImmutableArray<StudyCandidateScore> Scores, string? SelectedCandidateId = null, SearchDiagnosticReport? SelectionDiagnostics = null,
    string? ErrorCode = null, string? FinalRequestId = null, DateTimeOffset? FinalRequestedAt = null, string? FinalBaseRunId = null,
    string? FinalStressRunId = null, string? FinalBaseReportSha256 = null, string? FinalStressReportSha256 = null,
    decimal? FinalWorstMarginGbp = null, bool? FinalPassed = null);

public sealed partial class ResearchStore
{
    private const int MaximumStudies = 16, MaximumStudyPayload = 1024 * 1024;
    private static string JsonHash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    public static string StudyCodeSha256 => ResearchIdentity.Current.Sha256;
    private static bool StudyCodeMatches(StudyPlan plan) => plan.IdentityScheme == ResearchIdentity.Scheme && plan.CodeSha256 == StudyCodeSha256;
    private static void InitialiseStudies(SqliteConnection db) => Execute(db,
        "CREATE TABLE IF NOT EXISTS studies(id TEXT PRIMARY KEY,request_id TEXT NOT NULL UNIQUE,json TEXT NOT NULL,sha256 TEXT NOT NULL);" +
        "CREATE TABLE IF NOT EXISTS study_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,study_id TEXT NOT NULL,at TEXT NOT NULL,kind TEXT NOT NULL);" +
        "CREATE TABLE IF NOT EXISTS study_holdout_claims(study_id TEXT PRIMARY KEY,request_id TEXT NOT NULL UNIQUE,start_date TEXT NOT NULL,end_date TEXT NOT NULL,synthetic INTEGER NOT NULL,at TEXT NOT NULL);");
    private static ResearchStudy ReadStudy(string json, string hash)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumStudyPayload || Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))) != hash)
            throw new ResearchException("study-integrity");
        var study = JsonSerializer.Deserialize<ResearchStudy>(json, Json) ?? throw new ResearchException("study-integrity");
        using var document = JsonDocument.Parse(json);
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document.RootElement.GetProperty("plan").GetRawText()))) != study.PlanSha256)
            throw new ResearchException("study-plan-integrity");
        return study;
    }
    public List<ResearchStudy> Studies()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json,sha256 FROM studies ORDER BY rowid DESC LIMIT 16";
        using var reader = command.ExecuteReader(); var result = new List<ResearchStudy>();
        while (reader.Read()) result.Add(ReadStudy(reader.GetString(0), reader.GetString(1))); return result;
    }
    public ResearchStudy? Study(string id) => Studies().SingleOrDefault(s => s.Id == id);
    public object StudyHistory()
    {
        var runs = ListRuns();
        return Studies().Select(s => new { s.Id, s.Name, s.Status, s.CreatedAt, s.Plan.Purpose, s.Plan.Synthetic,
            s.PlanSha256, datasetId = s.Plan.DatasetSha256, s.Plan.CodeSha256, candidates = s.Plan.Candidates.Length, s.SelectedCandidateId, s.FinalPassed, s.ErrorCode,
            completedRuns = runs.Count(r => r.StudyId == s.Id && r.Status == "succeeded"), plannedRuns = s.DevelopmentRuns.Length * 2 + (s.FinalRequestId is null ? 0 : 2) }).ToArray();
    }
    public string? StudyPlanJson(string id)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json,sha256 FROM studies WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        _ = ReadStudy(reader.GetString(0), reader.GetString(1));
        using var document = JsonDocument.Parse(reader.GetString(0)); return document.RootElement.GetProperty("plan").GetRawText();
    }
    public object StudyEvents(string id)
    {
        _ = Study(id) ?? throw new ResearchException("study-not-found");
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT sequence,at,kind FROM study_events WHERE study_id=$id ORDER BY sequence LIMIT 100";
        command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader(); var result = new List<object>();
        while (reader.Read()) result.Add(new { sequence = reader.GetInt64(0), at = reader.GetString(1), kind = reader.GetString(2) }); return result;
    }
    private int ReservedStudySlots() => Studies().Count(s => s.Status is "development" or "candidate-frozen") * 2;
    private void WriteStudy(SqliteConnection db, SqliteTransaction tx, ResearchStudy study, string kind, bool insert = false)
    {
        var json = JsonSerializer.Serialize(study, Json);
        if (!insert)
        {
            // Keep the exact original plan when later DTO versions gain optional fields. A code change blocks work, not access to historical evidence.
            using var previous = db.CreateCommand(); previous.Transaction = tx; previous.CommandText = "SELECT json,sha256 FROM studies WHERE id=$id"; previous.Parameters.AddWithValue("$id", study.Id);
            using var reader = previous.ExecuteReader(); if (!reader.Read()) throw new ResearchException("study-not-found");
            var stored = ReadStudy(reader.GetString(0), reader.GetString(1));
            if (stored.PlanSha256 != study.PlanSha256) throw new ResearchException("study-plan-binding");
            using var oldDocument = JsonDocument.Parse(reader.GetString(0)); using var newDocument = JsonDocument.Parse(json);
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartObject();
                foreach (var property in newDocument.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Name == "plan") oldDocument.RootElement.GetProperty("plan").WriteTo(writer); else property.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            json = Encoding.UTF8.GetString(bytes.ToArray());
        }
        if (Encoding.UTF8.GetByteCount(json) > MaximumStudyPayload) throw new ResearchException("study-payload-capacity");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = insert ? "INSERT INTO studies(id,request_id,json,sha256) VALUES($id,$request,$json,$sha)" : "UPDATE studies SET json=$json,sha256=$sha WHERE id=$id";
        command.Parameters.AddWithValue("$id", study.Id); command.Parameters.AddWithValue("$request", study.RequestId);
        command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$sha", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
        if (command.ExecuteNonQuery() != 1) throw new ResearchException("study-not-found");
        command.CommandText = "INSERT INTO study_events(study_id,at,kind) VALUES($id,$at,$kind)";
        command.Parameters.AddWithValue("$at", clock.GetUtcNow().ToString("O")); command.Parameters.AddWithValue("$kind", kind); command.ExecuteNonQuery();
    }
    private void SaveStudy(ResearchStudy study, string kind)
    { using var db = Open(); using var tx = db.BeginTransaction(); WriteStudy(db, tx, study, kind); tx.Commit(); }
    private void InsertStudyRun(SqliteConnection db, SqliteTransaction tx, RunEntry run)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO runs(id,request_id,dataset_id,json,status) VALUES($id,$request,$dataset,$json,$status)";
        command.Parameters.AddWithValue("$id", run.Id); command.Parameters.AddWithValue("$request", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$dataset", run.DatasetId); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(run, Json));
        command.Parameters.AddWithValue("$status", run.Status); command.ExecuteNonQuery(); Event(db, tx, run.Id, "study-planned");
    }
    private RunEntry PlannedStudyRun(ResearchStudy study, StudyCandidate candidate, string stage, string commit, bool final = false)
    {
        var dataset = Datasets().Single(d => d.Id == study.Plan.DatasetSha256);
        var config = candidate.Config with { EvaluateHoldout = final };
        var costs = stage.EndsWith("stress", StringComparison.Ordinal) ? study.Plan.StressCosts : study.Plan.BaseCosts;
        return new(Guid.NewGuid().ToString("N"), dataset.Id, dataset.Name, dataset.Synthetic, "planned", clock.GetUtcNow(), null, null,
            0, null, commit, JsonHash(new { DatasetId = dataset.Id, Config = config, Costs = costs }), config, costs, false,
            StudyId: study.Id, StudyCandidateId: candidate.Id, StudyStage: stage);
    }
    public ResearchStudy RegisterStudy(StudyRequest request, string commit)
    {
        if (!Guid.TryParseExact(request.RequestId, "D", out _) || request.Name is not { Length: > 0 and <= 80 } || request.Costs is null)
            throw new ResearchException("study-request");
        var requestHash = JsonHash(new { request.DatasetId, request.Name, request.Purpose, request.MonthlyGbp, request.Costs });
        lock (gate)
        {
            var previous = Studies().SingleOrDefault(s => s.RequestId == request.RequestId);
            if (previous is not null)
            { if (previous.RequestSha256 != requestHash) throw new ResearchException("request-id-conflict"); return previous; }
            if (Studies().Count >= MaximumStudies) throw new ResearchException("study-capacity");
            var panel = Panel(request.DatasetId);
            var identity = ResearchIdentity.Current;
            var plan = StudyProtocol.Declare(panel, request.MonthlyGbp, request.Costs, request.Purpose, identity.Sha256,
                identity.Scheme, identity.BuildSdk, identity.Framework, identity.Architecture);
            if (!plan.Synthetic)
            {
                GuardStudyReservations(panel, plan.Candidates[0].Config);
                GuardPriorReservedExposure(plan.ReservedStart, plan.ReservedEnd);
                if (Studies().Any(s => !s.Plan.Synthetic && (s.FinalRequestedAt is not null || s.Status is "development" or "candidate-frozen" or "final-evaluation") &&
                    Overlap(plan.ReservedStart, plan.ReservedEnd, s.Plan.ReservedStart, s.Plan.ReservedEnd))) throw new ResearchException("study-reserved-window-conflict");
            }
            var prior = ListRuns();
            if (prior.Count + ReservedStudySlots() + plan.Candidates.Length * 2 + 2 > 1000) throw new ResearchException("trial-capacity");
            var study = new ResearchStudy(Guid.NewGuid().ToString("N"), request.RequestId, requestHash, request.Name, "development", clock.GetUtcNow(), plan, JsonHash(plan), [],
                prior.Select(r => new PriorStudyTrial(r.Id, r.DatasetId, r.ConfigSha256, r.Status, r.ReportSha256)).ToImmutableArray(), []);
            using var db = Open(); using var tx = db.BeginTransaction(); var pairs = ImmutableArray.CreateBuilder<StudyRuns>();
            foreach (var candidate in plan.Candidates)
            {
                var a = PlannedStudyRun(study, candidate, "development-base", commit); var b = PlannedStudyRun(study, candidate, "development-stress", commit);
                InsertStudyRun(db, tx, a); InsertStudyRun(db, tx, b); pairs.Add(new(candidate.Id, a.Id, b.Id));
            }
            study = study with { DevelopmentRuns = pairs.ToImmutable() }; WriteStudy(db, tx, study, "registered-before-work", true); tx.Commit(); return study;
        }
    }
    // Outcome intervals are half-open. Include all feature/training history: moving a later evaluation start cannot hide earlier training access.
    private static bool Overlap(DateOnly a, DateOnly b, DateOnly c, DateOnly d) => a < d && c < b;
    private static (DateOnly Start, DateOnly End) InformationWindow(MonthlyPanel panel, ExperimentConfig config)
    {
        config.Validate(panel);
        var reserved = panel.Dates.Length - 1 - config.HoldoutMonths;
        return (panel.Dates[0], config.EvaluateHoldout ? panel.Dates[^1] : config.DevelopmentEnd ?? panel.Dates[reserved]);
    }
    private void GuardPriorReservedExposure(DateOnly start, DateOnly end, string? ownStudy = null)
    {
        var panels = new Dictionary<string, MonthlyPanel>(StringComparer.Ordinal);
        foreach (var run in ListRuns().Where(r => !r.Synthetic && (ownStudy is null || r.StudyId != ownStudy) && (r.StartedAt is not null || r.Status is "queued" or "planned")))
        {
            if (!panels.TryGetValue(run.DatasetId, out var panel)) panels[run.DatasetId] = panel = Panel(run.DatasetId);
            var window = InformationWindow(panel, run.Config);
            if (Overlap(start, end, window.Start, window.End)) throw new ResearchException("study-known-reserved-exposure");
        }
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT start_date,end_date FROM study_holdout_claims WHERE synthetic=0 AND study_id!=$own";
        command.Parameters.AddWithValue("$own", ownStudy ?? ""); using var reader = command.ExecuteReader();
        while (reader.Read()) if (Overlap(start, end, DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture)))
            throw new ResearchException("study-holdout-spent");
    }
    private void GuardStudyReservations(MonthlyPanel panel, ExperimentConfig config, string? ownStudy = null)
    {
        if (panel.Synthetic) return;
        var window = InformationWindow(panel, config);
        if (Studies().Any(s => !s.Plan.Synthetic && (ownStudy is null || s.Id != ownStudy) && (s.Status is "development" or "candidate-frozen" or "final-evaluation") &&
            Overlap(window.Start, window.End, s.Plan.ReservedStart, s.Plan.ReservedEnd))) throw new ResearchException("study-reserved-window-conflict");
    }
    public ResearchStudy EvaluateStudy(string id, StudyEvaluationRequest request, string commit)
    {
        if (!Guid.TryParseExact(request.RequestId, "D", out _)) throw new ResearchException("study-evaluation-request");
        lock (gate)
        {
            var study = Study(id) ?? throw new ResearchException("study-not-found");
            if (study.PlanSha256 != request.PlanSha256) throw new ResearchException("study-plan-binding");
            if (study.FinalRequestId == request.RequestId) return study;
            if (study.FinalRequestedAt is not null) throw new ResearchException("study-holdout-spent");
            if (study.Status != "candidate-frozen" || study.SelectedCandidateId is null) throw new ResearchException("study-candidate-not-frozen");
            if (!StudyCodeMatches(study.Plan)) throw new ResearchException("study-code-changed");
            if (!study.Plan.Synthetic) GuardPriorReservedExposure(study.Plan.ReservedStart, study.Plan.ReservedEnd, study.Id);
            var candidate = study.Plan.Candidates.Single(c => c.Id == study.SelectedCandidateId);
            GuardStudyReservations(Panel(study.Plan.DatasetSha256), candidate.Config with { EvaluateHoldout = true }, study.Id);
            var requestedAt = clock.GetUtcNow();
            var a = PlannedStudyRun(study, candidate, "final-base", commit, true); var b = PlannedStudyRun(study, candidate, "final-stress", commit, true);
            using var db = Open(); using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "SELECT study_id FROM study_holdout_claims WHERE request_id=$request"; command.Parameters.AddWithValue("$request", request.RequestId);
            if (command.ExecuteScalar() is not null) throw new ResearchException("request-id-conflict");
            command.CommandText = "INSERT INTO study_holdout_claims VALUES($study,$request,$start,$end,$synthetic,$at)";
            command.Parameters.AddWithValue("$study", study.Id);
            command.Parameters.AddWithValue("$start", study.Plan.ReservedStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$end", study.Plan.ReservedEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$synthetic", study.Plan.Synthetic ? 1 : 0); command.Parameters.AddWithValue("$at", requestedAt.ToString("O")); command.ExecuteNonQuery();
            InsertStudyRun(db, tx, a); InsertStudyRun(db, tx, b);
            command.CommandText = "INSERT INTO holdout_access VALUES($dataset,$run,$at)";
            command.Parameters.AddWithValue("$dataset", study.Plan.DatasetSha256); command.Parameters.AddWithValue("$run", a.Id); command.ExecuteNonQuery();
            command.Parameters["$run"].Value = b.Id; command.ExecuteNonQuery();
            study = study with { Status = "final-evaluation", FinalRequestId = request.RequestId, FinalRequestedAt = requestedAt, FinalBaseRunId = a.Id, FinalStressRunId = b.Id };
            WriteStudy(db, tx, study, "holdout-spent-before-work"); tx.Commit(); return study;
        }
    }
    public ResearchStudy CancelStudy(string id)
    {
        lock (gate)
        {
            var study = Study(id) ?? throw new ResearchException("study-not-found");
            if (study.Status is not ("development" or "candidate-frozen" or "final-evaluation")) return study;
            foreach (var run in ListRuns().Where(r => r.StudyId == id)) Cancel(run.Id);
            study = study with { Status = "cancelled", ErrorCode = "user-cancelled" }; SaveStudy(study, "cancelled-no-replacement"); return study;
        }
    }
    private bool ValidateStudyStart(RunEntry run)
    {
        if (run.StudyId is null) { GuardStudyReservations(Panel(run.DatasetId), run.Config); return true; }
        var study = Study(run.StudyId) ?? throw new ResearchException("study-not-found");
        var allowed = run.StudyStage?.StartsWith("development-", StringComparison.Ordinal) == true ? study.Status == "development" :
            study.Status == "final-evaluation" && (run.Id == study.FinalBaseRunId || run.Id == study.FinalStressRunId) && study.FinalRequestedAt is not null;
        if (!allowed || !StudyCodeMatches(study.Plan))
        {
            Save(run with { Status = "cancelled", CancelRequested = true, FinishedAt = clock.GetUtcNow(), ErrorCode = "study-code-or-state-changed" }, "study-not-started");
            FailStudy(study, "study-code-or-state-changed"); return false;
        }
        GuardStudyReservations(Panel(run.DatasetId), run.Config, study.Id);
        return true;
    }
    private void ValidateStudyReport(RunEntry run, BacktestReport report)
    {
        if (run.StudyId is null) return;
        var study = Study(run.StudyId) ?? throw new ResearchException("study-not-found");
        var candidate = study.Plan.Candidates.Single(c => c.Id == run.StudyCandidateId);
        var expectedConfig = candidate.Config with { EvaluateHoldout = run.Config.EvaluateHoldout };
        var expectedCosts = run.StudyStage!.EndsWith("stress", StringComparison.Ordinal) ? study.Plan.StressCosts : study.Plan.BaseCosts;
        if (run.Config != expectedConfig || run.Costs != expectedCosts || run.DatasetId != study.Plan.DatasetSha256 ||
            report.EvaluationStart != (run.Config.EvaluateHoldout ? study.Plan.ReservedStart : study.Plan.DevelopmentStart) ||
            report.EvaluationEnd != (run.Config.EvaluateHoldout ? study.Plan.ReservedEnd : study.Plan.DevelopmentEnd) || report.ReservedPeriodStart != study.Plan.ReservedStart ||
            report.FeatureVersion != MonthlyPanel.FeatureVersion || !StudyCodeMatches(study.Plan) || report.Config != run.Config || report.Costs != run.Costs || report.DatasetSha256 != run.DatasetId || report.Synthetic != study.Plan.Synthetic ||
            (run.Config.EvaluateHoldout ? report.Scope != "reserved_holdout" || study.FinalRequestedAt is null : report.Scope != "development_walk_forward"))
            throw new ResearchException("study-report-contract");
    }
    private void FailStudy(ResearchStudy study, string code)
    {
        foreach (var run in ListRuns().Where(r => r.StudyId == study.Id && r.Status is "planned" or "queued")) Cancel(run.Id);
        SaveStudy(study with { Status = study.FinalRequestedAt is null ? "research-failed" : "final-failed", ErrorCode = code }, "failed-no-replacement");
    }
    private static DiagnosticReturns StudyStream(RunEntry run, BacktestReport report)
    {
        var candidate = report.Equity.Where(e => e.Strategy == "candidate").ToArray();
        var comparators = SearchDiagnostics.Comparators.ToImmutableDictionary(s => s, s =>
        {
            var rows = report.Equity.Where(e => e.Strategy == s).ToArray();
            if (!rows.Select(e => e.PeriodEnd).SequenceEqual(candidate.Select(e => e.PeriodEnd))) throw new ResearchException("study-comparator-grid");
            return rows.Select(e => (double)e.State.PeriodReturn).ToImmutableArray();
        });
        return new(run.Id, run.ReportSha256!, candidate.Select(e => e.PeriodEnd).ToImmutableArray(), candidate.Select(e => (double)e.State.PeriodReturn).ToImmutableArray(), comparators);
    }
    private void AdvanceStudies()
    {
        foreach (var study in Studies().Where(s => s.Status is "development" or "candidate-frozen" or "final-evaluation"))
        {
            try
            {
                if (!StudyCodeMatches(study.Plan)) { FailStudy(study, "study-code-changed"); continue; }
                if (study.Status == "candidate-frozen") continue;
                var runs = ListRuns().Where(r => r.StudyId == study.Id && (study.Status == "development" ? r.StudyStage!.StartsWith("development-", StringComparison.Ordinal) : r.Config.EvaluateHoldout)).ToArray();
                if (runs.Any(r => r.Status is "cancelled" or "failed" or "interrupted")) { FailStudy(study, "study-trial-incomplete"); continue; }
                if (runs.Any(r => r.Status != "succeeded")) continue;
                var expected = study.Status == "development" ? study.Plan.Candidates.Length * 2 : 2;
                if (runs.Length != expected) throw new ResearchException("study-family-incomplete");
                if (study.Status == "development")
                {
                    var scores = ImmutableArray.CreateBuilder<StudyCandidateScore>(); var streams = new List<DiagnosticReturns>();
                    foreach (var pair in study.DevelopmentRuns)
                    {
                        var a = runs.Single(r => r.Id == pair.BaseRunId); var b = runs.Single(r => r.Id == pair.StressRunId);
                        var ar = Report(a.Id)!; var br = Report(b.Id)!; ValidateStudyReport(a, ar); ValidateStudyReport(b, br);
                        scores.Add(StudyProtocol.Score(pair.CandidateId, a.Id, b.Id, ar, br)); streams.Add(StudyStream(a, ar)); streams.Add(StudyStream(b, br));
                    }
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    var diagnostic = SearchDiagnostics.Build(streams, deadline.Token); var frozen = StudyProtocol.Choose(scores);
                    var nominated = study.Plan.Synthetic || frozen.WorstMarginGbp > 0;
                    SaveStudy(study with { Status = nominated ? "candidate-frozen" : "no-candidate", Scores = scores.ToImmutable(),
                        SelectedCandidateId = nominated ? frozen.CandidateId : null, SelectionDiagnostics = diagnostic }, "development-selection-frozen");
                }
                else
                {
                    var a = runs.Single(r => r.Id == study.FinalBaseRunId); var b = runs.Single(r => r.Id == study.FinalStressRunId);
                    var ar = Report(a.Id)!; var br = Report(b.Id)!; ValidateStudyReport(a, ar); ValidateStudyReport(b, br);
                    var score = StudyProtocol.Score(study.SelectedCandidateId!, a.Id, b.Id, ar, br); var passed = score.WorstMarginGbp > 0;
                    SaveStudy(study with { Status = study.Plan.Synthetic ? "engineering-complete" : passed ? "forward-candidate" : "rejected",
                        FinalBaseReportSha256 = a.ReportSha256, FinalStressReportSha256 = b.ReportSha256, FinalWorstMarginGbp = score.WorstMarginGbp, FinalPassed = passed }, "final-decision-no-substitute");
                }
            }
            catch (Exception error) when (error is ResearchException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
            { FailStudy(study, error is ResearchException re ? re.Code : "study-evaluation-failed"); }
        }
    }
    public void AssertStudyExecutionRun(RunEntry run, bool live)
    {
        // Live execution requires a registered-study final. Ordinary runs may still rehearse in demo,
        // where the remaining coordinator gates exercise the plumbing without real-money exposure.
        if (run.StudyId is null)
        {
            if (live) throw new ResearchException("execution-study-required");
            return;
        }
        var study = Study(run.StudyId) ?? throw new ResearchException("study-not-found");
        if (!StudyCodeMatches(study.Plan)) throw new ResearchException("execution-study-code-changed");
        if (run.Id != study.FinalBaseRunId || run.ReportSha256 != study.FinalBaseReportSha256 ||
            (live ? study.Status != "forward-candidate" || study.Plan.Synthetic : study.Status is not ("forward-candidate" or "engineering-complete")))
            throw new ResearchException("execution-study-selection");
    }
}
