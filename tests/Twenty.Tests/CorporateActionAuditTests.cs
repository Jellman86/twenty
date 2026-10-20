using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Listings;

namespace Twenty.Tests;

public sealed class CorporateActionAuditTests
{
    private static readonly byte[] Source = Encoding.UTF8.GetBytes("Authored issuer statement: 2:1 split June 3; 0.50 dividend declared June 4, record June 6.");
    private static DateOnly Day(int d) => new(2024, 6, d);
    private static ActionReferences References() => new("action-reference.v1", "AAA", Day(1), Day(10),
        [new("issuer", "https://issuer.example/actions", new(2024, 6, 11, 0, 0, 0, TimeSpan.Zero), CaptureStore.Hash(Source))],
        [new("split", "exact-ex-date", Day(3), Day(3), 2m, "issuer"),
         new("dividend", "declaration-record-window", Day(4), Day(6), 0.5m, "issuer")]);

    private static byte[] History(decimal dividend = 0.5m, int dividendDay = 5, decimal split = 2m, bool extra = false) =>
        JsonSerializer.SerializeToUtf8Bytes(new[] { 1, 3, 5, 7, 10 }.Select(day => new
        {
            date = $"2024-06-{(day == 5 ? dividendDay : day):00}T00:00:00Z",
            open = 10m, high = 10m, low = 10m, close = 10m, volume = 100m,
            adjOpen = 10m, adjHigh = 10m, adjLow = 10m, adjClose = 10m, adjVolume = 100m,
            divCash = day == 5 ? dividend : extra && day == 7 ? 0.25m : 0m,
            splitFactor = day == 3 ? split : 1m
        }).OrderBy(b => b.date, StringComparer.Ordinal));
    private static CaptureObservation Capture(byte[] history) => new("0123456789abcdef0123456789abcdef", "tiingo-history-aaa", "https://api.tiingo.com/tiingo/daily/AAA/prices",
        CaptureStore.Hash(history), history.Length, "application/json", new(2024, 6, 11, 0, 0, 0, TimeSpan.Zero), "authored-fixture", false);
    private static CorporateActionAuditReport Run(byte[]? history = null, ActionReferences? refs = null) =>
        CorporateActionAudit.Run(Capture(history ?? History()), history ?? History(), JsonSerializer.SerializeToUtf8Bytes(refs ?? References(), DatasetJson.Options), _ => Source);

