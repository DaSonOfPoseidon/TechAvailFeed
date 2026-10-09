using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TechAvail.Ingest;

// Read-only check of the mailbox code against the live mailbox: every mail still under the processed
// label, fetched through ImapMailbox.Peek, must match the raw copy the ingest archived in corpus/mail
// (bytes, Message-ID and received time). Prints counts only, never mail contents.
//
//   scripts/imap-check.sh
var root = args.Length > 0 ? args[0] : "corpus";
var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var settings = IngestSettings.From(config);
using var session = new ImapMailbox(settings, NullLogger<ImapMailbox>.Instance).Open();
int matched = 0, differing = 0, missing = 0;
foreach (var fetched in session.Peek(settings.ProcessedLabel))
{
    var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fetched.Mail.MessageId)))[..32];
    var eml = Path.Combine(root, "mail", $"{name}.eml");
    if (!File.Exists(eml))
    {
        missing++;
        continue;
    }
    var meta = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(eml, ".json")))!;
    var sameBytes = File.ReadAllBytes(eml).AsSpan().SequenceEqual(fetched.Raw);
    var archived = meta["mailbox_received_at"]?.GetValue<string>();
    var sameTime = archived is not null && fetched.ReceivedAt == DateTimeOffset.Parse(archived);
    if (sameBytes && sameTime)
        matched++;
    else
    {
        differing++;
        Console.WriteLine($"{name}: {(sameBytes ? "" : "bytes ")}{(sameTime ? "" : "received time")}");
    }
}
Console.WriteLine($"{matched} identical, {differing} differing, {missing} not in the corpus");
return differing == 0 ? 0 : 1;
