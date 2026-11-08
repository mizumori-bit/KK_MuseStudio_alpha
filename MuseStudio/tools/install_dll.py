"""Puts the built plugin from a GitHub release (tools/make_release.py) into the launch folder - no .NET SDK needed (the owner's
decision of 2026-11-07: the DLLs on Releases, the source public as ever). Instead of tools/build.py:

  python -B MuseStudio/tools/install_dll.py MuseStudio-dll-<tag>.zip
  ... --any-source        (a DLL built from another commit than this clone's: put in anyway - not advised)

It checks, before anything is written:
- the zip has the plugin's folder (BepInEx/plugins/MuseStudio/: MuseStudio.dll, MuseMaker.dll, MuseStudio.ico and the
  manifest musestudio_dll.json), and nothing else;
- each file's SHA-256 is the manifest's;
- the manifest's source commit is this clone's (git rev-parse HEAD): the DLLs read this clone's MuseStudio/data and speak
  its contract, so a DLL of another commit may not fit them;
- tools/setup.py has made the launch folder (game/BepInEx/plugins).
Then the files go into game/BepInEx/plugins/MuseStudio/ as one set (GPT's post-review of #74: a write failing half-way left
the new and the old mixed): each new file written beside its place first (<name>.new) and read back; a journal says
"swapping"; each old one put aside (<name>.bak) and the new one moved in; the journal says "done"; the leftovers removed. A
failure on the way puts the old set back as a whole - the install refused with why, saying so only when it is so. An install
cut off before is finished by its journal first (GPT's re-review of #75): "swapping" - the old set back as a whole; "done" -
only the leftovers removed, the new set kept. A journal there but unreadable, or leftovers that cannot be removed, stop the
install with nothing touched (GPT's re-review of #76): a new swap begins only from a folder with no .bak, .new or journal
left. The folder's other files are left as they are. Nothing is downloaded: the zip is the one you took from the release
page. Standard library only.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
REPO = ROOT.parent
PLUGINS = ROOT / "game" / "BepInEx" / "plugins"
IN_ZIP = "BepInEx/plugins/MuseStudio/"
MANIFEST = "musestudio_dll.json"
WANT = ("MuseStudio.dll", "MuseMaker.dll", "MuseStudio.ico", MANIFEST)


def head_commit() -> str | None:
    try:
        return subprocess.run(["git", "rev-parse", "HEAD"], cwd=str(REPO), capture_output=True, text=True, check=True).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return None


def read_release(zip_path: Path) -> tuple[dict, dict[str, bytes]]:
    """the zip's manifest and files, checked: only the plugin's folder, every file there, each SHA-256 the manifest's"""
    with zipfile.ZipFile(zip_path) as z:
        names = [n for n in z.namelist() if not n.endswith("/")]
        stray = [n for n in names if not n.startswith(IN_ZIP) or n[len(IN_ZIP):] not in WANT]
        if stray:
            raise ValueError("the zip has files that are not the plugin's: " + ", ".join(stray))
        missing = [w for w in WANT if IN_ZIP + w not in names]
        if missing:
            raise ValueError("the zip lacks: " + ", ".join(missing))
        data = {w: z.read(IN_ZIP + w) for w in WANT}
    man = json.loads(data.pop(MANIFEST).decode("utf-8"))
    for n, b in data.items():
        f = (man.get("files") or {}).get(n)
        if not f:
            raise ValueError(f"the manifest does not name {n}")
        if hashlib.sha256(b).hexdigest() != f.get("sha256") or len(b) != f.get("size"):
            raise ValueError(f"{n} is not the manifest's (its SHA-256 or size differs)")
    return man, data


# (the writes, the moves and the removals: the tests put failures in here)
def _write(path: Path, data: bytes) -> None:
    path.write_bytes(data)


def _replace(src: Path, dst: Path) -> None:
    os.replace(src, dst)


def _remove(path: Path) -> None:
    path.unlink()


