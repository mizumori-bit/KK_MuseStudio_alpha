"""MuseStudio setup: builds the dedicated launch folder MuseStudio/game from the original install (never writes there).

  python -B MuseStudio/tools/setup.py [--original PATH] [--refresh-plugins] [--remove]

game/ gets
  - links (directory junctions) to the original's big read-only folders: CharaStudio_Data, abdata, mods, Koikatu_Data (the
    maker for the creation: Koikatu.exe -femalemaker / -malemaker);
  - copies: CharaStudio.exe (the name stays: a hundred of the original's plugins only run in a process named CharaStudio),
    winhttp.dll and doorstop_config.ini (Unity's log goes to game/output_log.txt, so nothing is written into the linked
    CharaStudio_Data), BepInEx core / patchers / plugins (minus EXCLUDED) / config (the original's absolute export folders
    pointed at game/UserData);
  - its own UserData: the folder layout, setup.xml, config/ and studio/option.xml from the original. Scenes, cards and
    plugin data stay separate from the user's Studio.
Run again: links and copies already there are kept; --refresh-plugins copies BepInEx core / patchers / plugins again (the
MuseStudio plugin folder is never touched). --remove deletes game/: the links first (only the link, never what it points
to), then the rest. Never delete game/ any other way (rm -rf can follow a junction into the original).
Standard library only.
"""
from __future__ import annotations

import argparse
import os
import re
import shutil
import stat
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent                        # MuseStudio/
GAME = ROOT / "game"
from localcfg import original_path      # (this machine's original install: not in the repository)
LINKS = ("CharaStudio_Data", "abdata", "mods", "Koikatu_Data")
# Koikatu.exe (with Koikatu_Data): the original's character maker for MuseStudio's creation (Koikatu.exe -femalemaker /
# -malemaker, the Autostart plugin), saving its cards into game/UserData/chara - never into the original install
FILES = ("CharaStudio.exe", "winhttp.dll", "Koikatu.exe")
# Plugins left out (paths under BepInEx/plugins): the English translation (MuseStudio has its own Japanese UI; 418 MB of
# translations at start-up) with the plugins that need it, and the video export. XUnity.ResourceRedirector stays: Sideloader
# needs it. BepInEx.MessageCenter: its log lines on the screen (the stage has its own UI; the log file keeps them).
EXCLUDED = {"XUnity.AutoTranslator", "TranslationTools", "README (AutoTranslator).md", "GeBo_Plugins/KK_TranslationCacheCleaner.dll",
            "GeBo_Plugins/KK_TranslationHelper.dll", "VideoExport", "VideoExport.dll", "BepInEx.MessageCenter.dll"}
USERDATA_FILES = ("setup.xml", "config", "studio/option.xml", "MakerCameraPresets")
# MuseStudio's own plugin settings, set on every run (file -> {key: value}). UncensorSelector: a character whose card names no
# uncensor gets the default body (not a random one: the list has odd bodies) and the SoS penis (the user's decision
# 2026-11-02: SoS; the BetterPenetration build, as BetterPenetration runs here). The female genitals wait for the user's choice.
CONFIG_OVERRIDES = {
    "com.deathweasel.bepinex.uncensorselector.cfg": {
        "Default Male Body": "[Male] Default Body M", "Default Male Penis": "BP SoS", "Default Male Balls": "BP Balls",
        "Default Female Body": "[Female] Default Body F", "Default Female Penis": "BP SoS", "Default Female Balls": "BP Balls"},
}
OUR_PLUGIN = "MuseStudio"


def is_link(p: Path) -> bool:
    try:
        st = os.lstat(p)
    except OSError:
        return False
    return bool(getattr(st, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT) or stat.S_ISLNK(st.st_mode)


def link(name: str, original: Path) -> None:
    dst, src = GAME / name, original / name
    if is_link(dst):
        return
    if dst.exists():
        sys.exit(f"setup: {dst} exists and is not a link - remove it with --remove first")
    r = subprocess.run(["cmd", "/c", "mklink", "/J", str(dst), str(src)], capture_output=True, text=True)
    if r.returncode != 0 or not is_link(dst):
        sys.exit(f"setup: could not link {dst} -> {src}: {r.stdout}{r.stderr}")
    print(f"link  {name} -> {src}")


def copy_file(src: Path, dst: Path, overwrite: bool) -> None:
    if dst.exists() and not overwrite:
        return
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)


def copy_tree(src: Path, dst: Path, overwrite: bool, skip: set[str] = frozenset(), rel: str = "") -> int:
    # skip: paths relative to the first src ("a/b.dll")
    n = 0
    for item in src.iterdir():
        r = rel + item.name
        if r in skip:
            continue
        if item.is_dir():
            n += copy_tree(item, dst / item.name, overwrite, skip, r + "/")
        elif overwrite or not (dst / item.name).exists():
            copy_file(item, dst / item.name, True)
            n += 1
    return n


