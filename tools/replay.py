# Replays a local corpus of feed mail through the Python ingest into an empty database, in the
# order the mailbox received it, so the .NET port's replay (TechAvail.Parity replay) can be
# compared table by table. Prints counts only; the corpus is real feed data.
#
#   python -m tools.replay replay <root> <database_url>
#   python -m tools.replay compare <database_url> <database_url>
#   python -m tools.replay finalize <database_url>
#   python -m tools.replay history <database_url> <start> <end> <out.json>
#   python -m tools.replay diff <a.json> <b.json>
import email
import json
import os
import sys
from datetime import date, datetime
from pathlib import Path
from zoneinfo import ZoneInfo

import psycopg

from feed.__main__ import ingest
from feed.history import finalize, outcomes
from feed.mail import sender_rejection, to_feed_mail
from feed.outcomes import summarise
from feed.store import Store

# ingested_at is when each replay ran, so it never matches.
TABLES = {
    "snapshots": "SELECT * FROM snapshots ORDER BY id",
    "blocks": "SELECT * FROM blocks ORDER BY id",
    "slots": "SELECT * FROM slots ORDER BY snapshot_id, work_date, tech_id, open_from",
}
IGNORED = {"snapshots": {"ingested_at"}}
OUTCOME_TABLES = {
    "outcome_days": "SELECT * FROM outcome_days ORDER BY plan_date",
    "job_outcomes": "SELECT * FROM job_outcomes ORDER BY plan_date, kind, ref_id",
}
TZ = ZoneInfo(os.environ.get("MAIL_TZ", "America/Chicago"))


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
        cur = conn.execute({**TABLES, **OUTCOME_TABLES}[table])
        names = [d.name for d in cur.description]
        ignored = IGNORED.get(table, set())
        return [
            {n: v for n, v in zip(names, row, strict=True) if n not in ignored}
            for row in cur.fetchall()
        ]


def compare(url_a: str, url_b: str, tables=TABLES) -> int:
    differing = 0
    for table in tables:
        a, b = rows(url_a, table), rows(url_b, table)
        bad = [i for i, (x, y) in enumerate(zip(a, b, strict=False)) if x != y]
        columns = sorted({k for i in bad[:50] for k in a[i] if a[i][k] != b[i].get(k)})
        same = len(a) == len(b) and not bad
        differing += not same
        detail = "" if same else f" ({len(bad)} rows differ, columns: {', '.join(columns)})"
        print(f"{table}: {len(a)} vs {len(b)} rows, {'identical' if same else 'DIFFERENT'}{detail}")
    return differing


def plain(value):
    if isinstance(value, datetime | date):
        return value.isoformat()
    if isinstance(value, dict):
        return {k: plain(v) for k, v in value.items()}
    if isinstance(value, list | tuple):
        return [plain(v) for v in value]
    return value


def history(url: str, start: str, end: str, out: str) -> None:
    # Every day in the range as outcomes() returns it, with its summary.
    days = []
    for outcome, provisional in outcomes(
        Store(url), TZ, date.fromisoformat(start), date.fromisoformat(end)
    ):
        days.append(
            {
                "day": outcome.day,
                "status": outcome.status,
                "provisional": provisional,
                "morning": [outcome.morning.id, outcome.morning.at] if outcome.morning else None,
                "items": [vars(item) for item in outcome.items],
                "added_after_morning": outcome.added_after_morning,
                "summary": summarise(outcome) if outcome.status == "ok" else None,
            }
        )
    Path(out).write_text(json.dumps(plain(days), indent=1, sort_keys=True))
    print(f"{len(days)} days")


def diff(a, b, path="") -> list[str]:
    if isinstance(a, dict) and isinstance(b, dict):
        return [
            p
            for key in sorted(set(a) | set(b))
            for p in (
                diff(a[key], b[key], f"{path}.{key}")
                if key in a and key in b
                else [f"{path}.{key} (missing on one side)"]
            )
        ]
    if isinstance(a, list) and isinstance(b, list):
        found = [] if len(a) == len(b) else [f"{path} (length {len(a)} vs {len(b)})"]
        return found + [
            p
            for i, (x, y) in enumerate(zip(a, b, strict=False))
            for p in diff(x, y, f"{path}[{i}]")
        ]
    return [] if a == b else [path]


if __name__ == "__main__":
    command, *rest = sys.argv[1:]
    if command == "replay":
        replay(Path(rest[0]), rest[1])
    elif command == "finalize":
        print(f"{finalize(Store(rest[0]), TZ)} days finalized")
    elif command == "history":
        history(*rest)
    elif command == "diff":
        found = diff(*(json.loads(Path(p).read_text()) for p in rest))
        print(f"{len(found)} differences" + (f": {', '.join(found[:10])}" if found else ""))
        sys.exit(1 if found else 0)
    elif command == "compare-outcomes":
        sys.exit(1 if compare(*rest, tables=OUTCOME_TABLES) else 0)
    else:
        sys.exit(1 if compare(*rest) else 0)
