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
}
