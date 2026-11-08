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

    # (GPT's post-review of #74) an install already there: a write or a move failing half-way leaves it whole
    def put_old(self) -> dict[str, bytes]:
        dest = self.plugins / "MuseStudio"
        dest.mkdir(parents=True)
        old = {n: b"old " + n.encode() for n in install_dll.WANT}
        for n, b in old.items():
            (dest / n).write_bytes(b)
        return old

    def assert_old(self, old: dict[str, bytes]):
        dest = self.plugins / "MuseStudio"
        for n, b in old.items():
            self.assertEqual((dest / n).read_bytes(), b, n)
        self.assertEqual(sorted(p.name for p in dest.iterdir()), sorted(old))      # (no .new, no .bak left)

    def test_a_write_failing_half_way_leaves_the_old_set(self):
        old = self.put_old()
        real, calls = install_dll._write, []

        def failing(path, data):
            calls.append(path.name)
            if len(calls) == 2:
                raise OSError("disk full")
            real(path, data)
        install_dll._write = failing
        try:
            why = install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT)
        finally:
            install_dll._write = real
        self.assertIn("disk full", why)
        self.assert_old(old)

    def test_a_move_failing_half_way_puts_the_old_set_back(self):
        old = self.put_old()
        real, calls = install_dll._replace, []

        def failing(src, dst):
            calls.append(src.name)
            if len(calls) == 3:                 # (the second file's old one aside: refused, as a file in use)
                raise OSError("in use")
            real(src, dst)
        install_dll._replace = failing
        try:
            why = install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT)
        finally:
            install_dll._replace = real
        self.assertIn("in use", why)
        self.assert_old(old)

    def test_an_install_cut_off_before_is_put_back_then_done(self):
        old = self.put_old()
        dest = self.plugins / "MuseStudio"
        # (cut off mid-way: the first file swapped - its old one aside as .bak, the new in - the second's .new written)
        (dest / "MuseStudio.dll.bak").write_bytes(old["MuseStudio.dll"])
        (dest / "MuseStudio.dll").write_bytes(b"half new")
        (dest / "MuseMaker.dll.new").write_bytes(b"half new")
        self.assertEqual(install_dll.recover(dest), ["MuseStudio.dll"])
        self.assert_old(old)
        self.assertEqual(install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT), "")
        for n, p in self.files.items():
            self.assertEqual((dest / n).read_bytes(), p.read_bytes())

    # (GPT's post-review of #74) only what this build made is packed: one not made now (an older one left there) refuses
    def fake_build(self, out: Path, made: tuple[str, ...]) -> list[str]:
        code = "import sys, pathlib; [(pathlib.Path(sys.argv[1]) / n).write_bytes(b'new ' + n.encode()) for n in sys.argv[2:]]"
        return [sys.executable, "-c", code, str(out), *made]

    def test_a_dll_not_built_now_is_not_packed(self):
        out = self.tmp / "build_out"
        out.mkdir()
        (out / "MuseMaker.dll").write_bytes(b"old maker from before")
        why = make_release.build_fresh(out, self.fake_build(out, ("MuseStudio.dll", "MuseStudio.ico")))
        self.assertIn("MuseMaker.dll", why)
        self.assertFalse((out / "MuseMaker.dll").exists())
        self.assertEqual(make_release.build_fresh(out, self.fake_build(out, make_release.FILES)), "")
        self.assertEqual((out / "MuseMaker.dll").read_bytes(), b"new MuseMaker.dll")

    def test_this_pcs_paths_are_found_in_either_encoding(self):
        marks = ["E:\\Some\\Repo", "someone"]
        self.assertEqual(make_release.find_private(b"..e:/some/repo/x..", marks), ["E:\\Some\\Repo"])
        self.assertEqual(make_release.find_private("C:\\Users\\Someone\\x".encode("utf-16-le"), marks), ["someone"])
        self.assertEqual(make_release.find_private(b"MZ nothing here", marks), [])


if __name__ == "__main__":
    unittest.main()
