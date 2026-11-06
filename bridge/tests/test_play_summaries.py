"""The play summary table (MUSE_GUIDE_plays.md, 2026-11-06): kept apart from the catalog (the lookup), so a play added or
removed may be left out of one of them. This fails when the table and the plays MuseStudio has drift: an id with no row, a
row for no play, a name that is not the play's, an empty summary. The plays: the original's rows
(MuseStudio/tools/base/plays.json) and the AnimationLoader ones (MuseStudio/tools/al_plays.py's HOUSHI / SONYU tables).
No game, no network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TABLE = ROOT / "MUSE_GUIDE_plays.md"
BASE = ROOT / "MuseStudio" / "tools" / "base" / "plays.json"
AL = ROOT / "MuseStudio" / "tools" / "al_plays.py"


def plays() -> dict[str, str]:
    out = {}
    for r in json.loads(BASE.read_text(encoding="utf-8"))["plays"]:
        out[r["id"]] = r["name"]
    src = AL.read_text(encoding="utf-8")
    for table, mode, pattern in (("HOUSHI", "houshi", r'(\d+):\s*\("([^"]+)"'), ("SONYU", "sonyu", r'(\d+):\s*"([^"]+)"')):
        m = re.search(table + r"\s*=\s*\{(.*?)\n\}", src, re.S)
        assert m, f"al_plays.py has no {table} table"
        for num, name in re.findall(pattern, m.group(1)):
            out[f"al_{mode}_{num}"] = name
    return out


def rows() -> dict[str, tuple[str, str]]:
    out = {}
    for line in TABLE.read_text(encoding="utf-8").splitlines():
        m = re.match(r"^\|\s*`([a-z_0-9]+)`\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|\s*$", line)
        if m:
            assert m.group(1) not in out, "the table has " + m.group(1) + " twice"
            out[m.group(1)] = (m.group(2), m.group(3))
    return out


class PlaySummaries(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.plays = plays()
        cls.rows = rows()

    def test_every_play_has_a_row(self):
        self.assertGreater(len(self.plays), 100)
        self.assertEqual(sorted(set(self.plays) - set(self.rows)), [], "plays with no summary row")

    def test_every_row_is_a_play(self):
        self.assertEqual(sorted(set(self.rows) - set(self.plays)), [], "rows for no play")

    def test_names_and_summaries(self):
        for pid, (name, summary) in self.rows.items():
            with self.subTest(play=pid):
                self.assertEqual(name, self.plays.get(pid, name).replace("|", "｜"), "the name is not the play's")
                self.assertGreater(len(summary), 10, "the summary is empty")

    def test_the_contract_points_at_the_table(self):
        self.assertIn("MUSE_GUIDE_plays.md", (ROOT / "MUSE_GUIDE.md").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
