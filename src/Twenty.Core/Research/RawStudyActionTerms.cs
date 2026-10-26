using Twenty.Core.Data;
using System.Text.Json.Serialization;

namespace Twenty.Core.Research;

// Original per-share terms are policy inputs. Actual account amounts are derived only from frozen entitlements.
public sealed record RawStudyDividendDelivery(RawSchedulePoint Booking, string SourcePaymentId, string SourceReference,
    string ContentSha256, DateTimeOffset PaidAt, DateTimeOffset AvailableAt, string Currency, decimal GrossPerShare,
    decimal WithheldPerShare, decimal FixedFeeGbp, string? FxObservationId, string AllocationRule);
public sealed record RawStudyDividendSchedule(string ListingId, string ActionId, string ObservationId, RawSchedulePoint Capture,
    RawDividendEvidence Evidence, RawStudyDividendDelivery? Delivery) : RawActionSchedule(ListingId, ActionId, ObservationId, Capture)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RawStudyClaimMarkPolicy? MarkPolicy { get; init; }
}
public sealed record RawStudyTerminalDelivery(RawSchedulePoint Booking, string SourceSettlementId, string SourceReference,
    string ContentSha256, DateTimeOffset SettledAt, DateTimeOffset AvailableAt, string? Currency, decimal GrossPerShare,
    decimal WithheldPerShare, decimal FixedFeeGbp, string? FxObservationId, string? SuccessorListingId,
    decimal SuccessorSharesPerShare, string AllocationRule);
public sealed record RawStudyTerminalSchedule(string ListingId, string ActionId, string ObservationId, RawSchedulePoint Capture,
    RawTerminalEvidence Evidence, RawStudyTerminalDelivery? Delivery) : RawActionSchedule(ListingId, ActionId, ObservationId, Capture)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RawStudyClaimMarkPolicy? MarkPolicy { get; init; }
}

