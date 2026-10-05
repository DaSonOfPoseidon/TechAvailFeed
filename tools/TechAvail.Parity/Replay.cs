using System.Globalization;
using System.Text.Json.Nodes;
using MimeKit;
using TechAvail.Core.Mail;
using TechAvail.Data;

namespace TechAvail.Parity;

// The .NET side of tools/replay.py: the corpus through the .NET ingest into an empty database,
// in the order the mailbox received it. Prints counts only.
public static class Replay
{
    public static int Run(string root, string connectionString, string mailFrom, string authservId)
    {
        Schema.Migrate(connectionString);
        var store = new FeedStore(connectionString);
        var counts = new Dictionary<string, int> { ["processed"] = 0, ["failed"] = 0, ["rejected"] = 0, ["duplicate"] = 0 };
        foreach (var (eml, received) in CorpusOrder(root))
        {
            var message = MimeMessage.Load(eml);
            if (SenderCheck.Rejection(message, mailFrom, authservId) is not null)
            {
                counts["rejected"]++;
                continue;
            }
            var mail = FeedMail.FromMime(message, "0");
            if (store.Seen(mail.MessageId))
            {
                counts["duplicate"]++;
                continue;
            }
            counts[FeedIngest.Ingest(store, mail, received) ? "processed" : "failed"]++;
        }
        Console.WriteLine(string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}")));
        return 0;
    }

    // Python sorts by the sidecar's ISO string, then the file name.
    static IEnumerable<(string, DateTimeOffset?)> CorpusOrder(string root) =>
        Directory
            .GetFiles(Path.Combine(root, "mail"), "*.eml")
            .Select(eml =>
            {
                var meta = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(eml, ".json")))!;
                var received = meta["mailbox_received_at"]?.GetValue<string>();
                return (eml, received);
            })
            .OrderBy(x => x.received ?? "", StringComparer.Ordinal)
            .ThenBy(x => Path.GetFileName(x.eml), StringComparer.Ordinal)
            .Select(x =>
                (
                    x.eml,
                    x.received is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(x.received, CultureInfo.InvariantCulture)
                )
            );
}
