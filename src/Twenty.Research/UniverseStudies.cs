using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed record UniverseStudyRequest(string RequestId, string Name, string Purpose, string DatasetSha256,
    string UniverseId, ImmutableArray<DateTimeOffset> Decisions, int MaximumPriceAgeDays, decimal MonthlyGbp, ResearchCosts Costs);
public sealed record UniverseStudyRuns(string CandidateId, string BaseRunId, string StressRunId);
public sealed record UniverseStudyScore(string CandidateId, string BaseRunId, string StressRunId,
    decimal BaseEndingNotional, decimal StressEndingNotional, decimal BaseComparatorNotional, decimal StressComparatorNotional,
    decimal WorstMarginNotional);
public sealed record UniverseResearchStudy(string Id, string RequestId, string RequestSha256, string Name, string Status,
    DateTimeOffset CreatedAt, UniverseStudyPlan Plan, string PlanSha256, ImmutableArray<UniverseStudyRuns> DevelopmentRuns,
    ImmutableArray<PriorStudyTrial> PriorTrials, ImmutableArray<UniverseRunEntry> PriorUniverseTrials,
    string? SelectedCandidateId = null, string? ErrorCode = null, string? FinalRequestId = null,
    DateTimeOffset? FinalRequestedAt = null, string? FinalBaseRunId = null, string? FinalStressRunId = null)
{
    public ImmutableArray<UniverseStudyScore> Scores { get; init; } = [];
    public SearchDiagnosticReport? SelectionDiagnostics { get; init; }
    public ResearchBuildEvidence? BuildEvidence { get; init; }
    public string? FinalBaseReportSha256 { get; init; }
    public string? FinalStressReportSha256 { get; init; }
    public decimal? FinalWorstMarginNotional { get; init; }
    public bool? FinalPassed { get; init; }
}

