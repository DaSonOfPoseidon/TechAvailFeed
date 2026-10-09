using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TechAvail.Ingest;

// Keeps the raw mail for replays and the IMAP check, since Gmail only holds today's. Named by a hash
// of the Message-ID so a re-run skips mail it already has.
public static class MailArchive
{
    // "<" and ">" in the Message-ID are left as they are.
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool Save(string directory, string messageId, DateTimeOffset? mailboxReceivedAt, byte[] raw)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(messageId)))[..32];
        var path = Path.Combine(directory, $"{name}.eml");
        if (File.Exists(path))
            return false;
        Directory.CreateDirectory(directory);
        var meta = new JsonObject
        {
            ["message_id"] = messageId,
            // The IMAP internal date, in UTC.
            ["mailbox_received_at"] = mailboxReceivedAt?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
        };
        File.WriteAllText(Path.ChangeExtension(path, ".json"), meta.ToJsonString(Json));
        File.WriteAllBytes(path, raw);
        return true;
    }
}