def doorstop(original: Path) -> None:
    dst = GAME / "doorstop_config.ini"
    if dst.exists():
        return
    text = (original / "doorstop_config.ini").read_text(encoding="utf-8")
    assert "redirect_output_log = false" in text
    dst.write_text(text.replace("redirect_output_log = false", "redirect_output_log = true"), encoding="utf-8")
    print("copy  doorstop_config.ini (Unity's log -> game/output_log.txt)")


def bepinex(original: Path, refresh: bool) -> None:
    src, dst = original / "BepInEx", GAME / "BepInEx"
    for part in ("core", "patchers"):
        n = copy_tree(src / part, dst / part, refresh)
        if n:
            print(f"copy  BepInEx/{part} ({n} files)")
    n = copy_tree(src / "plugins", dst / "plugins", refresh, EXCLUDED | {OUR_PLUGIN})
    if n:
        print(f"copy  BepInEx/plugins ({n} files; left out: {', '.join(sorted(EXCLUDED))})")
    if (src / "Stiletto").is_dir():
        copy_tree(src / "Stiletto", dst / "Stiletto", refresh)
    cfg = dst / "config"
    if not cfg.exists():
        copy_tree(src / "config", cfg, False)
        here = str(GAME)
        for f in cfg.iterdir():
            if f.suffix not in (".cfg", ".xml", ".ini"):
                continue
            t = f.read_text(encoding="utf-8", errors="surrogateescape")
            # (any absolute folder named like the original install - its packer's own default too)
            t2 = re.sub(r"[A-Za-z]:\\(?:[^\\\r\n\"<>|]*\\)*[^\\\r\n\"<>|]*" + re.escape(original.name), lambda m: here, t).replace(str(original), here)
            if t2 != t:
                f.write_text(t2, encoding="utf-8", errors="surrogateescape")
        print("copy  BepInEx/config (absolute folders -> game/)")
    (dst / "plugins" / OUR_PLUGIN).mkdir(parents=True, exist_ok=True)


def overrides() -> None:
    cfg = GAME / "BepInEx" / "config"
    for name, keys in CONFIG_OVERRIDES.items():
        f = cfg / name
        if not f.is_file():
            continue
        lines = f.read_text(encoding="utf-8").splitlines()
        changed = 0
        for i, line in enumerate(lines):
            k = line.split("=", 1)[0].strip()
            if "=" in line and not line.lstrip().startswith("#") and k in keys:
                new = f"{k} = {keys[k]}"
                if line != new:
                    lines[i] = new
                    changed += 1
        if changed:
            f.write_text("\n".join(lines) + "\n", encoding="utf-8")
            print(f"set   BepInEx/config/{name} ({changed} values)")


def userdata(original: Path) -> None:
    src, dst = original / "UserData", GAME / "UserData"
    if dst.exists():
        for rel in USERDATA_FILES:            # (what was added to the list later: the missing files only)
            if (src / rel).is_dir():
                if copy_tree(src / rel, dst / rel, False):
                    print(f"copy  UserData/{rel}")
            elif (src / rel).is_file() and not (dst / rel).exists():
                copy_file(src / rel, dst / rel, False)
                print(f"copy  UserData/{rel}")
        return
    for d in src.iterdir():
        if d.is_dir():
            (dst / d.name).mkdir(parents=True, exist_ok=True)
    for sub in ("chara/female", "chara/male", "studio/scene", "studio/pose", "coordinate"):
        (dst / sub).mkdir(parents=True, exist_ok=True)
    for rel in USERDATA_FILES:
        s = src / rel
        if s.is_dir():
            copy_tree(s, dst / rel, False)
        elif s.is_file():
            copy_file(s, dst / rel, False)
    print("make  UserData (own; layout and settings from the original)")


def remove() -> None:
    if not GAME.exists():
        return
    for name in LINKS:
        p = GAME / name
        if is_link(p):
            os.rmdir(p)                    # removes the junction itself, not the original's folder
            print(f"unlink {name}")
    left = [p for p in GAME.rglob("*") if is_link(p)]
    if left:
        sys.exit(f"setup: links left inside game/, not deleting: {left[:5]}")
    shutil.rmtree(GAME)
    print("removed game/")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--original", default=original_path(), help="the original install (default: MUSESTUDIO_ORIGINAL / tools/original_path.txt)")
    ap.add_argument("--refresh-plugins", action="store_true")
    ap.add_argument("--remove", action="store_true")
    a = ap.parse_args()
    if a.remove:
        remove()
        return 0
    if not a.original:
        sys.exit("setup: the original install? --original PATH, or MUSESTUDIO_ORIGINAL, or MuseStudio/tools/original_path.txt")
    original = Path(a.original)
    if not (original / "CharaStudio.exe").is_file():
        sys.exit(f"setup: no CharaStudio.exe in {original}")
    GAME.mkdir(parents=True, exist_ok=True)
    for name in LINKS:
        link(name, original)
    for name in FILES:
        if not (GAME / name).exists():
            copy_file(original / name, GAME / name, False)
            print(f"copy  {name}")
    doorstop(original)
    bepinex(original, a.refresh_plugins)
    userdata(original)
    overrides()
    print("ready:", GAME)
    return 0


if __name__ == "__main__":
    sys.exit(main())
