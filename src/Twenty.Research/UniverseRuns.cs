using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed record UniverseDatasetImportRequest(string DatasetSha256);
public sealed record UniverseRunRequest(string RequestId, string DatasetSha256, string UniverseId,
    ImmutableArray<DateTimeOffset> Decisions, int MaximumPriceAgeDays, UniverseBacktestConfig Config, ResearchCosts Costs);
public sealed record UniverseRunEntry(string Id, string DatasetSha256, string DatasetId, bool Synthetic, string UniverseId,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, string? ErrorCode, string CodeCommit,
    string RequestSha256, string? PanelSha256 = null, string? ReportSha256 = null,
    DateOnly? InformationStart = null, DateOnly? InformationEnd = null,
    string? IdentityScheme = null, string? ResearchSha256 = null, string? RequestArtifactSha256 = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StudyId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StudyCandidateId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StudyStage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? StartedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? InformationStartAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? InformationEndAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? WorkerCommit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool CancelRequested { get; init; }
}

/// <summary>Registered changing-universe trials. Every admitted attempt is inserted as
/// "started" before any computation and reaches exactly one terminal state, so failures
/// (gap panels, missing terminal proceeds, input loss) count as trials and cannot be
/// silently retried away. Replaying a request id returns the original entry; changing
/// its semantics under the same id is a conflict. Execution is synchronous in v1: the
/// 8 MiB dataset bound and 600-decision cap keep it bounded, and an interrupted process
/// leaves "started" rows that store opening marks "interrupted".</summary>
public sealed partial class ResearchStore
{
    private static void InitialiseUniverseRuns(SqliteConnection db) => Execute(db,
        "CREATE TABLE IF NOT EXISTS universe_runs(id TEXT PRIMARY KEY, request_id TEXT NOT NULL UNIQUE, dataset_sha TEXT NOT NULL, json TEXT NOT NULL, status TEXT NOT NULL);" +
        "CREATE TABLE IF NOT EXISTS universe_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL, at TEXT NOT NULL, kind TEXT NOT NULL);");

