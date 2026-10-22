using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public enum ExecutionRefusal
{
    InvalidOrder = 0, NoNextSession = 1, NoOpen = 2, OpenNotObservable = 3,
    BelowMinimum = 4, VolumeParticipation = 5, UnsupportedAction = 6
}

public sealed record FillPolicy(decimal HalfSpreadBps = 5m, decimal SlippageBps = 5m,
    decimal MinimumOrderValue = 1m, decimal MaximumParticipation = 0.05m, decimal QuantityStep = 0m)
{
    /// <summary>Spread and slippage move onto the fill price rather than being charged as a fee,
    /// so the executed price stays comparable with the observed open.</summary>
    public decimal Rate => (HalfSpreadBps + SlippageBps) / 10000m;
    public void Validate()
    {
        // QuantityStep 0 means fractional quantities, the declared default: a fractional fill is a
        // broker product feature rather than a market fact, but the caller may step down to shares.
        if (HalfSpreadBps is < 0 or > 5000 || SlippageBps is < 0 or > 5000 || MinimumOrderValue <= 0 ||
            MaximumParticipation is <= 0 or > 1 || QuantityStep < 0)
            throw new ResearchException("invalid-execution-policy");
    }
}

/// <summary>A desired trade, in the listing's own major currency units. Quantity is derived at the
/// fill price, never assumed at the price the decision was made on.</summary>
public sealed record PlannedOrder(string ListingId, bool Buy, decimal NotionalMajor,
    DateTimeOffset DecisionAt, DateTimeOffset NextDecisionAt);

public sealed record ExecutionFill(string ListingId, bool Buy, decimal Quantity, decimal FillPrice,
    decimal ObservedOpen, Denomination Denomination, string PriceId, DateTimeOffset FillSessionCloseAt)
{
    /// <summary>Modelled execution versus the open it derives from: the distance a reader can audit
    /// instead of finding it buried in a cost rate.</summary>
    public decimal SlippageFromOpen => ObservedOpen <= 0 ? 0 : FillPrice / ObservedOpen - 1m;
}

public sealed record ExecutionRejection(string ListingId, bool Buy, ExecutionRefusal Reason, decimal NotionalMajor);
public sealed record ExecutionPlan(ImmutableArray<ExecutionFill> Fills, ImmutableArray<ExecutionRejection> Rejections);

/// <summary>Decides what a desired trade could have achieved. The decision is taken at the end of a
/// session and the order fills at the <b>next</b> session's open on the raw basis, so a trade is
/// never priced at the close that was just observed. Orders that cannot be priced, sized or
/// sustained are refused with a reason and never approximated. Funding is deliberately not decided
/// here: it is the ledger's job, and it refuses an unfunded fill atomically.</summary>
public static class ExecutionPlanner
{
    public const string Version = "execution-v1";

    public static ExecutionPlan Plan(DataSlice market, IEnumerable<PlannedOrder> orders, FillPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        var fills = ImmutableArray.CreateBuilder<ExecutionFill>();
        var rejections = ImmutableArray.CreateBuilder<ExecutionRejection>();
        foreach (var order in orders)
        {
            if (order is null) continue;
            if (string.IsNullOrWhiteSpace(order.ListingId) || order.NotionalMajor <= 0 ||
                order.DecisionAt == default || order.NextDecisionAt <= order.DecisionAt)
            { rejections.Add(new(order.ListingId ?? "", order.Buy, ExecutionRefusal.InvalidOrder, order.NotionalMajor)); continue; }

            var bars = market.Prices(order.ListingId, PriceBasis.Raw);
            var listing = market.ListingsAt(order.DecisionAt).SingleOrDefault(l => l.ListingId == order.ListingId);
            if (listing is not { Status: ListingStatus.Active })
            { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.InvalidOrder, order.NotionalMajor)); continue; }

            // The fill session is the first one strictly after the decision. Nothing fills at the
            // decision session's close: that price was the input to the decision, not a quote.
            var bar = bars.FirstOrDefault(b => b.SessionCloseAt > order.DecisionAt && b.SessionCloseAt <= order.NextDecisionAt);
            if (bar is null) { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.NoNextSession, order.NotionalMajor)); continue; }
            if (bar.Open <= 0) { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.NoOpen, order.NotionalMajor)); continue; }
            if (bar.Evidence.AvailableAt > order.NextDecisionAt)
            { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.OpenNotObservable, order.NotionalMajor)); continue; }
            // A structural change between the decision and the fill makes the derived quantity wrong.
            if (market.EffectiveActions(order.ListingId).Any(a => a.EffectiveAt > order.DecisionAt &&
                a.EffectiveAt <= bar.SessionCloseAt && a.Action is CorporateActionKind.Split or CorporateActionKind.Dividend
                    or CorporateActionKind.Merger or CorporateActionKind.SpinOff or CorporateActionKind.Delisting))
            { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.UnsupportedAction, order.NotionalMajor)); continue; }

            var fillPrice = bar.Open * (order.Buy ? 1m + policy.Rate : 1m - policy.Rate);
            if (fillPrice <= 0) { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.NoOpen, order.NotionalMajor)); continue; }
            var quantity = order.NotionalMajor / fillPrice;
            if (policy.QuantityStep > 0) quantity = decimal.Floor(quantity / policy.QuantityStep) * policy.QuantityStep;
            var gross = quantity * fillPrice;
            if (quantity <= 0 || gross < policy.MinimumOrderValue)
            { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.BelowMinimum, order.NotionalMajor)); continue; }
            if (bar.Volume <= 0 || quantity > policy.MaximumParticipation * bar.Volume)
            { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.VolumeParticipation, order.NotionalMajor)); continue; }

            fills.Add(new(order.ListingId, order.Buy, quantity, fillPrice, bar.Open, bar.Denomination, bar.Id, bar.SessionCloseAt));
        }
        return new(fills.ToImmutable(), rejections.ToImmutable());
    }
}