# The install's journal (GPT's re-review of #75: a .bak left after the swap - its removal failing or cut off - was put back
# alone by the next run, the new and the old mixed): "swapping" before the first file is moved - an install cut off then is
# put back as a whole to the old set; "done" once every new file is in - only the leftovers removed then, the new set kept.
# Each file's being there before is in it (a first install: nothing to put back, the new file taken out).
JOURNAL = "musestudio_install.json"


def _journal_write(dest: Path, doc: dict) -> None:
    tmp = dest / (JOURNAL + ".tmp")
    _write(tmp, (json.dumps(doc) + "\n").encode("utf-8"))
    _replace(tmp, dest / JOURNAL)


class JournalUnreadable(OSError):
    """the install's journal is there but cannot be read, or is not in its form: nothing is touched (GPT's re-review of #76)"""


def _journal_read(dest: Path) -> dict | None:
    """the journal: None only when there is none (GPT's re-review of #76: one there but locked or broken was taken for none -
    an older tool's leftovers - and the backups of the install before it were removed); there but not readable or not in its
    form: JournalUnreadable"""
    path = dest / JOURNAL
    if not path.exists():
        return None
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        raise JournalUnreadable(f"the install's journal is there but cannot be read ({e})") from e
    if (not isinstance(doc, dict) or doc.get("phase") not in ("swapping", "done") or not isinstance(doc.get("names"), list)
            or not isinstance(doc.get("had", {}), dict)):
        raise JournalUnreadable("the install's journal is not in its form")
    return doc


def _restore(dest: Path, names: list[str], had: dict) -> list[str]:
    """the old set back as a whole: each .bak over its file, a new file where there was none taken out, the .new ones gone -
    the names that could not be (empty: all back)"""
    failed = []
    for n in names:
        cur, bak, new = dest / n, dest / (n + ".bak"), dest / (n + ".new")
        try:
            if bak.exists():
                _replace(bak, cur)
            elif not had.get(n, True) and cur.exists():
                _remove(cur)
        except OSError:
            failed.append(n)
        try:
            if new.exists():
                _remove(new)
        except OSError:
            pass
    return failed


def _cleanup(dest: Path, names) -> list[str]:
    """the leftovers of a set put in: the .bak and .new ones, then the journal - the names left (removed next time)"""
    left = []
    for n in names:
        for side in (dest / (n + ".bak"), dest / (n + ".new")):
            try:
                if side.exists():
                    _remove(side)
            except OSError:
                left.append(side.name)
    if not left:
        for j in (dest / JOURNAL, dest / (JOURNAL + ".tmp")):
            try:
                if j.exists():
                    _remove(j)
            except OSError:
                left.append(j.name)
    return left


def recover(dest: Path) -> str:
    """an install before, finished by its journal before another may begin - "" nothing to do, else what was done; OSError
    (nothing more touched, the journal and the backups kept) while it cannot be finished:
    - "swapping": the old set put back as a whole, then its leftovers removed;
    - "done": its leftovers removed, the new set kept;
    - no journal (an older tool's leftovers, or a journal never put in place): those leftovers removed - nothing put back
      alone, the install that follows puts in a whole set;
    - a journal there but unreadable or not in its form: JournalUnreadable.
    Leftovers that cannot be removed stop the next install (GPT's re-review of #76: the next one's swap took over their names
    and its journal, and a .bak of an older generation was put back for it): its swap begins only from a folder with no .bak,
    .new or journal left"""
    j = _journal_read(dest)
    if j is None:
        sides = [dest / (n + s) for n in WANT for s in (".bak", ".new")] + [dest / (JOURNAL + ".tmp")]
        there = [f for f in sides if f.exists()]
        if not there:
            return ""
        failed = []
        for f in there:
            try:
                _remove(f)
            except OSError:
                failed.append(f.name)
        if failed:
            raise OSError("files left by an install cut off with an older tool could not be removed (" + ", ".join(failed) + ")")
        return ("files left by an install cut off with an older tool (" + ", ".join(f.name for f in there) + ") - removed, "
                "not put back alone; this install puts in a whole set")
    names = [n for n in j.get("names", WANT) if n in WANT]
    if j.get("phase") == "done":
        left = _cleanup(dest, names)
        if left:
            raise OSError("the install before is done, but its leftovers could not be removed (" + ", ".join(left) + ")")
        return "an install done before had leftovers - removed"
    failed = _restore(dest, names, j.get("had", {}))
    if failed:
        raise OSError("could not put back " + ", ".join(failed))
    left = _cleanup(dest, names)
    if left:
        raise OSError("the old set is back, but the leftovers could not be removed (" + ", ".join(left) + ")")
    return "an install cut off before - its old set put back"


