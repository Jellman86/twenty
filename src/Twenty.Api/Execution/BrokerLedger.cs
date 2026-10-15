using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Api.Research;
using Twenty.Core.Execution;
using Twenty.Core.Research;

namespace Twenty.Api.Execution;

public sealed record BrokerBatch(string Id, string Month, string SignalId, decimal ContributionGbp, DateTimeOffset CreatedAt, ImmutableArray<string> Notes, string ExecutionContractSha256, string RulesSha256, bool SyntheticSignal);
public sealed record BrokerIntentRecord(string Id, string BatchId, BuyIntent Intent, string OrderCurrency, string SecurityId,
    decimal PriceTick, string QuoteSha256, bool SyntheticQuotes, DateTimeOffset ExpiresAt, string Phase, OrderProgress Progress,
    DateTimeOffset? SubmittedAt = null, long? BrokerOrderId = null, decimal ReportedFilled = 0, string? ErrorCode = null, string? SubmittedCodeSha256 = null);
public sealed record BookedBrokerFill(long Id, long OrderId, string Sha256, decimal Quantity, decimal DebitGbp, DateTimeOffset FilledAt, decimal Price);
public sealed record BrokerAccountState(string Key, string Environment, long AccountId, string PolicyHash, decimal CashGbp,
    decimal ContributionsGbp, ImmutableDictionary<string, decimal> OutsideBaseline, ImmutableDictionary<string, decimal> Holdings,
    ImmutableArray<BrokerBatch> Batches, ImmutableArray<BrokerIntentRecord> Intents, ImmutableDictionary<long, BookedBrokerFill> Fills,
    string? BlockCode = null, string? IntegrityBlockCode = null);
public sealed record DemoExecutionReceipt(string Id, string BatchId, string StrategyReportSha256, string ExecutionCodeSha256,
    DateTimeOffset VerifiedAt, bool Synthetic, string EvidenceSha256, string ExecutionContractSha256, string RulesSha256);

