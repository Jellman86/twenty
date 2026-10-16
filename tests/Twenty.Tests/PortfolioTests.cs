using System.Text.Json;
using Twenty.Api.Execution;
using Twenty.Core.Execution;
using Twenty.Research;

namespace Twenty.Tests;

public sealed class PortfolioTests
{
    [Fact]
    public async Task BrokerHoldingsKeepTheBrokersOwnValuationsAndTotalOnlyInOneCurrency()
    {
        using var f = new Fixture();
        var gateway = new Gateway([
            new(new("AAPL_US_EQ", "USD", Name: "Apple"), 0.5m, 150m, 200m, new("GBP", 80m, 60m, 20m, -1m)),
            new(new("VUSA_EQ", "GBP", Name: "S&P 500 ETF"), 2m, 50m, 55m, new("GBP", 110m, 100m, 10m)),
            new(new("SOLD_EQ"), 0m),
        ]);
        var view = await PortfolioView.Build(gateway, f.Journal, f.Paper, CancellationToken.None);
        var broker = Assert.IsType<BrokerPortfolio>(view.Broker);
        Assert.Null(view.BrokerError); Assert.True(view.BrokerConfigured);
        Assert.Equal("demo", broker.Environment); Assert.Equal("…0042", broker.Account);
        Assert.Equal(["VUSA_EQ", "AAPL_US_EQ"], broker.Holdings.Select(h => h.Symbol));
        var apple = broker.Holdings[1];
        Assert.Equal("Apple", apple.Name); Assert.Equal(150m, apple.AveragePrice); Assert.Equal("USD", apple.PriceCurrency);
        Assert.Equal(60m, apple.CostValue); Assert.Equal(80m, apple.CurrentValue); Assert.Equal(0m, apple.ManagedQuantity);
        Assert.Equal(160m, broker.TotalCost); Assert.Equal(190m, broker.TotalValue);

        var mixed = await PortfolioView.Build(new Gateway([new(new("X_EQ"), 1m, WalletImpact: new("EUR", 1m, 1m, 0m))]), f.Journal, f.Paper, CancellationToken.None);
        Assert.Null(mixed.Broker!.TotalValue); Assert.Null(mixed.Broker.TotalCost);
    }

    [Fact]
    public async Task UnavailableOrMissingBrokerStillShowsSimulatedHoldings()
    {
        using var f = new Fixture();
        var none = await PortfolioView.Build(null, f.Journal, f.Paper, CancellationToken.None);
        Assert.False(none.BrokerConfigured); Assert.Null(none.Broker); Assert.Empty(none.Simulated);
        var failed = await PortfolioView.Build(new Gateway([], fail: true), f.Journal, f.Paper, CancellationToken.None);
        Assert.True(failed.BrokerConfigured); Assert.Null(failed.Broker); Assert.Equal("broker-read-unavailable", failed.BrokerError);
    }

    [Fact]
    public void BrokerPositionsReadTheDocumentedValuationFields()
    {
        const string json = """[{"instrument":{"ticker":"AAPL_US_EQ","name":"Apple","currency":"USD","isin":"US0378331005"},"quantity":0.5,"averagePricePaid":150,"currentPrice":200,"walletImpact":{"currency":"GBP","currentValue":80,"totalCost":60,"unrealizedProfitLoss":20,"fxImpact":null}}]""";
        var position = Assert.Single(JsonSerializer.Deserialize<BrokerPosition[]>(json, new JsonSerializerOptions(ResearchStore.Json) { RespectRequiredConstructorParameters = true })!);
        Assert.Equal("Apple", position.Instrument.Name); Assert.Equal(200m, position.CurrentPrice);
        Assert.Equal(20m, position.WalletImpact!.UnrealizedProfitLoss); Assert.Null(position.WalletImpact.FxImpact);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "twenty-portfolio-" + Guid.NewGuid().ToString("N"));
        private readonly ResearchStore store;
        public PaperLedger Paper { get; }
        public BrokerLedger Journal { get; }
        public Fixture() { store = new ResearchStore(root); Paper = new PaperLedger(store); Journal = new BrokerLedger(store); }
        public void Dispose() { store.Dispose(); Directory.Delete(root, true); }
    }

    private sealed class Gateway(BrokerPosition[] positions, bool fail = false) : IBrokerGateway
    {
        public BrokerEnvironment Environment => BrokerEnvironment.Demo;
        public Task<BrokerSummary> Summary(CancellationToken ct) =>
            fail ? throw new HttpRequestException("offline") : Task.FromResult(new BrokerSummary(1000042, "GBP", new BrokerCash(12.5m, 0m)));
        public Task<BrokerPosition[]> Positions(CancellationToken ct) => Task.FromResult(positions);
        public Task<BrokerOrder[]> PendingOrders(CancellationToken ct) => throw new NotSupportedException();
        public Task<BrokerHistorySnapshot> History(CancellationToken ct) => throw new NotSupportedException();
        public Task<SubmissionResult> PlaceLimit(BuyIntent intent, CancellationToken ct, string? localIntentId = null) => throw new NotSupportedException();
        public Task<bool> RequestCancel(long id, CancellationToken ct) => throw new NotSupportedException();
    }
}
