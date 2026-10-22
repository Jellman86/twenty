using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record ExecutionSessionEvidence(CalendarReference Calendar, string Venue, string SourceReference,
    string ScheduleSha256, ExecutionDay Session);

internal static class ExecutionFx
{
    public static bool TrySelect(DataSlice market, string currency, string? id, DateTimeOffset executedAt,
        TimeSpan maximumAge, out FxRate? rate)
    {
        rate = null;
        if (currency == "GBP") return id is null;
        var candidate = market.Observations.OfType<FxRate>().SingleOrDefault(f => f.Id == id);
        if (candidate is null || candidate.BaseCurrency != currency || candidate.QuoteCurrency != "GBP" ||
            candidate.Purpose != FxPurpose.Executable || candidate.ObservedAt > executedAt ||
            candidate.Evidence.AvailableAt is not { } available || available > executedAt ||
            executedAt - candidate.ObservedAt > maximumAge) return false;
        rate = candidate;
        return true;
    }
}
