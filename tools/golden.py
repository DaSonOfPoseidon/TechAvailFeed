# Freezes what the Python pipeline makes of each mail in the local corpus, so a port can be
# checked against it byte-for-byte in meaning (tools/TechAvail.Parity). Output stays local:
# corpus/golden/ is gitignored. With --fixtures it writes the fake fixtures' goldens instead,
# which are committed and checked in CI.
#
#   python -m tools.golden [root]     <root>/mail/*.eml -> <root>/golden/*.json (+ DB cross-check)
#                                     root defaults to corpus
#   python -m tools.golden --fixtures tests/fixtures/*.csv -> contract/golden/fixtures/*.json
import dataclasses
import email
import hashlib
import json
import os
import sys
from datetime import date, datetime
from pathlib import Path

from feed.mail import sender_rejection, to_feed_mail
from feed.parse import FeedParseError, ParsedFeed, parse_feed

# The values the real feed passes with; only the From/Authentication-Results shape matters.
AUTHSERV_ID = os.environ.get("AUTHSERV_ID", "mx.google.com")


def plain(value):
    if isinstance(value, datetime | date):
        return value.isoformat()
    if isinstance(value, list):
        return [plain(v) for v in value]
    if isinstance(value, dict):
        return {k: plain(v) for k, v in value.items()}
    return value


def feed_json(data: bytes) -> dict:
    try:
        feed: ParsedFeed = parse_feed(data)
    except FeedParseError as exc:
        return {"error": str(exc)}
    rows = feed.blocks if feed.format == "blocks" else feed.slots
    return {
        "sha256": feed.sha256,
        "format": feed.format,
        "generated_at": plain(feed.generated_at),
        "row_count": feed.row_count,
        "rows": [plain(dataclasses.asdict(row)) for row in rows],
    }


def mail_json(raw: bytes, mail_from: str, received: str | None) -> dict:
    mail = to_feed_mail(b"0", raw, datetime.fromisoformat(received) if received else None)
    result = {
        "message_id": mail.message_id,
        "subject": mail.subject,
        "email_date": plain(mail.email_date),
        "sender_rejection": sender_rejection(email.message_from_bytes(raw), mail_from, AUTHSERV_ID),
        "attachments": [
            {"filename": name, "sha256": hashlib.sha256(data).hexdigest()}
            for name, data in mail.attachments
        ],
    }
    if len(mail.attachments) == 1:
        result["feed"] = feed_json(mail.attachments[0][1])
    return result


def write(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=1, sort_keys=True, ensure_ascii=False) + "\n")


def cross_check(golden: dict) -> list[str]:
    # The production snapshot for the same Message-ID must agree with the golden file.
    import psycopg

    feed = golden.get("feed", {})
    with psycopg.connect(os.environ["DATABASE_URL"]) as conn:
        row = conn.execute(
            "SELECT status, format, row_count, sha256, generated_at FROM snapshots"
            " WHERE message_id = %s",
            (golden["message_id"],),
        ).fetchone()
    if row is None:
        return ["no snapshot"]
    status, fmt, row_count, sha256, generated_at = row
    expected = {
        "status": "ok",
        "format": feed.get("format"),
        "row_count": feed.get("row_count"),
        "sha256": feed.get("sha256"),
        "generated_at": feed.get("generated_at"),
    }
    actual = {
        "status": status,
        "format": fmt,
        "row_count": row_count,
        "sha256": sha256,
        "generated_at": generated_at.isoformat() if generated_at else None,
    }
    # generated_at comes back in the session time zone, so compare instants.
    for side in (expected, actual):
        if side["generated_at"]:
            side["generated_at"] = datetime.fromisoformat(side["generated_at"]).timestamp()
    return [key for key in expected if expected[key] != actual[key]]


def corpus(root: Path) -> int:
    mail_from = os.environ["MAIL_FROM"]
    check_db = bool(os.environ.get("DATABASE_URL"))
    emls = sorted((root / "mail").glob("*.eml"))
    problems = 0
    for eml in emls:
        meta = json.loads(eml.with_suffix(".json").read_text())
        golden = mail_json(eml.read_bytes(), mail_from, meta.get("mailbox_received_at"))
        write(root / "golden" / f"{eml.stem}.json", golden)
        issues = []
        if golden["sender_rejection"] is not None:
            issues.append("sender rejected")
        if "rows" not in golden.get("feed", {}):
            issues.append("did not parse")
        if check_db:
            issues += cross_check(golden)
        if issues:
            problems += 1
            print(f"{eml.stem}: {', '.join(issues)}")
    db = "checked against snapshots" if check_db else "no DATABASE_URL, DB not checked"
    print(f"{len(emls)} mails, {problems} with problems ({db})")
    return problems


def fixtures(out: Path) -> None:
    for csv in sorted(Path("tests/fixtures").glob("*.csv")):
        write(out / f"{csv.stem}.json", feed_json(csv.read_bytes()))
        print(f"wrote {out / csv.stem}.json")


if __name__ == "__main__":
    if "--fixtures" in sys.argv:
        fixtures(Path("contract/golden/fixtures"))
    else:
        sys.exit(1 if corpus(Path(sys.argv[1] if len(sys.argv) > 1 else "corpus")) else 0)
