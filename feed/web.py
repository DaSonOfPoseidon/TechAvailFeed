import json
import logging
import threading
from dataclasses import asdict, is_dataclass
from datetime import date, datetime, tzinfo
from decimal import Decimal
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from feed.availability import free_slots, unassigned_demand
from feed.coords import public_block
from feed.history import history
from feed.store import Store

log = logging.getLogger(__name__)


class PollState:
    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.last_poll_at: datetime | None = None
        self.last_poll_error: str | None = None

    def record(self, at: datetime, error: str | None) -> None:
        with self.lock:
            self.last_poll_at = at
            self.last_poll_error = error

    def as_dict(self) -> dict:
        with self.lock:
            return {"last_poll_at": self.last_poll_at, "last_poll_error": self.last_poll_error}


def to_json(value) -> bytes:
    def default(obj):
        if isinstance(obj, (datetime, date)):
            return obj.isoformat()
        if isinstance(obj, Decimal):
            return float(obj)
        if is_dataclass(obj):
            return asdict(obj)
        raise TypeError(f"not serialisable: {type(obj)}")

    return json.dumps(value, default=default, indent=1).encode()


def latest_view(store: Store, tz: tzinfo) -> dict | None:
    latest = store.latest()
    if latest is not None and "blocks" in latest:
        # Recomputed per request, so today's free time stays clipped to the real "now".
        now = datetime.now(tz).replace(tzinfo=None)
        latest["free"] = free_slots(latest["blocks"], now)
        latest["unassigned_demand"] = unassigned_demand(latest["blocks"], now.date())
        # A debug endpoint, but still served: never with exact home coordinates.
        latest["blocks"] = [public_block(b) for b in latest["blocks"]]
    return latest


def make_handler(store: Store, state: PollState, mail_configured: bool, tz: tzinfo):
    class Handler(BaseHTTPRequestHandler):
        def _send(self, status: int, body) -> None:
            payload = to_json(body)
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def do_GET(self) -> None:
            path = self.path.split("?", 1)[0]
            try:
                if path == "/health":
                    # Stays 200 before the first delivery; the poll fields say what is wrong.
                    self._send(
                        200, {"ok": True, "mail_configured": mail_configured, **state.as_dict()}
                    )
                elif path == "/latest.json":
                    latest = latest_view(store, tz)
                    self._send(200 if latest else 404, latest or {"error": "no snapshot yet"})
                elif path == "/history.json":
                    self._send(200, history(store, tz))
                elif path == "/runs.json":
                    self._send(200, {"summary": store.latency(), "runs": store.runs()})
                else:
                    self._send(404, {"error": "not found"})
            except Exception as exc:
                log.exception("request failed")
                self._send(500, {"error": str(exc)})

        def log_message(self, format, *args) -> None:
            log.debug(format, *args)

    return Handler


def serve(port: int, store: Store, state: PollState, mail_configured: bool, tz: tzinfo) -> None:
    handler = make_handler(store, state, mail_configured, tz)
    server = ThreadingHTTPServer(("0.0.0.0", port), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    log.info("http listening on %s", port)
