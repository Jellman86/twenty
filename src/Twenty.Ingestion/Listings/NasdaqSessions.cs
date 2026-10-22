using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Ingestion.Listings;

/// <summary>Bounded Nasdaq cash-equity sessions, curated from official annual calendars
/// and superseding notices. This is retrospective session metadata, not evidence that a
/// future closure was known to a trading decision. See docs/nasdaq-sessions.md.</summary>
public static class NasdaqSessions
{
    public const string Version = "xnas-equities-2015-2026-v1";
    public const string Venue = "XNAS";
    public const string TimeZone = "America/New_York";
    public const int FirstYear = 2015;
    public const int LastYear = 2026;
    private sealed record CalendarYear(int Year, string Source, ImmutableArray<DateOnly> Closed,
        ImmutableArray<DateOnly> EarlyCloses);
    private sealed record CalendarOverride(DateOnly Date, string Reason, string Source);
    private sealed record CalendarFile(string Version, string Venue, string TimeZone,
        ImmutableArray<CalendarYear> Years, ImmutableArray<CalendarOverride> Overrides);
    private static readonly FrozenSet<DateOnly> Closed;
    private static readonly FrozenSet<DateOnly> EarlyCloses;
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
    public static string ContentSha256 { get; }

    static NasdaqSessions()
    {
        using var resource = typeof(NasdaqSessions).Assembly.GetManifestResourceStream(
            "Twenty.Ingestion.Listings.nasdaq-equity-sessions.v1.json")
            ?? throw new InvalidOperationException("The reviewed Nasdaq session table is missing.");
        using var buffer = new MemoryStream(); resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var file = JsonSerializer.Deserialize<CalendarFile>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidOperationException("The Nasdaq session table is invalid.");
        if (file.Version != Version || file.Venue != Venue || file.TimeZone != TimeZone ||
            !file.Years.Select(y => y.Year).SequenceEqual(Enumerable.Range(FirstYear, LastYear - FirstYear + 1)) ||
            file.Years.Any(y => y.Closed.IsDefault || y.EarlyCloses.IsDefault ||
                y.Closed.Concat(y.EarlyCloses).Any(d => d.Year != y.Year || d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) ||
                y.Closed.Concat(y.EarlyCloses).Distinct().Count() != y.Closed.Length + y.EarlyCloses.Length))
            throw new InvalidOperationException("The Nasdaq session table failed its integrity rules.");
        Closed = file.Years.SelectMany(y => y.Closed).ToFrozenSet();
        EarlyCloses = file.Years.SelectMany(y => y.EarlyCloses).ToFrozenSet();
        if (file.Overrides.IsDefault || file.Overrides.Any(o => !Closed.Contains(o.Date)))
            throw new InvalidOperationException("The Nasdaq closure notices are not reflected in the session table.");
        ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Null for a weekend/closed day; unsupported years fail rather than invent sessions.</summary>
    public static DateTimeOffset? CloseAt(DateOnly date)
    {
        if (date.Year is < FirstYear or > LastYear)
            throw new ResearchException("price-conversion-calendar-range");
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || Closed.Contains(date)) return null;
        var local = date.ToDateTime(new TimeOnly(EarlyCloses.Contains(date) ? 13 : 16, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    public static CalendarReference Reference(string id) => new(id, TimeZone, Version, ContentSha256);
    public static ExecutionCalendar ExecutionCalendar(string id, DateOnly from, DateOnly through)
    {
        if (from > through || from.Year < FirstYear || through.Year > LastYear)
            throw new ResearchException("execution-calendar-range");
        var days = ImmutableArray.CreateBuilder<ExecutionDay>();
        for (var day = from; day <= through; day = day.AddDays(1))
        {
            var close = CloseAt(day);
            var local = day.ToDateTime(new TimeOnly(9, 30), DateTimeKind.Unspecified);
            days.Add(new(day, close is null ? null : new DateTimeOffset(local, Zone.GetUtcOffset(local)), close));
        }
        return new(Reference(id), Venue, days.ToImmutable(), "https://listingcenter.nasdaq.com/rulebook/nasdaq/rules/nasdaq-equity-1");
    }

}
