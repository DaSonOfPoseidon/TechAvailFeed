from datetime import date, datetime, timedelta, timezone
from pathlib import Path

import pytest

from feed.parse import FeedParseError, parse_feed

HEADER = (
    "GENERATED_AT,WORK_DATE,TECH_ID,TECH_NAME,OPEN_FROM,OPEN_UNTIL,OPEN_MINUTES,REGION,SKILLS\n"
)
ROW = (
    '"2026-09-29T09:15:02-05","2026-09-29","jdoe0170","Jane Doe",'
    '"2026-09-29 13:00","2026-09-29 15:00",120,"North Core","CONN, INS, MDU"\n'
)


def test_parses_mbs_style_export():
    feed = parse_feed((HEADER + ROW).encode())
    assert feed.generated_at == datetime(
        2026, 9, 29, 9, 15, 2, tzinfo=timezone(timedelta(hours=-5))
    )
    assert len(feed.slots) == 1
    slot = feed.slots[0]
    assert slot.work_date == date(2026, 9, 29)
    assert slot.open_from == datetime(2026, 9, 29, 13, 0)
    assert slot.open_minutes == 120
    # The comma-separated skills list survives CSV quoting.
    assert slot.skills == "CONN, INS, MDU"


def test_headers_match_case_insensitively_and_in_any_order():
    header = (
        "skills,region,open_minutes,open_until,open_from,tech_name,tech_id,work_date,generated_at\n"
    )
    row = (
        '"MDU","North",60.0,"09-30-2026 04:00 PM","09-30-2026 03:00 PM",'
        '"A B","ab01","09-30-2026","2026-09-29T09:15:02+00"\n'
    )
    feed = parse_feed((header + row).encode())
    assert feed.slots[0].open_from == datetime(2026, 9, 30, 15, 0)
    assert feed.slots[0].open_minutes == 60


def test_header_only_is_an_empty_snapshot():
    feed = parse_feed(HEADER.encode())
    assert feed.slots == []
    assert feed.generated_at is None


def test_crlf_and_bom_are_accepted():
    data = ("\ufeff" + HEADER + ROW).replace("\n", "\r\n").encode()
    assert len(parse_feed(data).slots) == 1


def test_wrong_report_is_rejected():
    data = b'TICKET_NUMBER,SCHEDULED_START_TIME\n1,"09-24-2026 03:00 PM"\n'
    with pytest.raises(FeedParseError, match="missing columns"):
        parse_feed(data)


def test_truncated_mid_row_is_rejected():
    data = (HEADER + ROW + ROW[:40]).encode()
    with pytest.raises(FeedParseError):
        parse_feed(data)


def test_empty_file_is_rejected():
    with pytest.raises(FeedParseError, match="empty"):
        parse_feed(b"")


def test_sha256_is_stable():
    data = (HEADER + ROW).encode()
    assert parse_feed(data).sha256 == parse_feed(data).sha256


BLOCK_HEADER = (
    "GENERATED_AT,KIND,WORK_DATE,TECH_ID,TECH_NAME,STARTS_AT,ENDS_AT,REF_ID,STATUS,DEPARTMENT,"
    "REGION,SKILLS\n"
)
SHIFT_ROW = (
    '"2026-09-29T09:15:02-05","shift","2026-09-29","jdoe0170","Jane Doe",'
    '"2026-09-29 08:00","2026-09-29 17:00","","","","North Core","CONN, INS"\n'
)
JOB_ROW = (
    '"2026-09-29T09:15:02-05","job","2026-09-29","jdoe0170","Jane Doe",'
    '"2026-09-29 09:00","2026-09-29 11:00","123456","A","FIELD","",""\n'
)


def test_kind_column_selects_the_blocks_format():
    feed = parse_feed((BLOCK_HEADER + SHIFT_ROW + JOB_ROW).encode())
    assert feed.format == "blocks"
    assert feed.slots == []
    assert feed.row_count == 2
    shift, job = feed.blocks
    assert shift.kind == "shift"
    assert shift.region == "North Core"
    assert job.ref_id == "123456"
    assert job.department == "FIELD"
    assert job.starts_at == datetime(2026, 9, 29, 9, 0)


def test_legacy_header_stays_the_slots_format():
    feed = parse_feed((HEADER + ROW).encode())
    assert feed.format == "slots"
    assert feed.row_count == 1


def test_blocks_format_needs_all_its_columns():
    header = "GENERATED_AT,KIND,WORK_DATE,TECH_ID\n"
    with pytest.raises(FeedParseError, match="missing columns: tech_name"):
        parse_feed(header.encode())


def test_unknown_block_kind_is_rejected():
    data = (BLOCK_HEADER + SHIFT_ROW.replace('"shift"', '"meeting"')).encode()
    with pytest.raises(FeedParseError, match="unknown kind"):
        parse_feed(data)


def test_moved_rows_keep_the_sentinel_start():
    row = (
        '"2026-09-29T09:15:02-05","job_moved","9999-12-31","","",'
        '"9999-12-31 00:00","9999-12-31 00:00","123456","A","FLDSVCCON","",""\n'
    )
    block = parse_feed((BLOCK_HEADER + row).encode()).blocks[0]
    assert block.kind == "job_moved"
    assert block.starts_at == datetime(9999, 12, 31)
    assert block.tech_id == ""


def test_department_is_optional_for_the_first_blocks_release():
    header = BLOCK_HEADER.replace("DEPARTMENT,", "")
    row = JOB_ROW.replace('"FIELD",', "")
    block = parse_feed((header + row).encode()).blocks[0]
    assert block.department == ""


