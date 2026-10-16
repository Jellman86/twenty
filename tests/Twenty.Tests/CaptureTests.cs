using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Twenty.Api.Collection;
using Twenty.Ingestion.Capture;

namespace Twenty.Tests;

public sealed class CaptureTests
{
    private static readonly CaptureSource Listed = CaptureSources.Find("nasdaq-listed");

    [Fact]
    public async Task CapturesValidatedFilesAndRecordsUnchangedDaysAsObservations()
    {
        using var f = new Fixture();
        f.Handler.Respond = request => request.Headers.IfNoneMatch.Any(t => t.Tag == "\"v1\"")
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Text(NasdaqFile(), etag: "\"v1\"");
        var first = await f.Collector.Collect(Listed, f.Store.Status(Listed.Id), CancellationToken.None);
        Assert.Equal("captured", first.Result);
        Assert.False(f.Collector.Due(Listed, f.Store.Status(Listed.Id), f.Clock.GetUtcNow().AddHours(1)));
        f.Clock.Advance(TimeSpan.FromDays(1));
        var second = await f.Collector.Collect(Listed, f.Store.Status(Listed.Id), CancellationToken.None);
        Assert.Equal("unchanged", second.Result);
        var observations = f.Store.Observations(Listed.Id);
        Assert.Equal(2, observations.Count);
        Assert.Equal(observations[0].Sha256, observations[1].Sha256);
        Assert.True(observations[1].NotModified);
        Assert.Equal(NasdaqFile(), Encoding.ASCII.GetString(f.Store.Bytes(observations[1].Sha256)));
        Assert.Equal(2, f.Store.Status(Listed.Id).Observations);
    }

    [Fact]
    public async Task InvalidOrDisguisedContentStoresNothing()
    {
        using var f = new Fixture();
        f.Handler.Respond = _ => Text("Symbol|Wrong|Header\n");
        var invalid = await f.Collector.Collect(Listed, f.Store.Status(Listed.Id), CancellationToken.None);
        Assert.Equal(("failed", "invalid-unexpected-header"), (invalid.Result, invalid.Code));
        Assert.Equal(f.Clock.GetUtcNow() + TimeSpan.FromHours(1), f.Store.Status(Listed.Id).PausedUntil);
        Assert.Empty(f.Store.Observations(Listed.Id));
        f.Clock.Advance(TimeSpan.FromHours(2));
        f.Handler.Respond = _ => Text("<html>Checking your browser</html>", "text/html");
        var challenge = await f.Collector.Collect(Listed, f.Store.Status(Listed.Id), CancellationToken.None);
        Assert.Equal(("paused", "challenge"), (challenge.Result, challenge.Code));
        Assert.Empty(f.Store.Observations(Listed.Id));
    }

