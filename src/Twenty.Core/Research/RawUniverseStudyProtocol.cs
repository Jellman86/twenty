using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Twenty.Core.Research;

public sealed record RawStudyCalendarPin(string CalendarId, string Venue, string Sha256);
public sealed record RawStudyExecutionPolicy(int MaximumPriceAgeDays, int MaximumFxAgeDays, decimal CashReserveGbp,
    decimal FeePerFillGbp, FillPolicy Fills);
public sealed record RawStudyContribution(DateTimeOffset At, decimal AmountGbp);
public sealed record RawStudyTrial(string Id, string CandidateId, string CostScenario);
public sealed record RawUniverseStudyPlan(string Version, string DatasetSha256, string UniverseId, bool Synthetic,
    string Purpose, string CodeSha256, string IdentityScheme, RawReplayBuildIdentity Build,
    ImmutableArray<DateTimeOffset> Decisions, decimal MonthlyGbp, ImmutableArray<RawStudyCalendarPin> Calendars,
    string ActionCatalogSha256, RawStudyExecutionPolicy BaseExecution, RawStudyExecutionPolicy StressExecution,
    DateTimeOffset DevelopmentStart, DateTimeOffset DevelopmentEnd, DateTimeOffset ReservedStart, DateTimeOffset ReservedEnd,
    ImmutableArray<DateTimeOffset> DevelopmentDecisions, ImmutableArray<DateTimeOffset> FinalDecisions,
    ImmutableArray<RawStudyContribution> DevelopmentContributions, ImmutableArray<RawStudyContribution> FinalContributions,
    ImmutableArray<UniverseStudyCandidate> Candidates, ImmutableArray<RawStudyTrial> DevelopmentTrials,
    int ReservedFinalTrials, string UnitContract, string StartingLedgerRule, string ExecutionRule, string SelectionRule,
    string CandidateAcceptanceRule, string FinalEvaluationRule,
    ImmutableArray<string> Limitations);
public sealed record RawStudyDevelopmentDecision(string PlanSha256, string DatasetSha256, string ActionCatalogSha256,
    ImmutableArray<RawStudyCalendarPin> Calendars, string TrialId, string CandidateId, string CostScenario,
    RawStudyContribution Contribution, DateTimeOffset NextDecisionAt, RawStudyExecutionPolicy Execution,
    ImmutableArray<RawUniverseStrategySpec> Strategies);

/// <summary>Metadata-only declaration. A plan is not an admitted trial or authority to
/// access reserved outcomes. Registration and permanent final spending remain separate.</summary>
public static class RawUniverseStudyProtocol
{
    public const string Version = "raw-universe-study-v1";
    public const int MaximumPlanBytes = 256 * 1024;

