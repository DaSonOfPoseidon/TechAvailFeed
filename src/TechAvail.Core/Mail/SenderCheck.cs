using System.Text;
using MimeKit;
using TechAvail.Core.Parsing;

namespace TechAvail.Core.Mail;

// Port of feed/mail.py's sender_rejection: a From header is trivially forged, so the receiving
// server's DMARC verdict decides. Returns why a mail is rejected, or null when it is accepted.
public static class SenderCheck
{
    public static string? Rejection(MimeMessage message, string mailFrom, string authservId)
    {
        var senders = FeedMail.RawHeaders(message, "From").SelectMany(Addresses).ToList();
        if (senders.Count != 1 || !senders[0].Equals(mailFrom, StringComparison.OrdinalIgnoreCase))
            return $"sender {PyText.Repr(senders)} is not MAIL_FROM alone";

        // The receiving server adds its header on top; any copies further down came from the sender.
        var results = FeedMail.RawHeaders(message, "Authentication-Results").ToList();
        var stripped = results.Count == 0 ? "" : StripCommentsAndQuotes(results[0]);
        if (stripped is null)
            return "ambiguous Authentication-Results";
        var parts = string.Join(' ', Split(stripped)).Split(';').Select(p => p.Trim(' ')).ToList();
        if (!parts[0].Equals(authservId, StringComparison.OrdinalIgnoreCase))
            return $"no Authentication-Results from {authservId}";

        // Exactly one DMARC result, read from its own clause: another one means injected text.
        var dmarc = parts
            .Skip(1)
            .Where(r => r.StartsWith("dmarc=", StringComparison.OrdinalIgnoreCase))
            .Select(r => Split(r).ToList())
            .ToList();
        var at = mailFrom.LastIndexOf('@');
        var domain = (at < 0 ? mailFrom : mailFrom[(at + 1)..]).ToLowerInvariant();
        if (dmarc.Count != 1 || !dmarc[0][0].Equals("dmarc=pass", StringComparison.OrdinalIgnoreCase))
            return $"DMARC did not pass for {domain}";
        var properties = new Dictionary<string, string>();
        foreach (var property in dmarc[0].Skip(1).Where(p => p.Contains('=')))
        {
            var pair = property.ToLowerInvariant().Split('=', 2);
            properties[pair[0]] = pair[1];
        }
        if (properties.GetValueOrDefault("header.from") != domain)
            return $"DMARC did not pass for {domain}";
        return null;
    }

    // getaddresses(): one entry per address; an unparsable header counts as one empty address.
    static IEnumerable<string> Addresses(string header) =>
        InternetAddressList.TryParse(header, out var list)
            ? list.SelectMany(Mailboxes).Select(m => m.Address)
            : [""];

    static IEnumerable<MailboxAddress> Mailboxes(InternetAddress address) =>
        address switch
        {
            MailboxAddress mailbox => [mailbox],
            GroupAddress group => group.Members.SelectMany(Mailboxes),
            _ => [],
        };

    // str.split(): runs of ASCII whitespace (the header is ASCII-only by then).
    static IEnumerable<string> Split(string text) =>
        text.Split([' ', '\t', '\n', '\r', '\v', '\f', '\x1c', '\x1d', '\x1e', '\x1f'], StringSplitOptions.RemoveEmptyEntries);

    // Comments "(...)" and quoted strings carry sender-controlled text, such as the envelope
    // address in the SPF comment, so they must never be read as results. Gmail's own comments
    // hold no quotes or nested parentheses; one that does, or anything left open, could have
    // been shaped by the sender to end a comment early, so it fails closed (null). Non-ASCII
    // text fails closed too: Gmail's header never has any.
    public static string? StripCommentsAndQuotes(string text)
    {
        if (text.Any(c => c > '\x7f'))
            return null;
        var output = new StringBuilder();
        int depth = 0;
        bool quoted = false,
            escaped = false;
        foreach (var c in text)
        {
            if (escaped)
                escaped = false;
            else if (c == '\\' && (quoted || depth > 0))
                escaped = true;
            else if (quoted)
                quoted = c != '"';
            else if (depth > 0)
            {
                if (c is '"' or '(')
                    return null;
                if (c == ')')
                    depth--;
            }
            else if (c == '(')
                depth = 1;
            else if (c == ')')
                return null;
            else if (c == '"')
                quoted = true;
            else
                output.Append(c);
        }
        return depth > 0 || quoted || escaped ? null : output.ToString();
    }
}
