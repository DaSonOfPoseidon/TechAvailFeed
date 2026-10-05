from datetime import date, datetime
from zoneinfo import ZoneInfo

from feed.parse import Block
from feed.web import latest_view


class FakeStore:
    def latest(self):
        job = Block(
            kind="job",
            work_date=date(2026, 10, 6),
            tech_id="a",
            tech_name="A",
            starts_at=datetime(2026, 10, 6, 9),
            ends_at=datetime(2026, 10, 6, 10),
            ref_id="1",
            status="A",
            department="FIELD",
            region="",
            skills="",
            latitude=40.123456,
            longitude=-100.654321,
        )
        return {"snapshot": {"id": 1}, "blocks": [job]}


def test_latest_json_never_serves_exact_coordinates():
    [job] = latest_view(FakeStore(), ZoneInfo("America/Chicago"))["blocks"]
    assert (job.latitude, job.longitude) == (40.123, -100.654)
