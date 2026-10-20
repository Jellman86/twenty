using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class NotionalPortfolioTests
{
    [Fact]
    public void TerminalCashProceedsMoveToCashAndDoNotBreakTheNextStep()
    {
        var portfolio = new NotionalPortfolio();
        // Hold one name at 100 GBP, then it cash-delists at +90% proceeds in period 0.
        portfolio.Step(100m, new Dictionary<string, decimal> { ["aaa"] = 1m },
            new Dictionary<string, decimal> { ["aaa"] = 0.9m }, new ResearchCosts(), terminalExits: new HashSet<string> { "aaa" });
        Assert.DoesNotContain("aaa", portfolio.Holdings.Keys);
        Assert.True(portfolio.Cash > 100m);
        // Period 1: aaa has left the universe, so no return entry. Before the fix this threw
        // invalid-portfolio-step; now the realized proceeds sit in cash and the step succeeds.
        var step = portfolio.Step(0m, new Dictionary<string, decimal>(), new Dictionary<string, decimal>(), new ResearchCosts());
        Assert.True(step.EquityGbp >= portfolio.Cash);
    }

    private static readonly ResearchCosts Free = new(0m, 0m, 0m, 0m, 0m);

    [Fact]
    public void StockExchangeCarriesSuccessorExposureAndOnlyLaterSalePaysCosts()
    {
        var portfolio = new NotionalPortfolio();
        // 100 / 20 = 5 original shares; 0.5 exchange ratio delivers 2.5 successor
        // shares marked at 50 = 125. The ledger carries their exposure, not cash.
        var exchange = portfolio.Step(100m, new Dictionary<string, decimal> { ["old"] = 1m },
            new Dictionary<string, decimal> { ["old"] = 0.25m }, Free,
            stockSuccessors: new Dictionary<string, string> { ["old"] = "new" });
        Assert.Equal(125m, portfolio.Holdings["new"]);
        Assert.DoesNotContain("old", portfolio.Holdings.Keys);
        Assert.Equal(0m, exchange.CashGbp);
        Assert.Equal(0m, exchange.PeriodCostGbp);
        Assert.Single(exchange.Trades); // original buy only
        Assert.Equal(new NotionalStockTransfer("old", "new", 125m), Assert.Single(exchange.StockTransfers));
        var retain = portfolio.Step(0m, new Dictionary<string, decimal> { ["new"] = 1m },
            new Dictionary<string, decimal> { ["new"] = 0.2m }, Free);
        Assert.Equal(150m, retain.EquityGbp); // 2.5 * 60; successor return applied once
        Assert.Empty(retain.Trades);
        var sell = portfolio.Step(0m, new Dictionary<string, decimal>(),
            new Dictionary<string, decimal> { ["new"] = 0m }, new ResearchCosts(0m, 100m, 0m, 0m, 0m));
        Assert.Equal(new NotionalTrade("new", "sell", 150m, 1.5m), Assert.Single(sell.Trades));
        Assert.Equal(148.5m, sell.CashGbp);
        Assert.Empty(portfolio.Holdings);
        Assert.Equal(100m, portfolio.Contributions);
        Assert.Equal(1.5m, portfolio.CostsPaid);
    }

    [Fact]
    public void MultipleOriginalsMergeIntoAnExistingSuccessorAfterItsOwnReturn()
    {
        var portfolio = new NotionalPortfolio();
        var step = portfolio.Step(100m,
            new Dictionary<string, decimal> { ["a"] = 0.2m, ["b"] = 0.3m, ["new"] = 0.5m },
            new Dictionary<string, decimal> { ["a"] = 0.25m, ["b"] = -0.5m, ["new"] = 0.1m }, Free,
            stockSuccessors: new Dictionary<string, string> { ["a"] = "new", ["b"] = "new" });
        // Existing 50 * 1.1 + delivered 20 * 1.25 + delivered 30 * 0.5 = 95.
        Assert.Equal(95m, portfolio.Holdings["new"]);
        Assert.Single(portfolio.Holdings);
        Assert.Equal(0m, portfolio.Cash);
        Assert.Equal(2, step.StockTransfers.Length);
        Assert.Equal(40m, step.StockTransfers.Sum(t => t.NotionalGbp));
        Assert.Equal(-0.05m, step.PeriodReturn);
    }

    [Theory]
    [InlineData("a", "a", false)] // self exchange
    [InlineData("a", "b", true)] // overlap with a cash terminal exit
    [InlineData("missing", "b", false)] // no period return to value original
    public void InvalidStockTransfersLeaveLedgerUnchanged(string original, string successor, bool cashExit)
    {
        var portfolio = new NotionalPortfolio();
        portfolio.Step(100m, new Dictionary<string, decimal> { ["a"] = 1m },
            new Dictionary<string, decimal> { ["a"] = 0m }, Free);
        var error = Assert.Throws<ResearchException>(() => portfolio.Step(20m,
            new Dictionary<string, decimal> { ["a"] = 1m }, new Dictionary<string, decimal> { ["a"] = 0m }, Free,
            terminalExits: cashExit ? new HashSet<string> { "a" } : null,
            stockSuccessors: new Dictionary<string, string> { [original] = successor }));
        Assert.Equal("invalid-stock-settlement", error.Code);
        Assert.Equal(100m, portfolio.Equity);
        Assert.Equal(100m, portfolio.Contributions);
        Assert.Equal(100m, portfolio.Holdings["a"]);
    }

    [Fact]
    public void SamePeriodChainedExchangesAreRefusedBeforeMutation()
    {
        var portfolio = new NotionalPortfolio();
        Assert.Throws<ResearchException>(() => portfolio.Step(100m,
            new Dictionary<string, decimal> { ["a"] = 1m },
            new Dictionary<string, decimal> { ["a"] = 0m, ["b"] = 0m }, Free,
            stockSuccessors: new Dictionary<string, string> { ["a"] = "b", ["b"] = "c" }));
        Assert.Equal(0m, portfolio.Equity);
        Assert.Equal(0m, portfolio.Contributions);
    }
}
