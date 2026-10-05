import json
import threading
import urllib.error
import urllib.request
from datetime import UTC
from http.server import ThreadingHTTPServer

import pytest

from feed.web import PollState, make_handler


class EmptyStore:
    def latency(self):
        return {}

    def runs(self):
        return []


@pytest.fixture
def server():
    handler = make_handler(EmptyStore(), PollState(), True, UTC, api_key="s3cret")
    httpd = ThreadingHTTPServer(("127.0.0.1", 0), handler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    yield f"http://127.0.0.1:{httpd.server_address[1]}"
    httpd.shutdown()


def get(url: str, key: str | None = None) -> int:
    request = urllib.request.Request(url, headers={"X-API-Key": key} if key else {})
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            json.load(response)
            return response.status
    except urllib.error.HTTPError as error:
        return error.code


def test_health_needs_no_key(server):
    assert get(f"{server}/health") == 200


def test_data_endpoints_need_the_key(server):
    assert get(f"{server}/runs.json") == 401
    assert get(f"{server}/runs.json", "wrong") == 401
    assert get(f"{server}/runs.json", "s3cret") == 200