    public static RawUniverseStudyPlan Declare(string datasetSha256, string universeId, bool synthetic, string purpose,
        string codeSha256, string identityScheme, ImmutableArray<DateTimeOffset> decisions, decimal monthlyGbp,
        RawStudyExecutionPolicy execution, ImmutableArray<RawStudyCalendarPin> calendars, string actionCatalogSha256)
    {
        static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
        static bool Name(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
        if (!Hash(actionCatalogSha256)) throw new ResearchException("raw-study-action-catalog");
        if (calendars.IsDefaultOrEmpty || calendars.Length > 16 || calendars.Any(p => p is null ||
                !Name(p.CalendarId, 128) || !Name(p.Venue, 20) || !Hash(p.Sha256)) ||
            calendars.Select(p => p.CalendarId).Distinct(StringComparer.Ordinal).Count() != calendars.Length)
            throw new ResearchException("raw-study-calendar-pins");
        if (execution is null || execution.Fills is null || execution.MaximumPriceAgeDays is < 1 or > 92 ||
            execution.MaximumFxAgeDays is < 1 or > 92 || execution.CashReserveGbp < 0 || execution.FeePerFillGbp < 0)
            throw new ResearchException("raw-study-execution-policy");
        RawStudyExecutionPolicy stress;
        try
        {
            execution.Fills.Validate();
            stress = execution with { Fills = execution.Fills with
                { HalfSpreadBps = execution.Fills.HalfSpreadBps * 2, SlippageBps = execution.Fills.SlippageBps * 2 } };
            stress.Fills.Validate();
        }
        catch (ResearchException) { throw new ResearchException("raw-study-execution-policy"); }
        // Share only the established chronological split and finite candidate family.
        // Its notional costs and unit contract are neither emitted nor executed here.
        var windows = UniverseStudyProtocol.Declare(datasetSha256, universeId, synthetic, decisions,
            execution.MaximumPriceAgeDays, monthlyGbp, new(0, 0, 0, 0, 1), purpose, codeSha256, identityScheme);
        var canonical = decisions.Select(d => d.ToUniversalTime()).ToImmutableArray();
        static ImmutableArray<RawStudyContribution> Contributions(ImmutableArray<DateTimeOffset> grid, decimal monthly) =>
            grid.Skip(RawUniverseDecision.HistoryDecisions - 1).SkipLast(1).Select(at => new RawStudyContribution(at, monthly)).ToImmutableArray();
        return new(Version, datasetSha256, universeId, synthetic, purpose, codeSha256, identityScheme, RawReplayBuildIdentity.Current,
            canonical, monthlyGbp, calendars.OrderBy(p => p.CalendarId, StringComparer.Ordinal).ToImmutableArray(), actionCatalogSha256,
            execution, stress, windows.DevelopmentStart, windows.DevelopmentEnd, windows.ReservedStart, windows.ReservedEnd,
            windows.DevelopmentDecisions, windows.FinalDecisions,
            Contributions(windows.DevelopmentDecisions, monthlyGbp), Contributions(windows.FinalDecisions, monthlyGbp),
            windows.Candidates, windows.Candidates.SelectMany(c => new[] { "base", "stress" }
                .Select(cost => new RawStudyTrial(c.Id + ":" + cost, c.Id, cost))).ToImmutableArray(), 2,
            "Raw fractional quantities and GBP cash/equity using explicit executable-purpose FX; model assumptions, not broker quotes or investable wealth certification.",
            "empty-ledger-per-stage-no-transfer-v1", "deposit-before-decision-first-reviewed-open-by-next-decision-v1",
            "max-min-base-stress-ending-gbp-margin-over-best-equal-weight-or-cash-ordinal-tie-v1",
            "research-positive-worst-margin-or-no-candidate-engineering-lifecycle-only-v1",
            "one-explicit-spent-claim-two-trials-same-uuid-replay-no-reranking-v1",
            ["Metadata-only declaration; no market values, session tables or action payloads are read. Hashes are unverified original-input assertions until admission validates them.",
             "Eight development trials and two reserved final slots are declared, not registered or spent. No final-execution authority is granted by this plan.",
             "Each candidate/cost run requires independent momentum, equal-weight and cash ledgers with identical declared deposits; warm-up and final marks receive no deposits.",
             "Stress doubles spread and slippage only. Fixed GBP fees, minimums, participation, quantity steps, reserves and freshness limits stay unchanged; no extra FX markup, tax or settlement-cycle model is implied.",
             "Same-open sale proceeds may fund later purchases under the existing offline ledger convention. Unfilled orders refuse the bound batch; no cancellation or partial-execution recovery is certified.",
             "Source-wide action discovery, trial admission/disclosure, final spending, reports, paper accounts and genuine-data acceptance remain separate gates."]);
    }

    private static void Validate(RawUniverseStudyPlan plan)
    {
        if (plan is null || plan.Version != Version) throw new ResearchException("raw-study-version");
        if (plan.Build != RawReplayBuildIdentity.Current) throw new ResearchException("raw-study-build");
        if (plan.DevelopmentDecisions.IsDefault || plan.FinalDecisions.IsDefault || plan.DevelopmentContributions.IsDefault ||
            plan.FinalContributions.IsDefault || plan.Candidates.IsDefault || plan.DevelopmentTrials.IsDefault || plan.Limitations.IsDefault)
            throw new ResearchException("raw-study-plan-integrity");
        var expected = Declare(plan.DatasetSha256, plan.UniverseId, plan.Synthetic, plan.Purpose, plan.CodeSha256, plan.IdentityScheme,
            plan.Decisions, plan.MonthlyGbp, plan.BaseExecution, plan.Calendars, plan.ActionCatalogSha256);
        if (!RawReplayJson.Write(plan, MaximumPlanBytes).AsSpan().SequenceEqual(RawReplayJson.Write(expected, MaximumPlanBytes)))
            throw new ResearchException("raw-study-plan-integrity");
    }

    public static byte[] Encode(RawUniverseStudyPlan plan)
    {
        Validate(plan); return RawReplayJson.Write(plan, MaximumPlanBytes);
    }
    public static RawUniverseStudyPlan Decode(ReadOnlySpan<byte> bytes)
    {
        var plan = RawReplayJson.Read<RawUniverseStudyPlan>(bytes, MaximumPlanBytes); Validate(plan); return plan;
    }
    public static RawStudyDevelopmentDecision DevelopmentDecision(RawUniverseStudyPlan plan, string trialId, DateTimeOffset at)
    {
        Validate(plan);
        var trial = plan.DevelopmentTrials.SingleOrDefault(t => t.Id == trialId);
        var index = plan.DevelopmentDecisions.IndexOf(at);
        if (trial is null || index < RawUniverseDecision.HistoryDecisions - 1 || index >= plan.DevelopmentDecisions.Length - 1)
            throw new ResearchException("raw-study-development-decision");
        var candidate = plan.Candidates.Single(c => c.Id == trial.CandidateId);
        var history = plan.DevelopmentDecisions.Skip(index - RawUniverseDecision.HistoryDecisions + 1)
            .Take(RawUniverseDecision.HistoryDecisions).ToImmutableArray();
        var execution = trial.CostScenario == "base" ? plan.BaseExecution : plan.StressExecution;
        var planSha256 = Convert.ToHexStringLower(SHA256.HashData(RawReplayJson.Write(plan, MaximumPlanBytes)));
        return new(planSha256, plan.DatasetSha256, plan.ActionCatalogSha256, plan.Calendars,
            trial.Id, candidate.Id, trial.CostScenario, plan.DevelopmentContributions[index - RawUniverseDecision.HistoryDecisions + 1],
            plan.DevelopmentDecisions[index + 1], execution,
            new[] { "momentum", "equal_weight", "cash" }.Select(strategy => new RawUniverseStrategySpec(RawUniverseDecision.Version, plan.UniverseId,
                history, execution.MaximumPriceAgeDays, candidate.Config, strategy)).ToImmutableArray());
    }
}
