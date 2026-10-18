using System.Text.Json;
using Twenty.Api.Execution;
using Twenty.Research;
using Twenty.Core.Execution;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class ContributionPolicyTests
{
    private static readonly Dictionary<string, decimal> FlatReturns = new() { ["A"]=0,["B"]=0,["C"]=0,["D"]=0 };
    [Fact] public void RotatingTargetsRetainExistingHoldingsAndLeaveUnusedCash()
    {
        var portfolio=new NotionalPortfolio();var costs=new ResearchCosts();
        portfolio.Step(20,new Dictionary<string,decimal>{{"A",.5m},{"B",.5m}},FlatReturns,costs,true,2,.5m);
        var old=portfolio.Holdings.ToDictionary();
        var next=portfolio.Step(20,new Dictionary<string,decimal>{{"C",.5m},{"D",.5m}},FlatReturns,costs,true,2,.5m);
        Assert.Empty(next.Trades);Assert.Equal(2,portfolio.Holdings.Count);Assert.Equal(old,portfolio.Holdings.ToDictionary());Assert.True(portfolio.Cash>20);
        Assert.Equal(40,portfolio.Contributions);
    }
    [Fact] public void InitialNotionalPurchasesAgreeWithTheQuantityPlannerBeforeRounding()
    {
        var portfolio=new NotionalPortfolio();var weights=new Dictionary<string,decimal>{{"A",.5m},{"B",.5m}};
        var now=DateTimeOffset.UtcNow;
        var rules=weights.Keys.ToDictionary(s=>s,s=>new InstrumentRule(s,s+"_EQ","GBP",2,2,.00000001m,1,25,now,true));
        var plan=LimitPlanner.Plan(20,new Dictionary<string,decimal>(),new Dictionary<string,decimal>(),weights,rules,.5m,now);
        var result=portfolio.Step(20,weights,FlatReturns,new(),true,2,.5m);
        Assert.InRange(Math.Abs(plan.CashRemainingGbp-result.CashGbp),0,.00000005m);
        Assert.All(result.Trades,t=>Assert.Equal("buy",t.Side));Assert.Equal(2,result.Trades.Length);
    }
    [Fact] public void ScarceCashUsesTheSameDeterministicPurchasePriorityAsTheBrokerPlanner()
    {
        var portfolio=new NotionalPortfolio();var flat=new Dictionary<string,decimal>{{"A",0},{"B",0},{"C",0},{"D",0},{"E",0},{"F",0}};
        portfolio.Step(60,new Dictionary<string,decimal>{{"A",.5m},{"B",.5m}},flat,new(FxBps:0,HalfSpreadBps:0,SlippageBps:0));
        var targets=new Dictionary<string,decimal>{{"D",.25m},{"C",.25m},{"E",.25m},{"F",.25m}};
        var now=DateTimeOffset.UtcNow;var rules=flat.Keys.ToDictionary(s=>s,s=>new InstrumentRule(s,s+"_EQ","GBP",1,1,.00000001m,1,25,now,true));
        var plan=LimitPlanner.Plan(40,portfolio.Holdings,new Dictionary<string,decimal>(),new Dictionary<string,decimal>{{"D",.25m},{"C",.25m}},rules,.5m,now);
        portfolio.Step(40,targets,flat,new(),true,4,.5m);
        foreach(var order in plan.Orders)Assert.InRange(Math.Abs(portfolio.Holdings[order.Symbol]-order.Quantity),0,.00000005m);
        Assert.Equal(30,portfolio.Holdings["A"]);Assert.Equal(30,portfolio.Holdings["B"]);
        Assert.False(portfolio.Holdings.ContainsKey("E"));Assert.False(portfolio.Holdings.ContainsKey("F"));
    }
    [Fact] public void AppreciatedOverweightPositionBlocksItsOwnBuysButContributionsStillBuyTheUnderweight()
    {
        // Purchase-cap semantics: drift above the cap is never repaired by selling and never added to,
        // but new contributions must still buy underweight names (cash-flow rebalancing) instead of idling.
        var portfolio=new NotionalPortfolio();var weights=new Dictionary<string,decimal>{{"A",.5m},{"B",.5m}};
        portfolio.Step(20,weights,new Dictionary<string,decimal>{{"A",4},{"B",0}},new(),true,2,.5m);
        var before=portfolio.Holdings.ToDictionary();
        var next=portfolio.Step(20,weights,FlatReturns,new(),true,2,.5m);
        var trade=Assert.Single(next.Trades);Assert.Equal("buy",trade.Side);Assert.Equal("B",trade.Symbol);
        Assert.Equal(before["A"],portfolio.Holdings["A"]);Assert.True(portfolio.Holdings["B"]>before["B"]);
        Assert.True(next.CashGbp<1);
    }
    [Fact] public void SubMinimumScaledBuyCashIsReallocatedToRemainingBuysInFullRebalance()
    {
        // With a 40% buy cost, £2.60 across two £1.30 targets scales each buy to ~£0.93, below the
        // £1 minimum. The reserved cash must fund the first unmet target in full instead of idling.
        var portfolio=new NotionalPortfolio();var costs=new ResearchCosts(FxBps:4000,HalfSpreadBps:0,SlippageBps:0,MinimumOrderGbp:1);
        var step=portfolio.Step(2.60m,new Dictionary<string,decimal>{{"A",.5m},{"B",.5m}},FlatReturns,costs);
        var trade=Assert.Single(step.Trades);Assert.Equal("A",trade.Symbol);Assert.Equal(1.30m,trade.NotionalGbp);
        Assert.Equal(2.60m,step.CashGbp+step.TradedNotionalGbp+step.PeriodCostGbp);
    }
    [Fact] public void ContributionOnlyBacktestsDoNotSellOrAccumulateUnboundedNames()
    {
        var panel=MonthlyPanel.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"stress.csv")),File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"stress.metadata.json")));
        var report=Backtest.Run(panel,new(TopK:2,Hypothesis:"momentum",AllocationPolicy:"contribution-only"),new());
        Assert.All(report.Equity.SelectMany(e=>e.State.Trades),t=>Assert.Equal("buy",t.Side));
        Assert.InRange(report.Equity.Where(e=>e.Strategy=="candidate").SelectMany(e=>e.State.Trades).Select(t=>t.Symbol).Distinct().Count(),1,2);
        Assert.Single(report.Summaries.Select(s=>s.ContributionsGbp).Distinct());
        Assert.Equal("full-rebalance",JsonSerializer.Deserialize<ExperimentConfig>("{}",ResearchStore.Json)!.AllocationPolicy);
    }
    [Fact] public async Task FullRebalanceEvidenceCannotApproveTheBuyOnlyCoordinator()
    {
        using var f=new ForwardExecutionTests.Fixture(allocationPolicy:"full-rebalance");
        var result=await f.Coordinator.Run(default);Assert.Equal("execution-backtest-policy-mismatch",result.Code);Assert.Equal(0,f.Gateway.Posts);Assert.Equal(0,f.Gateway.Reads);
    }
}
