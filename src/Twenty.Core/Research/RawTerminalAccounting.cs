using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawTerminalEvidence(DateTimeOffset EntitlementAt, DateTimeOffset AvailableAt,
    string SourceReference, string ContentSha256);
public sealed record RawTerminalEntitlement(CorporateAction Action, DateTimeOffset BookedAt, decimal Quantity,
    decimal GrossLocal, decimal SuccessorQuantity, RawTerminalEvidence Evidence, DateTimeOffset PositionOpenedAt,
    DateTimeOffset? SettledAt = null, string? SourceSettlementId = null);
public sealed record RawTerminalSettlement(string SourceSettlementId, string SourceReference, string ContentSha256,
    DateTimeOffset SettledAt, DateTimeOffset AvailableAt, string? Currency, decimal GrossLocal, decimal WithheldLocal,
    decimal FeeGbp, string? FxObservationId, string? SuccessorListingId, decimal SuccessorQuantity);

public sealed partial class RawGbpLedger
{
    private ImmutableDictionary<(string ListingId, string ActionId), RawTerminalEntitlement> terminals =
        ImmutableDictionary<(string ListingId, string ActionId), RawTerminalEntitlement>.Empty;
    private ImmutableHashSet<string> terminalSettlementIds = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    public ImmutableArray<RawTerminalEntitlement> TerminalEntitlements => terminals.Values
        .OrderBy(t => t.Action.ListingId, StringComparer.Ordinal).ThenBy(t => t.Action.ActionId, StringComparer.Ordinal).ToImmutableArray();

    public void CaptureTerminal(string id, DataSlice market, string listingId, string actionId, RawTerminalEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(market); CheckEvent(id, market.DecisionAt); CheckBookedActions(market);
        var action = market.EffectiveActions(listingId).SingleOrDefault(a => a.ActionId == actionId);
        if (action is not { Action: CorporateActionKind.Delisting or CorporateActionKind.Merger } ||
            action.TerminalOutcome is not (TerminalOutcome.Zero or TerminalOutcome.Cash or TerminalOutcome.Stock))
            throw new ResearchException("raw-ledger-terminal");
        var validTerms = action.NewSymbol is null && (action.TerminalOutcome switch
        {
            TerminalOutcome.Zero => action.CashPerShare is null or 0 && action.SplitRatio is null or 1 && action.SuccessorListingId is null,
            TerminalOutcome.Cash => action.CashPerShare > 0 && action.SplitRatio is null or 1 && action.SuccessorListingId is null,
            TerminalOutcome.Stock => action.SplitRatio > 0 && action.SuccessorListingId is not null && action.SuccessorListingId != listingId && action.CashPerShare is null or 0,
            _ => false
        });
        if (!validTerms) throw new ResearchException("raw-ledger-action-complex");
        if (evidence is null || evidence.EntitlementAt != action.EffectiveAt || evidence.AvailableAt == default ||
            evidence.AvailableAt > market.DecisionAt || !LedgerSource(evidence.SourceReference, evidence.ContentSha256))
            throw new ResearchException("raw-ledger-terminal-evidence");
        var key = (listingId, actionId);
        if (bookedActions.ContainsKey(key) || terminals.Keys.Any(k => k.ListingId == listingId))
            throw new ResearchException("raw-ledger-action-already-booked");
        if (!holdings.TryGetValue(listingId, out var position) || position.OpenedAt >= action.EffectiveAt)
            throw new ResearchException("raw-ledger-action-position");
        if (Entries.Any(e => e.ListingId == listingId &&
            ((e.Kind is "buy" or "sell") && e.At >= action.EffectiveAt || e.Kind == "split" && e.ActionEffectiveAt >= action.EffectiveAt)))
            throw new ResearchException("raw-ledger-action-history");
        CheckHoldings(market, action);
        if (market.EffectiveActions(listingId).Any(a => a != action && a.EffectiveAt >= action.EffectiveAt && !IsNeutralAction(a)))
            throw new ResearchException("raw-ledger-action-complex");
        var gross = action.TerminalOutcome == TerminalOutcome.Cash ? position.Quantity * action.CashPerShare!.Value : 0;
        var successorQuantity = action.TerminalOutcome == TerminalOutcome.Stock ? position.Quantity * action.SplitRatio!.Value : 0;
        if (action.TerminalOutcome == TerminalOutcome.Cash && gross <= 0 || action.TerminalOutcome == TerminalOutcome.Stock && successorQuantity <= 0)
            throw new ResearchException("raw-ledger-terminal-amount");
        var claim = new RawTerminalEntitlement(action, market.DecisionAt.ToUniversalTime(), position.Quantity, gross, successorQuantity,
            evidence, position.OpenedAt, action.TerminalOutcome == TerminalOutcome.Zero ? action.EffectiveAt.ToUniversalTime() : null);
        var next = terminals.Add(key, claim); var positions = holdings.Remove(listingId); var actions = bookedActions.Add(key, action);
        Record(new(id, market.DecisionAt.ToUniversalTime(), "terminal-entitlement", listingId, -position.Quantity, null,
            position.Denomination, 0, 0, null, null, CashGbp)
        { ActionId = actionId, ActionObservationId = action.Id, ActionEffectiveAt = action.EffectiveAt, TerminalEntitlement = claim });
        terminals = next; holdings = positions; bookedActions = actions;
    }

