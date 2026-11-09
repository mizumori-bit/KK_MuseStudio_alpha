"""The contract's scene quick sheets (MUSE_GUIDE_quick/, PR #12 adopted): each names the contract's version and the sections it
points at. They are changed in the same change as the contract; this fails when they drift - another version, a section
the contract no longer has. No game, no network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
GUIDE = ROOT / "MUSE_GUIDE.md"
QUICK = ROOT / "MUSE_GUIDE_quick"
SCENES = ("talk", "play", "outing", "match", "kensei")


def guide_version(text: str) -> str:
    m = re.search(r"\*\*契約の版：(\d+\.\d+)", text)
    assert m, "the contract names no version"
    return m.group(1)


def guide_sections(text: str) -> set[str]:
    found = set()
    for line in text.splitlines():
        m = re.match(r"^#{2,4} (\d+(?:\.\d+)*)[ .]", line)
        if m:
            found.add(m.group(1))
    return found


class Versions(unittest.TestCase):
    """(#70 adopted, 5) the bridge and the app name the contract's version they speak: the same as the contract's"""

    def test_the_bridge_and_the_app_speak_the_contracts_version(self):
        import sys
        sys.path.insert(0, str(ROOT / "bridge"))
        import bridge  # noqa: E402
        version = guide_version(GUIDE.read_text(encoding="utf-8"))
        self.assertEqual(bridge.CONTRACT, version)
        m = re.search(r'public const string Contract = "(\d+\.\d+)";', (ROOT / "MuseStudio" / "src" / "Plugin.cs").read_text(encoding="utf-8"))
        self.assertTrue(m)
        self.assertEqual(m.group(1), version)


class QuickSheets(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = GUIDE.read_text(encoding="utf-8")
        cls.version = guide_version(cls.text)
        cls.sections = guide_sections(cls.text)

    def test_every_scene_has_a_sheet(self):
        for scene in SCENES:
            self.assertTrue((QUICK / f"{scene}.md").is_file(), scene)
        self.assertIn("MUSE_GUIDE_quick/", self.text)

    def test_each_sheet_names_the_contracts_version(self):
        for scene in SCENES:
            sheet = (QUICK / f"{scene}.md").read_text(encoding="utf-8")
            m = re.search(r"契約の版：(\d+\.\d+)", sheet)
            with self.subTest(scene=scene):
                self.assertIsNotNone(m, "no version")
                self.assertEqual(m.group(1), self.version, "the sheet is for another version of the contract - change it with the contract")

    def test_each_section_a_sheet_points_at_is_in_the_contract(self):
        for scene in SCENES:
            sheet = (QUICK / f"{scene}.md").read_text(encoding="utf-8")
            refs = set(re.findall(r"第 (\d+(?:\.\d+)*) 節", sheet)) | set(re.findall(r"第 (\d+) 章", sheet))
            for ref in re.findall(r"第 ([\d.・〜]+) (?:節|章)", sheet):
                for part in re.split(r"[・〜]", ref):
                    if part:
                        refs.add(part)
            with self.subTest(scene=scene):
                missing = sorted(r for r in refs if r not in self.sections)
                self.assertEqual(missing, [], "sections the contract has no heading for")


if __name__ == "__main__":
    unittest.main()
