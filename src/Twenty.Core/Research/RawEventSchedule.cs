using System.Collections.Immutable;
using System.Security.Cryptography;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawSchedulePoint(string Id, int TieOrder, string DatasetSha256);
public sealed record RawScheduleEvent(RawSchedulePoint Point, DateTimeOffset At, RawReplayOperation Operation);
public abstract record RawActionSchedule(string ListingId, string ActionId, string ObservationId, RawSchedulePoint Capture);
public sealed record RawSplitSchedule(string ListingId, string ActionId, string ObservationId, RawSchedulePoint Capture)
    : RawActionSchedule(ListingId, ActionId, ObservationId, Capture);
public sealed record RawScheduledDividendPayment(RawSchedulePoint Booking, RawDividendPayment Payment);
public sealed record RawDividendSchedule(string ListingId, string ActionId, string ObservationId, RawSchedulePoint Capture,
    RawDividendEvidence Evidence, RawScheduledDividendPayment? Payment)
    : RawActionSchedule(ListingId, ActionId, ObservationId, Capture);
public sealed record RawScheduledTerminalSettlement(RawSchedulePoint Booking, RawTerminalSettlement Settlement);
public sealed record RawTerminalSchedule(string ListingId, string ActionId, string ObservationId, RawSchedulePoint Capture,
    RawTerminalEvidence Evidence, RawScheduledTerminalSettlement? Settlement)
    : RawActionSchedule(ListingId, ActionId, ObservationId, Capture);
public sealed record RawSchedulePlan(string Version, string EngineVersion, RawReplayBuildIdentity Build,
    long MaximumPriceAgeTicks, long MaximumFxAgeTicks, FxPurpose FxPurpose,
    ImmutableArray<RawScheduleEvent> Timeline, ImmutableArray<RawActionSchedule> Actions);
public sealed record RawReplayScheduleBinding(string Version, string PlanSha256, string PlanBase64);

/// <summary>Compiles a declared, source-pinned action catalog into offline replay events.
/// Availability determines booking, not legal entitlement or actual economic settlement time.
/// Ownership, cash and action completeness are checked by executing the compiled replay.</summary>
public static class RawEventSchedule
{
    public const string Version = "raw-event-schedule-v1";
    public const int MaximumPlanBytes = 512 * 1024;
    public const int MaximumActions = 1000;
    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Hash(string? text) => text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200;
    private static DateTimeOffset Later(params DateTimeOffset[] times)
    {
        if (times.Any(t => t == default)) throw new ResearchException("raw-schedule-time");
        return times.Max().ToUniversalTime();
    }

