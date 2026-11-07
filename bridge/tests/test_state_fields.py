"""The state's fields (MUSE_GUIDE 7.4): the bridge keeps only the fields it knows of the app's state - every one the contract
lists reaches Muse's GET /api/state (settings of 1.14 and play_serial of 1.12 were dropped since their versions; items, of 1.33, let through
ahead of it). Read from the contract's table, so a field added to it without the bridge fails here."""
from __future__ import annotations

import re
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import ROOT, Bridge  # noqa: E402

BRIDGE_OWN = {"app", "queue", "events_pending"}         # (the bridge's own: not the app's)


def contract_fields() -> list[str]:
    text = (ROOT / "MUSE_GUIDE.md").read_text(encoding="utf-8")
    m = re.search(r"^### 7\.4 .*?$(.*?)^### 7\.5 ", text, re.S | re.M)
    assert m, "the contract has no section 7.4"
    return [f for f in re.findall(r"^\| `([a-z_]+)` \|", m.group(1), re.M) if f not in BRIDGE_OWN]


class StateFields(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.b = Bridge(18962)

    @classmethod
    def tearDownClass(cls):
        cls.b.close()

    def test_the_bridge_knows_every_field_of_the_contract(self):
        fields = contract_fields()
        self.assertGreater(len(fields), 20)
        src = (ROOT / "bridge" / "bridge.py").read_text(encoding="utf-8")
        known = set(re.findall(r'"([a-z_]+)"', re.search(r"^STATE_FIELDS = \((.*?)\)", src, re.S | re.M).group(1)))
        self.assertEqual([f for f in fields if f not in known], [])

    def test_every_field_reaches_muse(self):
        body = {f: {"field": f} for f in contract_fields()}
        body["play_serial"] = 7
        body["no_such_field"] = 1
        self.assertEqual(self.b.call("POST", "/api/state", body, role="app")[0], 200)
        code, got = self.b.call("GET", "/api/state", role="muse")
        self.assertEqual(code, 200)
        for f, v in body.items():
            if f != "no_such_field":
                with self.subTest(field=f):
                    self.assertEqual(got.get(f), v)
        self.assertNotIn("no_such_field", got)
        for f in BRIDGE_OWN:
            self.assertIn(f, got)


if __name__ == "__main__":
    unittest.main()
