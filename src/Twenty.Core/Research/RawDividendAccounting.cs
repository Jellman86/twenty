using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawDividendEvidence(DateTimeOffset EntitlementAt, DateTimeOffset AvailableAt, string SourceReference, string ContentSha256);
public sealed record RawDividendEntitlement(string ListingId, string ActionId, string ActionObservationId,
    DateTimeOffset EffectiveAt, DateTimeOffset BookedAt, decimal Quantity, string Currency, decimal GrossLocal,
    RawDividendEvidence Evidence, DateTimeOffset? PaidAt = null, string? SourcePaymentId = null);
public sealed record RawDividendPayment(string SourcePaymentId, string SourceReference, string ContentSha256,
    DateTimeOffset PaidAt, DateTimeOffset AvailableAt, string Currency, decimal GrossLocal, decimal WithheldLocal,
    decimal FeeGbp, string? FxObservationId);

public sealed partial class RawGbpLedger
{
    private ImmutableDictionary<(string ListingId, string ActionId), RawDividendEntitlement> dividends =
        ImmutableDictionary<(string ListingId, string ActionId), RawDividendEntitlement>.Empty;
    private ImmutableHashSet<string> dividendPaymentIds = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    public ImmutableArray<RawDividendEntitlement> DividendEntitlements => dividends.Values.OrderBy(d => d.ListingId, StringComparer.Ordinal)
        .ThenBy(d => d.ActionId, StringComparer.Ordinal).ToImmutableArray();

