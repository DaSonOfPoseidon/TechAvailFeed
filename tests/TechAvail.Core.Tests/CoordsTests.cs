namespace TechAvail.Core.Tests;

public class CoordsTests
{
    [Fact]
    public void Served_coordinates_are_rounded_to_about_110_m() =>
        Assert.Equal((40.123, -100.654), Coords.Public(40.123456, -100.654321));

    [Fact]
    public void Rounding_uses_the_exact_binary_value() =>
        // 1.2345 is stored just below the tie, so it rounds to 1.234, not 1.235.
        Assert.Equal((1.234, -1.234), Coords.Public(1.2345, -1.2345));

    [Fact]
    public void Exact_or_missing_coordinates_pass_through()
    {
        Assert.Equal((40.123456, -100.654321), Coords.Public(40.123456, -100.654321, exact: true));
        Assert.Equal((40.123456, null), Coords.Public(40.123456, null));
    }
}
