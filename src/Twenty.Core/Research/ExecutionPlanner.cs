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

/// <summary>A desired trade sized by either positive local-major notional or explicit positive
/// shares with zero notional. Quantity orders preserve their share intent across opening gaps.</summary>
public sealed record PlannedOrder(string ListingId, bool Buy, decimal NotionalMajor,
    DateTimeOffset DecisionAt, DateTimeOffset NextDecisionAt)
{
    public string? FxObservationId { get; init; }
    public decimal? Quantity { get; init; }
}

public sealed record ExecutionParticipationEvidence(decimal MaximumParticipation, decimal SessionVolume);

public sealed record ExecutionFill(string ListingId, bool Buy, decimal Quantity, decimal FillPrice,
    decimal ObservedOpen, Denomination Denomination, string PriceId, DateTimeOffset FillSessionCloseAt)
{
    public required ExecutionSessionEvidence Timing { get; init; }
    public ExecutionParticipationEvidence? Participation { get; init; }
    public decimal? RequestedQuantity { get; init; }
    public FxRate? ExecutionFx { get; init; }
    public required SourceSnapshot PriceSource { get; init; }
    public required Evidence PriceEvidence { get; init; }
    public SourceSnapshot? FxSource { get; init; }
    /// <summary>Modelled execution versus the open it derives from: the distance a reader can audit
    /// instead of finding it buried in a cost rate.</summary>
    public decimal SlippageFromOpen => ObservedOpen <= 0 ? 0 : FillPrice / ObservedOpen - 1m;
}

public sealed record ExecutionRejection(string ListingId, bool Buy, ExecutionRefusal Reason, decimal NotionalMajor)
{
    public decimal? RequestedQuantity { get; init; }
}
public sealed record ExecutionPlan(ImmutableArray<ExecutionFill> Fills, ImmutableArray<ExecutionRejection> Rejections);

/// <summary>Decides what a desired trade could have achieved. The decision is taken at the end of a
/// session and the order fills at the <b>next</b> session's open on the raw basis, so a trade is
/// never priced at the close that was just observed. Orders that cannot be priced, sized or
/// sustained are refused with a reason and never approximated. Funding is deliberately not decided
/// here: it is the ledger's job, and it refuses an unfunded fill atomically.</summary>
public static class ExecutionPlanner
{
    public const string Version = "execution-v5";
    public const int MaximumOrders = 10000;

