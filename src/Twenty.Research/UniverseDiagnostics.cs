using System.Collections.Immutable;
using System.Text.Json;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed partial class ResearchStore
{
    // All research admission and state transitions hold this gate. Take both registers
    // together so an analysis cannot disclose a snapshot assembled across admissions.
    internal (DateTimeOffset At, ImmutableArray<RunEntry> Models, ImmutableArray<UniverseRunEntry> Universes, ImmutableArray<RawStudyTrialEntry> Raw) DiagnosticSnapshot()
    {
        lock (gate)
        {
            var models = ListRuns().ToImmutableArray(); var universes = ListUniverseRuns().ToImmutableArray();
            return (clock.GetUtcNow(), models, universes, ListRawTrials().ToImmutableArray());
        }
    }
}

public sealed partial class DiagnosticStore
{
    private static DiagnosticTrial RawTrial(RawStudyTrialEntry run) =>
        new(run.Id, run.DatasetSha256, run.Status, run.RequestSha256, null, run.ResearchSha256, false, "different-research-engine") { ResearchEngine = "raw-universe" };
    private static DiagnosticTrial UniverseTrial(UniverseRunEntry run, string reason) =>
        new(run.Id, run.DatasetId, run.Status, run.RequestSha256, run.ReportSha256, run.ResearchSha256,
            reason == "included", reason) { ResearchEngine = "changing-universe" };

    private UniverseRunRequest UniverseRequest(UniverseRunEntry run)
    {
        var bytes = research.UniverseRequestBytes(run);
        var request = JsonSerializer.Deserialize<UniverseRunRequest>(bytes, ResearchStore.Json)
            ?? throw new ResearchException("universe-request-integrity");
        if (request.DatasetSha256 != run.DatasetSha256 || request.UniverseId != run.UniverseId)
            throw new ResearchException("universe-request-integrity");
        return request;
    }

    private async Task<DiagnosticAttempt> AnalyseUniverse(DiagnosticAttempt attempt, UniverseRunEntry anchor,
        ImmutableArray<RunEntry> models, ImmutableArray<UniverseRunEntry> universe, ImmutableArray<RawStudyTrialEntry> raw, CancellationToken ct)
    {
        attempt = attempt with { DatasetId = anchor.DatasetId, Synthetic = anchor.Synthetic, FamilyEngine = "changing-universe" };
        Save(attempt);
        if (anchor.Status != "succeeded" || anchor.StudyStage?.StartsWith("final-", StringComparison.Ordinal) == true || anchor.ReportSha256 is null || anchor.PanelSha256 is null ||
            anchor.IdentityScheme != ResearchIdentity.Scheme || anchor.ResearchSha256 is null)
            throw new ResearchException("diagnostic-development-run-required");
        var anchorRequest = UniverseRequest(anchor);
        var trials = ImmutableArray.CreateBuilder<DiagnosticTrial>();
        trials.AddRange(models.Select(run => new DiagnosticTrial(run.Id, run.DatasetId, run.Status, run.ConfigSha256,
            run.ReportSha256, run.EngineSha256, false, "different-research-engine")));
        trials.AddRange(raw.Select(RawTrial));
        var requests = new Dictionary<string, UniverseRunRequest>(StringComparer.Ordinal);
        foreach (var run in universe)
        {
            ct.ThrowIfCancellationRequested();
            var reason = run.Status != "succeeded" ? "incomplete-or-failed-trial"
                : run.StudyStage?.StartsWith("final-", StringComparison.Ordinal) == true ? "reserved-final-evaluation"
                : run.DatasetSha256 != anchor.DatasetSha256 ? "other-dataset"
                : run.UniverseId != anchor.UniverseId ? "other-universe"
                : run.IdentityScheme != anchor.IdentityScheme || run.ResearchSha256 != anchor.ResearchSha256 ? "different-engine-artifact"
                : run.PanelSha256 != anchor.PanelSha256 ? "different-panel" : "included";
            // Price-age policy can differ even if this sample happens to use the same
            // prices. Do not pool that policy change with concentration alternatives.
            if (reason is "included" or "different-panel")
            {
                var request = run.Id == anchor.Id ? anchorRequest : UniverseRequest(run);
                reason = request.MaximumPriceAgeDays != anchorRequest.MaximumPriceAgeDays ? "different-price-policy"
                    : !request.Decisions.SequenceEqual(anchorRequest.Decisions) ? "different-decision-grid"
                    : request.Costs != anchorRequest.Costs || request.Config.MonthlyGbp != anchorRequest.Config.MonthlyGbp ? "different-account-assumptions"
                    : reason;
                if (reason == "included") requests.Add(run.Id, request);
            }
            trials.Add(UniverseTrial(run, reason));
        }
        attempt = attempt with { Trials = trials.ToImmutable() }; Save(attempt);
        if (requests.Count > SearchDiagnostics.MaximumCandidates) throw new ResearchException("diagnostic-candidate-count");
        var streams = new List<DiagnosticReturns>();
        foreach (var run in universe.Where(r => requests.ContainsKey(r.Id)))
        {
            ct.ThrowIfCancellationRequested();
            var report = research.UniverseReport(run) ?? throw new ResearchException("diagnostic-report-unavailable");
            var request = requests[run.Id];
            if (report.DatasetId != run.DatasetId || report.Synthetic != run.Synthetic || report.UniverseId != run.UniverseId ||
                report.PanelSha256 != run.PanelSha256 || report.Config != request.Config || report.Costs != request.Costs ||
                report.EngineVersion != UniverseBacktest.Version ||
                !report.Equity.Select(e => e.Strategy).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                    .SequenceEqual(UniverseBacktest.Strategies.Order(StringComparer.Ordinal)))
                throw new ResearchException("diagnostic-report-contract");
            var candidate = report.Equity.Where(e => e.Strategy == "momentum").ToArray();
            var expected = request.Decisions.Skip(UniverseBacktest.MomentumPeriods + 1)
                .Zip(request.Decisions.Skip(UniverseBacktest.MomentumPeriods + 2), (from, to) =>
                    (DateOnly.FromDateTime(from.UtcDateTime), DateOnly.FromDateTime(to.UtcDateTime))).ToArray();
            if (report.EvaluationStart != request.Decisions[UniverseBacktest.MomentumPeriods + 1] ||
                report.EvaluationEnd != request.Decisions[^1] ||
                !candidate.Select(e => (e.DecisionDate, e.PeriodEnd)).SequenceEqual(expected))
                throw new ResearchException("diagnostic-report-contract");
            var comparisons = SearchDiagnostics.UniverseComparators.ToImmutableDictionary(name => name, name =>
            {
                var values = report.Equity.Where(e => e.Strategy == name).ToArray();
                if (!values.Select(e => (e.DecisionDate, e.PeriodEnd)).SequenceEqual(candidate.Select(e => (e.DecisionDate, e.PeriodEnd))))
                    throw new ResearchException("diagnostic-comparator-grid");
                return values.Select(e => (double)e.State.PeriodReturn).ToImmutableArray();
            });
            streams.Add(new(run.Id, run.ReportSha256!, candidate.Select(e => e.PeriodEnd).ToImmutableArray(),
                candidate.Select(e => (double)e.State.PeriodReturn).ToImmutableArray(), comparisons));
        }
        var result = await Task.Run(() => SearchDiagnostics.BuildUniverse(streams, ct, attempt.Trials.Length), ct);
        ct.ThrowIfCancellationRequested();
        return attempt with { Report = result };
    }
}
