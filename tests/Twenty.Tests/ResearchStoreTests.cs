using Twenty.Research;
using Twenty.Core.Research;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Twenty.Tests;

public class ResearchStoreTests
{
    private static (byte[] Csv, byte[] Metadata) Fixture() => (File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"demo.csv")),File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"demo.metadata.json")));
    private sealed class Workspace : IDisposable
    {
        public readonly string Root=Path.Combine(Path.GetTempPath(),"twenty-store-"+Guid.NewGuid().ToString("N"));
        public void Dispose()=>Directory.Delete(Root,true);
    }
    private static DatasetEntry Import(ResearchStore store) { var f=Fixture();return store.Import(f.Csv,f.Metadata,"Synthetic tests"); }
    [Fact]
    public void RequestsAreIdempotentAndConflictingReuseFails()
    {
        using var w=new Workspace();using var store=new ResearchStore(w.Root);var data=Import(store);
        var request=new RunRequest(Guid.NewGuid().ToString("D"),data.Id,new(),new());
        var a=store.Enqueue(request,"test-code");var b=store.Enqueue(request,"test-code");
        Assert.Equal(a.Id,b.Id);Assert.Single(store.ListRuns());
        Assert.Throws<ResearchException>(()=>store.Enqueue(request with {Config=new(TopK:2)},"test-code"));
        Assert.Equal(data.Sha256,store.Panel(data.Id).Sha256);
    }
    [Fact]
    public void LegacyRequestsKeepTheirIdentityWhenOptionalConfigFieldsAreAdded()
    {
        using var w=new Workspace();using var store=new ResearchStore(w.Root);var data=Import(store);
        var request=new RunRequest(Guid.NewGuid().ToString("D"),data.Id,new(),new());var run=store.Enqueue(request,"older-code");
        var record=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(run,ResearchStore.Json))!.AsObject();
        record["config"]!.AsObject().Remove("developmentStart");record["config"]!.AsObject().Remove("developmentEnd");
        foreach(var field in new[]{"studyId","studyCandidateId","studyStage"})record.Remove(field);
        var original=new JsonObject { ["datasetId"]=data.Id,["config"]=record["config"]!.DeepClone(),["costs"]=record["costs"]!.DeepClone() };
        var legacyHash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original.ToJsonString(ResearchStore.Json))));
        record["configSha256"]=legacyHash;
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(w.Root,"research.db"),Pooling=false}.ToString()))
        {
            db.Open();using var update=db.CreateCommand();update.CommandText="UPDATE runs SET json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$json",record.ToJsonString(ResearchStore.Json));update.Parameters.AddWithValue("$id",run.Id);Assert.Equal(1,update.ExecuteNonQuery());
        }
        var replay=store.Enqueue(request,"newer-code");Assert.Equal(run.Id,replay.Id);Assert.Equal(legacyHash,replay.ConfigSha256);Assert.Single(store.ListRuns());
        Assert.Equal("request-id-conflict",Assert.Throws<ResearchException>(()=>store.Enqueue(request with{Config=new(TopK:2)},"newer-code")).Code);
    }
    [Fact]
    public void RestartPreservesQueuedTrialsAndMarksRunningTrialInterrupted()
    {
        using var w=new Workspace();string a,b;
        using(var store=new ResearchStore(w.Root))
        {
            var d=Import(store);a=store.Enqueue(new(Guid.NewGuid().ToString("D"),d.Id,new(),new()),"test").Id;
            b=store.Enqueue(new(Guid.NewGuid().ToString("D"),d.Id,new(TopK:2),new()),"test").Id;
            Assert.Equal(a,store.Claim()!.Id);
        }
        using var resumed=new ResearchStore(w.Root);
        Assert.Equal("interrupted",resumed.Find(a)!.Status);Assert.Equal("process-restarted",resumed.Find(a)!.ErrorCode);
        Assert.Equal("queued",resumed.Find(b)!.Status);Assert.Equal(b,resumed.Claim()!.Id);
    }
    [Fact]
    public void CancellationCannotBeOverwrittenByProgressOrCompletion()
    {
        using var w=new Workspace();using var store=new ResearchStore(w.Root);var d=Import(store);
        var run=store.Enqueue(new(Guid.NewGuid().ToString("D"),d.Id,new(),new()),"test");store.Claim();store.Cancel(run.Id);
        Assert.ThrowsAny<OperationCanceledException>(()=>store.Progress(run.Id,50));
        Assert.ThrowsAny<OperationCanceledException>(()=>store.Complete(run.Id,Backtest.Run(store.Panel(d.Id),new(),new())));
        Assert.True(store.Find(run.Id)!.CancelRequested);
    }
    [Fact]
    public void CorruptInputsAndReportsFailIntegrityChecks()
    {
        using var w=new Workspace();using var store=new ResearchStore(w.Root);var d=Import(store);
        var run=store.Enqueue(new(Guid.NewGuid().ToString("D"),d.Id,new(),new()),"test");store.Claim();store.Complete(run.Id,Backtest.Run(store.Panel(d.Id),new(),new()));
        Assert.NotNull(store.Report(run.Id));
        var path=Path.Combine(w.Root,"runs",run.Id,"report.json");File.AppendAllText(path," ");
        Assert.Equal("report-integrity",Assert.Throws<ResearchException>(()=>store.Report(run.Id)).Code);
        path=Path.Combine(w.Root,"datasets",d.Id,"prices.csv");File.AppendAllText(path,"2000-01-01,BAD,1\n");
        Assert.Throws<ResearchException>(()=>store.Panel(d.Id));
    }
    [Fact]
    public void HoldoutAndConcurrentWriterGatesFailClosed()
    {
        using var w=new Workspace();using var store=new ResearchStore(w.Root);var d=Import(store);
        Assert.Throws<IOException>(()=>new ResearchStore(w.Root));
        Assert.Equal("holdout-gate-closed",Assert.Throws<ResearchException>(()=>store.Enqueue(new(Guid.NewGuid().ToString("D"),d.Id,new(EvaluateHoldout:true),new()),"test")).Code);
        Assert.Empty(store.ListRuns());
    }
    [Fact]
    public void QueueIsBoundedAndCancelledQueuedRunsStayRecorded()
    {
        using var w=new Workspace();using var store=new ResearchStore(w.Root);var d=Import(store);
        for(var i=0;i<10;i++)store.Enqueue(new(Guid.NewGuid().ToString("D"),d.Id,new(),new()),"test");
        Assert.Equal("queue-capacity",Assert.Throws<ResearchException>(()=>store.Enqueue(new(Guid.NewGuid().ToString("D"),d.Id,new(),new()),"test")).Code);
        var first=store.ListRuns().Last();store.Cancel(first.Id);Assert.Equal("cancelled",store.Find(first.Id)!.Status);
        store.Enqueue(new(Guid.NewGuid().ToString("D"),d.Id,new(),new()),"test");Assert.Equal(11,store.ListRuns().Count);
    }
}
