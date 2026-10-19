using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Twenty.Core.Data;
using Twenty.Ingestion.Sec;

namespace Twenty.Tests;

public class SecCollectorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2024-03-04T12:00:00Z");
    // All HTTP is replaced by an in-memory handler. This is not a real contact identity.
    private const string Contact = "offline-fixture@sec.gov";
    private static byte[] Fixture(string name = "submissions") => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name + ".synthetic.json"));
    private static byte[] Change(Action<JsonNode> edit)
    {
        var json = JsonNode.Parse(Fixture())!; edit(json); return Encoding.UTF8.GetBytes(json.ToJsonString());
    }
    private static HttpResponseMessage Response(byte[] bytes, string type = "application/json", HttpStatusCode code = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(code) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(type);
        response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
        return response;
    }
    private sealed class Clock : ISecClock
    {
        public DateTimeOffset UtcNow { get; private set; } = Now;
        public TimeSpan MonotonicNow { get; private set; }
        public readonly List<TimeSpan> Delays = [];
        public void Advance(TimeSpan amount) { UtcNow += amount; MonotonicNow += amount; }
        public Task Delay(TimeSpan delay, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Delays.Add(delay); Advance(delay); return Task.CompletedTask;
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class Rig : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "twenty-sec-test-" + Guid.NewGuid().ToString("N"));
        public readonly Clock Clock = new();
        public readonly SecLimits Limits;
        public readonly SecArchive Archive;
        public readonly SecClient Client;
        public readonly List<(Uri Uri, DateTimeOffset At, string Agent)> Requests = [];
        private readonly HttpClient http;
        public Rig(Func<HttpRequestMessage, HttpResponseMessage>? respond = null, SecLimits? limits = null)
        {
            Limits = limits ?? new();
            Archive = new(Root, Limits);
            http = new(new Handler((request, _) =>
            {
                Requests.Add((request.RequestUri!, Clock.UtcNow, request.Headers.UserAgent.ToString()));
                return Task.FromResult(respond is null ? DefaultResponse(request) : respond(request));
            }));
            Client = new(http, Archive, Contact, Limits, Clock, new SecRequestGate(Clock));
        }
        private static HttpResponseMessage DefaultResponse(HttpRequestMessage request) =>
            request.RequestUri!.Host == "www.sec.gov" ? Response(Encoding.UTF8.GetBytes("<html>AUTHORED SYNTHETIC " + request.RequestUri.AbsolutePath + "</html>"), "text/html") :
            Response(Fixture(request.RequestUri.AbsolutePath.Contains("-submissions-", StringComparison.Ordinal) ? "history" : "submissions"));
        public Task<SecCollectionResult> Collect() => new SecCollector(Client, Archive, Limits, Clock)
            .Collect("1", new DateOnly(2023, 1, 1), new DateOnly(2024, 3, 4));
        public void Dispose() { http.Dispose(); Archive.Dispose(); Directory.Delete(Root, recursive: true); }
    }

    [Fact]
    public async Task RecentHistoryAmendmentsAndOriginalsAreArchivedWithObservedTiming()
    {
        using var rig = new Rig();
        var result = await rig.Collect();
        Assert.Equal(3, result.Filings.Length);
        Assert.Equal(5, result.Requests);
        Assert.Equal(1, result.HistoryFilesVisited);
        Assert.False(result.HistoryLimitReached);
        Assert.False(result.DocumentLimitReached);
        Assert.Equal("0000000001", result.Cik);
        Assert.True(result.Filings[0].Metadata.IsAmendment);
        Assert.Equal("0000000002-23-000001", result.Filings[2].Metadata.Accession); // Agent prefix differs from issuer CIK.
        Assert.All(result.Filings, item =>
        {
            Assert.True(item.AvailableAt >= Now);
            Assert.True(item.AvailableAt > item.Metadata.AcceptedAt);
            Assert.Equal(item.DocumentSource.ContentSha256, SecArchive.Hash(rig.Archive.Bytes(item.DocumentSource)));
            var domain = item.ToDomain("verified-issuer-id");
            Assert.Equal(item.AvailableAt, domain.Evidence.AvailableAt);
            Assert.Equal(item.AvailableAt, domain.Evidence.PublishedAt); // Observed bound, not original dissemination time.
            Assert.Equal(AvailabilityEvidence.ObservedPublication, domain.Evidence.AvailabilityEvidence);
            Assert.Null(domain.AmendsAccession); // No guessed amendment ancestry.
        });
        Assert.Empty(result.KnownAt(Now.AddTicks(-1)));
        Assert.Equal(3, result.KnownAt(result.CompletedAt).Length);
        Assert.Throws<SecCollectionException>(() => result.KnownAt(result.CompletedAt.AddSeconds(1)));
        for (var i = 1; i < rig.Requests.Count; i++) Assert.True(rig.Requests[i].At - rig.Requests[i - 1].At >= TimeSpan.FromMilliseconds(500));
        Assert.All(rig.Requests, x => Assert.Contains(Contact, x.Agent));
        Assert.DoesNotContain(Contact, string.Join("", Directory.GetFiles(rig.Root, "*", SearchOption.AllDirectories)
            .Where(x => !x.EndsWith(".collector.lock", StringComparison.Ordinal)).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task FreshCacheIsIdempotentAndSurvivesRestartWithoutNewRequests()
    {
        using var rig = new Rig();
        var first = await rig.Collect();
        var again = await rig.Collect();
        Assert.Equal(5, rig.Requests.Count);
        Assert.Equal<CollectedSecFiling>(first.Filings, again.Filings);
        rig.Archive.Dispose();
        using var reopened = new SecArchive(rig.Root, rig.Limits);
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Cache should avoid HTTP")));
        var client = new SecClient(http, reopened, Contact, rig.Limits, rig.Clock, new SecRequestGate(rig.Clock));
        var resumed = await new SecCollector(client, reopened, rig.Limits, rig.Clock).Collect("1", new(2023, 1, 1), new(2024, 3, 4));
        Assert.Equal(0, resumed.Requests);
        Assert.Equal<CollectedSecFiling>(first.Filings, resumed.Filings);
    }

    [Fact]
    public void NonPeriodicRowsMayHaveBlankOrFutureReportDatesButPeriodicReportsMustAlreadyHaveEnded()
    {
        // Genuine Apple submissions carry blank report dates (e.g. Form 144) and DEF 14A proxies whose
        // reportDate is the future shareholder-meeting date. Both must catalogue; malformed values must not.
        var bytes = Change(root =>
        {
            var recent = root["filings"]!["recent"]!.AsObject();
            void Append(string accession, string form, string filing, string report, string accepted, string document)
            {
                recent["accessionNumber"]!.AsArray().Add(accession);
                recent["form"]!.AsArray().Add(form);
                recent["filingDate"]!.AsArray().Add(filing);
                recent["reportDate"]!.AsArray().Add(report);
                recent["acceptanceDateTime"]!.AsArray().Add(accepted);
                recent["primaryDocument"]!.AsArray().Add(document);
                recent["unrecognisedUpstreamField"]!.AsArray().Add("retained in original");
            }
            Append("0000000001-24-000003", "144", "2024-02-05", "", "2024-02-05T17:00:00Z", "notice.htm");
            Append("0000000001-24-000004", "DEF 14A", "2024-01-11", "2024-02-28", "2024-01-11T17:00:00Z", "proxy.htm");
        });
        var page = SecParser.Parse(bytes, "1", false, Now);
        Assert.Equal(4, page.Filings.Length);
        Assert.Null(Assert.Single(page.Filings, f => f.Form == "144").ReportDate);
        Assert.Equal(new DateOnly(2024, 2, 28), Assert.Single(page.Filings, f => f.Form == "DEF 14A").ReportDate);

        var futurePeriodic = Change(root => root["filings"]!["recent"]!["reportDate"]![1] = "2024-02-02");
        Assert.Equal("invalid-report-period", Assert.Throws<SecCollectionException>(() =>
            SecParser.Parse(futurePeriodic, "1", false, Now)).Code);
        var malformed = Change(root => root["filings"]!["recent"]!["reportDate"]![0] = "31-12-2023");
        Assert.Equal("invalid-filing-date", Assert.Throws<SecCollectionException>(() =>
            SecParser.Parse(malformed, "1", false, Now)).Code);
    }

    [Fact]
    public void BlankPrimaryDocumentsCatalogueButInvalidOnesStillFail()
    {
        // Genuine ASML/AAPL payloads carry blank primaryDocument on paper-era rows
        // (1995 SC 13D, pre-2001 10-Q). They catalogue; only fetch selection needs a path.
        var bytes = Change(root =>
        {
            var recent = root["filings"]!["recent"]!.AsObject();
            void Append(string accession, string form, string filing, string report, string accepted, string? document)
            {
                recent["accessionNumber"]!.AsArray().Add(accession);
                recent["form"]!.AsArray().Add(form);
                recent["filingDate"]!.AsArray().Add(filing);
                recent["reportDate"]!.AsArray().Add(report);
                recent["acceptanceDateTime"]!.AsArray().Add(accepted);
                recent["primaryDocument"]!.AsArray().Add(document);
                recent["unrecognisedUpstreamField"]!.AsArray().Add("retained in original");
            }
            Append("0000000001-95-000005", "SC 13D", "1995-05-19", "", "1995-05-19T17:00:00Z", "");
            Append("0000000001-00-000006", "10-Q", "2000-05-11", "2000-03-31", "2000-05-11T17:00:00Z", null);
            // Genuine filer naming with a double dot before the extension is not traversal.
            Append("0000000001-14-000007", "SC 13G/A", "2014-10-08", "", "2014-10-08T17:00:00Z", "synthetic.inc..txt");
        });
        var page = SecParser.Parse(bytes, "1", false, Now);
        Assert.Equal(5, page.Filings.Length);
        Assert.Equal("synthetic.inc..txt", Assert.Single(page.Filings, f => f.Form == "SC 13G/A").PrimaryDocument);
        Assert.Equal("", Assert.Single(page.Filings, f => f.Form == "SC 13D").PrimaryDocument);
        Assert.Equal("", Assert.Single(page.Filings, f => f.FilingDate == new DateOnly(2000, 5, 11)).PrimaryDocument);

        var traversal = Change(root => root["filings"]!["recent"]!["primaryDocument"]![0] = "../evil.htm");
        Assert.Equal("invalid-document-name", Assert.Throws<SecCollectionException>(() =>
            SecParser.Parse(traversal, "1", false, Now)).Code);
    }

    [Fact]
    public async Task SelectedFilingWithoutDocumentPathFailsBeforeAnyDocumentFetch()
    {
        var changed = Change(root =>
        {
            var documents = root["filings"]!["recent"]!["primaryDocument"]!.AsArray();
            for (var i = 0; i < documents.Count; i++) documents[i] = "";
        });
        using var rig = new Rig(request => request.RequestUri!.Host == "www.sec.gov"
            ? Response(Encoding.UTF8.GetBytes("<html>doc</html>"), "text/html")
            : Response(request.RequestUri.AbsolutePath.Contains("-submissions-", StringComparison.Ordinal) ? Fixture("history") : changed));
        var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
        Assert.Equal("document-path-missing", error.Code);
        Assert.DoesNotContain(rig.Requests, r => r.Uri.Host == "www.sec.gov");
    }

    [Fact]
    public async Task StaleCacheUsesConditionalGetAnd304PreservesFirstSeen()
    {
        var calls = 0;
        using var rig = new Rig(request =>
        {
            calls++;
            if (calls == 1) return Response(Fixture());
            Assert.Equal("\"fixture-v1\"", request.Headers.IfNoneMatch.Single().ToString());
            return Response([], code: HttpStatusCode.NotModified);
        }, new SecLimits { CacheLifetime = TimeSpan.Zero });
        var uri = SecIdentity.Submissions("1");
        var first = await rig.Client.Fetch(uri, true, Validate, default);
        rig.Clock.Advance(TimeSpan.FromDays(1));
        var second = await rig.Client.Fetch(uri, true, Validate, default);
        Assert.Equal(first.Receipt, second.Receipt);
        Assert.Equal(2, calls);
        Assert.Equal(rig.Clock.UtcNow, rig.Archive.Cached(uri)!.CheckedAt);
        static void Validate(byte[] b, string _, DateTimeOffset at) { SecParser.Parse(b, "1", false, at); }
    }

    [Fact]
    public async Task ChangedPayloadRetainsBothOriginalVersionsAndNewFirstSeen()
    {
        using var rig = new Rig();
        var uri = SecIdentity.Submissions("1");
        var first = rig.Archive.Store(uri, Fixture(), "application/json", null, null, Now);
        var changed = Change(x => x["name"] = "SYNTHETIC CHANGED NAME");
        var second = rig.Archive.Store(uri, changed, "application/json", null, null, Now.AddDays(1));
        Assert.NotEqual(first.ContentSha256, second.ContentSha256);
        Assert.Equal(Fixture(), rig.Archive.Bytes(first));
        Assert.Equal(changed, rig.Archive.Bytes(second));
        Assert.Equal(Now.AddDays(1), second.FirstSeenAt);
        Assert.Equal(second, rig.Archive.Cached(uri)!.Receipt);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CorruptCachedOriginalFailsBeforeNetwork()
    {
        using var rig = new Rig();
        var result = await rig.Collect();
        var receipt = result.Filings[0].DocumentSource;
        File.WriteAllText(Path.Combine(rig.Root, "sec-v1", "objects", receipt.ContentSha256 + ".bin"), "tampered");
        var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
        Assert.Equal("archive-content-integrity", error.Code);
        Assert.Equal(5, rig.Requests.Count);
    }

    [Fact]
    public async Task MalformedArraysAreQuarantinedWithoutEnteringCacheOrCatalogue()
    {
        var invalid = Change(x => x["filings"]!["recent"]!["form"] = new JsonArray("10-K"));
        using var rig = new Rig(_ => Response(invalid));
        var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
        Assert.Equal("sec-array-shape", error.Code);
        Assert.Null(rig.Archive.Cached(SecIdentity.Submissions("1")));
        var path = Assert.Single(Directory.GetFiles(Path.Combine(rig.Root, "sec-v1", "quarantine")));
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(invalid, evidence.RootElement.GetProperty("original").GetBytesFromBase64());
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, "sec-v1", "catalogues")));
    }

    [Fact]
    public void QuarantinePrunesOldEntriesAndHasByteAndCountBounds()
    {
        using var rig = new Rig(limits: new SecLimits { MaximumQuarantineEntries = 2, MaximumQuarantineBytes = 2048 });
        var uri = SecIdentity.Submissions("1");
        for (var i = 0; i < 4; i++) rig.Archive.Quarantine(uri, Encoding.UTF8.GetBytes("bad" + i), "sec-json-shape", Now.AddSeconds(i));
        var files = Directory.GetFiles(Path.Combine(rig.Root, "sec-v1", "quarantine"));
        Assert.Equal(2, files.Length);
        Assert.True(files.Sum(x => new FileInfo(x).Length) <= 2048);
        Assert.Throws<SecCollectionException>(() => rig.Archive.Quarantine(uri, new byte[2048], "sec-json-shape", Now));
    }

    [Fact]
    public async Task RateLimitedReadHonoursRetryAfterAndGlobalSpacing()
    {
        var calls = 0;
        using var rig = new Rig(_ =>
        {
            if (++calls > 1) return Response(Fixture());
            var response = Response([], code: HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });
        await rig.Client.Fetch(SecIdentity.Submissions("1"), true, (_, _, _) => { }, default);
        Assert.Equal(2, rig.Client.Requests);
        Assert.Equal(TimeSpan.FromSeconds(7), rig.Requests[1].At - rig.Requests[0].At);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "sec-access-denied", 1)]
    [InlineData(HttpStatusCode.Redirect, "sec-http-status", 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "sec-retries-exhausted", 3)]
    public async Task DenialsRedirectsAndTransientFailuresHaveBoundedHandling(HttpStatusCode status, string code, int count)
    {
        using var rig = new Rig(_ => Response([], code: status));
        var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
        Assert.Equal(code, error.Code); Assert.Equal(count, rig.Requests.Count);
    }

    [Fact]
    public async Task LongRetryAfterStopsWithoutRetryingEarly()
    {
        using var rig = new Rig(_ =>
        {
            var response = Response([], code: HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3)); return response;
        });
        var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
        Assert.Equal("sec-retry-after-limit", error.Code); Assert.Single(rig.Requests);
    }

    [Fact]
    public async Task RequestBudgetStopsTraversalAndKeepsCompletedOriginals()
    {
        using var rig = new Rig(limits: new SecLimits { MaximumRequests = 2 });
        var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
        Assert.Equal("request-budget-exhausted", error.Code); Assert.Equal(2, rig.Requests.Count);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(rig.Root, "sec-v1", "objects")).Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, "sec-v1", "catalogues")));
    }

    [Fact]
    public async Task HistoryAndDocumentBoundsAreReportedRatherThanClaimingCompleteness()
    {
        using var rig = new Rig(limits: new SecLimits { MaximumHistoryFiles = 0, MaximumDocuments = 1 });
        var result = await rig.Collect();
        Assert.True(result.HistoryLimitReached); Assert.True(result.DocumentLimitReached);
        Assert.Single(result.Filings); Assert.Equal(2, rig.Requests.Count);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeArchiving()
    {
        using var rig = new Rig(_ => Response(new byte[2048]), new SecLimits { MaximumResponseBytes = 1024 });
        var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
        Assert.Equal("response-size-limit", error.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, "sec-v1", "objects")));
    }

    [Fact]
    public void ArchiveBudgetDoesNotEvictOriginalsAndConcurrentWriterIsRefused()
    {
        using var rig = new Rig(limits: new SecLimits { MaximumArchiveBytes = 1024 });
        Assert.Throws<IOException>(() => new SecArchive(rig.Root, rig.Limits));
        var error = Assert.Throws<SecCollectionException>(() => rig.Archive.Store(SecIdentity.Submissions("1"), new byte[2048], "application/json", null, null, Now));
        Assert.Equal("archive-size-limit", error.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, "sec-v1", "objects")));
    }

    [Fact]
    public async Task CancellationPreventsRequests()
    {
        using var rig = new Rig();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Client.Fetch(SecIdentity.Submissions("1"), true, (_, _, _) => { }, cancelled.Token));
        Assert.Empty(rig.Requests);
    }

    [Fact]
    public async Task CancellationDuringHttpDoesNotRetryOrPublishPartialData()
    {
        using var rig = new Rig();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        }));
        var client = new SecClient(http, rig.Archive, Contact, rig.Limits, rig.Clock, new SecRequestGate(rig.Clock));
        using var cancelled = new CancellationTokenSource();
        var task = client.Fetch(SecIdentity.Submissions("1"), true, (_, _, _) => { }, cancelled.Token);
        await started.Task;
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, client.Requests);
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, "sec-v1", "objects")));
    }

    [Fact]
    public async Task CollectedFilingCanJoinVerifiedDomainContextWithoutInventingReportDates()
    {
        using var rig = new Rig();
        var item = (await rig.Collect()).Filings[0];
        var file = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
        var joined = file with
        {
            Manifest = file.Manifest with { Sources = file.Manifest.Sources.Add(item.DocumentProvenance()) },
            Observations = file.Observations.Add(item.ToDomain("company-a"))
        };
        Assert.True(DatasetValidator.Validate(joined).IsValid);
        var unknown = item with { Metadata = item.Metadata with { ReportDate = null } };
        Assert.Equal("report-period-required", Assert.Throws<SecCollectionException>(() => unknown.ToDomain("company-a")).Code);
    }

    [Fact]
    public async Task LaterMetadataCannotBorrowEarlierDocumentAvailabilityForDomainImport()
    {
        using var rig = new Rig();
        var item = (await rig.Collect()).Filings[0];
        var later = item with { MetadataSource = item.MetadataSource with { FirstSeenAt = item.DocumentSource.FirstSeenAt.AddDays(1) } };
        Assert.Equal(later.MetadataSource.FirstSeenAt, later.AvailableAt);
        var error = Assert.Throws<SecCollectionException>(() => later.ToDomain("verified-issuer-id"));
        Assert.Equal("composite-provenance-required", error.Code);
        Assert.Throws<SecCollectionException>(() => later.DocumentProvenance());
    }

    [Fact]
    public async Task StreamedResponseWithoutContentLengthStillHasSizeBound()
    {
        using var rig = new Rig(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new UnknownLengthStream(new byte[2048]))
            };
            response.Content.Headers.ContentType = new("application/json");
            return response;
        }, new SecLimits { MaximumResponseBytes = 1024 });
        var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
        Assert.Equal("response-size-limit", error.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, "sec-v1", "objects")));
    }
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Theory]
    [InlineData("wrong-type", "sec-json-content-type")]
    [InlineData("block-page", "sec-block-page")]
    [InlineData("unexpected-304", "unexpected-not-modified")]
    public async Task UnexpectedSuccessfulResponsesCannotMasqueradeAsData(string scenario, string code)
    {
        using var rig = new Rig(_ => scenario switch
        {
            "wrong-type" => Response(Fixture(), "text/html"),
            "block-page" => Response(Encoding.UTF8.GetBytes("<html>Undeclared Automated Tool</html>"), "text/html"),
            _ => Response([], code: HttpStatusCode.NotModified)
        });
        var error = scenario == "block-page"
            ? await Assert.ThrowsAsync<SecCollectionException>(() => rig.Client.Fetch(
                SecIdentity.PrimaryDocument("1", "0000000001-24-000001", "annual.htm"), false,
                (b, type, _) => SecParser.ValidateDocument(b, type), default))
            : await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
        Assert.Equal(code, error.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, "sec-v1", "objects")));
    }

    [Fact]
    public async Task CorruptCacheTimestampCannotBackdateAvailability()
    {
        using var rig = new Rig();
        await rig.Collect();
        var path = Directory.GetFiles(Path.Combine(rig.Root, "sec-v1", "cache"))[0];
        var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        json["receipt"]!["firstSeenAt"] = Now.AddDays(-1).ToString("O");
        File.WriteAllText(path, json.ToJsonString());
        var error = Assert.Throws<SecCollectionException>(() =>
            rig.Archive.Cached(new Uri(json["receipt"]!["url"]!.GetValue<string>())));
        Assert.Equal("archive-receipt-integrity", error.Code);
    }

    [Fact]
    public async Task TransportFailuresStopAfterThreeAttemptsWithoutEchoingExceptionDetails()
    {
        using var rig = new Rig(_ => throw new HttpRequestException("untrusted response and private headers"));
        var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
        Assert.Equal("sec-transport-exhausted", error.Code);
        Assert.Equal(3, rig.Requests.Count);
        Assert.DoesNotContain("private headers", error.Message);
    }

    [Fact]
    public async Task SharedGateSerialisesClientsAcrossOriginsAndSpacesRequestStarts()
    {
        using var a = new Rig(); using var b = new Rig();
        var gate = new SecRequestGate(a.Clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var times = new List<DateTimeOffset>();
        var calls = 0;
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            times.Add(a.Clock.UtcNow);
            if (Interlocked.Increment(ref calls) == 1) { started.SetResult(); await release.Task.WaitAsync(ct); }
            return Response(Fixture());
        }));
        var first = new SecClient(http, a.Archive, Contact, a.Limits, a.Clock, gate)
            .Fetch(SecIdentity.Submissions("1"), true, (_, _, _) => { }, default);
        await started.Task;
        var second = new SecClient(http, b.Archive, Contact, b.Limits, a.Clock, gate)
            .Fetch(SecIdentity.PrimaryDocument("1", "0000000001-24-000001", "annual.htm"), false, (_, _, _) => { }, default);
        Assert.Equal(1, calls); Assert.False(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, calls);
        Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task ConflictingAccessionAcrossHistoryPagesStopsBeforeDocuments()
    {
        using var rig = new Rig(request =>
        {
            if (!request.RequestUri!.AbsolutePath.Contains("-submissions-", StringComparison.Ordinal)) return Response(Fixture());
            var history = JsonNode.Parse(Fixture("history"))!;
            history["accessionNumber"]![0] = "0000000001-24-000001";
            return Response(Encoding.UTF8.GetBytes(history.ToJsonString()));
        });
        var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
        Assert.Equal("conflicting-accession", error.Code);
        Assert.Equal(2, rig.Requests.Count);
        Assert.Single(Directory.GetFiles(Path.Combine(rig.Root, "sec-v1", "quarantine")));
    }

    [Fact]
    public async Task DeclaredHistoryCoverageMustMatchDownloadedRows()
    {
        var invalid = Change(x => x["filings"]!["files"]![0]!["filingCount"] = 10);
        using var rig = new Rig(request => Response(request.RequestUri!.AbsolutePath.Contains("-submissions-", StringComparison.Ordinal) ? Fixture("history") : invalid));
        var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
        Assert.Equal("history-range-mismatch", error.Code);
    }

    [Fact]
    public async Task OneDayHistoryRangeSpillIsToleratedTwoDaysIsNot()
    {
        // Genuine AAPL submissions-001 claims an end of 2015-09-16 but holds rows dated
        // 2015-09-17, with the declared count exact. One day of spill is SEC metadata
        // imprecision; larger spill remains a mismatch.
        static byte[] HistoryOn(string date)
        {
            var json = JsonNode.Parse(Fixture("history"))!;
            json["filingDate"]![0] = date;
            return Encoding.UTF8.GetBytes(json.ToJsonString());
        }
        using (var rig = new Rig(request => request.RequestUri!.Host == "www.sec.gov"
            ? Response(Encoding.UTF8.GetBytes("<html>doc</html>"), "text/html")
            : Response(request.RequestUri.AbsolutePath.Contains("-submissions-", StringComparison.Ordinal) ? HistoryOn("2023-02-02") : Fixture())))
        {
            var result = await rig.Collect();
            Assert.Equal(3, result.Filings.Length);
        }
        using (var rig = new Rig(request => request.RequestUri!.Host == "www.sec.gov"
            ? Response(Encoding.UTF8.GetBytes("<html>doc</html>"), "text/html")
            : Response(request.RequestUri.AbsolutePath.Contains("-submissions-", StringComparison.Ordinal) ? HistoryOn("2023-02-03") : Fixture())))
        {
            var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Collect());
            Assert.Equal("history-range-mismatch", error.Code);
        }
    }

    [Fact]
    public async Task NonTargetForm4WithRelativeXslPathDoesNotBlockAnnualReports()
    {
        var page = Change(x =>
        {
            var rows = x["filings"]!["recent"]!;
            rows["form"]![0] = "4";
            rows["primaryDocument"]![0] = "xslF345X05/ownership.xml";
        });
        using var rig = new Rig(request => request.RequestUri!.Host == "www.sec.gov"
            ? Response(Encoding.UTF8.GetBytes("<html>AUTHORED SYNTHETIC ANNUAL REPORT</html>"), "text/html")
            : Response(request.RequestUri.AbsolutePath.Contains("-submissions-", StringComparison.Ordinal) ? Fixture("history") : page));
        var result = await rig.Collect();
        Assert.Equal(2, result.Filings.Length);
        Assert.All(result.Filings, x => Assert.Equal("10-K", x.Metadata.Form));
        Assert.DoesNotContain(rig.Requests, x => x.Uri.AbsolutePath.Contains("xslF345X05", StringComparison.Ordinal));
        Assert.Equal("https://www.sec.gov/Archives/edgar/data/1/000000000124000001/xslF345X05/ownership.xml",
            SecIdentity.PrimaryDocument("1", "0000000001-24-000001", "xslF345X05/ownership.xml").AbsoluteUri);
    }

    [Theory]
    [InlineData("../report.htm")]
    [InlineData("xsl/../../report.htm")]
    [InlineData("xsl//report.htm")]
    [InlineData("/report.htm")]
    [InlineData("https://evil.invalid/report.htm")]
    [InlineData("xsl/%2e%2e/report.htm")]
    [InlineData("xsl/report.htm?token=redacted")]
    [InlineData("xsl\\report.htm")]
    public void RelativeDocumentPathsStillRejectEscapesAndAmbiguousNames(string document) =>
        Assert.Throws<SecCollectionException>(() => SecIdentity.PrimaryDocument("1", "0000000001-24-000001", document));

    [Theory]
    [InlineData("mismatch")]
    [InlineData("traversal")]
    [InlineData("unzoned")]
    [InlineData("future")]
    [InlineData("conflict")]
    public void UnsafeIdentityTimingAndConflictingAccessionsFail(string mutation)
    {
        var bytes = Change(x =>
        {
            var recent = x["filings"]!["recent"]!;
            switch (mutation)
            {
                case "mismatch": x["cik"] = "2"; break;
                case "traversal": x["filings"]!["files"]![0]!["name"] = "../../evil.json"; break;
                case "unzoned": recent["acceptanceDateTime"]![0] = "2024-03-02T17:00:00"; break;
                case "future": recent["acceptanceDateTime"]![0] = "2025-03-02T17:00:00Z"; break;
                case "conflict": recent["accessionNumber"]![1] = recent["accessionNumber"]![0]!.GetValue<string>(); break;
            }
        });
        Assert.Throws<SecCollectionException>(() => SecParser.Parse(bytes, "1", false, Now));
    }

    [Theory]
    [InlineData("https://evil.example/submissions/CIK0000000001.json")]
    [InlineData("http://data.sec.gov/submissions/CIK0000000001.json")]
    [InlineData("https://data.sec.gov/submissions/CIK0000000001.json?secret=redacted")]
    [InlineData("https://data.sec.gov:8443/submissions/CIK0000000001.json")]
    public async Task OffOriginOrQueryRequestsFailBeforeHttp(string url)
    {
        using var rig = new Rig();
        var error = await Assert.ThrowsAsync<SecCollectionException>(() => rig.Client.Fetch(new(url), true, (_, _, _) => { }, default));
        Assert.Equal("off-origin-request", error.Code); Assert.Empty(rig.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("fake@example.com")]
    [InlineData("Name <contact@sec.gov>")]
    [InlineData("contact@sec.gov\r\nX-Injected: yes")]
    public void ContactMustBeExplicitAndCannotInjectHeaders(string value) =>
        Assert.Throws<SecCollectionException>(() => SecIdentity.UserAgent(value));
}
