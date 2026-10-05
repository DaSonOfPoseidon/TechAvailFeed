using System.Text;
using MimeKit;
using TechAvail.Core.Mail;

namespace TechAvail.Core.Tests;

// Port of tests/test_sender_auth.py.
public class SenderCheckTests
{
    const string From = "reports@v.example";
    const string GoodResults =
        "mx.google.com; dkim=pass header.i=@v.example;\n"
        + " spf=pass smtp.mailfrom=bounce@v.example;\n dmarc=pass header.from=v.example";

    static MimeMessage Message(string sender, string? fromHeader, params string[] results)
    {
        var headers = string.Concat(results.Select(r => $"Authentication-Results: {r}\n"));
        fromHeader ??= $"From: Reports <{sender}>\n";
        var text = $"{headers}{fromHeader}Subject: feed\n\nbody";
        return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(text)));
    }

    // Named apart from Message(sender, fromHeader, ...) so two results never bind to those.
    static MimeMessage Results(params string[] results) => Message(From, null, results);

    static string? Check(MimeMessage message) => SenderCheck.Rejection(message, From, "mx.google.com");

    static string Reason(MimeMessage message) => Check(message) ?? throw new Xunit.Sdk.XunitException("accepted");

    [Fact]
    public void Authenticated_sender_is_accepted() => Assert.Null(Check(Results(GoodResults)));

    [Fact]
    public void Gmails_real_header_shape_is_accepted()
    {
        var results =
            "mx.google.com;\n       dkim=pass header.i=@v.example header.s=abc header.b=\"IRCP1r/p\";"
            + "\n       spf=pass (google.com: domain of 0100@ses.v.example designates 54.240.9.99 as"
            + " permitted sender) smtp.mailfrom=0100@ses.v.example;\n"
            + "       dmarc=pass (p=NONE sp=NONE dis=NONE) header.from=v.example";
        Assert.Null(Check(Results(results)));
    }

    [Fact]
    public void Dmarc_text_injected_into_the_spf_comment_is_ignored()
    {
        // The envelope address is the attacker's and is echoed inside the SPF comment. A quoted
        // local part can hold spaces, so the fake clause ends exactly at the sender's domain.
        var results =
            "mx.google.com; spf=pass (google.com: domain of \"dmarc=pass header.from=v.example x\""
            + "@e.example designates 1.2.3.4) smtp.mailfrom=\"dmarc=pass header.from=v.example x\""
            + "@e.example; dmarc=fail header.from=v.example";
        Assert.NotNull(Check(Results(results)));
    }

    // A ")" in a quoted envelope address would end the comment early and expose a fake clause,
    // even with no real DMARC clause after it.
    [Theory]
    [InlineData(
        "mx.google.com; spf=pass (google.com: domain of \"x); dmarc=pass header.from=v.example"
            + " (y\"@e.example designates 1.2.3.4) smtp.mailfrom=x@e.example"
    )]
    [InlineData("mx.google.com; spf=pass (google.com: domain of (x) dmarc=pass header.from=v.example")]
    [InlineData("mx.google.com; dmarc=pass header.from=v.example (unterminated")]
    [InlineData("mx.google.com; dmarc=pass header.from=v.example) x")]
    public void A_comment_closed_early_by_the_sender_fails_closed(string results) =>
        Assert.Contains("ambiguous", Reason(Results(results)));

    [Fact]
    public void Non_ascii_results_fail_closed() =>
        Assert.Contains("ambiguous", Reason(Results("mx.google.com; dmarc=pass header.from=v.example é")));

    [Fact]
    public void A_second_dmarc_clause_is_treated_as_injected()
    {
        var results =
            "mx.google.com; spf=pass smtp.mailfrom=x;dmarc=pass header.from=v.example@e.example;"
            + " dmarc=fail header.from=v.example";
        Assert.Contains("DMARC", Reason(Results(results)));
    }

    [Theory]
    [InlineData("From: x@e.example\nFrom: reports@v.example\n")]
    [InlineData("From: x@e.example, reports@v.example\n")]
    public void More_than_one_from_is_rejected(string fromHeader) =>
        Assert.Contains("not MAIL_FROM", Reason(Message(From, fromHeader, GoodResults)));

    [Fact]
    public void Other_sender_is_rejected() =>
        Assert.Equal(
            "sender ['x@e.example'] is not MAIL_FROM alone",
            Reason(Message("x@e.example", null, GoodResults))
        );

    [Fact]
    public void Spoofed_from_without_dmarc_is_rejected() =>
        Assert.Equal(
            "DMARC did not pass for v.example",
            Reason(Results("mx.google.com; spf=pass smtp.mailfrom=x@e.example; dmarc=fail header.from=v.example"))
        );

    [Fact]
    public void Dmarc_for_another_domain_is_rejected() =>
        Assert.Contains("DMARC", Reason(Results("mx.google.com; dmarc=pass header.from=e.example")));

    [Fact]
    public void Only_the_receiving_servers_header_counts()
    {
        // A forged header lower down, or one from another server on top, doesn't count.
        var forged = "e.example; dmarc=pass header.from=v.example";
        Assert.Equal("no Authentication-Results from mx.google.com", Reason(Results(forged, GoodResults)));
        Assert.Contains("no Authentication-Results", Reason(Results()));
    }
}

// Port of the to_feed_mail tests in tests/test_mail.py.
public class FeedMailTests
{
    static MimeMessage Build(string? date = "Tue, 29 Sep 2026 09:16:40 -0500", bool messageId = true, params (string Name, byte[] Data)[] attachments)
    {
        var body = new BodyBuilder { TextBody = "Scheduled report attached." };
        foreach (var (name, data) in attachments)
            body.Attachments.Add(name, data, new ContentType("text", "csv"));
        var message = new MimeMessage { Subject = "TechAvailFeed", Body = body.ToMessageBody() };
        message.From.Add(MailboxAddress.Parse("mbs@example.com"));
        message.Headers.Remove(HeaderId.Date);
        message.Headers.Remove(HeaderId.MessageId);
        if (date is not null)
            message.Headers.Add(HeaderId.Date, date);
        if (messageId)
            message.Headers.Add(HeaderId.MessageId, "<abc@example.com>");
        // Round-trip so the headers are parsed the way a fetched mail's are.
        var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        return MimeMessage.Load(stream);
    }

    [Fact]
    public void Extracts_csv_attachment_and_headers()
    {
        var mail = FeedMail.FromMime(Build(attachments: ("TechAvailFeed.csv", "a,b\n1,2\n"u8.ToArray())), "7");
        Assert.Equal("<abc@example.com>", mail.MessageId);
        Assert.Equal("TechAvailFeed", mail.Subject);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 9, 16, 40, TimeSpan.FromHours(-5)), mail.EmailDate);
        var (name, data) = Assert.Single(mail.Attachments);
        Assert.Equal("TechAvailFeed.csv", name);
        Assert.Equal("a,b\n1,2\n"u8.ToArray(), data);
    }

    [Fact]
    public void Ignores_non_csv_parts_and_falls_back_to_uid()
    {
        var mail = FeedMail.FromMime(Build(messageId: false, attachments: ("report.html", "<html></html>"u8.ToArray())), "7");
        Assert.Empty(mail.Attachments);
        Assert.Equal("uid:7", mail.MessageId);
    }

    [Fact]
    public void Unparsable_date_is_null() => Assert.Null(FeedMail.FromMime(Build(date: "not a date"), "7").EmailDate);
}
