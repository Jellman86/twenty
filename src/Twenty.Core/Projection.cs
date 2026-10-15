namespace Twenty.Core;

public sealed record ProjectionRequest(decimal Monthly = 20, int Years = 30, double AnnualReturn = 0.07, decimal Target = 1_000_000);
public sealed record ProjectionPoint(int Year, decimal Contributions, decimal Balance);
public sealed record ProjectionResult(ProjectionRequest Assumptions, IReadOnlyList<ProjectionPoint> Points, double? RequiredAnnualReturn);

/// <summary>Constant nominal return scenarios with end-of-month deposits. Not forecasts.</summary>
public static class Projection
{
    public static ProjectionResult Calculate(ProjectionRequest input)
    {
        if (input.Monthly is < 1 or > 100_000 || input.Years is < 1 or > 60 ||
            !double.IsFinite(input.AnnualReturn) || input.AnnualReturn is < -0.99 or > 1 ||
            input.Target is < 1 or > 100_000_000)
            throw new ArgumentException("Use £1–£100,000 monthly, 1–60 years, −99%–100% annual return and a £1–£100m target.");
        var points = new List<ProjectionPoint> { new(0, 0, 0) };
        var monthlyGrowth = Math.Pow(1 + input.AnnualReturn, 1.0 / 12);
        double balance = 0;
        for (var month = 1; month <= input.Years * 12; month++)
        {
            balance = balance * monthlyGrowth + (double)input.Monthly;
            if (month % 12 == 0)
                points.Add(new(month / 12, input.Monthly * month, decimal.Round((decimal)balance, 2)));
        }
        return new(input, points, RequiredReturn((double)input.Monthly, input.Years, (double)input.Target));
    }

    public static double FutureValue(double monthly, int years, double annualReturn)
    {
        var growth = Math.Pow(1 + annualReturn, 1.0 / 12);
        double balance = 0;
        for (var month = 0; month < years * 12; month++) balance = balance * growth + monthly;
        return balance;
    }

    public static double? RequiredReturn(double monthly, int years, double target)
    {
        double low = -0.999999, high = 1;
        while (FutureValue(monthly, years, high) < target)
        {
            high *= 2;
            if (high > 1024) return null;
        }
        for (var i = 0; i < 120; i++)
        {
            var mid = (low + high) / 2;
            if (FutureValue(monthly, years, mid) < target) low = mid;
            else high = mid;
        }
        return (low + high) / 2;
    }
}
