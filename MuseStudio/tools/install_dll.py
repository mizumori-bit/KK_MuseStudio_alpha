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
Then the files go into game/BepInEx/plugins/MuseStudio/ (the folder's other files left as they are). Nothing is downloaded:
the zip is the one you took from the release page. Standard library only.
"""
from __future__ import annotations

import argparse
import hashlib
import json
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


def install(zip_path: Path, any_source: bool = False, plugins: Path = PLUGINS, commit: str | None = None) -> str:
    """put the release's plugin in place: "" done, else why not (nothing written)"""
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
    for n, b in data.items():
        (dest / n).write_bytes(b)
    (dest / MANIFEST).write_text(json.dumps(man, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return ""


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
