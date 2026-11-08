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
the new and the old mixed): each new file written beside its place first (<name>.new) and read back; then each old one put
aside (<name>.bak) and the new one moved in; any failure on the way puts every old one back and removes the new ones - the
install refused with why, the old set as it was. An install cut off before (its .bak / .new left) is put back to its old set
first. The folder's other files are left as they are. Nothing is downloaded: the zip is the one you took from the release
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


# (the writes and the moves: the tests put failures in here)
def _write(path: Path, data: bytes) -> None:
    path.write_bytes(data)


def _replace(src: Path, dst: Path) -> None:
    os.replace(src, dst)


def recover(dest: Path) -> list[str]:
    """an install cut off before (a .bak beside its file): the old set back - each .bak over its file, the .new ones gone"""
    back = []
    for n in WANT:
        bak, new = dest / (n + ".bak"), dest / (n + ".new")
        if bak.exists():
            os.replace(bak, dest / n)
            back.append(n)
        if new.exists():
            new.unlink()
    return back


def _rollback(dest: Path, names: list[str], swapped: list[str]) -> None:
    """every old one back (its .bak), a new one where there was none taken out, the .new ones gone"""
    for n in names:
        cur, bak, new = dest / n, dest / (n + ".bak"), dest / (n + ".new")
        try:
            if bak.exists():
                os.replace(bak, cur)
            elif n in swapped and cur.exists():
                cur.unlink()
        except OSError:
            pass
        try:
            if new.exists():
                new.unlink()
        except OSError:
            pass


def put_in_place(dest: Path, files: dict[str, bytes]) -> str:
    """the files into dest as one set: "" done, else why not - the set there before as it was"""
    names = list(files)
    try:
        for n, b in files.items():
            tmp = dest / (n + ".new")
            _write(tmp, b)
            if tmp.read_bytes() != b:
                raise OSError(f"{n} was not written as it is")
    except OSError as e:
        _rollback(dest, names, [])
        return f"could not write the new files ({e}) - the installed ones are as they were"
    swapped: list[str] = []
    try:
        for n in names:
            cur = dest / n
            if cur.exists():
                _replace(cur, dest / (n + ".bak"))
            _replace(dest / (n + ".new"), cur)
            swapped.append(n)
    except OSError as e:
        _rollback(dest, names, swapped)
        return f"could not put the new files in place ({e}; is the game running?) - the installed ones are back as they were"
    for n in names:
        try:
            bak = dest / (n + ".bak")
            if bak.exists():
                bak.unlink()
        except OSError:
            pass
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
        back = recover(dest)
    except OSError as e:
        return f"an install cut off before could not be put back ({e}) - nothing changed; see {dest}"
    if back:
        print("install: an install cut off before - its old files put back: " + ", ".join(back))
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