def test_task_type_is_read_when_present():
    header = BLOCK_HEADER.replace("DEPARTMENT,", "DEPARTMENT,TASK_TYPE,")
    row = JOB_ROW.replace('"FIELD",', '"FIELD","3",')
    assert parse_feed((header + row).encode()).blocks[0].task_type == "3"


def test_trace_columns_are_read_and_blank_ones_are_none():
    header = BLOCK_HEADER.replace(
        "SKILLS\n", "SKILLS,MODIFIED_AT,MODIFIED_BY,ENROUTE_AT,INPROGRESS_AT,Pre-Reqs Status\n"
    )
    trace = (
        '"2026-09-29 10:15:30","jsmi0171","","2026-09-29 09:05:00",'
        '"PreDrop - Legacy: C, PreBury - Legacy: A"'
    )
    job = JOB_ROW.replace('"",""\n', f'"","",{trace}\n')
    shift = SHIFT_ROW.replace('"CONN, INS"\n', '"CONN, INS","","","","",""\n')
    shift_block, job_block = parse_feed((header + shift + job).encode()).blocks
    assert job_block.modified_at == datetime(2026, 9, 29, 10, 15, 30)
    assert job_block.modified_by == "jsmi0171"
    assert job_block.enroute_at is None
    assert job_block.inprogress_at == datetime(2026, 9, 29, 9, 5)
    assert job_block.prereqs_status == "PreDrop - Legacy: C, PreBury - Legacy: A"
    assert shift_block.modified_at is None
    assert shift_block.prereqs_status == ""


def test_mbs_rewritten_seconds_timestamp_is_accepted():
    header = BLOCK_HEADER.replace("SKILLS\n", "SKILLS,MODIFIED_AT\n")
    job = JOB_ROW.replace('"",""\n', '"","","09-29-2026 17:03:34"\n')
    assert parse_feed((header + job).encode()).blocks[0].modified_at == datetime(
        2026, 9, 29, 17, 3, 34
    )


def test_modified_by_keeps_only_the_id_from_mbs_display_form():
    header = BLOCK_HEADER.replace("SKILLS\n", "SKILLS,MODIFIED_BY\n")
    for shown in ('"Doe, Jane (jdoe0170)"', '"jdoe0170"'):
        job = JOB_ROW.replace('"",""\n', f'"","",{shown}\n')
        assert parse_feed((header + job).encode()).blocks[0].modified_by == "jdoe0170"


def test_unassigned_rows_carry_no_tech_and_their_own_region():
    row = (
        '"2026-09-29T09:15:02-05","job_unassigned","2026-09-30","","",'
        '"2026-09-30 10:00","2026-09-30 12:00","123456","A","FIELD","North Core",""\n'
    )
    block = parse_feed((BLOCK_HEADER + row).encode()).blocks[0]
    assert block.kind == "job_unassigned"
    assert block.tech_id == ""
    assert block.region == "North Core"


ADDRESS_HEADER = BLOCK_HEADER.replace(
    "SKILLS\n", "SKILLS,ADDRESS_ISSUE,LATITUDE,LONGITUDE,GPS_PRECISION\n"
)


def test_address_columns_are_read():
    job = JOB_ROW.replace('"",""\n', '"","","","40.123456","-100.654321","ROOFTOP"\n')
    flagged = JOB_ROW.replace('"",""\n', '"","","no_gps","","",""\n')
    shift = SHIFT_ROW.replace('"CONN, INS"\n', '"CONN, INS","","","",""\n')
    shift_block, job_block, flagged_block = parse_feed(
        (ADDRESS_HEADER + shift + job + flagged).encode()
    ).blocks
    assert (job_block.latitude, job_block.longitude) == (40.123456, -100.654321)
    assert job_block.address_issue == ""
    assert job_block.gps_precision == "ROOFTOP"
    assert flagged_block.address_issue == "no_gps"
    assert flagged_block.latitude is None
    assert shift_block.address_issue == ""


def test_retired_gps_confidence_column_is_ignored():
    # The query as registered before 10-05 still sends GPS_CONFIDENCE (always blank) in its place.
    header = ADDRESS_HEADER.replace("GPS_PRECISION", "GPS_CONFIDENCE")
    job = JOB_ROW.replace('"",""\n', '"","","","40.123456","-100.654321",""\n')
    block = parse_feed((header + job).encode()).blocks[0]
    assert block.gps_precision == ""
    assert block.latitude == 40.123456


def test_zero_coordinates_mean_none():
    job = JOB_ROW.replace('"",""\n', '"","","","0","0.0",""\n')
    block = parse_feed((ADDRESS_HEADER + job).encode()).blocks[0]
    assert (block.latitude, block.longitude) == (None, None)


def test_export_without_address_columns_leaves_the_issue_unknown():
    block = parse_feed((BLOCK_HEADER + JOB_ROW).encode()).blocks[0]
    assert block.address_issue is None


def test_bad_coordinate_is_rejected():
    job = JOB_ROW.replace('"",""\n', '"","","","north","-92.3",""\n')
    with pytest.raises(FeedParseError, match="latitude"):
        parse_feed((ADDRESS_HEADER + job).encode())


def test_tc_only_shift_kind_is_accepted():
    row = SHIFT_ROW.replace('"shift"', '"shift_tc"')
    assert parse_feed((BLOCK_HEADER + row).encode()).blocks[0].kind == "shift_tc"


def test_sample_fixture_parses():
    data = (Path(__file__).parent / "fixtures" / "sample_feed.csv").read_bytes()
    feed = parse_feed(data)
    assert feed.format == "blocks"
    assert feed.row_count == 6
    assert {block.kind for block in feed.blocks} >= {"shift", "job", "ticket", "time_off"}
