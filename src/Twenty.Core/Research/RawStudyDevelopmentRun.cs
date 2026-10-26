using System.Collections.Immutable;
using System.Security.Cryptography;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawStudyActionDisposition(string EventId, string ListingId, string ActionId, DateTimeOffset At, string Disposition);
public sealed record RawReplayStudyBinding(string Version, string PlanSha256, string PlanBase64, string TrialId, string Strategy)
{
    public string? ActionCatalogBase64 { get; init; }
    public ImmutableArray<RawStudyActionDisposition> Dispositions { get; init; }
}
public sealed record RawStudyMonth(DateTimeOffset DecisionAt, DateTimeOffset EndAt, decimal ContributionGbp,
    decimal OpeningEquityAfterDepositGbp, decimal EndingEquityGbp, decimal CashGbp, decimal PeriodReturn);
public sealed record RawStudyStrategyReport(string Strategy, decimal EndingEquityGbp, decimal ContributionsGbp,
    decimal FeesGbp, ImmutableArray<RawStudyMonth> Months, RawReplayReceipt Receipt);
public sealed record RawStudyDevelopmentReport(string Version, string PlanSha256, string TrialId, string DatasetSha256,
    ImmutableArray<RawStudyStrategyReport> Strategies, ImmutableArray<string> Limitations);

public static class RawStudyDevelopmentRun
{
    public const string Version = "raw-study-development-v3";
    public const int MaximumReportBytes = 8 * 1024 * 1024;
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static readonly string[] Strategies = ["momentum", "equal_weight", "cash"];
    public static byte[] EncodeReport(RawStudyDevelopmentReport report) => RawReplayJson.Write(report, MaximumReportBytes);
    private static readonly ImmutableArray<string> Limitations = ["Registered-development calculation only; development selection is a separate recorded step. No final evaluation, investment nomination or broker execution is granted.",
             "Independent empty momentum/equal-weight/cash accounts receive the exact frozen deposits; all orders in each bound batch must execute or the trial fails.",
             "Original catalogs freeze pure splits and per-cutoff-share dividend/terminal policies with explicit ownership dispositions. Unsettled claims at marks, mixed consideration and cross-calendar batches are refused; future final-window actions do not gate development.",
             "Raw GBP marks use executable-purpose historical FX labels and supplied reviewed sessions; same-open funding, fractional quantities and retrospective volume screens remain model assumptions."];
    private static void Scope(DatasetFile file, RawUniverseStudyPlan plan)
    {
        if (file.Manifest.Synthetic != plan.Synthetic || !file.Manifest.Universes.Any(u => u.Id == plan.UniverseId) ||
            plan.DevelopmentEnd > file.Manifest.CreatedAt) throw new ResearchException("raw-study-dataset-contract");

    }
    public static RawStudyDevelopmentReport Run(RawUniverseStudyPlan plan, string trialId, byte[] datasetBytes,
        IReadOnlyDictionary<string, ExecutionCalendar> calendars, CancellationToken ct = default, Action? checkpoint = null) =>
        Run(plan, trialId, datasetBytes, calendars, "[]"u8.ToArray(), ct, checkpoint);

