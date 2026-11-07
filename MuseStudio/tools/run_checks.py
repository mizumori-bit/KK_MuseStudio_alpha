"""MuseStudio checks: a test bridge on a spare port, then MuseStudio (game/CharaStudio.exe) with --muse-check; then the maker's
part (game/Koikatu.exe -femalemaker --muse-maker-check: the creation's changes; no bridge, no card saved in the user's folders).

  python -B MuseStudio/tools/run_checks.py [--port 18792] [--timeout 1200] [--keep] [--no-maker | --maker-only]

The bridge is bridge/bridge.py with its own temporary events file (never the bridge Muse watches); MuseStudio talks only
to it (--muse-bridge). Results (CHECK lines, the FPS notes) are printed; the screenshots stay in the temporary folder
(printed; --keep also keeps the bridge's files). Standard library only.
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path

GAMES = ("CharaStudio.exe", "Koikatu.exe", "KoikatsuSunshine.exe")


def games_running() -> list[str]:
    """the game's programs already open on this PC (Windows' task list): a second start may end at once"""
    try:
        out = subprocess.run(["tasklist", "/fo", "csv", "/nh"], capture_output=True, text=True, timeout=20).stdout
    except (OSError, subprocess.SubprocessError):
        return []
    names = set()
    for line in out.splitlines():
        name = line.split('","')[0].strip('"')
        if name in GAMES:
            names.add(name)
    return sorted(names)

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent              # the project
GAME = HERE.parent / "game"


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=18792)
    ap.add_argument("--timeout", type=float, default=1200)      # (the checks take some 15 min now)
    ap.add_argument("--keep", action="store_true")
    ap.add_argument("--probe", default="", help="(development) instead of the checks, look at this kept room (src/Probe.cs)")
    ap.add_argument("--no-maker", action="store_true", help="skip the maker's part")
    ap.add_argument("--maker-only", action="store_true", help="only the maker's part")
    a = ap.parse_args()
    if a.maker_only:
        return maker_checks(Path(tempfile.mkdtemp(prefix="musestudio_check_")), a.timeout)
    if not (GAME / "BepInEx" / "plugins" / "MuseStudio" / "MuseStudio.dll").is_file():
        sys.exit("run_checks: build first (tools/setup.py, tools/build.py)")
    tmp = Path(tempfile.mkdtemp(prefix="musestudio_check_"))
    own = ROOT / "bridge/bridge_config.json"            # (this machine's; a fresh clone has only the example)
    cfg = json.loads((own if own.is_file() else ROOT / "bridge/bridge_config.example.json").read_text(encoding="utf-8"))
    for k in ("web_root", "static_root", "kkdata_root", "audio_cache_dir", "voice_catalog", "characters_dir"):
        cfg[k] = str((ROOT / "bridge" / cfg[k]).resolve())
    cfg["events_file"] = str(tmp / "events.json")
    cfg["persona_file"] = str(tmp / "personas.json")      # the check's own (MuseStudio writes it next to the results)
    cfg["card_dirs"] = [str((ROOT / "bridge" / d).resolve()) for d in cfg.get("card_dirs", [])]     # (read only)
    cfg["auth"] = "required"                              # (the tokens, as a fresh install has them - this PC's may be off)
    cfg["tokens_file"] = str(tmp / "tokens.json")         # (the check's own: the game is told where)
    cfg["rate_limits"] = {"muse": [1000, 5000]}           # (the checks stand in for Muse every frame; the app's rate as it is)
    cfg.pop("muse_listener", None)                        # (Muse's own port is the live bridge's, never a check's: 2026-11-07)
    (tmp / "bridge_config.json").write_text(json.dumps(cfg), encoding="utf-8")
    blog = open(tmp / "bridge.log", "w", encoding="utf-8")          # (the test bridge's own log: kept with --keep)
    bridge = subprocess.Popen([sys.executable, "-B", str(ROOT / "bridge/bridge.py"), "--config", str(tmp / "bridge_config.json"),
                               "--port", str(a.port), "--test"], stdout=blog, stderr=subprocess.STDOUT)
    results = tmp / "results.txt"
    game = None
    try:
        for _ in range(50):
            try:
                urllib.request.urlopen(f"http://127.0.0.1:{a.port}/health", timeout=1)
                break
            except OSError:
                time.sleep(0.1)
        else:
            print("CHECK FAIL test bridge did not start")
            return 1
        running = games_running()
        if running:
            print(f"CHECK FAIL the game is already open ({', '.join(running)}): close it, then run the checks")
            return 1
        t0 = time.time()
        game = subprocess.Popen([str(GAME / "CharaStudio.exe"), f"--muse-bridge=http://127.0.0.1:{a.port}", f"--muse-check={results}",
                                 f"--muse-bridge-tokens={tmp / 'tokens.json'}"]
                                + ([f"--muse-probe={a.probe}"] if a.probe else []),
                                cwd=str(GAME))
        done = False
        while time.time() - t0 < a.timeout and game.poll() is None:
            time.sleep(1.0)
            if results.is_file() and "CHECK SUMMARY" in results.read_text(encoding="utf-8", errors="replace"):
                done = True
                break
        quit_alone = False
        for _ in range(20):
            if game.poll() is not None:
                quit_alone = True
                break
            time.sleep(0.5)
        text = results.read_text(encoding="utf-8", errors="replace") if results.is_file() else ""
        if not done and "CHECK SUMMARY" in text:
            done = True                                   # (written at the very end of the time: they did finish)
        print(text.rstrip() or "(no results)")
        if not done and quit_alone and time.time() - t0 < 120:
            print(f"CHECK FAIL the game ended by itself {time.time() - t0:.0f} s after it started, before the checks began (exit code "
                  f"{game.returncode}; another game open meanwhile? {', '.join(games_running()) or 'none now'})")
        elif not done:
            print(f"CHECK FAIL the checks did not finish in {a.timeout:.0f} s (see game/BepInEx/LogOutput.log)")
        if done:
            print("NOTE the game closed by itself at the end (the quit marker: no 終了しますか？)" if quit_alone else "NOTE the game did not close by itself (killed)")
        print(f"NOTE {time.time() - t0:.0f} s in all; screenshots: {tmp}")
        ok = done and "fail=0" in text
        if not a.probe and not a.no_maker:
            if game is not None and game.poll() is None:
                game.kill()
            ok = maker_checks(tmp, a.timeout) == 0 and ok
        return 0 if ok else 1
    finally:
        if game is not None and game.poll() is None:
            game.kill()
        bridge.terminate()
        bridge.wait()
        blog.close()
        if not a.keep:
            for f in ("events.json", "bridge_config.json"):
                (tmp / f).unlink(missing_ok=True)


def maker_checks(tmp: Path, timeout: float) -> int:
    """the maker's part: the original's maker opened as the creation opens it, its checks run in it (src/maker/MakerCheck.cs)"""
    exe = GAME / "Koikatu.exe"
    if not exe.is_file() or not (GAME / "BepInEx" / "plugins" / "MuseStudio" / "MuseMaker.dll").is_file():
        print("CHECK FAIL maker: no game/Koikatu.exe or MuseMaker.dll (tools/setup.py, tools/build.py)")
        return 1
    results = tmp / "maker_results.txt"
    t0 = time.time()
    game = subprocess.Popen([str(exe), "-femalemaker", f"--muse-maker-check={results}"], cwd=str(GAME))
    done = False
    try:
        while time.time() - t0 < timeout and game.poll() is None:
            time.sleep(1.0)
            if results.is_file() and "CHECK SUMMARY" in results.read_text(encoding="utf-8", errors="replace"):
                done = True
                break
        quit_alone = False
        for _ in range(30):
            if game.poll() is not None:
                quit_alone = True
                break
            time.sleep(0.5)
        text = results.read_text(encoding="utf-8", errors="replace") if results.is_file() else ""
        print(text.rstrip() or "(no maker results)")
        if not done:
            print(f"CHECK FAIL maker: the checks did not finish in {timeout:.0f} s (see game/BepInEx/LogOutput.log)")
        else:
            print("NOTE the maker closed by itself" if quit_alone else "NOTE the maker did not close by itself (killed)")
        print(f"NOTE the maker's part {time.time() - t0:.0f} s; screenshots: {tmp}")
        return 0 if done and "fail=0" in text else 1
    finally:
        if game.poll() is None:
            game.kill()


if __name__ == "__main__":
    sys.exit(main())
