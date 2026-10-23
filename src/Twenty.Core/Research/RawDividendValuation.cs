using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawDividendMarkAssumption(string ListingId, string ActionId, string Currency,
    decimal ExpectedWithheldLocal, decimal ExpectedFeeGbp, DateTimeOffset AvailableAt, string SourceReference, string ContentSha256);
public sealed record RawDividendReceivableValue(RawDividendEntitlement Entitlement, RawDividendMarkAssumption Assumption,
    string? FxId, decimal? GbpPerLocalUnit, decimal ExpectedNetLocal, decimal ExpectedValueGbp);
public sealed record RawGbpAccrualValuation(RawGbpValuation HoldingsAndCash,
    ImmutableArray<RawDividendReceivableValue> DividendReceivables, decimal ExpectedDividendReceivablesGbp, decimal ExpectedEquityGbp);

public sealed partial class RawGbpLedger
{
    public RawGbpAccrualValuation ValueWithDividendReceivables(DataSlice market, TimeSpan maximumPriceAge, TimeSpan maximumFxAge,
        FxPurpose purpose, ImmutableArray<RawDividendMarkAssumption> assumptions)
    {
        ArgumentNullException.ThrowIfNull(market);
        var pending = ValidateDividendMarks(market, assumptions, DividendEntitlements);
        // Preserve this API's settled-terminal contract. Only the complete wrapper
        // may mark pending terminal claims alongside dividends.
        var portfolio = ValueCore(market, maximumPriceAge, maximumFxAge, purpose, requireSettledDividends: false);
        var values = MarkDividends(market, maximumFxAge, purpose, pending);
        var income = values.Sum(v => v.ExpectedValueGbp);
        return new(portfolio, values, income, portfolio.EquityGbp + income);
    }

    private static ImmutableArray<(RawDividendEntitlement Claim, RawDividendMarkAssumption Assumption)> ValidateDividendMarks(
        DataSlice market, ImmutableArray<RawDividendMarkAssumption> assumptions, ImmutableArray<RawDividendEntitlement> entitlements)
    {
        var pending = entitlements.Where(d => d.PaidAt is null).ToArray();
        if (assumptions.IsDefault || assumptions.Length != pending.Length || assumptions.Any(a => a is null) ||
            assumptions.Select(a => (a.ListingId, a.ActionId)).Distinct().Count() != assumptions.Length)
            throw new ResearchException("raw-ledger-dividend-valuation");
        var byAction = assumptions.ToDictionary(a => (a.ListingId, a.ActionId));
        foreach (var claim in pending)
        {
            if (!byAction.TryGetValue((claim.ListingId, claim.ActionId), out var assumption) ||
                assumption.Currency != claim.Currency || assumption.ExpectedWithheldLocal < 0 || assumption.ExpectedWithheldLocal > claim.GrossLocal ||
                assumption.ExpectedFeeGbp < 0 || assumption.AvailableAt == default || assumption.AvailableAt > market.DecisionAt ||
                !LedgerSource(assumption.SourceReference, assumption.ContentSha256))
                throw new ResearchException("raw-ledger-dividend-valuation");
        }
        return pending.Select(claim => (claim, byAction[(claim.ListingId, claim.ActionId)])).ToImmutableArray();
    }

    private static ImmutableArray<RawDividendReceivableValue> MarkDividends(DataSlice market, TimeSpan maximumFxAge,
        FxPurpose purpose, ImmutableArray<(RawDividendEntitlement Claim, RawDividendMarkAssumption Assumption)> pending)
    {
        var values = ImmutableArray.CreateBuilder<RawDividendReceivableValue>();
        foreach (var (claim, assumption) in pending)
        {
            var net = claim.GrossLocal - assumption.ExpectedWithheldLocal;
            decimal? rate = null; string? fxId = null;
            if (net > 0)
            {
                var conversion = Conversion(market, claim.Currency, purpose, maximumFxAge);
                rate = conversion.Rate; fxId = conversion.Id;
            }
            values.Add(new(claim, assumption, fxId, rate, net, net * (rate ?? 0) - assumption.ExpectedFeeGbp));
        }
        return values.ToImmutable();
    }
}