    private string UniverseDatasetPath(string sha256) => Path.Combine(Root, "universe-datasets", sha256 + ".json");

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);

    public string ImportUniverseDataset(byte[] bytes)
    {
        var file = DatasetJson.Read(bytes);
        var validation = DatasetValidator.Validate(file);
        if (!validation.IsValid) throw new InvalidDatasetException(validation);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        lock (gate)
        {
            var directory = Path.Combine(Root, "universe-datasets"); Directory.CreateDirectory(directory);
            if (!File.Exists(UniverseDatasetPath(sha)) && Directory.EnumerateFiles(directory, "*.json").Count() >= 20)
                throw new ResearchException("dataset-capacity");
            ImmutableWrite(UniverseDatasetPath(sha), bytes);
        }
        return sha;
    }

    public string ImportUniverseDatasetInbox(UniverseDatasetImportRequest request)
    {
        if (!IsSha256(request.DatasetSha256)) throw new ResearchException("import-dataset-id");
        var path = Path.Combine(Root, "imports", request.DatasetSha256 + ".dataset.json");
        if (!File.Exists(path)) throw new ResearchException("import-dataset-not-found");
        var bytes = DatasetArchive.ReadBounded(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != request.DatasetSha256)
            throw new ResearchException("import-dataset-integrity");
        return ImportUniverseDataset(bytes);
    }

    public UniverseRunEntry ExecuteUniverseRun(UniverseRunRequest request, string codeCommit)
    {
        if (!TryBeginComputation())
        {
            // A replay is a read, even while the shared computation slot is busy.
            lock (gate)
            {
                if (request is not null && request.Config is not null && request.Costs is not null)
                {
                    using var db = Open(); using var lookup = db.CreateCommand();
                    lookup.CommandText = "SELECT json FROM universe_runs WHERE request_id=$request";
                    lookup.Parameters.AddWithValue("$request", request.RequestId ?? "");
                    if (lookup.ExecuteScalar() is string existing)
                    {
                        var prior = JsonSerializer.Deserialize<UniverseRunEntry>(existing, Json)!;
                        if (prior.RequestSha256 != UniverseRequestHash(request)) throw new ResearchException("request-id-conflict");
                        return prior;
                    }
                }
            }
            throw new ResearchException("research-worker-busy");
        }
        try { return ExecuteUniverseRunCore(request, codeCommit); }
        finally { EndComputation(); }
    }

    private UniverseRunEntry ExecuteUniverseRunCore(UniverseRunRequest request, string codeCommit)
    {
        if (request is null || !Guid.TryParseExact(request.RequestId, "D", out _) || !IsSha256(request.DatasetSha256) ||
            request.UniverseId is not { Length: >= 1 and <= 128 } || request.Decisions.IsDefault ||
            request.MaximumPriceAgeDays is < 1 or > 92 || request.Config is null || request.Costs is null)
            throw new ResearchException("universe-run-request");
        request.Config.Validate(); request.Costs.Validate();
        var requestHash = UniverseRequestHash(request);
        UniverseRunEntry entry;
        DatasetFile datasetFile;
        lock (gate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction();
            using var lookup = db.CreateCommand(); lookup.Transaction = transaction;
            lookup.CommandText = "SELECT json FROM universe_runs WHERE request_id=$request";
            lookup.Parameters.AddWithValue("$request", request.RequestId);
            if (lookup.ExecuteScalar() is string existing)
            {
                var prior = JsonSerializer.Deserialize<UniverseRunEntry>(existing, Json)!;
                if (prior.RequestSha256 != requestHash) throw new ResearchException("request-id-conflict");
                return prior;
            }
            if (request.Decisions.Length is < 2 or > 600 || request.Decisions.Any(d => d == default))
                throw new ResearchException("universe-run-request");
            if (!File.Exists(UniverseDatasetPath(request.DatasetSha256))) throw new ResearchException("dataset-not-found");
            var bytes = DatasetArchive.ReadBounded(UniverseDatasetPath(request.DatasetSha256));
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != request.DatasetSha256) throw new ResearchException("dataset-integrity");
            datasetFile = DatasetJson.Read(bytes);
            // Include warm-up returns and the bounded stale-price lookback, not just the
            // later evaluated periods. Persist this before any panel/return computation.
            var first = DateOnly.FromDateTime(request.Decisions.Min().UtcDateTime);
            var informationStart = DateOnly.FromDayNumber(Math.Max(0, first.DayNumber - request.MaximumPriceAgeDays));
            var informationEnd = DateOnly.FromDateTime(request.Decisions.Max().UtcDateTime);
            var window = UniverseInformationWindow(request);
            if (!datasetFile.Manifest.Synthetic)
                GuardStudyReservations(window.Start, window.End);
            using var count = db.CreateCommand(); count.Transaction = transaction;
            count.CommandText = "SELECT (SELECT COUNT(*) FROM universe_runs) + (SELECT COUNT(*) FROM runs) + (SELECT COUNT(*) FROM raw_trials)";
            if ((long)count.ExecuteScalar()! + ReservedStudySlots() >= 1000) throw new ResearchException("trial-capacity");
            entry = new(Guid.NewGuid().ToString("N"), request.DatasetSha256, datasetFile.Manifest.DatasetId,
                datasetFile.Manifest.Synthetic, request.UniverseId, "started", clock.GetUtcNow(), null, null,
                codeCommit, requestHash, InformationStart: informationStart, InformationEnd: informationEnd,
                IdentityScheme: ResearchIdentity.Scheme, ResearchSha256: ResearchIdentity.Current.Sha256)
                { InformationStartAt = window.Start, InformationEndAt = window.End, StartedAt = clock.GetUtcNow(), WorkerCommit = codeCommit };
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
            EnsureArtifactCapacity(requestBytes);
            var directory = Path.Combine(Root, "runs", entry.Id); Directory.CreateDirectory(directory);
            ImmutableWrite(Path.Combine(directory, "universe-request.json"), requestBytes);
            entry = entry with { RequestArtifactSha256 = Convert.ToHexStringLower(SHA256.HashData(requestBytes)) };
            using var insert = db.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO universe_runs(id,request_id,dataset_sha,json,status) VALUES($id,$request,$dataset,$json,'started')";
            insert.Parameters.AddWithValue("$id", entry.Id); insert.Parameters.AddWithValue("$request", request.RequestId);
            insert.Parameters.AddWithValue("$dataset", request.DatasetSha256);
            insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry, Json)); insert.ExecuteNonQuery();
            UniverseEvent(db, transaction, entry.Id, "started"); transaction.Commit();
        }
        return ComputeUniverseRun(entry, request, datasetFile);
    }

    private UniverseRunEntry ComputeUniverseRun(UniverseRunEntry entry, UniverseRunRequest request, DatasetFile datasetFile,
        CancellationToken ct = default)
    {
        string? panelSha = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var panel = ChangingUniversePanel.FromDataset(new PointInTimeDataset(datasetFile), request.UniverseId,
                request.Decisions, TimeSpan.FromDays(request.MaximumPriceAgeDays));
            panelSha = panel.Sha256;
            var report = UniverseBacktest.Run(panel, request.Config, request.Costs, ct);
            ct.ThrowIfCancellationRequested();
            var reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, Json);
            lock (gate)
            {
                ct.ThrowIfCancellationRequested();
                if (FindUniverseRun(entry.Id)?.CancelRequested == true) throw new ResearchException("user-cancelled");
                EnsureArtifactCapacity(reportBytes);
                ImmutableWrite(Path.Combine(Root, "runs", entry.Id, "universe-report.json"), reportBytes);
                entry = entry with
                {
                    Status = "succeeded", FinishedAt = clock.GetUtcNow(), PanelSha256 = panelSha,
                    ReportSha256 = Convert.ToHexStringLower(SHA256.HashData(reportBytes))
                };
                SaveUniverseRun(entry, "succeeded");
            }
        }
        catch (Exception error)
        {
            var code = error switch
            {
                OperationCanceledException => "deadline-or-shutdown",
                ResearchException re => re.Code,
                InvalidDatasetException ide => ide.Validation.Issues.FirstOrDefault()?.Code ?? "dataset-invalid",
                _ => "universe-run-input-unavailable"
            };
            entry = entry with { Status = code == "user-cancelled" ? "cancelled" : error is OperationCanceledException ? "interrupted" : "failed",
                FinishedAt = clock.GetUtcNow(), ErrorCode = code, PanelSha256 = panelSha, CancelRequested = FindUniverseRun(entry.Id)?.CancelRequested ?? false };
            SaveUniverseRun(entry, entry.Status);
        }
        return entry;
    }

    public List<UniverseRunEntry> ListUniverseRuns()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT json FROM universe_runs ORDER BY rowid DESC LIMIT 1000";
        using var reader = command.ExecuteReader(); var rows = new List<UniverseRunEntry>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<UniverseRunEntry>(reader.GetString(0), Json)!);
        return rows;
    }

    public UniverseRunEntry? FindUniverseRun(string id) => ListUniverseRuns().SingleOrDefault(x => x.Id == id);

    private static string UniverseRequestHash(UniverseRunRequest request) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { request.DatasetSha256, request.UniverseId, request.Decisions,
            request.MaximumPriceAgeDays, request.Config, request.Costs }, Json)));

    public byte[]? UniverseRequestBytes(string id)
    {
        var entry = FindUniverseRun(id);
        if (entry is null) return null;
        return UniverseRequestBytes(entry);
    }

    internal byte[] UniverseRequestBytes(UniverseRunEntry entry)
    {
        if (entry.RequestArtifactSha256 is null) throw new ResearchException("universe-request-unavailable");
        var bytes = DatasetArchive.ReadBounded(Path.Combine(Root, "runs", entry.Id, "universe-request.json"));
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != entry.RequestArtifactSha256)
            throw new ResearchException("universe-request-integrity");
        var request = JsonSerializer.Deserialize<UniverseRunRequest>(bytes, Json);
        if (request is null || UniverseRequestHash(request) != entry.RequestSha256)
            throw new ResearchException("universe-request-integrity");
        return bytes;
    }

    public UniverseBacktestReport? UniverseReport(string id)
    {
        var run = FindUniverseRun(id);
        if (run?.Status != "succeeded") return null;
        return UniverseReport(run);
    }

    internal UniverseBacktestReport? UniverseReport(UniverseRunEntry run)
    {
        var bytes = DatasetArchive.ReadBounded(Path.Combine(Root, "runs", run.Id, "universe-report.json"));
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != run.ReportSha256) throw new ResearchException("report-integrity");
        return JsonSerializer.Deserialize<UniverseBacktestReport>(bytes, Json);
    }

    private void SaveUniverseRun(UniverseRunEntry entry, string kind)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "UPDATE universe_runs SET json=$json,status=$status WHERE id=$id";
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry, Json));
            cmd.Parameters.AddWithValue("$status", entry.Status); cmd.Parameters.AddWithValue("$id", entry.Id);
            if (cmd.ExecuteNonQuery() != 1) throw new ResearchException("run-not-found");
            UniverseEvent(db, tx, entry.Id, kind); tx.Commit();
        }
    }

    private void UniverseEvent(SqliteConnection db, SqliteTransaction tx, string id, string kind)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO universe_events(run_id,at,kind) VALUES($id,$at,$kind)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$at", clock.GetUtcNow().ToString("O")); cmd.Parameters.AddWithValue("$kind", kind);
        cmd.ExecuteNonQuery();
    }

    private void InterruptStaleUniverseRuns()
    {
        foreach (var run in ListUniverseRuns().Where(r => r.Status == "started"))
            SaveUniverseRun(run with { Status = "interrupted", FinishedAt = clock.GetUtcNow(), ErrorCode = "process-restarted" }, "restart-interrupted");
    }
}
