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

    # (GPT's re-review of #76) the install before not finished - its journal there but locked or broken, its leftovers not
    # removable - stops the next one with nothing touched; real Windows locks: a byte-range lock on the journal (no reading),
    # a file held open (no removal)
    def swapping_state(self) -> tuple[dict[str, bytes], list[str]]:
        old = self.put_old()
        dest = self.plugins / "MuseStudio"
        new = self.new_set()
        names = list(new)
        install_dll._journal_write(dest, {"phase": "swapping", "names": names, "had": {n: True for n in names}})
        (dest / names[0]).replace(dest / (names[0] + ".bak"))
        (dest / names[0]).write_bytes(new[names[0]])
        return old, names

    def second_release(self) -> Path:
        built = self.tmp / "built_b"
        built.mkdir()
        files = {}
        for n, body in (("MuseStudio.dll", b"MZ plugin B"), ("MuseMaker.dll", b"MZ maker B"), ("MuseStudio.ico", b"icon B")):
            (built / n).write_bytes(body)
            files[n] = built / n
        man = dict(self.man)
        man["files"] = {n: {"sha256": make_release.sha256(f), "size": f.stat().st_size} for n, f in files.items()}
        return make_release.pack(self.tmp / "out_b", "v0.1.0-0123456", files, man)

    @unittest.skipUnless(sys.platform == "win32", "a Windows lock")
    def test_a_locked_journal_is_not_taken_for_none(self):
        import msvcrt
        old, names = self.swapping_state()
        dest = self.plugins / "MuseStudio"
        journal = dest / install_dll.JOURNAL
        size = journal.stat().st_size
        f = open(journal, "rb+")
        try:
            msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, size)
            why = install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT)
            self.assertIn("not finished", why)
            self.assertTrue((dest / (names[0] + ".bak")).exists())          # (the backup kept)
            self.assertEqual((dest / (names[0] + ".bak")).read_bytes(), old[names[0]])
            self.assertFalse(any(p.name.endswith(".new") for p in dest.iterdir()))
            f.seek(0)
            msvcrt.locking(f.fileno(), msvcrt.LK_UNLCK, size)
        finally:
            f.close()
        self.assertIn("old set put back", install_dll.recover(dest))       # (unlocked: the old set back as a whole)
        self.assert_old(old)

    def test_a_broken_journal_stops_the_install(self):
        old, names = self.swapping_state()
        dest = self.plugins / "MuseStudio"
        for broken in ('{"phase": "swapp', '{"phase": "other", "names": []}', '["swapping"]'):
            (dest / install_dll.JOURNAL).write_text(broken, encoding="utf-8")
            why = install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT)
            self.assertIn("not finished", why, broken)
            self.assertEqual((dest / (names[0] + ".bak")).read_bytes(), old[names[0]], broken)
            self.assertEqual((dest / install_dll.JOURNAL).read_text(encoding="utf-8"), broken)
            for n in names[1:]:
                self.assertEqual((dest / n).read_bytes(), old[n], broken)

    @unittest.skipUnless(sys.platform == "win32", "a Windows lock")
    def test_leftovers_that_cannot_be_removed_stop_the_next_install(self):
        self.put_old()
        dest = self.plugins / "MuseStudio"
        real_remove = install_dll._remove

        def no_maker_bak(path):
            if path.name == "MuseMaker.dll.bak":
                raise OSError("denied")
            real_remove(path)
        install_dll._remove = no_maker_bak
        try:
            self.assertEqual(install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT), "")     # (set A, a .bak left)
        finally:
            install_dll._remove = real_remove
        set_a = self.new_set()
        self.assertTrue((dest / "MuseMaker.dll.bak").exists())
        zip_b = self.second_release()
        held = open(dest / "MuseMaker.dll.bak", "rb")                        # (held open: no removal, no rename)
        try:
            why = install_dll.install(zip_b, plugins=self.plugins, commit=COMMIT)
            self.assertIn("not finished", why)
            for n, b in set_a.items():
                self.assertEqual((dest / n).read_bytes(), b, n)                 # (set A whole: no swap begun)
            self.assertEqual(json.loads((dest / install_dll.JOURNAL).read_text(encoding="utf-8"))["phase"], "done")
        finally:
            held.close()
        self.assertEqual(install_dll.install(zip_b, plugins=self.plugins, commit=COMMIT), "")             # (unlocked: B whole)
        man_b, data_b = install_dll.read_release(zip_b)
        for n, b in data_b.items():
            self.assertEqual((dest / n).read_bytes(), b, n)
        self.assertFalse(any(p.name.endswith((".bak", ".new")) or p.name.startswith(install_dll.JOURNAL) for p in dest.iterdir()))

    @unittest.skipUnless(sys.platform == "win32", "a Windows lock")
    def test_an_older_tools_leftover_held_stops_the_install(self):
        old = self.put_old()
        dest = self.plugins / "MuseStudio"
        (dest / "MuseMaker.dll.bak").write_bytes(b"older maker")
        held = open(dest / "MuseMaker.dll.bak", "rb")
        try:
            self.assertIn("not finished", install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT))
            for n, b in old.items():
                self.assertEqual((dest / n).read_bytes(), b, n)
        finally:
            held.close()
        self.assertEqual(install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT), "")
        self.assert_set(self.new_set())

    # (GPT's post-review of #81) a journal that names no set of the plugin's files - empty, an unknown name, one twice, its
    # had not matching - is refused with nothing touched: not taken for put back, its journal and backups kept
    def test_a_journal_naming_no_set_is_refused(self):
        dest = self.plugins / "MuseStudio"
        for bad_names, bad_had in (([], {}), (["not-a-plugin-file"], {"not-a-plugin-file": True}),
                                   (["MuseStudio.dll", "MuseStudio.dll"], {"MuseStudio.dll": True}),
                                   (list(install_dll.WANT), {"MuseStudio.dll": True}),
                                   (list(install_dll.WANT), {n: "yes" for n in install_dll.WANT})):
            if dest.exists():
                import shutil
                shutil.rmtree(dest)
            old, names = self.swapping_state()
            install_dll._journal_write(dest, {"phase": "swapping", "names": bad_names, "had": bad_had})
            journal = (dest / install_dll.JOURNAL).read_text(encoding="utf-8")
            with self.assertRaises(install_dll.JournalUnreadable):
                install_dll.recover(dest)
            self.assertIn("not finished", install_dll.install(self.zip, plugins=self.plugins, commit=COMMIT))
            self.assertEqual((dest / install_dll.JOURNAL).read_text(encoding="utf-8"), journal, bad_names)
            self.assertEqual((dest / (names[0] + ".bak")).read_bytes(), old[names[0]], bad_names)     # (the backup kept)
            for n in names[1:]:
                self.assertEqual((dest / n).read_bytes(), old[n], bad_names)

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
