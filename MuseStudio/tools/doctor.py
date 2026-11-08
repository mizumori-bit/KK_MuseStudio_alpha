"""MuseStudio's install check (#70 adopted, 4): what the setup needs, looked at - nothing written, nothing started, nothing
sent anywhere but this PC's own bridge port.

  python -B MuseStudio/tools/doctor.py

Each line is OK / 要対応 (to do: what to do next) / 未確認 (not known from here). Looked at:
- Python's version;
- the original install (tools/original_path.txt or MUSESTUDIO_ORIGINAL): there, CharaStudio.exe and abdata in it, BepInEx and
  its version;
- the mods MuseStudio needs there (KKAPI, Sideloader, ExtensibleSaveFormat) and the ones it uses when present (Push Up,
  HideAllUI), with their versions;
- the launch folder (tools/setup.py's game/): its links to the original, MuseStudio's DLLs - a release's (its manifest's source
  commit against this clone's) or a build of its own;
- the .NET SDK's compiler (for tools/build.py; without it, a release's DLLs);
- the bridge: its settings file, the port (a bridge answering there, or free), its tokens' file - each token's length and a
  fingerprint only, never the token - and Muse's own port set or not.
Never shown: a token, the tailnet's names or addresses, a folder's path (found / not found only). Standard library only.
"""
from __future__ import annotations

import hashlib
import json
import os
import socket
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent                    # MuseStudio/
REPO = ROOT.parent
sys.path.insert(0, str(HERE))
from localcfg import original_path  # noqa: E402

NEEDED = (("KKAPI", ("KKAPI.dll",)), ("Sideloader", ("Sideloader.dll",)), ("ExtensibleSaveFormat", ("ExtensibleSaveFormat.dll",)))
USED = (("Push Up", ("KK_Pushup.dll",)), ("HideAllUI", ("HideAllUI.Koikatu.dll", "HideAllUI.dll")))
LINKS = ("CharaStudio_Data", "abdata", "mods", "Koikatu_Data")


class Report:
    def __init__(self):
        self.lines: list[tuple[str, str, str, str]] = []

    def add(self, status: str, item: str, detail: str = "", todo: str = ""):
        self.lines.append((status, item, detail, todo))

    def text(self) -> str:
        out = []
        for status, item, detail, todo in self.lines:
            out.append(f"[{status}] {item}" + (f" - {detail}" if detail else ""))
            if todo:
                out.append(f"        → {todo}")
        n = {s: sum(1 for x in self.lines if x[0] == s) for s in ("OK", "要対応", "未確認")}
        out.append(f"\nOK {n['OK']}・要対応 {n['要対応']}・未確認 {n['未確認']}")
        return "\n".join(out)


def file_version(path: Path) -> str:
    """a DLL's FileVersion from its version resource (UTF-16 in the file), "" when there is none"""
    try:
        data = path.read_bytes()
    except OSError:
        return ""
    key = "FileVersion".encode("utf-16-le") + b"\x00\x00"
    i = data.find(key)
    if i < 0:
        return ""
    j = i + len(key)
    while j < len(data) - 1 and data[j:j + 2] == b"\x00\x00":       # (the padding to the value)
        j += 2
    end = data.find(b"\x00\x00", j)
    while end > 0 and (end - j) % 2:
        end = data.find(b"\x00\x00", end + 1)
    try:
        v = data[j:end].decode("utf-16-le").strip()
    except UnicodeDecodeError:
        return ""
    return v if v and v != "0.0.0.0" and all(c.isdigit() or c in ".-" for c in v[:1]) else ""


def find(root: Path, names: tuple[str, ...]) -> Path | None:
    for name in names:
        for p in root.rglob(name):
            return p
    return None


