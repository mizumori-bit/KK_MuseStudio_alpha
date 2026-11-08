"""The bridge's totals (the security review of 2026-11-05, SEC-07): the package queue (429), a body too big (413), the event
feed's bytes (the oldest dropped), the old choice queue. No game, no outside network.

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
sys.path.insert(0, str(Path(__file__).resolve().parent))
import bridge  # noqa: E402
from _helpers import Bridge, package  # noqa: E402


class EventBytes(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="bridge_bytes_"))
        self.st = bridge.State({"_audio_dir": self.tmp / "audio", "_events_file": self.tmp / "events.json", "irodori": {"enabled": False}})
        self.keep = bridge.MAX_EVENTS_BYTES
        bridge.MAX_EVENTS_BYTES = 10000

    def tearDown(self):
        bridge.MAX_EVENTS_BYTES = self.keep
        if self.st.match is not None:
            self.st.match.stop.set()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_the_feed_stays_under_its_bytes(self):
        for i in range(30):
            self.st.add_event({"type": "t", "n": i, "pad": "x" * 1000})
        feed = self.st.peek_events(0, 1000)
        self.assertLessEqual(sum(bridge._size(e) for e in feed), 10000)
        kept = [e for e in feed if e["type"] != "events_dropped"]
        self.assertEqual(kept[-1]["n"], 29)                  # (the newest kept, the oldest gone)
        self.assertEqual(feed[-1]["type"], "events_dropped")  # (#70 adopted, 2: what went told in the feed)
        self.assertGreater(kept[0]["n"], 0)
        self.assertEqual(sum(1 for e in feed if e["type"] == "events_dropped"), 1)     # (one marker: the ranges joined)
        self.assertEqual(set(self.st.event_sizes), {e["id"] for e in feed})
        self.st.ack_events({feed[0]["id"]}, None)
        self.assertEqual(set(self.st.event_sizes), {e["id"] for e in self.st.peek_events(0, 1000)})

    def test_one_event_bigger_than_the_total_is_still_kept(self):     # (only with a total set under MAX_EVENT_BYTES)
        self.st.add_event({"type": "t", "pad": "x" * 20000})
        self.assertEqual(len(self.st.peek_events(0, 10)), 1)

    def test_sizes_are_utf8_bytes(self):                    # GPT's review of PR #19: characters were counted
        ev = {"type": "t", "text": "あ" * 100}
        self.assertEqual(bridge._size(ev), len(json.dumps(ev, ensure_ascii=False).encode("utf-8")))
        for i in range(30):
            self.st.add_event({"type": "t", "n": i, "pad": "あ" * 1000})
        feed = self.st.peek_events(0, 1000)
        self.assertLessEqual(sum(len(json.dumps(e, ensure_ascii=False).encode("utf-8")) for e in feed), 10000)
        self.assertEqual([e for e in feed if e["type"] != "events_dropped"][-1]["n"], 29)

    def test_one_event_too_big(self):                       # its own limit: the app's refused (413), the bridge's told as such
        keep = bridge.MAX_EVENT_BYTES
        bridge.MAX_EVENT_BYTES = 5000
        try:
            with self.assertRaises(bridge.TooLarge):
                self.st.add_app_event({"type": "t", "pad": "x" * 6000}, "c1")
            self.assertNotIn("c1", self.st.event_ids)
            self.st.add_event({"type": "match_package", "pad": "x" * 6000})
            feed = self.st.peek_events(0, 10)
            self.assertEqual((feed[-1]["type"], feed[-1]["of"]), ("event_too_large", "match_package"))
        finally:
            bridge.MAX_EVENT_BYTES = keep


class EventLimitsAtStart(unittest.TestCase):                 # GPT's review of PR #19: a file saved before the limits
    def test_the_limits_apply_when_the_file_is_read(self):
        tmp = Path(tempfile.mkdtemp(prefix="bridge_start_"))
        self.addCleanup(shutil.rmtree, tmp, True)
        events = [{"id": i, "time": "t", "type": "t", "pad": "x" * 1000} for i in range(1, 31)]
        (tmp / "events.json").write_text(json.dumps({"next_id": 100, "events": events, "event_ids": [["c5", 5]]}), encoding="utf-8")
        keep = bridge.MAX_EVENTS_BYTES
        bridge.MAX_EVENTS_BYTES = 10000
        try:
            st = bridge.State({"_audio_dir": tmp / "audio", "_events_file": tmp / "events.json", "irodori": {"enabled": False}})
            if st.match is not None:
                st.match.stop.set()
        finally:
            bridge.MAX_EVENTS_BYTES = keep
        feed = st.peek_events(0, 1000)
        self.assertLessEqual(sum(bridge._size(e) for e in feed), 10000)
        self.assertEqual(feed[-1]["id"], 30)
        self.assertEqual((st.next_event_id, st.event_ids.get("c5")), (100, 5))
        saved = json.loads((tmp / "events.json").read_text(encoding="utf-8"))
        self.assertEqual([e["id"] for e in saved["events"]], [e["id"] for e in feed])


class HttpLimits(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.b = Bridge(18794)

    @classmethod
    def tearDownClass(cls):
        cls.b.close()

    def test_1_body_too_big_is_413(self):
        # (declared too big: answered without its body read - sending the whole body, Windows may abort the connection
        # before the answer is read)
        import http.client
        c = http.client.HTTPConnection("127.0.0.1", self.b.port, timeout=10)
        c.putrequest("POST", "/api/packages")
        c.putheader("Content-Type", "application/json")
        c.putheader("Content-Length", str(bridge.MAX_BODY + 1))
        c.putheader("Authorization", "Bearer " + self.b.token("muse"))
        c.endheaders()
        r = c.getresponse()
        self.assertEqual(r.status, 413)
        c.close()

    def test_2_package_queue_full_is_429(self):
        for i in range(bridge.MAX_PACKAGES):
            self.assertEqual(self.b.call("POST", "/api/packages", package(turn=i + 1))[0], 200)
        code, doc = self.b.call("POST", "/api/packages", package(turn=999))
        self.assertEqual((code, doc["accepted"], doc["queue"]), (429, False, bridge.MAX_PACKAGES))
        self.assertEqual(self.b.call("GET", "/api/packages/next")[0], 200)          # (the app takes one: room again)
        self.assertEqual(self.b.call("POST", "/api/packages", package(turn=1000))[0], 200)
        while self.b.call("GET", "/api/packages/next")[0] == 200:
            pass

    def test_3_package_queue_bytes_is_429(self):
        big = package("x" * (1536 * 1024))
        n = 0
        while True:
            big = package("x" * (1536 * 1024), n + 1)      # (a new turn each time: the same content is one package - PR #24)
            code, _doc = self.b.call("POST", "/api/packages", big)
            if code != 200:
                break
            n += 1
            self.assertLess(n, 50)
        self.assertEqual(code, 429)
        self.assertLessEqual(n * bridge._size(big), bridge.MAX_PACKAGES_BYTES)
        while self.b.call("GET", "/api/packages/next")[0] == 200:
            pass

    def test_4_old_choice_queue_bounded(self):
        for i in range(bridge.MAX_CHOICES + 20):
            self.assertEqual(self.b.call("POST", "/api/choices", {"session": "t", "turn": i, "kind": "stance", "value": "a"})[0], 200)
        code, doc = self.b.call("GET", "/api/choices")
        self.assertEqual(code, 200)
        self.assertEqual(len(doc["choices"]), bridge.MAX_CHOICES)
        self.assertEqual(doc["choices"][-1]["turn"], bridge.MAX_CHOICES + 19)

    def test_5_japanese_packages_counted_in_bytes(self):    # GPT's review of PR #19: 11 of about 1.5 MiB all taken
        raws = (json.dumps(package("あ" * (512 * 1024), turn), ensure_ascii=False).encode("utf-8") for turn in range(1, 100))
        raw = next(raws)
        n = 0
        while True:
            code, _doc = self.b.call("POST", "/api/packages", raw=raw)
            if code != 200:
                break
            n += 1
            self.assertLess(n, 20)
            raw = next(raws)              # (a new turn each time: the same content is one package - PR #24)
        self.assertEqual(code, 429)
        self.assertLessEqual(n * len(raw), bridge.MAX_PACKAGES_BYTES)
        self.assertEqual(self.b.call("GET", "/api/packages/next")[0], 200)          # (one taken: its bytes free again)
        self.assertEqual(self.b.call("POST", "/api/packages", raw=raw)[0], 200)
        while self.b.call("GET", "/api/packages/next")[0] == 200:
            pass

    def test_6_old_choice_queue_bytes(self):                # GPT's review of PR #19: one choice's strings, the queue's bytes
        code, doc = self.b.call("POST", "/api/choices", {"session": "t", "turn": 1, "kind": "stance", "value": "a" * 4097})
        self.assertEqual(code, 400)
        for i in range(120):
            self.assertEqual(self.b.call("POST", "/api/choices", {"session": "t", "turn": i, "kind": "stance", "value": "あ" * 4096})[0], 200)
        code, doc = self.b.call("GET", "/api/choices")
        self.assertLessEqual(sum(bridge._size(c) for c in doc["choices"]), bridge.MAX_CHOICES_BYTES)
        self.assertEqual(doc["choices"][-1]["turn"], 119)


if __name__ == "__main__":
    unittest.main()
