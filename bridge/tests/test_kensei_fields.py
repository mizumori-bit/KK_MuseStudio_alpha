"""献精室 (MUSE_GUIDE 3.15, contract 1.41): a block's move and came, and a package's kensei_answers pass the bridge's
structural check when they have their shape (else 400); a package of the answers alone has no blocks and is taken (the app
writes them down, nothing is shown); the state's kensei reaches Muse. The app judges the rest (the run, the turn, the play).
No game, no outside network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import Bridge, package  # noqa: E402

APP = {"X-MMS-Instance": "win-1", "X-MMS-Started": "1000"}


def move(**over):
    mv = {"run": "k20261109120000-abcd", "turn": 1, "request": "t1", "play": "houshi_01", "pace": 2}
    mv.update(over)
    return mv


class KenseiFields(unittest.TestCase):
    def setUp(self):
        self.b = Bridge(18966, test=True)

    def tearDown(self):
        self.b.close()

    def post(self, p):
        return self.b.call("POST", "/api/packages", p)[0]

    def test_a_move_and_its_came_lines_are_taken(self):
        p = package("手でしてあげる。", 1)
        p["blocks"][0]["move"] = move()
        p["blocks"].append({"type": "dialogue", "text": "いっぱい出たね。", "came": True})
        p["blocks"].append({"type": "dialogue", "text": "どう？"})
        self.assertEqual(self.post(p), 200)
        q = package("ペースはおまかせ。", 2)
        q["blocks"][0]["move"] = move(turn=2, request="t2", pace=None)
        self.assertEqual(self.post(q), 200)

    def test_a_broken_move_is_refused(self):
        bad = [move(run=""), move(run=3), move(turn=0), move(turn=1.5), move(turn=True), move(request=""), move(request="x" * 201),
               move(play=None), move(pace=0), move(pace=4), move(pace="2"), "houshi_01"]
        for i, mv in enumerate(bad):
            p = package("誤り。", 10 + i)
            p["blocks"][0]["move"] = mv
            self.assertEqual(self.post(p), 400, mv)
        p = package("誤り。", 40)
        p["blocks"][0]["came"] = "yes"
        self.assertEqual(self.post(p), 400)

    def test_the_answers_alone(self):
        a = {"session": "t", "turn": 50, "kensei_answers": [{"run": "k1", "turn": 3, "tap": "かなり", "text": "脚がすごく効いた"}]}
        self.assertEqual(self.post(a), 200)
        b = {"session": "t", "turn": 51, "blocks": [], "kensei_answers": [{"run": "k1", "turn": 4, "tap": None, "text": "あまり"}]}
        self.assertEqual(self.post(b), 200)
        self.assertEqual(self.post({"session": "t", "turn": 52}), 400)                     # (no blocks, no answers)
        self.assertEqual(self.post({"session": "t", "turn": 53, "blocks": []}), 400)
        for i, ans in enumerate([[], "x", [{"run": "k1", "turn": 0, "tap": "a"}], [{"run": "", "turn": 1, "tap": "a"}],
                                 [{"run": "k1", "turn": 1}], [{"run": "k1", "turn": 1, "tap": "x" * 51}], [{"run": "k1", "turn": 1, "text": 3}]]):
            self.assertEqual(self.post({"session": "t", "turn": 60 + i, "kensei_answers": ans}), 400, ans)
        # (with an ordinary package too)
        p = package("次はこれ。", 70)
        p["kensei_answers"] = [{"run": "k1", "turn": 5, "tap": "普通", "text": None}]
        self.assertEqual(self.post(p), 200)

    def test_the_state_carries_kensei(self):
        st = {"app_name": "MuseStudio", "kensei": {"run": "k1", "phase": "running", "turn": 3, "turns_max": 18}}
        self.assertEqual(self.b.call("POST", "/api/state", st, role="app", headers=APP)[0], 200)
        code, doc = self.b.call("GET", "/api/state", role="muse")
        self.assertEqual(code, 200)
        self.assertEqual(doc["kensei"]["turns_max"], 18)


if __name__ == "__main__":
    unittest.main()
