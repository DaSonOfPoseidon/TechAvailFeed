import csv
import hashlib
import io
import re
from dataclasses import dataclass, field
from datetime import date, datetime

# Legacy gap format, still emitted by the registered feed query until it is updated in MBS.
# MBS upper-cases unquoted aliases in its exports, so headers are matched case-insensitively.
REQUIRED_COLUMNS = (
    "generated_at",
    "work_date",
    "tech_id",
    "tech_name",
    "open_from",
    "open_until",
    "open_minutes",
    "region",
    "skills",
)

# Raw calendar blocks, emitted by the feed query
# (columns documented in docs/feed-format.md).
# A header with a "kind" column selects this format.
BLOCK_COLUMNS = (
    "generated_at",
    "kind",
    "work_date",
    "tech_id",
    "tech_name",
    "starts_at",
    "ends_at",
    "ref_id",
    "status",
    "region",
    "skills",
)
# Added after the first blocks release of the query went live on 09-29; a missing one reads as "".
# The last five (10-01) are job-only: the task's last save, when it last entered E / I in the
# past 2 days (task history), and every pre-drop/pre-bury task on its order that isn't U, as
# "PreDrop - Legacy: A, PreBury - Legacy: W". The last one's header is "Pre-Reqs Status".
OPTIONAL_BLOCK_COLUMNS = (
    "department",
    "task_type",
    "modified_at",
    "modified_by",
    "enroute_at",
    "inprogress_at",
    "pre-reqs status",
    # 10-02: the service address, as a flag and coordinates only. Never address text.
    "address_issue",
    "latitude",
    "longitude",
    # 10-05: the address's geocode precision (ROOFTOP, PARCEL, ZIP9), or "" when MBS has none.
    "gps_precision",
)
BLOCK_KINDS = (
    "shift",
    # 10-02: a schedule on the TC (in-house trouble) calendar only. Dual techs' schedules are tagged
    # FIELD and TC at once and stay "shift"; feed.availability keeps the two calendars apart.
    "shift_tc",
    "job",
    "ticket",
    "time_off",
    "job_moved",
    "ticket_moved",
    "job_unassigned",
    "ticket_unassigned",
)

DATE_FORMATS = ("%Y-%m-%d", "%m-%d-%Y", "%m/%d/%Y")
# MBS rewrites a seconds-precision value ("2026-09-29 12:03:34") as "09-29-2026 12:03:34".
TIMESTAMP_FORMATS = (
    "%Y-%m-%d %H:%M",
    "%Y-%m-%d %H:%M:%S",
    "%m-%d-%Y %H:%M:%S",
    "%m-%d-%Y %I:%M %p",
)


class FeedParseError(ValueError):
    pass


@dataclass
class Slot:
    work_date: date
    tech_id: str
    tech_name: str
    open_from: datetime
    open_until: datetime
    open_minutes: int
    region: str
    skills: str


@dataclass
class Block:
    kind: str
    work_date: date
    tech_id: str
    tech_name: str
    starts_at: datetime
    ends_at: datetime
    ref_id: str
    status: str
    department: str
    region: str
    skills: str
    task_type: str = ""
    modified_at: datetime | None = None
    modified_by: str = ""
    enroute_at: datetime | None = None
    inprogress_at: datetime | None = None
    prereqs_status: str = ""
    # None when the export predates the column, so "no issue" and "not checked" stay distinct.
    address_issue: str | None = None
    # Exact home-level coordinates: round with feed.coords.public_coords before serving.
    latitude: float | None = None
    longitude: float | None = None
    gps_precision: str = ""


@dataclass
class ParsedFeed:
    sha256: str
    format: str = "slots"
    generated_at: datetime | None = None
    slots: list[Slot] = field(default_factory=list)
    blocks: list[Block] = field(default_factory=list)

    @property
    def row_count(self) -> int:
        return len(self.blocks) if self.format == "blocks" else len(self.slots)


def parse_date(value: str) -> date:
    for fmt in DATE_FORMATS:
        try:
            return datetime.strptime(value.strip(), fmt).date()
        except ValueError:
            continue
    raise FeedParseError(f"unrecognised date {value!r}")


def parse_timestamp(value: str) -> datetime:
    for fmt in TIMESTAMP_FORMATS:
        try:
            return datetime.strptime(value.strip(), fmt)
        except ValueError:
            continue
    raise FeedParseError(f"unrecognised timestamp {value!r}")


# MBS's export can render a user id as "Last, First (userid)"; keep only the id.
USER_ID = re.compile(r"\(([^()]+)\)\s*$")


def parse_user_id(value: str) -> str:
    match = USER_ID.search(value)
    return match.group(1).strip() if match else value.strip()


def parse_optional_timestamp(value: str) -> datetime | None:
    return parse_timestamp(value) if value.strip() else None