public static class RawStudyActionTerms
{
    public const string AllocationRule = "exact-decimal-per-cutoff-share-fixed-gbp-fee-v1";
    private static bool Source(string? reference, string? hash) => !string.IsNullOrWhiteSpace(reference) && reference.Length <= 200 &&
        hash is { Length: 64 } && hash.All(char.IsAsciiHexDigitLower);
    private static void Point(RawSchedulePoint? point, string datasetSha)
    {
        if (point is null || string.IsNullOrWhiteSpace(point.Id) || point.Id.Length > 128 || point.TieOrder < 0 || point.DatasetSha256 != datasetSha)
            throw new ResearchException("raw-study-action-binding");
        if (new[] { "deposit-", "decision-", "fill-", "mark-" }.Any(p => point.Id.StartsWith(p, StringComparison.Ordinal)))
            throw new ResearchException("raw-study-action-id");
    }
    public static void Validate(RawActionSchedule entry, CorporateAction observed, string datasetSha)
    {
        if (entry.ListingId != observed.ListingId || entry.ActionId != observed.ActionId || entry.ObservationId != observed.Id)
            throw new ResearchException("raw-study-action-binding");
        Point(entry.Capture, datasetSha);
        RawStudyClaimMarks.Validate(entry, observed);
        if (observed.NewSymbol is not null) throw new ResearchException("raw-ledger-action-complex");
        switch (entry)
        {
            case RawSplitSchedule when observed.Action == CorporateActionKind.Split && observed.SplitRatio > 0 &&
                observed.CashPerShare is null or 0 && observed.SuccessorListingId is null && observed.TerminalOutcome == TerminalOutcome.NotApplicable: return;
            case RawStudyDividendSchedule dividend when observed.Action == CorporateActionKind.Dividend && observed.CashPerShare > 0 &&
                observed.SplitRatio is null or 1 && observed.SuccessorListingId is null && observed.TerminalOutcome == TerminalOutcome.NotApplicable:
                if (dividend.Evidence is not { } proof || proof.EntitlementAt != observed.EffectiveAt || proof.AvailableAt == default || !Source(proof.SourceReference, proof.ContentSha256))
                    throw new ResearchException("raw-study-action-proof");
                if (dividend.Delivery is { } payment)
                {
                    Point(payment.Booking, datasetSha);
                    if (payment.AllocationRule != AllocationRule || string.IsNullOrWhiteSpace(payment.SourcePaymentId) || payment.SourcePaymentId.Length > 128 ||
                        !Source(payment.SourceReference, payment.ContentSha256) || payment.PaidAt < observed.EffectiveAt || payment.AvailableAt < payment.PaidAt ||
                        payment.Currency != observed.CashCurrency || payment.GrossPerShare != observed.CashPerShare || payment.WithheldPerShare < 0 ||
                        payment.WithheldPerShare > payment.GrossPerShare || payment.FixedFeeGbp < 0) throw new ResearchException("raw-study-payment-policy");
                }
                return;
            case RawStudyTerminalSchedule terminal when observed.Action is CorporateActionKind.Delisting or CorporateActionKind.Merger &&
                observed.TerminalOutcome is TerminalOutcome.Zero or TerminalOutcome.Cash or TerminalOutcome.Stock:
                if (terminal.Evidence is not { } evidence || evidence.EntitlementAt != observed.EffectiveAt || evidence.AvailableAt == default || !Source(evidence.SourceReference, evidence.ContentSha256))
                    throw new ResearchException("raw-study-action-proof");
                var validTerms = observed.TerminalOutcome switch
                {
                    TerminalOutcome.Zero => observed.CashPerShare is null or 0 && observed.SplitRatio is null or 1 && observed.SuccessorListingId is null,
                    TerminalOutcome.Cash => observed.CashPerShare > 0 && observed.SplitRatio is null or 1 && observed.SuccessorListingId is null,
                    TerminalOutcome.Stock => observed.CashPerShare is null or 0 && observed.SplitRatio > 0 && observed.SuccessorListingId is not null && observed.SuccessorListingId != observed.ListingId,
                    _ => false
                };
                if (!validTerms) throw new ResearchException("raw-ledger-action-complex");
                if (terminal.Delivery is { } delivery)
                {
                    Point(delivery.Booking, datasetSha);
                    if (observed.TerminalOutcome == TerminalOutcome.Zero || delivery.AllocationRule != AllocationRule || string.IsNullOrWhiteSpace(delivery.SourceSettlementId) ||
                        delivery.SourceSettlementId.Length > 128 || !Source(delivery.SourceReference, delivery.ContentSha256) || delivery.SettledAt < observed.EffectiveAt ||
                        delivery.AvailableAt < delivery.SettledAt || delivery.FixedFeeGbp < 0) throw new ResearchException("raw-study-payment-policy");
                    if (observed.TerminalOutcome == TerminalOutcome.Cash ?
                        delivery.Currency != observed.CashCurrency || delivery.GrossPerShare != observed.CashPerShare || delivery.WithheldPerShare < 0 || delivery.WithheldPerShare > delivery.GrossPerShare ||
                        delivery.SuccessorListingId is not null || delivery.SuccessorSharesPerShare != 0 :
                        delivery.Currency is not null || delivery.GrossPerShare != 0 || delivery.WithheldPerShare != 0 || delivery.FxObservationId is not null ||
                        delivery.SuccessorListingId != observed.SuccessorListingId || delivery.SuccessorSharesPerShare != observed.SplitRatio)
                        throw new ResearchException("raw-study-payment-policy");
                }
                return;
            case RawDividendSchedule or RawTerminalSchedule:
                throw new ResearchException("raw-study-absolute-payment-unsupported");
            default: throw new ResearchException("raw-study-action-binding");
        }
    }
    internal static RawActionSchedule ForCompiler(RawActionSchedule entry, DateTimeOffset end) => entry switch
    {
        RawSplitSchedule => entry,
        RawStudyDividendSchedule d => new RawDividendSchedule(d.ListingId,d.ActionId,d.ObservationId,d.Capture,d.Evidence,
            d.Delivery is { } p && p.PaidAt <= end && p.AvailableAt <= end ? new(p.Booking,
                new(p.SourcePaymentId,p.SourceReference,p.ContentSha256,p.PaidAt,p.AvailableAt,p.Currency,p.GrossPerShare,p.WithheldPerShare,p.FixedFeeGbp,p.FxObservationId)) : null),
        RawStudyTerminalSchedule t => new RawTerminalSchedule(t.ListingId,t.ActionId,t.ObservationId,t.Capture,t.Evidence,
            t.Delivery is { } p && p.SettledAt <= end && p.AvailableAt <= end ? new(p.Booking,
                new(p.SourceSettlementId,p.SourceReference,p.ContentSha256,p.SettledAt,p.AvailableAt,p.Currency,p.GrossPerShare,p.WithheldPerShare,p.FixedFeeGbp,p.FxObservationId,p.SuccessorListingId,p.SuccessorSharesPerShare)) : null),
        _ => throw new ResearchException("raw-study-action-binding")
    };
}
