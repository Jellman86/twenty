using System.Reflection;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Twenty.Core.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Core.Research;
using Twenty.Research;

namespace Twenty.Tests;

public sealed partial class RawStudyWorkerTests
{
    private void PublicationFault(bool enabled)
    {
        using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")); db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = enabled ? "CREATE TRIGGER authored_publication_failure BEFORE UPDATE OF status ON raw_trials WHEN OLD.status<>'succeeded' AND NEW.status='succeeded' BEGIN SELECT RAISE(ABORT,'authored publication failure'); END;"
            : "DROP TRIGGER authored_publication_failure";
        cmd.ExecuteNonQuery();
    }
    private RawResearchStudy RecoverPublication(RawResearchStudy study, StudyEvaluationRequest request) =>
        store.RecoverRawFinalPublication(study.Id,request);

    [Fact]
    public void RawFinalPublicationCapacityRefusesBeforeSpendingOrWritingAFinalPair()
    {
        var study = FreezeRawCandidate(); var selection = store.RawStudySelectionBytes(study.Id);
        var reports = store.ListRawTrials().ToDictionary(t=>t.Id,t=>store.RawTrialReportBytes(t.Id));
        var blocker = Path.Combine(root,"authored-capacity-blocker");
        using(var file=File.Create(blocker))
        {
            var used=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Sum(p=>new FileInfo(p).Length);
            file.SetLength(256L*1024*1024-used-1024*1024);
        }
        try
        {
            Assert.Equal("raw-study-publication-capacity",Assert.Throws<ResearchException>(()=>store.EvaluateRawStudy(study.Id,
                new(Guid.NewGuid().ToString("D"),study.PlanSha256),"authored")).Code);
            Assert.Equal(0,RawFinalClaims());Assert.Equal(8,store.ListRawTrials().Count);
            Assert.Equal(8,Directory.EnumerateFiles(Path.Combine(root,"runs"),"raw-study-request.json",SearchOption.AllDirectories).Count());
            Assert.Equal("candidate-frozen",store.RawStudies().Single().Status);
            Assert.Equal(selection,store.RawStudySelectionBytes(study.Id));
            Assert.All(reports,pair=>Assert.Equal(pair.Value,store.RawTrialReportBytes(pair.Key)));
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public void RawFinalPublicationFaultRetainsExactVerifiedBytesAndTheOriginalPartner()
    {
        var study=FreezeRawCandidate();var request=new StudyEvaluationRequest(Guid.NewGuid().ToString("D"),study.PlanSha256);
        var selection=store.RawStudySelectionBytes(study.Id);var reports=store.ListRawTrials().ToDictionary(t=>t.Id,t=>store.RawTrialReportBytes(t.Id));
        study=store.EvaluateRawStudy(study.Id,request,"authored");PublicationFault(true);
        try { Assert.True(store.ProcessNextRawStudyRun("authored")); }
        finally { PublicationFault(false); }
        var pending=Assert.Single(store.ListRawTrials(),t=>t.Status=="publishing");
        Assert.Null(pending.ReportSha256);Assert.Equal(1,RawFinalClaims());
        Assert.Single(store.ListRawTrials(),t=>t.Status=="planned");Assert.False(store.ProcessNextRawStudyRun("authored"));
        var path=Path.Combine(root,"runs",pending.Id,"raw-study-report.json");var original=File.ReadAllBytes(path);File.Delete(path);
        var recovered=RecoverPublication(study,request);Assert.Equal("final-evaluation",recovered.Status);
        Assert.Equal(original,store.RawTrialReportBytes(pending.Id));Assert.Equal(original,File.ReadAllBytes(path));
        Assert.True(store.ProcessNextRawStudyRun("authored"));Assert.False(store.ProcessNextRawStudyRun("authored"));
        Assert.Equal("engineering-complete",store.RawStudies().Single().Status);Assert.Equal(10,store.ListRawTrials().Count);
        Assert.Equal(1,RawFinalClaims());Assert.Equal(selection,store.RawStudySelectionBytes(study.Id));
        Assert.All(reports,pair=>Assert.Equal(pair.Value,store.RawTrialReportBytes(pair.Key)));
        Assert.Equal("engineering-complete",RecoverPublication(study,request).Status);
        Assert.Equal("study-holdout-spent",Assert.Throws<ResearchException>(()=>store.EvaluateRawStudy(study.Id,request with{RequestId=Guid.NewGuid().ToString("D")},"authored")).Code);
    }
    private (RawResearchStudy Study, StudyEvaluationRequest Request, RawStudyTrialEntry Row, byte[] Stage) PendingPublication()
    {
        var study=FreezeRawCandidate();var request=new StudyEvaluationRequest(Guid.NewGuid().ToString("D"),study.PlanSha256);
        study=store.EvaluateRawStudy(study.Id,request,"authored");PublicationFault(true);
        try { Assert.True(store.ProcessNextRawStudyRun("authored")); }
        finally { PublicationFault(false); }
        var row=Assert.Single(store.ListRawTrials(),r=>r.Status=="publishing");
        return (study,request,row,File.ReadAllBytes(Path.Combine(root,"runs",row.Id,"raw-study-publication.stage")));
    }
    private long HeldPublicationBytes()
    {
        using var db=new SqliteConnection("Data Source="+Path.Combine(root,"research.db"));db.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT sum(bytes) FROM raw_final_headroom";return (long)cmd.ExecuteScalar()!;
    }

    [Theory]
    [InlineData("missing","raw-study-publication-stage-unavailable")]
    [InlineData("corrupt","raw-study-publication-stage-integrity")]
    [InlineData("binding","raw-study-publication-stage-integrity")]
    [InlineData("summary","raw-study-report-binding")]
    [InlineData("report-binding","raw-study-publication-stage-integrity")]
    [InlineData("receipt-binding","raw-study-publication-stage-integrity")]
    [InlineData("conflict","raw-study-publication-report-conflict")]
    public void RawFinalPublicationRejectsBadStagingWithoutSubstitutionAndExactRestorationRecovers(string change,string code)
    {
        var f=PendingPublication();var path=Path.Combine(root,"runs",f.Row.Id,"raw-study-publication.stage");
        var reportPath=Path.Combine(root,"runs",f.Row.Id,"raw-study-report.json");var originalReport=File.ReadAllBytes(reportPath);
        var original=f.Study.Originals.Single(o=>o.Kind=="dataset");var dataPath=Path.Combine(root,"universe-datasets",original.OriginalSha256+".json");
        var dataset=File.ReadAllBytes(dataPath);
        if(change=="missing")File.Delete(path);
        else if(change=="corrupt") { var bad=f.Stage.ToArray();bad[^1]^=1;File.WriteAllBytes(path,bad);File.Delete(dataPath); }
        else if(change=="conflict")File.WriteAllText(reportPath,"authored conflicting result");
        else
        {
            var length=BinaryPrimitives.ReadInt32LittleEndian(f.Stage);
            var header=JsonNode.Parse(f.Stage.AsSpan(4,length))!.AsObject();var body=f.Stage.AsSpan(4+length).ToArray();
            if(change=="binding")header["registryTrialId"]=f.Study.FinalStressTrialId;
            else
            {
                var report=JsonSerializer.Deserialize<RawStudyDevelopmentReport>(body,RawReplayJson.Options)!;var account=report.Strategies[0];
                if(change=="report-binding")report=report with{TrialId="final:momentum:1:stress"};
                else if(change=="receipt-binding")
                {
                    var request=JsonSerializer.Deserialize<RawReplayDocument>(Convert.FromBase64String(account.Receipt.RequestBase64),RawReplayJson.Options)!;
                    var requestBytes=JsonSerializer.SerializeToUtf8Bytes(request with{Study=request.Study! with{TrialId="final:momentum:1:stress"}},RawReplayJson.Options);
                    account=account with{Receipt=account.Receipt with{RequestBase64=Convert.ToBase64String(requestBytes),RequestSha256=Convert.ToHexStringLower(SHA256.HashData(requestBytes))}};
                    report=report with{Strategies=report.Strategies.SetItem(0,account)};
                }
                else report=report with {Strategies=report.Strategies.SetItem(0,account with {EndingEquityGbp=account.EndingEquityGbp+1})};
                body=RawStudyDevelopmentRun.EncodeReport(report);
                header["reportSha256"]=Convert.ToHexStringLower(SHA256.HashData(body));header["reportBytes"]=body.Length;
            }
            var encoded=JsonSerializer.SerializeToUtf8Bytes(header,RawReplayJson.Options);var stage=new byte[4+encoded.Length+body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(stage,encoded.Length);encoded.CopyTo(stage,4);body.CopyTo(stage,4+encoded.Length);File.WriteAllBytes(path,stage);
            var marker=f.Row.Publication! with {StageSha256=Convert.ToHexStringLower(SHA256.HashData(stage)),StageBytes=stage.Length,
                ReportSha256=Convert.ToHexStringLower(SHA256.HashData(body)),ReportBytes=body.Length};
            WriteAuthoredFinalIntegrityFixture(f.Study,f.Row with {Publication=marker});
        }
        if(change is "report-binding" or "receipt-binding")File.Delete(dataPath);
        var markerBefore=store.ListRawTrials().Single(r=>r.Id==f.Row.Id).Publication;
        Assert.Equal(code,Assert.Throws<ResearchException>(()=>RecoverPublication(f.Study,f.Request)).Code);
        var pending=store.ListRawTrials().Single(r=>r.Id==f.Row.Id);Assert.Equal("publishing",pending.Status);Assert.Equal(markerBefore,pending.Publication);
        Assert.Equal(code,pending.ErrorCode);Assert.Null(pending.ReportSha256);Assert.Equal(1,RawFinalClaims());Assert.False(store.ProcessNextRawStudyRun("authored"));
        var journal=JsonSerializer.Serialize(store.RawStudyEvents(f.Study.Id));
        Assert.Equal(code,Assert.Throws<ResearchException>(()=>RecoverPublication(f.Study,f.Request)).Code);
        Assert.Equal(journal,JsonSerializer.Serialize(store.RawStudyEvents(f.Study.Id)));
        if(change=="conflict")Assert.Equal("authored conflicting result",File.ReadAllText(reportPath));
        File.WriteAllBytes(path,f.Stage);File.WriteAllBytes(reportPath,originalReport);File.WriteAllBytes(dataPath,dataset);
        WriteAuthoredFinalIntegrityFixture(f.Study,f.Row);
        RecoverPublication(f.Study,f.Request);Assert.Equal(originalReport,store.RawTrialReportBytes(f.Row.Id));Assert.Equal(10,store.ListRawTrials().Count);
    }

    [Fact]
    public void RawFinalPublicationRestartQueuesVerificationAndDoesNotRunTheCandidateAgain()
    {
        var f=PendingPublication();var missing=f.Study.Originals.Where(o=>o.Kind!="dataset").ToDictionary(o=>
            Path.Combine(root,"raw-study-inputs",o.Kind,o.OriginalSha256+".json"),o=>store.RawStudyOriginalBytes(f.Study.Id,o.OriginalSha256));
        foreach(var file in missing.Keys)File.Delete(file);
        var path=Path.Combine(root,"runs",f.Row.Id,"raw-study-report.json");var report=File.ReadAllBytes(path);File.Delete(path);
        store.Dispose();store=new(root);
        Assert.Equal("publishing",store.ListRawTrials().Single(r=>r.Id==f.Row.Id).Status); // No constructor-time receipt replay.
        Assert.True(store.ProcessNextRawStudyRun("new-publication-label"));
        Assert.Equal(report,store.RawTrialReportBytes(f.Row.Id));Assert.Equal("authored",store.ListRawTrials().Single(r=>r.Id==f.Row.Id).WorkerCommit);
        Assert.Single(store.ListRawTrials(),r=>r.Status=="planned");
        foreach(var file in missing)File.WriteAllBytes(file.Key,file.Value);
        Assert.True(store.ProcessNextRawStudyRun("authored"));Assert.Equal("engineering-complete",store.RawStudies().Single().Status);
        Assert.Equal(0,HeldPublicationBytes());Assert.Equal(1,RawFinalClaims());Assert.Equal(10,store.ListRawTrials().Count);
    }

    [Fact]
    public void RawFinalPublicationRespectsTheWorkerAndCancellationNeverReopensIt()
    {
        var f=PendingPublication();var held=HeldPublicationBytes();Assert.True(held>0);
        var begin=typeof(ResearchStore).GetMethod("TryBeginComputation",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var end=typeof(ResearchStore).GetMethod("EndComputation",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Assert.Equal(true,begin.Invoke(store,null));
        try{Assert.Equal("research-worker-busy",Assert.Throws<ResearchException>(()=>RecoverPublication(f.Study,f.Request)).Code);}
        finally{end.Invoke(store,null);}
        Assert.Equal(held,HeldPublicationBytes());
        store.CancelRawStudy(f.Study.Id);Assert.Equal("cancelled",RecoverPublication(f.Study,f.Request).Status);
        Assert.Equal(0,HeldPublicationBytes());Assert.False(store.ProcessNextRawStudyRun("authored"));
        Assert.Equal(f.Stage,File.ReadAllBytes(Path.Combine(root,"runs",f.Row.Id,"raw-study-publication.stage")));
        Assert.Equal(1,RawFinalClaims());Assert.Equal("cancelled",store.ListRawTrials().Single(r=>r.Id==f.Row.Id).Status);
    }

    [Fact]
    public void RawFinalPublicationPendingFamilyDoesNotBlockAnotherRegisteredFamily()
    {
        var f=PendingPublication();var other=Register();store.StartRawStudy(other.Id,new(Guid.NewGuid().ToString("D"),other.PlanSha256));
        Assert.True(store.ProcessNextRawStudyRun("authored"));
        Assert.Single(store.ListRawTrials(),r=>r.StudyId==other.Id&&r.Status=="succeeded");
        Assert.Single(store.ListRawTrials(),r=>r.StudyId==f.Study.Id&&r.Status=="planned");
        Assert.Equal("publishing",store.ListRawTrials().Single(r=>r.Id==f.Row.Id).Status);
    }

    [Fact]
    public void RawFinalPublicationReservationsGuardDatasetWritesAndCannotDisappear()
    {
        var study=FreezeRawCandidate();store.EvaluateRawStudy(study.Id,new(Guid.NewGuid().ToString("D"),study.PlanSha256),"authored");
        var held=HeldPublicationBytes();Assert.True(held>2*RawStudyDevelopmentRun.MaximumReportBytes);
        var f=RawStudyDevelopmentRunTests.Fixture();var file=DatasetJson.Read(f.Dataset);
        var data=JsonSerializer.SerializeToUtf8Bytes(file with{Manifest=file.Manifest with{DatasetId="authored-reservation-probe"}},DatasetJson.Options);
        var blocker=Path.Combine(root,"authored-reservation-blocker");
        using(var stream=File.Create(blocker))
        {
            var used=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Sum(p=>new FileInfo(p).Length);
            stream.SetLength(256L*1024*1024-used-held-data.Length+1);
        }
        try
        {
            Assert.Equal("artifact-capacity",Assert.Throws<ResearchException>(()=>store.ImportUniverseDataset(data)).Code);
            Assert.Equal(study.Plan.DatasetSha256,store.ImportUniverseDataset(f.Dataset)); // Existing immutable input consumes no new headroom.
        }
        finally {File.Delete(blocker);}
        using(var db=new SqliteConnection("Data Source="+Path.Combine(root,"research.db")))
        {
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM raw_final_headroom WHERE trial_id=$id";
            cmd.Parameters.AddWithValue("$id",store.ListRawTrials().Single(r=>r.Id==store.RawStudies().Single().FinalBaseTrialId).Id);cmd.ExecuteNonQuery();
        }
        Assert.Equal("raw-study-publication-reservation-integrity",Assert.Throws<ResearchException>(()=>store.ListRawTrials()).Code);
    }

    [Fact]
    public void RawFinalPublicationPendingFamilyDoesNotStarveAUniverseStudy()
    {
        var f=PendingPublication();var sha=store.ImportUniverseDataset(UniverseDiagnosticTests.DatasetBytes());
        var other=store.RegisterUniverseStudy(new(Guid.NewGuid().ToString("D"),"Authored independent universe family","engineering",sha,
            "research-universe",f.Study.Plan.Decisions,45,20,new()),"authored");
        Assert.True(store.ProcessNextUniverseStudyRun("authored"));
        Assert.Single(store.ListUniverseRuns(),r=>r.StudyId==other.Id&&r.Status=="succeeded");
        Assert.Single(store.ListRawTrials(),r=>r.StudyId==f.Study.Id&&r.Status=="planned");
        Assert.Equal("publishing",store.ListRawTrials().Single(r=>r.Id==f.Row.Id).Status);
    }

    [Fact]
    public void RawFinalPublicationClockRegressionLeavesItsOriginalMarkerRecoverable()
    {
        var f=PendingPublication();store.Dispose();store=new(root,new RawFinalClock(f.Row.Publication!.VerifiedAt.AddMinutes(-1)));
        Assert.Equal("raw-study-clock-regression",Assert.Throws<ResearchException>(()=>RecoverPublication(f.Study,f.Request)).Code);
        Assert.Equal(f.Row.Publication,store.ListRawTrials().Single(r=>r.Id==f.Row.Id).Publication);
        Assert.Equal("publishing",store.ListRawTrials().Single(r=>r.Id==f.Row.Id).Status);Assert.True(HeldPublicationBytes()>0);
        store.Dispose();store=new(root);RecoverPublication(f.Study,f.Request with{RequestId=f.Request.RequestId.ToUpperInvariant()});
        Assert.Equal("succeeded",store.ListRawTrials().Single(r=>r.Id==f.Row.Id).Status);Assert.Equal(1,RawFinalClaims());
    }

    [Fact]
    public void RawFinalPublicationNeverAdoptsALegacyOrphanWithoutItsOptInMarker()
    {
        var f=PendingPublication();var legacy=f.Study with{FinalPublicationVersion=null};
        var row=f.Row with{Status="started",Publication=null,ErrorCode=null};
        using(var db=new SqliteConnection("Data Source="+Path.Combine(root,"research.db")))
        {
            db.Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            var bytes=JsonSerializer.SerializeToUtf8Bytes(legacy,RawReplayJson.Options);
            cmd.CommandText="UPDATE raw_studies SET json=$json,sha256=$sha WHERE id=$id";
            cmd.Parameters.AddWithValue("$json",System.Text.Encoding.UTF8.GetString(bytes));cmd.Parameters.AddWithValue("$sha",Convert.ToHexStringLower(SHA256.HashData(bytes)));cmd.Parameters.AddWithValue("$id",legacy.Id);cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();cmd.CommandText="UPDATE raw_trials SET json=$json,status='started' WHERE id=$id";
            cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(row,RawReplayJson.Options));cmd.Parameters.AddWithValue("$id",row.Id);cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();cmd.CommandText="DELETE FROM raw_final_headroom";cmd.ExecuteNonQuery();tx.Commit();
        }
        var path=Path.Combine(root,"runs",row.Id,"raw-study-report.json");var report=File.ReadAllBytes(path);
        store.Dispose();store=new(root);
        Assert.Equal("final-failed",store.RawStudies().Single().Status);Assert.False(store.ProcessNextRawStudyRun("authored"));
        Assert.Equal("final-failed",RecoverPublication(legacy,f.Request).Status);Assert.Equal(report,File.ReadAllBytes(path));
        Assert.Equal("raw-study-report-unavailable",Assert.Throws<ResearchException>(()=>store.RawTrialReportBytes(row.Id)).Code);
        Assert.Equal(1,RawFinalClaims());Assert.Equal(10,store.ListRawTrials().Count);
    }

}
