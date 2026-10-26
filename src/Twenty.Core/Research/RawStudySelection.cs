using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Twenty.Core.Research;

// The service supplies only immutable, hash-checked artifacts already independently replayed by its worker.
public sealed record RawStudySelectionObservation(string TrialId, string ReportSha256, decimal MomentumGbp, decimal EqualWeightGbp, decimal CashGbp);
public sealed record RawStudyCandidateScore(string CandidateId, string BaseTrialId, string StressTrialId, string BaseReportSha256,
    string StressReportSha256, decimal BaseMomentumGbp, decimal StressMomentumGbp, decimal BaseComparatorGbp, decimal StressComparatorGbp, decimal WorstMarginGbp);
public sealed record RawStudySelectionResult(string Version, string PlanSha256, bool Synthetic, string Rule, string? SelectedCandidateId,
    ImmutableArray<RawStudyCandidateScore> Scores, ImmutableArray<string> Limitations);

public static class RawStudySelection
{
    public const string Version = "raw-study-selection-v1";
    public static RawStudySelectionResult Evaluate(RawUniverseStudyPlan plan, IEnumerable<RawStudySelectionObservation> observations)
    {
        var bytes = RawUniverseStudyProtocol.Encode(plan); var rows = observations.Take(9).ToArray();
        if (rows.Length != 8 || rows.Any(r => r is null || r.ReportSha256 is not { Length:64 } || !r.ReportSha256.All(char.IsAsciiHexDigitLower) ||
            r.MomentumGbp < 0 || r.EqualWeightGbp < 0 || r.CashGbp < 0) || rows.Select(r=>r.ReportSha256).Distinct(StringComparer.Ordinal).Count()!=8 ||
            !rows.Select(r=>r.TrialId).Order(StringComparer.Ordinal).SequenceEqual(plan.DevelopmentTrials.Select(t=>t.Id).Order(StringComparer.Ordinal)))
            throw new ResearchException("raw-study-selection-family");
        var scores = plan.Candidates.Select(candidate =>
        {
            var a=rows.Single(r=>r.TrialId==candidate.Id+":base");var b=rows.Single(r=>r.TrialId==candidate.Id+":stress");
            var ac=Math.Max(a.EqualWeightGbp,a.CashGbp);var bc=Math.Max(b.EqualWeightGbp,b.CashGbp);
            return new RawStudyCandidateScore(candidate.Id,a.TrialId,b.TrialId,a.ReportSha256,b.ReportSha256,a.MomentumGbp,b.MomentumGbp,ac,bc,
                Math.Min(a.MomentumGbp-ac,b.MomentumGbp-bc));
        }).OrderBy(s=>s.CandidateId,StringComparer.Ordinal).ToImmutableArray();
        var best=scores.OrderByDescending(s=>s.WorstMarginGbp).ThenBy(s=>s.CandidateId,StringComparer.Ordinal).First();
        return new(Version,Convert.ToHexStringLower(SHA256.HashData(bytes)),plan.Synthetic,plan.SelectionRule,
            plan.Synthetic || best.WorstMarginGbp > 0 ? best.CandidateId : null,scores,
            ["Selection uses all eight original worker-verified development artifacts; this arithmetic does not authenticate independently supplied observations.",
             "The frozen worst base/stress ending-wealth margin and ordinal tie rule are an engineering screen, not uncertainty adjustment, evidence of profitability or investment nomination.",
             "Synthetic selection is lifecycle-only. No final evaluation, holdout spending, paper promotion or broker authority is granted."]);
    }
}
