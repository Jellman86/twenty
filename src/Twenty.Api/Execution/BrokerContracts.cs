using Twenty.Core.Execution;

namespace Twenty.Api.Execution;

public sealed record BrokerInstrument(string Ticker, string? Currency = null, string? Isin = null);
public sealed record BrokerPosition(BrokerInstrument Instrument, decimal Quantity);
public sealed record BrokerTax(string Currency, decimal Quantity, string Name);
public sealed record BrokerWalletImpact(string Currency, decimal NetValue, decimal FxRate, BrokerTax[]? Taxes = null);
public sealed record BrokerFill(long Id, DateTimeOffset FilledAt, decimal Quantity, decimal Price, string Type, BrokerWalletImpact WalletImpact);
public sealed record BrokerHistoryItem(BrokerOrder Order, BrokerFill? Fill = null);
public sealed record BrokerHistoryPage(BrokerHistoryItem[] Items, string? NextPagePath);
public sealed record BrokerHistorySnapshot(BrokerHistoryItem[] Items, bool Complete, int Pages);

public interface IBrokerGateway
{
    BrokerEnvironment Environment { get; }
    Task<BrokerSummary> Summary(CancellationToken ct);
    Task<BrokerPosition[]> Positions(CancellationToken ct);
    Task<BrokerOrder[]> PendingOrders(CancellationToken ct);
    Task<BrokerHistorySnapshot> History(CancellationToken ct);
    Task<SubmissionResult> PlaceLimit(BuyIntent intent, CancellationToken ct, string? localIntentId = null);
    Task<bool> RequestCancel(long id, CancellationToken ct);
}
