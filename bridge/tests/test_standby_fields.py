"""A reaction made ready (#102 adopted, plan D - contract 1.40): a package's standby passes the bridge's structural check
when it has its on, request, play_serial and count (expires_seconds a number or left out); else 400. The app judges the
rest (the play's serial, the count, what its blocks may hold). The delivery log names its request. No game, no outside
network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import Bridge, package  # noqa: E402


def standby(**over):
    sb = {"on": "climax", "request": "next-climax-1", "play_serial": 3, "count": 1, "expires_seconds": 120}
    sb.update(over)
    return sb


class StandbyFields(unittest.TestCase):
    def setUp(self):
        self.b = Bridge(18877, test=True)

    def tearDown(self):
        self.b.close()

    def test_a_standby_is_taken(self):
        p = package("……っ。", 1)
        p["standby"] = standby()
        self.assertEqual(self.b.call("POST", "/api/packages", p)[0], 200)
        q = package("期限なし。", 2)
        q["standby"] = standby(request="next-climax-2", expires_seconds=None)
        self.assertEqual(self.b.call("POST", "/api/packages", q)[0], 200)

    def test_a_broken_standby_is_refused(self):
        bad = [
            "climax",
            standby(on=1),
            standby(request=""),
            standby(request="x" * 201),
            standby(play_serial=-1),
            standby(play_serial=True),
            standby(count=0),
            standby(count=1.5),
            standby(expires_seconds="60"),
        ]
        for i, sb in enumerate(bad):
            p = package("誤り。", 10 + i)
            p["standby"] = sb
            code, doc = self.b.call("POST", "/api/packages", p)
            self.assertEqual(code, 400, sb)
        p = package("欠け。", 30)
        sb = standby()
        del sb["count"]
        p["standby"] = sb
        self.assertEqual(self.b.call("POST", "/api/packages", p)[0], 400)

    def test_the_delivery_log_names_its_request(self):
        p = package("記録。", 40)
        p["id"] = "standby-log-check"
        p["standby"] = standby(request="logged-one")
        self.assertEqual(self.b.call("POST", "/api/packages", p)[0], 200)
        code, doc = self.b.call("GET", "/api/packages/status?id=standby-log-check", role="muse")
        self.assertEqual(code, 200)
        self.assertEqual(doc.get("standby"), "logged-one")


if __name__ == "__main__":
    unittest.main()