    [Fact]
    public void ExactSplitAndWindowedDividendMatchWithoutClaimingAnExactDividendDate()
    {
        var report = Run();
        Assert.True(report.Valid);
        Assert.Equal(2, report.Matches.Length);
        Assert.Equal(2, report.ReferenceActions);
        Assert.Equal(2, report.VendorActions);
        Assert.Equal("exact-ex-date", report.Matches.Single(m => m.Kind == "split").DateBasis);
        var dividend = report.Matches.Single(m => m.Kind == "dividend");
        Assert.Equal(Day(5), dividend.VendorDate);
        Assert.Equal(Day(4), dividend.ReferenceFrom);
        Assert.Equal(Day(6), dividend.ReferenceTo);
        Assert.Equal("declaration-record-window", dividend.DateBasis);
        Assert.Equal(CaptureStore.Hash(History()), report.HistorySha256);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(report, DatasetJson.Options),
            JsonSerializer.SerializeToUtf8Bytes(Run(), DatasetJson.Options));
    }

    [Theory]
    [InlineData("dividend")]
    [InlineData("split")]
    public void WrongAmountsAndRemovedActionsFail(string kind)
    {
        var wrong = kind == "dividend" ? History(dividend: 0.51m) : History(split: 3m);
        Assert.Contains(Run(wrong).Issues, i => i.Code == "reference-action-value" && i.Kind == kind);
        var removed = kind == "dividend" ? History(dividend: 0m) : History(split: 1m);
        Assert.Contains(Run(removed).Issues, i => i.Code == "reference-action-missing" && i.Kind == kind);
    }

    [Fact]
    public void WindowMoveAndAmbiguousActionsFail()
    {
        var moved = Run(History(dividendDay: 8));
        Assert.Contains(moved.Issues, i => i.Code == "reference-action-missing");
        Assert.Contains(moved.Issues, i => i.Code == "vendor-action-unreferenced");
        var refs = References();
        var ambiguous = Run(History(extra: true), refs with
        { Actions = refs.Actions.Select(a => a.Kind == "dividend" ? a with { To = Day(7) } : a).ToImmutableArray() });
        Assert.Equal("reference-action-ambiguous", Assert.Single(ambiguous.Issues).Code);
    }

    [Fact]
    public void ExtraUnreferencedActionFails()
    {
        var report = Run(History(extra: true));
        Assert.False(report.Valid);
        Assert.Equal("vendor-action-unreferenced", Assert.Single(report.Issues).Code);
    }

    [Fact]
    public void OverlappingAssertionsAreRefusedInsteadOfChoosingByAmount()
    {
        var refs = References();
        var invalid = refs with { Actions = refs.Actions.Add(refs.Actions[1] with { Value = 0.25m }) };
        Assert.Equal("action-reference-overlap", Assert.Throws<ResearchException>(() => Run(refs: invalid)).Code);
    }

    [Fact]
    public void WrongDocumentBytesAndInsufficientHistoryFailClosed()
    {
        Assert.Equal("action-source-integrity", Assert.Throws<ResearchException>(() =>
            CorporateActionAudit.Run(Capture(History()), History(), JsonSerializer.SerializeToUtf8Bytes(References(), DatasetJson.Options), _ => [1, 2])).Code);
        Assert.Equal("action-history-coverage", Assert.Throws<ResearchException>(() => Run(refs: References() with { To = Day(11) })).Code);
    }

    [Fact]
    public void ArchiveSelectionRequiresHistoryAndRefusesConflictingLatestCaptures()
    {
        var root = Path.Combine(Path.GetTempPath(), "twenty-action-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new CaptureStore(root);
            var reference = JsonSerializer.SerializeToUtf8Bytes(References(), DatasetJson.Options);
            Assert.Equal("action-history-not-captured", Assert.Throws<ResearchException>(() =>
                CorporateActionAudit.Build(store, reference, _ => Source)).Code);
            var history = History();
            store.Record(Capture(history), history, false);
            var valid = CorporateActionAudit.Build(store, reference, _ => Source);
            Assert.True(valid.Valid);
            Assert.Equal("0123456789abcdef0123456789abcdef", valid.HistoryCaptureId);
            var changed = History(dividend: 0.51m);
            store.Record(Capture(changed) with { Id = "1123456789abcdef0123456789abcdef" }, changed, false);
            Assert.Equal("action-history-capture-conflict", Assert.Throws<ResearchException>(() =>
                CorporateActionAudit.Build(store, reference, _ => Source)).Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("symbol")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("url")]
    public void MislabelledOrTamperedCaptureMetadataIsRefused(string field)
    {
        var bytes = History();
        var capture = Capture(bytes);
        capture = field switch
        {
            "symbol" => capture with { SourceId = "tiingo-history-bbb" },
            "hash" => capture with { Sha256 = new string('a', 64) },
            "url" => capture with { Url = "https://api.tiingo.com/tiingo/daily/BBB/prices" },
            _ => capture with { Bytes = bytes.Length + 1 }
        };
        Assert.Equal("action-history-integrity", Assert.Throws<ResearchException>(() => CorporateActionAudit.Run(
            capture, bytes, JsonSerializer.SerializeToUtf8Bytes(References(), DatasetJson.Options), _ => Source)).Code);
    }

    [Fact]
    public void CollidingLegacySourceIdCannotBindAnotherTickersActions()
    {
        var bytes = History();
        var capture = Capture(bytes) with { SourceId = "tiingo-history-aaa-b", Url = "https://api.tiingo.com/tiingo/daily/AAA-B/prices" };
        var refs = JsonSerializer.SerializeToUtf8Bytes(References() with { Symbol = "AAA.B" }, DatasetJson.Options);
        Assert.Equal("action-history-integrity", Assert.Throws<ResearchException>(() =>
            CorporateActionAudit.Run(capture, bytes, refs, _ => Source)).Code);
    }

    [Theory]
    [InlineData("unknown-basis", "dividend", 0.5)]
    [InlineData("exact-ex-date", "dividend", 0.5)] // a range cannot assert an exact date
    [InlineData("declaration-record-window", "split", 2)]
    [InlineData("declaration-record-window", "dividend", 0)]
    public void UnsupportedReferenceSemanticsAreRefused(string basis, string kind, decimal value)
    {
        var refs = References();
        refs = refs with { Actions = [new(kind, basis, Day(4), Day(6), value, "issuer")] };
        Assert.Equal("action-reference-assertion", Assert.Throws<ResearchException>(() => Run(refs: refs)).Code);
    }
}
