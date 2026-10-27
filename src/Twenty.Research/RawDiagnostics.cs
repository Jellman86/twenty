using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed partial class DiagnosticStore
{
    private async Task<DiagnosticAttempt> AnalyseRaw(DiagnosticAttempt attempt, RawStudyTrialEntry anchor,
        ImmutableArray<RunEntry> models, ImmutableArray<UniverseRunEntry> universe, ImmutableArray<RawStudyTrialEntry> raw,
        ImmutableArray<RawResearchStudy> studies, CancellationToken ct)
    {
        var study = studies.Single(s => s.Id == anchor.StudyId);
        string Reason(RawStudyTrialEntry row) => !studies.Single(s => s.Id == row.StudyId).DevelopmentTrialIds.Contains(row.Id)
            ? "reserved-final-evaluation" : row.StudyId != study.Id ? "different-registered-family"
            : row.CostScenario != anchor.CostScenario ? "different-cost-scenario"
            : row.Status != "succeeded" || row.ReportSha256 is null ? "incomplete-or-failed-trial" : "included";
        attempt = attempt with { DatasetId = anchor.DatasetSha256, Synthetic = anchor.Synthetic, FamilyEngine = "raw-universe",
            RawPlanSha256 = study.PlanSha256, RawCostScenario = anchor.CostScenario,
            Trials = models.Select(r => new DiagnosticTrial(r.Id, r.DatasetId, r.Status, r.ConfigSha256, r.ReportSha256,
                    r.EngineSha256, false, "different-research-engine"))
                .Concat(universe.Select(r => UniverseTrial(r, "different-research-engine")))
                .Concat(raw.Select(r => RawTrial(r, Reason(r), studies.Single(s => s.Id == r.StudyId).Plan))).ToImmutableArray() };
        Save(attempt); // Preserve the complete classifications before any artifact load/refusal.
        if (!study.DevelopmentTrialIds.Contains(anchor.Id) || anchor.Status != "succeeded" || anchor.ReportSha256 is null)
            throw new ResearchException("diagnostic-development-run-required");
        var required = study.Plan.DevelopmentTrials.Where(t => t.CostScenario == anchor.CostScenario).ToArray();
        var family = raw.Where(r => r.StudyId == study.Id && study.DevelopmentTrialIds.Contains(r.Id) && r.CostScenario == anchor.CostScenario).ToArray();
        if (required.Length != 4 || family.Length != 4 || !required.Select(t => t.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(family.Select(r => r.TrialId).Order(StringComparer.Ordinal)) || family.Any(r => r.Status != "succeeded" || r.ReportSha256 is null))
            throw new ResearchException("raw-diagnostic-family-incomplete");
        attempt = attempt with { RawReportVersion = RawStudyDevelopmentRun.Version }; Save(attempt);
        var streams = new List<DiagnosticReturns>();
        foreach (var row in family.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            // A mutable register cannot replace the immutable snapshot's input bindings.
            CheckRawSnapshot(row);
            byte[] request, bytes;
            try { request = research.RawTrialRequestBytes(row.Id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new ResearchException("raw-trial-request-integrity"); }
            try { bytes = research.RawTrialReportBytes(row.Id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new ResearchException("raw-study-report-integrity"); }
            if (RawDiagnosticHash(request) != row.RequestSha256 || RawDiagnosticHash(bytes) != row.ReportSha256)
                throw new ResearchException("diagnostic-snapshot-changed");
            CheckRawSnapshot(row);
            var report = RawDiagnosticRead<RawStudyDevelopmentReport>(bytes, RawStudyDevelopmentRun.MaximumReportBytes);
            try { ValidateRawDiagnosticReport(study, row, report, ct); }
            catch (OverflowException) { throw new ResearchException("diagnostic-report-contract"); }
            var momentum = report.Strategies[0];
            streams.Add(new(row.Id, row.ReportSha256!, momentum.Months.Select(m => DateOnly.FromDateTime(m.EndAt.UtcDateTime)).ToImmutableArray(),
                momentum.Months.Select(m => (double)m.PeriodReturn).ToImmutableArray(),
                report.Strategies.Skip(1).ToImmutableDictionary(s => s.Strategy, s => s.Months.Select(m => (double)m.PeriodReturn).ToImmutableArray())));
        }
        var result = await Task.Run(() => SearchDiagnostics.BuildRaw(streams, ct, attempt.Trials.Length), ct);
        ct.ThrowIfCancellationRequested();
        return attempt with { Report = result };
    }

    private void CheckRawSnapshot(RawStudyTrialEntry snapshot)
    {
        if (research.ListRawTrials().SingleOrDefault(r => r.Id == snapshot.Id) != snapshot)
            throw new ResearchException("diagnostic-snapshot-changed");
    }
    private static string RawDiagnosticHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static T RawDiagnosticRead<T>(byte[] bytes, int maximum)
    {
        if (bytes.Length is 0 || bytes.Length > maximum) throw new ResearchException("diagnostic-report-contract");
        try { return JsonSerializer.Deserialize<T>(bytes, RawReplayJson.Options) ?? throw new ResearchException("diagnostic-report-contract"); }
        catch (JsonException) { throw new ResearchException("diagnostic-report-contract"); }
    }
    private static byte[] RawDiagnosticDecode(string text, string sha, int maximum)
    {
        if (text is null || text.Length > 4L * ((maximum + 2L) / 3)) throw new ResearchException("diagnostic-report-contract");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); }
        catch (FormatException) { throw new ResearchException("diagnostic-report-contract"); }
        if (bytes.Length > maximum || RawDiagnosticHash(bytes) != sha) throw new ResearchException("diagnostic-report-contract");
        return bytes;
    }
    private static void RawDiagnosticVersion(byte[] bytes, string name, string expected)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                throw new ResearchException("diagnostic-report-contract");
            if (value.GetString() != expected) throw new ResearchException("raw-diagnostic-report-era");
        }
        catch (JsonException) { throw new ResearchException("diagnostic-report-contract"); }
    }

    // Structural consistency of published evidence, not a replacement for worker ledger replay.
    // Only development receipts are parsed; no dataset original or final report is read.
    private static void ValidateRawDiagnosticReport(RawResearchStudy study, RawStudyTrialEntry row, RawStudyDevelopmentReport report, CancellationToken ct)
    {
        if (report.Version != RawStudyDevelopmentRun.Version) throw new ResearchException("raw-diagnostic-report-era");
        if (report.PlanSha256 != study.PlanSha256 || report.TrialId != row.TrialId || report.DatasetSha256 != row.DatasetSha256 ||
            report.Strategies.IsDefault || report.Strategies.Any(s => s is null) ||
            !report.Strategies.Select(s => s.Strategy).SequenceEqual(new[] { "momentum", "equal_weight", "cash" }))
            throw new ResearchException("diagnostic-report-contract");
        foreach (var strategy in report.Strategies)
        {
            ct.ThrowIfCancellationRequested();
            var receipt = strategy.Receipt ?? throw new ResearchException("diagnostic-report-contract");
            if (receipt.Version != RawReplayArchive.Version) throw new ResearchException("raw-diagnostic-report-era");
            var requestBytes = RawDiagnosticDecode(receipt.RequestBase64, receipt.RequestSha256, RawReplayArchive.MaximumRequestBytes);
            var resultBytes = RawDiagnosticDecode(receipt.ResultBase64, receipt.ResultSha256, RawReplayArchive.MaximumResultBytes);
            RawDiagnosticVersion(requestBytes, "version", RawReplayArchive.Version);
            RawDiagnosticVersion(requestBytes, "engineVersion", RawEventReplay.Version);
            RawDiagnosticVersion(resultBytes, "version", RawEventReplay.Version);
            var request = RawDiagnosticRead<RawReplayDocument>(requestBytes, RawReplayArchive.MaximumRequestBytes);
            var result = RawDiagnosticRead<RawEventReplayResult>(resultBytes, RawReplayArchive.MaximumResultBytes);
            if (request.Build != study.Plan.Build ||
                request.Study is not { } binding || binding.Version != report.Version || binding.PlanSha256 != study.PlanSha256 ||
                binding.PlanBase64 != study.PlanBase64 || binding.TrialId != row.TrialId || binding.Strategy != strategy.Strategy ||
                request.Events.IsDefaultOrEmpty || request.Events.Length > 10000 || request.Events.Any(e => e is null || e.Operation is null || e.DatasetSha256 != row.DatasetSha256) ||
                result.Marks.IsDefault || result.Marks.Any(m => m is null || m.Valuation is null ||
                    m.Valuation.HoldingsAndCash is null || m.Valuation.DividendReceivables.IsDefault || m.Valuation.TerminalReceivables.IsDefault ||
                    m.Valuation.DividendReceivables.Any(c => c is null || c.Assumption is null) ||
                    m.Valuation.TerminalReceivables.Any(c => c is null || c.Assumption is null)) ||
                strategy.Months.IsDefaultOrEmpty || strategy.Months.Length != study.Plan.DevelopmentContributions.Length ||
                result.Marks.Length != 2 * strategy.Months.Length || strategy.ContributionsGbp != study.Plan.MonthlyGbp * strategy.Months.Length ||
                strategy.ContributionsGbp != result.ContributionsGbp || strategy.FeesGbp != result.FeesGbp ||
                strategy.EndingEquityGbp != strategy.Months[^1].EndingEquityGbp)
                throw new ResearchException("diagnostic-report-contract");
            var deposits = request.Events.Where(e => e.Operation is RawReplayDeposit).ToArray();
            if (deposits.Length != strategy.Months.Length) throw new ResearchException("diagnostic-report-contract");
            for (var i = 0; i < strategy.Months.Length; i++)
            {
                var month = strategy.Months[i]; var deposit = study.Plan.DevelopmentContributions[i];
                var end = i + 1 < strategy.Months.Length ? study.Plan.DevelopmentContributions[i + 1].At : study.Plan.DevelopmentEnd;
                var opening = result.Marks[2 * i]; var closing = result.Marks[2 * i + 1];
                if (month is null || month.DecisionAt != deposit.At || month.EndAt != end || month.ContributionGbp != deposit.AmountGbp ||
                    deposits[i].Id != "deposit-" + i || deposits[i].At != deposit.At || ((RawReplayDeposit)deposits[i].Operation).AmountGbp != deposit.AmountGbp ||
                    opening.Id != "mark-open-" + i || closing.Id != "mark-" + i ||
                    opening.Valuation.HoldingsAndCash.At != deposit.At || closing.Valuation.HoldingsAndCash.At != end ||
                    month.OpeningEquityAfterDepositGbp <= 0 || month.OpeningEquityAfterDepositGbp != opening.Valuation.ExpectedEquityGbp ||
                    month.EndingEquityGbp != closing.Valuation.ExpectedEquityGbp || month.CashGbp != closing.Valuation.HoldingsAndCash.CashGbp ||
                    month.PeriodReturn != month.EndingEquityGbp / month.OpeningEquityAfterDepositGbp - 1 ||
                    closing.Valuation.DividendReceivables.IsDefault || closing.Valuation.TerminalReceivables.IsDefault ||
                    month.PendingClaimCount != closing.Valuation.DividendReceivables.Length + closing.Valuation.TerminalReceivables.Length ||
                    month.ExpectedClaimValueGbp != closing.Valuation.ExpectedDividendReceivablesGbp + closing.Valuation.ExpectedTerminalReceivablesGbp ||
                    month.ExpectedClaimFeesGbp != closing.Valuation.DividendReceivables.Sum(c => c.Assumption.ExpectedFeeGbp) +
                        closing.Valuation.TerminalReceivables.Sum(c => c.Assumption.ExpectedFeeGbp))
                    throw new ResearchException("diagnostic-report-contract");
            }
            if (strategy.Months[0].OpeningEquityAfterDepositGbp != study.Plan.DevelopmentContributions[0].AmountGbp ||
                !result.Marks[^1].Valuation.DividendReceivables.IsEmpty || !result.Marks[^1].Valuation.TerminalReceivables.IsEmpty)
                throw new ResearchException("diagnostic-report-contract");
        }
    }
}
