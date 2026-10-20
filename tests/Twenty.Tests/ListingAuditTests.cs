using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Listings;

namespace Twenty.Tests;

public sealed class ListingAuditTests
{
    private static readonly DateTimeOffset Evidence = new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero);
    private const string IntervalHeader = "source,symbol,exchange,asset_type,price_currency,security_name,producer_start_date,producer_end_date,first_seen_utc,last_seen_utc,closed_absent_at_utc,snapshots_present,status,name_changed,producer_dates_changed\n";

    private sealed class Workspace : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "twenty-listing-audit-" + Guid.NewGuid().ToString("N"));
        public readonly CaptureStore Store; public Workspace() => Store = new(Root);
        public void Dispose() { Store.Dispose(); Directory.Delete(Root, true); }
    }
    private static string RecordHistory(CaptureStore store, string symbol, string json, DateTimeOffset at)
    {
        var id = Guid.NewGuid().ToString("N");
        store.Record(new(id, "tiingo-history-" + symbol.ToLowerInvariant().Replace('.', '-'),
            "https://api.tiingo.com/authored", CaptureStore.Hash(Encoding.UTF8.GetBytes(json)), Encoding.UTF8.GetByteCount(json),
            "application/json", at, "authored-unit", false), Encoding.UTF8.GetBytes(json), false);
        return id;
    }

    // Authored bars with internally consistent split/dividend adjustment factors.
    private static string HistoryJson((string Date, decimal Close, decimal Div, decimal Split)[] rows, Func<int, decimal?>? adjOverride = null)
    {
        var steps = new decimal[rows.Length];
        for (var t = 1; t < rows.Length; t++)
        {
            var step = 1m;
            if (rows[t].Split != 1) step *= 1 / rows[t].Split;
            if (rows[t].Div > 0) step *= 1 - rows[t].Div / rows[t - 1].Close;
            steps[t] = step;
        }
        var json = new StringBuilder("[");
        for (var t = 0; t < rows.Length; t++)
        {
            var factor = 1m;
            for (var s = t + 1; s < rows.Length; s++) factor *= steps[s];
            var adj = adjOverride?.Invoke(t) ?? rows[t].Close * factor;
            if (t > 0) json.Append(',');
            json.Append(CultureInfo.InvariantCulture,
                $"{{\"date\":\"{rows[t].Date}T00:00:00.000Z\",\"open\":{rows[t].Close},\"high\":{rows[t].Close},\"low\":{rows[t].Close},\"close\":{rows[t].Close},\"adjClose\":{adj:0.##########},\"adjOpen\":{adj:0.##########},\"adjHigh\":{adj:0.##########},\"adjLow\":{adj:0.##########},\"volume\":1000,\"adjVolume\":1000,\"divCash\":{rows[t].Div},\"splitFactor\":{rows[t].Split}}}");
        }
        return json.Append(']').ToString();
    }
    private static string IntervalRow(string source, string symbol, string exchange = "NASDAQ", string type = "Stock",
        string start = "2020-01-02", string end = "", string status = "present-at-final-snapshot",
        string first = "2026-10-15T06:00:00.0000000+00:00", string last = "2026-10-16T06:00:00.0000000+00:00",
        string closed = "", int snapshots = 2, bool nameChanged = false, bool datesChanged = false, string name = "Authored Inc.") =>
        $"{source},{symbol},{exchange},{type},USD,\"{name}\",{start},{end},{first},{last},{closed},{snapshots},{status},{(nameChanged ? "true" : "false")},{(datesChanged ? "true" : "false")}\n";
    private static (byte[] Csv, string Id) Intervals(params string[] rows)
    {
        var bytes = Encoding.UTF8.GetBytes(IntervalHeader + string.Concat(rows));
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ListingIntervals.Version + "\n").Concat(bytes).ToArray()));
        return (bytes, id);
    }
    private static (string Date, decimal Close, decimal Div, decimal Split)[] Bars(
        params (string Date, decimal Close, decimal Div, decimal Split)[] rows) => rows;

    [Fact]
    public void ConsistentSplitAndDividendStepsVerifyWithinTolerance()
    {
        using var w = new Workspace();
        var rows = Bars(("2023-01-03", 100m, 0m, 1m), ("2023-02-01", 102m, 1m, 1m), ("2023-03-01", 51m, 0m, 2m), ("2023-04-03", 52m, .5m, 1m));
        RecordHistory(w.Store, "AAA", HistoryJson(rows), Evidence);
        var (csv, id) = Intervals(
            IntervalRow("tiingo-tickers", "AAA", end: "2023-04-03"),
            IntervalRow("nasdaq-listed", "AAA", type: "stock"));
        var files = ListingAudit.Build(csv, id, w.Store, ["AAA"]);
        var subject = Assert.Single(files.Subjects);
        // Bars end 2023-04 in a capture taken 2026-10: the producer had no later bars, yet Nasdaq still lists it.
        Assert.Equal("history-ended-but-directory-present", subject.TerminalClass);
        Assert.Equal(1, subject.SplitEvents); Assert.Equal(2, subject.DividendEvents);
        Assert.Empty(subject.AdjustmentMismatches); Assert.Empty(subject.UnexplainedSteps);
        Assert.True(subject.MaximumStepRelativeError <= ListingAudit.StepTolerance);
        Assert.Equal(52m, subject.LastClose);
    }

    [Fact]
    public void TamperedAdjustedCloseAndMissingSplitStepAreFlagged()
    {
        using var w = new Workspace();
        var rows = Bars(("2023-01-03", 100m, 0m, 1m), ("2023-02-01", 25m, 0m, 4m), ("2023-03-01", 26m, 0m, 1m), ("2023-04-03", 27m, 0m, 1m));
        // splitFactor 4 recorded but adjClose identical to close: the producer step never happened.
        var json = HistoryJson(rows, t => rows[t].Close);
        RecordHistory(w.Store, "AAA", json, Evidence);
        var (csv, id) = Intervals(IntervalRow("tiingo-tickers", "AAA", end: "2023-04-03"));
        var subject = Assert.Single(ListingAudit.Build(csv, id, w.Store, ["AAA"]).Subjects);
        Assert.Contains("adjustment-step-mismatch", subject.Findings);
        var step = Assert.Single(subject.AdjustmentMismatches);
        Assert.Equal(new DateOnly(2023, 2, 1), step.Date); Assert.Equal("split", step.Kind); Assert.Equal(4m, step.SplitFactor);
        Assert.True(step.RelativeError > ListingAudit.StepTolerance);
    }

    [Fact]
    public void UnexplainedAdjustmentStepIsFlagged()
    {
        using var w = new Workspace();
        var rows = Bars(("2023-01-03", 100m, 0m, 1m), ("2023-02-01", 101m, 0m, 1m), ("2023-03-01", 102m, 0m, 1m));
        var json = HistoryJson(rows, t => t == 0 ? rows[0].Close * .9m : null);
        RecordHistory(w.Store, "AAA", json, Evidence);
        var (csv, id) = Intervals(IntervalRow("tiingo-tickers", "AAA", end: "2023-03-01"));
        var subject = Assert.Single(ListingAudit.Build(csv, id, w.Store, ["AAA"]).Subjects);
        Assert.Contains("unexplained-adjustment-step", subject.Findings);
        Assert.Equal(new DateOnly(2023, 2, 1), Assert.Single(subject.UnexplainedSteps).Date);
    }

    [Fact]
    public void EndedHistoryAbsentDirectoryIsADelistingCandidateWithProceedsUnresolved()
    {
        using var w = new Workspace();
        var rows = Bars(("2023-09-27", .12m, 0m, 1m), ("2023-09-28", .11m, 0m, 1m), ("2023-09-29", .10m, 0m, 1m));
        RecordHistory(w.Store, "BBBYQ", HistoryJson(rows), Evidence.AddDays(-30));
        var (csv, id) = Intervals(
            IntervalRow("tiingo-tickers", "BBBYQ", exchange: "PINK", start: "1992-06-05", end: "2023-09-29"),
            IntervalRow("nasdaq-listed", "AAA", type: "stock"));
        var subject = Assert.Single(ListingAudit.Build(csv, id, w.Store, ["BBBYQ"]).Subjects);
        Assert.Equal("delisting-candidate-proceeds-unresolved", subject.TerminalClass);
        Assert.Contains("producer-ended-and-history-ended", subject.Findings);
        Assert.False(subject.NasdaqPresentAtFinal);
        Assert.Equal(.10m, subject.LastClose); Assert.Equal(new DateOnly(2023, 9, 29), subject.HistoryLast);
        Assert.DoesNotContain("history-continues-past-producer-end", subject.Findings);
    }

    [Fact]
    public void TiingoOnlyArtifactCannotProduceADelistingCandidate()
    {
        using var w = new Workspace();
        var rows = Bars(("2023-09-28", .11m, 0m, 1m), ("2023-09-29", .10m, 0m, 1m));
        RecordHistory(w.Store, "BBBYQ", HistoryJson(rows), Evidence);
        var (csv, id) = Intervals(IntervalRow("tiingo-tickers", "BBBYQ", exchange: "PINK", start: "1992-06-05", end: "2023-09-29"));
        var subject = Assert.Single(ListingAudit.Build(csv, id, w.Store, ["BBBYQ"]).Subjects);
        // One vendor saying coverage and prices both ended is not cross-source delisting evidence.
        Assert.Equal("history-ended-unresolved", subject.TerminalClass);
        Assert.Contains("no-nasdaq-directory-evidence", subject.Findings);
        Assert.False(subject.NasdaqSourcesPresent);
    }

    [Fact]
    public void DegenerateDividendIsFlaggedAndMismatchCountsAreUncapped()
    {
        using var w = new Workspace();
        var many = Enumerable.Range(0, 160).Select(i => (Date: new DateOnly(2023, 1, 3).AddDays(i).ToString("yyyy-MM-dd"), Close: 10m, Div: 0m, Split: i == 0 ? 1m : 2m)).ToArray();
        // adjClose == close everywhere: every split step mismatches; first bar carries a dividend event.
        var withFirst = many.Select((r, i) => i == 0 ? (r.Date, r.Close, Div: .5m, r.Split) : r).ToArray();
        RecordHistory(w.Store, "AAA", HistoryJson(withFirst, t => withFirst[t].Close), Evidence);
        var (csv, id) = Intervals(IntervalRow("tiingo-tickers", "AAA", end: "2023-06-11"));
        var subject = Assert.Single(ListingAudit.Build(csv, id, w.Store, ["AAA"]).Subjects);
        Assert.Equal(159, subject.AdjustmentMismatchCount);
        Assert.Equal(100, subject.AdjustmentMismatches.Length);
        Assert.Equal(159, subject.SplitEvents); Assert.Equal(1, subject.DividendEvents);

        using var w2 = new Workspace();
        var degenerate = Bars(("2023-01-03", 10m, 0m, 1m), ("2023-02-01", 9m, 11m, 1m), ("2023-03-01", 9m, 0m, 1m));
        RecordHistory(w2.Store, "BBB", HistoryJson(degenerate, t => degenerate[t].Close), Evidence);
        var (csv2, id2) = Intervals(IntervalRow("tiingo-tickers", "BBB", end: "2023-03-01"));
        var flagged = Assert.Single(ListingAudit.Build(csv2, id2, w2.Store, ["BBB"]).Subjects);
        Assert.Contains("degenerate-corporate-action", flagged.Findings);
        Assert.Contains("adjustment-step-mismatch", flagged.Findings);
        Assert.Equal(1, flagged.AdjustmentMismatchCount);
    }

    [Fact]
    public void HistoryContinuingPastProducerEndAndStaleCaptureAreDistinctFromDelisting()
    {
        using var w = new Workspace();
        var rows = Bars(("2023-06-01", 10m, 0m, 1m), ("2023-06-02", 10m, 0m, 1m));
        RecordHistory(w.Store, "AAA", HistoryJson(rows), new DateTimeOffset(2023, 6, 5, 0, 0, 0, TimeSpan.Zero));
        var (csv, id) = Intervals(
            IntervalRow("tiingo-tickers", "AAA", end: "2022-01-04"),
            IntervalRow("nasdaq-listed", "AAA", type: "stock"));
        var subject = Assert.Single(ListingAudit.Build(csv, id, w.Store, ["AAA"]).Subjects);
        Assert.Equal("history-capture-stale", subject.TerminalClass);
        Assert.Contains("history-continues-past-producer-end", subject.Findings);
        Assert.Contains("history-capture-stale", subject.Findings);
    }

    [Fact]
    public void ActiveSymbolWithAlignedProducerEndIsHistoryCurrent()
    {
        using var w = new Workspace();
        var rows = Bars(("2026-10-14", 250m, 0m, 1m), ("2026-10-15", 252m, 0m, 1m), ("2026-10-16", 251m, 0m, 1m));
        RecordHistory(w.Store, "AAA", HistoryJson(rows), Evidence);
        var (csv, id) = Intervals(
            IntervalRow("tiingo-tickers", "AAA", end: "2026-10-16"),
            IntervalRow("nasdaq-listed", "AAA", type: "stock"));
        var subject = Assert.Single(ListingAudit.Build(csv, id, w.Store, ["AAA"]).Subjects);
        Assert.Equal("history-current", subject.TerminalClass);
        Assert.True(subject.NasdaqPresentAtFinal);
        Assert.DoesNotContain("producer-ended-and-history-ended", subject.Findings);
    }

    [Fact]
    public void RenameSegmentRestartAndMissingEvidenceAreFindingsNotSilence()
    {
        using var w = new Workspace();
        var (csv, id) = Intervals(
            IntervalRow("tiingo-tickers", "DDC", exchange: "AMEX", start: "1992-06-22", end: "2020-01-02", nameChanged: true),
            IntervalRow("tiingo-tickers", "DDC", exchange: "AMEX", start: "2023-11-17", end: "2026-10-16", datesChanged: true));
        var files = ListingAudit.Build(csv, id, w.Store, ["DDC", "GHOST"]);
        var ddc = files.Subjects[0]; var ghost = files.Subjects[1];
        Assert.Contains("name-changed", ddc.Findings); Assert.Contains("producer-dates-changed", ddc.Findings);
        Assert.Contains("segment-restart", ddc.Findings); Assert.Contains("no-history-capture", ddc.Findings);
        Assert.Equal(2, ddc.TiingoSegments); Assert.Equal("no-history-evidence", ddc.TerminalClass);
        Assert.Contains("no-history-capture", ghost.Findings); Assert.Contains("no-interval-rows", ghost.Findings);
        Assert.Equal(0, ghost.IntervalRows);
        Assert.Equal(["DDC", "GHOST"], files.Subjects.Select(s => s.Symbol));
        Assert.Equal(files.Id, ListingAudit.Build(csv, id, w.Store, ["DDC", "GHOST"]).Id);
        using var audit = JsonDocument.Parse(files.Audit);
        Assert.Equal(ListingAudit.Version, audit.RootElement.GetProperty("converter").GetString());
        Assert.Equal(id, audit.RootElement.GetProperty("intervalId").GetString());
    }

    [Fact]
    public void IntervalIntegrityCaptureConflictsAndSymbolRequestsFailClosed()
    {
        using var w = new Workspace();
        var (csv, id) = Intervals(IntervalRow("tiingo-tickers", "AAA"));
        Assert.Equal("listing-audit-interval-integrity", Assert.Throws<ResearchException>(() =>
            ListingAudit.Build(csv, new string('0', 64), w.Store, ["AAA"])).Code);
        Assert.Equal("listing-audit-symbols", Assert.Throws<ResearchException>(() =>
            ListingAudit.Build(csv, id, w.Store, ["AAA", "aaa"])).Code);
        Assert.Equal("listing-audit-symbols", Assert.Throws<ResearchException>(() =>
            ListingAudit.Build(csv, id, w.Store, ["bad symbol!"])).Code);
        Assert.Equal("listing-audit-symbols", Assert.Throws<ResearchException>(() =>
            ListingAudit.Build(csv, id, w.Store, [])).Code);
        var badHeader = Encoding.UTF8.GetBytes("nope\n");
        var badId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ListingIntervals.Version + "\n").Concat(badHeader).ToArray()));
        Assert.Equal("listing-audit-interval-header", Assert.Throws<ResearchException>(() =>
            ListingAudit.Build(badHeader, badId, w.Store, ["AAA"])).Code);
        var rows = Bars(("2023-01-03", 10m, 0m, 1m), ("2023-01-04", 10m, 0m, 1m));
        RecordHistory(w.Store, "AAA", HistoryJson(rows), Evidence);
        RecordHistory(w.Store, "AAA", HistoryJson(rows, _ => 9m), Evidence);
        Assert.Equal("listing-audit-capture-conflict", Assert.Throws<ResearchException>(() =>
            ListingAudit.Build(csv, id, w.Store, ["AAA"])).Code);
    }

    [Fact]
    public void LatestHistoryCaptureWins()
    {
        using var w = new Workspace();
        var early = Bars(("2023-01-03", 10m, 0m, 1m), ("2023-01-04", 10m, 0m, 1m));
        var later = Bars(("2023-01-03", 10m, 0m, 1m), ("2023-01-04", 11m, 0m, 1m), ("2023-01-05", 12m, 0m, 1m));
        RecordHistory(w.Store, "AAA", HistoryJson(early), Evidence.AddDays(-2));
        var laterId = RecordHistory(w.Store, "AAA", HistoryJson(later), Evidence);
        var (csv, id) = Intervals(IntervalRow("tiingo-tickers", "AAA", end: "2023-01-05"));
        var subject = Assert.Single(ListingAudit.Build(csv, id, w.Store, ["AAA"]).Subjects);
        Assert.Equal(laterId, subject.HistoryCaptureId); Assert.Equal(3, subject.HistoryRows); Assert.Equal(12m, subject.LastClose);
    }

    [Fact]
    public void FullPipelineFromCapturedDirectoriesToAudit()
    {
        var root = Path.Combine(Path.GetTempPath(), "twenty-listing-audit-integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var archive = new CaptureStore(Path.Combine(root, "captures"));
            var listed = new StringBuilder("Symbol|Security Name|Market Category|Test Issue|Financial Status|Round Lot Size|ETF|NextShares\n");
            listed.Append("AAA|Authored, Inc. - Common Stock|Q|N|N|100|N|N\n");
            for (var i = 0; i < 120; i++) listed.Append($"F{i:D3}|Filler Security {i}|Q|N|N|100|N|N\n");
            listed.Append("File Creation Time: 1016202607:00|||||||\n");
            var listedBytes = Encoding.ASCII.GetBytes(listed.ToString());
            archive.Record(new(Guid.NewGuid().ToString("N"), "nasdaq-listed", CaptureSources.Find("nasdaq-listed").Url.AbsoluteUri,
                CaptureStore.Hash(listedBytes), listedBytes.Length, "text/plain", Evidence, "authored-unit", false), listedBytes, false);
            using var zipOut = new MemoryStream();
            using (var zip = new ZipArchive(zipOut, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("supported_tickers.csv").Open()))
                writer.Write("ticker,exchange,assetType,priceCurrency,startDate,endDate\nAAA,NASDAQ,Stock,USD,2020-01-02,2026-10-16\nDEL,PINK,Stock,USD,2019-01-02,2023-09-29\n");
            var zipBytes = zipOut.ToArray();
            archive.Record(new(Guid.NewGuid().ToString("N"), "tiingo-tickers", CaptureSources.Find("tiingo-tickers").Url.AbsoluteUri,
                CaptureStore.Hash(zipBytes), zipBytes.Length, "application/zip", Evidence, "authored-unit", false), zipBytes, false);
            var intervals = ListingIntervals.Build(archive, ["nasdaq-listed", "tiingo-tickers"]);
            RecordHistory(archive, "DEL", HistoryJson(Bars(("2023-09-28", .2m, 0m, 1m), ("2023-09-29", .1m, 0m, 1m))), Evidence);
            var files = ListingAudit.Build(intervals.Csv, intervals.Id, archive, ["DEL"]);
            var subject = Assert.Single(files.Subjects);
            Assert.Equal("delisting-candidate-proceeds-unresolved", subject.TerminalClass);
            using var audit = JsonDocument.Parse(files.Audit);
            Assert.Equal(intervals.Id, audit.RootElement.GetProperty("intervalId").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
