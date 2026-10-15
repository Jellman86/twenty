using System.Text.Json;
using Twenty.Core;

namespace Twenty.Tests;
public class ProjectionTests
{
    [Fact]
    public void MatchesIndependentPythonFixtures()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "projections.json"));
        var rows = JsonSerializer.Deserialize<JsonElement[]>(file)!;
        foreach (var row in rows)
        {
            var monthly = row.GetProperty("monthly").GetDecimal();
            var years = row.GetProperty("years").GetInt32();
            var rate = row.GetProperty("annualReturn").GetDouble();
            var expected = row.GetProperty("balance").GetDouble();
            var actual = Projection.Calculate(new(monthly, years, rate));
            Assert.InRange(Math.Abs((double)actual.Points[^1].Balance - expected), 0, Math.Max(0.011, Math.Abs(expected) * 1e-11));
            if (row.GetProperty("requiredReturn").ValueKind == JsonValueKind.Null) Assert.Null(actual.RequiredAnnualReturn);
            else Assert.InRange(Math.Abs(actual.RequiredAnnualReturn!.Value - row.GetProperty("requiredReturn").GetDouble()), 0, 1e-9);
            Assert.Equal(monthly * years * 12, actual.Points[^1].Contributions);
        }
    }

    [Fact]
    public void DepositsAtMonthEndAndDoesNotAccrueBeforeFirstDeposit()
    {
        var result = Projection.Calculate(new(20, 1, 1));
        var monthlyGrowth = Math.Pow(2, 1.0 / 12);
        var expected = 20 * (Math.Pow(monthlyGrowth, 12) - 1) / (monthlyGrowth - 1);
        Assert.InRange(Math.Abs((double)result.Points[^1].Balance - expected), 0, 0.005);
        Assert.Equal(new ProjectionPoint(0, 0, 0), result.Points[0]);
    }

    [Fact]
    public void ImpossibleTargetBelowLastDepositHasNoSupportedRoot() =>
        Assert.Null(Projection.Calculate(new(20, 30, 0.07, 1)).RequiredAnnualReturn);

    [Theory]
    [InlineData(0, 30, .07)]
    [InlineData(20, 0, .07)]
    [InlineData(20, 61, .07)]
    [InlineData(20, 30, -1)]
    [InlineData(20, 30, double.NaN)]
    [InlineData(20, 30, double.PositiveInfinity)]
    public void RejectsUnsupportedInputs(double monthly, int years, double rate) =>
        Assert.Throws<ArgumentException>(() => Projection.Calculate(new((decimal)monthly, years, rate)));
}
