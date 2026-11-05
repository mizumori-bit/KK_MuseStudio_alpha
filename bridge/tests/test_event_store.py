"""The bridge's event feed and the app's event ids (GPT's review of PR #14): a resend is kept once - also when saving failed
once on the way, and after an ack and a restart; the ids are not in the feed. No game, no network (the store in memory over
a temporary file).

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import bridge  # noqa: E402


def make_state(tmp: Path) -> "bridge.State":
    cfg = {"_audio_dir": tmp / "audio", "_events_file": tmp / "events.json", "irodori": {"enabled": False}}
    return bridge.State(cfg)


class EventStore(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="bridge_store_"))
        self.st = make_state(self.tmp)

    def tearDown(self):
        if self.st.match is not None:
            self.st.match.stop.set()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_resend_kept_once_and_ids_not_in_the_feed(self):
        a = self.st.add_app_event({"type": "t", "n": 1}, "app-1")
        b = self.st.add_app_event({"type": "t", "n": 1}, "app-1")
        self.assertEqual((a, b), ((1, False), (1, True)))
        feed = self.st.peek_events(0, 100)
        self.assertEqual(len(feed), 1)
        self.assertNotIn("_cid", feed[0])
        self.assertNotIn("_cid", json.loads((self.tmp / "events.json").read_text(encoding="utf-8"))["events"][0])

    def test_a_failed_save_changes_nothing(self):          # P2: the event was shown, its id not - added again on the resend
        real = self.st._save_events
        calls = {"n": 0}

        def failing(*a, **k):
            calls["n"] += 1
            if calls["n"] == 1:
                raise OSError("disk full (test)")
            return real(*a, **k)
        self.st._save_events = failing
        with self.assertRaises(OSError):
            self.st.add_app_event({"type": "t", "n": 1}, "app-1")
        self.assertEqual((self.st.peek_events(0, 100), self.st.next_event_id), ([], 1))
        self.assertNotIn("app-1", self.st.event_ids)
        self.assertEqual(self.st.add_app_event({"type": "t", "n": 1}, "app-1"), (1, False))
        self.assertEqual(self.st.add_app_event({"type": "t", "n": 1}, "app-1"), (1, True))
        self.assertEqual(len(self.st.peek_events(0, 100)), 1)

    def test_resend_after_ack_and_restart_kept_once(self):  # P2: the ids came back only from unacknowledged events
        eid, _ = self.st.add_app_event({"type": "t", "n": 1}, "app-1")
        self.st.ack_events({eid}, None)
        self.assertEqual(self.st.peek_events(0, 100), [])
        if self.st.match is not None:
            self.st.match.stop.set()
        self.st = make_state(self.tmp)                        # (the bridge started again over the same file)
        self.assertEqual(self.st.add_app_event({"type": "t", "n": 1}, "app-1"), (eid, True))
        self.assertEqual(self.st.peek_events(0, 100), [])

    def test_the_ids_remembered_are_bounded(self):
        keep = bridge.MAX_EVENT_IDS
        bridge.MAX_EVENT_IDS = 5
        try:
            for i in range(8):
                self.st.add_app_event({"type": "t", "n": i}, f"app-{i}")
            self.assertEqual(list(self.st.event_ids), [f"app-{i}" for i in range(3, 8)])
        finally:
            bridge.MAX_EVENT_IDS = keep

    def test_first_version_ids_in_events_are_read(self):    # (the file the first version wrote: the id inside the event)
        (self.tmp / "events.json").write_text(json.dumps({"next_id": 2, "events": [{"id": 1, "time": "x", "type": "t", "_cid": "app-9"}]}),
                                              encoding="utf-8")
        if self.st.match is not None:
            self.st.match.stop.set()
        self.st = make_state(self.tmp)
        self.assertEqual(self.st.add_app_event({"type": "t"}, "app-9"), (1, True))
        self.assertNotIn("_cid", self.st.peek_events(0, 100)[0])


if __name__ == "__main__":
    unittest.main()
