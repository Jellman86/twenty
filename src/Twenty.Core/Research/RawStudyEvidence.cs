using System.Collections.Immutable;

namespace Twenty.Core.Research;

/// <summary>Strict framing for original offline admission inputs, without calculating returns.</summary>
public static class RawStudyEvidence
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public static ExecutionCalendar Calendar(ReadOnlySpan<byte> bytes) => RawReplayJson.Read<ExecutionCalendar>(bytes, MaximumBytes);
    public static ImmutableArray<RawActionSchedule> Actions(ReadOnlySpan<byte> bytes)
    {
        var actions = RawReplayJson.Read<ImmutableArray<RawActionSchedule>>(bytes, MaximumBytes);
        if (actions.IsDefault || actions.Length > RawEventSchedule.MaximumActions || actions.Any(a => a is null))
            throw new ResearchException("raw-study-actions");
        return actions;
    }
}
