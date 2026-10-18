using System.Collections.Immutable;
using System.Net;
using System.Text;
using Twenty.Api.Execution;
using Twenty.Research;
using Twenty.Core.Execution;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class ExecutionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    private static InstrumentRule Rule(string currency = "GBP", decimal price = 3m) => new("A", "A_TEST_EQ", currency, price, price, .01m, 1, 50, Now, true);
    [Fact] public void LimitQuantityReservesCostsAndRoundsDown()
    {
        var plan = LimitPlanner.Plan(20, new Dictionary<string, decimal>(), new Dictionary<string, decimal>(),
            new Dictionary<string, decimal> { ["A"] = .5m }, new Dictionary<string, InstrumentRule> { ["A"] = Rule() }, .5m, Now);
        var order = Assert.Single(plan.Orders);
        Assert.Equal(3.30m, order.Quantity); Assert.Equal(9.9495m, order.MaximumDebitGbp);
        Assert.Equal(20, plan.CashRemainingGbp + order.MaximumDebitGbp);
    }
    [Fact] public void PenceQuotesConvertToPoundsForBudgetOnly()
    {
        var plan = LimitPlanner.Plan(20, new Dictionary<string, decimal>(), new Dictionary<string, decimal>(),
            new Dictionary<string, decimal> { ["A"] = 1 }, new Dictionary<string, InstrumentRule> { ["A"] = Rule("GBX", 300) }, 1, Now);
        var order = Assert.Single(plan.Orders); Assert.Equal(300, order.LimitPrice); Assert.InRange(order.MaximumDebitGbp, 19.8m, 20m);
    }
    [Fact] public void PendingExposureAndAppreciationPreventFurtherConcentration()
    {
        var plan = LimitPlanner.Plan(10, new Dictionary<string, decimal> { ["A"] = 5 }, new Dictionary<string, decimal> { ["A"] = 5 },
            new Dictionary<string, decimal> { ["A"] = .5m }, new Dictionary<string, InstrumentRule> { ["A"] = Rule() }, .5m, Now);
        Assert.Empty(plan.Orders); Assert.Single(plan.Notes); Assert.Equal(10, plan.CashRemainingGbp);
    }
    [Theory] [InlineData("USD",0)] [InlineData("GBP",6)] [InlineData("GBP",-1)]
    public void MissingFxBoundsOrStaleFutureQuotesReject(string currency, int age)
    {
        Assert.Throws<ResearchException>(() => LimitPlanner.Plan(20, new Dictionary<string, decimal>(), new Dictionary<string, decimal>(),
            new Dictionary<string, decimal> { ["A"] = 1 }, new Dictionary<string, InstrumentRule> { ["A"] = Rule(currency) with { ObservedAt = Now.AddMinutes(-age) } }, 1, Now));
    }
    [Fact] public void MinimumOrderRetainsCash()
    {
        var plan = LimitPlanner.Plan(.5m, new Dictionary<string, decimal>(), new Dictionary<string, decimal>(),
            new Dictionary<string, decimal> { ["A"] = 1 }, new Dictionary<string, InstrumentRule> { ["A"] = Rule() }, 1, Now);
        Assert.Empty(plan.Orders); Assert.Equal(.5m, plan.CashRemainingGbp);
    }
    [Fact] public void CumulativeFillsAreMonotoneBoundedAndCancellationReleasesOnlyUnfilledReserve()
    {
        var pending = new OrderProgress(2, 0, 0, 10, "pending");
        var partial = pending.Reconcile(1, 4, "partially_filled");
        Assert.Equal(6, partial.RemainingReserve); Assert.Equal(partial, partial.Reconcile(1, 4, "partially_filled"));
        Assert.Throws<ResearchException>(() => partial.Reconcile(.5m, 2, "partially_filled"));
        Assert.Throws<ResearchException>(() => partial.Reconcile(2, 11, "filled"));
        Assert.Throws<ResearchException>(() => partial.Reconcile(1, 4, "rejected"));
        var cancelled = partial.Reconcile(1, 4, "cancelled"); Assert.Equal(0, cancelled.RemainingReserve);
        Assert.Throws<ResearchException>(() => cancelled.Reconcile(2, 8, "filled"));
    }
    [Fact] public async Task LimitTransportUsesOfficialOriginAndQuantityContract()
    {
        string? body = null; Uri? uri = null; string? scheme = null;
        var handler = new Stub(async request => { uri = request.RequestUri; scheme = request.Headers.Authorization?.Scheme; body = await request.Content!.ReadAsStringAsync();
            return Json("{\"id\":12,\"ticker\":\"A_TEST_EQ\",\"quantity\":1,\"filledQuantity\":0,\"status\":\"NEW\"}"); });
        var result = await new Trading212Client(new HttpClient(handler), BrokerEnvironment.Demo, "fixture-key", "fixture-secret").PlaceLimit(Intent(), default);
        Assert.Equal("acknowledged", result.Outcome); Assert.Equal("https://demo.trading212.com/api/v0/equity/orders/limit", uri?.ToString());
        Assert.Equal("Basic", scheme); Assert.Contains("\"quantity\":1", body); Assert.Contains("\"timeValidity\":\"DAY\"", body); Assert.DoesNotContain("value", body);
    }
    [Theory] [InlineData(408,"unknown")] [InlineData(500,"unknown")] [InlineData(302,"unknown")] [InlineData(400,"rejected")] [InlineData(429,"rejected")]
    public async Task BrokerSubmissionDoesNotRetryAmbiguousOrRejectedRequests(int status, string outcome)
    {
        var count = 0; var handler = new Stub(_ => { count++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)); });
        var result = await new Trading212Client(new HttpClient(handler), BrokerEnvironment.Live, "fixture-key", "fixture-secret").PlaceLimit(Intent(), default);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(1, count);
    }
    [Fact] public async Task TimeoutAndMalformedAcknowledgementRemainUnknown()
    {
        foreach (var handler in new[] { new Stub(_ => throw new TaskCanceledException()), new Stub(_ => Task.FromResult(Json("{}"))) })
        {
            var result = await new Trading212Client(new HttpClient(handler), BrokerEnvironment.Demo, "fixture-key", "fixture-secret").PlaceLimit(Intent(), default);
            Assert.Equal("unknown", result.Outcome); Assert.Null(result.Order);
        }
    }
    [Fact] public async Task MissingPendingOrderDoesNotImplyCancelledOrFilled()
    {
        var client = new Trading212Client(new HttpClient(new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))), BrokerEnvironment.Demo, "fixture-key", "fixture-secret");
        Assert.Null(await client.PendingOrder(12, default));
    }
    [Fact] public void RehearsalPersistsDecisionsPartialFillsCancellationAndFundingDeduplication()
    {
        var root = Path.Combine(Path.GetTempPath(), "twenty-paper-" + Guid.NewGuid().ToString("N"));
        string id;
        try
        {
            using (var store = new ResearchStore(root))
            {
                var panel = MonthlyPanel.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"demo.csv")), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"demo.metadata.json")));
                var dataset = store.Import(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"demo.csv")), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"demo.metadata.json")), "fixture");
                var run = store.Enqueue(new(Guid.NewGuid().ToString("D"), dataset.Id, new(), new()), "test"); store.Claim(); store.Complete(run.Id, Backtest.Run(panel, new(), new()));
                var ledger = new PaperLedger(store); var request = new RehearsalRequest(Guid.NewGuid().ToString("D"), run.Id, "2026-10");
                var account = ledger.Rehearse(request); id = account.Id;
                Assert.Equal(20, account.ContributionsGbp); Assert.NotEmpty(account.Orders); Assert.All(account.Orders, o => Assert.Equal("pending",o.Progress.Status));
                Assert.Equal(id, ledger.Rehearse(request).Id); Assert.Single(ledger.Accounts());
                Assert.Throws<ResearchException>(()=>ledger.Rehearse(request with { RequestId=Guid.NewGuid().ToString("D") }));
                var partial = ledger.Reconcile(id, partial:true); Assert.All(partial.Orders,o=>Assert.Equal("partially_filled",o.Progress.Status));
                Assert.Equal(partial.CashGbp,ledger.Reconcile(id,partial:true).CashGbp);
                var cancelled = ledger.Reconcile(id,cancel:true); Assert.All(cancelled.Orders,o=>Assert.Equal(0,o.Progress.RemainingReserve));
                Assert.Equal(20,cancelled.CashGbp+cancelled.Orders.Sum(o=>o.Progress.DebitGbp));
                ledger.Pause(true); Assert.Throws<ResearchException>(()=>ledger.Rehearse(new(Guid.NewGuid().ToString("D"),run.Id,"2026-11")));
            }
            using (var store = new ResearchStore(root))
            {
                var ledger = new PaperLedger(store); Assert.True(ledger.Paused); var account = Assert.Single(ledger.Accounts()); Assert.Equal(id,account.Id);
                ledger.Pause(false); var next = ledger.Rehearse(new(Guid.NewGuid().ToString("D"),account.RunId,"2026-11"));
                Assert.Equal(40,next.ContributionsGbp); var filled = ledger.Reconcile(id); Assert.Equal(40,filled.CashGbp+filled.Orders.Sum(o=>o.Progress.DebitGbp));
                Assert.Equal(filled.CashGbp,ledger.Reconcile(id).CashGbp);
            }
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact] public void DuplicateListingsCannotEvadePerStockConcentration()
    {
        var rules = new Dictionary<string, InstrumentRule> { ["A"] = Rule(), ["B"] = Rule() with { Symbol = "B" } };
        Assert.Throws<ResearchException>(() => LimitPlanner.Plan(20, new Dictionary<string, decimal>(), new Dictionary<string, decimal>(),
            new Dictionary<string, decimal> { ["A"] = .5m, ["B"] = .5m }, rules, .5m, Now));
    }
    [Fact] public void FeesAndLimitPremiumCannotPushNewExposureAbovePostFillCap()
    {
        var rule = Rule() with { LimitPrice = 3.06m, QuantityStep = .000001m, MaximumFeeBps = 1000 };
        var plan = LimitPlanner.Plan(20, new Dictionary<string, decimal>(), new Dictionary<string, decimal>(),
            new Dictionary<string, decimal> { ["A"] = .5m }, new Dictionary<string, InstrumentRule> { ["A"] = rule }, .5m, Now);
        var order = Assert.Single(plan.Orders); var markedHolding = order.Quantity * rule.Ask;
        var nav = 20 - order.MaximumDebitGbp + markedHolding;
        Assert.True(markedHolding / nav <= .5m);
    }
    [Fact] public void OverweightHoldingBlocksItsOwnOrderButNotDilutingOrdersForOthers()
    {
        // Purchase-cap semantics mirror NotionalPortfolio: an existing holding at/above the cap is
        // never added to, but contributions still buy underweight names sized on post-cost NAV.
        var rules = new Dictionary<string, InstrumentRule> { ["A"] = Rule() with { Ask = 1, LimitPrice = 1, MaximumFeeBps = 0 },
            ["B"] = Rule() with { Symbol = "B", BrokerTicker = "B_TEST_EQ", Ask = 1, LimitPrice = 1, MaximumFeeBps = 1000 } };
        var plan = LimitPlanner.Plan(10, new Dictionary<string, decimal> { ["A"] = 10 }, new Dictionary<string, decimal>(),
            new Dictionary<string, decimal> { ["A"] = .5m, ["B"] = .5m }, rules, .5m, Now);
        var order = Assert.Single(plan.Orders); Assert.Equal("B", order.Symbol);
        Assert.Contains(plan.Notes, n => n.Contains("A:") && n.Contains("post-cost"));
        // B itself stays within the cap on worst-case post-cost NAV.
        var navFloor = 20 - 10 * (1 - 1 / 1.1m);
        Assert.True(order.Quantity * 1 <= navFloor * .5m);
    }
    [Fact] public async Task StalledResponseBodyIsBoundedAndSubmissionRemainsUnknown()
    {
        var handler = new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }));
        var client = new Trading212Client(new HttpClient(handler), BrokerEnvironment.Demo, "fixture-key", "fixture-secret", TimeSpan.FromMilliseconds(50));
        var result = await client.PlaceLimit(Intent(), default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("unknown", result.Outcome);
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer,int offset,int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset,SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer,int offset,int count) => throw new NotSupportedException();
    }
    private static BuyIntent Intent() => new("A","A_TEST_EQ",1,3,"GBP",3.1m,50);
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json,Encoding.UTF8,"application/json") };
    private sealed class Stub(Func<HttpRequestMessage,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken) => send(request); }
}
