using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Research;
using Twenty.Core.Research;

namespace Twenty.Api.Execution;

public sealed record SignalRequest(string RunId, string DatasetId);
public sealed class SignalStore
{
    private readonly ResearchStore research;
    private readonly string connectionString;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    public SignalStore(ResearchStore research, TimeProvider? clock = null)
    {
        this.research = research; this.clock = clock ?? TimeProvider.System;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(research.Root, "research.db"), Pooling = false }.ToString();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS forward_signals(id TEXT PRIMARY KEY,json TEXT NOT NULL)"; command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public ForwardSignal Create(SignalRequest request)
    {
        var run = research.Find(request.RunId) ?? throw new ResearchException("run-not-found");
        var report = research.Report(run.Id) ?? throw new ResearchException("signal-completed-run-required");
        var observed = research.Datasets().SingleOrDefault(d => d.Id == request.DatasetId)?.RegisteredAt ?? throw new ResearchException("signal-availability-unknown");
        var signal = ForwardSignalBuilder.Build(run.Id, run.ReportSha256!, report, research.Panel(run.DatasetId), research.Panel(request.DatasetId), clock.GetUtcNow(), observed);
        lock (gate)
        {
            var prior = Find(signal.Id); if (prior is not null) return prior;
            if (List().Count >= 1000) throw new ResearchException("signal-capacity");
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO forward_signals(id,json) VALUES($id,$json)";
            command.Parameters.AddWithValue("$id", signal.Id); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(signal, ResearchStore.Json));
            command.ExecuteNonQuery(); return signal;
        }
    }
    public List<ForwardSignal> List()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM forward_signals ORDER BY rowid DESC LIMIT 1000";
        using var reader = command.ExecuteReader(); var result = new List<ForwardSignal>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<ForwardSignal>(reader.GetString(0), ResearchStore.Json)!);
        return result;
    }
    public ForwardSignal? Find(string id) => List().SingleOrDefault(s => s.Id == id);
}