def fingerprint(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()[:8]


def check_python(r: Report):
    v = sys.version_info
    if v >= (3, 10):
        r.add("OK", "Python", f"{v.major}.{v.minor}.{v.micro}")
    else:
        r.add("要対応", "Python", f"{v.major}.{v.minor}", "Python 3.10 以上を入れてください（作者の環境は 3.10）")


def check_original(r: Report, orig: str) -> Path | None:
    if not orig:
        r.add("要対応", "原作の場所", "設定されていない",
              "MuseStudio/tools/original_path.txt に原作のフォルダを 1 行で書くか、python -B MuseStudio/tools/setup.py --original <場所> を実行")
        return None
    p = Path(orig)
    if not p.is_dir():
        r.add("要対応", "原作の場所", "設定された場所が見つからない", "original_path.txt の場所を確かめてください")
        return None
    missing = [n for n in ("CharaStudio.exe", "abdata") if not (p / n).exists()]
    if missing:
        r.add("要対応", "原作の場所", "見つかったが、" + "・".join(missing) + " が無い", "CharaStudio のある原作のフォルダを指してください")
        return None
    r.add("OK", "原作の場所", "見つかった（CharaStudio.exe・abdata あり）")
    core = p / "BepInEx" / "core" / "BepInEx.dll"
    if core.is_file():
        r.add("OK", "BepInEx", file_version(core) or "版は不明")
    else:
        r.add("要対応", "BepInEx", "原作に無い", "原作に BepInEx 5 を入れてください（BetterRepack などの導入済み環境を想定）")
    return p


def check_mods(r: Report, orig: Path | None):
    if orig is None:
        r.add("未確認", "前提の Mod", "原作の場所が分からない")
        return
    plugins = orig / "BepInEx" / "plugins"
    for label, names in NEEDED:
        f = find(plugins, names) if plugins.is_dir() else None
        if f:
            r.add("OK", label, file_version(f) or "版は不明")
        else:
            r.add("要対応", label, "原作の BepInEx/plugins に無い", f"{label} を原作に入れてください（MuseStudio に要ります）")
    for label, names in USED:
        f = find(plugins, names) if plugins.is_dir() else None
        if f:
            r.add("OK", label, (file_version(f) or "版は不明") + "（あれば使う）")
        else:
            r.add("OK", label, "無い（無くても動きます。あれば使います）")


def head_commit() -> str:
    try:
        return subprocess.run(["git", "rev-parse", "HEAD"], cwd=str(REPO), capture_output=True, text=True, check=True).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return ""


def check_launch_folder(r: Report, game: Path, commit: str):
    if not game.is_dir():
        r.add("要対応", "専用の起動フォルダ", "まだ無い", "python -B MuseStudio/tools/setup.py を実行（原作には書きません）")
        return
    missing = [n for n in LINKS if not (game / n).exists()]
    if missing:
        r.add("要対応", "専用の起動フォルダ", "原作へのリンクが足りない：" + "・".join(missing), "python -B MuseStudio/tools/setup.py をもう一度")
    else:
        r.add("OK", "専用の起動フォルダ", "あり（原作へのリンクも揃っている）")
    dest = game / "BepInEx" / "plugins" / "MuseStudio"
    dlls = [n for n in ("MuseStudio.dll", "MuseMaker.dll") if (dest / n).is_file()]
    if len(dlls) < 2:
        r.add("要対応", "MuseStudio の DLL", "足りない" + ("（" + "・".join(dlls) + " だけ）" if dlls else ""),
              "Releases の DLL を python -B MuseStudio/tools/install_dll.py <zip> で入れるか、.NET SDK があれば python -B MuseStudio/tools/build.py")
        return
    man = dest / "musestudio_dll.json"
    if man.is_file():
        try:
            m = json.loads(man.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            r.add("要対応", "MuseStudio の DLL", "配布版の目録が読めない", "install_dll.py で入れ直してください")
            return
        src = str(m.get("source_commit", ""))
        if commit and src == commit:
            r.add("OK", "MuseStudio の DLL", f"配布版 {m.get('plugin_version')}（契約 {m.get('contract')}、このクローンと同じコミット {src[:7]}）")
        elif commit:
            r.add("要対応", "MuseStudio の DLL", f"配布版のコミット {src[:7]} とこのクローン {commit[:7]} が違う",
                  f"git checkout {src[:7]} するか、このコミットのリリースを入れてください")
        else:
            r.add("未確認", "MuseStudio の DLL", f"配布版 {src[:7]}（このクローンのコミットが分からない）")
    else:
        r.add("OK", "MuseStudio の DLL", "このクローンでビルドしたもの（" + (file_version(dest / "MuseStudio.dll") or "版は不明") + "）")


def check_sdk(r: Report, sdk_glob_root: Path = Path(r"C:\Program Files\dotnet\sdk")):
    found = sorted(sdk_glob_root.glob("*/Roslyn/bincore/csc.dll")) if sdk_glob_root.is_dir() else []
    if found:
        r.add("OK", ".NET SDK", found[-1].parent.parent.parent.name + "（ソースからビルドできる）")
    else:
        r.add("OK", ".NET SDK", "無い（Releases の DLL を使ってください：install_dll.py）")


def check_bridge(r: Report, bridge_dir: Path, probe: bool = True):
    cfg_file = bridge_dir / "bridge_config.json"
    if not cfg_file.is_file():
        r.add("要対応", "ブリッジの設定", "bridge/bridge_config.json が無い", "bridge/bridge_config.example.json を写して作ってください")
        cfg = {}
    else:
        try:
            cfg = json.loads(cfg_file.read_text(encoding="utf-8"))
            r.add("OK", "ブリッジの設定", "あり（鍵 " + str(cfg.get("auth", "required")) + "）")
        except (OSError, ValueError):
            r.add("要対応", "ブリッジの設定", "読めない（JSON の誤り）", "bridge_config.json を確かめてください")
            cfg = {}
    port = int(cfg.get("port", 18765) or 18765)
    if probe:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=2) as res:
                doc = json.loads(res.read() or b"{}")
            r.add("OK", "ブリッジ", f"ポート {port} で動いている" + ("（鍵が要る）" if doc.get("auth") == "required" else ""))
        except urllib.error.HTTPError as e:
            r.add("未確認", "ブリッジ", f"ポート {port} で応答 {e.code}（ブリッジではないかもしれない）")
        except (OSError, ValueError):
            s = socket.socket()
            try:
                s.settimeout(1)
                busy = s.connect_ex(("127.0.0.1", port)) == 0
            finally:
                s.close()
            if busy:
                r.add("要対応", "ブリッジ", f"ポート {port} をほかのものが使っている", "そのプログラムを止めるか、bridge_config.json の port を変えてください")
            else:
                r.add("OK", "ブリッジ", f"動いていない（ポート {port} は空いている。ランチャーか python -B bridge/bridge.py で起動）")
    tf = cfg.get("tokens_file", "cache/tokens.json")
    tokens = Path(tf) if Path(tf).is_absolute() else bridge_dir / tf
    if not tokens.is_file():
        r.add("OK", "ブリッジの鍵", "まだ無い（ブリッジを初めて起動したときに作られます）")
    else:
        try:
            t = json.loads(tokens.read_text(encoding="utf-8"))
            parts = [f"{role} 長さ {len(str(t[role]))}・指紋 {fingerprint(str(t[role]))}" for role in ("app", "muse") if role in t]
            bad = [role for role in ("app", "muse") if role not in t or len(str(t.get(role, ""))) < 32]
            if bad:
                r.add("要対応", "ブリッジの鍵", "形が違う：" + "・".join(bad), "鍵の作り直しは持ち主の判断で（python -B bridge/bridge.py --new-tokens）")
            else:
                r.add("OK", "ブリッジの鍵", "、".join(parts))
        except (OSError, ValueError):
            r.add("要対応", "ブリッジの鍵", "読めない", "鍵のファイルを確かめてください（作り直しは持ち主の判断で）")
    ml = cfg.get("muse_listener") or {}
    if isinstance(ml, dict) and ml.get("enabled"):
        r.add("OK" if ml.get("machine") else "要対応", "Muse の受け口", "有効" + ("（機械の指定あり）" if ml.get("machine") else "（機械の指定が無い）"),
              "" if ml.get("machine") else "muse_listener.machine に Muse の機械を指定してください（README の手順）")
    else:
        r.add("OK", "Muse の受け口", "使っていない（鍵を渡す経路 A）")


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    r = Report()
    check_python(r)
    orig = check_original(r, original_path())
    check_mods(r, orig)
    check_launch_folder(r, ROOT / "game", head_commit())
    check_sdk(r)
    check_bridge(r, REPO / "bridge")
    print("MuseStudio の導入診断（読むだけ。何も書き換えません）\n")
    print(r.text())
    return 1 if any(x[0] == "要対応" for x in r.lines) else 0


if __name__ == "__main__":
    sys.exit(main())
