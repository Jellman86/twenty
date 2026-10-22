using System.Collections.Immutable;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public enum ExecutionRefusal
{
    InvalidOrder = 0, NoNextSession = 1, NoOpen = 2, OpenNotObservable = 3,
    BelowMinimum = 4, VolumeParticipation = 5, UnsupportedAction = 6, InvalidExecutionFx = 7
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
    DateTimeOffset DecisionAt, DateTimeOffset NextDecisionAt)
{
    public string? FxObservationId { get; init; }
}

public sealed record ExecutionFill(string ListingId, bool Buy, decimal Quantity, decimal FillPrice,
    decimal ObservedOpen, Denomination Denomination, string PriceId, DateTimeOffset FillSessionCloseAt)
{
    public required ExecutionSessionEvidence Timing { get; init; }
    public FxRate? ExecutionFx { get; init; }
    public required SourceSnapshot PriceSource { get; init; }
    public required Evidence PriceEvidence { get; init; }
    public SourceSnapshot? FxSource { get; init; }
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
    public const string Version = "execution-v3";

    public static ExecutionPlan Plan(DataSlice market, IEnumerable<PlannedOrder> orders, FillPolicy policy, ExecutionCalendar calendar, TimeSpan maximumFxAge)
    {
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        ArgumentNullException.ThrowIfNull(calendar);
        if (maximumFxAge < TimeSpan.Zero) throw new ResearchException("raw-ledger-age");
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

            if (listing.Venue != calendar.Venue || market.Calendars.SingleOrDefault(c => c.Id == listing.CalendarId) != calendar.Reference)
                throw new ResearchException("execution-calendar-binding");
            var session = calendar.Next(order.DecisionAt, order.NextDecisionAt);
            var bar = session is null ? null : bars.SingleOrDefault(b => b.SessionDate == session.Date && b.SessionCloseAt == session.CloseAt);
            if (bar is null) { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.NoNextSession, order.NotionalMajor)); continue; }
            if (bar.Open <= 0) { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.NoOpen, order.NotionalMajor)); continue; }
            if (bar.Evidence.AvailableAt > order.NextDecisionAt)
            { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.OpenNotObservable, order.NotionalMajor)); continue; }
            // A structural change between the decision and the fill makes the derived quantity wrong.
            if (market.EffectiveActions(order.ListingId).Any(a => a.EffectiveAt > order.DecisionAt &&
                a.EffectiveAt <= bar.SessionCloseAt && a.Action is CorporateActionKind.Split or CorporateActionKind.Dividend
                    or CorporateActionKind.Merger or CorporateActionKind.SpinOff or CorporateActionKind.Delisting))
            { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.UnsupportedAction, order.NotionalMajor)); continue; }

            if (!ExecutionFx.TrySelect(market, bar.Denomination.Currency, order.FxObservationId,
                session!.OpenAt!.Value, maximumFxAge, out var fx))
            { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.InvalidExecutionFx, order.NotionalMajor)); continue; }

            var fillPrice = bar.Open * (order.Buy ? 1m + policy.Rate : 1m - policy.Rate);
            if (fillPrice <= 0) { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.NoOpen, order.NotionalMajor)); continue; }
            var majorPrice = bar.Denomination.ToMajor(fillPrice);
            var quantity = order.NotionalMajor / majorPrice;
            if (policy.QuantityStep > 0) quantity = decimal.Floor(quantity / policy.QuantityStep) * policy.QuantityStep;
            var gross = quantity * majorPrice;
            if (quantity <= 0 || gross < policy.MinimumOrderValue)
            { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.BelowMinimum, order.NotionalMajor)); continue; }
            if (bar.Volume <= 0 || quantity > policy.MaximumParticipation * bar.Volume)
            { rejections.Add(new(order.ListingId, order.Buy, ExecutionRefusal.VolumeParticipation, order.NotionalMajor)); continue; }

            fills.Add(new(order.ListingId, order.Buy, quantity, fillPrice, bar.Open, bar.Denomination, bar.Id, bar.SessionCloseAt)
            {
                Timing = new(calendar.Reference, calendar.Venue, calendar.SourceReference, calendar.Sha256, session!),
                ExecutionFx = fx, PriceEvidence = bar.Evidence,
                PriceSource = market.Sources.Single(s => s.Id == bar.Evidence.SourceId),
                FxSource = fx is null ? null : market.Sources.Single(s => s.Id == fx.Evidence.SourceId)
            });
        }
        return new(fills.ToImmutable(), rejections.ToImmutable());
    }
}
