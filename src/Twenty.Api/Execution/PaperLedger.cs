using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Api.Research;
using Twenty.Core.Execution;
using Twenty.Core.Research;

namespace Twenty.Api.Execution;

public sealed record RehearsalRequest(string RequestId, string RunId, string Month);
public sealed record PaperOrder(string Id, string Period, BuyIntent Intent, OrderProgress Progress);
public sealed record PaperDecision(string RequestId, string Month, decimal ContributionGbp, DateOnly SignalDate,
    ImmutableDictionary<string, decimal> Weights, ImmutableArray<string> Notes);
public sealed record PaperAccount(string Id, string RunId, bool Synthetic, decimal CashGbp, decimal ContributionsGbp,
    ImmutableDictionary<string, decimal> Quantities, ImmutableArray<PaperOrder> Orders, ImmutableArray<PaperDecision> Decisions);

// Durable, isolated simulator. No credentials, real broker identifiers, broker network or actual money.
// A frozen historical decision is rehearsed against explicit synthetic raw quotes, never presented as a current signal.
public sealed class PaperLedger
{
    private readonly ResearchStore research;
    private readonly string connectionString;
    private readonly object gate = new();
    public PaperLedger(ResearchStore research)
    {
        this.research = research;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(research.Root, "research.db"), Pooling = false }.ToString();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS paper_accounts(id TEXT PRIMARY KEY,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS execution_settings(id INTEGER PRIMARY KEY,paused INTEGER NOT NULL); INSERT OR IGNORE INTO execution_settings VALUES(1,0);";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public bool Paused
    {
        get { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT paused FROM execution_settings WHERE id=1"; return (long)cmd.ExecuteScalar()! == 1; }
    }
    public void Pause(bool paused)
    {
        lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE execution_settings SET paused=$value WHERE id=1"; cmd.Parameters.AddWithValue("$value", paused ? 1 : 0); cmd.ExecuteNonQuery(); }
    }
    public List<PaperAccount> Accounts()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM paper_accounts ORDER BY rowid DESC";
        using var reader = cmd.ExecuteReader(); var rows = new List<PaperAccount>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<PaperAccount>(reader.GetString(0), ResearchStore.Json)!);
        return rows;
    }
    public PaperAccount Rehearse(RehearsalRequest request)
    {
        if (!Guid.TryParseExact(request.RequestId, "D", out _) || !DateOnly.TryParseExact(request.Month + "-01", "yyyy-MM-dd", out var month))
            throw new ResearchException("paper-request");
        if (month.Year is < 2000 or > 2200) throw new ResearchException("paper-month-range");
        lock (gate)
        {
            var all = Accounts();
            var prior = all.SelectMany(a => a.Decisions.Select(d => (Account: a, Decision: d))).SingleOrDefault(x => x.Decision.RequestId == request.RequestId);
            if (prior.Account is not null)
            {
                if (prior.Account.RunId != request.RunId || prior.Decision.Month != request.Month) throw new ResearchException("request-id-conflict");
                return prior.Account;
            }
            if (Paused) throw new ResearchException("execution-paused");
            var run = research.Find(request.RunId) ?? throw new ResearchException("run-not-found");
            var report = research.Report(run.Id) ?? throw new ResearchException("paper-report-required");
            var account = all.SingleOrDefault(a => a.RunId == run.Id) ?? new PaperAccount(Guid.NewGuid().ToString("N"), run.Id, true,
                0, 0, ImmutableDictionary<string, decimal>.Empty, [], []);
            if (account.Decisions.Any(d => d.Month == request.Month)) throw new ResearchException("paper-month-already-funded");
            if (account.Decisions.Length >= 60 || (account.Decisions.Length == 0 && all.Count >= 100)) throw new ResearchException("paper-capacity");
            if (account.Decisions.Length > 0 && month != DateOnly.ParseExact(account.Decisions[^1].Month + "-01", "yyyy-MM-dd").AddMonths(1))
                throw new ResearchException("paper-month-sequence");
            if (account.Orders.Any(o => o.Progress.RemainingReserve > 0)) throw new ResearchException("paper-reconciliation-required");
            var signalDate = report.Predictions[^1].DecisionDate;
            var weights = report.Predictions.Where(p => p.DecisionDate == signalDate && p.TargetWeight > 0).ToImmutableDictionary(p => p.Symbol, p => p.TargetWeight);
            var symbols = weights.Keys.Concat(account.Quantities.Keys).Distinct();
            var now = DateTimeOffset.UtcNow;
            var rules = symbols.ToDictionary(s => s, s => SyntheticRule(s, now));
            var cash = account.CashGbp + run.Config.MonthlyGbp;
            var plan = LimitPlanner.Plan(cash, account.Quantities, ImmutableDictionary<string, decimal>.Empty, weights, rules, run.Config.MaximumWeight, now);
            var orders = plan.Orders.Select(i => new PaperOrder(Guid.NewGuid().ToString("N"), request.Month, i,
                new OrderProgress(i.Quantity, 0, 0, i.MaximumDebitGbp, "pending")));
            var updated = account with { CashGbp = cash, ContributionsGbp = account.ContributionsGbp + run.Config.MonthlyGbp,
                Orders = account.Orders.AddRange(orders), Decisions = account.Decisions.Add(new(request.RequestId, request.Month, run.Config.MonthlyGbp,
                    signalDate, weights, plan.Notes.Add("Synthetic raw prices are unrelated to the backtest's total-return indices. No future stock recommendation or performance is implied."))) };
            Save(updated); // Intent and cash reservation survive before any simulated fill.
            return updated;
        }
    }
    public PaperAccount Reconcile(string id, bool partial = false, bool cancel = false)
    {
        lock (gate)
        {
            var account = Accounts().SingleOrDefault(a => a.Id == id) ?? throw new ResearchException("paper-not-found");
            var orders = account.Orders.ToBuilder(); var holdings = account.Quantities.ToBuilder(); var cash = account.CashGbp;
            for (var i = 0; i < orders.Count; i++)
            {
                var order = orders[i]; var old = order.Progress;
                if (old.Status is "filled" or "cancelled" or "rejected") continue;
                var quantity = cancel ? old.Filled : partial ? Math.Max(old.Filled, order.Intent.Quantity / 2) : order.Intent.Quantity;
                var price = order.Intent.LimitPrice / (order.Intent.QuoteCurrency == "GBX" ? 100 : 1);
                var debit = quantity * price * (1 + order.Intent.MaximumFeeBps / 10000);
                var status = cancel ? "cancelled" : quantity == order.Intent.Quantity ? "filled" : "partially_filled";
                var progress = old.Reconcile(quantity, debit, status);
                holdings[order.Intent.Symbol] = holdings.GetValueOrDefault(order.Intent.Symbol) + quantity - old.Filled;
                cash -= debit - old.DebitGbp;
                orders[i] = order with { Progress = progress };
            }
            if (cash < 0) throw new ResearchException("paper-budget");
            var updated = account with { CashGbp = cash, Quantities = holdings.ToImmutable(), Orders = orders.ToImmutable() };
            Save(updated); return updated; // Atomic cumulative fills: repeated reconciliation adds nothing.
        }
    }
    private void Save(PaperAccount account)
    {
        var json = JsonSerializer.Serialize(account, ResearchStore.Json);
        if (Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024) throw new ResearchException("paper-capacity");
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO paper_accounts(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$id", account.Id); cmd.Parameters.AddWithValue("$json", json); cmd.ExecuteNonQuery(); tx.Commit();
    }
    private static InstrumentRule SyntheticRule(string symbol, DateTimeOffset now)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(symbol));
        var price = 0.25m + hash[0] / 50m;
        return new(symbol, "SIM:" + symbol, "GBP", price, price, 0.0001m, 1m, 25m, now, true);
    }
}
