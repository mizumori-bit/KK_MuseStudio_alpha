"""The built plugin on Releases (tools/make_release.py, tools/install_dll.py): the zip as packed is taken - only the plugin's
folder, each file's SHA-256 the manifest's, the source commit this clone's - and a zip changed, a file added or another
commit's is refused, nothing written; the check for this PC's paths finds them as ASCII and as UTF-16. No game, no network.

  python -B -m unittest discover -s MuseStudio/tools/tests
"""
from __future__ import annotations

import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import install_dll  # noqa: E402
import make_release  # noqa: E402

COMMIT = "0123456789abcdef0123456789abcdef01234567"


class Release(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="release_test_"))
        built = self.tmp / "built"
        built.mkdir()
        self.files = {}
        for n, body in (("MuseStudio.dll", b"MZ plugin"), ("MuseMaker.dll", b"MZ maker"), ("MuseStudio.ico", b"icon")):
            (built / n).write_bytes(body)
            self.files[n] = built / n
        self.man = {"plugin_version": "0.1.0", "contract": "1.34", "source_commit": COMMIT,
                    "files": {n: {"sha256": make_release.sha256(p), "size": p.stat().st_size} for n, p in self.files.items()}}
        self.zip = make_release.pack(self.tmp / "out", "v0.1.0-0123456", self.files, self.man)
        self.plugins = self.tmp / "game" / "BepInEx" / "plugins"
        self.plugins.mkdir(parents=True)

    def rezip(self, change: dict[str, bytes], extra: dict[str, bytes] | None = None) -> Path:
        out = self.tmp / "changed.zip"
        with zipfile.ZipFile(self.zip) as src, zipfile.ZipFile(out, "w") as dst:
            for n in src.namelist():
                short = n[len(make_release.IN_ZIP):]
                dst.writestr(n, change.get(short, src.read(n)))
            for n, b in (extra or {}).items():
                dst.writestr(n, b)
        return out

    def test_the_packed_zip_goes_in(self):
        self.assertTrue((self.tmp / "out" / (self.zip.name + ".sha256")).is_file())
        self.assertEqual(install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT), "")
        dest = self.plugins / "MuseStudio"
        for n, p in self.files.items():
            self.assertEqual((dest / n).read_bytes(), p.read_bytes())
        self.assertEqual(json.loads((dest / install_dll.MANIFEST).read_text(encoding="utf-8"))["source_commit"], COMMIT)

    def test_another_commits_dll_is_refused_unless_asked(self):
        why = install_dll.install(self.zip, plugins=self.plugins, commit="f" * 40)
        self.assertIn("0123456", why)
        self.assertFalse((self.plugins / "MuseStudio").exists())
        self.assertEqual(install_dll.install(self.zip, any_source=True, plugins=self.plugins, commit="f" * 40), "")

    def test_a_changed_file_is_refused(self):
        bad = self.rezip({"MuseStudio.dll": b"MZ something else"})
        self.assertIn("MuseStudio.dll", install_dll.install(bad, plugins=self.plugins, commit=COMMIT))
        self.assertFalse((self.plugins / "MuseStudio").exists())

    def test_a_file_not_the_plugins_is_refused(self):
        bad = self.rezip({}, {"BepInEx/plugins/Other/evil.dll": b"MZ"})
        self.assertIn("evil.dll", install_dll.install(bad, plugins=self.plugins, commit=COMMIT))
        self.assertFalse((self.plugins / "MuseStudio").exists())

    def test_no_launch_folder(self):
        self.assertIn("setup.py", install_dll.install(self.zip, plugins=self.tmp / "nowhere", commit=COMMIT))

    def test_this_pcs_paths_are_found_in_either_encoding(self):
        marks = ["E:\\Some\\Repo", "someone"]
        self.assertEqual(make_release.find_private(b"..e:/some/repo/x..", marks), ["E:\\Some\\Repo"])
        self.assertEqual(make_release.find_private("C:\\Users\\Someone\\x".encode("utf-16-le"), marks), ["someone"])
        self.assertEqual(make_release.find_private(b"MZ nothing here", marks), [])


if __name__ == "__main__":
    unittest.main()
