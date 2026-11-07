"""Builds the MuseStudio plugin (MuseStudio/src/*.cs) into MuseStudio/game/BepInEx/plugins/MuseStudio/MuseStudio.dll, and the
maker's part (MuseStudio/src/maker/*.cs: the creation's changes in the original's maker, game/Koikatu.exe) into MuseMaker.dll
beside it, against the original's Koikatu_Data/Managed.

  python -B MuseStudio/tools/build.py

The C# compiler of the installed .NET SDK (Roslyn's csc.dll through `dotnet`), C# 7.3, against the game's own assemblies
(CharaStudio_Data/Managed: Unity 5.6's .NET 3.5 profile, -nostdlib), BepInEx and KKAPI from the launch folder. No NuGet,
nothing downloaded. Run tools/setup.py first. Standard library only.
"""
from __future__ import annotations

import glob
import os
import shutil
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME = ROOT / "game"
OUT = GAME / "BepInEx" / "plugins" / "MuseStudio" / "MuseStudio.dll"
MAKER_OUT = OUT.parent / "MuseMaker.dll"
PLUGIN_REFS = ("KKAPI.dll",)


def csc() -> list[str]:
    sdks = sorted(glob.glob(r"C:\Program Files\dotnet\sdk\*\Roslyn\bincore\csc.dll"))
    if not sdks:
        sys.exit("build: no .NET SDK compiler (C:\\Program Files\\dotnet\\sdk\\*\\Roslyn\\bincore\\csc.dll)")
    return ["dotnet", sdks[-1]]


BEPINEX_REFUSES = ["=", "\n", "\t", "\\", '"', "'", "[", "]"]     # (BepInEx's ConfigDefinition: in a section or a key)


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    managed = GAME / "CharaStudio_Data" / "Managed"
    if not managed.is_dir():
        sys.exit("build: run tools/setup.py first (no game/CharaStudio_Data)")
    refs = [p for p in sorted(managed.glob("*.dll"))]
    refs += [GAME / "BepInEx" / "core" / n for n in ("BepInEx.dll", "0Harmony.dll")]
    refs += [GAME / "BepInEx" / "plugins" / n for n in PLUGIN_REFS]
    srcs = sorted((ROOT / "src").glob("*.cs"))
    # (a setting's section or key with a character BepInEx refuses throws in the plugin's Awake - the plugin then does
    # nothing at all, silently: 2026-11-07, an apostrophe in a key)
    bad = []
    for s in srcs:
        for m in re.finditer(r'Config\.Bind\(\s*"([^"]*)"\s*,\s*"([^"]*)"', s.read_text(encoding="utf-8")):
            for part in m.groups():
                if any(c in part for c in BEPINEX_REFUSES):
                    bad.append(f"{s.name}: {part!r}")
    if bad:
        sys.exit("build: a setting's section or key has a character BepInEx refuses (= tab newline backslash quotes [ ]): " + "; ".join(bad))
    OUT.parent.mkdir(parents=True, exist_ok=True)
    cmd = csc() + ["-nologo", "-noconfig", "-nostdlib+", "-target:library", "-langversion:7.3", "-optimize+", "-debug-",
                   "-warn:4", "-nowarn:1701,1702", f"-out:{OUT}"] + [f"-r:{r}" for r in refs] + [str(s) for s in srcs]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = (r.stdout + r.stderr).strip()
    if out:
        print(out)
    if r.returncode != 0:
        print("build: FAILED")
        return 1
    # the launcher's icon beside the plugin: the game window takes it (src/Plugin.cs WindowTitle.SetIcon)
    icon = ROOT.parent / "launcher" / "icons" / "musestudio-launcher-app.ico"
    if icon.is_file():
        shutil.copyfile(icon, OUT.parent / "MuseStudio.ico")
    print(f"build: {OUT} ({os.path.getsize(OUT)} bytes, {len(srcs)} sources)")
    # the maker's part: the main game's assemblies (the maker's ChaCustom is not in Studio's)
    kmanaged = GAME / "Koikatu_Data" / "Managed"
    msrcs = sorted((ROOT / "src" / "maker").glob("*.cs"))
    if not kmanaged.is_dir():
        print("build: no game/Koikatu_Data (tools/setup.py) - the maker's part is not built")
        return 0
    mrefs = [p for p in sorted(kmanaged.glob("*.dll"))] + [GAME / "BepInEx" / "core" / n for n in ("BepInEx.dll", "0Harmony.dll")]
    cmd = csc() + ["-nologo", "-noconfig", "-nostdlib+", "-target:library", "-langversion:7.3", "-optimize+", "-debug-",
                   "-warn:4", "-nowarn:1701,1702", f"-out:{MAKER_OUT}"] + [f"-r:{r}" for r in mrefs] + [str(s) for s in msrcs]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = (r.stdout + r.stderr).strip()
    if out:
        print(out)
    if r.returncode != 0:
        print("build: the maker's part FAILED")
        return 1
    print(f"build: {MAKER_OUT} ({os.path.getsize(MAKER_OUT)} bytes, {len(msrcs)} sources)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
