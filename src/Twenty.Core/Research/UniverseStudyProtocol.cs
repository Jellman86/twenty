using System.Collections.Immutable;

namespace Twenty.Core.Research;

public sealed record UniverseStudyCandidate(string Id, UniverseBacktestConfig Config);
public sealed record UniverseStudyPlan(string Version, string DatasetSha256, string UniverseId, bool Synthetic, string Purpose,
    string CodeSha256, string IdentityScheme, int MaximumPriceAgeDays, string UnitContract,
    DateTimeOffset DevelopmentStart, DateTimeOffset DevelopmentEnd, DateTimeOffset ReservedStart, DateTimeOffset ReservedEnd,
    ImmutableArray<DateTimeOffset> DevelopmentDecisions, ImmutableArray<DateTimeOffset> FinalDecisions,
    ResearchCosts BaseCosts, ResearchCosts StressCosts, ImmutableArray<UniverseStudyCandidate> Candidates);

/// <summary>Pure declaration from identity metadata and decision times, before reading
/// any price or realized return. Persistence, admission and final spending belong to the
/// research service and are not granted by possessing this declaration.</summary>
public static class UniverseStudyProtocol
{
    public const string Version = "changing-universe-study-v1";
    public const int ReservedPeriods = 24;
    public const int MinimumDevelopmentPeriods = 24;
    public static UniverseStudyPlan Declare(string datasetSha256, string universeId, bool synthetic,
        ImmutableArray<DateTimeOffset> decisions, int maximumPriceAgeDays, decimal monthly, ResearchCosts costs,
        string purpose, string codeSha256, string identityScheme)
    {
        static bool Sha(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
        if (!Sha(datasetSha256) || !Sha(codeSha256) || universeId is not { Length: >= 1 and <= 128 } ||
            string.IsNullOrWhiteSpace(universeId) || universeId.Any(char.IsControl) ||
            identityScheme is not { Length: >= 1 and <= 80 } || string.IsNullOrWhiteSpace(identityScheme) || identityScheme.Any(char.IsControl) || costs is null)
            throw new ResearchException("universe-study-declaration");
        if (purpose != (synthetic ? "engineering" : "research")) throw new ResearchException("study-purpose-provenance");
        if (decisions.IsDefault || maximumPriceAgeDays is < 1 or > 92) throw new ResearchException("universe-panel-decisions");
        ChangingUniversePanel.ValidateDecisions(decisions, TimeSpan.FromDays(maximumPriceAgeDays));
        // Normalize equivalent offset representations before recording exact plan bytes.
        decisions = decisions.Select(d => d.ToUniversalTime()).ToImmutableArray();
        costs.Validate();
        var stress = costs with { FxBps = costs.FxBps * 2, HalfSpreadBps = costs.HalfSpreadBps * 2,
            SlippageBps = costs.SlippageBps * 2, BuyTaxBps = costs.BuyTaxBps * 2 };
        stress.Validate();
        var warmup = UniverseBacktest.MomentumPeriods + 1;
        var reserved = decisions.Length - 1 - ReservedPeriods;
        var developmentEnd = reserved - 1; // One full selection/execution interval is unused.
        if (developmentEnd - warmup < MinimumDevelopmentPeriods)
            throw new ResearchException("universe-study-development-window-too-short");
        // Do not prune by future eligible counts, winners, failures or observed returns.
        var candidates = new[] { 1, 2, 4, 8 }.Select(n => new UniverseStudyCandidate("momentum:" + n,
            new UniverseBacktestConfig(monthly, n, 1m / n))).ToImmutableArray();
        foreach (var candidate in candidates) candidate.Config.Validate();
        return new(Version, datasetSha256, universeId, synthetic, purpose, codeSha256, identityScheme, maximumPriceAgeDays,
            "Local-currency adjusted-return ratios in GBP notionals; not achievable GBP wealth. No raw execution/FX or investment nomination is certified.",
            decisions[warmup], decisions[developmentEnd], decisions[reserved], decisions[^1],
            decisions.Take(developmentEnd + 1).ToImmutableArray(), decisions.Skip(reserved - warmup).ToImmutableArray(),
            costs, stress, candidates);
    }
}
