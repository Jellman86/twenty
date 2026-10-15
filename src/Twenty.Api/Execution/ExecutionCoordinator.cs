using System.Collections.Immutable;
using System.Security.Cryptography;
using Twenty.Api.Research;
using Twenty.Core.Execution;
using Twenty.Core.Research;

namespace Twenty.Api.Execution;

public sealed record ExecutionCycleResult(string Status, string? AccountKey, string? SignalId, int Submitted, string? Code);
public sealed class ExecutionCoordinator
{
    private readonly BrokerLedger ledger;
    private readonly SignalStore signals;
    private readonly ResearchStore research;
    private readonly IBrokerGateway? broker;
    private readonly Func<ExecutionPolicy?> policySource;
    private readonly Func<ExecutionPolicy, ExecutionQuotes> quoteSource;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim cycle = new(1, 1);
    private readonly BrokerEvidenceArchive quoteArchive;
    public string CodeSha256 { get; }
    public ExecutionCoordinator(BrokerLedger ledger, SignalStore signals, ResearchStore research, IBrokerGateway? broker,
        Func<ExecutionPolicy?> policySource, Func<ExecutionPolicy, ExecutionQuotes>? quoteSource = null, TimeProvider? clock = null)
    {
        this.ledger = ledger; this.signals = signals; this.research = research; this.broker = broker; this.policySource = policySource;
        this.quoteSource = quoteSource ?? (p => ExecutionQuotes.Load(p.QuotesFile)); this.clock = clock ?? TimeProvider.System;
        quoteArchive = new BrokerEvidenceArchive(research.Root);
        var core = File.ReadAllBytes(typeof(Backtest).Assembly.Location); var host = File.ReadAllBytes(typeof(ExecutionCoordinator).Assembly.Location);
        CodeSha256 = Convert.ToHexStringLower(SHA256.HashData(core.Concat(host).ToArray()));
    }
    public ExecutionPolicy? Policy() => policySource();
    private ExecutionPolicy RequirePolicy()
    {
        var policy = policySource() ?? throw new ResearchException("execution-private-configuration-required"); policy.Validate();
        if (!policy.Enabled) throw new ResearchException("execution-disabled");
        if (broker is null) throw new ResearchException("broker-credentials-missing");
        if (broker.Environment != policy.BrokerEnvironment) throw new ResearchException("execution-environment-mismatch");
        var run = research.Find(policy.RunId) ?? throw new ResearchException("run-not-found");
        if (run.Status != "succeeded" || run.ReportSha256 != policy.ReportSha256 || run.Config.MonthlyGbp != policy.MonthlyGbp)
            throw new ResearchException("execution-strategy-binding");
        if (run.Config.AllocationPolicy != "contribution-only") throw new ResearchException("execution-backtest-policy-mismatch");
        if (policy.Environment == "live" && (run.Synthetic || policy.SyntheticEvidence)) throw new ResearchException("execution-synthetic-live");
        return policy;
    }
    public async Task<ExecutionCycleResult> Run(CancellationToken ct)
    {
        if (!await cycle.WaitAsync(0, ct)) return new("busy", null, null, 0, "execution-cycle-running");
        ExecutionPolicy? policy = null; string? signalId = null; var submitted = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2)); ct = deadline.Token;
        try
        {
            policy = RequirePolicy();
            var summary = await VerifiedSummary(policy, ct);
            var positions = await broker!.Positions(ct);
            var pending = await broker.PendingOrders(ct);
            var history = await broker.History(ct);
            var account = ledger.BindAccount(policy, positions);
            foreach (var intent in account.Intents.Where(i => i.BrokerOrderId is not null)) ledger.Reconcile(account.Key, intent.Id, pending, history, policy.WalletRule);
            account = ledger.Find(account.Key)!;
            if (account.IntegrityBlockCode is { } integrityCode) throw new ResearchException(integrityCode);
            if (account.Intents.Any(i => i.Phase is "unknown" or "submitting")) throw new ResearchException("broker-submission-unknown");
            if (account.Intents.Any(i => i.Progress.DebitGbp > i.Progress.ReservedGbp) || account.CashGbp < 0) throw new ResearchException("broker-reserve-breach");
            var quotes = quoteSource(policy); quotes.Validate(clock.GetUtcNow(), policy.Environment == "live");
            quoteArchive.Record(policy.BrokerEnvironment, "SNAPSHOT", "execution-quotes", null, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(quotes, ResearchStore.Json), true);
            ValidatePositions(account, positions, quotes, pending);
            ledger.Block(account.Key, null);
            ledger.RecordDemoReceipt(policy, CodeSha256, positions);
            if (ledger.Paused) return new("paused", account.Key, null, 0, null); // Reconciliation still runs while submission is paused.
            if (policy.Environment == "live" && !ledger.DemoReceipts().Any(r => !r.Synthetic && r.StrategyReportSha256 == policy.ReportSha256 &&
                r.ExecutionCodeSha256 == CodeSha256 && r.ExecutionContractSha256 == policy.ContractHash && r.RulesSha256 == quotes.ContractHash))
                throw new ResearchException("execution-genuine-demo-required");
            var now = clock.GetUtcNow(); var month = now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            if (!account.Batches.Any(b => b.Month == month) && account.Intents.All(BrokerLedger.Terminal))
            {
                var signal = signals.Create(new(policy.RunId, policy.DatasetId)); signalId = signal.Id;
                if (policy.Environment == "live" && signal.Synthetic) throw new ResearchException("execution-synthetic-live");
                var expected = signal.Predictions.Select(p => p.Symbol).ToHashSet(StringComparer.Ordinal);
                if (quotes.Items.Any(q => !expected.Contains(q.Rule.Symbol)) || expected.Any(s => quotes.Items.All(q => q.Rule.Symbol != s)))
                    throw new ResearchException("execution-quote-universe");
                summary = await VerifiedSummary(policy, ct);
                if (summary.Cash.AvailableToTrade < account.CashGbp + policy.MonthlyGbp) throw new ResearchException("execution-broker-funds");
                var report = research.Report(policy.RunId)!;
                var holdings = account.Holdings.ToDictionary(k => quotes.Items.Single(q => q.Rule.BrokerTicker == k.Key).Rule.Symbol, k => k.Value);
                var targets = ConstrainedTargets(signal, holdings, report.Config.TopK, out var slotNotes);
                var plan = LimitPlanner.Plan(account.CashGbp + policy.MonthlyGbp, holdings, ImmutableDictionary<string, decimal>.Empty, targets,
                    quotes.Items.ToDictionary(q => q.Rule.Symbol, q => q.Rule), signal.MaximumWeight, now);
                plan = plan with { Notes = plan.Notes.AddRange(slotNotes) };
                account = ledger.Register(policy, signal, quotes, plan);
            }
            account = ledger.Find(account.Key)!;
            foreach (var planned in account.Intents.Where(i => i.Phase == "planned"))
            {
                ct.ThrowIfCancellationRequested();
                var currentPolicy = RequirePolicy();
                if (currentPolicy.PolicyHash != policy.PolicyHash || ledger.Paused) break;
                if (clock.GetUtcNow() >= planned.ExpiresAt) { ledger.CancelUnsubmitted(account.Key, planned.Id, "plan-expired"); continue; }
                // The broker has no atomic multi-endpoint snapshot. Refresh and reconcile immediately before each POST,
                // and stop on any inconsistency/external order rather than relying on the beginning-of-cycle view.
                var freshPositions = await broker.Positions(ct);
                var freshPending = await broker.PendingOrders(ct);
                var freshHistory = await broker.History(ct);
                foreach (var known in ledger.Find(account.Key)!.Intents.Where(i => i.BrokerOrderId is not null))
                    ledger.Reconcile(account.Key, known.Id, freshPending, freshHistory, policy.WalletRule);
                var state = ledger.Find(account.Key)!;
                if (state.IntegrityBlockCode is { } sticky) throw new ResearchException(sticky);
                if (state.Intents.Any(i => i.Phase is "unknown" or "submitting")) throw new ResearchException("broker-submission-unknown");
                summary = await VerifiedSummary(policy, ct);
                currentPolicy = RequirePolicy();
                if (currentPolicy.PolicyHash != policy.PolicyHash || ledger.Paused) break;
                var currentQuotes = quoteSource(policy); currentQuotes.Validate(clock.GetUtcNow(), policy.Environment == "live");
                var batch = account.Batches.Single(b => b.Id == planned.BatchId);
                if (currentQuotes.ContractHash != batch.RulesSha256 || currentPolicy.ContractHash != batch.ExecutionContractSha256)
                    throw new ResearchException("execution-planned-rules-changed");
                if (policy.Environment == "live" && !ledger.DemoReceipts().Any(r => !r.Synthetic && r.StrategyReportSha256 == policy.ReportSha256 &&
                    r.ExecutionCodeSha256 == CodeSha256 && r.ExecutionContractSha256 == currentPolicy.ContractHash && r.RulesSha256 == currentQuotes.ContractHash))
                    throw new ResearchException("execution-genuine-demo-required");
                ValidatePositions(state, freshPositions, currentQuotes, freshPending);
                var rule = currentQuotes.Items.SingleOrDefault(q => q.Rule.BrokerTicker == planned.Intent.BrokerTicker) ?? throw new ResearchException("execution-quote-universe");
                ValidatePlannedRule(planned, rule);
                ValidateOutstandingExposure(state, currentQuotes, research.Report(policy.RunId)!.Config.MaximumWeight, research.Report(policy.RunId)!.Config.TopK);
                var signal = signals.Find(batch.SignalId) ?? throw new ResearchException("signal-not-found");
                if (signal.DecisionDate.Year != clock.GetUtcNow().Year || signal.DecisionDate.Month != clock.GetUtcNow().Month) throw new ResearchException("execution-signal-expired");
                if (summary.Cash.AvailableToTrade < state.Intents.Where(i => i.Phase == "planned").Sum(i => i.Progress.RemainingReserve))
                    throw new ResearchException("execution-broker-funds");
                ct.ThrowIfCancellationRequested();
                quoteArchive.Record(policy.BrokerEnvironment, "SNAPSHOT", "execution-quotes", null, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(currentQuotes, ResearchStore.Json), true);
                var started = ledger.StartSubmission(account.Key, planned.Id, CodeSha256);
                SubmissionResult result;
                try { result = await broker.PlaceLimit(started.Intent, ct, started.Id); }
                catch (Exception)
                { result = new("unknown", null, "broker-submission-unknown"); }
                var finished = ledger.FinishSubmission(account.Key, planned.Id, result); submitted++;
                if (finished.Phase is "unknown" or "rejected") break;
            }
            return new("processed", account.Key, signalId, submitted, null);
        }
        catch (Exception error) when (error is ResearchException or HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException or OverflowException)
        {
            var code = error is ResearchException re ? re.Code : error is OverflowException ? "broker-fill-numeric-range" : error is OperationCanceledException ? "execution-deadline-or-shutdown" : "broker-read-unavailable";
            if (policy is not null) ledger.Block(policy.AccountKey, code);
            return new("blocked", policy?.AccountKey, signalId, submitted, code);
        }
        finally { cycle.Release(); }
    }
    public async Task Cancel(string key, string intentId, CancellationToken ct)
    {
        await cycle.WaitAsync(ct);
        try
        {
            var policy = RequirePolicy(); if (policy.AccountKey != key) throw new ResearchException("execution-account-mismatch");
            await VerifiedSummary(policy, ct);
            var intent = ledger.Find(key)?.Intents.SingleOrDefault(i => i.Id == intentId) ?? throw new ResearchException("execution-intent-not-found");
            if (intent.Phase == "planned") { ledger.CancelUnsubmitted(key, intentId); return; }
            if (intent.BrokerOrderId is not { } orderId || BrokerLedger.Terminal(intent)) throw new ResearchException("execution-cancel-state");
            ledger.CancellationRequested(key, intentId); // Retain cash even if the cancellation call times out.
            await broker!.RequestCancel(orderId, ct);
        }
        finally { cycle.Release(); }
    }
    public async Task BindUnknown(string key, string intentId, long orderId, CancellationToken ct)
    {
        await cycle.WaitAsync(ct);
        try
        {
            var policy = RequirePolicy(); if (policy.AccountKey != key) throw new ResearchException("execution-account-mismatch");
            await VerifiedSummary(policy, ct);
            var pending = await broker!.PendingOrders(ct); var history = await broker.History(ct);
            var candidates = pending.Concat(history.Items.Select(i => i.Order)).Where(o => o.Id == orderId).ToArray();
            if (candidates.Length == 0) throw new ResearchException("execution-binding-evidence-missing");
            var intent = ledger.Find(key)?.Intents.SingleOrDefault(i => i.Id == intentId) ?? throw new ResearchException("execution-intent-not-found");
            if (candidates.Any(o => !BrokerLedger.Matches(intent, o, clock.GetUtcNow()))) throw new ResearchException("execution-unknown-binding");
            ct.ThrowIfCancellationRequested();
            ledger.BindUnknown(key, intentId, candidates.OrderByDescending(o => o.FilledQuantity).First());
        }
        finally { cycle.Release(); }
    }
    private async Task<BrokerSummary> VerifiedSummary(ExecutionPolicy policy, CancellationToken ct)
    {
        var summary = await broker!.Summary(ct);
        if (summary.Id != policy.AccountId || summary.Currency != "GBP" || summary.Cash is null || summary.Cash.AvailableToTrade < 0 || summary.Cash.ReservedForOrders < 0)
            throw new ResearchException("execution-account-mismatch");
        return summary;
    }
    private static void ValidatePlannedRule(BrokerIntentRecord planned, QuoteEntry quote)
    {
        var rule = quote.Rule;
        if (!rule.Eligible || rule.Symbol != planned.Intent.Symbol || rule.QuoteCurrency != planned.Intent.QuoteCurrency || quote.OrderCurrency != planned.OrderCurrency ||
            quote.SecurityId != planned.SecurityId || quote.PriceTick != planned.PriceTick || planned.Intent.LimitPrice % quote.PriceTick != 0 ||
            planned.Intent.Quantity % rule.QuantityStep != 0 || planned.Intent.LimitPrice < rule.Ask || planned.Intent.LimitPrice > rule.Ask * 1.02m ||
            rule.MaximumFeeBps > planned.Intent.MaximumFeeBps || planned.Intent.Quantity * planned.Intent.LimitPrice / (rule.QuoteCurrency == "GBX" ? 100 : 1) < rule.MinimumNotionalGbp)
            throw new ResearchException("execution-planned-rules-changed");
    }
    public static void ValidateOutstandingExposure(BrokerAccountState account, ExecutionQuotes quotes, decimal cap, int maximumHoldings)
    {
        var rules = quotes.Items.ToDictionary(q => q.Rule.BrokerTicker, q => q.Rule);
        decimal Price(string ticker) => rules.TryGetValue(ticker, out var r) ? r.Ask / (r.QuoteCurrency == "GBX" ? 100 : 1) : throw new ResearchException("execution-quote-universe");
        var exposure = account.Holdings.ToDictionary(h => h.Key, h => h.Value * Price(h.Key), StringComparer.Ordinal);
        var nav = account.CashGbp + exposure.Values.Sum(); var maximumLoss = 0m;
        foreach (var order in account.Intents.Where(i => !BrokerLedger.Terminal(i)))
        {
            var quantity = order.Intent.Quantity - order.Progress.Filled;
            var mark = quantity * Price(order.Intent.BrokerTicker);
            exposure[order.Intent.BrokerTicker] = exposure.GetValueOrDefault(order.Intent.BrokerTicker) + mark;
            maximumLoss += Math.Max(0, order.Progress.RemainingReserve - mark);
        }
        var floor = nav - maximumLoss;
        if (floor <= 0 || exposure.Values.Any(v => v > floor * cap) || exposure.Values.Count(v => v > 0) > maximumHoldings)
            throw new ResearchException("execution-pre-submit-exposure");
    }
    private static void ValidatePositions(BrokerAccountState account, BrokerPosition[] positions, ExecutionQuotes quotes, BrokerOrder[] pending)
    {
        foreach (var quote in quotes.Items)
        {
            var ticker = quote.Rule.BrokerTicker;
            var actual = positions.SingleOrDefault(p => p.Instrument.Ticker == ticker)?.Quantity ?? 0;
            if (actual != account.OutsideBaseline.GetValueOrDefault(ticker) + account.Holdings.GetValueOrDefault(ticker))
                throw new ResearchException("execution-position-reconciliation");
            if (pending.Any(o => o.Ticker == ticker && !account.Intents.Any(i => i.BrokerOrderId == o.Id)))
                throw new ResearchException("execution-external-pending-order");
        }
        if (account.Holdings.Keys.Any(t => quotes.Items.All(q => q.Rule.BrokerTicker != t))) throw new ResearchException("execution-quote-universe");
    }
    public static Dictionary<string, decimal> ConstrainedTargets(ForwardSignal signal, IReadOnlyDictionary<string, decimal> held, int maximumHoldings, out ImmutableArray<string> notes)
    {
        var occupied = held.Where(h => h.Value > 0).Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        var slots = Math.Max(0, maximumHoldings - occupied.Count);
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal); var messages = ImmutableArray.CreateBuilder<string>();
        foreach (var prediction in signal.Predictions.Where(p => p.TargetWeight > 0).OrderByDescending(p => p.Score).ThenBy(p => p.Symbol, StringComparer.Ordinal))
        {
            if (!occupied.Contains(prediction.Symbol) && slots == 0) { messages.Add($"{prediction.Symbol}: holding slots occupied; retain cash rather than adding another name."); continue; }
            result[prediction.Symbol] = prediction.TargetWeight;
            if (!occupied.Contains(prediction.Symbol)) slots--;
        }
        notes = messages.ToImmutable(); return result;
    }
}
