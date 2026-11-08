"""The contract's version on the wire (#70 adopted, 5): /health names the bridge's, a package may carry the one it was written
to (a version string, else 400). No game, no outside network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import Bridge, package  # noqa: E402
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import bridge  # noqa: E402


class ContractFields(unittest.TestCase):
    def setUp(self):
        self.b = Bridge(18876, test=True)

    def tearDown(self):
        self.b.close()

    def test_health_names_the_contract(self):
        code, doc = self.b.call("GET", "/health", role="muse")
        self.assertEqual((code, doc["contract"]), (200, bridge.CONTRACT))

    def test_a_packages_contract_is_a_version(self):
        p = package("版つき。", 1)
        p["contract"] = "1.38"
        self.assertEqual(self.b.call("POST", "/api/packages", p)[0], 200)
        for bad in ("abc", "1", "1.38.2", 1.38, True):
            q = package("誤った版。", 2)
            q["contract"] = bad
            code, doc = self.b.call("POST", "/api/packages", q)
            self.assertEqual(code, 400, bad)


if __name__ == "__main__":
    unittest.main()
