using System.Globalization;
using System.Numerics;

namespace TechAvail.Core;

// Python's round(x, ndigits) for floats: the exact binary value is rounded to ndigits decimals
// (half to even only on an exact tie), then read back as the nearest double. So round(2.675, 2)
// is 2.67, because 2.675 is stored as 2.67499999...; Math.Round and decimal conversion disagree.
public static class PyMath
{
    public static double Round(double x, int digits)
    {
        if (!double.IsFinite(x) || x == 0)
            return x;
        var bits = BitConverter.DoubleToInt64Bits(x);
        var negative = bits < 0;
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
            exponent++;
        else
            mantissa |= 1L << 52;
        exponent -= 1075;
        // |x| = mantissa * 2^exponent; scaled = |x| * 10^digits = numerator / denominator.
        var numerator = new BigInteger(mantissa) * BigInteger.Pow(10, digits);
        var denominator = BigInteger.One;
        if (exponent >= 0)
            numerator <<= exponent;
        else
            denominator <<= -exponent;
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var twice = remainder * 2;
        if (twice > denominator || (twice == denominator && !quotient.IsEven))
            quotient += 1;
        var text = $"{(negative ? "-" : "")}{quotient}E-{digits}";
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