    public static ExecutionPlan Plan(DataSlice market, IEnumerable<PlannedOrder> orders, FillPolicy policy, ExecutionCalendar calendar, TimeSpan maximumFxAge)
    {
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        ArgumentNullException.ThrowIfNull(calendar);
        if (maximumFxAge < TimeSpan.Zero) throw new ResearchException("raw-ledger-age");
        var input = orders.Take(MaximumOrders + 1).ToArray();
        if (input.Length > MaximumOrders || input.Any(o => o is null)) throw new ResearchException("invalid-execution-orders");
        var participation = new Dictionary<(string Listing, DateOnly Session), decimal>();
        var fills = ImmutableArray.CreateBuilder<ExecutionFill>();
        var rejections = ImmutableArray.CreateBuilder<ExecutionRejection>();
        void Reject(PlannedOrder order, ExecutionRefusal reason) => rejections.Add(new(order.ListingId ?? "", order.Buy, reason, order.NotionalMajor)
        { RequestedQuantity = order.Quantity });
        foreach (var order in input)
        {
            if (string.IsNullOrWhiteSpace(order.ListingId) ||
                (order.Quantity is { } requested ? requested <= 0 || order.NotionalMajor != 0 : order.NotionalMajor <= 0) ||
                order.DecisionAt == default || order.NextDecisionAt <= order.DecisionAt)
            { Reject(order, ExecutionRefusal.InvalidOrder); continue; }

            var bars = market.Prices(order.ListingId, PriceBasis.Raw);
            var listing = market.ListingsAt(order.DecisionAt).SingleOrDefault(l => l.ListingId == order.ListingId);
            if (listing is not { Status: ListingStatus.Active })
            { Reject(order, ExecutionRefusal.InvalidOrder); continue; }

            if (listing.Venue != calendar.Venue || market.Calendars.SingleOrDefault(c => c.Id == listing.CalendarId) != calendar.Reference)
                throw new ResearchException("execution-calendar-binding");
            var session = calendar.Next(order.DecisionAt, order.NextDecisionAt);
            var bar = session is null ? null : bars.SingleOrDefault(b => b.SessionDate == session.Date && b.SessionCloseAt == session.CloseAt);
            if (bar is null) { Reject(order, ExecutionRefusal.NoNextSession); continue; }
            if (bar.Open <= 0) { Reject(order, ExecutionRefusal.NoOpen); continue; }
            if (bar.Evidence.AvailableAt > order.NextDecisionAt)
            { Reject(order, ExecutionRefusal.OpenNotObservable); continue; }
            // A structural change between the decision and the fill makes the derived quantity wrong.
            if (market.EffectiveActions(order.ListingId).Any(a => a.EffectiveAt > order.DecisionAt &&
                a.EffectiveAt <= bar.SessionCloseAt && a.Action is CorporateActionKind.Split or CorporateActionKind.Dividend
                    or CorporateActionKind.Merger or CorporateActionKind.SpinOff or CorporateActionKind.Delisting))
            { Reject(order, ExecutionRefusal.UnsupportedAction); continue; }

            if (!ExecutionFx.TrySelect(market, bar.Denomination.Currency, order.FxObservationId,
                session!.OpenAt!.Value, maximumFxAge, out var fx))
            { Reject(order, ExecutionRefusal.InvalidExecutionFx); continue; }

            var fillPrice = bar.Open * (order.Buy ? 1m + policy.Rate : 1m - policy.Rate);
            if (fillPrice <= 0) { Reject(order, ExecutionRefusal.NoOpen); continue; }
            var majorPrice = bar.Denomination.ToMajor(fillPrice);
            if (majorPrice <= 0) { Reject(order, ExecutionRefusal.InvalidOrder); continue; }
            decimal quantity, gross;
            try
            {
                quantity = order.Quantity ?? order.NotionalMajor / majorPrice;
                // Positive quantities: remainder-based flooring avoids an overflowing
                // intermediate quotient for otherwise valid, very small share steps.
                if (policy.QuantityStep > 0) quantity -= quantity % policy.QuantityStep;
                gross = quantity * majorPrice;
            }
            catch (OverflowException) { Reject(order, ExecutionRefusal.InvalidOrder); continue; }
            if (quantity <= 0 || gross < policy.MinimumOrderValue)
            { Reject(order, ExecutionRefusal.BelowMinimum); continue; }
            var key = (order.ListingId, session.Date);
            var used = participation.GetValueOrDefault(key);
            if (bar.Volume <= 0 || quantity > policy.MaximumParticipation * bar.Volume - used)
            { Reject(order, ExecutionRefusal.VolumeParticipation); continue; }

            participation[key] = used + quantity;
            fills.Add(new(order.ListingId, order.Buy, quantity, fillPrice, bar.Open, bar.Denomination, bar.Id, bar.SessionCloseAt)
            {
                Participation = new(policy.MaximumParticipation, bar.Volume),
                RequestedQuantity = order.Quantity,
                Timing = new(calendar.Reference, calendar.Venue, calendar.SourceReference, calendar.Sha256, session!),
                ExecutionFx = fx, PriceEvidence = bar.Evidence,
                PriceSource = market.Sources.Single(s => s.Id == bar.Evidence.SourceId),
                FxSource = fx is null ? null : market.Sources.Single(s => s.Id == fx.Evidence.SourceId)
            });
        }
        return new(fills.ToImmutable(), rejections.ToImmutable());
    }
}
