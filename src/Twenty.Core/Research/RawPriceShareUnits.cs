using Twenty.Core.Data;
namespace Twenty.Core.Research;

/// <summary>A raw close and a current share quantity must use the same split vintage.
/// The point-in-time slice supplies only known/effective actions; no future evidence is read.</summary>
internal static class RawPriceShareUnits
{
    public static void Validate(DataSlice market, PriceBar price)
    {
        if (market.EffectiveActions(price.ListingId).Any(a => a.Action == CorporateActionKind.Split &&
            a.SplitRatio != 1 && a.EffectiveAt > price.SessionCloseAt))
            throw new ResearchException("raw-price-share-units");
    }
}
