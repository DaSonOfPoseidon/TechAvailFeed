# Copies the feed mail still in the processed label into a local corpus for parity tests.
# Read-only on the mailbox: the label is opened with EXAMINE and bodies are fetched with PEEK.
# The corpus is real feed data: it stays in the gitignored corpus/ and never leaves this machine.
import imaplib
import sys
import time
from datetime import UTC, datetime
from pathlib import Path

from feed.config import Config
from feed.mail import archive, to_feed_mail


def main(directory: Path) -> None:
    config = Config.from_env()
    imap = imaplib.IMAP4_SSL(config.imap_host, config.imap_port)
    imap.login(config.imap_user, config.imap_password)
    try:
        status, data = imap.select(f'"{config.processed_label}"', readonly=True)
        if status != "OK":
            raise RuntimeError(f"could not open {config.processed_label}: {data!r}")
        status, data = imap.uid("SEARCH", None, "ALL")
        uids = data[0].split() if status == "OK" else []
        saved = 0
        for uid in uids:
            status, parts = imap.uid("FETCH", uid, "(INTERNALDATE BODY.PEEK[])")
            if status != "OK" or not parts or parts[0] is None:
                print(f"could not fetch uid {uid.decode()}", file=sys.stderr)
                continue
            meta, raw = parts[0]
            internal = imaplib.Internaldate2tuple(meta)
            received = datetime.fromtimestamp(time.mktime(internal), tz=UTC) if internal else None
            saved += archive(directory, to_feed_mail(uid, raw, received))
        print(f"{len(uids)} mails in {config.processed_label}, {saved} new in {directory}")
    finally:
        imap.logout()


if __name__ == "__main__":
    main(Path(sys.argv[1] if len(sys.argv) > 1 else "corpus/mail"))
