using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record ExecutionDay(DateOnly Date, DateTimeOffset? OpenAt, DateTimeOffset? CloseAt);

/// <summary>Bounded retrospective sessions, including closed dates. The source adapter supplies
/// the facts; matching the dataset reference does not assert historical announcement availability.</summary>
public sealed class ExecutionCalendar
{
    public CalendarReference Reference { get; }
    public string Venue { get; }
    public string SourceReference { get; }
    public ImmutableArray<ExecutionDay> Days { get; }
    public string Sha256 { get; }
    private readonly TimeZoneInfo zone;

    public ExecutionCalendar(CalendarReference reference, string venue, ImmutableArray<ExecutionDay> days,
        string sourceReference = "urn:twenty:authored-sessions")
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.Id) || string.IsNullOrWhiteSpace(reference.Version) ||
            !Hash(reference.ContentSha256) || string.IsNullOrWhiteSpace(venue) || venue.Length > 20 ||
            string.IsNullOrWhiteSpace(sourceReference) || sourceReference.Length > 500 || days.IsDefaultOrEmpty || days.Length > 10000)
            throw new ResearchException("invalid-execution-calendar");
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(reference.TimeZone); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new ResearchException("invalid-execution-calendar"); }
        for (var i = 0; i < days.Length; i++)
        {
            var day = days[i];
            if (day is null || day.Date == default || (i > 0 && day.Date.DayNumber != days[i - 1].Date.DayNumber + 1) ||
                day.OpenAt.HasValue != day.CloseAt.HasValue ||
                day.OpenAt is { } open && (open == default || day.CloseAt <= open ||
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(open, zone).DateTime) != day.Date ||
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(day.CloseAt!.Value, zone).DateTime) != day.Date))
                throw new ResearchException("invalid-execution-calendar");
        }
        Reference = reference; Venue = venue; SourceReference = sourceReference;
        Days = days.Select(d => d with { OpenAt = d.OpenAt?.ToUniversalTime(), CloseAt = d.CloseAt?.ToUniversalTime() }).ToImmutableArray();
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Reference, Venue, SourceReference, Days })));
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public ExecutionDay? Next(DateTimeOffset decisionAt, DateTimeOffset deadline)
    {
        if (decisionAt == default || deadline <= decisionAt) throw new ResearchException("execution-calendar-time");
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(decisionAt, zone).DateTime);
        if (date < Days[0].Date || date > Days[^1].Date) throw new ResearchException("execution-calendar-range");
        var next = Days.FirstOrDefault(d => d.OpenAt > decisionAt);
        if (next is not null) return next.OpenAt <= deadline ? next : null;
        if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(deadline, zone).DateTime) > Days[^1].Date)
            throw new ResearchException("execution-calendar-range");
        return null;
    }
}