    public static RawStudyDevelopmentReport Run(RawUniverseStudyPlan plan, string trialId, byte[] datasetBytes,
        IReadOnlyDictionary<string, ExecutionCalendar> calendars, byte[] actionCatalogBytes, CancellationToken ct = default, Action? checkpoint = null)
    {
        _ = RawUniverseStudyProtocol.Encode(plan);
        datasetBytes = datasetBytes.ToArray(); actionCatalogBytes = actionCatalogBytes.ToArray();
        var file = ReadDataset(plan, datasetBytes); var dataset = new PointInTimeDataset(file);
        var pinned = calendars.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var pin in plan.Calendars)
            if (!pinned.TryGetValue(pin.CalendarId, out var calendar) || calendar.Sha256 != pin.Sha256 || calendar.Venue != pin.Venue ||
                !file.Manifest.Calendars.Contains(calendar.Reference)) throw new ResearchException("raw-study-calendar-binding");
        var catalog = RawStudyEvidence.Actions(actionCatalogBytes); var actions = StageActions(plan, file, datasetBytes, actionCatalogBytes, catalog);
        var reports = ImmutableArray.CreateBuilder<RawStudyStrategyReport>();
        foreach (var strategy in Strategies)
        {
            var request = BuildRequest(plan, trialId, strategy, file, dataset, pinned, actionCatalogBytes, catalog, actions, ct, checkpoint);
            var receipt = RawReplayArchive.Execute(RawReplayJson.Write(request, RawReplayArchive.MaximumRequestBytes), _ => datasetBytes, ct);
            reports.Add(Summary(strategy, receipt));
        }
        return new(Version, Hash(RawUniverseStudyProtocol.Encode(plan)), trialId, plan.DatasetSha256, reports.ToImmutable(), Limitations);
    }
    private static DatasetFile ReadDataset(RawUniverseStudyPlan plan, byte[] bytes)
    {
        if (bytes.Length > DatasetJson.MaximumBytes || Hash(bytes) != plan.DatasetSha256) throw new ResearchException("raw-replay-dataset-hash");
        var file = RawReplayJson.Read<DatasetFile>(bytes, DatasetJson.MaximumBytes); _ = new PointInTimeDataset(file); Scope(file, plan); return file;
    }
    private static ImmutableArray<RawReplayEventSpec> StageActions(RawUniverseStudyPlan plan, DatasetFile file, byte[] datasetBytes,
        byte[] actionBytes, ImmutableArray<RawActionSchedule> catalog)
    {
        if (Hash(actionBytes) != plan.ActionCatalogSha256) throw new ResearchException("raw-study-action-catalog");
        if (catalog.Select(c => (c.ListingId, c.ActionId)).Distinct().Count() != catalog.Length) throw new ResearchException("raw-study-actions");
        var required = file.Observations.OfType<CorporateAction>().Where(a => a.Action != CorporateActionKind.SymbolChange &&
            a.EffectiveAt >= plan.DevelopmentStart && a.EffectiveAt <= plan.DevelopmentEnd && a.Evidence.AvailableAt <= plan.DevelopmentEnd)
            .Select(a => (a.ListingId, a.ActionId)).Distinct().ToArray();
        if (required.Length > RawEventSchedule.MaximumActions || (long)required.Length * file.Observations.Length > RawReplayArchive.MaximumEventObservationWork)
            throw new ResearchException("raw-schedule-work");
        var selected = ImmutableArray.CreateBuilder<RawActionSchedule>();
        foreach (var subject in required)
        {
            var entry = catalog.SingleOrDefault(c => c.ListingId == subject.ListingId && c.ActionId == subject.ActionId);
            if (entry is null) throw new ResearchException("raw-study-action-coverage");
            if (entry.Capture is null || string.IsNullOrWhiteSpace(entry.Capture.Id)) throw new ResearchException("raw-study-action-binding");
            var source = file.Observations.OfType<CorporateAction>().SingleOrDefault(a => a.Id == entry.ObservationId &&
                a.ListingId == entry.ListingId && a.ActionId == entry.ActionId);
            if (source is null || source.Evidence.AvailableAt is not { } available || source.EffectiveAt < plan.DevelopmentStart ||
                source.EffectiveAt > plan.DevelopmentEnd || available > plan.DevelopmentEnd) throw new ResearchException("raw-study-action-binding");
            RawStudyActionTerms.Validate(entry, source, plan.DatasetSha256);
            selected.Add(RawStudyActionTerms.ForCompiler(entry, plan.DevelopmentEnd));
        }
        if (selected.Count == 0) return [];
        var schedule = new RawSchedulePlan(RawEventSchedule.Version, RawEventReplay.Version, plan.Build,
            TimeSpan.FromDays(plan.BaseExecution.MaximumPriceAgeDays).Ticks, TimeSpan.FromDays(plan.BaseExecution.MaximumFxAgeDays).Ticks, FxPurpose.Executable,
            [new(new("study-action-boundary", int.MaxValue, plan.DatasetSha256), plan.DevelopmentEnd, new RawReplayMark([], []))], selected.ToImmutable());
        var compiled = RawEventSchedule.Compile(RawReplayJson.Write(schedule, RawEventSchedule.MaximumPlanBytes), _ => datasetBytes);
        return RawReplayJson.Read<RawReplayDocument>(compiled, RawReplayArchive.MaximumRequestBytes).Events.Where(e => e.Id != "study-action-boundary").ToImmutableArray();
    }
    private static RawReplayDocument BuildRequest(RawUniverseStudyPlan plan, string trialId, string strategy, DatasetFile file,
        PointInTimeDataset dataset, IReadOnlyDictionary<string, ExecutionCalendar> pinned, byte[] actionCatalogBytes,
        ImmutableArray<RawActionSchedule> catalog, ImmutableArray<RawReplayEventSpec> actions, CancellationToken ct, Action? checkpoint = null)
    {
        var encoded = RawUniverseStudyProtocol.Encode(plan); var planSha = Hash(encoded);
        var policy = RawUniverseStudyProtocol.DevelopmentDecision(plan, trialId, plan.DevelopmentStart).Execution;
        var priceAge = TimeSpan.FromDays(policy.MaximumPriceAgeDays); var fxAge = TimeSpan.FromDays(policy.MaximumFxAgeDays);
        var ledger = new RawGbpLedger(); var events = ImmutableArray.CreateBuilder<RawReplayEventSpec>();
        long observationWork = 0;
        void Charge(long count)
        {
            ct.ThrowIfCancellationRequested();
            if ((observationWork += count) > RawReplayArchive.MaximumEventObservationWork) throw new ResearchException("raw-replay-work");
        }
        void Add(string id, DateTimeOffset at, RawReplayOperation op)
        {
            if (events.Count >= 10000) throw new ResearchException("raw-replay-input");
            events.Add(new(id, events.Count, at.ToUniversalTime(), plan.DatasetSha256, op));
        }
        var dispositions = ImmutableArray.CreateBuilder<RawStudyActionDisposition>(); var cursor = 0; long actionWork = 0;
        var captured = new HashSet<(string, string)>();
        decimal OwnedBefore(string listing, DateTimeOffset cutoff)
        {
            decimal quantity = 0;
            foreach (var entry in ledger.Entries)
            {
                if (entry.TerminalSettlement is { SuccessorListingId: { } successor } delivered && successor == listing && delivered.SettledAt < cutoff)
                    quantity += delivered.SuccessorQuantity;
                if (entry.ListingId != listing || entry.Quantity == 0) continue;
                var at = entry.Kind is "split" or "terminal-entitlement" ? entry.ActionEffectiveAt : entry.ModelledExecution?.Timing.Session.OpenAt ?? entry.At;
                if (at < cutoff) quantity += entry.Quantity;
            }
            return quantity;
        }
        void Through(DateTimeOffset until, bool pendingBatch = false)
        {
            while (cursor < actions.Length && actions[cursor].At <= until)
            {
                ct.ThrowIfCancellationRequested(); var item = actions[cursor++];
                if ((actionWork += ledger.Entries.Length) > RawReplayArchive.MaximumEventObservationWork) throw new ResearchException("raw-study-action-work");
                var (listing, action) = item.Operation switch
                {
                    RawReplaySplit a => (a.ListingId,a.ActionId), RawReplayDividendCapture a => (a.ListingId,a.ActionId),
                    RawReplayDividendPayment a => (a.ListingId,a.ActionId), RawReplayTerminalCapture a => (a.ListingId,a.ActionId),
                    RawReplayTerminalSettlement a => (a.ListingId,a.ActionId), _ => throw new ResearchException("raw-study-action-binding")
                };
                var instruction = catalog.Single(c => c.ListingId == listing && c.ActionId == action);
                var observed = file.Observations.OfType<CorporateAction>().Single(a => a.Id == instruction.ObservationId);
                var capture = item.Operation is RawReplaySplit or RawReplayDividendCapture or RawReplayTerminalCapture;
                var owned = capture ? OwnedBefore(listing, observed.EffectiveAt) : 0;
                if (owned < 0) throw new ResearchException("raw-study-action-history");
                var applies = capture ? owned > 0 : captured.Contains((listing,action));
                var disposition = capture ? "not-held-before-cutoff" : "no-entitlement";
                if (applies)
                {
                    if (pendingBatch) throw new ResearchException("raw-study-action-during-batch");
                    var market = dataset.AsOf(item.At); Charge(market.Observations.Length);
                    RawReplayOperation actual = item.Operation;
                    switch (item.Operation)
                    {
                        case RawReplaySplit split:
                            ledger.ApplySplit(item.Id,market,listing,action); disposition = "applied-split"; break;
                        case RawReplayDividendCapture dividend:
                            ledger.CaptureDividend(item.Id,market,listing,action,dividend.Evidence); disposition = "captured-dividend"; break;
                        case RawReplayDividendPayment dividend:
                            var quantity = ledger.DividendEntitlements.Single(e => e.ListingId == listing && e.ActionId == action).Quantity;
                            var payment = dividend.Payment with { GrossLocal = dividend.Payment.GrossLocal * quantity, WithheldLocal = dividend.Payment.WithheldLocal * quantity };
                            ledger.PayDividend(item.Id,market,listing,action,payment,fxAge); actual = new RawReplayDividendPayment(listing,action,payment);
                            disposition = "paid-dividend"; break;
                        case RawReplayTerminalCapture terminal:
                            ledger.CaptureTerminal(item.Id,market,listing,action,terminal.Evidence); disposition = "captured-terminal"; break;
                        case RawReplayTerminalSettlement terminal:
                            var shares = ledger.TerminalEntitlements.Single(e => e.Action.ListingId == listing && e.Action.ActionId == action).Quantity;
                            var settlement = terminal.Settlement with { GrossLocal = terminal.Settlement.GrossLocal * shares,
                                WithheldLocal = terminal.Settlement.WithheldLocal * shares, SuccessorQuantity = terminal.Settlement.SuccessorQuantity * shares };
                            ledger.SettleTerminal(item.Id,market,listing,action,settlement,fxAge); actual = new RawReplayTerminalSettlement(listing,action,settlement);
                            disposition = "settled-terminal"; break;
                    }
                    Add(item.Id,item.At,actual);
                    if (capture) captured.Add((listing,action));
                }
                dispositions.Add(new(item.Id,listing,action,item.At,disposition));
            }
        }
        for (var i = 0; i < plan.DevelopmentContributions.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); checkpoint?.Invoke(); ct.ThrowIfCancellationRequested();
            var instruction = RawUniverseStudyProtocol.DevelopmentDecision(plan, trialId, plan.DevelopmentContributions[i].At);
            var at = instruction.Contribution.At; Through(at); var market = dataset.AsOf(at);
            Charge(2L * market.Observations.Length); // Deposit and decision, including repeated slices.
            ledger.Deposit("deposit-" + i, at, instruction.Contribution.AmountGbp);
            Add("deposit-" + i, at, new RawReplayDeposit(instruction.Contribution.AmountGbp));
            var spec = instruction.Strategies.Single(s => s.Strategy == strategy);
            foreach (var historicalAt in spec.Decisions) Charge(dataset.AsOf(historicalAt).Observations.Length);
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
                Through(booked, pendingBatch: true);
                var fillMarket = dataset.AsOf(booked);
                Charge((long)decision.Orders.Length * fillMarket.Observations.Length);
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
            Through(instruction.NextDecisionAt);
            var markMarket = dataset.AsOf(instruction.NextDecisionAt); Charge(markMarket.Observations.Length);
            _ = ledger.Value(markMarket, priceAge, fxAge, FxPurpose.Executable);
            Add("mark-" + i, instruction.NextDecisionAt, new RawReplayMark([], []));
        }
        ct.ThrowIfCancellationRequested(); checkpoint?.Invoke(); ct.ThrowIfCancellationRequested();
        return new RawReplayDocument(RawReplayArchive.Version, RawEventReplay.Version, plan.Build,
            priceAge.Ticks, fxAge.Ticks, FxPurpose.Executable, events.ToImmutable())
            { Study = new(Version, planSha, Convert.ToBase64String(encoded), trialId, strategy)
              { ActionCatalogBase64 = Convert.ToBase64String(actionCatalogBytes), Dispositions = dispositions.ToImmutable() },
              Calendars = pinned.Values.Where(c => events.Any(e => e.Operation is RawReplayModelledFill f && f.CalendarSha256 == c.Sha256))
                  .OrderBy(c => c.Sha256, StringComparer.Ordinal).ToImmutableArray() };
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
        if (binding.Dispositions.IsDefault || binding.Dispositions.Length > 2 * RawEventSchedule.MaximumActions || binding.Dispositions.Any(d => d is null) ||
            binding.ActionCatalogBase64 is null || binding.ActionCatalogBase64.Length > 4L * ((RawStudyEvidence.MaximumBytes + 2L) / 3))
            throw new ResearchException("raw-study-replay-binding");
        byte[] actionBytes;
        try { actionBytes = Convert.FromBase64String(binding.ActionCatalogBase64); }
        catch (FormatException) { throw new ResearchException("raw-study-replay-binding"); }
        var datasetBytes = readDataset(plan.DatasetSha256)?.ToArray() ?? throw new ResearchException("raw-replay-dataset-hash");
        var file = ReadDataset(plan, datasetBytes); var catalog = RawStudyEvidence.Actions(actionBytes);
        var actions = StageActions(plan, file, datasetBytes, actionBytes, catalog);
        var tables = request.Calendars.IsDefault ? ImmutableArray<ExecutionCalendar>.Empty : request.Calendars;
        if (tables.Select(c => c.Reference.Id).Distinct(StringComparer.Ordinal).Count() != tables.Length)
            throw new ResearchException("raw-study-calendar-binding");
        var calendars = tables.ToDictionary(c => c.Reference.Id, StringComparer.Ordinal);
        foreach (var calendar in calendars.Values)
            if (!plan.Calendars.Any(p => p.CalendarId == calendar.Reference.Id && p.Venue == calendar.Venue && p.Sha256 == calendar.Sha256) ||
                !file.Manifest.Calendars.Contains(calendar.Reference)) throw new ResearchException("raw-study-calendar-binding");
        var expected = BuildRequest(plan, binding.TrialId, binding.Strategy, file, new PointInTimeDataset(file), calendars,
            actionBytes, catalog, actions, ct);
        if (!RawReplayJson.Write(request, RawReplayArchive.MaximumRequestBytes).AsSpan()
            .SequenceEqual(RawReplayJson.Write(expected, RawReplayArchive.MaximumRequestBytes))) throw new ResearchException("raw-study-replay-binding");
    }
}
