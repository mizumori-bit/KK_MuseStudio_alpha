"""The launcher's shortcut with its icon (the user's choice of 2026-11-05, GPT's PR #8): "MuseStudio Launcher.lnk", starting
start.cmd in this folder. A shortcut holds this PC's paths, so it is made here, not kept in the repository.

  python -B launcher/make_shortcut.py              (beside the project: the folder above launcher/)
  python -B launcher/make_shortcut.py --desktop    (on the desktop)
  python -B launcher/make_shortcut.py --to DIR

Standard library only (Windows' own WScript.Shell through PowerShell).
"""
from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ICON = HERE / "icons" / "musestudio-launcher-app.ico"
NAME = "MuseStudio Launcher.lnk"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--desktop", action="store_true", help="put it on the desktop")
    ap.add_argument("--to", default="", help="put it in this folder")
    a = ap.parse_args()
    if a.to:
        folder = Path(a.to)
    elif a.desktop:
        folder = Path(subprocess.run(["powershell", "-NoProfile", "-Command", "[Environment]::GetFolderPath('Desktop')"],
                                     capture_output=True, text=True).stdout.strip())
    else:
        folder = HERE.parent
    if not folder.is_dir():
        sys.exit(f"make_shortcut: no folder {folder}")
    lnk = folder / NAME
    ps = ("$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:MS_LNK); $s.TargetPath = $env:MS_TARGET; "
          "$s.WorkingDirectory = $env:MS_DIR; $s.IconLocation = $env:MS_ICON + ',0'; "
          "$s.Description = 'MuseStudio Launcher (bridge, game, Muse panel)'; $s.Save()")
    env = dict(__import__("os").environ, MS_LNK=str(lnk), MS_TARGET=str(HERE / "start.cmd"), MS_DIR=str(HERE), MS_ICON=str(ICON))
    r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], env=env, capture_output=True, text=True)
    if r.returncode != 0 or not lnk.is_file():
        sys.exit(f"make_shortcut: failed ({r.stderr.strip()[:300]})")
    print(f"made {lnk}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
