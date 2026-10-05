using System.Text.Json.Nodes;
using TechAvail.Parity;

// Compares the .NET pipeline's output for each mail in a local corpus with the Python golden
// files (python -m tools.golden). Prints counts and differing JSON paths only, never values:
// the corpus is real feed data.
//
//   scripts/dotnet.sh run --project tools/TechAvail.Parity -- [root]   (root defaults to corpus)
//   scripts/dotnet.sh run --project tools/TechAvail.Parity -- replay <root> <connection string>
//   scripts/dotnet.sh run --project tools/TechAvail.Parity -- finalize <connection string>
//   scripts/dotnet.sh run --project tools/TechAvail.Parity -- history <connection string> <start> <end> <out.json>
if (args.Length > 0 && args[0] == "finalize")
    return HistoryDump.Finalize(args[1]);
if (args.Length > 0 && args[0] == "history")
    return HistoryDump.Write(args[1], args[2], args[3], args[4]);
var replay = args.Length > 0 && args[0] == "replay";
var root = replay ? args[1] : args.Length > 0 ? args[0] : "corpus";
var mailFrom = Environment.GetEnvironmentVariable("MAIL_FROM");
var authservId = Environment.GetEnvironmentVariable("AUTHSERV_ID") is { Length: > 0 } id ? id : "mx.google.com";
if (string.IsNullOrEmpty(mailFrom))
{
    Console.Error.WriteLine("parity: set MAIL_FROM to the feed's sender address");
    return 2;
}
if (replay)
    return Replay.Run(root, args[2], mailFrom, authservId);
var mails = Directory.Exists(Path.Combine(root, "mail"))
    ? Directory.GetFiles(Path.Combine(root, "mail"), "*.eml").Order().ToList()
    : [];
if (mails.Count == 0)
{
    Console.Error.WriteLine($"parity: no mail in {Path.Combine(root, "mail")}");
    return 2;
}

int identical = 0, differing = 0, missing = 0, rejected = 0;
foreach (var eml in mails)
{
    var name = Path.GetFileNameWithoutExtension(eml);
    var goldenPath = Path.Combine(root, "golden", $"{name}.json");
    if (!File.Exists(goldenPath))
    {
        missing++;
        Console.WriteLine($"{name}: no golden file (run python -m tools.golden)");
        continue;
    }
    var actual = GoldenJson.Mail(File.ReadAllBytes(eml), mailFrom, authservId);
    if (actual["sender_rejection"] is not null)
        rejected++;
    var diffs = GoldenJson.Diff(JsonNode.Parse(File.ReadAllText(goldenPath)), actual);
    if (diffs.Count == 0)
    {
        identical++;
        continue;
    }
    differing++;
    var shown = string.Join(", ", diffs.Take(5));
    Console.WriteLine($"{name}: {diffs.Count} differences: {shown}{(diffs.Count > 5 ? ", ..." : "")}");
}
Console.WriteLine(
    $"{mails.Count} mails: {identical} identical, {differing} differing, {missing} without golden, {rejected} rejected by sender check"
);
return differing + missing == 0 ? 0 : 1;