    private static bool LedgerSource(string? reference, string? hash) =>
        !string.IsNullOrWhiteSpace(reference) && reference.Length <= 200 && hash is { Length: 64 } &&
        hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public void CaptureDividend(string id, DataSlice market, string listingId, string actionId, RawDividendEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(market); CheckEvent(id, market.DecisionAt); CheckBookedActions(market);
        var action = market.EffectiveActions(listingId).SingleOrDefault(a => a.ActionId == actionId);
        if (action is not { Action: CorporateActionKind.Dividend, CashPerShare: > 0 }) throw new ResearchException("raw-ledger-dividend");
        if (action.SplitRatio is not (null or 1) || action.SuccessorListingId is not null || action.NewSymbol is not null ||
            action.TerminalOutcome != TerminalOutcome.NotApplicable) throw new ResearchException("raw-ledger-action-complex");
        if (evidence is null || evidence.EntitlementAt != action.EffectiveAt || evidence.AvailableAt == default ||
            evidence.AvailableAt > market.DecisionAt || !LedgerSource(evidence.SourceReference, evidence.ContentSha256))
            throw new ResearchException("raw-ledger-dividend-evidence");
        var key = (listingId, actionId);
        if (bookedActions.ContainsKey(key)) throw new ResearchException("raw-ledger-action-already-booked");
        if (!holdings.TryGetValue(listingId, out var position) || position.OpenedAt >= action.EffectiveAt)
            throw new ResearchException("raw-ledger-action-position");
        if (HasQuantityHistoryAfter(listingId, action.EffectiveAt))
            throw new ResearchException("raw-ledger-action-history");
        CheckHoldings(market, action);
        var gross = position.Quantity * action.CashPerShare.Value;
        if (gross <= 0) throw new ResearchException("raw-ledger-dividend-amount");
        var entitlement = new RawDividendEntitlement(listingId, actionId, action.Id, action.EffectiveAt.ToUniversalTime(),
            market.DecisionAt.ToUniversalTime(), position.Quantity, action.CashCurrency!, gross, evidence);
        var next = dividends.Add(key, entitlement); var actions = bookedActions.Add(key, action);
        Record(new(id, market.DecisionAt.ToUniversalTime(), "dividend-entitlement", listingId, 0, null,
            new(action.CashCurrency!, QuoteUnit.Major), 0, 0, null, null, CashGbp)
        { ActionId = actionId, ActionObservationId = action.Id, ActionEffectiveAt = entitlement.EffectiveAt, DividendEntitlement = entitlement });
        dividends = next; bookedActions = actions;
    }
    public void PayDividend(string id, DataSlice market, string listingId, string actionId, RawDividendPayment payment, TimeSpan maximumFxAge)
    {
        ArgumentNullException.ThrowIfNull(market); CheckEvent(id, market.DecisionAt); CheckAge(maximumFxAge); CheckBookedActions(market);
        var key = (listingId, actionId);
        if (!dividends.TryGetValue(key, out var entitlement)) throw new ResearchException("raw-ledger-dividend-entitlement");
        if (entitlement.PaidAt is not null) throw new ResearchException("raw-ledger-dividend-paid");
        if (payment is null || string.IsNullOrWhiteSpace(payment.SourcePaymentId) || payment.SourcePaymentId.Length > 128 ||
            !LedgerSource(payment.SourceReference, payment.ContentSha256) || payment.PaidAt == default ||
            payment.PaidAt < entitlement.EffectiveAt || payment.PaidAt > market.DecisionAt ||
            payment.AvailableAt < payment.PaidAt || payment.AvailableAt > market.DecisionAt ||
            payment.Currency != entitlement.Currency || payment.GrossLocal != entitlement.GrossLocal ||
            payment.WithheldLocal < 0 || payment.WithheldLocal > payment.GrossLocal || payment.FeeGbp < 0)
            throw new ResearchException("raw-ledger-dividend-payment");
        if (dividendPaymentIds.Contains(payment.SourcePaymentId) || terminalSettlementIds.Contains(payment.SourcePaymentId)) throw new ResearchException("raw-ledger-dividend-payment-id");
        var net = payment.GrossLocal - payment.WithheldLocal;
        decimal? rate; string? fxId;
        if (net == 0)
        {
            if (payment.FxObservationId is not null) throw new ResearchException("raw-ledger-dividend-fx");
            rate = null; fxId = null;
        }
        else if (payment.Currency == "GBP")
        {
            if (payment.FxObservationId is not null) throw new ResearchException("raw-ledger-dividend-fx");
            rate = 1; fxId = null;
        }
        else
        {
            // Select the exact payment conversion, never a newer booking-date rate.
            // This slice establishes what was known when booked; freshness is relative to payment time.
            var fx = market.Observations.OfType<FxRate>().SingleOrDefault(f => f.Id == payment.FxObservationId);
            if (fx is null || fx.BaseCurrency != payment.Currency || fx.QuoteCurrency != "GBP" || fx.Purpose != FxPurpose.Executable ||
                fx.ObservedAt > payment.PaidAt || fx.Evidence.AvailableAt is not { } available || available > payment.PaidAt ||
                payment.PaidAt - fx.ObservedAt > maximumFxAge)
                throw new ResearchException("raw-ledger-dividend-fx");
            rate = fx.QuoteUnitsPerBase; fxId = fx.Id;
        }
        var change = net * (rate ?? 0) - payment.FeeGbp; var cash = CashGbp + change; var fees = FeesGbp + payment.FeeGbp;
        if (cash < 0) throw new ResearchException("raw-ledger-cash");
        var paid = entitlement with { PaidAt = payment.PaidAt.ToUniversalTime(), SourcePaymentId = payment.SourcePaymentId };
        var next = dividends.SetItem(key, paid); var paymentIds = dividendPaymentIds.Add(payment.SourcePaymentId);
        Record(new(id, market.DecisionAt.ToUniversalTime(), "dividend-payment", listingId, 0, null,
            new(payment.Currency, QuoteUnit.Major), payment.FeeGbp, change, fxId, rate, cash)
        { ActionId = actionId, ActionObservationId = entitlement.ActionObservationId, ActionEffectiveAt = entitlement.EffectiveAt,
            DividendEntitlement = paid, DividendPayment = payment });
        CashGbp = cash; FeesGbp = fees; dividends = next; dividendPaymentIds = paymentIds;
    }
}
