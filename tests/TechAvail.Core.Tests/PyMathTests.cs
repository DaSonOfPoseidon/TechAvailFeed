using System.Globalization;
using System.Text.Json;

namespace TechAvail.Core.Tests;

// Data/pyround.json: [repr(x), ndigits, repr(round(x, ndigits))] from CPython 3.12.
public class PyMathTests
{
    public static TheoryData<string, int, string> Cases()
    {
        var data = new TheoryData<string, int, string>();
        var rows = JsonSerializer.Deserialize<JsonElement[][]>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "pyround.json"))
        )!;
        foreach (var row in rows)
            data.Add(row[0].GetString()!, row[1].GetInt32(), row[2].GetString()!);
        return data;
    }

    static double Parse(string repr) => double.Parse(repr, NumberStyles.Float, CultureInfo.InvariantCulture);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Round_matches_python(string value, int digits, string expected) =>
        // Bit for bit, so -0.0 and 0.0 count as different.
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(Parse(expected)),
            BitConverter.DoubleToInt64Bits(PyMath.Round(Parse(value), digits))
        );
}
