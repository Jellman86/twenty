using System.Collections.Immutable;
using System.Net;
using System.Text;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Tiingo;

namespace Twenty.Tests;

public sealed class TiingoTests
{
    private const string FakeToken = "AuthoredUnitTestCredentialOnly0000";
    private const string Success = "{\"message\":\"You successfully sent a request\"}";
    private const string Row = "{\"date\":\"2020-01-02T00:00:00.000Z\",\"open\":10,\"high\":12,\"low\":9,\"close\":11,\"adjClose\":5.5,\"adjOpen\":5,\"adjHigh\":6,\"adjLow\":4.5,\"volume\":100,\"adjVolume\":50,\"divCash\":0,\"splitFactor\":1}";
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; Assert.Equal("api.tiingo.com", request.RequestUri!.Host); Assert.DoesNotContain(FakeToken, request.RequestUri.AbsoluteUri);
            Assert.Equal("Token", request.Headers.Authorization!.Scheme); Assert.Equal(FakeToken, request.Headers.Authorization.Parameter);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType); return Task.FromResult(respond(request)); }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "twenty-tiingo-" + Guid.NewGuid().ToString("N"));
        public CaptureStore Store; public Clock Clock = new(); public Handler Handler; public HttpClient Http; public TiingoClient Client;
        public Fixture(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
        {
            Store = new(Root); Handler = new(respond ?? (_ => new(HttpStatusCode.OK) { Content = new StringContent(Success) }));
            Http = new(Handler); Client = new(Http, Store, FakeToken, Clock);
        }
        public void Dispose() { Http.Dispose(); Store.Dispose(); Directory.Delete(Root, true); }
    }
    [Fact]
    public async Task HeaderAuthenticationAndRestartPacingNeverRetry()
    {
        using var f = new Fixture(); Assert.True(await f.Client.Check(default));
        Assert.Equal(Success.Length, f.Store.PrivateState<TiingoQuota>("tiingo-quota")!.MonthBytes);
        var restarted = new TiingoClient(f.Http, f.Store, FakeToken, f.Clock);
        Assert.Equal("tiingo-request-not-due", (await Assert.ThrowsAsync<ResearchException>(() => restarted.Check(default))).Code);
        Assert.Equal(1, f.Handler.Calls); f.Clock.Now = f.Clock.Now.AddSeconds(90); Assert.True(await restarted.Check(default));
        Assert.Equal(2, f.Store.PrivateState<TiingoQuota>("tiingo-quota")!.DayRequests);
    }
    [Theory]
    [InlineData("symbols", "tiingo-monthly-symbol-limit")]
    [InlineData("bytes", "tiingo-monthly-byte-limit")]
    [InlineData("requests", "tiingo-daily-request-limit")]
    public async Task EachPersistentFreeTierBudgetStopsBeforeTheNetwork(string kind, string expected)
    {
        using var f = new Fixture();
        f.Store.UpdatePrivateState<TiingoQuota>("tiingo-quota", _ => new(f.Clock.Now.AddMinutes(-2), "202610", "20261018",
            kind == "requests" ? TiingoClient.DailyRequestLimit : 0, kind == "bytes" ? TiingoClient.MonthlyByteLimit : 0,
            kind == "symbols" ? Enumerable.Range(0, 500).Select(i => "TEST" + i).ToImmutableArray() : []));
        var request = new TiingoHistoryRequest("TESTNEW", new(2020, 1, 1), new(2020, 1, 3));
        Assert.Equal(expected, (await Assert.ThrowsAsync<ResearchException>(() => f.Client.Capture(request, default))).Code);
        Assert.Equal(0, f.Handler.Calls);
    }
    [Fact]
    public async Task FailedRequestConsumesWorstCaseBytesAndRateLimitPauses()
    {
        using var f = new Fixture(_ => new(HttpStatusCode.TooManyRequests) { Content = new StringContent("provider error omitted") });
        Assert.Equal("tiingo-rate-limited", (await Assert.ThrowsAsync<ResearchException>(() => f.Client.Check(default))).Code);
        Assert.Equal(TiingoClient.MaximumResponseBytes, f.Store.PrivateState<TiingoQuota>("tiingo-quota")!.MonthBytes);
        f.Clock.Now = f.Clock.Now.AddMinutes(5);
        Assert.Equal("tiingo-source-paused", (await Assert.ThrowsAsync<ResearchException>(() => f.Client.Check(default))).Code);
        Assert.Equal(1, f.Handler.Calls);
    }
    [Fact]
    public async Task MonthAndDayRolloverRetainTheCrossBoundaryPacingReservation()
    {
        using var f = new Fixture(); f.Clock.Now = new(2026, 11, 1, 0, 0, 10, TimeSpan.Zero);
        f.Store.UpdatePrivateState<TiingoQuota>("tiingo-quota", _ => new(f.Clock.Now.AddSeconds(-20), "202610", "20261031", 900, TiingoClient.MonthlyByteLimit, ["OLD"]));
        Assert.Equal("tiingo-request-not-due", (await Assert.ThrowsAsync<ResearchException>(() => f.Client.Check(default))).Code);
        f.Clock.Now = f.Clock.Now.AddSeconds(90); Assert.True(await f.Client.Check(default));
        var quota = f.Store.PrivateState<TiingoQuota>("tiingo-quota")!;
        Assert.Equal("202611", quota.Month); Assert.Equal(1, quota.DayRequests); Assert.Empty(quota.Symbols); Assert.Equal(Success.Length, quota.MonthBytes);
    }
    [Fact]
    public async Task RedirectResponsesCannotForwardCredentialsOrRetry()
    {
        using var f = new Fixture(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new("https://untrusted.example/"); return r; });
        Assert.Equal("tiingo-read-unavailable", (await Assert.ThrowsAsync<ResearchException>(() => f.Client.Check(default))).Code);
        Assert.Equal(1, f.Handler.Calls); Assert.Equal(TiingoClient.MaximumResponseBytes, f.Store.PrivateState<TiingoQuota>("tiingo-quota")!.MonthBytes);
    }
    [Fact]
    public async Task HistoryKeepsExactPrivateBytesWithoutCredentialsAndHandlesEmptyCoverage()
    {
        using var f = new Fixture(_ => new(HttpStatusCode.OK) { Content = new StringContent("[" + Row + "]") });
        var result = await f.Client.Capture(new("UNIT", new(2020, 1, 1), new(2020, 1, 3)), default);
        Assert.Equal(1, result.Rows); Assert.Equal(new DateOnly(2020, 1, 2), result.First);
        Assert.Equal("[" + Row + "]", Encoding.UTF8.GetString(f.Store.Bytes(result.Sha256)));
        Assert.Single(f.Store.PrivateState<TiingoQuota>("tiingo-quota")!.Symbols);
        Assert.Empty(TiingoClient.ValidateHistory(Encoding.UTF8.GetBytes("[]"), new(2020, 1, 1), new(2020, 1, 3)));
        Assert.DoesNotContain(FakeToken, string.Join("", Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != ".writer.lock").Select(File.ReadAllText)));
    }
    [Fact]
    public async Task CaptureKeepsDottedAndHyphenatedSymbolsInDistinctBuckets()
    {
        using var f = new Fixture(_ => new(HttpStatusCode.OK) { Content = new StringContent("[" + Row + "]") });
        await f.Client.Capture(new("AAA.B", new(2020, 1, 1), new(2020, 1, 3)), default);
        f.Clock.Now = f.Clock.Now.AddSeconds(90);
        await f.Client.Capture(new("AAA-B", new(2020, 1, 1), new(2020, 1, 3)), default);
        var buckets = Directory.GetDirectories(Path.Combine(f.Root, "capture-v1", "observations"));
        Assert.Equal(2, buckets.Length);
        Assert.All(buckets, bucket => Assert.Single(f.Store.Observations(Path.GetFileName(bucket))));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("price")]
    [InlineData("date")]
    [InlineData("range")]
    [InlineData("adj-volume-missing")]
    [InlineData("adj-volume-negative")]
    public void MalformedOrAmbiguousPriceRowsCannotBeRegistered(string kind)
    {
        var row = kind switch { "duplicate" => Row.Replace("\"open\":10", "\"open\":10,\"open\":12"),
            "price" => Row.Replace("\"close\":11", "\"close\":99"), "date" => Row.Replace("00:00:00", "12:00:00"),
            "adj-volume-missing" => Row.Replace(",\"adjVolume\":50", ""),
            "adj-volume-negative" => Row.Replace("\"adjVolume\":50", "\"adjVolume\":-1"), _ => Row };
        Assert.Throws<ResearchException>(() => TiingoClient.ValidateHistory(Encoding.UTF8.GetBytes("[" + row + "]"),
            kind == "range" ? new(2020, 1, 3) : new(2020, 1, 1), new(2020, 1, 5)));
    }
    [Fact]
    public void CredentialsAcceptOnePrivateRouteAndNeverMixSources()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Root, "unit-key"); File.WriteAllText(path, FakeToken);
        Assert.Equal(FakeToken, TiingoClient.ReadToken(null, path)); Assert.Null(TiingoClient.ReadToken("", null));
        Assert.Equal("tiingo-secret-configuration-conflict", Assert.Throws<ResearchException>(() => TiingoClient.ReadToken(FakeToken, path)).Code);
        Assert.Throws<ResearchException>(() => TiingoClient.ReadToken("unsafe\r\nheader", null));
    }
}
