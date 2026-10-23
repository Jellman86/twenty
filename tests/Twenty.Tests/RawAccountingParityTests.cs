using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed partial class RawGbpLedgerTests
{
    public static IEnumerable<object[]> RawParityScenarios()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "raw-accounting-parity.v1.json")));
        Assert.Equal("raw-accounting-parity-v1", document.RootElement.GetProperty("version").GetString());
        Assert.Equal("authored-synthetic", document.RootElement.GetProperty("inputKind").GetString());
        foreach (var scenario in document.RootElement.GetProperty("scenarios").EnumerateArray())
            yield return [scenario.GetProperty("id").GetString()!, scenario.Clone()];
    }

    [Theory]
    [MemberData(nameof(RawParityScenarios))]
    public void SharedIndependentRawAccountingScenariosMatchEveryLedgerCheckpoint(string id, JsonElement scenario)
    {
        Assert.Equal(id, scenario.GetProperty("id").GetString());
        CheckParityScenario(scenario);
    }

    private static decimal Number(JsonElement row, string name) => decimal.Parse(row.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
    private static void CheckParityScenario(JsonElement scenario)
    {
        var ledger = new RawGbpLedger(); var actions = new List<CorporateAction>();
        var currency = scenario.GetProperty("currency").GetString()!;
        var units = new Denomination(currency, scenario.GetProperty("quoteUnit").GetString() == "pence" ? QuoteUnit.Pence : QuoteUnit.Major);
        CorporateAction? dividend = null, terminal = null; decimal? equity = null;
        var index = 0;
        foreach (var e in scenario.GetProperty("events").EnumerateArray())
        {
            var at = BuyAt.AddDays(index); var id = $"parity-{index++}";
            var kind = e.GetProperty("kind").GetString();
            if (kind is "split" or "dividend" or "terminal")
            {
                var action = kind switch
                {
                    "split" => Split() with { Id = id, ActionId = id, EffectiveAt = at, Evidence = Known(at), SplitRatio = Number(e, "ratio") },
                    "dividend" => Dividend() with { Id = id, ActionId = id, EffectiveAt = at, Evidence = Known(at), CashPerShare = Number(e, "perShare"), CashCurrency = currency },
                    _ => Terminal(Enum.Parse<TerminalOutcome>(e.GetProperty("outcome").GetString()!, true)) with
                    { Id = id, ActionId = id, EffectiveAt = at, Evidence = Known(at) }
                };
                if (kind == "terminal")
                    action = action with { CashPerShare = action.TerminalOutcome == TerminalOutcome.Cash ? Number(e, "perShare") : null,
                        CashCurrency = action.TerminalOutcome == TerminalOutcome.Cash ? currency : null,
                        SplitRatio = action.TerminalOutcome == TerminalOutcome.Stock ? Number(e, "ratio") : null };
                actions.Add(action);
                if (kind == "dividend") dividend = action;
                if (kind == "terminal") terminal = action;
            }
            var fx = e.TryGetProperty("fx", out _) ? Number(e, "fx") : .75m;
            var price = e.TryGetProperty("price", out _) ? Number(e, "price") : 100;
            var market = Market(at, units, price, fx, actions: [.. actions.Where(a => terminal?.TerminalOutcome != TerminalOutcome.Stock || a != terminal)]);
            // The successor uses major units; original pence scaling never changes delivered share counts.
            if (terminal?.TerminalOutcome == TerminalOutcome.Stock)
            {
                var fixture = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
                var listing = market.Observations.OfType<Listing>().Single(); var bar = market.Observations.OfType<PriceBar>().Single();
                var successorPrice = e.TryGetProperty("successorPrice", out _) ? Number(e, "successorPrice") : 80;
                market = new PointInTimeDataset(fixture with { Observations = market.Observations.AddRange(ImmutableArray.Create<Observation>(terminal,
                    listing with { Id = "successor-listing", ListingId = "successor", SecurityId = "successor-security", CompanyId = "successor-company", Symbol = "NEW",
                        ProviderIds = [new ProviderIdentifier("fixture", "NEW")], Denomination = new(currency, QuoteUnit.Major) },
                    bar with { Id = "successor-price", ListingId = "successor", Denomination = new(currency, QuoteUnit.Major),
                        Open = successorPrice, High = successorPrice, Low = successorPrice, Close = successorPrice })) }).AsOf(at);
            }
            switch (kind)
            {
                case "deposit": ledger.Deposit(id, at, Number(e, "amount")); break;
                case "fill": ledger.Fill(id, market, "listing-a", Number(e, "quantity"), Number(e, "price"), PriceBasis.Raw, Number(e, "fee"), Age); break;
                case "split": ledger.ApplySplit(id, market, "listing-a", actions[^1].ActionId); break;
                case "dividend": ledger.CaptureDividend(id, market, "listing-a", dividend!.ActionId, EntitlementEvidence(dividend)); break;
                case "dividend-payment":
                    ledger.PayDividend(id, market, "listing-a", dividend!.ActionId,
                        new(id, "urn:twenty:authored:parity-payment", new string('a', 64), at, at, currency,
                            Number(e, "gross"), Number(e, "withheld"), Number(e, "fee"), currency == "GBP" || Number(e, "gross") == Number(e, "withheld") ? null : "fx"), Age); break;
                case "terminal": ledger.CaptureTerminal(id, market, "listing-a", terminal!.ActionId, TerminalProof(terminal)); break;
                case "terminal-settlement":
                    var stock = terminal!.TerminalOutcome == TerminalOutcome.Stock;
                    var gross = stock ? 0 : Number(e, "gross"); var withheld = stock ? 0 : Number(e, "withheld");
                    ledger.SettleTerminal(id, market, "listing-a", terminal.ActionId,
                        new(id, "urn:twenty:authored:parity-settlement", new string('b', 64), at, at, stock ? null : currency,
                            gross, withheld, Number(e, "fee"), stock || currency == "GBP" || gross == withheld ? null : "fx",
                            stock ? "successor" : null, stock ? Number(e, "quantity") : 0), Age); break;
                case "mark": equity = ledger.Value(market, Age, Age, FxPurpose.Executable).EquityGbp; break;
                default: throw new InvalidOperationException("Unknown shared accounting operation");
            }
            var expected = e.GetProperty("expected");
            Assert.Equal(Number(expected, "cash"), ledger.CashGbp);
            Assert.Equal(Number(expected, "contributions"), ledger.ContributionsGbp);
            Assert.Equal(Number(expected, "fees"), ledger.FeesGbp);
            Assert.Equal(Number(expected, "quantity"), ledger.Holdings.GetValueOrDefault("listing-a")?.Quantity ?? 0);
            Assert.Equal(Number(expected, "successorQuantity"), ledger.Holdings.GetValueOrDefault("successor")?.Quantity ?? 0);
            Assert.Equal(Number(expected, "pendingDividendLocal"), ledger.DividendEntitlements.Where(d => d.PaidAt is null).Sum(d => d.GrossLocal));
            Assert.Equal(Number(expected, "pendingTerminalLocal"), ledger.TerminalEntitlements.Where(t => t.SettledAt is null).Sum(t => t.GrossLocal));
            Assert.Equal(Number(expected, "pendingSuccessorQuantity"), ledger.TerminalEntitlements.Where(t => t.SettledAt is null).Sum(t => t.SuccessorQuantity));
            Assert.Equal(expected.GetProperty("equity").ValueKind == JsonValueKind.Null ? null : (decimal?)Number(expected, "equity"), equity);
        }
    }
}
