using System.Collections.Immutable;
using System.Security.Cryptography;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawReplayStudyBinding(string Version, string PlanSha256, string PlanBase64, string TrialId, string Strategy);
public sealed record RawStudyMonth(DateTimeOffset DecisionAt, DateTimeOffset EndAt, decimal ContributionGbp,
    decimal OpeningEquityAfterDepositGbp, decimal EndingEquityGbp, decimal CashGbp, decimal PeriodReturn);
public sealed record RawStudyStrategyReport(string Strategy, decimal EndingEquityGbp, decimal ContributionsGbp,
    decimal FeesGbp, ImmutableArray<RawStudyMonth> Months, RawReplayReceipt Receipt);
public sealed record RawStudyDevelopmentReport(string Version, string PlanSha256, string TrialId, string DatasetSha256,
    ImmutableArray<RawStudyStrategyReport> Strategies, ImmutableArray<string> Limitations);

public static class RawStudyDevelopmentRun
{
    public const string Version = "raw-study-development-v1";
    public const int MaximumReportBytes = 8 * 1024 * 1024;
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static readonly string[] Strategies = ["momentum", "equal_weight", "cash"];
    public static byte[] EncodeReport(RawStudyDevelopmentReport report) => RawReplayJson.Write(report, MaximumReportBytes);
    private static readonly ImmutableArray<string> Limitations = ["Registered-development calculation only; no final evaluation, selection, investment nomination or broker execution is granted.",
             "Independent empty momentum/equal-weight/cash accounts receive the exact frozen deposits; all orders in each bound batch must execute or the trial fails.",
             "Economic actions known within the evaluated development window and simultaneous cross-calendar order batches are explicitly unsupported. Future final-window actions do not gate development.",
             "Raw GBP marks use executable-purpose historical FX labels and supplied reviewed sessions; same-open funding, fractional quantities and retrospective volume screens remain model assumptions."];
    private static void Scope(DatasetFile file, RawUniverseStudyPlan plan)
    {
        if (file.Manifest.Synthetic != plan.Synthetic || !file.Manifest.Universes.Any(u => u.Id == plan.UniverseId) ||
            plan.DevelopmentEnd > file.Manifest.CreatedAt) throw new ResearchException("raw-study-dataset-contract");
        if (file.Observations.OfType<CorporateAction>().Any(a => a.Action != CorporateActionKind.SymbolChange &&
            a.EffectiveAt >= plan.DevelopmentStart && a.EffectiveAt <= plan.DevelopmentEnd && a.Evidence.AvailableAt <= plan.DevelopmentEnd))
            throw new ResearchException("raw-study-actions-unsupported");
    }
    public static RawStudyDevelopmentReport Run(RawUniverseStudyPlan plan, string trialId, byte[] datasetBytes,
        IReadOnlyDictionary<string, ExecutionCalendar> calendars, CancellationToken ct = default, Action? checkpoint = null)
    {
        var encoded = RawUniverseStudyProtocol.Encode(plan); var planSha = Hash(encoded);
        datasetBytes = datasetBytes.ToArray();
        if (datasetBytes.Length > DatasetJson.MaximumBytes || Hash(datasetBytes) != plan.DatasetSha256)
            throw new ResearchException("raw-replay-dataset-hash");
        var file = RawReplayJson.Read<DatasetFile>(datasetBytes, DatasetJson.MaximumBytes); var dataset = new PointInTimeDataset(file); Scope(file, plan);
        var pinned = calendars.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var pin in plan.Calendars)
            if (!pinned.TryGetValue(pin.CalendarId, out var calendar) || calendar.Sha256 != pin.Sha256 || calendar.Venue != pin.Venue ||
                !file.Manifest.Calendars.Contains(calendar.Reference)) throw new ResearchException("raw-study-calendar-binding");
        var first = RawUniverseStudyProtocol.DevelopmentDecision(plan, trialId, plan.DevelopmentStart);
        var policy = first.Execution; var priceAge = TimeSpan.FromDays(policy.MaximumPriceAgeDays); var fxAge = TimeSpan.FromDays(policy.MaximumFxAgeDays);
        var reports = ImmutableArray.CreateBuilder<RawStudyStrategyReport>();
        foreach (var strategy in Strategies)
        {
            var ledger = new RawGbpLedger(); var events = ImmutableArray.CreateBuilder<RawReplayEventSpec>();
            void Add(string id, DateTimeOffset at, RawReplayOperation op)
            {
                if (events.Count >= 10000) throw new ResearchException("raw-replay-input");
                events.Add(new(id, events.Count, at.ToUniversalTime(), plan.DatasetSha256, op));
            }
            for (var i = 0; i < plan.DevelopmentContributions.Length; i++)
            {
                ct.ThrowIfCancellationRequested(); checkpoint?.Invoke(); ct.ThrowIfCancellationRequested();
                var instruction = RawUniverseStudyProtocol.DevelopmentDecision(plan, trialId, plan.DevelopmentContributions[i].At);
                var at = instruction.Contribution.At; var market = dataset.AsOf(at);
                ledger.Deposit("deposit-" + i, at, instruction.Contribution.AmountGbp);
                Add("deposit-" + i, at, new RawReplayDeposit(instruction.Contribution.AmountGbp));
                var spec = instruction.Strategies.Single(s => s.Strategy == strategy);
                var allocation = RawUniverseDecision.Allocate(dataset, spec);
                var decision = RawRebalancePlanner.Plan(ledger, market, plan.UniverseId, allocation.Weights, instruction.NextDecisionAt,
                    policy.CashReserveGbp, policy.FeePerFillGbp, priceAge, fxAge);
                Add("decision-" + i, at, new RawReplayRebalanceDecision(decision) { UniverseStrategy = new(spec, allocation) });
                if (!decision.Orders.IsEmpty)
                {
                    var listings = market.ListingsAt(at).ToDictionary(l => l.ListingId, StringComparer.Ordinal);
                    var ids = decision.Orders.Select(o => listings[o.ListingId].CalendarId).Distinct(StringComparer.Ordinal).ToArray();
                    if (ids.Length != 1) throw new ResearchException("raw-study-cross-calendar-batch");
                    if (!pinned.TryGetValue(ids[0], out var calendar)) throw new ResearchException("raw-study-calendar-binding");
                    var session = calendar.Next(at, instruction.NextDecisionAt);
                    if (session is null) throw new ResearchException("raw-study-execution-refused");
                    var later = dataset.AsOf(instruction.NextDecisionAt); var booked = session.CloseAt!.Value;
                    foreach (var order in decision.Orders)
                    {
                        var bar = later.Prices(order.ListingId, PriceBasis.Raw).SingleOrDefault(b => b.SessionDate == session.Date && b.SessionCloseAt == session.CloseAt);
                        if (bar?.Evidence.AvailableAt is not { } available) throw new ResearchException("raw-study-execution-refused");
                        if (available > booked) booked = available;
                    }
                    if (booked > instruction.NextDecisionAt) throw new ResearchException("raw-study-execution-refused");
                    var fillMarket = dataset.AsOf(booked);
                    var execution = ExecutionPlanner.Plan(fillMarket, decision.Orders, policy.Fills, calendar, fxAge);
                    if (!execution.Rejections.IsEmpty || execution.Fills.Length != decision.Orders.Length) throw new ResearchException("raw-study-execution-refused");
                    for (var j = 0; j < execution.Fills.Length; j++)
                    {
                        ct.ThrowIfCancellationRequested(); var fill = execution.Fills[j]; var id = "fill-" + i + "-" + j;
                        ledger.Fill(id, fillMarket, fill.ListingId, fill.Buy ? fill.Quantity : -fill.Quantity, fill.FillPrice, PriceBasis.Raw,
                            policy.FeePerFillGbp, fxAge, fill, calendar);
                        Add(id, booked, new RawReplayModelledFill(decision.Orders[j], policy.Fills, fill, policy.FeePerFillGbp, null)
                            { Rebalance = new("decision-" + i, j), CalendarSha256 = calendar.Sha256 });
                    }
                }
                _ = ledger.Value(dataset.AsOf(instruction.NextDecisionAt), priceAge, fxAge, FxPurpose.Executable);
                Add("mark-" + i, instruction.NextDecisionAt, new RawReplayMark([], []));
            }
            ct.ThrowIfCancellationRequested(); checkpoint?.Invoke(); ct.ThrowIfCancellationRequested();
            var request = new RawReplayDocument(RawReplayArchive.Version, RawEventReplay.Version, plan.Build,
                priceAge.Ticks, fxAge.Ticks, FxPurpose.Executable, events.ToImmutable())
                { Study = new(Version, planSha, Convert.ToBase64String(encoded), trialId, strategy),
                  Calendars = pinned.Values.Where(c => events.Any(e => e.Operation is RawReplayModelledFill f && f.CalendarSha256 == c.Sha256))
                      .OrderBy(c => c.Sha256, StringComparer.Ordinal).ToImmutableArray() };
            var receipt = RawReplayArchive.Execute(RawReplayJson.Write(request, RawReplayArchive.MaximumRequestBytes), _ => datasetBytes, ct);
            reports.Add(Summary(strategy, receipt));
        }
        return new(Version, planSha, trialId, plan.DatasetSha256, reports.ToImmutable(),
            Limitations);
    }
    private static RawStudyStrategyReport Summary(string strategy, RawReplayReceipt receipt)
    {
        var request = RawReplayJson.Read<RawReplayDocument>(Convert.FromBase64String(receipt.RequestBase64), RawReplayArchive.MaximumRequestBytes);
        var result = RawReplayJson.Read<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayArchive.MaximumResultBytes);
        var decisions = request.Events.Where(e => e.Operation is RawReplayRebalanceDecision).ToArray();
        var months = decisions.Select((e, i) =>
        {
            var decision = (RawReplayRebalanceDecision)e.Operation; var mark = result.Marks.Single(m => m.Id == "mark-" + i).Valuation;
            var deposit = (RawReplayDeposit)request.Events.Single(x => x.Id == "deposit-" + i).Operation;
            var opening = decision.Plan.Valuation.EquityGbp;
            if (opening <= 0) throw new ResearchException("raw-study-opening-equity");
            return new RawStudyMonth(e.At, decision.Plan.NextDecisionAt, deposit.AmountGbp, opening, mark.ExpectedEquityGbp,
                mark.HoldingsAndCash.CashGbp, mark.ExpectedEquityGbp / opening - 1m);
        }).ToImmutableArray();
        return new(strategy, result.Marks[^1].Valuation.ExpectedEquityGbp, result.ContributionsGbp, result.FeesGbp, months, receipt);
    }
    public static RawStudyDevelopmentReport Verify(ReadOnlySpan<byte> reportBytes, Func<string, byte[]> readDataset, CancellationToken ct = default)
    {
        var report = RawReplayJson.Read<RawStudyDevelopmentReport>(reportBytes, MaximumReportBytes);
        if (report.Version != Version || report.Limitations.IsDefault || !report.Limitations.SequenceEqual(Limitations) ||
            report.Strategies.IsDefault || report.Strategies.Any(s => s is null) || !report.Strategies.Select(s => s.Strategy).SequenceEqual(Strategies))
            throw new ResearchException("raw-study-report-binding");
        string? original = null;
        foreach (var strategy in report.Strategies)
        {
            ct.ThrowIfCancellationRequested();
            var receipt = RawReplayArchive.Verify(RawReplayJson.Write(strategy.Receipt, RawReplayArchive.MaximumReceiptBytes), readDataset, ct);
            var request = RawReplayJson.Read<RawReplayDocument>(Convert.FromBase64String(receipt.RequestBase64), RawReplayArchive.MaximumRequestBytes);
            var binding = request.Study;
            if (binding is null || binding.PlanSha256 != report.PlanSha256 || binding.TrialId != report.TrialId || binding.Strategy != strategy.Strategy ||
                original is not null && original != binding.PlanBase64 || request.Events.Any(e => e.DatasetSha256 != report.DatasetSha256))
                throw new ResearchException("raw-study-report-binding");
            original = binding.PlanBase64;
            if (!RawReplayJson.Write(strategy, MaximumReportBytes).AsSpan().SequenceEqual(RawReplayJson.Write(Summary(strategy.Strategy, receipt), MaximumReportBytes)))
                throw new ResearchException("raw-study-report-binding");
        }
        return report;
    }
    internal static void VerifyBinding(RawReplayDocument request, RawReplayStudyBinding binding, Func<string, byte[]> readDataset, CancellationToken ct)
    {
        if (binding.Version != Version || request.Schedule is not null || !Strategies.Contains(binding.Strategy) || binding.PlanBase64 is null ||
            binding.PlanBase64.Length > 4L * ((RawUniverseStudyProtocol.MaximumPlanBytes + 2L) / 3)) throw new ResearchException("raw-study-replay-binding");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(binding.PlanBase64); }
        catch (FormatException) { throw new ResearchException("raw-study-replay-binding"); }
        if (Hash(bytes) != binding.PlanSha256) throw new ResearchException("raw-study-replay-binding");
        var plan = RawUniverseStudyProtocol.Decode(bytes);
        var policy = RawUniverseStudyProtocol.DevelopmentDecision(plan, binding.TrialId, plan.DevelopmentStart).Execution;
        if (request.MaximumPriceAgeTicks != TimeSpan.FromDays(policy.MaximumPriceAgeDays).Ticks ||
            request.MaximumFxAgeTicks != TimeSpan.FromDays(policy.MaximumFxAgeDays).Ticks || request.FxPurpose != FxPurpose.Executable ||
            request.Events.Any(e => e.DatasetSha256 != plan.DatasetSha256)) throw new ResearchException("raw-study-replay-binding");
        var dataset = readDataset(plan.DatasetSha256)?.ToArray();
        if (dataset is null || dataset.Length > DatasetJson.MaximumBytes || Hash(dataset) != plan.DatasetSha256) throw new ResearchException("raw-replay-dataset-hash");
        var file = RawReplayJson.Read<DatasetFile>(dataset, DatasetJson.MaximumBytes); _ = new PointInTimeDataset(file); Scope(file, plan);
        var cursor = 0;
        RawReplayEventSpec Take(string id)
        {
            if (cursor >= request.Events.Length || request.Events[cursor].Id != id) throw new ResearchException("raw-study-replay-binding");
            return request.Events[cursor++];
        }
        for (var i = 0; i < plan.DevelopmentContributions.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); var contribution = plan.DevelopmentContributions[i];
            var instruction = RawUniverseStudyProtocol.DevelopmentDecision(plan, binding.TrialId, contribution.At);
            var deposit = Take("deposit-" + i);
            if (deposit.At != contribution.At || deposit.Operation is not RawReplayDeposit d || d.AmountGbp != contribution.AmountGbp)
                throw new ResearchException("raw-study-replay-binding");
            var entry = Take("decision-" + i);
            if (entry.At != contribution.At || entry.Operation is not RawReplayRebalanceDecision decision || decision.UniverseStrategy is not { } strategy ||
                !RawReplayJson.Write(strategy.Spec, MaximumReportBytes).AsSpan().SequenceEqual(RawReplayJson.Write(instruction.Strategies.Single(s => s.Strategy == binding.Strategy), MaximumReportBytes)) ||
                decision.Plan.NextDecisionAt != instruction.NextDecisionAt || decision.Plan.CashReserveGbp != policy.CashReserveGbp ||
                decision.Plan.FeePerFillGbp != policy.FeePerFillGbp || decision.Plan.MaximumPriceAgeTicks != request.MaximumPriceAgeTicks ||
                decision.Plan.MaximumFxAgeTicks != request.MaximumFxAgeTicks || decision.Plan.Orders.IsDefault)
                throw new ResearchException("raw-study-replay-binding");
            var batch = new List<RawReplayEventSpec>();
            for (var j = 0; j < decision.Plan.Orders.Length; j++)
            {
                var fill = Take("fill-" + i + "-" + j);
                if (fill.At <= contribution.At || fill.At > instruction.NextDecisionAt || fill.Operation is not RawReplayModelledFill f ||
                    f.Calendar is null || f.Fill is null || f.Fill.PriceEvidence is null || f.Policy != policy.Fills || f.FeeGbp != policy.FeePerFillGbp || f.Rebalance != new RawRebalanceOrderReference(entry.Id, j) ||
                    !plan.Calendars.Any(p => p.CalendarId == f.Calendar.Reference.Id && p.Venue == f.Calendar.Venue && p.Sha256 == f.Calendar.Sha256))
                    throw new ResearchException("raw-study-replay-binding");
                batch.Add(fill);
            }
            if (batch.Count > 0)
            {
                var booked = batch.Max(e =>
                {
                    var f = (RawReplayModelledFill)e.Operation;
                    return f.Fill.PriceEvidence.AvailableAt > f.Fill.FillSessionCloseAt ? f.Fill.PriceEvidence.AvailableAt : f.Fill.FillSessionCloseAt;
                });
                if (batch.Any(e => e.At != booked) || batch.Select(e => ((RawReplayModelledFill)e.Operation).Calendar!.Sha256).Distinct().Count() != 1)
                    throw new ResearchException("raw-study-replay-binding");
            }
            var mark = Take("mark-" + i);
            if (mark.At != instruction.NextDecisionAt || mark.Operation is not RawReplayMark m || m.Dividends.IsDefault || m.Terminals.IsDefault ||
                !m.Dividends.IsEmpty || !m.Terminals.IsEmpty) throw new ResearchException("raw-study-replay-binding");
        }
        if (cursor != request.Events.Length) throw new ResearchException("raw-study-replay-binding");
    }
}
