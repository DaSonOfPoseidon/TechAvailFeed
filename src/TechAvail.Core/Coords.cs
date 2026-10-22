using TechAvail.Core.Parsing;

namespace TechAvail.Core;

// Job coordinates locate a customer's home. 3 decimals is ~110 m: enough for a map, not to pick out a house.
public static class Coords
{
    public const int Decimals = 3;

    public static (double? Latitude, double? Longitude) Public(double? latitude, double? longitude, bool exact = false) =>
        exact || latitude is null || longitude is null
            ? (latitude, longitude)
            : (Math.Round(latitude.Value, Decimals), Math.Round(longitude.Value, Decimals));

    public static Block PublicBlock(Block block, bool exact = false)
    {
        var (latitude, longitude) = Public(block.Latitude, block.Longitude, exact);
        return block with { Latitude = latitude, Longitude = longitude };
    }
}
