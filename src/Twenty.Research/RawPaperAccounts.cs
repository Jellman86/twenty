using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Core.Data;
using Twenty.Core.Research;
namespace Twenty.Research;

public sealed record RawPaperCreateRequest(string RequestId, string StudyId, string PlanSha256, string SelectionSha256,
    string CostScenario, string DatasetSha256, ImmutableArray<RawStudyCalendarPin> Calendars, DateTimeOffset FirstDecisionAt, ImmutableArray<DateTimeOffset> Decisions);
public sealed record RawPaperAdvanceRequest(string RequestId, string? ExpectedReceiptSha256, string ReplaySha256);
public sealed record RawPaperBalance(decimal CashGbp, decimal ContributionsGbp, decimal FeesGbp, ImmutableSortedDictionary<string, RawQuantityPosition> Holdings, decimal EquityGbp, DateTimeOffset? ValuedAt);
public sealed record RawPaperOriginal(string Kind, string Sha256, int Bytes);
public sealed record RawPaperAccount(string Id, string Version, string RequestId, string RequestSha256, string StudyId,
    string PlanSha256, string SelectionSha256, string CandidateId, string CostScenario, DateTimeOffset SourceReservedEnd,
    RawPaperPolicy Policy, string CodeSha256, DateTimeOffset CreatedAt, string Status, int AcceptedPeriods, int Attempts,
    string? HeadOperationId, string? HeadReceiptSha256, string? LastAttemptId, RawPaperBalance Balance,
    ImmutableArray<RawPaperOriginal> Originals);
public sealed record RawPaperOperation(string Id, string AccountId, string RequestId, string RequestSha256, string ReplaySha256,
    string? ExpectedReceiptSha256, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, string? ReceiptSha256, string? ErrorCode);