def put_in_place(dest: Path, files: dict[str, bytes]) -> str:
    """the files into dest as one set: "" done, else why not - and whether the set there before is as it was"""
    names = list(files)
    try:
        for n, b in files.items():
            tmp = dest / (n + ".new")
            _write(tmp, b)
            if tmp.read_bytes() != b:
                raise OSError(f"{n} was not written as it is")
    except OSError as e:
        _cleanup(dest, names)
        return f"could not write the new files ({e}) - the installed ones are as they were"
    had = {n: (dest / n).exists() for n in names}
    try:
        _journal_write(dest, {"phase": "swapping", "names": names, "had": had})
    except OSError as e:
        _cleanup(dest, names)
        return f"could not write the install's journal ({e}) - the installed ones are as they were"
    try:
        for n in names:
            cur = dest / n
            if cur.exists():
                _replace(cur, dest / (n + ".bak"))
            _replace(dest / (n + ".new"), cur)
        _journal_write(dest, {"phase": "done", "names": names, "had": had})
    except OSError as e:
        failed = _restore(dest, names, had)
        if failed:
            return (f"could not put the new files in place ({e}; is the game running?), and could not put all of the old ones back "
                    f"({', '.join(failed)}) - run the install again to put the old set back (its journal is kept: {dest / JOURNAL})")
        _cleanup(dest, names)
        return f"could not put the new files in place ({e}; is the game running?) - the installed ones are back as they were"
    left = _cleanup(dest, names)
    if left:
        print("install: done; leftovers not removed now (removed by the next run): " + ", ".join(left))
    return ""


def install(zip_path: Path, any_source: bool = False, plugins: Path = PLUGINS, commit: str | None = None) -> str:
    """put the release's plugin in place: "" done, else why not (the set there before left as it was)"""
    try:
        man, data = read_release(zip_path)
    except (OSError, ValueError, zipfile.BadZipFile, json.JSONDecodeError) as e:
        return f"not a release zip of the plugin: {e}"
    src = man.get("source_commit", "")
    here = commit if commit is not None else head_commit()
    if not any_source and (not here or not src or src != here):
        return (f"the DLLs were built from {src[:7] or '?'}, this clone is at {(here or '?')[:7]} - check out {src[:7]} "
                f"(git checkout {src[:7]}) or take the release of this commit (--any-source puts it in anyway; not advised)")
    if not plugins.is_dir():
        return "no launch folder - run python -B MuseStudio/tools/setup.py first"
    dest = plugins / "MuseStudio"
    dest.mkdir(parents=True, exist_ok=True)
    try:
        note = recover(dest)
    except OSError as e:
        return (f"the install before is not finished ({e}) - nothing else changed, no new install begun (close the game, "
                f"then run the install again; see {dest} and its journal {JOURNAL})")
    if note:
        print("install: " + note)
    files = dict(data)
    files[MANIFEST] = (json.dumps(man, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    return put_in_place(dest, files)


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser()
    ap.add_argument("zip", help="the release's MuseStudio-dll-<tag>.zip")
    ap.add_argument("--any-source", action="store_true", help="put in a DLL of another commit anyway (not advised)")
    a = ap.parse_args()
    zp = Path(a.zip)
    if not zp.is_file():
        print(f"install: no such file: {zp}")
        return 1
    why = install(zp, a.any_source)
    if why:
        print("install: " + why)
        return 1
    man, _ = read_release(zp)
    print(f"install: MuseStudio {man.get('plugin_version')} (contract {man.get('contract')}, source {man.get('source_commit', '')[:7]}) "
          f"-> {PLUGINS / 'MuseStudio'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
