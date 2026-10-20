using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Tiingo;

namespace Twenty.Tests;

public sealed class TiingoHistoryIdentityTests
{
    private sealed class Workspace : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "twenty-history-identity-" + Guid.NewGuid().ToString("N"));
        public readonly CaptureStore Store;
        public Workspace() => Store = new(Root);
        public void Dispose() { Store.Dispose(); Directory.Delete(Root, true); }
    }

    private static CaptureObservation Record(CaptureStore store, string symbol, string bucket, string? url = null)
    {
        byte[] bytes = "[]"u8.ToArray();
        return store.Record(new(Guid.NewGuid().ToString("N"), bucket,
            url ?? $"https://api.tiingo.com/tiingo/daily/{symbol}/prices?startDate=2020-01-01&endDate=2020-01-03",
            CaptureStore.Hash(bytes), bytes.Length, "application/json", new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
            "authored-fixture", false), bytes, false);
    }

    [Fact]
    public void ReadsBothVersionsWithoutAliasingOrDuplicatingUnpunctuatedCaptures()
    {
        using var w = new Workspace();
        var old = Record(w.Store, "AAA.B", "tiingo-history-aaa-b");
        var other = Record(w.Store, "AAA-B", "tiingo-history-aaa-b");
        var current = Record(w.Store, "AAA.B", TiingoHistoryIdentity.SourceId("AAA.B"));
        var plain = Record(w.Store, "AAA", TiingoHistoryIdentity.SourceId("AAA"));
        Assert.Equal(new[] { old.Id, current.Id }.Order(), TiingoHistoryIdentity.Observations(w.Store, "aaa.b").Select(c => c.Id).Order());
        Assert.Equal(other.Id, Assert.Single(TiingoHistoryIdentity.Observations(w.Store, "AAA-B")).Id);
        Assert.Equal(plain.Id, Assert.Single(TiingoHistoryIdentity.Observations(w.Store, "AAA")).Id);
        Assert.Empty(TiingoHistoryIdentity.Observations(w.Store, "AAA.C"));
    }

    [Fact]
    public void VersionPrefixCannotTurnAnotherLegacyTickerIntoTargetEvidence()
    {
        using var w = new Workspace();
        // This legitimate old ticker's bucket equals the new bucket for AAA.B.
        Record(w.Store, "V2-AAA-DB", "tiingo-history-v2-aaa-db");
        Assert.Empty(TiingoHistoryIdentity.Observations(w.Store, "AAA.B"));
        Assert.Single(TiingoHistoryIdentity.Observations(w.Store, "V2-AAA-DB"));
    }

    [Fact]
    public void CaptureFiledInAnotherSourceDirectoryIsRefused()
    {
        using var w = new Workspace();
        Record(w.Store, "BBB", "tiingo-history-bbb");
        var parent = Path.Combine(w.Root, "capture-v1", "observations");
        Directory.Move(Path.Combine(parent, "tiingo-history-bbb"), Path.Combine(parent, "tiingo-history-aaa"));
        Assert.Equal("tiingo-history-provenance", Assert.Throws<ResearchException>(() =>
            TiingoHistoryIdentity.Observations(w.Store, "AAA")).Code);
    }

    [Theory]
    [InlineData("https://api.tiingo.com/authored")]
    [InlineData("https://api.tiingo.com/tiingo/daily/BBB/prices")]
    [InlineData("https://other.example/tiingo/daily/AAA/prices")]
    [InlineData("https://api.tiingo.com:" + "444/tiingo/daily/AAA/prices")]
    [InlineData("https://user@api.tiingo.com/tiingo/daily/AAA/prices")]
    [InlineData("https://api.tiingo.com/tiingo/daily/AAA/prices#other")]
    public void UndisambiguatedOrMislabelledHistoryFailsClosed(string url)
    {
        using var w = new Workspace();
        var capture = Record(w.Store, "AAA", "tiingo-history-aaa", url);
        Assert.False(TiingoHistoryIdentity.Matches(capture, "AAA"));
        Assert.Equal("tiingo-history-provenance", Assert.Throws<ResearchException>(() =>
            TiingoHistoryIdentity.Observations(w.Store, "AAA")).Code);
    }

    [Fact]
    public void EncodingIsDistinctAndFitsTheCaptureStoreAtMaximumTickerLength()
    {
        using var w = new Workspace();
        var symbols = new[] { "AAA.B", "AAA-B", "AAA-DB", "AAA.HB", "A" + new string('-', 19), "A" + new string('.', 19) };
        Assert.Equal(symbols.Length, symbols.Select(TiingoHistoryIdentity.SourceId).Distinct().Count());
        Assert.Equal("tiingo-history-aapl", TiingoHistoryIdentity.SourceId("AAPL"));
        foreach (var symbol in symbols)
        {
            var bucket = TiingoHistoryIdentity.SourceId(symbol);
            Assert.True(bucket.Length <= 64);
            var capture = Record(w.Store, symbol, bucket);
            Assert.Equal(capture.Id, Assert.Single(TiingoHistoryIdentity.Observations(w.Store, symbol)).Id);
        }
    }
}
