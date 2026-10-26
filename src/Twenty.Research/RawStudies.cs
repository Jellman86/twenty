using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed record RawStudyRegistrationRequest(string RequestId, string Name, string? PlanBase64 = null, string? PlanSha256 = null);
public sealed record RawStudyInboxRequest(string RequestId, string Name, string PlanSha256);
public sealed record RawStudyTrialRequest(string StudyId, string PlanSha256, string TrialId, string Stage);
public sealed record RawStudyTrialEntry(string Id, string StudyId, string TrialId, string CandidateId, string CostScenario,
    string Status, DateTimeOffset CreatedAt, string DatasetSha256, bool Synthetic, string ResearchSha256,
    string RequestSha256, DateTimeOffset InformationStartAt, DateTimeOffset InformationEndAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? QueuedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? StartedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? FinishedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? WorkerCommit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ErrorCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ReportSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool CancelRequested { get; init; }
}
public sealed record RawStudyOriginal(string Kind, string PinSha256, string OriginalSha256, int Bytes);
public sealed record RawResearchStudy(string Id, string RequestId, string RequestSha256, string Name, string Status,
    DateTimeOffset CreatedAt, string CodeCommit, RawUniverseStudyPlan Plan, string PlanSha256, string PlanBase64,
    ImmutableArray<PriorStudyTrial> PriorTrials, ImmutableArray<UniverseRunEntry> PriorUniverseTrials,
    ImmutableArray<RawStudyTrialEntry> PriorRawTrials, ImmutableArray<string> DevelopmentTrialIds, ImmutableArray<RawStudyOriginal> Originals)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FinalRequestId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? FinalRequestedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FinalBaseTrialId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FinalStressTrialId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FinalBaseReportSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FinalStressReportSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public decimal? FinalWorstMarginGbp { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? FinalPassed { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SelectionRequestId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? SelectionRequestedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SelectionReportSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SelectedCandidateId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StartRequestId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? StartRequestedAt { get; init; }
}

public sealed partial class ResearchStore
{
    private static void InitialiseRawStudies(SqliteConnection db) => Execute(db,
        "CREATE TABLE IF NOT EXISTS raw_studies(id TEXT PRIMARY KEY,request_id TEXT NOT NULL UNIQUE,json TEXT NOT NULL,sha256 TEXT NOT NULL);" +
        "CREATE TABLE IF NOT EXISTS raw_trials(id TEXT PRIMARY KEY,study_id TEXT NOT NULL,json TEXT NOT NULL,status TEXT NOT NULL);" +
        "CREATE TABLE IF NOT EXISTS raw_study_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,study_id TEXT NOT NULL,at TEXT NOT NULL,kind TEXT NOT NULL);");
    private int RawReservedStudySlots()
    {
        _ = ListRawTrials(); // Missing/corrupt admitted rows cannot silently free budget.
        return RawStudies().Count(s => RawReservationActive(s) && s.FinalRequestId is null) * 2;
    }
    private static string RawHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] RawBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, RawReplayJson.Options);
    private static RawResearchStudy ReadRawStudy(string json, string sha)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumStudyPayload || RawHash(bytes) != sha) throw new ResearchException("raw-study-integrity");
        var study = JsonSerializer.Deserialize<RawResearchStudy>(bytes, RawReplayJson.Options) ?? throw new ResearchException("raw-study-integrity");
        if (!Guid.TryParseExact(study.Id, "N", out _) || study.Status is not ("registered" or "development" or "development-complete" or "candidate-frozen" or "no-candidate" or "failed" or "cancelled" or "final-evaluation" or "final-failed" or "engineering-complete" or "research-complete" or "research-rejected") ||
            study.Originals.IsDefault || study.DevelopmentTrialIds.IsDefault || study.Originals.Any(i => i is null || i.Kind is not ("dataset" or "calendar" or "actions") ||
                !IsSha256(i.OriginalSha256) || !IsSha256(i.PinSha256) || i.Bytes < 0)) throw new ResearchException("raw-study-integrity");
        var selection = study.SelectionRequestId is not null;
        if (selection != (study.SelectionReportSha256 is not null) || selection != (study.SelectionRequestedAt is not null) ||
            selection && (!Guid.TryParseExact(study.SelectionRequestId,"D",out _) || !IsSha256(study.SelectionReportSha256!)) ||
            study.Status is "candidate-frozen" or "no-candidate" && !selection ||
            study.Status == "candidate-frozen" && (study.SelectedCandidateId is null || !study.Plan.Candidates.Any(c=>c.Id==study.SelectedCandidateId)) ||
            study.Status == "no-candidate" && study.SelectedCandidateId is not null ||
            selection && study.Status is not ("candidate-frozen" or "no-candidate" or "cancelled" or "final-evaluation" or "final-failed" or "engineering-complete" or "research-complete" or "research-rejected")) throw new ResearchException("raw-study-integrity");
        ValidateRawFinalMetadata(study);
        byte[] plan;
        try { plan = Convert.FromBase64String(study.PlanBase64); }
        catch (FormatException) { throw new ResearchException("raw-study-plan-integrity"); }
        // Historical rows remain inspectable without requiring their old build to
        // become today's scientific implementation. Execution must decode anew.
        if (plan.Length > RawUniverseStudyProtocol.MaximumPlanBytes || RawHash(plan) != study.PlanSha256 ||
            !plan.AsSpan().SequenceEqual(RawBytes(study.Plan))) throw new ResearchException("raw-study-plan-integrity");
        var expectedInputs = study.Plan.Calendars.Select(p => (Kind: "calendar", Pin: p.Sha256))
            .Append(("actions", study.Plan.ActionCatalogSha256)).Append(("dataset", study.Plan.DatasetSha256))
            .OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal);
        var recordedInputs = study.Originals.Select(p => (p.Kind, p.PinSha256)).OrderBy(p => p.Kind, StringComparer.Ordinal).ThenBy(p => p.PinSha256, StringComparer.Ordinal);
        if (!expectedInputs.SequenceEqual(recordedInputs) || study.RequestSha256 != RawHash(RawBytes(new { study.Name, PlanSha256 = study.PlanSha256 })))
            throw new ResearchException("raw-study-integrity");
        return study;
    }
    public List<RawResearchStudy> RawStudies()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json,sha256 FROM raw_studies ORDER BY rowid DESC LIMIT 16";
        using var reader = cmd.ExecuteReader(); var rows = new List<RawResearchStudy>();
        while (reader.Read()) rows.Add(ReadRawStudy(reader.GetString(0), reader.GetString(1))); return rows;
    }
    public List<RawStudyTrialEntry> ListRawTrials()
    {
        lock (gate)
        {
            var studies = RawStudies().ToDictionary(s => s.Id, StringComparer.Ordinal);
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json,status FROM raw_trials ORDER BY rowid DESC LIMIT 1000";
            using var reader = cmd.ExecuteReader(); var rows = new List<RawStudyTrialEntry>();
            while (reader.Read())
            {
                var row = JsonSerializer.Deserialize<RawStudyTrialEntry>(reader.GetString(0), RawReplayJson.Options) ?? throw new ResearchException("raw-trial-integrity");
                if (!studies.TryGetValue(row.StudyId, out var study)) throw new ResearchException("raw-trial-integrity");
                var final = IsRawFinalTrial(study, row);
                var role = final ? new RawStudyTrial("final:" + study.SelectedCandidateId + ":" + (row.Id == study.FinalBaseTrialId ? "base" : "stress"), study.SelectedCandidateId!, row.Id == study.FinalBaseTrialId ? "base" : "stress")
                    : study.Plan.DevelopmentTrials.SingleOrDefault(t => t.Id == row.TrialId);
                var window = final ? RawFinalWindow(study.Plan) : RawDevelopmentWindow(study.Plan);
                if (!Guid.TryParseExact(row.Id, "N", out _) || row.Status != reader.GetString(1) || !RawTrialStateValid(study, row) || role is null ||
                    row.TrialId != role.Id || row.CandidateId != role.CandidateId || row.CostScenario != role.CostScenario ||
                    row.DatasetSha256 != study.Plan.DatasetSha256 || row.Synthetic != study.Plan.Synthetic || row.ResearchSha256 != study.Plan.CodeSha256 ||
                    row.InformationStartAt != window.Start || row.InformationEndAt != window.End || row.CreatedAt != (final ? study.FinalRequestedAt : study.CreatedAt) ||
                    (!final && !study.DevelopmentTrialIds.Contains(row.Id)) || row.RequestSha256 != RawHash(RawBytes(new RawStudyTrialRequest(study.Id, study.PlanSha256, role.Id, final ? "final" : "development"))))
                    throw new ResearchException("raw-trial-integrity");
                rows.Add(row);
            }
            foreach (var study in studies.Values)
                if (study.DevelopmentTrialIds.Length != 8 || study.DevelopmentTrialIds.Distinct(StringComparer.Ordinal).Count() != 8 ||
                    !rows.Where(r => r.StudyId == study.Id && !IsRawFinalTrial(study, r)).Select(r => r.TrialId).Order(StringComparer.Ordinal)
                        .SequenceEqual(study.Plan.DevelopmentTrials.Select(t => t.Id).Order(StringComparer.Ordinal)) ||
                    !study.DevelopmentTrialIds.All(id => rows.Any(r => r.Id == id && r.StudyId == study.Id)))
                    throw new ResearchException("raw-trial-integrity");
            foreach (var study in studies.Values)
            {
                var final = rows.Where(r => r.StudyId == study.Id && IsRawFinalTrial(study, r)).ToArray();
                if (final.Length != (study.FinalRequestId is null ? 0 : 2)) throw new ResearchException("raw-trial-integrity");
                if (study.FinalBaseReportSha256 is not null &&
                    (final.Single(t => t.Id == study.FinalBaseTrialId).ReportSha256 != study.FinalBaseReportSha256 ||
                     final.Single(t => t.Id == study.FinalStressTrialId).ReportSha256 != study.FinalStressReportSha256))
                    throw new ResearchException("raw-study-final-integrity");
                ValidateRawFinalClaim(study);
            }
            return rows;
        }
    }
    public object RawStudyEvents(string id)
    {
        _ = RawStudies().SingleOrDefault(s => s.Id == id) ?? throw new ResearchException("study-not-found");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT sequence,at,kind FROM raw_study_events WHERE study_id=$id ORDER BY sequence LIMIT 100";
        cmd.Parameters.AddWithValue("$id", id); using var reader = cmd.ExecuteReader(); var events = new List<object>();
        while (reader.Read()) events.Add(new { sequence = reader.GetInt64(0), at = reader.GetString(1), kind = reader.GetString(2) });
        return events;
    }
    public RawResearchStudy CancelRawStudy(string id)
    {
        lock (gate)
        {
            var study = RawStudies().SingleOrDefault(s => s.Id == id) ?? throw new ResearchException("study-not-found");
            if (!RawReservationActive(study)) return study;
            var trials = ListRawTrials().Where(t => t.StudyId == id).ToArray();
            study = study with { Status = "cancelled" };
            SaveRawFamily(study, trials.Select(t => t.Status == "planned" ? t with { Status = "cancelled", FinishedAt = clock.GetUtcNow() } :
                t.Status == "started" ? t with { CancelRequested = true } : t), "cancelled-no-replacement");
            return study;
        }
    }
    private string RawOriginalPath(string kind, string sha) => Path.Combine(Root, "raw-study-inputs", kind, sha + ".json");
    public byte[] RawStudyOriginalBytes(string studyId, string originalSha256)
    {
        var study = RawStudies().SingleOrDefault(s => s.Id == studyId) ?? throw new ResearchException("study-not-found");
        var input = study.Originals.SingleOrDefault(i => i.OriginalSha256 == originalSha256) ?? throw new ResearchException("raw-study-input-unavailable");
        var path = input.Kind == "dataset" ? UniverseDatasetPath(input.OriginalSha256) : RawOriginalPath(input.Kind, input.OriginalSha256);
        var bytes = DatasetArchive.ReadBounded(path);
        if (bytes.Length != input.Bytes || RawHash(bytes) != input.OriginalSha256) throw new ResearchException("raw-study-input-integrity");
        return bytes;
    }
    public byte[] RawTrialRequestBytes(string id)
    {
        var trial = ListRawTrials().SingleOrDefault(t => t.Id == id) ?? throw new ResearchException("raw-trial-not-found");
        var bytes = DatasetArchive.ReadBounded(Path.Combine(Root, "runs", trial.Id, "raw-study-request.json"));
        var expected = RawBytes(new RawStudyTrialRequest(trial.StudyId, RawStudies().Single(s => s.Id == trial.StudyId).PlanSha256, trial.TrialId, IsRawFinalTrial(RawStudies().Single(s => s.Id == trial.StudyId), trial) ? "final" : "development"));
        if (RawHash(bytes) != trial.RequestSha256 || !bytes.AsSpan().SequenceEqual(expected)) throw new ResearchException("raw-trial-request-integrity");
        return bytes;
    }
    private static (DateTimeOffset Start, DateTimeOffset End) RawDevelopmentWindow(RawUniverseStudyPlan plan) =>
        (new(Math.Max(0, plan.DevelopmentDecisions[0].UtcTicks - TimeSpan.FromDays(plan.BaseExecution.MaximumPriceAgeDays).Ticks), TimeSpan.Zero), plan.DevelopmentEnd);
    public RawResearchStudy RegisterRawStudy(RawStudyRegistrationRequest request, string commit)
    {
        if (request is null || !Guid.TryParseExact(request.RequestId, "D", out _) || request.Name is not { Length: > 0 and <= 80 } ||
            request.Name.Any(char.IsControl) || (request.PlanBase64 is null) == (request.PlanSha256 is null))
            throw new ResearchException("raw-study-request");
        byte[]? bytes = null; string planSha;
        if (request.PlanBase64 is { } encoded)
        {
            if (encoded.Length > 4L * ((RawUniverseStudyProtocol.MaximumPlanBytes + 2L) / 3)) throw new ResearchException("raw-study-request");
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new ResearchException("raw-study-request"); }
            if (bytes.Length > RawUniverseStudyProtocol.MaximumPlanBytes) throw new ResearchException("raw-study-request");
            planSha = RawHash(bytes);
        }
        else
        {
            if (!IsSha256(request.PlanSha256)) throw new ResearchException("raw-study-request");
            planSha = request.PlanSha256!;
        }
        var requestSha = RawHash(RawBytes(new { request.Name, PlanSha256 = planSha }));
        lock (gate)
        {
            var old = RawStudies().SingleOrDefault(s => s.RequestId == request.RequestId);
            if (old is not null) { if (old.RequestSha256 != requestSha) throw new ResearchException("request-id-conflict"); return old; }
            if (bytes is null)
            {
                var path = Path.Combine(Root, "imports", planSha + ".raw-study.json");
                if (!File.Exists(path)) throw new ResearchException("raw-study-input-unavailable");
                bytes = DatasetArchive.ReadBounded(path);
                if (bytes.Length > RawUniverseStudyProtocol.MaximumPlanBytes || RawHash(bytes) != planSha)
                    throw new ResearchException("raw-study-plan-integrity");
            }
            var plan = RawUniverseStudyProtocol.Decode(bytes);
            if (!bytes.AsSpan().SequenceEqual(RawUniverseStudyProtocol.Encode(plan))) throw new ResearchException("raw-study-plan-integrity");
            if (plan.CodeSha256 != ResearchIdentity.Current.Sha256 || plan.IdentityScheme != ResearchIdentity.Scheme)
                throw new ResearchException("study-code-changed");
            if (Studies().Count + UniverseStudies().Count + RawStudies().Count >= MaximumStudies) throw new ResearchException("study-capacity");
            if (!File.Exists(UniverseDatasetPath(plan.DatasetSha256))) throw new ResearchException("dataset-not-found");
            var datasetBytes = DatasetArchive.ReadBounded(UniverseDatasetPath(plan.DatasetSha256));
            if (RawHash(datasetBytes) != plan.DatasetSha256) throw new ResearchException("dataset-integrity");
            var file = DatasetJson.Read(datasetBytes);
            var validation = DatasetValidator.Validate(file);
            if (!validation.IsValid) throw new InvalidDatasetException(validation);
            if (file.Manifest.Synthetic != plan.Synthetic) throw new ResearchException("raw-study-provenance");
            if (!file.Manifest.Universes.Any(u => u.Id == plan.UniverseId) || plan.ReservedEnd > file.Manifest.CreatedAt)
                throw new ResearchException("raw-study-dataset-contract");
            var window = RawDevelopmentWindow(plan);
            if (!plan.Synthetic)
            {
                GuardStudyReservations(window.Start, plan.ReservedEnd);
                GuardPriorReservedExposure(plan.ReservedStart, plan.ReservedEnd);
            }
            var models = ListRuns(); var universe = ListUniverseRuns(); var raw = ListRawTrials();
            if (models.Count + universe.Count + raw.Count + ReservedStudySlots() + plan.DevelopmentTrials.Length + 2 > 1000)
                throw new ResearchException("trial-capacity");
            var originals = new List<(RawStudyOriginal Original, byte[] Bytes)>(); long total = datasetBytes.Length;
            byte[] Inbox(string sha, string kind)
            {
                var path = Path.Combine(Root, "imports", sha + "." + kind + ".json");
                if (!File.Exists(path)) throw new ResearchException("raw-study-input-unavailable");
                var input = DatasetArchive.ReadBounded(path);
                if ((total += input.Length) > RawReplayArchive.MaximumDatasetBytes) throw new ResearchException("raw-study-input-capacity");
                return input;
            }
            foreach (var pin in plan.Calendars)
            {
                var input = Inbox(pin.Sha256, "calendar"); var calendar = RawStudyEvidence.Calendar(input);
                if (calendar.Sha256 != pin.Sha256 || calendar.Reference.Id != pin.CalendarId || calendar.Venue != pin.Venue ||
                    !file.Manifest.Calendars.Contains(calendar.Reference)) throw new ResearchException("raw-study-calendar-binding");
                var zone = TimeZoneInfo.FindSystemTimeZoneById(calendar.Reference.TimeZone);
                if (calendar.Days[0].Date > DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(plan.Decisions[0], zone).DateTime) ||
                    calendar.Days[^1].Date < DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(plan.ReservedEnd, zone).DateTime))
                    throw new ResearchException("raw-study-calendar-coverage");
                originals.Add((new("calendar", pin.Sha256, RawHash(input), input.Length), input));
            }
            if (file.Observations.OfType<Listing>().Any(l => !plan.Calendars.Any(p => p.CalendarId == l.CalendarId && p.Venue == l.Venue)))
                throw new ResearchException("raw-study-calendar-binding");
            var actionBytes = Inbox(plan.ActionCatalogSha256, "actions");
            if (RawHash(actionBytes) != plan.ActionCatalogSha256) throw new ResearchException("raw-study-input-integrity");
            var actions = RawStudyEvidence.Actions(actionBytes);
            var subjects = new HashSet<(string, string)>(); var points = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in actions)
            {
                if (action is RawDividendSchedule { Evidence: null } or RawTerminalSchedule { Evidence: null } or
                    RawDividendSchedule { Payment: { Payment: null } } or RawTerminalSchedule { Settlement: { Settlement: null } } ||
                    !subjects.Add((action.ListingId, action.ActionId))) throw new ResearchException("raw-study-actions");
                var observed = file.Observations.OfType<CorporateAction>().SingleOrDefault(a => a.Id == action.ObservationId && a.ListingId == action.ListingId && a.ActionId == action.ActionId);
                if (observed is null) throw new ResearchException("raw-study-action-binding");
                if (action is RawStudyDividendSchedule or RawStudyTerminalSchedule)
                    RawStudyActionTerms.Validate(action, observed, plan.DatasetSha256);
                else if (!(action is RawSplitSchedule && observed.Action == CorporateActionKind.Split ||
                    action is RawDividendSchedule && observed.Action == CorporateActionKind.Dividend ||
                    action is RawTerminalSchedule && observed.Action is CorporateActionKind.Delisting or CorporateActionKind.Merger))
                    throw new ResearchException("raw-study-action-binding");
                var bookings = new List<RawSchedulePoint> { action.Capture };
                if (action is RawDividendSchedule { Payment: { } payment }) bookings.Add(payment.Booking);
                if (action is RawTerminalSchedule { Settlement: { } settlement }) bookings.Add(settlement.Booking);
                if (action is RawStudyDividendSchedule { Delivery: { } income }) bookings.Add(income.Booking);
                if (action is RawStudyTerminalSchedule { Delivery: { } delivery }) bookings.Add(delivery.Booking);
                foreach (var point in bookings)
                    if (point is null || string.IsNullOrWhiteSpace(point.Id) || point.Id.Length > 128 || point.TieOrder < 0 ||
                        point.DatasetSha256 != plan.DatasetSha256 || !points.Add(point.Id)) throw new ResearchException("raw-study-action-binding");
            }
            originals.Add((new("actions", plan.ActionCatalogSha256, plan.ActionCatalogSha256, actionBytes.Length), actionBytes));
            var studyId = Guid.NewGuid().ToString("N"); var at = clock.GetUtcNow();
            var rows = plan.DevelopmentTrials.Select(t =>
            {
                var requestBytes = RawBytes(new RawStudyTrialRequest(studyId, planSha, t.Id, "development"));
                return (Entry: new RawStudyTrialEntry(Guid.NewGuid().ToString("N"), studyId, t.Id, t.CandidateId, t.CostScenario,
                    "planned", at, plan.DatasetSha256, plan.Synthetic, plan.CodeSha256, RawHash(requestBytes), window.Start, window.End), Bytes: requestBytes);
            }).ToArray();
            var study = new RawResearchStudy(studyId, request.RequestId, requestSha, request.Name, "registered", at, commit, plan, planSha,
                Convert.ToBase64String(bytes), models.Select(r => new PriorStudyTrial(r.Id, r.DatasetId, r.ConfigSha256, r.Status, r.ReportSha256)).ToImmutableArray(),
                universe.ToImmutableArray(), raw.ToImmutableArray(), rows.Select(r => r.Entry.Id).ToImmutableArray(),
                originals.Select(o => o.Original).Prepend(new("dataset", plan.DatasetSha256, plan.DatasetSha256, datasetBytes.Length)).ToImmutableArray());
            var stored = RawBytes(study);
            if (stored.Length > MaximumStudyPayload) throw new ResearchException("study-payload-capacity");
            using var db = Open(); using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO raw_studies VALUES($id,$request,$json,$sha)";
            cmd.Parameters.AddWithValue("$id", study.Id); cmd.Parameters.AddWithValue("$request", study.RequestId);
            cmd.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(stored)); cmd.Parameters.AddWithValue("$sha", RawHash(stored)); cmd.ExecuteNonQuery();
            foreach (var row in rows)
            {
                using var insert = db.CreateCommand(); insert.Transaction = tx; insert.CommandText = "INSERT INTO raw_trials VALUES($id,$study,$json,'planned')";
                insert.Parameters.AddWithValue("$id", row.Entry.Id); insert.Parameters.AddWithValue("$study", study.Id);
                insert.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(RawBytes(row.Entry))); insert.ExecuteNonQuery();
            }
            EnsureArtifactCapacity(stored);
            foreach (var original in originals)
            {
                EnsureArtifactCapacity(original.Bytes); var path = RawOriginalPath(original.Original.Kind, original.Original.OriginalSha256);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); ImmutableWrite(path, original.Bytes);
            }
            foreach (var row in rows)
            {
                EnsureArtifactCapacity(row.Bytes); var directory = Path.Combine(Root, "runs", row.Entry.Id); Directory.CreateDirectory(directory);
                ImmutableWrite(Path.Combine(directory, "raw-study-request.json"), row.Bytes);
            }
            cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO raw_study_events(study_id,at,kind) VALUES($id,$at,'registered-before-work')";
            cmd.Parameters.AddWithValue("$id", study.Id); cmd.Parameters.AddWithValue("$at", at.ToString("O")); cmd.ExecuteNonQuery();
            tx.Commit(); return study;
        }
    }
}
