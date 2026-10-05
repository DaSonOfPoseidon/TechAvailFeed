# Replays a local corpus of feed mail through the Python ingest into an empty database, in the
# order the mailbox received it, so the .NET port's replay (TechAvail.Parity replay) can be
# compared table by table. Prints counts only; the corpus is real feed data.
#
#   python -m tools.replay replay <root> <database_url>
#   python -m tools.replay compare <database_url> <database_url>
import email
import json
import os
import sys
from datetime import datetime
from pathlib import Path

import psycopg

from feed.__main__ import ingest
from feed.mail import sender_rejection, to_feed_mail
from feed.store import Store

# ingested_at is when each replay ran, so it never matches.
TABLES = {
    "snapshots": "SELECT * FROM snapshots ORDER BY id",
    "blocks": "SELECT * FROM blocks ORDER BY id",
    "slots": "SELECT * FROM slots ORDER BY snapshot_id, work_date, tech_id, open_from",
}
IGNORED = {"snapshots": {"ingested_at"}}


def corpus_order(root: Path) -> list[Path]:
    def received(eml: Path) -> tuple[str, str]:
        meta = json.loads(eml.with_suffix(".json").read_text())
        return (meta.get("mailbox_received_at") or "", eml.name)

    return sorted((root / "mail").glob("*.eml"), key=received)


def replay(root: Path, url: str) -> None:
    store = Store(url)
    store.init_schema()
    mail_from = os.environ["MAIL_FROM"]
    authserv_id = os.environ.get("AUTHSERV_ID", "mx.google.com")
    counts = {"processed": 0, "failed": 0, "rejected": 0, "duplicate": 0}
    for eml in corpus_order(root):
        raw = eml.read_bytes()
        if sender_rejection(email.message_from_bytes(raw), mail_from, authserv_id):
            counts["rejected"] += 1
            continue
        meta = json.loads(eml.with_suffix(".json").read_text())
        received = meta.get("mailbox_received_at")
        mail = to_feed_mail(b"0", raw, datetime.fromisoformat(received) if received else None)
        if store.seen(mail.message_id):
            counts["duplicate"] += 1
            continue
        counts["processed" if ingest(store, mail) else "failed"] += 1
    print(", ".join(f"{v} {k}" for k, v in counts.items()))


def rows(url: str, table: str) -> list[dict]:
    with psycopg.connect(url) as conn:
        cur = conn.execute(TABLES[table])
        names = [d.name for d in cur.description]
        ignored = IGNORED.get(table, set())
        return [
            {n: v for n, v in zip(names, row, strict=True) if n not in ignored}
            for row in cur.fetchall()
        ]


def compare(url_a: str, url_b: str) -> int:
    differing = 0
    for table in TABLES:
        a, b = rows(url_a, table), rows(url_b, table)
        bad = [i for i, (x, y) in enumerate(zip(a, b, strict=False)) if x != y]
        columns = sorted({k for i in bad[:50] for k in a[i] if a[i][k] != b[i].get(k)})
        same = len(a) == len(b) and not bad
        differing += not same
        detail = "" if same else f" ({len(bad)} rows differ, columns: {', '.join(columns)})"
        print(f"{table}: {len(a)} vs {len(b)} rows, {'identical' if same else 'DIFFERENT'}{detail}")
    return differing


if __name__ == "__main__":
    command, *rest = sys.argv[1:]
    if command == "replay":
        replay(Path(rest[0]), rest[1])
    else:
        sys.exit(1 if compare(*rest) else 0)