def parse_coordinate(value: str, name: str, line_number: int) -> float | None:
    # MBS stores a missing GPS position as 0.
    try:
        number = float(value) if value.strip() else 0.0
    except ValueError as exc:
        raise FeedParseError(f"line {line_number}: bad {name} {value!r}") from exc
    return number or None


def parse_generated_at(value: str) -> datetime:
    # Postgres TO_CHAR ... OF gives a bare hour offset like "-05"; fromisoformat needs "-05:00".
    text = value.strip()
    if len(text) >= 3 and text[-3] in "+-" and text[-2:].isdigit():
        text += ":00"
    try:
        return datetime.fromisoformat(text)
    except ValueError as exc:
        raise FeedParseError(f"unrecognised generated_at {value!r}") from exc


def parse_feed(data: bytes) -> ParsedFeed:
    parsed = ParsedFeed(sha256=hashlib.sha256(data).hexdigest())
    try:
        text = data.decode("utf-8-sig")
    except UnicodeDecodeError as exc:
        # Must be a FeedParseError: anything else escapes the poll and blocks the mailbox.
        raise FeedParseError("file is not valid UTF-8") from exc
    reader = csv.reader(io.StringIO(text, newline=""), strict=True)

    try:
        header = next(reader)
    except StopIteration as exc:
        raise FeedParseError("file is empty, no header row") from exc
    except csv.Error as exc:
        raise FeedParseError(f"bad header row: {exc}") from exc

    columns = [name.strip().lower() for name in header]
    if "kind" in columns:
        parsed.format = "blocks"
    required = BLOCK_COLUMNS if parsed.format == "blocks" else REQUIRED_COLUMNS
    missing = [name for name in required if name not in columns]
    if missing:
        raise FeedParseError(f"missing columns: {', '.join(missing)}")
    index = {name: columns.index(name) for name in required}
    if parsed.format == "blocks":
        index.update(
            {name: columns.index(name) for name in OPTIONAL_BLOCK_COLUMNS if name in columns}
        )

    try:
        for line_number, row in enumerate(reader, start=2):
            if not any(cell.strip() for cell in row):
                continue
            if len(row) != len(columns):
                raise FeedParseError(
                    f"line {line_number}: expected {len(columns)} fields, got {len(row)}"
                )
            values = {name: row[i] for name, i in index.items()}
            if parsed.generated_at is None:
                parsed.generated_at = parse_generated_at(values["generated_at"])
            if parsed.format == "blocks":
                parsed.blocks.append(parse_block(values, line_number))
            else:
                parsed.slots.append(parse_slot(values, line_number))
    except csv.Error as exc:
        raise FeedParseError(f"malformed CSV: {exc}") from exc

    return parsed


def parse_slot(values: dict[str, str], line_number: int) -> Slot:
    try:
        minutes = int(float(values["open_minutes"]))
    except ValueError as exc:
        raise FeedParseError(
            f"line {line_number}: bad open_minutes {values['open_minutes']!r}"
        ) from exc
    return Slot(
        work_date=parse_date(values["work_date"]),
        tech_id=values["tech_id"].strip(),
        tech_name=values["tech_name"].strip(),
        open_from=parse_timestamp(values["open_from"]),
        open_until=parse_timestamp(values["open_until"]),
        open_minutes=minutes,
        region=values["region"].strip(),
        skills=values["skills"].strip(),
    )


def parse_block(values: dict[str, str], line_number: int) -> Block:
    kind = values["kind"].strip().lower()
    if kind not in BLOCK_KINDS:
        raise FeedParseError(f"line {line_number}: unknown kind {values['kind']!r}")
    return Block(
        kind=kind,
        work_date=parse_date(values["work_date"]),
        tech_id=values["tech_id"].strip(),
        tech_name=values["tech_name"].strip(),
        starts_at=parse_timestamp(values["starts_at"]),
        ends_at=parse_timestamp(values["ends_at"]),
        ref_id=values["ref_id"].strip(),
        status=values["status"].strip(),
        department=values.get("department", "").strip(),
        region=values["region"].strip(),
        skills=values["skills"].strip(),
        task_type=values.get("task_type", "").strip(),
        modified_at=parse_optional_timestamp(values.get("modified_at", "")),
        modified_by=parse_user_id(values.get("modified_by", "")),
        enroute_at=parse_optional_timestamp(values.get("enroute_at", "")),
        inprogress_at=parse_optional_timestamp(values.get("inprogress_at", "")),
        prereqs_status=values.get("pre-reqs status", "").strip(),
        address_issue=values["address_issue"].strip() if "address_issue" in values else None,
        latitude=parse_coordinate(values.get("latitude", ""), "latitude", line_number),
        longitude=parse_coordinate(values.get("longitude", ""), "longitude", line_number),
        gps_precision=values.get("gps_precision", "").strip().upper(),
    )
