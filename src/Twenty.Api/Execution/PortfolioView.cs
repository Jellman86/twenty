using Twenty.Core.Execution;

namespace Twenty.Api.Execution;

public sealed record PortfolioHolding(string Symbol, string? Name, decimal Quantity, decimal? AveragePrice, decimal? CurrentPrice, string? PriceCurrency,
    decimal? CostValue, decimal? CurrentValue, decimal? UnrealizedProfitLoss, string? ValueCurrency, decimal ManagedQuantity);
public sealed record BrokerPortfolio(string Environment, string Account, string Currency, decimal AvailableCash, decimal ReservedCash,
    IReadOnlyList<PortfolioHolding> Holdings, decimal? TotalCost, decimal? TotalValue);
public sealed record SimulatedHolding(string Symbol, decimal Quantity, decimal CostGbp, decimal? AveragePriceGbp);
public sealed record SimulatedPortfolio(string Id, string RunId, bool Synthetic, decimal ContributionsGbp, decimal CashGbp, decimal ReservedGbp,
    IReadOnlyList<SimulatedHolding> Holdings, int OpenOrders, string? LastMonth);
public sealed record PortfolioOverview(BrokerPortfolio? Broker, string? BrokerError, bool BrokerConfigured, IReadOnlyList<SimulatedPortfolio> Simulated);

// Read-only view of what has been bought. Broker valuations come straight from the broker's position records;
// simulated holdings are priced at their booked debits only, because the simulator models no market value.
public static class PortfolioView
{
    public static async Task<PortfolioOverview> Build(IBrokerGateway? broker, BrokerLedger journal, PaperLedger paper, CancellationToken ct)
    {
        var simulated = paper.Accounts().Select(Simulated).ToList();
        if (broker is null) return new(null, null, false, simulated);
        try
        {
            var summary = await broker.Summary(ct);
            var positions = await broker.Positions(ct);
            var managed = journal.Accounts().Where(a => a.AccountId == summary.Id)
                .SelectMany(a => a.Holdings).GroupBy(h => h.Key).ToDictionary(g => g.Key, g => g.Sum(h => h.Value), StringComparer.Ordinal);
            var holdings = positions.Where(p => p.Quantity > 0).OrderByDescending(p => p.WalletImpact?.CurrentValue ?? 0).Select(p =>
                new PortfolioHolding(p.Instrument.Ticker, p.Instrument.Name, p.Quantity, p.AveragePricePaid, p.CurrentPrice, p.Instrument.Currency,
                    p.WalletImpact?.TotalCost, p.WalletImpact?.CurrentValue, p.WalletImpact?.UnrealizedProfitLoss, p.WalletImpact?.Currency,
                    managed.GetValueOrDefault(p.Instrument.Ticker))).ToList();
            // Only total when every holding reports a value in the account currency.
            var complete = holdings.All(h => h.CurrentValue is not null && h.CostValue is not null && h.ValueCurrency == summary.Currency);
            return new(new BrokerPortfolio(broker.Environment.ToString().ToLowerInvariant(), "…" + (summary.Id % 10000).ToString("D4"), summary.Currency,
                summary.Cash.AvailableToTrade, summary.Cash.ReservedForOrders, holdings,
                complete ? holdings.Sum(h => h.CostValue!.Value) : null, complete ? holdings.Sum(h => h.CurrentValue!.Value) : null), null, true, simulated);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or Twenty.Core.Research.ResearchException)
        {
            return new(null, "broker-read-unavailable", true, simulated);
        }
    }

    private static SimulatedPortfolio Simulated(PaperAccount account)
    {
        var costs = account.Orders.GroupBy(o => o.Intent.Symbol).ToDictionary(g => g.Key, g => g.Sum(o => o.Progress.DebitGbp), StringComparer.Ordinal);
        var holdings = account.Quantities.Where(q => q.Value > 0).OrderBy(q => q.Key, StringComparer.Ordinal).Select(q =>
        {
            var cost = costs.GetValueOrDefault(q.Key);
            return new SimulatedHolding(q.Key, q.Value, cost, q.Value > 0 ? decimal.Round(cost / q.Value, 6) : null);
        }).ToList();
        return new(account.Id, account.RunId, account.Synthetic, account.ContributionsGbp, account.CashGbp,
            account.Orders.Sum(o => o.Progress.RemainingReserve), holdings, account.Orders.Count(o => o.Progress.RemainingReserve > 0),
            account.Decisions.LastOrDefault()?.Month);
    }
}