public sealed partial class ResearchStore
{
    private static void InitialiseRawPaper(SqliteConnection db) => Execute(db,
        "CREATE TABLE IF NOT EXISTS raw_paper_accounts(id TEXT PRIMARY KEY,request_id TEXT UNIQUE NOT NULL,study_id TEXT NOT NULL,cost TEXT NOT NULL,sha256 TEXT NOT NULL,json TEXT NOT NULL,UNIQUE(study_id,cost));" +
        "CREATE TABLE IF NOT EXISTS raw_paper_operations(id TEXT PRIMARY KEY,request_id TEXT UNIQUE NOT NULL,account_id TEXT NOT NULL,sha256 TEXT NOT NULL,json TEXT NOT NULL);");
    private string PaperInputPath(RawPaperOriginal input) => Path.Combine(Root, "raw-paper-inputs", input.Kind, input.Sha256 + ".json");
    private string PaperOperationPath(string id, string kind) => Path.Combine(Root, "raw-paper-operations", id + "." + kind + ".json");
    private List<T> PaperRows<T>(string table)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT sha256,json FROM " + table + " ORDER BY rowid";
        using var reader = cmd.ExecuteReader(); var rows = new List<T>();
        while (reader.Read())
        {
            var bytes = Encoding.UTF8.GetBytes(reader.GetString(1));
            if (bytes.Length > 1024 * 1024 || RawHash(bytes) != reader.GetString(0)) throw new ResearchException("raw-paper-integrity");
            try { rows.Add(JsonSerializer.Deserialize<T>(bytes, RawReplayJson.Options) ?? throw new JsonException()); }
            catch (JsonException) { throw new ResearchException("raw-paper-integrity"); }
        }
        return rows;
    }
    public List<RawPaperAccount> RawPaperAccounts()
    {
        lock (gate)
        {
            var accounts = PaperRows<RawPaperAccount>("raw_paper_accounts");
            var operations = PaperRows<RawPaperOperation>("raw_paper_operations");
            foreach (var account in accounts)
            {
                var own = operations.Where(o => o.AccountId == account.Id).ToArray();
                if (account.Version != RawPaperContract.Version || account.Attempts != own.Length || account.AcceptedPeriods != own.Count(o => o.Status == "succeeded") ||
                    (account.HeadOperationId is null) != (account.HeadReceiptSha256 is null) || account.HeadOperationId is { } head &&
                    !own.Any(o => o.Id == head && o.Status == "succeeded" && o.ReceiptSha256 == account.HeadReceiptSha256) ||
                    account.Status is not ("ready" or "active" or "running" or "failed" or "stopped" or "completed")) throw new ResearchException("raw-paper-integrity");
            }
            return accounts;
        }
    }
    public List<RawPaperOperation> RawPaperOperations(string id)
    {
        lock (gate) { if (!RawPaperAccounts().Any(a => a.Id == id)) throw new ResearchException("raw-paper-not-found"); return PaperRows<RawPaperOperation>("raw_paper_operations").Where(o => o.AccountId == id).ToList(); }
    }
    private void SavePaper(RawPaperAccount account, RawPaperOperation? operation = null)
    {
        var accountBytes = RawBytes(account); if (accountBytes.Length > 256 * 1024) throw new ResearchException("raw-paper-capacity");
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO raw_paper_accounts VALUES($id,$request,$study,$cost,$sha,$json) ON CONFLICT(id) DO UPDATE SET sha256=excluded.sha256,json=excluded.json";
        cmd.Parameters.AddWithValue("$id", account.Id); cmd.Parameters.AddWithValue("$request", account.RequestId); cmd.Parameters.AddWithValue("$study", account.StudyId);
        cmd.Parameters.AddWithValue("$cost", account.CostScenario); cmd.Parameters.AddWithValue("$sha", RawHash(accountBytes)); cmd.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(accountBytes)); cmd.ExecuteNonQuery();
        if (operation is not null)
        {
            var bytes = RawBytes(operation); cmd.Parameters.Clear();
            cmd.CommandText = "INSERT INTO raw_paper_operations VALUES($id,$request,$account,$sha,$json) ON CONFLICT(id) DO UPDATE SET sha256=excluded.sha256,json=excluded.json";
            cmd.Parameters.AddWithValue("$id", operation.Id); cmd.Parameters.AddWithValue("$request", operation.RequestId); cmd.Parameters.AddWithValue("$account", operation.AccountId);
            cmd.Parameters.AddWithValue("$sha", RawHash(bytes)); cmd.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(bytes)); cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    private void InterruptRawPaper()
    {
        foreach (var account in RawPaperAccounts().Where(a => a.Status == "running"))
        {
            var row = RawPaperOperations(account.Id).Single(o => o.Id == account.LastAttemptId && o.Status == "running");
            SavePaper(account with { Status = "failed" }, row with { Status = "interrupted", ErrorCode = "process-restarted", FinishedAt = LaterPaper(clock.GetUtcNow(), row.StartedAt) });
        }
    }
    private static DateTimeOffset LaterPaper(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static string PaperUuid(string value) => Guid.TryParseExact(value, "D", out var id) ? id.ToString("D") : throw new ResearchException("raw-paper-request");
    public RawPaperAccount CreateRawPaperAccount(RawPaperCreateRequest request)
    {
        if (request is null || !IsSha256(request.PlanSha256) || !IsSha256(request.SelectionSha256) || !IsSha256(request.DatasetSha256) ||
            request.CostScenario is not ("base" or "stress") || request.FirstDecisionAt == default || request.Calendars.IsDefaultOrEmpty ||
            request.Decisions.IsDefault || request.Decisions.Length is < 14 or > 133 || request.Calendars.Length > 16 || request.Calendars.Any(c => c is null)) throw new ResearchException("raw-paper-request");
        request = request with { RequestId = PaperUuid(request.RequestId), FirstDecisionAt = request.FirstDecisionAt.ToUniversalTime(), Decisions = request.Decisions.Select(d => d.ToUniversalTime()).ToImmutableArray(), Calendars = request.Calendars.OrderBy(c => c.CalendarId, StringComparer.Ordinal).ToImmutableArray() };
        var sha = RawHash(RawBytes(request with { RequestId = "" }));
        lock (gate)
        {
            var accounts = RawPaperAccounts(); var prior = accounts.SingleOrDefault(a => a.RequestId == request.RequestId);
            if (prior is not null) { if (prior.RequestSha256 != sha) throw new ResearchException("request-id-conflict"); return prior; }
            if (accounts.Count >= 20) throw new ResearchException("raw-paper-capacity");
            if (accounts.Any(a => a.StudyId == request.StudyId && a.CostScenario == request.CostScenario)) throw new ResearchException("raw-paper-already-created");
            var source = RawStudies().SingleOrDefault(s => s.Id == request.StudyId) ?? throw new ResearchException("study-not-found");
            if (!source.Plan.Synthetic) throw new ResearchException("raw-paper-authored-only");
            if (source.PlanSha256 != request.PlanSha256 || source.SelectionReportSha256 != request.SelectionSha256 || source.SelectedCandidateId is null ||
                source.Status is not ("candidate-frozen" or "engineering-complete")) throw new ResearchException("raw-paper-source-selection");
            CurrentRawPlan(source); _ = RawStudySelectionBytes(source.Id);
            if (request.FirstDecisionAt != request.Decisions[RawUniverseDecision.HistoryDecisions - 1] || request.Decisions[0] <= source.Plan.ReservedEnd || request.Decisions.Any(d => d == default) || request.Decisions.Zip(request.Decisions.Skip(1)).Any(p => (p.Second.Year - p.First.Year) * 12 + p.Second.Month - p.First.Month != 1 || (p.Second - p.First).TotalDays is < 20 or > 45)) throw new ResearchException("raw-paper-information-window");
            var provenance = UniverseProvenance(request.DatasetSha256) ?? throw new ResearchException("raw-paper-dataset-unattested");
            if (!provenance) throw new ResearchException("raw-paper-authored-only");
            var data = PaperReadArtifact(UniverseDatasetPath(request.DatasetSha256), request.DatasetSha256, DatasetJson.MaximumBytes);
            if (RawHash(data) != request.DatasetSha256) throw new ResearchException("dataset-integrity");
            var file = DatasetJson.Read(data); var validation = DatasetValidator.Validate(file); if (!validation.IsValid) throw new InvalidDatasetException(validation);
            if (!file.Manifest.Synthetic) throw new ResearchException("raw-paper-authored-only");
            if (file.Observations.OfType<CorporateAction>().Any()) throw new ResearchException("raw-paper-actions-unsupported");
            if (!file.Manifest.Universes.Any(u => u.Id == source.Plan.UniverseId) || file.Manifest.CreatedAt < request.Decisions[^1]) throw new ResearchException("raw-paper-dataset");
            var originals = new List<(RawPaperOriginal Original, byte[] Bytes)> { (new("dataset", request.DatasetSha256, data.Length), data) };
            var calendars = new List<ExecutionCalendar>();
            foreach (var pin in request.Calendars)
            {
                if (!IsSha256(pin.Sha256)) throw new ResearchException("raw-paper-calendar");
                var bytes = DatasetArchive.ReadBounded(Path.Combine(Root, "imports", pin.Sha256 + ".calendar.json")); var calendar = RawStudyEvidence.Calendar(bytes);
                if (calendar.Sha256 != pin.Sha256 || calendar.Reference.Id != pin.CalendarId || calendar.Venue != pin.Venue || !file.Manifest.Calendars.Contains(calendar.Reference)) throw new ResearchException("raw-paper-calendar");
                var zone = TimeZoneInfo.FindSystemTimeZoneById(calendar.Reference.TimeZone);
                if (calendar.Days[0].Date > DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.Decisions[0], zone).DateTime) ||
                    calendar.Days[^1].Date < DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.Decisions[^1], zone).DateTime)) throw new ResearchException("raw-paper-calendar");
                calendars.Add(calendar); originals.Add((new("calendar", RawHash(bytes), bytes.Length), bytes));
            }
            if (calendars.Select(c => c.Reference.Id).Distinct(StringComparer.Ordinal).Count() != calendars.Count || file.Observations.OfType<Listing>().Any(l => !calendars.Any(c => c.Reference.Id == l.CalendarId && c.Venue == l.Venue))) throw new ResearchException("raw-paper-calendar");
            var execution = request.CostScenario == "base" ? source.Plan.BaseExecution : source.Plan.StressExecution;
            var policy = new RawPaperPolicy(request.DatasetSha256, source.Plan.UniverseId, request.FirstDecisionAt, source.Plan.MonthlyGbp,
                source.Plan.Candidates.Single(c => c.Id == source.SelectedCandidateId).Config, execution, request.Calendars, RawReplayBuildIdentity.Current, request.Decisions);
            var account = new RawPaperAccount(Guid.NewGuid().ToString("N"), RawPaperContract.Version, request.RequestId, sha, source.Id, source.PlanSha256,
                request.SelectionSha256, source.SelectedCandidateId, request.CostScenario, source.Plan.ReservedEnd, policy, ResearchIdentity.Current.Sha256,
                clock.GetUtcNow(), "ready", 0, 0, null, null, null, new(0, 0, 0, ImmutableSortedDictionary<string, RawQuantityPosition>.Empty, 0, null), originals.Select(o => o.Original).ToImmutableArray());
            EnsureAdditionalArtifactCapacity(originals.Where(o => !File.Exists(PaperInputPath(o.Original))).Sum(o => (long)o.Bytes.Length) + 1024 * 1024);
            foreach (var item in originals) { var path = PaperInputPath(item.Original); Directory.CreateDirectory(Path.GetDirectoryName(path)!); ImmutableWrite(path, item.Bytes); }
            SavePaper(account); return account;
        }
    }
    public byte[] RawPaperReceiptBytes(string operationId)
    {
        lock (gate)
        {
            var operation = PaperRows<RawPaperOperation>("raw_paper_operations").SingleOrDefault(o => o.Id == operationId) ?? throw new ResearchException("raw-paper-not-found");
            if (operation.Status != "succeeded" || operation.ReceiptSha256 is null) throw new ResearchException("raw-paper-receipt-unavailable");
            return PaperReadArtifact(PaperOperationPath(operation.Id, "receipt"), operation.ReceiptSha256, 8 * 1024 * 1024);
        }
    }
    private static byte[] PaperReadArtifact(string path, string sha, int maximum)
    {
        try { var bytes = DatasetArchive.ReadBounded(path); if (bytes.Length > maximum || RawHash(bytes) != sha) throw new ResearchException("raw-paper-integrity"); return bytes; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ResearchException("raw-paper-input-unavailable"); }
    }
    public RawPaperOperation AdvanceRawPaperAccount(string id, RawPaperAdvanceRequest request, CancellationToken ct = default)
    {
        if (request is null || !IsSha256(request.ReplaySha256) || request.ExpectedReceiptSha256 is not null && !IsSha256(request.ExpectedReceiptSha256)) throw new ResearchException("raw-paper-request");
        request = request with { RequestId = PaperUuid(request.RequestId) }; var requestSha = RawHash(RawBytes(new { id, request.ExpectedReceiptSha256, request.ReplaySha256 }));
        lock (gate)
        {
            var old = PaperRows<RawPaperOperation>("raw_paper_operations").SingleOrDefault(o => o.RequestId == request.RequestId);
            if (old is not null) { if (old.RequestSha256 != requestSha) throw new ResearchException("request-id-conflict"); return old; }
            var account = RawPaperAccounts().SingleOrDefault(a => a.Id == id) ?? throw new ResearchException("raw-paper-not-found");
            if (account.Status is not ("ready" or "active")) throw new ResearchException("raw-paper-stopped");
            if (request.ExpectedReceiptSha256 != account.HeadReceiptSha256) throw new ResearchException("raw-paper-head-conflict");
            if (account.CodeSha256 != ResearchIdentity.Current.Sha256 || account.Policy.Build != RawReplayBuildIdentity.Current) throw new ResearchException("study-code-changed");
            if (account.Attempts >= 120) throw new ResearchException("raw-paper-capacity");
            if (!TryBeginComputation()) throw new ResearchException("research-worker-busy");
            try
            {
                EnsureAdditionalArtifactCapacity(11L * 1024 * 1024);
                var at = clock.GetUtcNow(); if (at < account.CreatedAt) throw new ResearchException("raw-study-clock-regression");
                var row = new RawPaperOperation(Guid.NewGuid().ToString("N"), id, request.RequestId, requestSha, request.ReplaySha256,
                    request.ExpectedReceiptSha256, at, null, "running", null, null);
                account = account with { Status = "running", Attempts = account.Attempts + 1, LastAttemptId = row.Id }; SavePaper(account, row);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
                    var bytes = PaperReadArtifact(Path.Combine(Root, "imports", request.ReplaySha256 + ".raw-paper.json"), request.ReplaySha256, RawReplayArchive.MaximumRequestBytes);
                    var path = PaperOperationPath(row.Id, "request"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); ImmutableWrite(path, bytes);
                    byte[]? previous = null;
                    if (account.HeadOperationId is { } head)
                    {
                        var prior = RawPaperOperations(id).Single(o => o.Id == head);
                        previous = PaperReadArtifact(PaperOperationPath(head, "request"), prior.ReplaySha256, RawReplayArchive.MaximumRequestBytes);
                        var saved = JsonSerializer.Deserialize<RawReplayReceipt>(RawPaperReceiptBytes(head), RawReplayJson.Options)!;
                        var resultBytes = Convert.FromBase64String(saved.ResultBase64);
                        if (RawHash(resultBytes) != saved.ResultSha256 || !RawBytes(Balance(JsonSerializer.Deserialize<RawEventReplayResult>(resultBytes, RawReplayJson.Options)!)).AsSpan().SequenceEqual(RawBytes(account.Balance))) throw new ResearchException("raw-paper-integrity");
                    }
                    var document = RawPaperContract.ValidateAppend(account.Policy, previous, bytes);
                    if (document.Events.Select(e => e.Operation).OfType<RawReplayRebalanceDecision>().Any(d => d.UniverseStrategy!.Spec.Decisions[0] <= account.SourceReservedEnd)) throw new ResearchException("raw-paper-information-window");
                    var original = account.Originals.Single(o => o.Kind == "dataset");
                    var receipt = RawReplayArchive.Execute(bytes, sha => sha == account.Policy.DatasetSha256 ? PaperReadArtifact(PaperInputPath(original), original.Sha256, DatasetJson.MaximumBytes) : throw new ResearchException("raw-paper-dataset"), deadline.Token);
                    var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
                    if (result.DividendEntitlements.Length != 0 || result.TerminalEntitlements.Length != 0) throw new ResearchException("raw-paper-actions-unsupported");
                    var receiptBytes = RawBytes(receipt); if (receiptBytes.Length > 8 * 1024 * 1024) throw new ResearchException("raw-paper-capacity");
                    deadline.Token.ThrowIfCancellationRequested(); EnsureArtifactCapacity(receiptBytes); ImmutableWrite(PaperOperationPath(row.Id, "receipt"), receiptBytes);
                    row = row with { Status = "succeeded", ReceiptSha256 = RawHash(receiptBytes), FinishedAt = LaterPaper(clock.GetUtcNow(), at) };
                    var completed = account with { Status = account.AcceptedPeriods + 1 == account.Policy.Decisions.Length - RawUniverseDecision.HistoryDecisions ? "completed" : "active", AcceptedPeriods = account.AcceptedPeriods + 1, HeadOperationId = row.Id, HeadReceiptSha256 = row.ReceiptSha256, Balance = Balance(result) };
                    SavePaper(completed, row); return row;
                }
                catch (Exception e) when (e is ResearchException or IOException or UnauthorizedAccessException or JsonException or FormatException or OverflowException or OperationCanceledException)
                {
                    var error = e is ResearchException r ? r.Code : e is OperationCanceledException ? "raw-paper-interrupted" : e is IOException or UnauthorizedAccessException ? "raw-paper-input-unavailable" : "raw-paper-execution-failed";
                    row = row with { Status = e is OperationCanceledException ? "interrupted" : "failed", ErrorCode = error, FinishedAt = LaterPaper(clock.GetUtcNow(), at) };
                    SavePaper(account with { Status = "failed" }, row); return row;
                }
            }
            finally { EndComputation(); }
        }
    }
    public RawPaperAccount StopRawPaperAccount(string id)
    {
        lock (gate)
        {
            var account = RawPaperAccounts().SingleOrDefault(a => a.Id == id) ?? throw new ResearchException("raw-paper-not-found");
            if (account.Status is "stopped" or "failed" or "completed") return account;
            var stopped = account with { Status = "stopped" }; SavePaper(stopped); return stopped;
        }
    }
    private static RawPaperBalance Balance(RawEventReplayResult result) => new(result.CashGbp, result.ContributionsGbp, result.FeesGbp, result.Holdings, result.Marks[^1].Valuation.HoldingsAndCash.EquityGbp, result.Marks[^1].Valuation.HoldingsAndCash.At);
}
