import logging
import time
from datetime import UTC, datetime
from pathlib import Path
from zoneinfo import ZoneInfo

from feed.config import Config
from feed.history import finalize
from feed.mail import FeedMail, Mailbox, archive
from feed.parse import FeedParseError, parse_feed
from feed.store import Store
from feed.web import PollState, serve

log = logging.getLogger("feed")


def ingest(store: Store, mail: FeedMail) -> bool:
    # Returns True when the mail should be filed as processed, False for the failed label.
    common = {
        "message_id": mail.message_id,
        "channel": "email",
        "subject": mail.subject,
        "email_date": mail.email_date,
        "mailbox_received_at": mail.mailbox_received_at,
    }
    if len(mail.attachments) != 1:
        store.save(
            **common,
            filename=None,
            feed=None,
            error=f"expected 1 CSV attachment, found {len(mail.attachments)}",
        )
        return False
    filename, data = mail.attachments[0]
    try:
        feed = parse_feed(data)
    except FeedParseError as exc:
        store.save(**common, filename=filename, feed=None, error=str(exc))
        return False
    snapshot_id = store.save(**common, filename=filename, feed=feed)
    log.info("snapshot %s: %s %s from %s", snapshot_id, feed.row_count, feed.format, filename)
    if not feed.row_count:
        log.warning("snapshot %s had no rows; the previous snapshot stays current", snapshot_id)
    return True


def poll_once(store: Store, mailbox: Mailbox) -> None:
    imap, mails = mailbox.fetch_new()
    ingested = False
    try:
        mailbox.ensure_labels(imap)
        for mail in mails:
            if store.seen(mail.message_id):
                ok = True
            else:
                ok = ingest(store, mail)
                ingested = True
            if ok and mailbox.config.archive_dir:
                archive(Path(mailbox.config.archive_dir), mail)
            label = mailbox.config.processed_label if ok else mailbox.config.failed_label
            mailbox.file_away(imap, mail.uid, label)
        today = datetime.now(ZoneInfo(mailbox.config.mail_tz)).date()
        purged = mailbox.purge_processed(imap, today)
        if purged:
            log.info("deleted %s processed emails from before %s", purged, today)
    finally:
        imap.logout()
    if ingested:
        written = finalize(store, ZoneInfo(mailbox.config.mail_tz))
        if written:
            log.info("finalized outcome history for %s days", written)


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    config = Config.from_env()
    store = Store(config.database_url)
    store.init_schema()
    state = PollState()
    serve(config.http_port, store, state, config.mail_configured, ZoneInfo(config.mail_tz))

    if not config.mail_configured:
        log.warning("IMAP_USER / IMAP_PASSWORD not set; serving HTTP only")
    mailbox = Mailbox(config)
    while True:
        if config.mail_configured:
            error = None
            try:
                poll_once(store, mailbox)
            except Exception as exc:
                log.exception("poll failed")
                error = str(exc)
            state.record(datetime.now(UTC), error)
        time.sleep(config.poll_seconds)


if __name__ == "__main__":
    main()
