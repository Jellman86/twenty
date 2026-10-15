using System.Collections.Immutable;
using System.Text;
using Twenty.Api.Execution;
using Twenty.Api.Research;
using Twenty.Core.Execution;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class ForwardExecutionTests
{
    [Fact] public void FrozenForwardSignalUsesLatestFeaturesWithoutFutureLabelsOrRetraining()
    {
        using var f = new Fixture();
        var signal = f.Signals.Create(new(f.Run.Id, f.Dataset.Id));
        var expected = RidgeModel.Restore(f.Report.Folds[^1].Model).Predict(f.Panel.Features(f.Panel.Dates.Length));
        Assert.Equal(expected, signal.Predictions.Select(p => p.Score));
        Assert.All(signal.Predictions, p => Assert.True(p.FeatureCutoff < p.DecisionDate));
        Assert.Equal(new DateOnly(2015,1,1), signal.DecisionDate); Assert.True(signal.Synthetic);
        Assert.Equal(signal.Id, f.Signals.Create(new(f.Run.Id, f.Dataset.Id)).Id);
        f.Clock.Now = f.Clock.Now.AddMonths(1);
        Assert.Equal("signal-current-panel-required", Assert.Throws<ResearchException>(()=>f.Signals.Create(new(f.Run.Id, f.Dataset.Id))).Code);
    }
    [Theory] [InlineData("momentum")] [InlineData("breakout")] [InlineData("upside-tail")]
    public void ForwardScoringRetainsTheSelectedHypothesis(string hypothesis)
    {
        using var f = new Fixture(hypothesis, stress:true);
        var signal = f.Signals.Create(new(f.Run.Id,f.Dataset.Id));
        if (hypothesis=="momentum") Assert.Equal(f.Panel.Features(f.Panel.Dates.Length).Select(x=>x[2]),signal.Predictions.Select(p=>p.Score));
        if (hypothesis=="breakout")
        { Assert.Equal(f.Panel.BreakoutScores(f.Panel.Dates.Length),signal.Predictions.Select(p=>p.Score)); Assert.All(signal.Predictions.Where(p=>p.Score<=0),p=>Assert.Equal(0,p.TargetWeight)); }
        Assert.Equal(hypothesis,signal.Hypothesis);
    }
    [Fact] public void InvalidFrozenModelAndFutureSignalMonthReject()
    {
        using var f = new Fixture();
        Assert.Throws<ResearchException>(()=>RidgeModel.Restore(f.Report.Folds[^1].Model with { Scale=[0,1,1,1,1] }));
        Assert.Throws<ResearchException>(()=>ForwardSignalBuilder.Build(f.Run.Id,f.Run.ReportSha256!,f.Report,f.Panel,f.Panel,f.Clock.Now.AddMonths(-1)));
        Assert.Throws<ResearchException>(()=>ForwardSignalBuilder.Build(f.Run.Id,f.Run.ReportSha256!,f.Report with { FeatureVersion="unknown" },f.Panel,f.Panel,f.Clock.Now));
    }
    [Theory] [InlineData("negative-debit",-10,true,10)] [InlineData("positive-debit",10,true,10)]
    [InlineData("negative-debit",-10,false,10.5)] [InlineData("positive-debit",10,false,10.5)]
    public void WalletInterpretationIsExplicitAndDoesNotDoubleCountTaxes(string convention,decimal net,bool included,decimal expected)
    {
        var fill=new BrokerFill(1,DateTimeOffset.UtcNow,1,10,"TRADE",new("GBP",net,1,[new("GBP",.5m,"STAMP_DUTY")]));
        Assert.Equal(expected,BrokerFillAccounting.Debit(fill,new(convention,included,"positive-charge")));
        Assert.Throws<ResearchException>(()=>BrokerFillAccounting.Debit(fill with { WalletImpact=fill.WalletImpact with { Currency="USD" } },new(convention,included,"positive-charge")));
        Assert.Throws<ResearchException>(()=>BrokerFillAccounting.Debit(fill with { Type="STOCK_SPLIT" },new(convention,included,"positive-charge")));
    }
    [Fact] public async Task CoordinatorChoosesAndBuysOnceThenBooksUniqueFillsAtomically()
    {
        using var f=new Fixture();
        f.Gateway.OnSubmit = () => Assert.Contains(f.Ledger.Find(f.Policy.AccountKey)!.Intents,i=>i.Phase=="submitting");
        var first=await f.Coordinator.Run(default); Assert.Equal("processed",first.Status); Assert.Equal(3,first.Submitted);
        Assert.Equal(3,f.Gateway.Posts); Assert.Equal(20,f.Account.ContributionsGbp); Assert.Equal(2,f.Account.Intents.Count(i=>i.Phase=="filled"));Assert.Equal(1,f.Account.Intents.Count(i=>i.Phase=="pending"));
        var second=await f.Coordinator.Run(default); Assert.Equal("processed",second.Status); Assert.Equal(0,second.Submitted);
        Assert.All(f.Account.Intents,i=>Assert.Equal("filled",i.Phase)); Assert.Equal(3,f.Account.Fills.Count);
        Assert.Equal(20,f.Account.CashGbp+f.Account.Intents.Sum(i=>i.Progress.DebitGbp));
        var cash=f.Account.CashGbp; await f.Coordinator.Run(default); Assert.Equal(cash,f.Account.CashGbp); Assert.Equal(3,f.Gateway.Posts);
        Assert.True(Assert.Single(f.Ledger.DemoReceipts()).Synthetic);
    }
    [Fact] public async Task TimeoutAfterRemotePurchaseIsUnknownAndNeverReplayed()
    {
        using var f=new Fixture(); f.Gateway.UnknownAfterAccept=true;
        var result=await f.Coordinator.Run(default); Assert.Equal(1,result.Submitted);
        var unknown=Assert.Single(f.Account.Intents.Where(i=>i.Phase=="unknown"));
        Assert.Null(unknown.BrokerOrderId); Assert.Equal(20,f.Account.CashGbp);
        var retry=await f.Coordinator.Run(default); Assert.Equal("broker-submission-unknown",retry.Code); Assert.Equal(1,f.Gateway.Posts);
        f.Gateway.UnknownAfterAccept=false;
        await f.Coordinator.BindUnknown(f.Policy.AccountKey,unknown.Id,f.Gateway.Rows.Single().Order.Id,default);
        await f.Coordinator.Run(default); await f.Coordinator.Run(default);
        Assert.Equal(3,f.Gateway.Posts); Assert.All(f.Account.Intents,i=>Assert.Equal("filled",i.Phase));
    }
    [Fact] public async Task CrashBetweenDurableMarkerAndNetworkProducesUnknownOnRestart()
    {
        using var f=new Fixture(); var signal=f.Signals.Create(new(f.Run.Id,f.Dataset.Id));
        f.Ledger.BindAccount(f.Policy,[]); var plan=f.Plan(signal); f.Ledger.Register(f.Policy,signal,f.Quotes(),plan);
        var first=f.Account.Intents[0]; f.Ledger.StartSubmission(f.Policy.AccountKey,first.Id,f.Coordinator.CodeSha256);
        var restarted=new BrokerLedger(f.Store,f.Clock); Assert.Equal("unknown",restarted.Find(f.Policy.AccountKey)!.Intents[0].Phase);
        var coordinator=f.NewCoordinator(restarted); var result=await coordinator.Run(default);
        Assert.Equal("broker-submission-unknown",result.Code); Assert.Equal(0,f.Gateway.Posts);
    }
    [Fact] public async Task PausingRetainsPendingReservationsAndStillAllowsReconciliation()
    {
        using var f=new Fixture(); await f.Coordinator.Run(default); new PaperLedger(f.Store).Pause(true);
        var result=await f.Coordinator.Run(default); Assert.Equal("paused",result.Status);
        Assert.All(f.Account.Intents,i=>Assert.Equal("filled",i.Phase)); Assert.Equal(3,f.Gateway.Posts);
    }
    [Fact] public async Task PendingQuantitiesWithoutHistoryDoNotInventDebitsOrHoldings()
    {
        using var f=new Fixture(); f.Gateway.HideHistory=true;
        var result=await f.Coordinator.Run(default); Assert.Equal("execution-position-reconciliation",result.Code);
        Assert.Empty(f.Account.Fills); Assert.Empty(f.Account.Holdings); Assert.Equal(20,f.Account.CashGbp);
        Assert.All(f.Account.Intents.Where(i=>i.BrokerOrderId is not null),i=>Assert.True(i.ReportedFilled>0));
        f.Gateway.HideHistory=false; await f.Coordinator.Run(default); await f.Coordinator.Run(default); Assert.All(f.Account.Intents,i=>Assert.Equal("filled",i.Phase));
    }
    [Fact] public async Task ActualFeeOverrunIsRecordedAndFreezesNewExecution()
    {
        using var f=new Fixture(); await f.Coordinator.Run(default);
        var first=f.Gateway.Rows[^1]; f.Gateway.Rows[^1]=first with { Fill=first.Fill! with { WalletImpact=first.Fill!.WalletImpact with { NetValue=-40m } } };
        var result=await f.Coordinator.Run(default); Assert.Equal("broker-reserve-breach",result.Code);
        Assert.Contains(f.Account.Fills.Values,x=>x.DebitGbp==40); Assert.True(f.Account.CashGbp<0);
        Assert.Equal(f.Account.ContributionsGbp,f.Account.CashGbp+f.Account.Intents.Sum(i=>i.Progress.DebitGbp));
        await f.Coordinator.Run(default); Assert.Equal(3,f.Gateway.Posts);
    }
    [Fact] public async Task DuplicateReorderedHistoryDoesNotBookFillsTwiceAndRevisionBlocks()
    {
        using var f=new Fixture(); await f.Coordinator.Run(default);
        f.Gateway.Rows.AddRange(f.Gateway.Rows.ToArray().Reverse()); await f.Coordinator.Run(default);
        Assert.Equal(3,f.Account.Fills.Count); var cash=f.Account.CashGbp;
        f.Gateway.Rows[0]=f.Gateway.Rows[0] with { Fill=f.Gateway.Rows[0].Fill! with { Price=1m } };
        var result=await f.Coordinator.Run(default); Assert.Equal("broker-fill-revision",result.Code); Assert.Equal(cash,f.Account.CashGbp);
        f.Gateway.HideHistory=true;Assert.Equal("broker-fill-revision",(await f.Coordinator.Run(default)).Code);
    }
    [Fact] public async Task EnvironmentAccountAndDisabledGatesPrecedeSubmission()
    {
        using var f=new Fixture(); f.Policy=f.Policy with { Enabled=false };
        Assert.Equal("execution-disabled",(await f.Coordinator.Run(default)).Code); Assert.Equal(0,f.Gateway.Reads);
        f.Policy=f.Policy with { Enabled=true, Environment="live",AccountType="stocks-isa",SyntheticEvidence=false };
        Assert.Equal("execution-environment-mismatch",(await f.Coordinator.Run(default)).Code); Assert.Equal(0,f.Gateway.Reads);
        f.Policy=f.Policy with { Environment="demo",AccountType="demo-invest",AccountId=43 };
        Assert.Equal("execution-account-mismatch",(await f.Coordinator.Run(default)).Code); Assert.Equal(0,f.Gateway.Posts);
    }
    [Fact] public async Task TwoConcurrentCyclesCannotDuplicateFundingOrSubmission()
    {
        using var f=new Fixture(); var entered=new TaskCompletionSource();var release=new TaskCompletionSource();
        f.Gateway.Wait=async()=>{entered.TrySetResult();await release.Task;};
        var first=f.Coordinator.Run(default); await entered.Task;
        Assert.Equal("busy",(await f.Coordinator.Run(default)).Status); release.SetResult(); await first;
        Assert.Equal(20,f.Account.ContributionsGbp); Assert.Equal(3,f.Gateway.Posts);
    }
    [Fact] public void RotatingRankingsCannotAccumulateMoreThanTheConfiguredHoldings()
    {
        using var f=new Fixture();var signal=f.Signals.Create(new(f.Run.Id,f.Dataset.Id));
        var held=signal.Predictions.Where(p=>p.TargetWeight==0).Take(3).ToDictionary(p=>p.Symbol,_=>1m);
        var targets=ExecutionCoordinator.ConstrainedTargets(signal,held,3,out var notes);
        Assert.Empty(targets);Assert.Equal(3,notes.Length);
    }
    [Fact] public async Task ChangedModelCannotObtainASecondAllowanceInTheSameAccount()
    {
        using var f=new Fixture();await f.Coordinator.Run(default);await f.Coordinator.Run(default);
        f.Policy=f.Policy with { RunId="another-run" };
        Assert.Throws<ResearchException>(()=>f.Ledger.BindAccount(f.Policy,f.Gateway.CurrentPositions()));
        Assert.Equal(20,f.Account.ContributionsGbp);
    }
    [Fact] public async Task StaleQuotesAndChangedRulesCannotSubmitAStoredPlan()
    {
        using var f=new Fixture();var signal=f.Signals.Create(new(f.Run.Id,f.Dataset.Id));f.Ledger.BindAccount(f.Policy,[]);
        f.Ledger.Register(f.Policy,signal,f.Quotes(),f.Plan(signal));
        f.Clock.Now=f.Clock.Now.AddMinutes(6);
        var result=await f.Coordinator.Run(default);Assert.Equal(0,f.Gateway.Posts);
        Assert.All(f.Account.Intents,i=>Assert.Equal("expired",i.Phase));Assert.Equal("processed",result.Status);
    }

    [Fact] public async Task CancellationAcknowledgementDoesNotReleaseCashAndLateFillIsBooked()
    {
        using var f=new Fixture();f.Gateway.AutoFill=false;await f.Coordinator.Run(default);
        var intent=f.Account.Intents[0];var reserve=intent.Progress.RemainingReserve;
        await f.Coordinator.Cancel(f.Policy.AccountKey,intent.Id,default);await f.Coordinator.Run(default);
        Assert.Equal(reserve,f.Account.Intents[0].Progress.RemainingReserve);Assert.Equal("cancel_requested",f.Account.Intents[0].Phase);
        var active=f.Gateway.Active.Single(o=>o.Id==intent.BrokerOrderId);var quantity=active.Quantity/2;
        var cancelled=active with { Status="CANCELLED",FilledQuantity=quantity };
        var debit=quantity*active.LimitPrice!.Value*(1+intent.Intent.MaximumFeeBps/10000);
        f.Gateway.Rows.Add(new(cancelled,new(2001,f.Clock.Now,quantity,active.LimitPrice.Value,"TRADE",new("GBP",-debit,1,[]))));
        f.Gateway.Active.Remove(active);await f.Coordinator.Run(default);
        Assert.Equal("cancelled",f.Account.Intents[0].Phase);Assert.Equal(quantity,f.Account.Intents[0].Progress.Filled);
        Assert.Equal(0,f.Account.Intents[0].Progress.RemainingReserve);Assert.Equal(20,f.Account.CashGbp+f.Account.Intents.Sum(i=>i.Progress.DebitGbp));
    }
    [Fact] public async Task LiveNeedsAMatchingNonSyntheticDemoReceiptForCodeContractAndRules()
    {
        // Authored mocks model the gates only; this receipt never leaves its temporary test workspace.
        using var f=new Fixture(realFlags:true);await f.Coordinator.Run(default);await f.Coordinator.Run(default);
        Assert.False(Assert.Single(f.Ledger.DemoReceipts()).Synthetic);
        f.Gateway.Rows.Clear();f.Gateway.Mode=BrokerEnvironment.Live;
        f.Policy=f.Policy with { Environment="live",AccountType="stocks-isa",WalletRule=new("positive-debit",true,"positive-charge") };
        Assert.Equal("execution-genuine-demo-required",(await f.Coordinator.Run(default)).Code);Assert.Equal(3,f.Gateway.Posts);
    }
    [Fact] public async Task LivePathCanSubmitAfterMatchingDemoEvidenceUsingAuthoredGatewayOnly()
    {
        using var f=new Fixture(realFlags:true);await f.Coordinator.Run(default);await f.Coordinator.Run(default);
        f.Gateway.Rows.Clear();f.Gateway.Mode=BrokerEnvironment.Live;f.Policy=f.Policy with { Environment="live",AccountType="stocks-isa" };
        Assert.Equal("processed",(await f.Coordinator.Run(default)).Status);Assert.Equal(6,f.Gateway.Posts);
        await f.Coordinator.Run(default);Assert.All(f.Account.Intents,i=>Assert.Equal("filled",i.Phase));Assert.Equal(20,f.Account.ContributionsGbp);
    }
    [Fact] public void ObservationsArrivingAfterSignalTimeCannotBecomeFeatures()
    {
        using var f=new Fixture();
        Assert.Equal("signal-input-availability",Assert.Throws<ResearchException>(()=>ForwardSignalBuilder.Build(f.Run.Id,f.Run.ReportSha256!,
            f.Report,f.Panel,f.Panel,f.Clock.Now,f.Clock.Now.AddSeconds(1))).Code);
    }

    [Fact] public async Task AnExternalOrderAppearingMidCycleStopsTheNextSubmission()
    {
        using var f=new Fixture();var injected=false;
        f.Gateway.OnSubmit=()=>
        {
            if(injected)return;injected=true;
            f.Gateway.Active.Add(new(999,f.Panel.Assets[0]+"_EQ","NEW",1,0,f.Clock.Now,2,"LIMIT","BUY","GBP","API","QUANTITY","DAY"));
        };
        var result=await f.Coordinator.Run(default);
        Assert.Equal("execution-external-pending-order",result.Code);Assert.Equal(1,f.Gateway.Posts);
        Assert.Equal(20,f.Account.ContributionsGbp);Assert.Contains(f.Account.Intents,i=>i.Phase=="planned");
    }
    [Fact] public async Task ChangedRulesAfterPlanningCannotBypassThePinnedBatchOrLiveReceipt()
    {
        using var f=new Fixture(realFlags:true);await f.Coordinator.Run(default);await f.Coordinator.Run(default);
        f.Gateway.Rows.Clear();f.Gateway.Mode=BrokerEnvironment.Live;f.Policy=f.Policy with {Environment="live",AccountType="stocks-isa"};
        var reads=0;
        var coordinator=new ExecutionCoordinator(f.Ledger,f.Signals,f.Store,f.Gateway,()=>f.Policy,_=>
        {
            var quotes=f.Quotes();reads++;
            return reads==1?quotes:quotes with {Items=quotes.Items.Select(q=>q with {Rule=q.Rule with {QuantityStep=.00001m}}).ToArray()};
        },f.Clock);
        var result=await coordinator.Run(default);
        Assert.Equal("execution-planned-rules-changed",result.Code);Assert.Equal(3,f.Gateway.Posts);
        Assert.All(f.Account.Intents,i=>Assert.Equal("planned",i.Phase));
    }

    [Fact] public async Task RejectedOrderWithPositiveFillCannotResumeAfterTheContradictionDisappears()
    {
        using var f=new Fixture();var signal=f.Signals.Create(new(f.Run.Id,f.Dataset.Id));
        f.Ledger.BindAccount(f.Policy,[]);f.Ledger.Register(f.Policy,signal,f.Quotes(),f.Plan(signal));
        var first=f.Account.Intents[0];var started=f.Ledger.StartSubmission(f.Policy.AccountKey,first.Id,f.Coordinator.CodeSha256);
        var response=await f.Gateway.PlaceLimit(started.Intent,default);f.Ledger.FinishSubmission(f.Policy.AccountKey,first.Id,response);
        f.Gateway.Rows[0]=f.Gateway.Rows[0] with {Order=f.Gateway.Rows[0].Order with {Status="REJECTED"}};
        Assert.Equal("broker-fill-reconciliation",(await f.Coordinator.Run(default)).Code);
        f.Gateway.Rows.Clear();
        Assert.Equal("broker-fill-reconciliation",(await f.Coordinator.Run(default)).Code);
        Assert.Equal(1,f.Gateway.Posts);Assert.Equal(2,f.Account.Intents.Count(i=>i.Phase=="planned"));
    }

    [Fact] public async Task EquivalentDecimalSpellingsAndTimestampOffsetsAreNotFillRevisions()
    {
        using var f=new Fixture();await f.Coordinator.Run(default);await f.Coordinator.Run(default);var cash=f.Account.CashGbp;
        f.Gateway.Rows[0]=f.Gateway.Rows[0] with {Fill=f.Gateway.Rows[0].Fill! with {Price=2.000m,FilledAt=f.Clock.Now.ToOffset(TimeSpan.FromHours(1))}};
        Assert.Equal("processed",(await f.Coordinator.Run(default)).Status);Assert.Equal(cash,f.Account.CashGbp);
        Assert.Equal(ExecutionPolicy.Hash(new {Amount=20m}),ExecutionPolicy.Hash(new {Amount=20.000m}));
    }

    [Fact] public async Task UnknownBindingCannotClaimAnIdenticalOrderCreatedMuchLater()
    {
        using var f=new Fixture();f.Gateway.UnknownAfterAccept=true;await f.Coordinator.Run(default);
        var unknown=f.Account.Intents.Single(i=>i.Phase=="unknown");var order=f.Gateway.Rows.Single().Order;
        f.Clock.Now=f.Clock.Now.AddMinutes(10);f.Gateway.Rows[0]=f.Gateway.Rows[0] with {Order=order with {CreatedAt=f.Clock.Now}};
        Assert.Equal("execution-unknown-binding",(await Assert.ThrowsAsync<ResearchException>(()=>f.Coordinator.BindUnknown(f.Policy.AccountKey,unknown.Id,order.Id,default))).Code);
        Assert.Equal("unknown",f.Account.Intents.Single(i=>i.Id==unknown.Id).Phase);Assert.Equal(1,f.Gateway.Posts);
    }

    internal sealed class FixedClock : TimeProvider
    { public DateTimeOffset Now=DateTimeOffset.Parse("2015-01-15T12:00:00Z");public override DateTimeOffset GetUtcNow()=>Now; }
    internal sealed class Fixture : IDisposable
    {
        public string Root=Path.Combine(Path.GetTempPath(),"twenty-forward-"+Guid.NewGuid().ToString("N"));
        public FixedClock Clock=new();public ResearchStore Store;public SignalStore Signals;public BrokerLedger Ledger;
        public MonthlyPanel Panel;public DatasetEntry Dataset;public RunEntry Run;public BacktestReport Report;public ExecutionPolicy Policy;
        public FakeGateway Gateway;public ExecutionCoordinator Coordinator;private readonly bool realFlags;
        public BrokerAccountState Account=>Ledger.Find(Policy.AccountKey)!;
        public Fixture(string hypothesis="ridge-relative-return",bool stress=false,bool realFlags=false)
        {
            this.realFlags=realFlags;
            Store=new(Root,Clock);var name=stress?"stress":"demo";var csv=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,name+".csv"));var metadata=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,name+".metadata.json"));
            if(realFlags)metadata=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(metadata).Replace("\"synthetic\": true","\"synthetic\": false"));
            Panel=MonthlyPanel.Read(csv,metadata);Dataset=Store.Import(csv,metadata,"authored fixture");
            Run=Store.Enqueue(new(Guid.NewGuid().ToString("D"),Dataset.Id,new(Hypothesis:hypothesis),new()),"test");Store.Claim();
            Report=Backtest.Run(Panel,new(Hypothesis:hypothesis),new());Store.Complete(Run.Id,Report);Run=Store.Find(Run.Id)!;
            var evidenceFile=Path.Combine(Root,"authored-evidence.txt");File.WriteAllText(evidenceFile,"Authored test evidence only; not actual broker/account verification.");
            var reference=new EvidenceReference(evidenceFile,Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(evidenceFile))));
            Policy=new(true,false,"demo",42,"demo-invest",Run.Id,Run.ReportSha256!,20,new("negative-debit",true,"positive-charge"),reference,reference,reference,reference,Path.Combine(Root,"quotes.json"),Dataset.Id,!realFlags);
            Signals=new(Store,Clock);Ledger=new(Store,Clock);Gateway=new(Clock);Coordinator=NewCoordinator(Ledger);
        }
        public ExecutionCoordinator NewCoordinator(BrokerLedger ledger)=>new(ledger,Signals,Store,Gateway,()=>Policy,_=>Quotes(),Clock);
        public ExecutionQuotes Quotes()=>new("execution-quotes-v1",Panel.Assets.Select(s=>new QuoteEntry(new(s,s+"_EQ","GBP",2,2,.0001m,1,25,Clock.Now,true),"issuer:"+s,.01m,"authored quote",new string('a',64),"GBP",!realFlags)).ToArray());
        public BuyPlan Plan(ForwardSignal signal)=>LimitPlanner.Plan(20,ImmutableDictionary<string,decimal>.Empty,ImmutableDictionary<string,decimal>.Empty,
            signal.Predictions.Where(p=>p.TargetWeight>0).ToDictionary(p=>p.Symbol,p=>p.TargetWeight),Quotes().Items.ToDictionary(q=>q.Rule.Symbol,q=>q.Rule),signal.MaximumWeight,Clock.Now);
        public void Dispose(){Store.Dispose();Directory.Delete(Root,true);}
    }
    internal sealed class FakeGateway(FixedClock clock) : IBrokerGateway
    {
        public BrokerEnvironment Mode=BrokerEnvironment.Demo;public BrokerEnvironment Environment=>Mode;public int Posts,Reads;public bool UnknownAfterAccept,HideHistory;public bool AutoFill=true;
        public List<BrokerOrder> Active=[];
        public Func<Task>? Wait;public Action? OnSubmit;public List<BrokerHistoryItem> Rows=[];
        public async Task<BrokerSummary> Summary(CancellationToken ct){Reads++;if(Wait is not null){var w=Wait;Wait=null;await w();}return new(42,"GBP",new(100-Rows.DistinctBy(r=>r.Fill!.Id).Sum(r=>-r.Fill!.WalletImpact.NetValue),0));}
        public BrokerPosition[] CurrentPositions()=>Rows.DistinctBy(r=>r.Fill!.Id).GroupBy(r=>r.Order.Ticker).Select(g=>new BrokerPosition(new(g.Key),g.Sum(r=>r.Fill!.Quantity))).ToArray();
        public Task<BrokerPosition[]> Positions(CancellationToken ct)=>Task.FromResult(CurrentPositions());
        public Task<BrokerOrder[]> PendingOrders(CancellationToken ct)=>Task.FromResult(Active.ToArray());
        public Task<BrokerHistorySnapshot> History(CancellationToken ct)=>Task.FromResult(new BrokerHistorySnapshot(HideHistory?[]:Rows.ToArray(),true,1));
        public Task<SubmissionResult> PlaceLimit(BuyIntent intent,CancellationToken ct,string? localIntentId=null)
        {
            OnSubmit?.Invoke();Posts++;var order=new BrokerOrder(100+Posts,intent.BrokerTicker,"FILLED",intent.Quantity,intent.Quantity,clock.Now,intent.LimitPrice,"LIMIT","BUY","GBP","API","QUANTITY","DAY");
            if(!AutoFill){order=order with {Status="NEW",FilledQuantity=0};Active.Add(order);return Task.FromResult(new SubmissionResult("acknowledged",order,null));}
            var debit=intent.Quantity*intent.LimitPrice*(1+intent.MaximumFeeBps/10000);
            Rows.Add(new(order,new(1000+Posts,clock.Now,intent.Quantity,intent.LimitPrice,"TRADE",new("GBP",-debit,1,[]))));
            return Task.FromResult(UnknownAfterAccept?new SubmissionResult("unknown",null,"timeout"):new("acknowledged",order,null));
        }
        public Task<bool> RequestCancel(long id,CancellationToken ct)=>Task.FromResult(true);
    }
}
