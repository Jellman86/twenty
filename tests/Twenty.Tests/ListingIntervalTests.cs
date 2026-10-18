using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Listings;

namespace Twenty.Tests;

public sealed class ListingIntervalTests
{
    private const string ListedHeader = "Symbol|Security Name|Market Category|Test Issue|Financial Status|Round Lot Size|ETF|NextShares\n";
    private const string OtherHeader = "ACT Symbol|Security Name|Exchange|CQS Symbol|ETF|Round Lot Size|Test Issue|NASDAQ Symbol\n";
    private const string Trailer = "File Creation Time: 1016202607:00|||||||\n";
    private static readonly DateTimeOffset Day0 = new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);

    private static string ListedFile(params string[] symbols)
    {
        var file = new StringBuilder(ListedHeader);
        foreach (var symbol in symbols) file.Append(symbol).Append("|Authored, Inc. - Common Stock|Q|N|N|100|N|N\n");
        // The capture validator requires a directory-sized file; filler rows are authored test symbols.
        for (var i = 0; i < 120; i++) file.Append($"F{i:D3}|Filler Security {i}|Q|N|N|100|N|N\n");
        return file.Append(Trailer).ToString();
    }
    private static byte[] TiingoZip(string csv, string entry = "supported_tickers.csv")
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write(csv);
        return output.ToArray();
    }
    private static ListingSnapshot Snap(params ListingSnapshotRow[] rows) => new([.. rows], 0, null);
    private static ListingSnapshotRow Row(string symbol, string type = "stock", string? name = null, DateOnly? end = null) =>
        new(symbol, "NASDAQ", type, name ?? symbol + " Inc.", "USD", new DateOnly(2020, 1, 2), end);

    [Fact]
    public void NasdaqListedParsesRowsExcludesTestIssuesAndKeepsProducerCreation()
    {
        var text = ListedHeader
            + "AAA|Authored, Inc. - Common Stock|Q|N|N|100|N|N\n"
            + "BBB|Authored Fund Trust|S|N|N|100|Y|N\n"
            + "ZZTEST|Authored Test Security|Q|Y|N|100|N|N\n" + Trailer;
        var snapshot = ListingIntervals.ParseNasdaqListed(Encoding.ASCII.GetBytes(text));
        Assert.Equal(2, snapshot.Rows.Length);
        Assert.Equal(1, snapshot.TestIssuesExcluded);
        Assert.Equal("1016202607:00", snapshot.ProducerCreation);
        Assert.Equal(("AAA", "NASDAQ", "stock"), (snapshot.Rows[0].Symbol, snapshot.Rows[0].Exchange, snapshot.Rows[0].AssetType));
        Assert.Equal("etf", snapshot.Rows[1].AssetType);
    }

    [Fact]
    public void NasdaqProducerNamePaddingIsTrimmedButSymbolsStayStrict()
    {
        // Live directories pad some security names with a trailing space; that padding is not identity.
        var text = ListedHeader + "AAA|Authored Corp Common Stock |Q|N|N|100|N|N\n" + Trailer;
        Assert.Equal("Authored Corp Common Stock", ListingIntervals.ParseNasdaqListed(Encoding.ASCII.GetBytes(text)).Rows[0].Name);
        var paddedSymbol = ListedHeader + "AAA |Authored Corp|Q|N|N|100|N|N\n" + Trailer;
        Assert.Throws<ResearchException>(() => ListingIntervals.ParseNasdaqListed(Encoding.ASCII.GetBytes(paddedSymbol)));
    }

    [Fact]
    public void NasdaqOtherKeepsTheRawExchangeCodeAndActSymbol()
    {
        var text = OtherHeader + "CCC|Authored Corp|N|CCC|N|100|N|CCC\n" + Trailer;
        var snapshot = ListingIntervals.ParseNasdaqOther(Encoding.ASCII.GetBytes(text));
        Assert.Equal(("CCC", "N"), (snapshot.Rows[0].Symbol, snapshot.Rows[0].Exchange));
    }

    [Theory]
    [InlineData("header")]
    [InlineData("trailer")]
    [InlineData("duplicate")]
    [InlineData("flag")]
    [InlineData("shape")]
    public void NasdaqDirectoryDefectsFailClosed(string kind)
    {
        var text = kind switch
        {
            "header" => "Symbol|Wrong\nAAA|X|Q|N|N|100|N|N\n" + Trailer,
            "trailer" => ListedHeader + "AAA|X|Q|N|N|100|N|N\n",
            "duplicate" => ListedHeader + "AAA|X|Q|N|N|100|N|N\nAAA|Y|Q|N|N|100|N|N\n" + Trailer,
            "flag" => ListedHeader + "AAA|X|Q|maybe|N|100|N|N\n" + Trailer,
            _ => ListedHeader + "AAA|X|Q|N\n" + Trailer
        };
        Assert.Throws<ResearchException>(() => ListingIntervals.ParseNasdaqListed(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void TiingoTickersParseProducerDatesDedupeExactRowsAndRejectInvertedDates()
    {
        var csv = "ticker,exchange,assetType,priceCurrency,startDate,endDate\n"
            + "AAA,NASDAQ,Stock,USD,2015-01-02,\nDDD,NYSE,Stock,USD,2020-01-02,2023-09-29\n";
        var snapshot = ListingIntervals.ParseTiingoTickers(TiingoZip(csv));
        Assert.Equal(2, snapshot.Rows.Length);
        Assert.Equal(new DateOnly(2015, 1, 2), snapshot.Rows[0].ProducerStart);
        Assert.Null(snapshot.Rows[0].ProducerEnd);
        Assert.Equal(new DateOnly(2023, 9, 29), snapshot.Rows[1].ProducerEnd);
        // The genuine file repeats a few rows byte-identically; those are deduped and counted, not fatal.
        var deduped = ListingIntervals.ParseTiingoTickers(TiingoZip(csv + "AAA,NASDAQ,Stock,USD,2015-01-02,\n"));
        Assert.Equal((2, 1), (deduped.Rows.Length, deduped.ExactDuplicatesDeduped));
        Assert.Throws<ResearchException>(() => ListingIntervals.ParseTiingoTickers(TiingoZip(
            "ticker,exchange,assetType,priceCurrency,startDate,endDate\nAAA,NASDAQ,Stock,USD,2024-01-02,2023-01-02\n")));
        Assert.Throws<ResearchException>(() => ListingIntervals.ParseTiingoTickers(TiingoZip(csv, "other.csv")));
        Assert.Throws<ResearchException>(() => ListingIntervals.ParseTiingoTickers(Encoding.ASCII.GetBytes("not a zip")));
    }

    [Fact]
    public void ReusedTickerSegmentsNeverMergeAndEndExtensionIsCoverageNotRevision()
    {
        // Real Tiingo shape: one ticker, two disjoint producer segments; the active segment's end advances daily.
        var oldSegment = new ListingSnapshotRow("AAA", "NASDAQ", "Stock", null, "USD", new DateOnly(2015, 11, 11), new DateOnly(2018, 2, 20));
        var newSegment = new ListingSnapshotRow("AAA", "NASDAQ", "ETF", null, "USD", new DateOnly(2026, 6, 23), new DateOnly(2026, 10, 15));
        var (intervals, _) = ListingIntervals.IntervalsFor("tiingo-tickers",
        [
            new(Day0, Snap(oldSegment, newSegment)),
            new(Day0.AddDays(1), Snap(oldSegment, newSegment with { ProducerEnd = new DateOnly(2026, 10, 16) }))
        ]);
        Assert.Equal(2, intervals.Length);
        Assert.Equal([new DateOnly(2015, 11, 11), new DateOnly(2026, 6, 23)], intervals.Select(i => i.ProducerStart));
        Assert.All(intervals, i => Assert.False(i.ProducerDatesChanged));
        Assert.Equal(new DateOnly(2026, 10, 16), intervals[1].ProducerEnd);
        var csv = "ticker,exchange,assetType,priceCurrency,startDate,endDate\n"
            + "AAA,NASDAQ,Stock,USD,2015-11-11,2018-02-20\nAAA,NASDAQ,ETF,USD,2026-06-23,\n";
        Assert.Equal(2, ListingIntervals.ParseTiingoTickers(TiingoZip(csv)).Rows.Length);
        // The genuine file also repeats one segment with a one-day end beside its continuing row.
        var repeated = "ticker,exchange,assetType,priceCurrency,startDate,endDate\n"
            + "BBB,NASDAQ,Stock,USD,2026-07-01,2026-07-01\nBBB,NASDAQ,Stock,USD,2026-07-01,2026-10-16\nBBB,NASDAQ,ETF,USD,2026-07-01,2026-07-01\n";
        var collapsed = ListingIntervals.ParseTiingoTickers(TiingoZip(repeated));
        Assert.Equal((2, 1), (collapsed.Rows.Length, collapsed.SegmentRowsCollapsed));
        Assert.Equal(new DateOnly(2026, 10, 16), collapsed.Rows.Single(r => r.AssetType == "Stock").ProducerEnd);
        Assert.Throws<ResearchException>(() => ListingIntervals.IntervalsFor("tiingo-tickers",
            [new(Day0, Snap(Row("AAA"), Row("AAA")))]));
    }

    [Fact]
    public void PresenceAcrossSnapshotsIsOneIntervalAndAbsenceClosesWithoutADelistingClaim()
    {
        var (intervals, gaps) = ListingIntervals.IntervalsFor("tiingo-tickers",
        [
            new(Day0, Snap(Row("AAA"), Row("BBB"))),
            new(Day0.AddDays(1), Snap(Row("AAA"), Row("BBB"))),
            new(Day0.AddDays(2), Snap(Row("AAA")))
        ]);
        Assert.Empty(gaps);
        Assert.Equal(2, intervals.Length);
        var aaa = intervals.Single(i => i.Symbol == "AAA");
        Assert.Equal((Day0, Day0.AddDays(2), 3, "present-at-final-snapshot"), (aaa.FirstSeen, aaa.LastSeen, aaa.SnapshotsPresent, aaa.Status));
        Assert.Null(aaa.ClosedAbsentAt);
        var bbb = intervals.Single(i => i.Symbol == "BBB");
        Assert.Equal((Day0.AddDays(1), "absent-from-later-snapshot"), (bbb.LastSeen, bbb.Status));
        Assert.Equal(Day0.AddDays(2), bbb.ClosedAbsentAt);
    }

    [Fact]
    public void ReappearanceStartsANewIntervalAndLongGapsBreakContinuity()
    {
        var (intervals, gaps) = ListingIntervals.IntervalsFor("tiingo-tickers",
        [
            new(Day0, Snap(Row("AAA"))),
            new(Day0.AddDays(1), Snap(Row("BBB"))),
            new(Day0.AddDays(2), Snap(Row("AAA"), Row("BBB"))),
            new(Day0.AddDays(20), Snap(Row("AAA"), Row("BBB")))
        ]);
        var gap = Assert.Single(gaps);
        Assert.Equal((Day0.AddDays(2), Day0.AddDays(20)), (gap.From, gap.To));
        var aaa = intervals.Where(i => i.Symbol == "AAA").ToArray();
        Assert.Equal(3, aaa.Length);
        Assert.Equal(["absent-from-later-snapshot", "continuity-gap", "present-at-final-snapshot"], aaa.Select(i => i.Status));
        Assert.Null(aaa[1].ClosedAbsentAt);
        Assert.Equal(Day0.AddDays(20), aaa[2].FirstSeen);
    }

    [Fact]
    public void RevisedKeyFieldsCloseAndOpenSegmentsAndOtherRevisionsAreFlaggedNotHidden()
    {
        var (intervals, _) = ListingIntervals.IntervalsFor("tiingo-tickers",
        [
            new(Day0, Snap(Row("AAA", name: "Old Name Inc."))),
            new(Day0.AddDays(1), Snap(Row("AAA", name: "New Name Inc.", end: new DateOnly(2026, 1, 1)))),
            new(Day0.AddDays(2), Snap(Row("AAA", type: "etf", name: "New Name Inc.")))
        ]);
        Assert.Equal(2, intervals.Length);
        Assert.Equal("absent-from-later-snapshot", intervals[0].Status);
        Assert.Equal(Day0.AddDays(2), intervals[0].ClosedAbsentAt);
        Assert.True(intervals[0].NameChanged);
        Assert.True(intervals[0].ProducerDatesChanged);
        Assert.Equal(new DateOnly(2026, 1, 1), intervals[0].ProducerEnd);
        Assert.Equal("etf", intervals[1].AssetType);
        Assert.Throws<ResearchException>(() => ListingIntervals.IntervalsFor("tiingo-tickers",
            [new(Day0, Snap(Row("AAA"))), new(Day0, Snap(Row("BBB")))]));
        Assert.Throws<ResearchException>(() => ListingIntervals.IntervalsFor("tiingo-tickers", []));
    }

    [Fact]
    public void BuildCountsUnchangedCapturesAsPresenceAndIsDeterministic()
    {
        var root = Path.Combine(Path.GetTempPath(), "twenty-listings-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var archive = new CaptureStore(Path.Combine(root, "captures"));
            void Record(byte[] bytes, DateTimeOffset at, bool unchanged) =>
                archive.Record(new(Guid.NewGuid().ToString("N"), "nasdaq-listed", CaptureSources.Find("nasdaq-listed").Url.AbsoluteUri,
                    CaptureStore.Hash(bytes), bytes.Length, "text/plain", at, "authored-unit", unchanged), unchanged ? null : bytes, false);
            var first = Encoding.ASCII.GetBytes(ListedFile("AAA", "BBB"));
            var later = Encoding.ASCII.GetBytes(ListedFile("AAA"));
            Record(first, Day0, false);
            Record(first, Day0.AddDays(1), true);
            Record(later, Day0.AddDays(2), false);

            var files = ListingIntervals.Build(archive, ["nasdaq-listed"]);
            Assert.Equal(files.Id, ListingIntervals.Build(archive, ["nasdaq-listed"]).Id);
            var csv = Encoding.UTF8.GetString(files.Csv);
            Assert.Contains("\"Authored, Inc. - Common Stock\"", csv, StringComparison.Ordinal);
            var aaa = csv.Split('\n').Single(l => l.Contains(",AAA,", StringComparison.Ordinal));
            Assert.Contains(",3,present-at-final-snapshot,", aaa, StringComparison.Ordinal);
            var bbb = csv.Split('\n').Single(l => l.Contains(",BBB,", StringComparison.Ordinal));
            Assert.Contains(",2,absent-from-later-snapshot,", bbb, StringComparison.Ordinal);
            Assert.Equal(122, files.Intervals);
            using var audit = JsonDocument.Parse(files.Audit);
            var source = audit.RootElement.GetProperty("sources")[0];
            Assert.Equal(3, source.GetProperty("snapshots").GetInt32());
            Assert.Equal("1016202607:00", source.GetProperty("captures")[0].GetProperty("producerCreation").GetString());
            Assert.Equal("listing-source-unsupported", Assert.Throws<ResearchException>(() =>
                ListingIntervals.Build(archive, ["shiller-ie-data"])).Code);
            Assert.Equal("listing-capture-missing", Assert.Throws<ResearchException>(() =>
                ListingIntervals.Build(archive, ["nasdaq-listed", "tiingo-tickers"])).Code);
            // A different file at an already-used capture instant is ambiguity, not an ordering to guess.
            Record(later, Day0, false);
            Assert.Equal("listing-snapshot-conflict", Assert.Throws<ResearchException>(() =>
                ListingIntervals.Build(archive, ["nasdaq-listed"])).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