    public static byte[] Compile(ReadOnlySpan<byte> planBytes, Func<string, byte[]> readDataset)
    {
        ArgumentNullException.ThrowIfNull(readDataset);
        if (planBytes.IsEmpty || planBytes.Length > MaximumPlanBytes) throw new ResearchException("raw-schedule-size");
        planBytes = planBytes.ToArray();
        var plan = RawReplayJson.Read<RawSchedulePlan>(planBytes, MaximumPlanBytes);
        if (plan.Version != Version || plan.EngineVersion != RawEventReplay.Version) throw new ResearchException("raw-schedule-version");
        if (plan.Build != RawReplayBuildIdentity.Current) throw new ResearchException("raw-replay-build");
        if (plan.MaximumPriceAgeTicks < 0 || plan.MaximumFxAgeTicks < 0 || !Enum.IsDefined(plan.FxPurpose) ||
            plan.Timeline.IsDefaultOrEmpty || plan.Timeline.Length > 10000 || plan.Actions.IsDefault || plan.Actions.Length > MaximumActions)
            throw new ResearchException("raw-schedule-input");
        var points = new List<RawSchedulePoint>(); var subjects = new HashSet<(string, string)>();
        foreach (var item in plan.Timeline)
        {
            if (item is null || item.At == default || item.Operation is not (RawReplayDeposit or RawReplayAssumedFill or RawReplayModelledFill or RawReplayMark))
                throw new ResearchException("raw-schedule-timeline");
            points.Add(item.Point);
        }
        foreach (var action in plan.Actions)
        {
            if (action is null || !Identifier(action.ListingId) || !Identifier(action.ActionId) || !Identifier(action.ObservationId) ||
                !subjects.Add((action.ListingId, action.ActionId))) throw new ResearchException("raw-schedule-action");
            points.Add(action.Capture);
            switch (action)
            {
                case RawSplitSchedule: break;
                case RawDividendSchedule dividend when dividend.Evidence is not null:
                    if (dividend.Payment is { } payment)
                    {
                        if (payment.Payment is null) throw new ResearchException("raw-schedule-payment");
                        points.Add(payment.Booking);
                    }
                    break;
                case RawTerminalSchedule terminal when terminal.Evidence is not null:
                    if (terminal.Settlement is { } settlement)
                    {
                        if (settlement.Settlement is null) throw new ResearchException("raw-schedule-payment");
                        points.Add(settlement.Booking);
                    }
                    break;
                default: throw new ResearchException("raw-schedule-action");
            }
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (points.Count > 10000) throw new ResearchException("raw-schedule-input");
        foreach (var point in points)
            if (point is null || string.IsNullOrWhiteSpace(point.Id) || point.Id.Length > 128 || point.TieOrder < 0 ||
                !Hash(point.DatasetSha256) || !ids.Add(point.Id)) throw new ResearchException("raw-schedule-point");
        if (points.Select(p => p.DatasetSha256).Distinct(StringComparer.Ordinal).Count() > RawReplayArchive.MaximumDatasets)
            throw new ResearchException("raw-replay-datasets");

        var datasets = new Dictionary<string, (DatasetFile File, PointInTimeDataset Dataset)>(StringComparer.Ordinal);
        long totalBytes = 0, work = 0;
        (DatasetFile File, PointInTimeDataset Dataset) Dataset(string hash)
        {
            if (datasets.TryGetValue(hash, out var cached)) return cached;
            var bytes = readDataset(hash);
            if (bytes is null || bytes.Length > DatasetJson.MaximumBytes || (totalBytes += bytes.Length) > RawReplayArchive.MaximumDatasetBytes)
                throw new ResearchException("raw-replay-datasets");
            bytes = bytes.ToArray();
            if (Digest(bytes) != hash) throw new ResearchException("raw-replay-dataset-hash");
            var file = RawReplayJson.Read<DatasetFile>(bytes, DatasetJson.MaximumBytes);
            var result = (file, new PointInTimeDataset(file)); datasets.Add(hash, result); return result;
        }
        var events = plan.Timeline.Select(e => (e.Point, At: e.At.ToUniversalTime(), e.Operation)).ToList();
        void Add(RawSchedulePoint point, DateTimeOffset at, RawReplayOperation operation) => events.Add((point, at, operation));
        void AfterCapture(RawActionSchedule action, DateTimeOffset captured, RawSchedulePoint payment, DateTimeOffset booked)
        {
            if (booked == captured && payment.TieOrder <= action.Capture.TieOrder)
                throw new ResearchException("raw-schedule-dependency");
        }
        foreach (var entry in plan.Actions)
        {
            var input = Dataset(entry.Capture.DatasetSha256);
            if ((work += input.File.Observations.Length) > RawReplayArchive.MaximumEventObservationWork)
                throw new ResearchException("raw-schedule-work");
            var action = input.File.Observations.OfType<CorporateAction>().SingleOrDefault(a => a.Id == entry.ObservationId &&
                a.ListingId == entry.ListingId && a.ActionId == entry.ActionId) ?? throw new ResearchException("raw-schedule-action-vintage");
            if (action.Evidence.AvailableAt is not { } available) throw new ResearchException("raw-schedule-time");
            var at = Later(action.EffectiveAt, available);
            RawReplayOperation capture;
            switch (entry)
            {
                case RawSplitSchedule when action.Action == CorporateActionKind.Split:
                    capture = new RawReplaySplit(entry.ListingId, entry.ActionId); break;
                case RawDividendSchedule dividend when action.Action == CorporateActionKind.Dividend:
                    if (dividend.Evidence.EntitlementAt != action.EffectiveAt) throw new ResearchException("raw-schedule-cutoff");
                    at = Later(at, dividend.Evidence.AvailableAt);
                    capture = new RawReplayDividendCapture(entry.ListingId, entry.ActionId, dividend.Evidence);
                    if (dividend.Payment is { } delivery)
                    {
                        var payment = delivery.Payment;
                        if (payment.AvailableAt < payment.PaidAt) throw new ResearchException("raw-schedule-payment-time");
                        var booked = Later(at, payment.PaidAt, payment.AvailableAt);
                        AfterCapture(entry, at, delivery.Booking, booked);
                        Add(delivery.Booking, booked, new RawReplayDividendPayment(entry.ListingId, entry.ActionId, payment));
                    }
                    break;
                case RawTerminalSchedule terminal when action.Action is CorporateActionKind.Delisting or CorporateActionKind.Merger:
                    if (terminal.Evidence.EntitlementAt != action.EffectiveAt) throw new ResearchException("raw-schedule-cutoff");
                    at = Later(at, terminal.Evidence.AvailableAt);
                    capture = new RawReplayTerminalCapture(entry.ListingId, entry.ActionId, terminal.Evidence);
                    if (terminal.Settlement is { } terminalDelivery)
                    {
                        if (action.TerminalOutcome == TerminalOutcome.Zero) throw new ResearchException("raw-schedule-zero-settlement");
                        var settlement = terminalDelivery.Settlement;
                        if (settlement.AvailableAt < settlement.SettledAt) throw new ResearchException("raw-schedule-payment-time");
                        var booked = Later(at, settlement.SettledAt, settlement.AvailableAt);
                        AfterCapture(entry, at, terminalDelivery.Booking, booked);
                        Add(terminalDelivery.Booking, booked, new RawReplayTerminalSettlement(entry.ListingId, entry.ActionId, settlement));
                    }
                    break;
                default: throw new ResearchException("raw-schedule-action-kind");
            }
            var selected = input.Dataset.AsOf(at).EffectiveActions(entry.ListingId).SingleOrDefault(a => a.ActionId == entry.ActionId);
            if (selected?.Id != entry.ObservationId) throw new ResearchException("raw-schedule-action-vintage");
            Add(entry.Capture, at, capture);
        }
        var ordered = events.OrderBy(e => e.At).ThenBy(e => e.Point.TieOrder).ToArray();
        if (ordered.GroupBy(e => (e.At, e.Point.TieOrder)).Any(g => g.Count() != 1)) throw new ResearchException("raw-schedule-tie");
        if (ordered[^1].Operation is not RawReplayMark) throw new ResearchException("raw-schedule-final-mark");
        var request = new RawReplayDocument(RawReplayArchive.Version, plan.EngineVersion, plan.Build,
            plan.MaximumPriceAgeTicks, plan.MaximumFxAgeTicks, plan.FxPurpose,
            ordered.Select((e, i) => new RawReplayEventSpec(e.Point.Id, i, e.At, e.Point.DatasetSha256, e.Operation)).ToImmutableArray())
        { Schedule = new(Version, Digest(planBytes), Convert.ToBase64String(planBytes)) };
        return RawReplayJson.Write(request, RawReplayArchive.MaximumRequestBytes);
    }

    internal static void VerifyBinding(RawReplayDocument request, RawReplayScheduleBinding binding, Func<string, byte[]> readDataset)
    {
        if (binding.Version != Version || binding.PlanBase64 is null || binding.PlanBase64.Length > 4L * ((MaximumPlanBytes + 2L) / 3))
            throw new ResearchException("raw-schedule-binding");
        byte[] plan;
        try { plan = Convert.FromBase64String(binding.PlanBase64); }
        catch (FormatException) { throw new ResearchException("raw-schedule-binding"); }
        if (plan.Length > MaximumPlanBytes || Digest(plan) != binding.PlanSha256) throw new ResearchException("raw-schedule-binding");
        var expected = Compile(plan, readDataset);
        if (!expected.AsSpan().SequenceEqual(RawReplayJson.Write(request, RawReplayArchive.MaximumRequestBytes)))
            throw new ResearchException("raw-schedule-binding");
    }
}
