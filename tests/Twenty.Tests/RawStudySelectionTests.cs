using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class RawStudySelectionTests
{
    private static RawStudySelectionObservation[] Inputs(RawUniverseStudyPlan plan) => plan.DevelopmentTrials.Select((t,i)=>
        new RawStudySelectionObservation(t.Id,(i+1).ToString("x64"),700,700,700)).ToArray();
    [Fact]
    public void WorstStressMarginWinsOverHighestBaseUpsideAndAllCandidatesRemainDisclosed()
    {
        var plan=RawStudyDevelopmentRunTests.Fixture().Plan;var inputs=Inputs(plan);
        inputs=inputs.Select(r=>r.TrialId.StartsWith("momentum:1:",StringComparison.Ordinal)?r with { MomentumGbp=705 }:
            r.TrialId=="momentum:2:base"?r with { MomentumGbp=800 }:r.TrialId=="momentum:2:stress"?r with { MomentumGbp=701 }:r).ToArray();
        var result=RawStudySelection.Evaluate(plan,inputs.Reverse());
        Assert.Equal("momentum:1",result.SelectedCandidateId);Assert.Equal(4,result.Scores.Length);
        Assert.Equal(5,result.Scores.Single(s=>s.CandidateId=="momentum:1").WorstMarginGbp);
        Assert.Equal(1,result.Scores.Single(s=>s.CandidateId=="momentum:2").WorstMarginGbp);
    }
    [Fact]
    public void OrdinalSyntheticTieIsLifecycleOnlyAndGenuineFlatResultsProduceNoCandidate()
    {
        var p=RawStudyDevelopmentRunTests.Fixture().Plan;
        Assert.Equal("momentum:1",RawStudySelection.Evaluate(p,Inputs(p)).SelectedCandidateId);
        var real=RawUniverseStudyProtocol.Declare(p.DatasetSha256,p.UniverseId,false,"research",p.CodeSha256,p.IdentityScheme,p.Decisions,p.MonthlyGbp,
            p.BaseExecution,p.Calendars,p.ActionCatalogSha256);
        Assert.Null(RawStudySelection.Evaluate(real,Inputs(real)).SelectedCandidateId);
    }
    [Fact]
    public void MissingStressOrReusedArtifactCannotSelectOnlySuccessfulCandidates()
    {
        var p=RawStudyDevelopmentRunTests.Fixture().Plan;var rows=Inputs(p);
        Assert.Equal("raw-study-selection-family",Assert.Throws<ResearchException>(()=>RawStudySelection.Evaluate(p,rows.Skip(1))).Code);
        rows[1]=rows[1] with { ReportSha256=rows[0].ReportSha256 };
        Assert.Equal("raw-study-selection-family",Assert.Throws<ResearchException>(()=>RawStudySelection.Evaluate(p,rows)).Code);
    }
}
