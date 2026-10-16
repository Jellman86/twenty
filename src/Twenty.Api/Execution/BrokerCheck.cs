using System.Text.Json;
using Twenty.Core.Research;

namespace Twenty.Api.Execution;

/// <summary>
/// <c>dotnet Twenty.Api.dll --broker-check</c>: proves the configured credential works with two read-only calls
/// (account summary and positions) and prints a masked result. It places no order and records no evidence.
/// </summary>
public static class BrokerCheck
{
    public static async Task<int> Run(IConfiguration configuration)
    {
        try
        {
            var broker = BrokerConfiguration.Create(configuration);
            if (broker is null) { Print(new { ok = false, code = "broker-credentials-missing" }); return 1; }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var summary = await broker.Summary(deadline.Token);
            var positions = await broker.Positions(deadline.Token);
            Print(new { ok = true, environment = broker.Environment.ToString().ToLowerInvariant(), account = "…" + (summary.Id % 10000).ToString("D4"),
                summary.Currency, availableToTrade = summary.Cash.AvailableToTrade, reservedForOrders = summary.Cash.ReservedForOrders,
                positions = positions.Count(p => p.Quantity > 0), valuedPositions = positions.Count(p => p.WalletImpact is not null) });
            return 0;
        }
        catch (ResearchException error) { Print(new { ok = false, code = error.Code }); return 1; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        { Print(new { ok = false, code = error is JsonException ? "broker-schema" : "broker-unreachable" }); return 1; }
    }
    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
}
