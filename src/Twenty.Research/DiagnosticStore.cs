using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed record DiagnosticRequest(string RequestId, string AnchorRunId);
public sealed record DiagnosticTrial(string RunId, string DatasetId, string Status, string ConfigSha256,
    string? ReportSha256, string? EngineSha256, bool Included, string Reason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResearchEngine { get; init; }
}
public sealed record DiagnosticAttempt(string Id, string RequestId, string AnchorRunId, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, string? ErrorCode, string WorkerCommit,
    string DiagnosticEngineSha256, string? DatasetId, bool? Synthetic,
    ImmutableArray<DiagnosticTrial> Trials, SearchDiagnosticReport? Report, DateTimeOffset? RegistrySnapshotAt = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FamilyEngine { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResearchSha256 { get; init; }
}

/// <summary>A bounded journal of analysis attempts. Its family is derived from the complete local register, never a caller-supplied winner shortlist.</summary>
public sealed partial class DiagnosticStore
{
    private readonly ResearchStore research;
    private readonly string connectionString;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly SemaphoreSlim worker = new(1, 1);
    private const int MaximumAttempts = 200, MaximumPayload = 1024 * 1024;

    public DiagnosticStore(ResearchStore research, TimeProvider? clock = null)
    {
        this.research = research; this.clock = clock ?? TimeProvider.System;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(research.Root, "research.db"), Pooling = false }.ToString();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS research_diagnostics(id TEXT PRIMARY KEY, request_id TEXT NOT NULL UNIQUE, status TEXT NOT NULL, json TEXT NOT NULL, sha256 TEXT NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT json,sha256 FROM research_diagnostics WHERE status='running'";
        using var reader = command.ExecuteReader(); var interrupted = new List<DiagnosticAttempt>();
        while (reader.Read()) interrupted.Add(Read(reader.GetString(0), reader.GetString(1)));
        reader.Close();
        foreach (var attempt in interrupted) Save(attempt with { Status = "interrupted", FinishedAt = this.clock.GetUtcNow(), ErrorCode = "process-restarted" });
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    private static string Hash(string json) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    private static DiagnosticAttempt Read(string json, string hash)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumPayload || Hash(json) != hash) throw new ResearchException("diagnostic-integrity");
        return JsonSerializer.Deserialize<DiagnosticAttempt>(json, ResearchStore.Json) ?? throw new ResearchException("diagnostic-integrity");
    }
    public DiagnosticAttempt? Find(string id)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT json,sha256 FROM research_diagnostics WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader(); return reader.Read() ? Read(reader.GetString(0), reader.GetString(1)) : null;
    }
    public object History()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM research_diagnostics";
        var total = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT json,sha256 FROM research_diagnostics ORDER BY rowid DESC LIMIT 20";
        using var reader = command.ExecuteReader(); var result = new List<object>();
        while (reader.Read())
        {
            var a = Read(reader.GetString(0), reader.GetString(1));
            result.Add(new { a.Id, a.AnchorRunId, a.Status, a.CreatedAt, a.FinishedAt, a.ErrorCode,
                registered = a.Trials.Length, included = a.Trials.Count(t => t.Included), months = a.Report?.Months });
        }
        return new { total, displayedLimit = 20, attempts = result };
    }
    private void Save(DiagnosticAttempt attempt, bool insert = false)
    {
        var json = JsonSerializer.Serialize(attempt, ResearchStore.Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumPayload) throw new ResearchException("diagnostic-payload-capacity");
        using var db = Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(SUM(length(CAST(json AS BLOB))),0) FROM research_diagnostics WHERE id!=$id";
        command.Parameters.AddWithValue("$id", attempt.Id);
        // Admit a request only when its maximum permitted terminal payload fits. The single diagnostic worker owns this headroom until completion.
        var requiredBytes = insert ? MaximumPayload : System.Text.Encoding.UTF8.GetByteCount(json);
        if ((long)command.ExecuteScalar()! + requiredBytes > 32L * 1024 * 1024) throw new ResearchException("diagnostic-storage-capacity");
        command.CommandText = insert
            ? "INSERT INTO research_diagnostics(id,request_id,status,json,sha256) VALUES($id,$request,$status,$json,$sha)"
            : "UPDATE research_diagnostics SET status=$status,json=$json,sha256=$sha WHERE id=$id";
        command.Parameters.AddWithValue("$request", attempt.RequestId); command.Parameters.AddWithValue("$status", attempt.Status);
        command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$sha", Hash(json));
        if (command.ExecuteNonQuery() != 1) throw new ResearchException("diagnostic-not-found"); transaction.Commit();
    }
    public async Task<DiagnosticAttempt> Create(DiagnosticRequest request, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(request.RequestId, "D", out _) || request.AnchorRunId is not { Length: 32 } || request.AnchorRunId.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ResearchException("diagnostic-request");
        DiagnosticAttempt attempt;
        lock (gate)
        {
            using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json,sha256 FROM research_diagnostics WHERE request_id=$request";
            command.Parameters.AddWithValue("$request", request.RequestId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var prior = Read(reader.GetString(0), reader.GetString(1));
                if (prior.AnchorRunId != request.AnchorRunId) throw new ResearchException("request-id-conflict");
                return prior;
            }
            reader.Close(); command.CommandText = "SELECT COUNT(*) FROM research_diagnostics";
            if ((long)command.ExecuteScalar()! >= MaximumAttempts) throw new ResearchException("diagnostic-capacity");
            if (!worker.Wait(0)) throw new ResearchException("diagnostic-busy");
            try
            {
                attempt = new(Guid.NewGuid().ToString("N"), request.RequestId, request.AnchorRunId, "running", clock.GetUtcNow(), null, null,
                    Environment.GetEnvironmentVariable("TWENTY_COMMIT") ?? "local",
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(SearchDiagnostics).Assembly.Location))), null, null, [], null)
                    { ResearchSha256 = ResearchIdentity.Current.Sha256 };
                Save(attempt, true);
            }
            catch { worker.Release(); throw; }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var registry = research.DiagnosticSnapshot();
            var snapshot = registry.Models;
            attempt = attempt with { RegistrySnapshotAt = registry.At,
                Trials = snapshot.Select(run => new DiagnosticTrial(run.Id, run.DatasetId, run.Status, run.ConfigSha256,
                    run.ReportSha256, run.EngineSha256, false, "not-analysed"))
                    .Concat(registry.Universes.Select(run => UniverseTrial(run, "not-analysed"))).ToImmutableArray() };
            Save(attempt);
            var anchor = snapshot.SingleOrDefault(r => r.Id == request.AnchorRunId);
            var universeAnchor = registry.Universes.SingleOrDefault(r => r.Id == request.AnchorRunId);
            if (anchor is not null && universeAnchor is not null) throw new ResearchException("diagnostic-anchor-ambiguous");
            if (universeAnchor is not null)
            {
                attempt = await AnalyseUniverse(attempt, universeAnchor, snapshot, registry.Universes, deadline.Token);
                attempt = attempt with { Status = "succeeded", FinishedAt = clock.GetUtcNow() }; Save(attempt); return attempt;
            }
            if (anchor is null) throw new ResearchException("run-not-found");
            if (anchor.Status != "succeeded" || anchor.Config.EvaluateHoldout || anchor.EngineSha256 is null || anchor.ReportSha256 is null)
                throw new ResearchException("diagnostic-development-run-required");
            var trials = snapshot.Select(run => new DiagnosticTrial(run.Id, run.DatasetId, run.Status, run.ConfigSha256, run.ReportSha256, run.EngineSha256,
                Reason(run, anchor) == "included", Reason(run, anchor)))
                .Concat(registry.Universes.Select(run => UniverseTrial(run, "different-research-engine"))).ToImmutableArray();
            attempt = attempt with { Trials = trials, DatasetId = anchor.DatasetId, Synthetic = anchor.Synthetic }; Save(attempt);
            if (trials.Count(t => t.Included) > SearchDiagnostics.MaximumCandidates) throw new ResearchException("diagnostic-candidate-count");
            var streams = new List<DiagnosticReturns>();
            // Reports are read one at a time so the private 512 MiB deployment need not retain all artifacts in memory.
            foreach (var run in snapshot.Where(r => trials.Single(t => t.RunId == r.Id).Included))
            {
                deadline.Token.ThrowIfCancellationRequested();
                var report = research.Report(run.Id) ?? throw new ResearchException("diagnostic-report-unavailable");
                if (report.Scope != "development_walk_forward" || report.Config != run.Config || report.Costs != run.Costs || report.DatasetSha256 != run.DatasetId)
                    throw new ResearchException("diagnostic-report-contract");
                var candidate = report.Equity.Where(e => e.Strategy == "candidate").ToArray();
                var comparators = SearchDiagnostics.Comparators.ToImmutableDictionary(s => s, s =>
                {
                    var values = report.Equity.Where(e => e.Strategy == s).ToArray();
                    if (!values.Select(v => v.PeriodEnd).SequenceEqual(candidate.Select(v => v.PeriodEnd))) throw new ResearchException("diagnostic-comparator-grid");
                    return values.Select(v => (double)v.State.PeriodReturn).ToImmutableArray();
                });
                streams.Add(new(run.Id, run.ReportSha256!, candidate.Select(e => e.PeriodEnd).ToImmutableArray(),
                    candidate.Select(e => (double)e.State.PeriodReturn).ToImmutableArray(), comparators));
            }
            var result = await Task.Run(() => SearchDiagnostics.Build(streams, deadline.Token, trials.Length), deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            attempt = attempt with { Status = "succeeded", FinishedAt = clock.GetUtcNow(), Report = result }; Save(attempt); return attempt;
        }
        catch (Exception error)
        {
            // The universe adapter journals its retained snapshot before reading artifacts.
            // Preserve that evidence if a later input or computation fails.
            attempt = Find(attempt.Id) ?? attempt;
            attempt = attempt with { Status = error is OperationCanceledException ? "cancelled" : "failed", FinishedAt = clock.GetUtcNow(),
                ErrorCode = error is ResearchException re ? re.Code : error is OperationCanceledException ? "deadline-or-request-cancelled" : "diagnostic-failed", Report = null };
            Save(attempt); return attempt;
        }
        finally { worker.Release(); }
    }
    private static string Reason(RunEntry run, RunEntry anchor) => run.DatasetId != anchor.DatasetId ? "other-dataset"
        : run.Status != "succeeded" ? "incomplete-or-failed-trial" : run.Config.EvaluateHoldout ? "reserved-evaluation"
        : run.EngineSha256 != anchor.EngineSha256 ? "different-engine-artifact"
        : run.Costs != anchor.Costs || run.Config.MonthlyGbp != anchor.Config.MonthlyGbp || run.Config.AllocationPolicy != anchor.Config.AllocationPolicy ? "different-account-assumptions"
        : run.Config.HoldoutMonths != anchor.Config.HoldoutMonths ? "different-reserved-window" : "included";
}
