from datetime import date
from email.message import EmailMessage

from feed.mail import imap_date, to_feed_mail, trash_folder


def build_message(*attachments: tuple[str, bytes]) -> bytes:
    msg = EmailMessage()
    msg["Subject"] = "TechAvailFeed"
    msg["From"] = "mbs@example.com"
    msg["Date"] = "Tue, 29 Sep 2026 09:16:40 -0500"
    msg["Message-ID"] = "<abc@example.com>"
    msg.set_content("Scheduled report attached.")
    for name, data in attachments:
        msg.add_attachment(data, maintype="text", subtype="csv", filename=name)
    return msg.as_bytes()


def test_extracts_csv_attachment_and_headers():
    raw = build_message(("TechAvailFeed.csv", b"a,b\n1,2\n"))
    mail = to_feed_mail(b"7", raw, None)
    assert mail.message_id == "<abc@example.com>"
    assert mail.email_date.isoformat() == "2026-09-29T09:16:40-05:00"
    assert mail.attachments == [("TechAvailFeed.csv", b"a,b\n1,2\n")]


def test_ignores_non_csv_parts_and_falls_back_to_uid():
    raw = build_message(("report.html", b"<html></html>"))
    raw = raw.replace(b"Message-ID: <abc@example.com>\n", b"")
    mail = to_feed_mail(b"7", raw, None)
    assert mail.attachments == []
    assert mail.message_id == "uid:7"


def test_finds_trash_by_special_use_flag():
    lines = [
        b'(\\HasNoChildren) "/" "INBOX"',
        b'(\\HasNoChildren \\Trash) "/" "[Gmail]/Papierkorb"',
    ]
    assert trash_folder(lines) == "[Gmail]/Papierkorb"
    assert trash_folder(lines[:1]) is None


def test_imap_date_format():
    assert imap_date(date(2026, 9, 3)) == "03-Sep-2026"
