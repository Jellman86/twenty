using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Twenty.Core.Research;

namespace Twenty.Research;

// Require all financial assumptions on the wire; do not silently take FillPolicy defaults.
public sealed record RawStudyPlanExecution(
    [property: JsonRequired] int MaximumPriceAgeDays, [property: JsonRequired] int MaximumFxAgeDays,
    [property: JsonRequired] decimal CashReserveGbp, [property: JsonRequired] decimal FeePerFillGbp,
    [property: JsonRequired] decimal HalfSpreadBps, [property: JsonRequired] decimal SlippageBps,
    [property: JsonRequired] decimal MinimumOrderValue, [property: JsonRequired] decimal MaximumParticipation,
    [property: JsonRequired] decimal QuantityStep);
public sealed record RawStudyPlanPreparationRequest(
    [property: JsonRequired] string DatasetSha256, [property: JsonRequired] string UniverseId,
    [property: JsonRequired] bool Synthetic, [property: JsonRequired] string Purpose,
    [property: JsonRequired] DateOnly FirstMonth, [property: JsonRequired] int DecisionCount,
    [property: JsonRequired] TimeOnly DecisionTimeUtc, [property: JsonRequired] decimal MonthlyGbp,
    [property: JsonRequired] RawStudyPlanExecution Execution,
    [property: JsonRequired] ImmutableArray<RawStudyCalendarPin> Calendars,
    [property: JsonRequired] string ActionCatalogSha256);
public sealed record RawPreparedStudyPlan(string PlanSha256, string PlanBase64);

public sealed partial class ResearchStore
{
    public RawPreparedStudyPlan PrepareRawStudyPlan(RawStudyPlanPreparationRequest request)
    {
        if (request is null || request.Execution is null || request.FirstMonth == default || request.FirstMonth.Day != 1 ||
            request.DecisionCount is < 2 or > 600)
            throw new ResearchException("raw-study-plan-request");
        ImmutableArray<DateTimeOffset> decisions;
        try
        {
            decisions = Enumerable.Range(0, request.DecisionCount).Select(i =>
            {
                var month = request.FirstMonth.AddMonths(i);
                var end = new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
                return new DateTimeOffset(end.ToDateTime(request.DecisionTimeUtc, DateTimeKind.Utc));
            }).ToImmutableArray();
        }
        catch (ArgumentOutOfRangeException) { throw new ResearchException("raw-study-plan-request"); }
        var e = request.Execution;
        var plan = RawUniverseStudyProtocol.Declare(request.DatasetSha256, request.UniverseId, request.Synthetic,
            request.Purpose, ResearchIdentity.Current.Sha256, ResearchIdentity.Scheme, decisions, request.MonthlyGbp,
            new(e.MaximumPriceAgeDays, e.MaximumFxAgeDays, e.CashReserveGbp, e.FeePerFillGbp,
                new(e.HalfSpreadBps, e.SlippageBps, e.MinimumOrderValue, e.MaximumParticipation, e.QuantityStep)),
            request.Calendars, request.ActionCatalogSha256);
        var bytes = RawUniverseStudyProtocol.Encode(plan);
        var sha = RawHash(bytes);
        lock (gate)
        {
            var directory = Path.Combine(Root, "imports");
            var path = Path.Combine(directory, sha + ".raw-study.json");
            if (!File.Exists(path))
            {
                if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.raw-study.json").Take(100).Count() >= 100)
                    throw new ResearchException("raw-study-plan-capacity");
                EnsureAdditionalArtifactCapacity(bytes.Length);
                Directory.CreateDirectory(directory);
            }
            ImmutableWrite(path, bytes);
        }
        return new(sha, Convert.ToBase64String(bytes));
    }
}
