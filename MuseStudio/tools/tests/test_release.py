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
        real = install_dll._replace

        def failing(src, dst):
            if src.name == "MuseMaker.dll":            # (the second file's old one aside: refused, as a file in use)
                raise OSError("in use")
            real(src, dst)
        install_dll._replace = failing
        try:
            why = install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT)
        finally:
            install_dll._replace = real
        self.assertIn("in use", why)
        self.assertIn("back as they were", why)
        self.assert_old(old)

    # (GPT's re-review of #75) the journal: an install cut off is finished as a whole - the old set back while it was
    # swapping, the new set kept once it was done; a .bak not removed never put back alone
    def new_set(self) -> dict[str, bytes]:
        man, data = install_dll.read_release(self.zip)
        data[install_dll.MANIFEST] = (__import__("json").dumps(man, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
        return data

    def assert_set(self, want: dict[str, bytes]):
        dest = self.plugins / "MuseStudio"
        for n, b in want.items():
            self.assertEqual((dest / n).read_bytes(), b, n)
        self.assertEqual(sorted(p.name for p in dest.iterdir()), sorted(want))     # (no .new, .bak, journal left)

    def test_cut_off_while_swapping_puts_the_old_set_back(self):
        old = self.put_old()
        dest = self.plugins / "MuseStudio"
        new = self.new_set()
        names = list(new)
        install_dll._journal_write(dest, {"phase": "swapping", "names": names, "had": {n: True for n in names}})
        (dest / names[0]).replace(dest / (names[0] + ".bak"))
        (dest / names[0]).write_bytes(new[names[0]])
        (dest / (names[1] + ".new")).write_bytes(new[names[1]])
        self.assertIn("old set put back", install_dll.recover(dest))
        self.assert_old(old)

    def test_cut_off_while_swapping_a_first_install_takes_the_new_out(self):
        dest = self.plugins / "MuseStudio"
        dest.mkdir(parents=True)
        new = self.new_set()
        names = list(new)
        install_dll._journal_write(dest, {"phase": "swapping", "names": names, "had": {n: False for n in names}})
        (dest / names[0]).write_bytes(new[names[0]])
        install_dll.recover(dest)
        self.assertEqual(list(dest.iterdir()), [])

    def test_cut_off_while_tidying_keeps_the_new_set(self):
        old = self.put_old()
        dest = self.plugins / "MuseStudio"
        new = self.new_set()
        names = list(new)
        for n in names:
            (dest / n).replace(dest / (n + ".bak"))
            (dest / n).write_bytes(new[n])
        install_dll._journal_write(dest, {"phase": "done", "names": names, "had": {n: True for n in names}})
        (dest / (names[0] + ".bak")).unlink()                 # (the first leftover removed, then cut off)
        self.assertIn("leftovers", install_dll.recover(dest))
        self.assert_set(new)
        self.assertNotEqual(new[names[1]], old[names[1]])

    def test_a_backup_not_removed_is_never_put_back_alone(self):
        self.put_old()
        real_remove, real_write = install_dll._remove, install_dll._write

        def no_maker_bak(path):
            if path.name == "MuseMaker.dll.bak":
                raise OSError("denied")
            real_remove(path)
        install_dll._remove = no_maker_bak
        try:
            self.assertEqual(install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT), "")
        finally:
            install_dll._remove = real_remove
        new = self.new_set()
        calls = []

        def failing(path, data):
            calls.append(path.name)
            if len(calls) == 2:
                raise OSError("disk full")
            real_write(path, data)
        install_dll._write = failing
        try:
            why = install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT)
        finally:
            install_dll._write = real_write
        self.assertIn("disk full", why)
        self.assert_set(new)                                  # (the new set whole: the Maker's .bak not put back)

    def test_an_older_tools_leftovers_are_not_put_back_alone(self):
        self.put_old()
        dest = self.plugins / "MuseStudio"
        (dest / "MuseMaker.dll.bak").write_bytes(b"older maker")
        note = install_dll.recover(dest)
        self.assertIn("older tool", note)
        self.assertEqual((dest / "MuseMaker.dll").read_bytes(), b"old MuseMaker.dll")
        self.assertEqual(install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT), "")
        self.assert_set(self.new_set())

    def test_a_put_back_that_fails_is_not_called_done(self):
        old = self.put_old()
        dest = self.plugins / "MuseStudio"
        real = install_dll._replace

        def failing(src, dst):
            if src.name == "MuseMaker.dll" or src.name.endswith(".bak"):
                raise OSError("in use")
            real(src, dst)
        install_dll._replace = failing
        try:
            why = install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT)
        finally:
            install_dll._replace = real
        self.assertIn("could not put all of the old ones back", why)
        self.assertNotIn("back as they were", why)
        self.assertTrue((dest / install_dll.JOURNAL).exists())
        self.assertIn("old set put back", install_dll.recover(dest))       # (the next run: back as a whole)
        self.assert_old(old)

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
