using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed partial class RawGbpLedger
{
    private (decimal Rate, string? Id) ValidateExecution(DataSlice market, Listing listing, decimal signedQuantity,
        decimal rawPrice, ExecutionFill fill, ExecutionCalendar? calendar, TimeSpan maximumFxAge)
    {
        var timing = fill.Timing;
        var session = timing?.Session;
        var bar = market.Prices(listing.ListingId, PriceBasis.Raw).SingleOrDefault(p => p.Id == fill.PriceId);
        if (calendar is null || session?.OpenAt is not { } open || session.CloseAt is not { } close || open >= close || close > market.DecisionAt ||
            fill.ListingId != listing.ListingId || fill.Buy != (signedQuantity > 0) || fill.Quantity != Math.Abs(signedQuantity) ||
            fill.FillPrice != rawPrice || (fill.RequestedQuantity is { } requested && (requested <= 0 || fill.Quantity > requested)) ||
            fill.Denomination != listing.Denomination || fill.FillSessionCloseAt != close ||
            timing!.Venue != listing.Venue || timing.Venue != calendar.Venue || timing.Calendar != calendar.Reference ||
            timing.ScheduleSha256 != calendar.Sha256 || timing.SourceReference != calendar.SourceReference ||
            calendar.Days.SingleOrDefault(d => d.Date == session.Date) != session ||
            timing.Calendar != market.Calendars.SingleOrDefault(c => c.Id == listing.CalendarId) ||
            string.IsNullOrWhiteSpace(timing.SourceReference) || timing.ScheduleSha256 is not { Length: 64 } ||
            !timing.ScheduleSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            bar is null || bar.SessionDate != session.Date || bar.SessionCloseAt != close || bar.Open != fill.ObservedOpen ||
            bar.Denomination != fill.Denomination || fill.PriceEvidence != bar.Evidence ||
            fill.PriceSource != market.Sources.SingleOrDefault(s => s.Id == bar.Evidence.SourceId))
            throw new ResearchException("raw-ledger-execution-evidence");
        var participation = fill.Participation;
        if (participation is null || participation.MaximumParticipation is <= 0 or > 1 ||
            participation.SessionVolume <= 0 || participation.SessionVolume != bar.Volume)
            throw new ResearchException("raw-ledger-execution-participation");
        decimal used = 0;
        foreach (var prior in Entries.Select(e => e.ModelledExecution).Where(f => f is not null &&
            f.ListingId == listing.ListingId && f.Timing.Session.Date == session.Date))
        {
            // A fresh planner call, reversed direction or revised capture cannot reset
            // the same session's already committed gross-volume budget.
            if (prior!.Participation != participation) throw new ResearchException("raw-ledger-execution-participation");
            used += prior.Quantity;
        }
        if (fill.Quantity <= 0 || fill.Quantity > participation.MaximumParticipation * participation.SessionVolume - used)
            throw new ResearchException("raw-ledger-execution-participation");
        if (Entries.Any(e => (e.ModelledExecution?.Timing.Session.OpenAt ?? e.At) > open))
            throw new ResearchException("raw-ledger-execution-time");
        if (!ExecutionFx.TrySelect(market, listing.Denomination.Currency, fill.ExecutionFx?.Id, open, maximumFxAge, out var fx) ||
            fx != fill.ExecutionFx || fill.FxSource != (fx is null ? null : market.Sources.SingleOrDefault(s => s.Id == fx.Evidence.SourceId)))
            throw new ResearchException("raw-ledger-execution-fx");
        // A late receipt cannot step over an unresolved event affecting newly acquired shares.
        if (market.EffectiveActions(listing.ListingId).Any(a => a.EffectiveAt > open && !IsNeutralAction(a)))
            throw new ResearchException("raw-ledger-execution-action");
        return (fx?.QuoteUnitsPerBase ?? 1, fx?.Id);
    }
}
