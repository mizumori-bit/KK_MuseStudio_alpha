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


if __name__ == "__main__":
    unittest.main()