public sealed partial class ResearchStore
{
    private static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);
    private static DateTimeOffset UtcDate(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    private static DateOnly CeilingDay(DateTimeOffset at) => at.UtcDateTime.TimeOfDay == TimeSpan.Zero ? Day(at) :
        Day(at) < DateOnly.MaxValue ? Day(at).AddDays(1) : throw new ResearchException("universe-panel-decisions");
    private static (DateTimeOffset Start, DateTimeOffset End) UniverseInformationWindow(UniverseRunRequest request) =>
        (new DateTimeOffset(Math.Max(0, request.Decisions.Min().UtcTicks - TimeSpan.FromDays(request.MaximumPriceAgeDays).Ticks), TimeSpan.Zero), request.Decisions.Max().ToUniversalTime());
    private static (DateTimeOffset Start, DateTimeOffset End) UniverseExposureWindow(UniverseRunEntry entry)
    {
        if (entry.InformationStart is null || entry.InformationEnd is null) throw new ResearchException("study-universe-exposure-unknown");
        if (entry.InformationStartAt is { } start && entry.InformationEndAt is { } end && end > start) return (start, end);
        if (entry.InformationStartAt is not null || entry.InformationEndAt is not null) throw new ResearchException("study-universe-exposure-unknown");
        // Legacy day-only windows cannot establish a narrower intraday boundary.
        return (UtcDate(entry.InformationStart.Value), entry.InformationEnd.Value == DateOnly.MaxValue ? DateTimeOffset.MaxValue : UtcDate(entry.InformationEnd.Value.AddDays(1)));
    }
    private static bool Overlap(DateTimeOffset a, DateTimeOffset b, DateTimeOffset c, DateTimeOffset d) => a < d && c < b;
    private void GuardStudyReservations(DateTimeOffset start, DateTimeOffset end, string? ownStudy = null)
    {
        if (Studies().Any(s => !s.Plan.Synthetic && s.Id != ownStudy && (s.Status is "development" or "candidate-frozen" or "final-evaluation") &&
            Overlap(start, end, UtcDate(s.Plan.ReservedStart), UtcDate(s.Plan.ReservedEnd)))) throw new ResearchException("study-reserved-window-conflict");
        if (UniverseStudies().Any(s => !s.Plan.Synthetic && s.Id != ownStudy && (s.Status is "development" or "candidate-frozen" or "final-evaluation") &&
            Overlap(start, end, s.Plan.ReservedStart, s.Plan.ReservedEnd))) throw new ResearchException("study-reserved-window-conflict");
    }
    private void GuardPriorReservedExposure(DateTimeOffset start, DateTimeOffset end, string? ownStudy = null)
    {
        foreach (var run in ListUniverseRuns().Where(r => !r.Synthetic && (ownStudy is null || r.StudyId != ownStudy)))
        {
            var window = UniverseExposureWindow(run);
            if (Overlap(start, end, window.Start, window.End)) throw new ResearchException("study-known-reserved-exposure");
        }
        var panels = new Dictionary<string, MonthlyPanel>(StringComparer.Ordinal);
        foreach (var run in ListRuns().Where(r => !r.Synthetic && (ownStudy is null || r.StudyId != ownStudy) && (r.StartedAt is not null || r.Status is "queued" or "planned")))
        {
            if (!panels.TryGetValue(run.DatasetId, out var panel)) panels[run.DatasetId] = panel = Panel(run.DatasetId);
            var window = InformationWindow(panel, run.Config);
            if (Overlap(start, end, UtcDate(window.Start), UtcDate(window.End))) throw new ResearchException("study-known-reserved-exposure");
        }
        var universeIds = UniverseStudies().Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT c.start_date,c.end_date,w.start_at,w.end_at,c.study_id FROM study_holdout_claims c LEFT JOIN universe_holdout_windows w ON w.study_id=c.study_id WHERE c.synthetic=0 AND c.study_id!=$own";
        cmd.Parameters.AddWithValue("$own", ownStudy ?? ""); using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(2) && universeIds.Contains(reader.GetString(4))) throw new ResearchException("study-holdout-time-unknown");
            var from = reader.IsDBNull(2) ? UtcDate(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture)) : DateTimeOffset.ParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture);
            var to = reader.IsDBNull(3) ? UtcDate(DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture)) : DateTimeOffset.ParseExact(reader.GetString(3), "O", CultureInfo.InvariantCulture);
            if (Overlap(start, end, from, to)) throw new ResearchException("study-holdout-spent");
        }
    }
    private static void InitialiseUniverseStudies(SqliteConnection db) => Execute(db,
        "CREATE TABLE IF NOT EXISTS universe_studies(id TEXT PRIMARY KEY,request_id TEXT NOT NULL UNIQUE,json TEXT NOT NULL,sha256 TEXT NOT NULL);" +
        "CREATE TABLE IF NOT EXISTS universe_study_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,study_id TEXT NOT NULL,at TEXT NOT NULL,kind TEXT NOT NULL);" +
        "CREATE TABLE IF NOT EXISTS universe_holdout_windows(study_id TEXT PRIMARY KEY,start_at TEXT NOT NULL,end_at TEXT NOT NULL);");
    private static bool UniverseStudyCodeMatches(UniverseStudyPlan plan) => plan.IdentityScheme == ResearchIdentity.Scheme && plan.CodeSha256 == StudyCodeSha256;
    private static string TextHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static UniverseResearchStudy ReadUniverseStudy(string json, string hash)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumStudyPayload || TextHash(json) != hash) throw new ResearchException("study-integrity");
        var study = JsonSerializer.Deserialize<UniverseResearchStudy>(json, Json) ?? throw new ResearchException("study-integrity");
        using var document = JsonDocument.Parse(json);
        if (TextHash(document.RootElement.GetProperty("plan").GetRawText()) != study.PlanSha256) throw new ResearchException("study-plan-integrity");
        return study;
    }
    public List<UniverseResearchStudy> UniverseStudies()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json,sha256 FROM universe_studies ORDER BY rowid DESC LIMIT 16";
        using var reader = cmd.ExecuteReader(); var result = new List<UniverseResearchStudy>();
        while (reader.Read()) result.Add(ReadUniverseStudy(reader.GetString(0), reader.GetString(1))); return result;
    }
    public UniverseResearchStudy? UniverseStudy(string id) => UniverseStudies().SingleOrDefault(s => s.Id == id);
    public string? UniverseStudyPlanJson(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json,sha256 FROM universe_studies WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) return null;
        _ = ReadUniverseStudy(reader.GetString(0), reader.GetString(1)); using var doc = JsonDocument.Parse(reader.GetString(0));
        return doc.RootElement.GetProperty("plan").GetRawText();
    }
    public object UniverseStudyEvents(string id)
    {
        _ = UniverseStudy(id) ?? throw new ResearchException("study-not-found");
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT sequence,at,kind FROM universe_study_events WHERE study_id=$id ORDER BY sequence LIMIT 100";
        cmd.Parameters.AddWithValue("$id", id); using var reader = cmd.ExecuteReader(); var rows = new List<object>();
        while (reader.Read()) rows.Add(new { sequence = reader.GetInt64(0), at = reader.GetString(1), kind = reader.GetString(2) }); return rows;
    }
    private void WriteUniverseStudy(SqliteConnection db, SqliteTransaction tx, UniverseResearchStudy study, string kind, bool insert = false)
    {
        var json = JsonSerializer.Serialize(study, Json);
        if (!insert)
        {
            using var prior = db.CreateCommand(); prior.Transaction = tx; prior.CommandText = "SELECT json,sha256 FROM universe_studies WHERE id=$id"; prior.Parameters.AddWithValue("$id", study.Id);
            using var reader = prior.ExecuteReader(); if (!reader.Read()) throw new ResearchException("study-not-found");
            var stored = ReadUniverseStudy(reader.GetString(0), reader.GetString(1));
            if (stored.PlanSha256 != study.PlanSha256) throw new ResearchException("study-plan-binding");
            using var oldDoc = JsonDocument.Parse(reader.GetString(0)); using var newDoc = JsonDocument.Parse(json); using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartObject();
                foreach (var property in newDoc.RootElement.EnumerateObject())
                { writer.WritePropertyName(property.Name); if (property.Name == "plan") writer.WriteRawValue(oldDoc.RootElement.GetProperty("plan").GetRawText()); else property.Value.WriteTo(writer); }
                writer.WriteEndObject();
            }
            json = Encoding.UTF8.GetString(bytes.ToArray());
        }
        if (Encoding.UTF8.GetByteCount(json) > MaximumStudyPayload) throw new ResearchException("study-payload-capacity");
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = insert ? "INSERT INTO universe_studies VALUES($id,$request,$json,$sha)" : "UPDATE universe_studies SET json=$json,sha256=$sha WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", study.Id); cmd.Parameters.AddWithValue("$request", study.RequestId); cmd.Parameters.AddWithValue("$json", json); cmd.Parameters.AddWithValue("$sha", TextHash(json));
        if (cmd.ExecuteNonQuery() != 1) throw new ResearchException("study-not-found");
        cmd.CommandText = "INSERT INTO universe_study_events(study_id,at,kind) VALUES($id,$at,$kind)";
        cmd.Parameters.AddWithValue("$at", clock.GetUtcNow().ToString("O")); cmd.Parameters.AddWithValue("$kind", kind); cmd.ExecuteNonQuery();
    }
    private void SaveUniverseStudy(UniverseResearchStudy study, string kind)
    { using var db = Open(); using var tx = db.BeginTransaction(); WriteUniverseStudy(db, tx, study, kind); tx.Commit(); }
    private DatasetFile ReadUniverseDataset(string sha)
    {
        if (!IsSha256(sha) || !File.Exists(UniverseDatasetPath(sha))) throw new ResearchException("dataset-not-found");
        var bytes = DatasetArchive.ReadBounded(UniverseDatasetPath(sha));
        if (ByteHash(bytes) != sha) throw new ResearchException("dataset-integrity");
        return DatasetJson.Read(bytes);
    }
    public object UniverseDatasets()
    {
        lock (gate)
        {
            var directory = Path.Combine(Root, "universe-datasets");
            if (!Directory.Exists(directory)) return Array.Empty<object>();
            return Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal).Take(20).Select(path =>
            {
                var sha = Path.GetFileNameWithoutExtension(path); var file = ReadUniverseDataset(sha);
                return new { datasetSha256 = sha, file.Manifest.DatasetId, file.Manifest.Synthetic, file.Manifest.CreatedAt,
                    universeIds = file.Manifest.Universes.Select(u => u.Id).ToArray() };
            }).ToArray();
        }
    }
    private static string ByteHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private (UniverseRunEntry Entry, UniverseRunRequest Request) PrepareUniverseStudyRun(UniverseResearchStudy study, UniverseStudyCandidate candidate,
        string stage, string commit, DatasetFile file)
    {
        var final = stage.StartsWith("final-", StringComparison.Ordinal);
        var decisions = final ? study.Plan.FinalDecisions : study.Plan.DevelopmentDecisions;
        var costs = stage.EndsWith("stress", StringComparison.Ordinal) ? study.Plan.StressCosts : study.Plan.BaseCosts;
        var request = new UniverseRunRequest(Guid.NewGuid().ToString("D"), study.Plan.DatasetSha256, study.Plan.UniverseId, decisions, study.Plan.MaximumPriceAgeDays, candidate.Config, costs);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        var id = Guid.NewGuid().ToString("N");
        var start = Day(decisions[0]);
        var window = UniverseInformationWindow(request);
        var entry = new UniverseRunEntry(id, study.Plan.DatasetSha256, file.Manifest.DatasetId, file.Manifest.Synthetic, study.Plan.UniverseId,
            "planned", clock.GetUtcNow(), null, null, commit, UniverseRequestHash(request), InformationStart: DateOnly.FromDayNumber(Math.Max(0, start.DayNumber - study.Plan.MaximumPriceAgeDays)),
            InformationEnd: Day(decisions[^1]), IdentityScheme: ResearchIdentity.Scheme, ResearchSha256: study.Plan.CodeSha256, RequestArtifactSha256: ByteHash(bytes))
        { StudyId = study.Id, StudyCandidateId = candidate.Id, StudyStage = stage, InformationStartAt = window.Start, InformationEndAt = window.End };
        return (entry, request);
    }
    private void PersistUniverseStudyRequest((UniverseRunEntry Entry, UniverseRunRequest Request) pair)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(pair.Request, Json); EnsureArtifactCapacity(bytes);
        var directory = Path.Combine(Root, "runs", pair.Entry.Id); Directory.CreateDirectory(directory);
        ImmutableWrite(Path.Combine(directory, "universe-request.json"), bytes);
    }
    private void InsertUniverseStudyRun(SqliteConnection db, SqliteTransaction tx, (UniverseRunEntry Entry, UniverseRunRequest Request) pair)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO universe_runs VALUES($id,$request,$dataset,$json,'planned')";
        cmd.Parameters.AddWithValue("$id", pair.Entry.Id); cmd.Parameters.AddWithValue("$request", pair.Request.RequestId); cmd.Parameters.AddWithValue("$dataset", pair.Entry.DatasetSha256);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(pair.Entry, Json)); cmd.ExecuteNonQuery(); UniverseEvent(db, tx, pair.Entry.Id, "study-planned-before-work");
    }
    public UniverseResearchStudy RegisterUniverseStudy(UniverseStudyRequest request, string commit)
    {
        if (request is null || !Guid.TryParseExact(request.RequestId, "D", out _) || request.Name is not { Length: > 0 and <= 80 } || request.Costs is null)
            throw new ResearchException("study-request");
        if (request.Decisions.IsDefault) throw new ResearchException("universe-panel-decisions");
        request = request with { Decisions = request.Decisions.Select(d => d.ToUniversalTime()).ToImmutableArray() };
        var requestHash = JsonHash(request with { RequestId = "" });
        lock (gate)
        {
            var previous = UniverseStudies().SingleOrDefault(s => s.RequestId == request.RequestId);
            if (previous is not null) { if (previous.RequestSha256 != requestHash) throw new ResearchException("request-id-conflict"); return previous; }
            if (Studies().Count + UniverseStudies().Count >= MaximumStudies) throw new ResearchException("study-capacity");
            var file = ReadUniverseDataset(request.DatasetSha256); var identity = ResearchIdentity.Current;
            var plan = UniverseStudyProtocol.Declare(request.DatasetSha256, request.UniverseId, file.Manifest.Synthetic, request.Decisions, request.MaximumPriceAgeDays,
                request.MonthlyGbp, request.Costs, request.Purpose, identity.Sha256, identity.Scheme);
            if (!file.Manifest.Universes.Any(u => u.Id == plan.UniverseId)) throw new ResearchException("universe-study-universe");
            if (plan.ReservedEnd > file.Manifest.CreatedAt) throw new ResearchException("universe-panel-decisions");
            if (!plan.Synthetic)
            {
                var start = new DateTimeOffset(Math.Max(0, plan.DevelopmentDecisions[0].UtcTicks - TimeSpan.FromDays(plan.MaximumPriceAgeDays).Ticks), TimeSpan.Zero);
                GuardStudyReservations(start, plan.ReservedEnd);
                GuardPriorReservedExposure(plan.ReservedStart, plan.ReservedEnd);
            }
            var prior = ListRuns(); var priorUniverse = ListUniverseRuns();
            if (prior.Count + priorUniverse.Count + ReservedStudySlots() + plan.Candidates.Length * 2 + 2 > 1000) throw new ResearchException("trial-capacity");
            var study = new UniverseResearchStudy(Guid.NewGuid().ToString("N"), request.RequestId, requestHash, request.Name, "development", clock.GetUtcNow(),
                plan, JsonHash(plan), [], prior.Select(r => new PriorStudyTrial(r.Id, r.DatasetId, r.ConfigSha256, r.Status, r.ReportSha256)).ToImmutableArray(), priorUniverse.ToImmutableArray())
            { BuildEvidence = identity };
            var rows = plan.Candidates.SelectMany(c => new[] { PrepareUniverseStudyRun(study, c, "development-base", commit, file),
                PrepareUniverseStudyRun(study, c, "development-stress", commit, file) }).ToArray();
            study = study with { DevelopmentRuns = plan.Candidates.Select((c, i) => new UniverseStudyRuns(c.Id, rows[i * 2].Entry.Id, rows[i * 2 + 1].Entry.Id)).ToImmutableArray() };
            using var db = Open(); using var tx = db.BeginTransaction();
            foreach (var row in rows) InsertUniverseStudyRun(db, tx, row);
            WriteUniverseStudy(db, tx, study, "registered-before-work", true);
            foreach (var row in rows) PersistUniverseStudyRequest(row);
            tx.Commit(); return study;
        }
    }
    public UniverseResearchStudy EvaluateUniverseStudy(string id, StudyEvaluationRequest request, string commit)
    {
        if (request is null || !Guid.TryParseExact(request.RequestId, "D", out _)) throw new ResearchException("study-evaluation-request");
        lock (gate)
        {
            var study = UniverseStudy(id) ?? throw new ResearchException("study-not-found");
            if (request.PlanSha256 != study.PlanSha256) throw new ResearchException("study-plan-binding");
            if (study.FinalRequestId == request.RequestId) return study;
            if (study.FinalRequestedAt is not null) throw new ResearchException("study-holdout-spent");
            if (study.Status != "candidate-frozen" || study.SelectedCandidateId is null) throw new ResearchException("study-candidate-not-frozen");
            if (!UniverseStudyCodeMatches(study.Plan)) throw new ResearchException("study-code-changed");
            if (!study.Plan.Synthetic)
            {
                GuardPriorReservedExposure(study.Plan.ReservedStart, study.Plan.ReservedEnd, study.Id);
                var start = new DateTimeOffset(Math.Max(0, study.Plan.FinalDecisions[0].UtcTicks - TimeSpan.FromDays(study.Plan.MaximumPriceAgeDays).Ticks), TimeSpan.Zero);
                GuardStudyReservations(start, study.Plan.ReservedEnd, study.Id);
            }
            var file = ReadUniverseDataset(study.Plan.DatasetSha256); var candidate = study.Plan.Candidates.Single(c => c.Id == study.SelectedCandidateId);
            var a = PrepareUniverseStudyRun(study, candidate, "final-base", commit, file); var b = PrepareUniverseStudyRun(study, candidate, "final-stress", commit, file);
            var at = clock.GetUtcNow();
            using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT study_id FROM study_holdout_claims WHERE request_id=$request"; cmd.Parameters.AddWithValue("$request", request.RequestId);
            if (cmd.ExecuteScalar() is not null) throw new ResearchException("request-id-conflict");
            cmd.CommandText = "INSERT INTO study_holdout_claims VALUES($study,$request,$start,$end,$synthetic,$at)";
            cmd.Parameters.AddWithValue("$study", study.Id); cmd.Parameters.AddWithValue("$start", Day(study.Plan.ReservedStart).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$end", CeilingDay(study.Plan.ReservedEnd).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$synthetic", study.Plan.Synthetic ? 1 : 0);
            cmd.Parameters.AddWithValue("$at", at.ToString("O")); cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO universe_holdout_windows VALUES($study,$from_at,$to_at)";
            cmd.Parameters.AddWithValue("$from_at", study.Plan.ReservedStart.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$to_at", study.Plan.ReservedEnd.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); cmd.ExecuteNonQuery();
            InsertUniverseStudyRun(db, tx, a); InsertUniverseStudyRun(db, tx, b);
            study = study with { Status = "final-evaluation", FinalRequestId = request.RequestId, FinalRequestedAt = at, FinalBaseRunId = a.Entry.Id, FinalStressRunId = b.Entry.Id };
            WriteUniverseStudy(db, tx, study, "holdout-spent-before-work");
            PersistUniverseStudyRequest(a); PersistUniverseStudyRequest(b); tx.Commit(); return study;
        }
    }
    public UniverseResearchStudy CancelUniverseStudy(string id)
    {
        lock (gate)
        {
            var study = UniverseStudy(id) ?? throw new ResearchException("study-not-found");
            if (study.Status is not ("development" or "candidate-frozen" or "final-evaluation")) return study;
            CancelUniverseStudyRuns(study.Id, "user-cancelled"); study = study with { Status = "cancelled", ErrorCode = "user-cancelled" }; SaveUniverseStudy(study, "cancelled-no-replacement"); return study;
        }
    }
    private void CancelUniverseStudyRuns(string studyId, string reason = "study-family-stopped")
    {
        foreach (var run in ListUniverseRuns().Where(r => r.StudyId == studyId && r.Status is "planned" or "started"))
            SaveUniverseRun(run with { CancelRequested = true, Status = run.Status == "planned" ? "cancelled" : run.Status,
                FinishedAt = run.Status == "planned" ? clock.GetUtcNow() : null, ErrorCode = reason }, "study-cancel-requested");
    }
    private void FailUniverseStudy(UniverseResearchStudy study, string code)
    {
        CancelUniverseStudyRuns(study.Id);
        SaveUniverseStudy(study with { Status = study.FinalRequestedAt is null ? "research-failed" : "final-failed", ErrorCode = code }, "failed-no-replacement");
    }
    private UniverseRunRequest ValidateUniverseStudyRequest(UniverseResearchStudy study, UniverseRunEntry entry)
    {
        var request = JsonSerializer.Deserialize<UniverseRunRequest>(UniverseRequestBytes(entry), Json) ?? throw new ResearchException("universe-request-integrity");
        var candidate = study.Plan.Candidates.Single(c => c.Id == entry.StudyCandidateId);
        var final = entry.StudyStage?.StartsWith("final-", StringComparison.Ordinal) == true;
        var declared = entry.StudyStage switch
        {
            "development-base" => study.DevelopmentRuns.Any(p => p.CandidateId == candidate.Id && entry.Id == p.BaseRunId),
            "development-stress" => study.DevelopmentRuns.Any(p => p.CandidateId == candidate.Id && entry.Id == p.StressRunId),
            "final-base" => entry.Id == study.FinalBaseRunId && candidate.Id == study.SelectedCandidateId,
            "final-stress" => entry.Id == study.FinalStressRunId && candidate.Id == study.SelectedCandidateId,
            _ => false
        };
        var stageValid = entry.StudyStage is "development-base" or "development-stress" or "final-base" or "final-stress";
        var costs = entry.StudyStage?.EndsWith("stress", StringComparison.Ordinal) == true ? study.Plan.StressCosts : study.Plan.BaseCosts;
        var window = UniverseInformationWindow(request);
        if (!declared || !stageValid || entry.IdentityScheme != study.Plan.IdentityScheme || entry.ResearchSha256 != study.Plan.CodeSha256 ||
            entry.InformationStartAt != window.Start || entry.InformationEndAt != window.End || entry.InformationStart != Day(window.Start) || entry.InformationEnd != Day(window.End) ||
            entry.Synthetic != study.Plan.Synthetic || entry.UniverseId != study.Plan.UniverseId || entry.DatasetSha256 != study.Plan.DatasetSha256 ||
            request.DatasetSha256 != study.Plan.DatasetSha256 || request.UniverseId != study.Plan.UniverseId || request.MaximumPriceAgeDays != study.Plan.MaximumPriceAgeDays ||
            !request.Decisions.SequenceEqual(final ? study.Plan.FinalDecisions : study.Plan.DevelopmentDecisions) || request.Config != candidate.Config || request.Costs != costs)
            throw new ResearchException("universe-study-request-contract");
        return request;
    }
    private UniverseBacktestReport ValidateUniverseStudyReport(UniverseResearchStudy study, UniverseRunEntry entry)
    {
        var request = ValidateUniverseStudyRequest(study, entry); var report = UniverseReport(entry) ?? throw new ResearchException("report-not-found");
        var expected = request.Decisions.Skip(UniverseBacktest.MomentumPeriods + 1).Zip(request.Decisions.Skip(UniverseBacktest.MomentumPeriods + 2), (from, to) => (Day(from), Day(to))).ToArray();
        if (report.DatasetId != entry.DatasetId || report.UniverseId != study.Plan.UniverseId || report.Synthetic != study.Plan.Synthetic ||
            report.Config != request.Config || report.Costs != request.Costs || report.PanelSha256 != entry.PanelSha256 || entry.PanelSha256 is null ||
            report.EngineVersion != UniverseBacktest.Version || report.EvaluationStart != request.Decisions[UniverseBacktest.MomentumPeriods + 1] || report.EvaluationEnd != request.Decisions[^1] ||
            !report.Summaries.Select(s => s.Strategy).Order(StringComparer.Ordinal).SequenceEqual(UniverseBacktest.Strategies.Order(StringComparer.Ordinal)) ||
            !report.Equity.Select(e => e.Strategy).Distinct().Order(StringComparer.Ordinal).SequenceEqual(UniverseBacktest.Strategies.Order(StringComparer.Ordinal)))
            throw new ResearchException("universe-study-report-contract");
        foreach (var strategy in UniverseBacktest.Strategies)
        {
            var rows = report.Equity.Where(e => e.Strategy == strategy).ToArray(); var summary = report.Summaries.Single(s => s.Strategy == strategy);
            if (!rows.Select(e => (e.DecisionDate, e.PeriodEnd)).SequenceEqual(expected) || rows.Length == 0 ||
                rows.Where((e, i) => e.State.ContributionsGbp != (i + 1) * request.Config.MonthlyGbp).Any() ||
                summary.EndingEquityGbp != rows[^1].State.EquityGbp || summary.ContributionsGbp != rows[^1].State.ContributionsGbp ||
                summary.ProfitGbp != summary.EndingEquityGbp - summary.ContributionsGbp || summary.TradingCostGbp != rows.Sum(e => e.State.PeriodCostGbp))
                throw new ResearchException("universe-study-report-contract");
        }
        return report;
    }
    private static UniverseStudyScore UniverseScore(string candidate, UniverseRunEntry a, UniverseRunEntry b, UniverseBacktestReport ar, UniverseBacktestReport br)
    {
        if (a.PanelSha256 != b.PanelSha256) throw new ResearchException("universe-study-panel-mismatch");
        static decimal Equity(UniverseBacktestReport r, string strategy) => r.Summaries.Single(s => s.Strategy == strategy).EndingEquityGbp;
        var av = Equity(ar, "momentum"); var bv = Equity(br, "momentum"); var ac = Math.Max(Equity(ar, "equal_weight"), Equity(ar, "cash")); var bc = Math.Max(Equity(br, "equal_weight"), Equity(br, "cash"));
        return new(candidate, a.Id, b.Id, av, bv, ac, bc, Math.Min(av - ac, bv - bc));
    }
    private static DiagnosticReturns UniverseStudyStream(UniverseRunEntry run, UniverseBacktestReport report) => new(run.Id, run.ReportSha256!,
        report.Equity.Where(e => e.Strategy == "momentum").Select(e => e.PeriodEnd).ToImmutableArray(),
        report.Equity.Where(e => e.Strategy == "momentum").Select(e => (double)e.State.PeriodReturn).ToImmutableArray(),
        new[] { "equal_weight", "cash" }.ToImmutableDictionary(s => s, s => report.Equity.Where(e => e.Strategy == s).Select(e => (double)e.State.PeriodReturn).ToImmutableArray()));
    private void AdvanceUniverseStudies()
    {
        foreach (var study in UniverseStudies().Where(s => s.Status is "development" or "candidate-frozen" or "final-evaluation"))
        {
            try
            {
                if (!UniverseStudyCodeMatches(study.Plan)) { FailUniverseStudy(study, "study-code-changed"); continue; }
                if (study.Status == "candidate-frozen") continue;
                var development = study.Status == "development";
                var runs = ListUniverseRuns().Where(r => r.StudyId == study.Id && (development ? r.StudyStage?.StartsWith("development-", StringComparison.Ordinal) == true : r.StudyStage?.StartsWith("final-", StringComparison.Ordinal) == true)).ToArray();
                if (runs.Any(r => r.Status is "failed" or "interrupted" or "cancelled")) { FailUniverseStudy(study, "study-trial-incomplete"); continue; }
                if (runs.Length != (development ? study.Plan.Candidates.Length * 2 : 2)) throw new ResearchException("study-family-incomplete");
                if (runs.Any(r => r.Status != "succeeded")) continue;
                if (runs.Select(r => r.PanelSha256).Distinct().Count() != 1) throw new ResearchException("universe-study-panel-mismatch");
                if (development)
                {
                    var scores = ImmutableArray.CreateBuilder<UniverseStudyScore>(); var streams = new List<DiagnosticReturns>();
                    foreach (var pair in study.DevelopmentRuns)
                    {
                        var a = runs.Single(r => r.Id == pair.BaseRunId); var b = runs.Single(r => r.Id == pair.StressRunId);
                        var ar = ValidateUniverseStudyReport(study, a); var br = ValidateUniverseStudyReport(study, b);
                        scores.Add(UniverseScore(pair.CandidateId, a, b, ar, br)); streams.Add(UniverseStudyStream(a, ar)); streams.Add(UniverseStudyStream(b, br));
                    }
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2)); var diagnostic = SearchDiagnostics.BuildUniverse(streams, deadline.Token);
                    var best = scores.OrderByDescending(s => s.WorstMarginNotional).ThenBy(s => s.CandidateId, StringComparer.Ordinal).First(); var nominate = study.Plan.Synthetic || best.WorstMarginNotional > 0;
                    SaveUniverseStudy(study with { Status = nominate ? "candidate-frozen" : "no-candidate", SelectedCandidateId = nominate ? best.CandidateId : null,
                        Scores = scores.ToImmutable(), SelectionDiagnostics = diagnostic }, "development-selection-frozen-notional-only");
                }
                else
                {
                    var a = runs.Single(r => r.Id == study.FinalBaseRunId); var b = runs.Single(r => r.Id == study.FinalStressRunId);
                    var score = UniverseScore(study.SelectedCandidateId!, a, b, ValidateUniverseStudyReport(study, a), ValidateUniverseStudyReport(study, b));
                    var passed = score.WorstMarginNotional > 0;
                    SaveUniverseStudy(study with { Status = study.Plan.Synthetic ? "engineering-complete" : passed ? "research-complete" : "research-rejected",
                        FinalBaseReportSha256 = a.ReportSha256, FinalStressReportSha256 = b.ReportSha256, FinalWorstMarginNotional = score.WorstMarginNotional, FinalPassed = passed }, "final-decision-no-substitute-notional-only");
                }
            }
            catch (Exception error) when (error is ResearchException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or OperationCanceledException)
            { FailUniverseStudy(study, error is ResearchException re ? re.Code : "study-evaluation-failed"); }
        }
    }
    public bool ProcessNextUniverseStudyRun(string commit, CancellationToken ct = default)
    {
        if (!TryBeginComputation()) return false;
        try
        {
            UniverseRunEntry? entry; UniverseRunRequest request; DatasetFile file;
            lock (gate)
            {
                AdvanceUniverseStudies(); entry = ListUniverseRuns().LastOrDefault(r => r.StudyId is not null && r.Status == "planned");
                if (entry is null) return false;
                // Preserve admission order across the two worker queues.
                if (ListRuns().Any(r => (r.Status is "queued" or "planned") && r.CreatedAt <= entry.CreatedAt)) return false;
                try
                {
                    var study = UniverseStudy(entry.StudyId!) ?? throw new ResearchException("study-not-found");
                    var final = entry.StudyStage?.StartsWith("final-", StringComparison.Ordinal) == true;
                    if (!UniverseStudyCodeMatches(study.Plan) || study.Status != (final ? "final-evaluation" : "development") || final && study.FinalRequestedAt is null)
                        throw new ResearchException("study-code-or-state-changed");
                    request = ValidateUniverseStudyRequest(study, entry);
                    if (!entry.Synthetic) { var window = UniverseExposureWindow(entry); GuardStudyReservations(window.Start, window.End, study.Id); }
                    entry = entry with { Status = "started", StartedAt = clock.GetUtcNow(), WorkerCommit = commit }; SaveUniverseRun(entry, "started");
                    file = ReadUniverseDataset(entry.DatasetSha256);
                    if (file.Manifest.DatasetId != entry.DatasetId || file.Manifest.Synthetic != entry.Synthetic) throw new ResearchException("dataset-integrity");
                }
                catch (Exception error) when (error is ResearchException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException)
                {
                    SaveUniverseRun(entry with { Status = "failed", FinishedAt = clock.GetUtcNow(), ErrorCode = error is ResearchException re ? re.Code : "universe-run-input-unavailable" }, "study-run-refused");
                    AdvanceUniverseStudies(); return true;
                }
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
            _ = ComputeUniverseRun(entry, request, file, deadline.Token); return true;
        }
        finally { EndComputation(); }
    }
}
