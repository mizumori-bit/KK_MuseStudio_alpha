"""After the start (#70 adopted, 1): the app tells each block it has shown (its operations done) and finished when it has
shown them all, or cut it short; a new launch makes the old launch's packages begun and not finished interrupted - the last
block known shown kept, the one after it not known either way (package_interrupted) - and leaves the finished ones and the
ones an older app began (no block count) as they are. No game, no outside network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import Bridge, package  # noqa: E402

APP = {"X-MMS-Instance": "win-1", "X-MMS-Started": "1000"}


class Interrupted(unittest.TestCase):
    def setUp(self):
        self.b = Bridge(18874, test=True)
        self.turn = 0

    def tearDown(self):
        self.b.close()

    def pkg(self, text="x", **kw):
        self.turn += 1
        p = package(text, self.turn)
        p.update(kw)
        return p

    def ack(self, ids, stage, **kw):
        body = {"ids": ids, "stage": stage}
        body.update(kw)
        return self.b.call("POST", "/api/packages/ack", body, role="app", headers=APP)

    def status(self, pid):
        return self.b.call("GET", "/api/packages/status?id=" + pid, role="muse")[1]

    def launch(self, lid, phase="character_selected"):
        return self.b.call("POST", "/api/launch", {"launch": lid, "phase": phase, "character": {"id": "card:a.png", "name": "a"}}, role="app", headers=APP)

    def events(self, kind):
        code, doc = self.b.call("GET", "/api/events?limit=500", role="muse")
        return [e for e in (doc or {}).get("events", []) if e.get("type") == kind]

    def begin(self, pid, blocks=3, shown=()):
        self.b.call("POST", "/api/packages", self.pkg("始める。", id=pid))
        self.b.call("GET", "/api/packages/next?ack=1", role="app", headers=APP)
        self.ack([pid], "received")
        self.assertEqual(self.ack([pid], "started", blocks=blocks)[0], 200)
        for i in shown:
            self.assertEqual(self.ack([pid], "started", block=i, blocks=blocks)[0], 200)

    def test_the_blocks_shown_are_kept(self):
        self.launch("L1")
        self.begin("m-1", 3, (0, 1))
        s = self.status("m-1")
        self.assertEqual((s["stage"], s["last_block"], s["blocks"]), ("started", 1, 3))
        self.ack(["m-1"], "started", block=0, blocks=3)                 # (an older block told late: last_block stays)
        self.assertEqual(self.status("m-1")["last_block"], 1)

    def test_a_new_launch_makes_the_unfinished_interrupted(self):
        self.launch("L1")
        self.begin("m-2", 3, (0, 1))
        self.begin("m-3", 2)                                            # (begun, no block shown yet)
        self.begin("m-4", 2, (0, 1))
        self.assertEqual(self.ack(["m-4"], "finished", block=1, blocks=2)[0], 200)
        self.assertEqual(self.launch("L2", "app_starting")[0], 200)
        self.assertEqual([self.status(i)["stage"] for i in ("m-2", "m-3", "m-4")], ["interrupted", "interrupted", "finished"])
        ev = {e["package"]: e for e in self.events("package_interrupted")}
        self.assertEqual(sorted(ev), ["m-2", "m-3"])
        self.assertEqual((ev["m-2"]["last_block"], ev["m-2"]["blocks"], ev["m-2"]["next_block"]), (1, 3, "unknown"))
        self.assertIsNone(ev["m-3"]["last_block"])
        self.assertEqual(self.events("package_dropped"), [])
        self.assertEqual(self.launch("L3", "app_starting")[0], 200)     # (told once)
        self.assertEqual(len(self.events("package_interrupted")), 2)

    def test_cut_short_is_finished_with_its_reason(self):
        self.launch("L1")
        self.begin("m-5", 4, (0,))
        self.assertEqual(self.ack(["m-5"], "finished", reason="superseded", block=0, blocks=4)[0], 200)
        s = self.status("m-5")
        self.assertEqual((s["stage"], s["reason"], s["last_block"]), ("finished", "superseded", 0))
        self.launch("L2", "app_starting")
        self.assertEqual(self.status("m-5")["stage"], "finished")

    def test_an_older_apps_start_is_left_as_it_is(self):
        self.launch("L1")
        self.b.call("POST", "/api/packages", self.pkg("古いアプリ。", id="m-6"))
        self.b.call("GET", "/api/packages/next?ack=1", role="app", headers=APP)
        self.ack(["m-6"], "received")
        self.ack(["m-6"], "started")                                   # (no block count: an app before this)
        self.launch("L2", "app_starting")
        self.assertEqual(self.status("m-6")["stage"], "started")
        self.assertEqual(self.events("package_interrupted"), [])

    def test_the_acks_shapes(self):
        self.launch("L1")
        self.begin("m-7", 2)
        for bad in ({"block": -1}, {"block": True}, {"block": "1"}, {"blocks": 10001}, {"block": 0, "stage": "received"},
                    {"block": 0, "stage": "dropped"}):
            body = {"stage": "started"}
            body.update(bad)
            stage = body.pop("stage")
            self.assertEqual(self.ack(["m-7"], stage, **body)[0], 400, bad)
        self.assertEqual(self.status("m-7")["stage"], "started")


if __name__ == "__main__":
    unittest.main()