public sealed class BrokerLedger
{
    private readonly string connectionString;
    private readonly object gate = new();
    private readonly TimeProvider clock;
    public BrokerLedger(ResearchStore research, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(research.Root, "research.db"), Pooling = false, DefaultTimeout = 10 }.ToString();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS broker_accounts(id TEXT PRIMARY KEY,json TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS broker_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,account_key TEXT NOT NULL,at TEXT NOT NULL,kind TEXT NOT NULL,intent_id TEXT);" +
            "CREATE TABLE IF NOT EXISTS demo_execution_receipts(id TEXT PRIMARY KEY,json TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS execution_settings(id INTEGER PRIMARY KEY,paused INTEGER NOT NULL); INSERT OR IGNORE INTO execution_settings VALUES(1,0);";
        command.ExecuteNonQuery();
        foreach (var account in Accounts())
            if (account.Intents.Any(i => i.Phase == "submitting"))
                Save(account with { BlockCode = "submission-restarted-unknown", Intents = account.Intents.Select(i => i.Phase == "submitting" ?
                    i with { Phase = "unknown", ErrorCode = "submission-restarted-unknown" } : i).ToImmutableArray() }, "restart-unknown");
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public bool Paused
    {
        get { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT paused FROM execution_settings WHERE id=1"; return (long)command.ExecuteScalar()! == 1; }
    }
    public List<BrokerAccountState> Accounts()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM broker_accounts ORDER BY rowid";
        using var reader = command.ExecuteReader(); var accounts = new List<BrokerAccountState>();
        while (reader.Read()) accounts.Add(JsonSerializer.Deserialize<BrokerAccountState>(reader.GetString(0), ResearchStore.Json)!);
        return accounts;
    }
    public BrokerAccountState? Find(string key) => Accounts().SingleOrDefault(a => a.Key == key);
    public BrokerAccountState BindAccount(ExecutionPolicy policy, BrokerPosition[] positions)
    {
        lock (gate)
        {
            var account = Find(policy.AccountKey);
            if (account is not null)
            {
                if (account.PolicyHash != policy.PolicyHash) throw new ResearchException("execution-policy-migration-required");
                return account;
            }
            if (Accounts().Count >= 8) throw new ResearchException("execution-account-capacity");
            var baseline = positions.ToImmutableDictionary(p => p.Instrument.Ticker, p => p.Quantity, StringComparer.Ordinal);
            account = new(policy.AccountKey, policy.Environment, policy.AccountId, policy.PolicyHash, 0, 0, baseline,
                ImmutableDictionary<string, decimal>.Empty, [], [], ImmutableDictionary<long, BookedBrokerFill>.Empty);
            Save(account, "account-bound"); return account;
        }
    }
    public BrokerAccountState Register(ExecutionPolicy policy, ForwardSignal signal, ExecutionQuotes quotes, BuyPlan plan)
    {
        lock (gate)
        {
            if (Paused) throw new ResearchException("execution-paused");
            var account = Find(policy.AccountKey) ?? throw new ResearchException("execution-account-required");
            if (account.PolicyHash != policy.PolicyHash || account.BlockCode is not null) throw new ResearchException("execution-account-blocked");
            var month = signal.DecisionDate.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            if (account.Batches.Any(b => b.Month == month)) return account;
            if (account.Intents.Any(i => !Terminal(i)) || account.Batches.Length >= 600 || account.Intents.Length + plan.Orders.Length > 5000)
                throw new ResearchException("execution-reconciliation-or-capacity");
            var cash = account.CashGbp + policy.MonthlyGbp;
            if (plan.Orders.Sum(o => o.MaximumDebitGbp) > cash || plan.CashRemainingGbp != cash - plan.Orders.Sum(o => o.MaximumDebitGbp))
                throw new ResearchException("execution-plan-budget");
            var now = clock.GetUtcNow(); var batch = new BrokerBatch(Guid.NewGuid().ToString("N"), month, signal.Id, policy.MonthlyGbp, now, plan.Notes, policy.ContractHash, quotes.ContractHash, signal.Synthetic);
            var intents = plan.Orders.Select(order =>
            {
                var quote = quotes.Items.Single(q => q.Rule.Symbol == order.Symbol);
                var expires = quote.Rule.ObservedAt.AddMinutes(5);
                var monthEnd = new DateTimeOffset(signal.DecisionDate.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                return new BrokerIntentRecord(Guid.NewGuid().ToString("N"), batch.Id, order, quote.OrderCurrency, quote.SecurityId, quote.PriceTick,
                    ExecutionPolicy.Hash(quote), quote.Synthetic, expires < monthEnd ? expires : monthEnd, "planned",
                    new(order.Quantity, 0, 0, order.MaximumDebitGbp, "pending"));
            });
            var updated = account with { CashGbp = cash, ContributionsGbp = account.ContributionsGbp + policy.MonthlyGbp,
                Batches = account.Batches.Add(batch), Intents = account.Intents.AddRange(intents) };
            Save(updated, "monthly-intents-registered"); return updated;
        }
    }
    public BrokerIntentRecord StartSubmission(string key, string id, string codeHash)
    {
        lock (gate)
        {
            if (Paused) throw new ResearchException("execution-paused");
            if (!ExecutionPolicy.IsHash(codeHash)) throw new ResearchException("execution-code-hash");
            var account = Find(key) ?? throw new ResearchException("execution-account-required");
            var intent = account.Intents.Single(i => i.Id == id);
            if (account.BlockCode is not null || account.Intents.Any(i => i.Phase is "unknown" or "submitting") || intent.Phase != "planned")
                throw new ResearchException("execution-submission-state");
            if (clock.GetUtcNow() >= intent.ExpiresAt) throw new ResearchException("execution-plan-expired");
            intent = intent with { Phase = "submitting", SubmittedAt = clock.GetUtcNow(), SubmittedCodeSha256 = codeHash };
            Save(Replace(account, intent), "submission-started", intent.Id); return intent; // This commit must precede the network call.
        }
    }
    public BrokerIntentRecord FinishSubmission(string key, string id, SubmissionResult result)
    {
        lock (gate)
        {
            var account = Find(key)!; var intent = account.Intents.Single(i => i.Id == id);
            if (intent.Phase != "submitting") throw new ResearchException("execution-submission-state");
            if (result.Outcome == "rejected") intent = intent with { Phase = "rejected", ErrorCode = result.ErrorCode,
                Progress = intent.Progress.Reconcile(0, 0, "rejected") };
            else if (result.Outcome == "acknowledged" && result.Order is { } order && Matches(intent, order, clock.GetUtcNow()) &&
                !account.Intents.Any(i => i.BrokerOrderId == order.Id))
                intent = intent with { Phase = "pending", BrokerOrderId = order.Id, ReportedFilled = order.FilledQuantity };
            else intent = intent with { Phase = "unknown", ErrorCode = "broker-submission-unknown" };
            account = Replace(account, intent);
            if (intent.Phase == "unknown") account = account with { BlockCode = "broker-submission-unknown" };
            Save(account, "submission-" + intent.Phase, intent.Id); return intent;
        }
    }
    public void Block(string key, string? code)
    {
        lock (gate)
        {
            var account = Find(key);
            if (account is not null)
            {
                var sticky = account.IntegrityBlockCode;
                if (code is not null && (code.StartsWith("broker-fill-", StringComparison.Ordinal) || code.StartsWith("broker-tax-", StringComparison.Ordinal) ||
                    code.StartsWith("broker-wallet-", StringComparison.Ordinal) || code is "fill-reconciliation" or "broker-terminal-conflict" or "broker-order-evidence-mismatch" or "broker-reserve-breach")) sticky ??= code;
                var next = sticky ?? code;
                if (account.BlockCode != next || account.IntegrityBlockCode != sticky)
                    Save(account with { BlockCode = next, IntegrityBlockCode = sticky }, next is null ? "block-cleared" : "blocked");
            }
        }
    }
    public void CancelUnsubmitted(string key, string id, string code = "user-cancelled")
    {
        lock (gate)
        {
            var account = Find(key)!; var intent = account.Intents.Single(i => i.Id == id);
            if (intent.Phase != "planned") throw new ResearchException("execution-cancel-state");
            intent = intent with { Phase = code == "plan-expired" ? "expired" : "cancelled", ErrorCode = code, Progress = intent.Progress.Reconcile(0, 0, "cancelled") };
            Save(Replace(account, intent), "unsubmitted-cancelled", intent.Id);
        }
    }
    public void CancellationRequested(string key, string id)
    {
        lock (gate)
        {
            var account = Find(key)!; var intent = account.Intents.Single(i => i.Id == id);
            if (intent.BrokerOrderId is null || Terminal(intent)) throw new ResearchException("execution-cancel-state");
            Save(Replace(account, intent with { Phase = "cancel_requested" }), "cancel-requested", intent.Id);
        }
    }
    // Binding an unknown POST is a deliberate reconciliation action with an independently inspected order ID.
    // A unique price/quantity/time match alone cannot distinguish our order from another API client's order.
    public void BindUnknown(string key, string id, BrokerOrder order)
    {
        lock (gate)
        {
            var account = Find(key)!; var intent = account.Intents.Single(i => i.Id == id);
            if (intent.Phase != "unknown" || !Matches(intent, order, clock.GetUtcNow()) || account.Intents.Any(i => i.BrokerOrderId == order.Id))
                throw new ResearchException("execution-unknown-binding");
            intent = intent with { Phase = "pending", BrokerOrderId = order.Id, ReportedFilled = order.FilledQuantity, ErrorCode = null };
            account = Replace(account, intent);
            if (!account.Intents.Any(i => i.Phase == "unknown") && account.IntegrityBlockCode is null) account = account with { BlockCode = null };
            Save(account, "unknown-bound-by-operator", intent.Id);
        }
    }
    public void Reconcile(string key, string id, BrokerOrder[] pending, BrokerHistorySnapshot history, WalletDebitRule walletRule)
    {
        lock (gate)
        {
            var account = Find(key)!; var intent = account.Intents.Single(i => i.Id == id);
            if (intent.BrokerOrderId is not { } orderId) return;
            var rows = history.Items.Where(i => i.Order.Id == orderId).ToArray();
            var observations = rows.Select(i => i.Order).Concat(pending.Where(o => o.Id == orderId)).ToArray();
            if (observations.Length == 0) return; // Missing from bounded pending/history never releases cash or permits replay.
            if (observations.Any(o => !Matches(intent, o, clock.GetUtcNow()))) throw new ResearchException("broker-order-evidence-mismatch");
            var fills = account.Fills.ToBuilder(); var holdings = account.Holdings.ToBuilder(); var cash = account.CashGbp;
            foreach (var row in rows.Where(r => r.Fill is not null))
            {
                var fill = row.Fill!;
                if (fill.FilledAt < intent.SubmittedAt!.Value.AddMinutes(-2) || fill.FilledAt > clock.GetUtcNow().AddMinutes(2) || fill.Price > intent.Intent.LimitPrice)
                    throw new ResearchException("broker-fill-timing-price");
                var debit = BrokerFillAccounting.Debit(fill, walletRule);
                var canonicalFill = fill with { FilledAt = fill.FilledAt.ToUniversalTime(), WalletImpact = fill.WalletImpact with
                { Taxes = fill.WalletImpact.Taxes?.OrderBy(t => t.Currency, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Quantity).ToArray() } };
                var hash = ExecutionPolicy.Hash(new { orderId, fill = canonicalFill });
                if (fills.TryGetValue(fill.Id, out var prior))
                {
                    if (prior.OrderId != orderId || prior.Sha256 != hash) throw new ResearchException("broker-fill-revision");
                    continue;
                }
                fills.Add(fill.Id, new(fill.Id, orderId, hash, fill.Quantity, debit, fill.FilledAt, fill.Price));
                holdings[intent.Intent.BrokerTicker] = holdings.GetValueOrDefault(intent.Intent.BrokerTicker) + fill.Quantity;
                cash -= debit;
            }
            var ownFills = fills.Values.Where(f => f.OrderId == orderId).ToArray();
            var quantity = ownFills.Sum(f => f.Quantity); var totalDebit = ownFills.Sum(f => f.DebitGbp);
            var reported = Math.Max(intent.ReportedFilled, observations.Max(o => o.FilledQuantity));
            if (quantity > reported || reported > intent.Intent.Quantity) throw new ResearchException("broker-fill-quantity");
            var finals = observations.Where(o => o.Status is "FILLED" or "CANCELLED" or "REJECTED").ToArray();
            if (finals.Select(o => o.Status).Distinct().Count() > 1) throw new ResearchException("broker-terminal-conflict");
            var terminal = finals.LastOrDefault();
            var status = quantity > 0 ? "partially_filled" : "pending";
            if (terminal is not null && quantity == reported && terminal.FilledQuantity == reported)
                status = terminal.Status switch { "FILLED" => "filled", "CANCELLED" => "cancelled", _ => "rejected" };
            // A prior terminal state cannot regress merely because the current scan is truncated/older.
            if (Terminal(intent)) status = intent.Progress.Status;
            OrderProgress progress;
            var reserveBreach = totalDebit > intent.Progress.ReservedGbp;
            if (reserveBreach)
            {
                if (quantity < intent.Progress.Filled || totalDebit < intent.Progress.DebitGbp || quantity > intent.Intent.Quantity ||
                    (status == "filled" && quantity != intent.Intent.Quantity)) throw new ResearchException("broker-fill-reconciliation");
                progress = intent.Progress with { Filled = quantity, DebitGbp = totalDebit, Status = status };
            }
            else
            {
                try { progress = intent.Progress.Reconcile(quantity, totalDebit, status); }
                catch (ResearchException error) when (error.Code == "fill-reconciliation")
                { throw new ResearchException("broker-fill-reconciliation"); }
            }
            intent = intent with { Progress = progress, ReportedFilled = reported,
                Phase = status is "filled" or "cancelled" or "rejected" ? status : intent.Phase == "cancel_requested" ? "cancel_requested" : "pending" };
            account = Replace(account, intent) with { CashGbp = cash, Holdings = holdings.ToImmutable(), Fills = fills.ToImmutable(),
                BlockCode = reserveBreach || cash < 0 ? "broker-reserve-breach" : account.BlockCode,
                IntegrityBlockCode = reserveBreach || cash < 0 ? "broker-reserve-breach" : account.IntegrityBlockCode };
            Save(account, "broker-evidence-reconciled", intent.Id);
        }
    }
    public static bool Terminal(BrokerIntentRecord intent) => intent.Phase is "filled" or "cancelled" or "rejected" or "expired";
    public static bool Matches(BrokerIntentRecord intent, BrokerOrder order, DateTimeOffset now) =>
        order.Id > 0 && order.Ticker == intent.Intent.BrokerTicker && order.Quantity == intent.Intent.Quantity && order.LimitPrice == intent.Intent.LimitPrice &&
        order.Type == "LIMIT" && order.Side == "BUY" && order.Strategy == "QUANTITY" && order.TimeInForce == "DAY" && order.InitiatedFrom == "API" &&
        order.Currency == intent.OrderCurrency && order.FilledQuantity >= 0 && order.FilledQuantity <= order.Quantity &&
        order.CreatedAt is { } created && intent.SubmittedAt is { } submitted && created >= submitted.AddMinutes(-2) && created <= now.AddMinutes(2) &&
        order.Status is "LOCAL" or "UNCONFIRMED" or "CONFIRMED" or "NEW" or "CANCELLING" or "CANCELLED" or "PARTIALLY_FILLED" or "FILLED" or "REJECTED";
    public void RecordDemoReceipt(ExecutionPolicy policy, string codeHash, BrokerPosition[] positions)
    {
        lock (gate)
        {
            var account = Find(policy.AccountKey)!;
            if (account.Environment != "demo" || account.BlockCode is not null) return;
            foreach (var batch in account.Batches)
            {
                var orders = account.Intents.Where(i => i.BatchId == batch.Id).ToArray();
                if (orders.Length == 0 || orders.Any(i => i.Phase != "filled" || i.SubmittedCodeSha256 != codeHash)) continue;
                var tickers = orders.Select(i => i.Intent.BrokerTicker).Distinct().ToArray();
                if (tickers.Any(t => positions.SingleOrDefault(p => p.Instrument.Ticker == t)?.Quantity != account.OutsideBaseline.GetValueOrDefault(t) + account.Holdings.GetValueOrDefault(t))) continue;
                var evidenceHash = ExecutionPolicy.Hash(new { batch, orders, account.Fills, positions });
                var receipt = new DemoExecutionReceipt(ExecutionPolicy.Hash(new { batch.Id, codeHash }), batch.Id, policy.ReportSha256, codeHash,
                    clock.GetUtcNow(), policy.SyntheticEvidence || batch.SyntheticSignal || orders.Any(i => i.SyntheticQuotes), evidenceHash, batch.ExecutionContractSha256, batch.RulesSha256);
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "INSERT OR IGNORE INTO demo_execution_receipts(id,json) VALUES($id,$json)";
                command.Parameters.AddWithValue("$id", receipt.Id); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(receipt, ResearchStore.Json)); command.ExecuteNonQuery();
            }
        }
    }
    public List<DemoExecutionReceipt> DemoReceipts()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM demo_execution_receipts";
        using var reader = command.ExecuteReader(); var result = new List<DemoExecutionReceipt>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<DemoExecutionReceipt>(reader.GetString(0), ResearchStore.Json)!);
        return result;
    }
    private static BrokerAccountState Replace(BrokerAccountState account, BrokerIntentRecord intent) => account with
    { Intents = account.Intents.Select(i => i.Id == intent.Id ? intent : i).ToImmutableArray() };
    private void Save(BrokerAccountState account, string kind, string? intentId = null)
    {
        var reserves = account.Intents.Sum(i => i.Progress.RemainingReserve);
        if (((account.CashGbp < 0 || reserves > account.CashGbp) && account.BlockCode != "broker-reserve-breach") || account.Intents.Sum(i => i.Progress.DebitGbp) + account.CashGbp != account.ContributionsGbp ||
            account.Holdings.Values.Any(q => q < 0)) throw new ResearchException("execution-ledger-invariant");
        var json = JsonSerializer.Serialize(account, ResearchStore.Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024) throw new ResearchException("execution-ledger-capacity");
        using var db = Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO broker_accounts(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        command.Parameters.AddWithValue("$id", account.Key); command.Parameters.AddWithValue("$json", json); command.ExecuteNonQuery();
        using var e = db.CreateCommand(); e.Transaction = transaction; e.CommandText = "INSERT INTO broker_events(account_key,at,kind,intent_id) VALUES($key,$at,$kind,$intent)";
        e.Parameters.AddWithValue("$key", account.Key); e.Parameters.AddWithValue("$at", clock.GetUtcNow().ToString("O")); e.Parameters.AddWithValue("$kind", kind); e.Parameters.AddWithValue("$intent", intentId is null ? DBNull.Value : intentId); e.ExecuteNonQuery();
        transaction.Commit();
    }
}