    public void SettleTerminal(string id, DataSlice market, string listingId, string actionId,
        RawTerminalSettlement settlement, TimeSpan maximumFxAge)
    {
        ArgumentNullException.ThrowIfNull(market); CheckEvent(id, market.DecisionAt); CheckAge(maximumFxAge); CheckBookedActions(market);
        var key = (listingId, actionId);
        if (!terminals.TryGetValue(key, out var claim)) throw new ResearchException("raw-ledger-terminal-entitlement");
        if (claim.SettledAt is not null) throw new ResearchException("raw-ledger-terminal-settled");
        if (settlement is null || string.IsNullOrWhiteSpace(settlement.SourceSettlementId) || settlement.SourceSettlementId.Length > 128 ||
            !LedgerSource(settlement.SourceReference, settlement.ContentSha256) || settlement.SettledAt == default ||
            settlement.SettledAt < claim.Action.EffectiveAt || settlement.SettledAt > market.DecisionAt ||
            settlement.AvailableAt < settlement.SettledAt || settlement.AvailableAt > market.DecisionAt || settlement.FeeGbp < 0)
            throw new ResearchException("raw-ledger-terminal-settlement");
        if (terminalSettlementIds.Contains(settlement.SourceSettlementId) || dividendPaymentIds.Contains(settlement.SourceSettlementId))
            throw new ResearchException("raw-ledger-terminal-settlement-id");
        var positions = holdings; decimal change; decimal? rate = null; string? fxId = null;
        if (claim.Action.TerminalOutcome == TerminalOutcome.Cash)
        {
            if (settlement.Currency != claim.Action.CashCurrency || settlement.GrossLocal != claim.GrossLocal ||
                settlement.WithheldLocal < 0 || settlement.WithheldLocal > claim.GrossLocal ||
                settlement.SuccessorListingId is not null || settlement.SuccessorQuantity != 0)
                throw new ResearchException("raw-ledger-terminal-settlement");
            var net = settlement.GrossLocal - settlement.WithheldLocal;
            if (net == 0)
            {
                if (settlement.FxObservationId is not null) throw new ResearchException("raw-ledger-terminal-fx");
            }
            else
            {
                if (!ExecutionFx.TrySelect(market, settlement.Currency!, settlement.FxObservationId,
                    settlement.SettledAt, maximumFxAge, out var fx)) throw new ResearchException("raw-ledger-terminal-fx");
                rate = fx?.QuoteUnitsPerBase ?? 1; fxId = fx?.Id;
            }
            change = net * (rate ?? 0) - settlement.FeeGbp;
        }
        else
        {
            if (settlement.Currency is not null || settlement.GrossLocal != 0 || settlement.WithheldLocal != 0 || settlement.FxObservationId is not null ||
                settlement.SuccessorListingId != claim.Action.SuccessorListingId || settlement.SuccessorQuantity != claim.SuccessorQuantity)
                throw new ResearchException("raw-ledger-terminal-settlement");
            CheckHoldings(market);
            var successor = ListingAt(market, settlement.SuccessorListingId!);
            var atDelivery = market.ListingsAt(settlement.SettledAt).SingleOrDefault(l => l.ListingId == successor.ListingId);
            if (successor.Status != ListingStatus.Active || atDelivery is not { Status: ListingStatus.Active } || atDelivery.Denomination != successor.Denomination ||
                terminals.Keys.Any(k => k.ListingId == successor.ListingId)) throw new ResearchException("raw-ledger-terminal-successor");
            if (market.EffectiveActions(successor.ListingId).Any(a => a.EffectiveAt >= claim.Action.EffectiveAt && !IsNeutralAction(a)) ||
                Entries.Any(e => (e.ListingId == successor.ListingId || e.TerminalSettlement?.SuccessorListingId == successor.ListingId) && e.At >= settlement.SettledAt))
                throw new ResearchException("raw-ledger-terminal-successor-history");
            var held = holdings.GetValueOrDefault(successor.ListingId);
            if (held is not null && held.Denomination != successor.Denomination) throw new ResearchException("raw-ledger-units");
            positions = holdings.SetItem(successor.ListingId, new((held?.Quantity ?? 0) + claim.SuccessorQuantity,
                successor.Denomination, held?.OpenedAt ?? settlement.SettledAt.ToUniversalTime()));
            change = -settlement.FeeGbp;
        }
        var cash = CashGbp + change; var fees = FeesGbp + settlement.FeeGbp;
        if (cash < 0) throw new ResearchException("raw-ledger-cash");
        var settled = claim with { SettledAt = settlement.SettledAt.ToUniversalTime(), SourceSettlementId = settlement.SourceSettlementId };
        var next = terminals.SetItem(key, settled); var settlementIds = terminalSettlementIds.Add(settlement.SourceSettlementId);
        Record(new(id, market.DecisionAt.ToUniversalTime(), "terminal-settlement", listingId, 0, null,
            settlement.Currency is null ? null : new(settlement.Currency, QuoteUnit.Major), settlement.FeeGbp, change, fxId, rate, cash)
        { ActionId = actionId, ActionObservationId = claim.Action.Id, ActionEffectiveAt = claim.Action.EffectiveAt,
            TerminalEntitlement = settled, TerminalSettlement = settlement });
        terminals = next; terminalSettlementIds = settlementIds; holdings = positions; CashGbp = cash; FeesGbp = fees;
    }
}
