using Twenty.Core.Research;
using Twenty.Ingestion.Listings;

namespace Twenty.Tests;

public sealed class NasdaqSessionsTests
{
    [Theory]
    [InlineData("2015-01-02", "2015-01-02T21:00:00Z")]
    [InlineData("2020-03-06", "2020-03-06T21:00:00Z")]
    [InlineData("2020-03-09", "2020-03-09T20:00:00Z")]
    [InlineData("2024-11-29", "2024-11-29T18:00:00Z")]
    [InlineData("2025-07-03", "2025-07-03T17:00:00Z")]
    [InlineData("2026-12-24", "2026-12-24T18:00:00Z")]
    [InlineData("2015-07-02", "2015-07-02T20:00:00Z")]
    [InlineData("2016-07-01", "2016-07-01T20:00:00Z")]
    [InlineData("2020-07-02", "2020-07-02T20:00:00Z")]
    [InlineData("2021-06-18", "2021-06-18T20:00:00Z")]
    [InlineData("2021-12-31", "2021-12-31T21:00:00Z")]
    public void OfficialRegularAndEarlyClosesHaveDstCorrectInstants(string day, string close)
        => Assert.Equal(DateTimeOffset.Parse(close), NasdaqSessions.CloseAt(DateOnly.Parse(day)));

    [Theory]
    [InlineData("2015-04-03")]
    [InlineData("2018-12-05")]
    [InlineData("2022-06-20")]
    [InlineData("2025-01-09")]
    [InlineData("2026-07-03")]
    [InlineData("2026-01-03")]
    [InlineData("2026-01-04")]
    public void HolidaysMourningDaysAndWeekendsHaveNoSession(string day)
        => Assert.Null(NasdaqSessions.CloseAt(DateOnly.Parse(day)));

    [Theory]
    [InlineData("2014-12-31")]
    [InlineData("2027-01-04")]
    public void UnsupportedYearsAreNotExtrapolated(string day)
    {
        var error = Assert.Throws<ResearchException>(() => NasdaqSessions.CloseAt(DateOnly.Parse(day)));
        Assert.Equal("price-conversion-calendar-range", error.Code);
    }

    [Theory]
    [InlineData(2015, 252, 2)]
    [InlineData(2016, 252, 1)]
    [InlineData(2017, 251, 2)]
    [InlineData(2018, 251, 3)]
    [InlineData(2019, 252, 3)]
    [InlineData(2020, 253, 2)]
    [InlineData(2021, 252, 1)]
    [InlineData(2022, 251, 1)]
    [InlineData(2023, 250, 2)]
    [InlineData(2024, 252, 3)]
    [InlineData(2025, 250, 3)]
    [InlineData(2026, 251, 2)]
    public void AnnualSessionAndEarlyCloseTotalsMatchReviewedTables(int year, int count, int early)
    {
        var dates = Enumerable.Range(0, DateTime.IsLeapYear(year) ? 366 : 365)
            .Select(d => new DateOnly(year, 1, 1).AddDays(d));
        var closes = dates.Select(NasdaqSessions.CloseAt).Where(c => c.HasValue).Select(c => c!.Value).ToArray();
        Assert.Equal(count, closes.Length);
        Assert.Equal(early, closes.Count(c => c.Hour == 13));
    }

    [Fact]
    public void CalendarReferenceBindsTheEmbeddedTable()
    {
        var reference = NasdaqSessions.Reference("ny-calendar");
        Assert.Equal("ny-calendar", reference.Id);
        Assert.Equal(NasdaqSessions.Version, reference.Version);
        Assert.Equal("America/New_York", reference.TimeZone);
        Assert.Equal(64, reference.ContentSha256.Length);
        Assert.Equal(NasdaqSessions.ContentSha256, reference.ContentSha256);
    }
}
