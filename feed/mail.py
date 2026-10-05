import email
import imaplib
import logging
import re
import time
from dataclasses import dataclass
from datetime import UTC, date, datetime
from email.message import Message
from email.utils import parseaddr, parsedate_to_datetime

from feed.config import Config

log = logging.getLogger(__name__)


@dataclass
class FeedMail:
    uid: bytes
    message_id: str
    subject: str
    email_date: datetime | None
    mailbox_received_at: datetime | None
    attachments: list[tuple[str, bytes]]


def csv_attachments(message: Message) -> list[tuple[str, bytes]]:
    found = []
    for part in message.walk():
        filename = part.get_filename()
        if not filename or not filename.lower().endswith(".csv"):
            continue
        payload = part.get_payload(decode=True)
        if payload is not None:
            found.append((filename, payload))
    return found


def to_feed_mail(uid: bytes, raw: bytes, internal_date: datetime | None) -> FeedMail:
    message = email.message_from_bytes(raw)
    date_header = message.get("Date")
    return FeedMail(
        uid=uid,
        # Some senders omit Message-ID; the IMAP UID is stable enough for dedupe within one mailbox.
        message_id=(message.get("Message-ID") or f"uid:{uid.decode()}").strip(),
        subject=str(message.get("Subject", "")),
        email_date=parsedate_to_datetime(date_header) if date_header else None,
        mailbox_received_at=internal_date,
        attachments=csv_attachments(message),
    )


def sender_rejection(message: Message, mail_from: str, authserv_id: str) -> str | None:
    # From headers are trivially forged, so the receiving server's verdict decides. It adds its
    # Authentication-Results header on top; any copies further down came from the sender.
    sender = parseaddr(str(message.get("From", "")))[1].lower()
    if sender != mail_from.lower():
        return f"sender {sender!r} is not MAIL_FROM"
    results = message.get_all("Authentication-Results") or []
    top = " ".join(str(results[0]).split()) if results else ""
    if not top.lower().startswith(f"{authserv_id.lower()};"):
        return f"no Authentication-Results from {authserv_id}"
    domain = sender.rpartition("@")[2]
    match = re.search(r"\bdmarc=pass\b[^;]*\bheader\.from=([^\s;]+)", top, re.IGNORECASE)
    if not match or match.group(1).lower() != domain:
        return f"DMARC did not pass for {domain}"
    return None


def trash_folder(list_lines: list[bytes]) -> str | None:
    # Gmail localises "[Gmail]/Trash"; the \\Trash special-use flag is what identifies it.
    for line in list_lines:
        text = line.decode(errors="replace")
        if "\\Trash" in text:
            return text.rsplit(' "/" ', 1)[-1].strip().strip('"')
    return None


def imap_date(day: date) -> str:
    months = "Jan Feb Mar Apr May Jun Jul Aug Sep Oct Nov Dec".split()
    return f"{day.day:02d}-{months[day.month - 1]}-{day.year}"


class Mailbox:
    def __init__(self, config: Config):
        self.config = config

    def _search_criteria(self) -> list[str]:
        criteria = ["UNSEEN", "SUBJECT", f'"{self.config.mail_subject}"']
        if self.config.mail_from:
            criteria += ["FROM", f'"{self.config.mail_from}"']
        return criteria

    def fetch_new(self) -> tuple[imaplib.IMAP4_SSL, list[FeedMail]]:
        imap = imaplib.IMAP4_SSL(self.config.imap_host, self.config.imap_port)
        imap.login(self.config.imap_user, self.config.imap_password)
        imap.select("INBOX")
        status, data = imap.uid("SEARCH", None, *self._search_criteria())
        if status != "OK":
            raise RuntimeError(f"IMAP search failed: {data!r}")
        mails = []
        for uid in data[0].split():
            # BODY.PEEK leaves the message unread until it has been stored.
            status, parts = imap.uid("FETCH", uid, "(INTERNALDATE BODY.PEEK[])")
            if status != "OK" or not parts or parts[0] is None:
                log.warning("could not fetch uid %s", uid)
                continue
            meta, raw = parts[0]
            internal = imaplib.Internaldate2tuple(meta)
            # Internaldate2tuple returns local time; mktime inverts it to an epoch.
            internal_date = (
                datetime.fromtimestamp(time.mktime(internal), tz=UTC) if internal else None
            )
            rejection = sender_rejection(
                email.message_from_bytes(raw), self.config.mail_from, self.config.authserv_id
            )
            if rejection:
                # Filed away unread, so it isn't fetched again and stays there for inspection.
                log.warning("rejected uid %s: %s", uid, rejection)
                self.file_away(imap, uid, self.config.failed_label)
                continue
            mails.append(to_feed_mail(uid, raw, internal_date))
        return imap, mails

    def file_away(self, imap: imaplib.IMAP4_SSL, uid: bytes, label: str) -> None:
        # Gmail: copying to a label and expunging from INBOX archives the message under that label.
        imap.uid("COPY", uid, label)
        imap.uid("STORE", uid, "+FLAGS", "(\\Seen \\Deleted)")
        imap.expunge()

    def ensure_labels(self, imap: imaplib.IMAP4_SSL) -> None:
        for label in (self.config.processed_label, self.config.failed_label):
            imap.create(label)

    def purge_processed(self, imap: imaplib.IMAP4_SSL, before: date) -> int:
        # Permanently deletes processed mail received before `before`. Expunging from a Gmail
        # label only removes the label, so it goes to Trash first and is expunged there too.
        status, lines = imap.list()
        trash = trash_folder(lines) if status == "OK" else None
        if trash is None:
            raise RuntimeError("could not find the Gmail Trash folder")
        # Only feed mail: anything else filed under the label, or sitting in Trash, is left alone.
        criteria = ["BEFORE", imap_date(before), "SUBJECT", f'"{self.config.mail_subject}"']
        imap.select(f'"{self.config.processed_label}"')
        status, data = imap.uid("SEARCH", None, *criteria)
        uids = data[0].split() if status == "OK" else []
        if not uids:
            return 0
        uid_set = b",".join(uids).decode()
        imap.uid("COPY", uid_set, f'"{trash}"')
        imap.uid("STORE", uid_set, "+FLAGS", "(\\Deleted)")
        imap.expunge()
        imap.select(f'"{trash}"')
        status, data = imap.uid("SEARCH", None, *criteria)
        if status == "OK" and data[0]:
            imap.uid("STORE", b",".join(data[0].split()).decode(), "+FLAGS", "(\\Deleted)")
            imap.expunge()
        return len(uids)
