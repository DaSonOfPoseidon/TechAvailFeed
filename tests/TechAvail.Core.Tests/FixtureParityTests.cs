using System.Text.Json.Nodes;
using TechAvail.Parity;

namespace TechAvail.Core.Tests;

// The .NET parser against what the Python parser made of the same fake fixtures
// (contract/golden/fixtures, written by `uv run python -m tools.golden --fixtures`).
public class FixtureParityTests
{
    static readonly string Root = FindRoot();

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TechAvailFeed.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    static List<string> Names(string directory, string pattern) =>
        [.. Directory.GetFiles(directory, pattern).Select(path => Path.GetFileNameWithoutExtension(path)).Order()];

    static List<string> FixtureNames() => Names(Path.Combine(Root, "tests", "fixtures"), "*.csv");

    public static TheoryData<string> Fixtures() => [.. FixtureNames()];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_matches_python(string name)
    {
        var golden = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "contract", "golden", "fixtures", $"{name}.json")));
        var actual = GoldenJson.Feed(File.ReadAllBytes(Path.Combine(Root, "tests", "fixtures", $"{name}.csv")));
        Assert.Empty(GoldenJson.Diff(golden, actual));
    }

    [Fact]
    public void Every_fixture_has_a_golden_file() =>
        Assert.Equal(FixtureNames(), Names(Path.Combine(Root, "contract", "golden", "fixtures"), "*.json"));

    [Fact]
    public void Diff_reports_paths_not_values()
    {
        var expected = JsonNode.Parse("""{"rows": [{"a": 1, "b": "x"}], "generated_at": "2026-01-01T06:00:00-05:00"}""");
        var actual = JsonNode.Parse("""{"rows": [{"a": 1.0, "b": "secret"}], "generated_at": "2026-01-01T11:00:00+00:00"}""");
        Assert.Equal(["rows[0].b"], GoldenJson.Diff(expected, actual));
    }
}
