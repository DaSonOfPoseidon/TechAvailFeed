using System.Text.Encodings.Web;
using System.Text.Json;

namespace TechAvail.Core.Tests;

// Each fake feed in tests/fixtures/*.csv against the parser output saved next to it as .json. After
// an intended parser change, regenerate them with UPDATE_SNAPSHOTS=1 scripts/dotnet.sh test and
// review the diff.
public class FixtureSnapshotTests
{
    static readonly string Fixtures = Path.Combine(FindRoot(), "tests", "fixtures");
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TechAvailFeed.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    static List<string> Names(string pattern) => [.. Directory.GetFiles(Fixtures, pattern).Select(p => Path.GetFileNameWithoutExtension(p)).Order()];

    public static TheoryData<string> Csvs() => [.. Names("*.csv")];

    [Theory]
    [MemberData(nameof(Csvs))]
    public void Fixture_matches_its_snapshot(string name)
    {
        var actual = FeedJson.Feed(File.ReadAllBytes(Path.Combine(Fixtures, $"{name}.csv"))).ToJsonString(Indented) + "\n";
        var snapshot = Path.Combine(Fixtures, $"{name}.json");
        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
            File.WriteAllText(snapshot, actual);
        Assert.Equal(File.ReadAllText(snapshot), actual);
    }

    [Fact]
    public void Every_fixture_has_a_snapshot() => Assert.Equal(Names("*.csv"), Names("*.json"));
}
