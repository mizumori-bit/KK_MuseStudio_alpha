"""Muse's start of a normal play (MUSE_GUIDE 3.7; GPT's handoff of Alpha PR #1): the package's play.start with its request,
basis (the state's play_serial), location, spot, reverse and anal - the bridge checks the shapes, the app the rest as it
shows. No game, no outside network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import bridge  # noqa: E402


def pkg(play):
    return {"session": "s", "turn": 1, "launch": "abc", "character": "card:a.png",
            "blocks": [{"type": "dialogue", "text": "はい。", "play": play}]}


class PlayStartShape(unittest.TestCase):
    def test_a_whole_start_passes(self):
        good = {"start": "sonyu_00", "request": "r-1", "basis": 4, "location": "myroom", "spot": "here", "reverse": False,
                "anal": False, "pace": 1}
        self.assertEqual(bridge.validate_package(pkg(good)), [])

    def test_each_field_of_the_wrong_shape_is_400(self):
        for field, bad in (("request", 5), ("request", ""), ("request", "x" * 201), ("basis", -1), ("basis", 1.5), ("basis", "3"),
                           ("basis", True), ("location", 3), ("reverse", "yes"), ("anal", 1), ("start", 7)):
            with self.subTest(field=field, value=bad):
                errs = bridge.validate_package(pkg({"start": "sonyu_00", "request": "r", "basis": 0, "location": "myroom", field: bad}))
                self.assertTrue(any(f"play.{field}" in e for e in errs), errs)

    def test_nulls_are_as_omitted(self):
        self.assertEqual(bridge.validate_package(pkg({"start": "aibu_00", "request": None, "basis": None, "location": None,
                                                       "reverse": None})), [])



class SettingsShape(unittest.TestCase):
    # (Muse's change of the speeds, PR #12 adopted: the bridge checks the shapes, the app the names and the ranges)
    def test_numbers_and_a_request_pass(self):
        p = {"session": "s", "turn": 1, "launch": "a", "character": "card:a.png", "settings": {"gauge_speed": 1.5, "arousal_speed": 2, "request": "r-1"},
             "blocks": [{"type": "dialogue", "text": "はい。"}]}
        self.assertEqual(bridge.validate_package(p), [])

    def test_wrong_shapes_are_400(self):
        for st, field in (("fast", "settings must be"), ({"gauge_speed": "1.5"}, "settings.gauge_speed"), ({"gauge_speed": True}, "settings.gauge_speed"),
                          ({"request": 3}, "settings.request"), ({"request": ""}, "settings.request")):
            with self.subTest(st=st):
                p = {"session": "s", "turn": 1, "settings": st, "blocks": [{"type": "dialogue", "text": "x"}]}
                self.assertTrue(any(field in e for e in bridge.validate_package(p)), bridge.validate_package(p))


class ReceivedTime(unittest.TestCase):
    # (the app's package_shown measures the wait from the bridge's taking: PR #12 adopted)
    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        from _helpers import Bridge, package
        cls.package = staticmethod(package)
        cls.b = Bridge(18871)

    @classmethod
    def tearDownClass(cls):
        cls.b.close()

    def test_the_package_carries_when_it_came(self):
        import time
        before = int(time.time() * 1000)
        code, r = self.b.call("POST", "/api/packages", self.package("はい。"))
        after = int(time.time() * 1000)
        self.assertEqual(code, 200, r)
        self.assertTrue(before <= r["received_ms"] <= after, r)
        self.assertRegex(r["received"], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}$")
        code, got = self.b.call("GET", "/api/packages/next")
        self.assertEqual(code, 200)
        self.assertEqual(got["_received_ms"], r["received_ms"])

    def test_a_muse_sent_time_is_overwritten(self):
        p = self.package("x", 2)
        p["_received_ms"] = 1
        code, r = self.b.call("POST", "/api/packages", p)
        self.assertEqual(code, 200, r)
        code, got = self.b.call("GET", "/api/packages/next")
        self.assertEqual(got["_received_ms"], r["received_ms"])
        self.assertNotEqual(got["_received_ms"], 1)


if __name__ == "__main__":
    unittest.main()
