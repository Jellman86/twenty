using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawTerminalMarkAssumption(string ListingId, string ActionId, string? Currency,
    decimal ExpectedWithheldLocal, decimal ExpectedFeeGbp, DateTimeOffset AvailableAt, string SourceReference, string ContentSha256);
public sealed record RawTerminalReceivableValue(RawTerminalEntitlement Entitlement, RawTerminalMarkAssumption Assumption,
    string? FxId, decimal? GbpPerLocalUnit, decimal ExpectedNetLocal, RawPositionValue? Successor,
    decimal ExpectedValueGbp);
public sealed record RawGbpClaimValuation(RawGbpValuation HoldingsAndCash,
    ImmutableArray<RawDividendReceivableValue> DividendReceivables,
    ImmutableArray<RawTerminalReceivableValue> TerminalReceivables,
    decimal ExpectedDividendReceivablesGbp, decimal ExpectedTerminalReceivablesGbp, decimal ExpectedEquityGbp);

public sealed partial class RawGbpLedger
{
    /// <summary>Complete, read-only accrual mark. Every outstanding claim needs explicit assumptions;
    /// estimates never alter cash, quantities, fees or settlement receipts.</summary>
    public RawGbpClaimValuation ValueWithReceivables(DataSlice market, TimeSpan maximumPriceAge, TimeSpan maximumFxAge,
        FxPurpose purpose, ImmutableArray<RawDividendMarkAssumption> dividendAssumptions,
        ImmutableArray<RawTerminalMarkAssumption> terminalAssumptions)
    {
        var portfolio = ValueCore(market, maximumPriceAge, maximumFxAge, purpose,
            requireSettledDividends: false, requireSettledTerminals: false);
        var income = MarkDividends(market, maximumFxAge, purpose, ValidateDividendMarks(market, dividendAssumptions, DividendEntitlements));
        var pending = TerminalEntitlements.Where(t => t.SettledAt is null).ToArray();
        if (terminalAssumptions.IsDefault || terminalAssumptions.Length != pending.Length || terminalAssumptions.Any(a => a is null) ||
            terminalAssumptions.Select(a => (a.ListingId, a.ActionId)).Distinct().Count() != terminalAssumptions.Length)
            throw new ResearchException("raw-ledger-terminal-valuation");
        var assumptions = terminalAssumptions.ToDictionary(a => (a.ListingId, a.ActionId));
        var values = ImmutableArray.CreateBuilder<RawTerminalReceivableValue>();
        foreach (var claim in pending)
        {
            if (!assumptions.TryGetValue((claim.Action.ListingId, claim.Action.ActionId), out var assumption) ||
                assumption.Currency != claim.Action.CashCurrency || assumption.ExpectedWithheldLocal < 0 ||
                assumption.ExpectedWithheldLocal > claim.GrossLocal || assumption.ExpectedFeeGbp < 0 ||
                assumption.AvailableAt == default || assumption.AvailableAt > market.DecisionAt ||
                !LedgerSource(assumption.SourceReference, assumption.ContentSha256))
                throw new ResearchException("raw-ledger-terminal-valuation");
            decimal? rate = null; string? fxId = null; RawPositionValue? successorValue = null;
            var net = claim.GrossLocal - assumption.ExpectedWithheldLocal;
            decimal grossGbp;
            if (claim.Action.TerminalOutcome == TerminalOutcome.Cash)
            {
                if (net > 0)
                {
                    var conversion = Conversion(market, claim.Action.CashCurrency!, purpose, maximumFxAge);
                    rate = conversion.Rate; fxId = conversion.Id;
                }
                grossGbp = net * (rate ?? 0);
            }
            else
            {
                var successor = ListingAt(market, claim.Action.SuccessorListingId!);
                if (successor.Status != ListingStatus.Active || terminals.Keys.Any(k => k.ListingId == successor.ListingId))
                    throw new ResearchException("raw-ledger-terminal-successor");
                // Frozen exchange quantity cannot be repriced through an unmodelled split,
                // distribution or further exit, even when an existing held lot was adjusted.
                if (market.EffectiveActions(successor.ListingId).Any(a => a.EffectiveAt >= claim.Action.EffectiveAt && !IsNeutralAction(a)))
                    throw new ResearchException("raw-ledger-terminal-successor-history");
                var price = market.Prices(successor.ListingId, PriceBasis.Raw).Where(p => p.SessionCloseAt <= market.DecisionAt)
                    .OrderByDescending(p => p.SessionCloseAt).FirstOrDefault();
                if (price is null || market.DecisionAt - price.SessionCloseAt > maximumPriceAge)
                    throw new ResearchException("raw-ledger-price");
                if (price.Denomination != successor.Denomination) throw new ResearchException("raw-ledger-units");
                var conversion = Conversion(market, successor.Denomination.Currency, purpose, maximumFxAge);
                rate = conversion.Rate; fxId = conversion.Id;
                grossGbp = claim.SuccessorQuantity * price.Denomination.ToMajor(price.Close) * rate.Value;
                successorValue = new(successor.ListingId, claim.SuccessorQuantity, price.Id, fxId,
                    price.Close, price.Denomination, rate.Value, grossGbp);
            }
            values.Add(new(claim, assumption, fxId, rate, net, successorValue, grossGbp - assumption.ExpectedFeeGbp));
        }
        var dividendGbp = income.Sum(v => v.ExpectedValueGbp);
        var terminalGbp = values.Sum(v => v.ExpectedValueGbp);
        return new(portfolio, income, values.ToImmutable(), dividendGbp, terminalGbp,
            portfolio.EquityGbp + dividendGbp + terminalGbp);
    }
}
