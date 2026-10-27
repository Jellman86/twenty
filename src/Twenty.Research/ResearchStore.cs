using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed record DatasetEntry(string Id, string Name, string Sha256, bool Synthetic, int Months, int Assets, DateOnly Start, DateOnly End, DateTimeOffset? RegisteredAt = null, bool ResearchOnly = false, string? ResearchOnlyReason = null);
public sealed record InboxImportRequest(string PanelId, string Name);
public sealed record RunRequest(string RequestId, string DatasetId, ExperimentConfig Config, ResearchCosts Costs);
public sealed record RunEntry(string Id, string DatasetId, string DatasetName, bool Synthetic, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int Progress, string? ErrorCode, string CodeCommit, string ConfigSha256,
    ExperimentConfig Config, ResearchCosts Costs, bool CancelRequested, string? ReportSha256 = null, string? WorkerCommit = null, string? EngineSha256 = null,
    string? StudyId = null, string? StudyCandidateId = null, string? StudyStage = null);

public sealed partial class ResearchStore : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 64 };
    public string Root { get; }
    private readonly string connectionString;
    private readonly FileStream lease;
    private readonly object gate = new();
    private readonly SemaphoreSlim computation = new(1, 1);
    internal bool TryBeginComputation() => computation.Wait(0);
    internal void EndComputation() => computation.Release();
    private readonly TimeProvider clock;
    public ResearchStore(string root, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        lease = new FileStream(Path.Combine(Root, ".app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Directory.CreateDirectory(Path.Combine(Root, "datasets")); Directory.CreateDirectory(Path.Combine(Root, "runs"));
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "research.db"), DefaultTimeout = 10, Pooling = false }.ToString();
        using var db = Open();
        Execute(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS datasets(id TEXT PRIMARY KEY, json TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY, request_id TEXT NOT NULL UNIQUE, dataset_id TEXT NOT NULL, json TEXT NOT NULL, status TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS events(sequence INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL, at TEXT NOT NULL, kind TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS holdout_access(dataset_id TEXT NOT NULL, run_id TEXT NOT NULL UNIQUE, at TEXT NOT NULL);" );
        InitialiseStudies(db);
        InitialiseUniverseRuns(db);
        InitialiseUniverseStudies(db);
        InitialiseRawStudies(db);
        InitialiseRawPaper(db);
        foreach (var run in ListRuns().Where(r => r.Status == "running")) Save(run with { Status = "interrupted", FinishedAt = this.clock.GetUtcNow(), ErrorCode = "process-restarted" }, "restart-interrupted");
        InterruptStaleUniverseRuns();
        InterruptStaleRawRuns();
        InterruptRawPaper();
    }
    public void Dispose() => lease.Dispose();
    public DatasetEntry ImportInbox(InboxImportRequest request)
    {
        if (request.PanelId is not { Length: 64 } || request.PanelId.Any(c => !char.IsAsciiHexDigitLower(c))) throw new ResearchException("import-panel-id");
        var directory = Path.Combine(Root, "imports");
        if (!File.Exists(Path.Combine(directory, request.PanelId + ".csv")) || !File.Exists(Path.Combine(directory, request.PanelId + ".metadata.json"))) throw new ResearchException("import-panel-not-found");
        var csv = Twenty.Core.Data.DatasetArchive.ReadBounded(Path.Combine(directory, request.PanelId + ".csv"));
        var metadata = Twenty.Core.Data.DatasetArchive.ReadBounded(Path.Combine(directory, request.PanelId + ".metadata.json"));
        if (MonthlyPanel.Read(csv, metadata).Sha256 != request.PanelId) throw new ResearchException("import-panel-integrity");
        return Import(csv, metadata, request.Name);
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    private static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public DatasetEntry Import(byte[] csv, byte[] metadata, string name)
    {
        var panel = MonthlyPanel.Read(csv, metadata);
        if (name is not { Length: >= 1 and <= 80 }) throw new ResearchException("dataset-name");
        lock (gate)
        {
            if (Datasets().Count >= 20 && Datasets().All(x => x.Id != panel.Sha256)) throw new ResearchException("dataset-capacity");
            var directory = Path.Combine(Root, "datasets", panel.Sha256); Directory.CreateDirectory(directory);
            EnsureNewArtifactFiles((Path.Combine(directory, "prices.csv"), csv), (Path.Combine(directory, "prices.metadata.json"), metadata));
            ImmutableWrite(Path.Combine(directory, "prices.csv"), csv); ImmutableWrite(Path.Combine(directory, "prices.metadata.json"), metadata);
            var entry = new DatasetEntry(panel.Sha256, name, panel.Sha256, panel.Synthetic, panel.Dates.Length, panel.Assets.Length, panel.Dates[0], panel.Dates[^1], this.clock.GetUtcNow(), panel.ResearchOnly, panel.ResearchOnlyReason);
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO datasets(id,json) VALUES($id,$json)";
            command.Parameters.AddWithValue("$id", entry.Id); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry, Json)); command.ExecuteNonQuery();
            var stored = Datasets().Single(d => d.Id == entry.Id);
            if (stored.RegisteredAt is null)
            {
                stored = stored with { RegisteredAt = entry.RegisteredAt };
                using var update = db.CreateCommand(); update.CommandText = "UPDATE datasets SET json=$json WHERE id=$id";
                update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(stored, Json)); update.Parameters.AddWithValue("$id", stored.Id); update.ExecuteNonQuery();
            }
            return stored;
        }
    }
    public List<DatasetEntry> Datasets()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM datasets ORDER BY id";
        using var reader = command.ExecuteReader(); var rows = new List<DatasetEntry>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<DatasetEntry>(reader.GetString(0), Json)!);
        return rows;
    }
    public MonthlyPanel Panel(string id)
    {
        if (id is null || id.Length != 64 || id.Any(c => !char.IsAsciiHexDigitLower(c)) || !Datasets().Any(x => x.Id == id)) throw new ResearchException("dataset-not-found");
        var directory = Path.Combine(Root, "datasets", id);
        var csv = Twenty.Core.Data.DatasetArchive.ReadBounded(Path.Combine(directory, "prices.csv"));
        var metadata = Twenty.Core.Data.DatasetArchive.ReadBounded(Path.Combine(directory, "prices.metadata.json"));
        var panel = MonthlyPanel.Read(csv, metadata);
        if (panel.Sha256 != id) throw new ResearchException("dataset-integrity");
        return panel;
    }
    public RunEntry Enqueue(RunRequest request, string codeCommit)
    {
        if (!Guid.TryParseExact(request.RequestId, "D", out _) || request.Config is null || request.Costs is null) throw new ResearchException("run-request");
        var panel = Panel(request.DatasetId); request.Config.Validate(panel); request.Costs.Validate();
        var configHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request.DatasetId, request.Config, request.Costs }, Json)));
        lock (gate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction();
            using var lookup = db.CreateCommand(); lookup.Transaction = transaction;
            lookup.CommandText = "SELECT json FROM runs WHERE request_id=$request"; lookup.Parameters.AddWithValue("$request", request.RequestId);
            if (lookup.ExecuteScalar() is string existing)
            {
                var prior = JsonSerializer.Deserialize<RunEntry>(existing, Json)!;
                // Compare resolved semantics across DTO upgrades; retain the original serialized-config hash as historical evidence.
                if (prior.DatasetId != request.DatasetId || prior.Config != request.Config || prior.Costs != request.Costs)
                    throw new ResearchException("request-id-conflict");
                return prior;
            }
            using var count = db.CreateCommand(); count.Transaction = transaction;
            count.CommandText = "SELECT (SELECT COUNT(*) FROM runs) + (SELECT COUNT(*) FROM universe_runs) + (SELECT COUNT(*) FROM raw_trials)";
            if ((long)count.ExecuteScalar()! + ReservedStudySlots() >= 1000) throw new ResearchException("trial-capacity");
            count.CommandText = "SELECT COUNT(*) FROM runs WHERE status IN ('queued','running')";
            if ((long)count.ExecuteScalar()! >= 10) throw new ResearchException("queue-capacity");
            // The initial app deliberately does not expose a final holdout run. Record/review promotion first.
            if (request.Config.EvaluateHoldout) throw new ResearchException("holdout-gate-closed");
            GuardStudyReservations(panel, request.Config);
            var dataset = Datasets().Single(x => x.Id == request.DatasetId);
            var run = new RunEntry(Guid.NewGuid().ToString("N"), dataset.Id, dataset.Name, dataset.Synthetic, "queued", this.clock.GetUtcNow(),
                null, null, 0, null, codeCommit, configHash, request.Config, request.Costs, false);
            using var insert = db.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO runs(id,request_id,dataset_id,json,status) VALUES($id,$request,$dataset,$json,'queued')";
            insert.Parameters.AddWithValue("$id", run.Id); insert.Parameters.AddWithValue("$request", request.RequestId);
            insert.Parameters.AddWithValue("$dataset", dataset.Id); insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(run, Json)); insert.ExecuteNonQuery();
            Event(db, transaction, run.Id, "queued"); transaction.Commit();
            return run;
        }
    }
    public List<RunEntry> ListRuns()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM runs ORDER BY rowid DESC LIMIT 1000";
        using var reader = command.ExecuteReader(); var rows = new List<RunEntry>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<RunEntry>(reader.GetString(0), Json)!);
        return rows;
    }
    public RunEntry? Find(string id) => ListRuns().SingleOrDefault(x => x.Id == id);
    public RunEntry? Claim()
    {
        lock (gate)
        {
            AdvanceStudies();
            while (true)
            {
                var queued = ListRuns().LastOrDefault(x => x.Status is "queued" or "planned");
                if (queued is null) return null;
                try { if (!ValidateStudyStart(queued)) continue; }
                catch (Exception error) when (error is ResearchException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                {
                    var code = error is ResearchException re ? re.Code : "claim-input-unavailable";
                    Save(queued with { Status = "failed", FinishedAt = clock.GetUtcNow(), ErrorCode = code }, "claim-rejected");
                    if (queued.StudyId is { } studyId && Study(studyId) is { } study) FailStudy(study, code);
                    continue;
                }
                var run = queued with { Status = "running", StartedAt = this.clock.GetUtcNow(), WorkerCommit = Environment.GetEnvironmentVariable("TWENTY_COMMIT") ?? "local",
                    EngineSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(Backtest).Assembly.Location))) };
                Save(run, "started"); return run;
            }
        }
    }
    public void Cancel(string id)
    {
        lock (gate)
        {
            var run = Find(id) ?? throw new ResearchException("run-not-found");
            if (run.Status is "queued" or "planned" or "running") Save(run with { CancelRequested = true, Status = run.Status != "running" ? "cancelled" : "running",
                FinishedAt = run.Status != "running" ? this.clock.GetUtcNow() : null }, "cancel-requested");
        }
    }
    public void Save(RunEntry run, string kind)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "UPDATE runs SET json=$json,status=$status WHERE id=$id";
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(run, Json)); cmd.Parameters.AddWithValue("$status", run.Status); cmd.Parameters.AddWithValue("$id", run.Id);
            if (cmd.ExecuteNonQuery() != 1) throw new ResearchException("run-not-found");
            Event(db, tx, run.Id, kind); tx.Commit();
        }
    }
    private void Event(SqliteConnection db, SqliteTransaction tx, string id, string kind)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events(run_id,at,kind) VALUES($id,$at,$kind)"; cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$at", this.clock.GetUtcNow().ToString("O")); cmd.Parameters.AddWithValue("$kind", kind); cmd.ExecuteNonQuery();
    }
    public void Complete(string id, BacktestReport report)
    {
        lock (gate)
        {
            var run = Find(id) ?? throw new ResearchException("run-not-found");
            if (run.CancelRequested) throw new OperationCanceledException();
            ValidateStudyReport(run, report);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json);
            EnsureArtifactCapacity(bytes);
            var directory = Path.Combine(Root, "runs", run.Id); Directory.CreateDirectory(directory);
            ImmutableWrite(Path.Combine(directory, "report.json"), bytes);
            Save(run with { Status = "succeeded", Progress = 100, FinishedAt = this.clock.GetUtcNow(),
                ReportSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }, "succeeded");
        }
    }
    public void Progress(string id, int percent)
    {
        lock (gate)
        {
            var run = Find(id)!;
            if (run.CancelRequested) throw new OperationCanceledException();
            Save(run with { Progress = percent }, "progress");
        }
    }
    public BacktestReport? Report(string id)
    {
        var run = Find(id);
        if (run?.Status != "succeeded") return null;
        var bytes = Twenty.Core.Data.DatasetArchive.ReadBounded(Path.Combine(Root, "runs", run.Id, "report.json"));
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != run.ReportSha256) throw new ResearchException("report-integrity");
        return JsonSerializer.Deserialize<BacktestReport>(bytes, Json);
    }
    private void EnsureArtifactCapacity(byte[] bytes)
    {
        if (bytes.Length > 8 * 1024 * 1024) throw new ResearchException("artifact-capacity");
        EnsureAdditionalArtifactCapacity(bytes.Length);
    }
    private void EnsureNewArtifactFiles(params (string Path, byte[] Bytes)[] files)
    {
        var additional = files.Where(f => !File.Exists(f.Path)).Sum(f => (long)f.Bytes.Length);
        if (additional > 0) EnsureAdditionalArtifactCapacity(additional);
    }
    private void EnsureAdditionalArtifactCapacity(long additional, string? publicationOwner = null)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT trial_id,bytes FROM raw_final_headroom";
        using var reader = command.ExecuteReader(); long held = 0; var owned = publicationOwner is null;
        while (reader.Read())
        {
            var amount = reader.GetInt64(1);
            if (amount != 0 && amount != RawFinalHeadroomBytes) throw new ResearchException("raw-study-publication-reservation-integrity");
            if (reader.GetString(0) == publicationOwner)
            { if (amount != RawFinalHeadroomBytes) throw new ResearchException("raw-study-publication-reservation-integrity"); owned = true; }
            else held = checked(held + amount);
        }
        if (!owned) throw new ResearchException("raw-study-publication-reservation-integrity");
        if (additional < 0 || ArtifactBytes(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".app.lock", StringComparison.Ordinal))) + additional + held > 256L * 1024 * 1024)
            throw new ResearchException("artifact-capacity");
    }
    private static long ArtifactBytes(IEnumerable<string> paths)
    {
        long bytes = 0;
        foreach (var path in paths)
        {
            try { bytes = checked(bytes + new FileInfo(path).Length); }
            // SQLite sidecars and completed temporary writes can disappear after
            // enumeration. Missing files consume no quota; other I/O errors still fail.
            catch (FileNotFoundException) { }
        }
        return bytes;
    }

    private static void ImmutableWrite(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length != bytes.Length || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new ResearchException("artifact-integrity");
            return;
        }
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
