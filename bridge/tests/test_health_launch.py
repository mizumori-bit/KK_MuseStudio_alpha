"""What the launch waiter (claude-setup/examples/waiter.example.sh, WAITER_MODE=launchwait - #101 item 3, PR #108) reads in
the keyed /health, as the bridge gives it: app (the app came within APP_SEEN_SECONDS), launch {id, phase, character}. The
waiter signals an unreported launch at character_selected only while app is true - after the app is gone the last launch
stays (its phase still character_selected); a new launch starts from app_starting with no character; another character in
the same launch keeps the id and the phase; a bridge restart keeps the id and puts the phase back to bridge_started till the
app reports again. Without the key (or with a wrong one) /health says only that a key is needed - no launch, no app. No
game, no outside network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import sys
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import Bridge, _bridge  # noqa: E402

APP = {"X-MMS-Instance": "win-1", "X-MMS-Started": "1000"}
CHA = {"id": "card:a.png", "name": "a", "persona": {"no": 1, "name": "p"}}
CHB = {"id": "card:b.png", "name": "b", "persona": None}


class HealthForTheLaunchWaiter(unittest.TestCase):
    def setUp(self):
        self.b = Bridge(18964, test=True)

    def tearDown(self):
        self.b.close()

    def health(self):
        code, doc = self.b.call("GET", "/health", role="muse")
        self.assertEqual(code, 200)
        return doc

    def launch(self, lid, phase, character=None):
        body = {"launch": lid, "phase": phase}
        if character is not None:
            body["character"] = character
        self.assertEqual(self.b.call("POST", "/api/launch", body, role="app", headers=APP)[0], 200)

    def test_before_the_app(self):
        h = self.health()
        self.assertIs(h["app"], False)
        self.assertIsNone(h["launch"]["id"])
        self.assertEqual(h["launch"]["phase"], "bridge_started")

    def test_the_phases_and_the_character(self):
        self.launch("L1", "app_starting")
        h = self.health()
        self.assertIs(h["app"], True)
        self.assertEqual((h["launch"]["id"], h["launch"]["phase"], h["launch"]["character"]), ("L1", "app_starting", None))
        self.launch("L1", "stage_ready")
        self.assertEqual(self.health()["launch"]["phase"], "stage_ready")
        self.launch("L1", "character_selected", CHA)
        h = self.health()
        self.assertEqual((h["launch"]["id"], h["launch"]["phase"]), ("L1", "character_selected"))
        self.assertEqual(h["launch"]["character"], CHA)
        # another character in the same launch: the id and the phase stay, the character is the new one
        self.launch("L1", "character_selected", CHB)
        h = self.health()
        self.assertEqual((h["launch"]["id"], h["launch"]["phase"]), ("L1", "character_selected"))
        self.assertEqual(h["launch"]["character"], CHB)

    def test_a_closed_app_leaves_its_launch(self):
        self.launch("L1", "character_selected", CHA)
        self.assertIs(self.health()["app"], True)
        time.sleep(_bridge.APP_SEEN_SECONDS + 0.5)
        h = self.health()
        self.assertIs(h["app"], False)                   # (the waiter: no signal from what is left)
        self.assertEqual((h["launch"]["id"], h["launch"]["phase"]), ("L1", "character_selected"))
        # the next start: a new id, from app_starting, no character yet
        self.launch("L2", "app_starting")
        h = self.health()
        self.assertIs(h["app"], True)
        self.assertEqual((h["launch"]["id"], h["launch"]["phase"], h["launch"]["character"]), ("L2", "app_starting", None))

    def test_a_bridge_restart_keeps_the_id_not_the_phase(self):
        self.launch("L1", "character_selected", CHA)
        self.b.restart()
        h = self.health()
        self.assertIs(h["app"], False)
        self.assertEqual((h["launch"]["id"], h["launch"]["phase"]), ("L1", "bridge_started"))
        self.launch("L1", "character_selected", CHA)     # (the app reports again)
        h = self.health()
        self.assertEqual((h["launch"]["id"], h["launch"]["phase"]), ("L1", "character_selected"))

    def test_no_launch_without_the_key(self):
        for headers in ({}, {"Authorization": "Bearer not-the-key"}):
            code, doc = self.b.call("GET", "/health", role=None, headers=headers)
            self.assertEqual(code, 200)
            self.assertEqual(doc, {"ok": True, "auth": "required"})


if __name__ == "__main__":
    unittest.main()
