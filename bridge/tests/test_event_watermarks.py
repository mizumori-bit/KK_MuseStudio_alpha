"""The events lost and the three watermarks (#70 adopted, 2): a full feed that drops its oldest unread events tells it in the
feed itself (events_dropped: from, to, count) and keeps the last range lost; the feed's side - next_id, acked_upto (nothing
at or below it left: acked, or dropped) - on GET /api/events and /health; the state's side - events_upto, the feed's last
event when the app's state came. No game, no network (the store in memory over a temporary file; the routes on a spare
port).

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import shutil
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import bridge  # noqa: E402
sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import Bridge  # noqa: E402

APP = {"X-MMS-Instance": "win-1", "X-MMS-Started": "1000"}


def make_state(tmp: Path) -> "bridge.State":
    cfg = {"_audio_dir": tmp / "audio", "_events_file": tmp / "events.json", "irodori": {"enabled": False}}
    return bridge.State(cfg)


class Dropped(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="bridge_marks_"))
        self.st = make_state(self.tmp)
        self.keep = bridge.MAX_EVENTS
        bridge.MAX_EVENTS = 5

    def tearDown(self):
        bridge.MAX_EVENTS = self.keep
        if self.st.match is not None:
            self.st.match.stop.set()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_a_full_feed_tells_what_it_dropped(self):
        for i in range(5):
            self.st.add_event({"type": "t", "n": i})                 # (ids 1-5: the feed full)
        self.assertIsNone(self.st.feed_marks()["dropped"])
        self.st.add_event({"type": "t", "n": 5})                     # (id 6: 1 goes - and 2 for the marker, id 7)
        feed = self.st.peek_events(0, 100)
        marker = [e for e in feed if e["type"] == "events_dropped"]
        self.assertEqual(len(marker), 1)
        m = marker[0]
        self.assertEqual((m["from"], m["to"], m["count"], m["reason"]), (1, 2, 2, "feed_full"))
        self.assertEqual(m["id"], 7)
        self.assertEqual([e["id"] for e in feed], [3, 4, 5, 6, 7])
        marks = self.st.feed_marks()
        self.assertEqual((marks["next_id"], marks["acked_upto"], marks["pending"]), (8, 2, 5))
        self.assertEqual((marks["dropped"]["from"], marks["dropped"]["to"], marks["dropped"]["total"]), (1, 2, 2))

    def test_a_feed_full_for_long_keeps_one_marker(self):
        for i in range(12):
            self.st.add_event({"type": "t", "n": i})
        feed = self.st.peek_events(0, 100)
        markers = [e for e in feed if e["type"] == "events_dropped"]
        self.assertEqual(len(markers), 1)
        kept = [e["n"] for e in feed if e["type"] == "t"]
        self.assertEqual(kept, list(range(8, 12)))                   # (the newest four; the marker the fifth)
        m = markers[0]
        self.assertEqual((m["from"], m["count"]), (1, 8))             # (events 0-7 lost, joined in one range)
        self.assertEqual(self.st.feed_marks()["dropped"]["total"], 8)

    def test_acked_upto_is_what_left_the_feed(self):
        for i in range(3):
            self.st.add_event({"type": "t", "n": i})
        self.assertEqual(self.st.feed_marks()["acked_upto"], 0)
        self.st.ack_events({1}, None)
        self.assertEqual(self.st.feed_marks()["acked_upto"], 1)
        self.st.ack_events({3}, None)                                # (3 acked, 2 still there: not past 1)
        self.assertEqual(self.st.feed_marks()["acked_upto"], 1)
        self.st.ack_events(set(), 2)
        self.assertEqual(self.st.feed_marks()["acked_upto"], 3)       # (empty: all up to the last)


class Routes(unittest.TestCase):
    def setUp(self):
        self.b = Bridge(18875, test=True)

    def tearDown(self):
        self.b.close()

    def test_the_marks_on_the_routes(self):
        for i in range(2):
            self.assertEqual(self.b.call("POST", "/api/events", {"type": "t", "n": i}, role="app", headers=APP)[0], 200)
        self.assertEqual(self.b.call("POST", "/api/state", {"location": "a"}, role="app", headers=APP)[0], 200)
        self.b.call("POST", "/api/events", {"type": "t", "n": 2}, role="app", headers=APP)
        code, st = self.b.call("GET", "/api/state", role="muse")
        self.assertEqual(st["events_upto"], 2)                        # (the two before the state are in it; the third came after)
        code, ev = self.b.call("GET", "/api/events", role="muse")
        self.assertEqual((ev["next_id"], ev["acked_upto"], ev["dropped"]), (4, 0, None))
        self.b.call("POST", "/api/events/ack", {"upto": 2}, role="muse")
        code, health = self.b.call("GET", "/health", role="muse")
        f = health["event_feed"]
        self.assertEqual((f["next_id"], f["acked_upto"], f["pending"], f["dropped"]), (4, 2, 1, None))
        self.assertTrue(isinstance(f["oldest_pending_seconds"], int) and 0 <= f["oldest_pending_seconds"] < 60)     # (#70 adopted, 3)
        self.assertEqual(health["events"], 1)


if __name__ == "__main__":
    unittest.main()
