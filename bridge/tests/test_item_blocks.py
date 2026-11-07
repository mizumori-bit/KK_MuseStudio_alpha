"""A block's item (MUSE_GUIDE 3.11, contract 1.33; PR #46 §7): one operation with a request id - the bridge refuses a package
whose item is not of that shape (400), so a mistyped operation never reaches the app half-understood."""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import Bridge  # noqa: E402


def pkg(item) -> dict:
    return {"session": "s-item", "turn": 1, "blocks": [{"type": "dialogue", "text": "どうぞ", "item": item}]}


class ItemBlocks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.b = Bridge(18960)

    @classmethod
    def tearDownClass(cls):
        cls.b.close()

    def test_good_shapes_are_taken(self):
        for i, item in enumerate(({"request": "r1", "use": "beer"}, {"request": "r2", "use": "beer", "on": "user", "from": "shelf"},
                                  {"request": "r3", "accept": "o1-abc"}, {"request": "r4", "decline": "o1-abc"}, {"request": "r5", "suggestion": "s2"},
                                  {"request": "r6", "give": "chocolate"})):
            with self.subTest(item=item):
                p = pkg(item)
                p["turn"] = 10 + i
                code, ans = self.b.call("POST", "/api/packages", p, role="muse")
                self.assertEqual(code, 200, ans)

    def test_bad_shapes_are_refused(self):
        for item in ("beer", {"use": "beer"}, {"request": "r", "use": "beer", "give": "beer"}, {"request": "", "use": "beer"},
                     {"request": "r", "use": ""}, {"request": "r", "use": 3}, {"request": "r", "use": "beer", "on": "her"},
                     {"request": "r", "use": "beer", "from": "user"}, {"request": "r"}):
            with self.subTest(item=item):
                code, ans = self.b.call("POST", "/api/packages", pkg(item), role="muse")
                self.assertEqual(code, 400, ans)
                self.assertIn("item", str(ans))


if __name__ == "__main__":
    unittest.main()
