using System.Net;
using System.Text;
using System.Text.Json;
using Twenty.Api.Execution;
using Twenty.Core.Execution;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class BrokerContractTests
{
    [Theory]
    [InlineData("https://untrusted.example/api/v0/equity/history/orders?cursor=1")]
    [InlineData("//untrusted.example/api/v0/equity/history/orders?cursor=1")]
    [InlineData("/api/v0/equity/history/orders/../account/summary")]
    [InlineData("/api/v0/equity/history/orders?cursor=1&cursor=2")]
    [InlineData("/api/v0/equity/history/orders?limit=51")]
    [InlineData("/api/v0/equity/history/orders?cursor=1#fragment")]
    [InlineData("/api/v0/equity/history/orders?token=secret")]
    [InlineData("/api/v0/equity/history/orders?cursor=%31")]
    public void PaginationCannotChangeOriginPathOrBudget(string path)=>Assert.Throws<ResearchException>(()=>Trading212Client.ValidateHistoryPath(path));
    [Fact] public void DocumentedCursorAndTickerPathsAreAccepted()
    {
        Trading212Client.ValidateHistoryPath("/api/v0/equity/history/orders?cursor=1760346100000&limit=50&ticker=AAPL_US_EQ");
    }
    [Fact] public async Task RepeatedPageCursorStopsBeforeASecondRequest()
    {
        var calls=0;var handler=new Stub(_=>{calls++;return Json("{\"items\":[],\"nextPagePath\":\"/api/v0/equity/history/orders?limit=50\"}");});
        var client=new Trading212Client(new HttpClient(handler),BrokerEnvironment.Demo,"fixture-key","fixture-secret");
        Assert.Equal("broker-pagination-cycle",(await Assert.ThrowsAsync<ResearchException>(()=>client.History(default))).Code);Assert.Equal(1,calls);
    }
    [Fact] public async Task OffOriginNextPageNeverReceivesCredentials()
    {
        var calls=0;var handler=new Stub(_=>{calls++;return Json("{\"items\":[],\"nextPagePath\":\"https://untrusted.example/steal\"}");});
        var client=new Trading212Client(new HttpClient(handler),BrokerEnvironment.Demo,"fixture-key","fixture-secret");
        Assert.Equal("broker-pagination-origin",(await Assert.ThrowsAsync<ResearchException>(()=>client.History(default))).Code);Assert.Equal(1,calls);
    }
    [Theory]
    [InlineData("{\"id\":42,\"ID\":43,\"currency\":\"GBP\",\"cash\":{\"availableToTrade\":20,\"reservedForOrders\":0}}")]
    [InlineData("{\"id\":42,\"currency\":\"GBP\",\"cash\":{\"availableToTrade\":20}}")]
    public async Task AmbiguousOrMissingFinancialFieldsCannotBecomeVerifiedAccountData(string json)
    {
        var client=new Trading212Client(new HttpClient(new Stub(_=>Json(json))),BrokerEnvironment.Demo,"fixture-key","fixture-secret");
        await Assert.ThrowsAnyAsync<Exception>(()=>client.Summary(default));
    }
    [Fact] public async Task ExactBodiesAreArchivedPrivatelyWithoutAuthenticationHeaders()
    {
        var root=Path.Combine(Path.GetTempPath(),"twenty-broker-wire-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            const string payload="{\"id\":42, \"currency\":\"GBP\",\"cash\":{\"availableToTrade\":20,\"reservedForOrders\":0}}";
            var handler=new Stub(r=>{var response=Json(payload);response.RequestMessage=r;return response;});
            var client=new Trading212Client(new HttpClient(handler),BrokerEnvironment.Demo,"authored-username","authored-password",evidence:new(root));
            Assert.Equal(42,(await client.Summary(default)).Id);
            var files=Directory.GetFiles(Path.Combine(root,"broker-evidence"));
            Assert.Contains(files,p=>p.EndsWith(".body")&&File.ReadAllText(p)==payload);
            foreach(var file in files)
            {
                var content=File.ReadAllText(file);Assert.DoesNotContain("Authorization",content);Assert.DoesNotContain("authored-password",content);Assert.DoesNotContain("authored-username",content);
            }
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task NonIdempotentRequestBodyIsArchivedBeforeTransportFailure()
    {
        var root=Path.Combine(Path.GetTempPath(),"twenty-broker-post-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var handler=new Stub(_=>throw new HttpRequestException("authored connection failure"));
            var client=new Trading212Client(new HttpClient(handler),BrokerEnvironment.Demo,"fixture-key","fixture-secret",evidence:new(root));
            var result=await client.PlaceLimit(new BuyIntent("A","A_TEST_EQ",1,2,"GBP",2.01m,25),default);
            Assert.Equal("unknown",result.Outcome);
            var bodies=Directory.GetFiles(Path.Combine(root,"broker-evidence"),"*.body").Select(File.ReadAllText).ToArray();
            Assert.Contains(bodies,b=>b.Contains("\"ticker\":\"A_TEST_EQ\"")&&b.Contains("\"timeValidity\":\"DAY\""));
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public void InvalidEnvironmentCannotSilentlySelectLiveOrigin()=>Assert.Throws<ResearchException>(()=>new Trading212Client(new HttpClient(),(BrokerEnvironment)99,"fixture-key","fixture-secret"));
    [Fact] public void ExcludedTaxesRequireExplicitCompleteTaxEvidence()
    {
        var fill=new BrokerFill(1,DateTimeOffset.UtcNow,1,2,"TRADE",new("GBP",-2,1));
        Assert.Equal("broker-tax-evidence-missing",Assert.Throws<ResearchException>(()=>BrokerFillAccounting.Debit(fill,new("negative-debit",false,"positive-charge"))).Code);
    }
    [Fact] public async Task ExhaustedAccountRateHeaderDelaysTheNextRequestBeyondTheFixedPacing()
    {
        var count=0;var handler=new Stub(_=>
        {
            count++;var response=Json("{\"id\":42,\"currency\":\"GBP\",\"cash\":{\"availableToTrade\":20,\"reservedForOrders\":0}}");
            response.Headers.Add("x-ratelimit-remaining","0");response.Headers.Add("x-ratelimit-reset",DateTimeOffset.UtcNow.AddSeconds(20).ToUnixTimeSeconds().ToString());return response;
        });
        var client=new Trading212Client(new HttpClient(handler),BrokerEnvironment.Demo,"fixture-key","fixture-secret",TimeSpan.FromSeconds(6));
        await client.Summary(default);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.PendingOrders(default));Assert.Equal(1,count);
    }
    [Fact] public void PrivateConfigIsDisabledByDefaultAndRejectsMissingSyntheticDeclaration()
    {
        Assert.Null(ExecutionPolicy.Load(null));
        using var f=new ForwardExecutionTests.Fixture();var p=Path.Combine(f.Root,"policy.json");
        var json=JsonSerializer.Serialize(f.Policy,Twenty.Research.ResearchStore.Json);
        File.WriteAllText(p,json);Assert.Equal(f.Policy.PolicyHash,ExecutionPolicy.Load(p)!.PolicyHash);
        File.WriteAllText(p,json.Replace("\"syntheticEvidence\":true,",""));
        Assert.Equal("execution-policy-json",Assert.Throws<ResearchException>(()=>ExecutionPolicy.Load(p)).Code);
    }
    private static HttpResponseMessage Json(string value)=>new(HttpStatusCode.OK){Content=new StringContent(value,Encoding.UTF8,"application/json")};
    private sealed class Stub(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(send(request));}
}