    [Fact]
    public async Task BlocksPauseTheSourceWithGrowingBackOffAndAreNotRetried()
    {
        using var f = new Fixture();
        f.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        Assert.Equal("blocked", (await f.Collector.Collect(Listed, f.Store.Status(Listed.Id), CancellationToken.None)).Code);
        Assert.Equal(f.Clock.GetUtcNow() + TimeSpan.FromHours(6), f.Store.Status(Listed.Id).PausedUntil);
        var results = await f.Collector.RunDue([Listed], CancellationToken.None);
        Assert.Equal("not-due", Assert.Single(results).Result);
        Assert.Equal(1, f.Handler.Requests);
        f.Clock.Advance(TimeSpan.FromHours(7));
        f.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Equal("rate-limited", (await f.Collector.Collect(Listed, f.Store.Status(Listed.Id), CancellationToken.None)).Code);
        Assert.Equal(f.Clock.GetUtcNow() + TimeSpan.FromHours(12), f.Store.Status(Listed.Id).PausedUntil);
        f.Clock.Advance(TimeSpan.FromHours(13));
        f.Handler.Respond = _ => { var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); r.Headers.Add("cf-mitigated", "challenge"); return r; };
        Assert.Equal("challenge", (await f.Collector.Collect(Listed, f.Store.Status(Listed.Id), CancellationToken.None)).Code);
        Assert.Equal(f.Clock.GetUtcNow() + TimeSpan.FromHours(24), f.Store.Status(Listed.Id).PausedUntil);
    }

    [Fact]
    public async Task RedirectsMustStayOnTheSourcesOwnHosts()
    {
        using var f = new Fixture();
        f.Handler.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("moved.txt", StringComparison.Ordinal)
            ? Text(NasdaqFile())
            : Redirect("https://www.nasdaqtrader.com/dynamic/SymDir/moved.txt");
        Assert.Equal("captured", (await f.Collector.Collect(Listed, f.Store.Status(Listed.Id), CancellationToken.None)).Result);
        using var g = new Fixture();
        g.Handler.Respond = _ => Redirect("https://elsewhere.example/nasdaqlisted.txt");
        Assert.Equal("url-not-allowed", (await g.Collector.Collect(Listed, g.Store.Status(Listed.Id), CancellationToken.None)).Code);
    }

    [Fact]
    public async Task ShillerFileIsFoundFromItsCurrentLinkAndChecked()
    {
        using var f = new Fixture();
        var source = CaptureSources.Find("shiller-ie-data");
        var spreadsheet = new byte[8192];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(spreadsheet, 0);
        f.Handler.Respond = request => request.RequestUri!.Host == "shillerdata.com"
            ? Text("""<a href="//img1.wsimg.com/blobby/go/abc/downloads/def/ie_data.xls?ver=17">US Stock Markets</a>""", "text/html")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(spreadsheet) };
        var outcome = await f.Collector.Collect(source, f.Store.Status(source.Id), CancellationToken.None);
        Assert.Equal("captured", outcome.Result);
        Assert.Equal("https://img1.wsimg.com/blobby/go/abc/downloads/def/ie_data.xls?ver=17", outcome.Observation!.Url);
    }

    [Fact]
    public void ZipSourcesCheckTheEntryAndItsColumnHeader()
    {
        var french = CaptureSources.Find("french-factors-monthly");
        Assert.Null(french.Validate(Zip("F-F_Research_Data_Factors.csv", "This file was created using CRSP.\r\n\r\n,Mkt-RF,SMB,HML,RF\r\n192607, 2.89\r\n")));
        Assert.Equal("missing-entry", french.Validate(Zip("other.csv", ",Mkt-RF,SMB,HML,RF\n")));
        Assert.Equal("unexpected-header", french.Validate(Zip("F-F_Research_Data_Factors.csv", "notes\n,Mkt,SMB\n")));
        Assert.Equal("invalid-zip", french.Validate(Encoding.ASCII.GetBytes("not a zip")));
    }

    [Fact]
    public void RolesSplitSourcesAndExplicitListsOverride()
    {
        Assert.Contains(CaptureSources.For(CaptureRole.Local, null), s => s.Id == "french-factors-monthly");
        Assert.Contains(CaptureSources.For(CaptureRole.Local, null), s => s.Id == "nasdaq-listed");
        Assert.DoesNotContain(CaptureSources.For(CaptureRole.Remote, null), s => s.Host != CaptureHost.Remote);
        Assert.Equal(["nasdaq-listed", "tiingo-tickers"], CaptureSources.For(CaptureRole.Remote, "nasdaq-listed, tiingo-tickers").Select(s => s.Id));
        Assert.Equal(CaptureSources.All.Length, CaptureSources.For(CaptureRole.Remote, "all").Length);
        Assert.Equal("unknown-source", Assert.Throws<CaptureException>(() => CaptureSources.For(CaptureRole.Local, "yahoo")).Code);
    }

    [Fact]
    public void CollectorSettingsFailAtStartUpRatherThanLater()
    {
        static CollectorSettings From(bool headless, params (string, string)[] values) => CollectorSettings.From(
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build(), "/data", headless);
        Assert.Null(From(false).Role);
        Assert.Equal(CaptureRole.Local, From(false, ("TWENTY_COLLECTOR_ROLE", "local")).Role);
        Assert.Equal("collector-remote-needs-ship-url-and-token", Assert.Throws<CaptureException>(() => From(true)).Code);
        Assert.Equal("ingest-token-too-short", Assert.Throws<CaptureException>(() => From(false, ("TWENTY_INGEST_TOKEN", "short"))).Code);
        Assert.Equal("twenty-ship-url-invalid", Assert.Throws<CaptureException>(() => From(true, ("TWENTY_SHIP_URL", "http://quark/api"), ("TWENTY_INGEST_TOKEN", new string('x', 40)))).Code);
        Assert.Equal("collector-remote-role-needs-headless", Assert.Throws<CaptureException>(() => From(false, ("TWENTY_COLLECTOR_ROLE", "remote"))).Code);
        var remote = From(true, ("TWENTY_SHIP_URL", "https://twenty.pownet.uk/api/ingest/captures"), ("TWENTY_INGEST_TOKEN", new string('x', 40)), ("TWENTY_COLLECTOR_NAME", "oracle-1"));
        Assert.Equal((CaptureRole.Remote, "oracle-1", Path.Combine("/data", "captures")), (remote.Role!.Value, remote.Name, remote.Root));
    }

    [Fact]
    public async Task RemoteCapturesShipToTheReceiverAndAreDroppedOnlyAfterAcknowledgement()
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        using var remote = new Fixture(CaptureRole.Remote);
        var receiverRoot = Path.Combine(Path.GetTempPath(), "twenty-capture-rx-" + Guid.NewGuid().ToString("N"));
        using var receiving = new CaptureStore(receiverRoot);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        CollectorModule.Map(app, new CollectorSettings { Root = receiverRoot, IngestToken = token }, receiving);
        await app.StartAsync();
        try
        {
            remote.Handler.Respond = _ => Text(NasdaqFile());
            var captured = (await remote.Collector.Collect(Listed, remote.Store.Status(Listed.Id), CancellationToken.None)).Observation!;
            Assert.Single(remote.Store.Outbox());
            using var http = new HttpClient();
            var endpoint = new Uri(new Uri(app.Urls.Single()), "/api/ingest/captures");

            Assert.Equal("ship-http-401", (await new CaptureShipper(http, remote.Store, endpoint, "wrong-token-of-sufficient-length-000000").ShipAll(CancellationToken.None)).Error);
            Assert.Single(remote.Store.Outbox());

            var (shipped, error) = await new CaptureShipper(http, remote.Store, endpoint, token).ShipAll(CancellationToken.None);
            Assert.Equal((1, (string?)null), (shipped, error));
            Assert.Empty(remote.Store.Outbox());
            var received = Assert.Single(receiving.Observations(Listed.Id));
            Assert.Equal((captured.Id, captured.Sha256, "test"), (received.Id, received.Sha256, received.Collector));
            Assert.NotNull(received.ReceivedAt);
            Assert.Equal(NasdaqFile(), Encoding.ASCII.GetString(receiving.Bytes(captured.Sha256)));
            // The latest object stays on the remote so tomorrow's conditional request can be confirmed against it.
            Assert.True(remote.Store.HasObject(captured.Sha256));

            // Replays are idempotent; a body that does not match its observation is refused.
            Assert.Equal(HttpStatusCode.OK, (await Post(http, endpoint, token, captured, Encoding.ASCII.GetBytes(NasdaqFile()))).StatusCode);
            Assert.Single(receiving.Observations(Listed.Id));
            var forged = captured with { Id = Guid.NewGuid().ToString("N") };
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(http, endpoint, token, forged, Encoding.ASCII.GetBytes("tampered"))).StatusCode);
            Assert.Single(receiving.Observations(Listed.Id));
        }
        finally { await app.StopAsync(); Directory.Delete(receiverRoot, true); }
    }

    private static async Task<HttpResponseMessage> Post(HttpClient http, Uri endpoint, string token, CaptureObservation observation, byte[] body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add(CaptureShipper.ObservationHeader, Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(observation,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));
        return await http.SendAsync(request);
    }

    private static string NasdaqFile() =>
        "Symbol|Security Name|Market Category|Test Issue|Financial Status|Round Lot Size|ETF|NextShares\n" +
        string.Concat(Enumerable.Range(0, 120).Select(i => $"T{i:D3}|Test Security {i}|Q|N|N|100|N|N\n")) +
        "File Creation Time: 1016202607:00|||||||\n";

    private static HttpResponseMessage Text(string body, string type = "text/plain", string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.ASCII, type) };
        if (etag is not null) response.Headers.ETag = new(etag);
        return response;
    }
    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };
    private static byte[] Zip(string name, string contents)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(contents);
        return buffer.ToArray();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new(HttpStatusCode.NotFound);
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; return Task.FromResult(Respond(request)); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "twenty-capture-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient http;
        public FakeHandler Handler { get; } = new();
        public ManualClock Clock { get; } = new();
        public CaptureStore Store { get; }
        public CaptureCollector Collector { get; }
        public Fixture(CaptureRole role = CaptureRole.Local)
        {
            Store = new CaptureStore(root); http = new HttpClient(Handler);
            Collector = new CaptureCollector(http, Store, role, new CaptureOptions { Collector = "test", PerHostInterval = TimeSpan.Zero }, Clock);
        }
        public void Dispose() { http.Dispose(); Store.Dispose(); Directory.Delete(root, true); }
    }
}
