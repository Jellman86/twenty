using System.Numerics;

namespace Twenty.Core.Research;

internal static class RawQuantityRounding
{
    // Decimal division can round up. Reduce only a bounded number of representable
    // quantity units; never absorb the excess as cash or resize an economic shortfall.
    public static decimal Fit(decimal quantity, Func<decimal, bool> affordable)
    {
        for (var i = 0; i <= 64; i++)
        {
            if (quantity > 0 && affordable(quantity)) return quantity;
            if (quantity <= 0 || i == 64) throw new ResearchException("raw-rebalance-rounding");
            var bits = decimal.GetBits(quantity); var scale = (bits[3] >> 16) & 255;
            var coefficient = (BigInteger)(uint)bits[0] | (BigInteger)(uint)bits[1] << 32 | (BigInteger)(uint)bits[2] << 64;
            var maximum = (BigInteger.One << 96) - 1;
            while (scale < 28 && coefficient * 10 <= maximum) { coefficient *= 10; scale++; }
            coefficient--;
            quantity = new decimal(unchecked((int)(uint)(coefficient & uint.MaxValue)),
                unchecked((int)(uint)((coefficient >> 32) & uint.MaxValue)),
                unchecked((int)(uint)((coefficient >> 64) & uint.MaxValue)), false, (byte)scale);
        }
        throw new ResearchException("raw-rebalance-rounding");
    }
}
