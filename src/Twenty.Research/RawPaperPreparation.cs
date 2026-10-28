using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twenty.Core.Data;
using Twenty.Core.Research;
namespace Twenty.Research;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RawPaperPrepareRequest([property: JsonRequired] string? ExpectedReceiptSha256);
public sealed record RawPreparedPaperBatch(string AccountId, string? ExpectedReceiptSha256, int Period,
    DateTimeOffset DecisionAt, DateTimeOffset NextDecisionAt, string ReplaySha256, int ReplayBytes, RawPaperBalance ProjectedBalance);

public sealed partial class ResearchStore
{
    /// <summary>Prepare one frozen authored batch. No operation or account mutation is admitted here.</summary>
    public RawPreparedPaperBatch PrepareRawPaperBatch(string id, RawPaperPrepareRequest request, CancellationToken ct = default)
    {
        if (request is null || request.ExpectedReceiptSha256 is not null && !IsSha256(request.ExpectedReceiptSha256)) throw new ResearchException("raw-paper-request");
        lock (gate)
        {
            var account = RawPaperAccounts().SingleOrDefault(a => a.Id == id) ?? throw new ResearchException("raw-paper-not-found");
            if (account.Status is not ("ready" or "active")) throw new ResearchException("raw-paper-stopped");
            if (account.HeadReceiptSha256 != request.ExpectedReceiptSha256) throw new ResearchException("raw-paper-head-conflict");
            if (account.CodeSha256 != ResearchIdentity.Current.Sha256 || account.Policy.Build != RawReplayBuildIdentity.Current) throw new ResearchException("study-code-changed");
            if (account.Attempts >= 120) throw new ResearchException("raw-paper-capacity");
            if (!TryBeginComputation()) throw new ResearchException("research-worker-busy");
            try
            {
                ct.ThrowIfCancellationRequested(); EnsureAdditionalArtifactCapacity(RawReplayArchive.MaximumRequestBytes);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
                var token = deadline.Token; var work = BuildPaperBatch(account, token);
                token.ThrowIfCancellationRequested(); var sha = RawHash(work.Bytes);
                var path = Path.Combine(Root, "imports", sha + ".raw-paper.json");
                if (!File.Exists(path)) EnsureAdditionalArtifactCapacity(work.Bytes.Length);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); ImmutableWrite(path, work.Bytes);
                return new(id, account.HeadReceiptSha256, account.AcceptedPeriods + 1, work.At, work.NextAt, sha, work.Bytes.Length, work.Balance);
            }
            catch (OverflowException) { throw new ResearchException("raw-paper-arithmetic"); }
            finally { EndComputation(); }
        }
    }
    private sealed record PreparedPaperWork(byte[] Bytes, RawReplayReceipt Receipt, RawPaperBalance Balance, DateTimeOffset At, DateTimeOffset NextAt);
    private PreparedPaperWork BuildPaperBatch(RawPaperAccount account, CancellationToken token)
    {
        var policy = account.Policy;
        var original = account.Originals.Single(o => o.Kind == "dataset" && o.Sha256 == policy.DatasetSha256);
        var data = PaperReadArtifact(PaperInputPath(original), original.Sha256, DatasetJson.MaximumBytes);
        byte[] Resolve(string sha) => sha == original.Sha256 ? data : throw new ResearchException("raw-paper-dataset");
        var file = DatasetJson.Read(data);
        if (!file.Manifest.Synthetic) throw new ResearchException("raw-paper-authored-only");
        var splits = ValidatePaperActions(file, policy.ActionMode, policy.DatasetSha256);
        var dataset = new PointInTimeDataset(file);
        var calendars = new Dictionary<string, ExecutionCalendar>(StringComparer.Ordinal);
        // Calendar pins hash semantic session tables; original hashes bind their wire bytes.
        foreach (var input in account.Originals.Where(o => o.Kind == "calendar"))
        {
            token.ThrowIfCancellationRequested();
            var calendar = RawStudyEvidence.Calendar(PaperReadArtifact(PaperInputPath(input), input.Sha256, RawStudyEvidence.MaximumBytes));
            if (!policy.Calendars.Any(pin => calendar.Sha256 == pin.Sha256 && calendar.Reference.Id == pin.CalendarId && calendar.Venue == pin.Venue) ||
                !calendars.TryAdd(calendar.Reference.Id, calendar)) throw new ResearchException("raw-paper-calendar");
        }
        if (calendars.Count != policy.Calendars.Length) throw new ResearchException("raw-paper-calendar");
        byte[]? previous = null; RawReplayDocument? prior = null;
        if (account.HeadOperationId is { } head)
        {
            var operation = RawPaperOperations(account.Id).Single(o => o.Id == head);
            previous = PaperReadArtifact(PaperOperationPath(head, "request"), operation.ReplaySha256, RawReplayArchive.MaximumRequestBytes);
            var verified = RawReplayArchive.Verify(RawPaperReceiptBytes(head), Resolve, token);
            if (verified.RequestSha256 != operation.ReplaySha256 || !Convert.FromBase64String(verified.RequestBase64).AsSpan().SequenceEqual(previous) ||
                !RawBytes(Balance(JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(verified.ResultBase64), RawReplayJson.Options)!)).AsSpan().SequenceEqual(RawBytes(account.Balance)))
                throw new ResearchException("raw-paper-integrity");
            prior = JsonSerializer.Deserialize<RawReplayDocument>(previous, RawReplayJson.Options)!;
        }
        if ((prior?.Events.Count(e => e.Operation is RawReplayRebalanceDecision) ?? 0) != account.AcceptedPeriods) throw new ResearchException("raw-paper-integrity");
        var index = RawUniverseDecision.HistoryDecisions - 1 + account.AcceptedPeriods;
        if (index + 1 >= policy.Decisions.Length) throw new ResearchException("raw-paper-capacity");
        var at = policy.Decisions[index]; var next = policy.Decisions[index + 1]; var execution = policy.Execution;
        var priceAge = TimeSpan.FromDays(execution.MaximumPriceAgeDays); var fxAge = TimeSpan.FromDays(execution.MaximumFxAgeDays);
        var ledger = new RawGbpLedger(); long work = 0;
        DataSlice Slice(DateTimeOffset time, long repetitions = 1)
        {
            token.ThrowIfCancellationRequested(); var slice = dataset.AsOf(time);
            if ((work += repetitions * slice.Observations.Length) > RawReplayArchive.MaximumEventObservationWork) throw new ResearchException("raw-replay-work");
            return slice;
        }
        // Rebuild state from already verified operations, never from submitted/stored balances.
        if (prior is not null) foreach (var item in prior.Events)
        {
            token.ThrowIfCancellationRequested();
            switch (item.Operation)
            {
                case RawReplayDeposit d: ledger.Deposit(item.Id, item.At, d.AmountGbp); break;
                case RawReplayModelledFill f:
                    var calendar = prior.Calendars.Single(c => c.Sha256 == f.CalendarSha256);
                    ledger.Fill(item.Id, Slice(item.At), f.Fill.ListingId, f.Fill.Buy ? f.Fill.Quantity : -f.Fill.Quantity,
                        f.Fill.FillPrice, PriceBasis.Raw, f.FeeGbp, fxAge, f.Fill, calendar); break;
                case RawReplayRebalanceDecision: case RawReplayMark: break;
                case RawReplaySplit split when policy.ActionMode == RawPaperContract.SplitMode:
                    ledger.ApplySplit(item.Id, Slice(item.At), split.ListingId, split.ActionId); break;
                default: throw new ResearchException("raw-paper-batch");
            }
        }
        var events = (prior?.Events ?? []).ToBuilder(); var used = events.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        string Add(string kind, DateTimeOffset time, RawReplayOperation operation)
        {
            var stem = "paper-" + account.Id + "-" + (account.AcceptedPeriods + 1) + "-" + kind; var key = stem;
            for (var suffix = 1; !used.Add(key); suffix++) key = stem + "-" + suffix;
            events.Add(new(key, events.Count, time, policy.DatasetSha256, operation)); return key;
        }
        var cursor = 0; long actionWork = 0;
        void Through(DateTimeOffset until, bool pendingBatch = false)
        {
            while (cursor < splits.Length && SplitBooking(splits[cursor]) <= until)
            {
                token.ThrowIfCancellationRequested(); var split = splits[cursor++]; var booked = SplitBooking(split);
                if (prior is not null && booked <= prior.Events[^1].At) continue;
                if ((actionWork += ledger.Entries.Length) > RawReplayArchive.MaximumEventObservationWork) throw new ResearchException("raw-paper-action-work");
                // Even an unheld split may affect an intention about to execute. The complete batch is indivisible.
                if (pendingBatch) throw new ResearchException("raw-paper-action-during-batch");
                decimal quantity = 0;
                foreach (var entry in ledger.Entries.Where(e => e.ListingId == split.ListingId))
                {
                    var cutoff = entry.Kind == "split" ? entry.ActionEffectiveAt : entry.ModelledExecution?.Timing.Session.OpenAt ?? entry.At;
                    if (cutoff < split.EffectiveAt) quantity += entry.Quantity;
                }
                if (quantity < 0) throw new ResearchException("raw-paper-action-history");
                if (quantity == 0) continue;
                var key = "split-" + RawHash(System.Text.Encoding.UTF8.GetBytes(split.Id));
                var actionId = Add(key, booked, new RawReplaySplit(split.ListingId, split.ActionId));
                ledger.ApplySplit(actionId, Slice(booked), split.ListingId, split.ActionId);
            }
        }
        void CheckSplitPrices(DataSlice slice, IEnumerable<string> subjects)
        {
            var ids = subjects.ToHashSet(StringComparer.Ordinal);
            foreach (var group in splits.Where(s => s.SplitRatio != 1 && SplitBooking(s) <= slice.DecisionAt && ids.Contains(s.ListingId)).GroupBy(s => s.ListingId))
            {
                token.ThrowIfCancellationRequested();
                if ((work += slice.Observations.Length) > RawReplayArchive.MaximumEventObservationWork) throw new ResearchException("raw-replay-work");
                var price = slice.Prices(group.Key, PriceBasis.Raw).Where(p => p.SessionCloseAt <= slice.DecisionAt).MaxBy(p => p.SessionCloseAt);
                // Applying a ratio to shares does not convert an older quote to the new share units.
                if (price is null || price.SessionCloseAt < group.Max(s => s.EffectiveAt)) throw new ResearchException("raw-paper-split-price");
            }
        }
        Through(at);
        var market = Slice(at, 3); var depositId = Add("deposit", at, new RawReplayDeposit(policy.MonthlyGbp));
        ledger.Deposit(depositId, at, policy.MonthlyGbp); Add("open", at, new RawReplayMark([], []));
        var spec = new RawUniverseStrategySpec(RawUniverseDecision.Version, policy.UniverseId,
            policy.Decisions.Skip(index - RawUniverseDecision.HistoryDecisions + 1).Take(RawUniverseDecision.HistoryDecisions).ToImmutableArray(), execution.MaximumPriceAgeDays, policy.Config, "momentum");
        foreach (var time in spec.Decisions) _ = Slice(time);
        var allocation = RawUniverseDecision.Allocate(dataset, spec);
        CheckSplitPrices(market, allocation.Weights.Where(w => w.Value > 0).Select(w => w.Key).Concat(ledger.Holdings.Keys));
        var plan = RawRebalancePlanner.Plan(ledger, market, policy.UniverseId, allocation.Weights, next,
            execution.CashReserveGbp, execution.FeePerFillGbp, priceAge, fxAge);
        var decisionId = Add("decision", at, new RawReplayRebalanceDecision(plan) { UniverseStrategy = new(spec, allocation) });
        var tables = (prior is null || prior.Calendars.IsDefault ? ImmutableArray<ExecutionCalendar>.Empty : prior.Calendars).ToDictionary(c => c.Sha256, StringComparer.Ordinal);
        if (!plan.Orders.IsEmpty)
        {
            var listings = market.ListingsAt(at).ToDictionary(l => l.ListingId, StringComparer.Ordinal);
            var ids = plan.Orders.Select(o => listings[o.ListingId].CalendarId).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length != 1 || !calendars.TryGetValue(ids[0], out var calendar)) throw new ResearchException("raw-paper-calendar");
            var session = calendar.Next(at, next) ?? throw new ResearchException("raw-paper-execution-refused");
            var later = Slice(next); var booked = session.CloseAt!.Value;
            foreach (var order in plan.Orders)
            {
                var bar = later.Prices(order.ListingId, PriceBasis.Raw).SingleOrDefault(b => b.SessionDate == session.Date && b.SessionCloseAt == session.CloseAt);
                if (bar?.Evidence.AvailableAt is not { } available) throw new ResearchException("raw-paper-execution-refused");
                if (available > booked) booked = available;
            }
            if (booked > next) throw new ResearchException("raw-paper-execution-refused");
            Through(booked, pendingBatch: true);
            var fillMarket = Slice(booked, plan.Orders.Length);
            var fills = ExecutionPlanner.Plan(fillMarket, plan.Orders, execution.Fills, calendar, fxAge);
            if (!fills.Rejections.IsEmpty || fills.Fills.Length != plan.Orders.Length) throw new ResearchException("raw-paper-execution-refused");
            tables[calendar.Sha256] = calendar;
            for (var j = 0; j < fills.Fills.Length; j++)
            {
                var fill = fills.Fills[j];
                var fillId = Add("fill-" + j, booked, new RawReplayModelledFill(plan.Orders[j], execution.Fills, fill, execution.FeePerFillGbp, null)
                    { Rebalance = new(decisionId, j), CalendarSha256 = calendar.Sha256 });
                ledger.Fill(fillId, fillMarket, fill.ListingId, fill.Buy ? fill.Quantity : -fill.Quantity, fill.FillPrice,
                    PriceBasis.Raw, execution.FeePerFillGbp, fxAge, fill, calendar);
            }
        }
        Through(next); var closing = Slice(next); CheckSplitPrices(closing, ledger.Holdings.Keys); Add("close", next, new RawReplayMark([], []));
        var document = new RawReplayDocument(RawReplayArchive.Version, RawEventReplay.Version, policy.Build,
            priceAge.Ticks, fxAge.Ticks, FxPurpose.Executable, events.ToImmutable())
            { Calendars = tables.Values.OrderBy(c => c.Sha256, StringComparer.Ordinal).ToImmutableArray() };
        var bytes = RawBytes(document); _ = RawPaperContract.ValidateAppend(policy, previous, bytes);
        // The independent archive checks the generated intentions, fills and entire prefix again.
        var receipt = RawReplayArchive.Execute(bytes, Resolve, token);
        if (RawBytes(receipt).Length > 8 * 1024 * 1024) throw new ResearchException("raw-paper-capacity");
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
        return new(bytes, receipt, Balance(result), at, next);
    }

}
