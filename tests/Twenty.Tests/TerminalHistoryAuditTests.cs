using System.Text;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Listings;

namespace Twenty.Tests;

/// <summary>Authored histories and documents; no genuine prices or accelerator inputs.</summary>
public sealed class TerminalHistoryAuditTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twenty-terminal-audit-" + Guid.NewGuid().ToString("N"));
    private readonly CaptureStore store;
    private static readonly byte[] Source = Encoding.UTF8.GetBytes("Authored notice: last trading June 7; merger June 10; suspension June 11; USD 12 cash per share.");
    private static DateOnly Day(int day) => new(2024, 6, day);
    private static TerminalSubject Subject() => new("AAA", "completed-merger", Day(10), Day(7), Day(11),
        new("cash-only", 12m, "USD", null, null, null), ["notice"]);
    private static TerminalReferences Reference(TerminalSubject? subject = null) => new("terminal-reference.v1",
        [new("notice", "https://exchange.example/notice", new(2024, 6, 12, 0, 0, 0, TimeSpan.Zero), CaptureStore.Hash(Source), "original-response")],
        [subject ?? Subject()]);
    private static byte[] History(params int[] days) => JsonSerializer.SerializeToUtf8Bytes(days.Select(day => new
    {
        date = $"{Day(1).AddDays(day - 1):yyyy-MM-dd}T00:00:00Z", open = 11m, high = 11m, low = 11m, close = 11m, volume = 100m,
        adjOpen = 11m, adjHigh = 11m, adjLow = 11m, adjClose = 11m, adjVolume = 100m, divCash = 0m, splitFactor = 1m
    }));
    private static CaptureObservation Capture(byte[] bytes, int end = 12) => new("0123456789abcdef0123456789abcdef", "tiingo-history-aaa",
        $"https://api.tiingo.com/tiingo/daily/AAA/prices?startDate=2024-06-01&endDate=2024-06-{end:00}", CaptureStore.Hash(bytes), bytes.Length,
        "application/json", new(2024, 6, 13, 0, 0, 0, TimeSpan.Zero), "authored-fixture", false);
    public TerminalHistoryAuditTests() => store = new(root);
    public void Dispose() { store.Dispose(); Directory.Delete(root, true); }
    private TerminalHistoryReport Run(TerminalSubject? subject = null, Func<string, byte[]>? bytes = null) =>
        TerminalHistoryAudit.Build(store, JsonSerializer.SerializeToUtf8Bytes(Reference(subject), DatasetJson.Options), bytes ?? (_ => Source));
    private void Record(int[]? days = null, int end = 12)
    {
        var bytes = History(days ?? [3, 4, 5, 6, 7]); store.Record(Capture(bytes, end), bytes, false);
    }

    [Fact]
    public void ExactTerminalDateComparisonRetainsEntitlementInsteadOfUsingLastClose()
    {
        Record(); var report = Run(); var result = Assert.Single(report.Subjects);
        Assert.True(report.Valid); Assert.True(result.TerminalDateChecked);
        Assert.Equal(Day(7), result.LastBar);
        Assert.Equal(12m, result.Reference.Consideration.CashPerShare); // authored close is 11, never used as proceeds
        Assert.Equal(Day(10), result.Reference.EventDate); Assert.Equal(Day(11), result.Reference.SuspensionDate);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(report, DatasetJson.Options), JsonSerializer.SerializeToUtf8Bytes(Run(), DatasetJson.Options));
    }

    [Fact]
    public void PostTradingRowsAreFlaggedEvenOnMergerOrSuspensionDate()
    {
        Record([6, 7, 10, 11]); var report = Run(); var result = Assert.Single(report.Subjects);
        Assert.False(report.Valid); Assert.False(result.TerminalDateChecked);
        Assert.Equal(2, result.PostLastTradingBars); Assert.Equal(new[] { Day(10), Day(11) }, result.PostLastTradingDates);
        Assert.Contains("post-last-trading-bars", result.Issues);
    }

    [Fact]
    public void DetailCapDoesNotHidePostTradingTotals()
    {
        var bytes = History(Enumerable.Range(6, 120).ToArray());
        store.Record(Capture(bytes) with { Url = "https://api.tiingo.com/tiingo/daily/AAA/prices?startDate=2024-06-01&endDate=2024-12-31",
            ObservedAt = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) }, bytes, false);
        var result = Run().Subjects[0];
        Assert.Equal(118, result.PostLastTradingBars); Assert.Equal(100, result.PostLastTradingDates.Length);
        Assert.Contains("post-last-trading-bars", result.Issues);
    }

    [Fact]
    public void ShortRequestWindowIsNotReportedAsMissingVendorTerminalHistory()
    {
        Record([3, 4, 5], end: 5); var result = Assert.Single(Run().Subjects);
        Assert.Contains("request-does-not-reach-event", result.Issues);
        Assert.Contains("request-does-not-cover-last-trading-date", result.Issues);
        Assert.DoesNotContain("last-trading-bar-missing", result.Issues);
    }

    [Fact]
    public void MissingLastTradingSessionAndUnreviewedLastTradingDateRemainDistinct()
    {
        Record([3, 4, 5, 6]);
        Assert.Contains("last-trading-bar-missing", Assert.Single(Run().Subjects).Issues);
        var unreviewed = Assert.Single(Run(Subject() with { LastTradingDate = null }).Subjects);
        Assert.Contains("last-trading-date-unverified", unreviewed.Issues);
        Assert.DoesNotContain("last-trading-bar-missing", unreviewed.Issues);
        Assert.False(unreviewed.TerminalDateChecked);
    }

    [Fact]
    public void LaterListingNoticeCannotBeTreatedAsATerminalEvent()
    {
        Record(); var subject = Subject() with { EventKind = "continued-listing-notice", LastTradingDate = null,
            SuspensionDate = null, Consideration = new("none", null, null, null, null, null) };
        var report = Run(subject);
        Assert.False(report.Valid); Assert.False(Assert.Single(report.Subjects).TerminalDateChecked);
        Assert.Contains("not-a-terminal-event", report.Subjects[0].Issues);
    }

    [Theory]
    [InlineData("cash-and-cvr")]
    [InlineData("cash-and-stock")]
    public void MixedConsiderationRemainsUnsupportedEvenWithCleanDates(string kind)
    {
        Record(); var c = kind == "cash-and-cvr" ? new TerminalConsideration(kind, 12m, "USD", null, null, 1m)
            : new(kind, 0.09m, "USD", "BBB", 0.3619m, null);
        var report = Run(Subject() with { Consideration = c });
        Assert.False(report.Valid); Assert.True(report.Subjects[0].TerminalDateChecked);
        Assert.Contains("consideration-not-supported", report.Subjects[0].Issues);
    }

    [Fact]
    public void RowsOutsideRequestOrAfterCaptureCannotPass()
    {
        Record([6, 7, 14], end: 7);
        Assert.Contains("history-outside-request-or-capture-date", Run().Subjects[0].Issues);
        Assert.False(Run().Subjects[0].TerminalDateChecked);
    }

    [Fact]
    public void EmptyHistoryAndCorruptSourceFail()
    {
        Record([]); Assert.Contains("history-empty", Run().Subjects[0].Issues);
        Assert.Equal("terminal-source-integrity", Assert.Throws<ResearchException>(() => Run(bytes: _ => [1, 2])).Code);
    }

    [Fact]
    public void MissingHistoryIsNotASuccess()
    {
        Assert.Equal("terminal-history-not-captured", Assert.Throws<ResearchException>(() => Run()).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameInstantConflictsIncludeIdenticalPayloadWithDifferentRequestWindow(bool changePayload)
    {
        Record(); var bytes = History(changePayload ? [6, 7] : [3, 4, 5, 6, 7]);
        store.Record(Capture(bytes, changePayload ? 12 : 11) with { Id = "1123456789abcdef0123456789abcdef" }, bytes, false);
        Assert.Equal("terminal-history-capture-conflict", Assert.Throws<ResearchException>(() => Run()).Code);
    }

    [Fact]
    public void IdenticalRetransmissionsUseDeterministicCaptureId()
    {
        Record(); var bytes = History(3, 4, 5, 6, 7);
        store.Record(Capture(bytes) with { Id = "1123456789abcdef0123456789abcdef" }, bytes, false);
        Assert.Equal("0123456789abcdef0123456789abcdef", Run().Subjects[0].HistoryCaptureId);
    }

    [Theory]
    [InlineData("https://api.tiingo.com/tiingo/daily/BBB/prices?startDate=2024-06-01&endDate=2024-06-12")]
    [InlineData("https://api.tiingo.com/tiingo/daily/AAA/prices?startDate=2024-06-01&endDate=2024-06-12&endDate=2024-06-30")]
    [InlineData("https://other.example/tiingo/daily/AAA/prices?startDate=2024-06-01&endDate=2024-06-12")]
    [InlineData("https://api.tiingo.com/tiingo/daily/AAA/prices?startDate=bad&endDate=2024-06-12")]
    public void MismatchedOrAmbiguousRequestIdentityIsRefused(string url)
    {
        var bytes = History(6, 7); store.Record(Capture(bytes) with { Url = url }, bytes, false);
        Assert.Equal("terminal-history-request", Assert.Throws<ResearchException>(() => Run()).Code);
    }

    [Fact]
    public void CaptureMetadataSizeMustMatchPayload()
    {
        Record(); var bytes = History(3, 4, 5, 6, 7);
        store.Record(Capture(bytes) with { Id = "1123456789abcdef0123456789abcdef", Bytes = bytes.Length + 1,
            ObservedAt = new(2024, 6, 14, 0, 0, 0, TimeSpan.Zero) }, null, false);
        Assert.Equal("terminal-history-integrity", Assert.Throws<ResearchException>(() => Run()).Code);
    }

    [Fact]
    public void InconsistentDatesAndIncompleteConsiderationAreRefused()
    {
        Record();
        foreach (var subject in new[] { Subject() with { LastTradingDate = Day(12) }, Subject() with { SuspensionDate = Day(7) },
            Subject() with { Consideration = new("cash-only", null, null, null, null, null) },
            Subject() with { Consideration = new("cash-only", 12m, "USD", null, null, 1m) }, Subject() with { SourceIds = ["unknown"] } })
            Assert.Equal("terminal-reference-subject", Assert.Throws<ResearchException>(() => Run(subject)).Code);
    }

    [Fact]
    public void RepeatedSubjectsAndUnsupportedSourceRepresentationsAreRefused()
    {
        Record(); var refs = Reference();
        Assert.Equal("terminal-reference-subject", Assert.Throws<ResearchException>(() => TerminalHistoryAudit.Build(store,
            JsonSerializer.SerializeToUtf8Bytes(refs with { Subjects = refs.Subjects.Add(refs.Subjects[0]) }, DatasetJson.Options), _ => Source)).Code);
        Assert.Equal("terminal-reference-shape", Assert.Throws<ResearchException>(() => TerminalHistoryAudit.Build(store,
            JsonSerializer.SerializeToUtf8Bytes(refs with { Sources = [refs.Sources[0] with { Representation = "unknown" }] }, DatasetJson.Options), _ => Source)).Code);
    }
}
