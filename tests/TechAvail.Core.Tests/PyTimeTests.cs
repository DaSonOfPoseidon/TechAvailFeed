using System.Globalization;
using System.Text.Json;
using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

// Data/pytime.json holds CPython 3.12's datetime.strptime / fromisoformat result for each case
// (an isoformat string, or null where Python raises ValueError).
public class PyTimeTests
{
    static readonly JsonElement Reference = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "pytime.json")))
        .RootElement;

    // Forms Python accepts that the port deliberately rejects; none can come from the feed query.
    static readonly HashSet<string> NotPorted = ["2026-09-29T06:15:02-05:00:30"];

    public static TheoryData<string, string, string?> StrptimeCases()
    {
        var data = new TheoryData<string, string, string?>();
        foreach (var row in Reference.GetProperty("strptime").EnumerateArray())
            data.Add(row[0].GetString()!, row[1].GetString()!, row[2].GetString());
        return data;
    }

    public static TheoryData<string, string?, bool> IsoCases()
    {
        var data = new TheoryData<string, string?, bool>();
        foreach (var row in Reference.GetProperty("iso").EnumerateArray())
        {
            var value = row[0].GetString()!;
            if (NotPorted.Contains(value))
                continue;
            var result = row[1];
            data.Add(
                value,
                result.ValueKind == JsonValueKind.Null ? null : result[0].GetString(),
                result.ValueKind != JsonValueKind.Null && result[1].GetBoolean()
            );
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(StrptimeCases))]
    public void Strptime_matches_python(string value, string format, string? expected)
    {
        var ok = PyTime.TryStrptime(value, format, out var parsed);
        Assert.Equal(expected, ok ? parsed.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) : null);
    }

    [Theory]
    [MemberData(nameof(IsoCases))]
    public void FromIsoFormat_matches_python(string value, string? expected, bool aware)
    {
        var ok = PyTime.TryFromIsoFormat(value, out var parsed);
        Assert.Equal(expected is not null, ok);
        if (expected is null)
            return;
        // A naive value comes back as UTC; compare the instant Postgres would store.
        var reference = DateTimeOffset.Parse(
            expected,
            CultureInfo.InvariantCulture,
            aware ? DateTimeStyles.None : DateTimeStyles.AssumeUniversal
        );
        Assert.Equal(reference, parsed);
        Assert.Equal(reference.Offset, parsed.Offset);
    }

    [Fact]
    public void Offsets_with_seconds_are_rejected() =>
        Assert.False(PyTime.TryFromIsoFormat("2026-09-29T06:15:02-05:00:30", out _));
}
