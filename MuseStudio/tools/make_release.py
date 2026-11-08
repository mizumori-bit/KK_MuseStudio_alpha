"""The built C# plugin for GitHub Releases (the owner's decision of 2026-11-07, handed over by GPT on PR #47: the source stays
public; the plugin's DLLs go on Releases so that one can set MuseStudio up without the .NET SDK - not to keep the code from
being changed, nor to hide the bridge, the skills or the contract, which stay in the repository as they are).

  python -B MuseStudio/tools/make_release.py              (builds, checks, packs: MuseStudio/release/)
  python -B MuseStudio/tools/make_release.py --publish    (... and makes the GitHub release with gh: the owner's word first)

What it makes, from the commit checked out (clean and pushed - the public source the DLLs are built from):
- MuseStudio-dll-<tag>.zip: BepInEx/plugins/MuseStudio/ - MuseStudio.dll, MuseMaker.dll (the maker's part), MuseStudio.ico (built
  now into an empty folder: each one this build's, none left from before - the release refused otherwise)
  and musestudio_dll.json (the manifest: the source commit and its URL, the plugin's and the contract's versions, each
  file's SHA-256 and size, what it was built against - BepInEx, KKAPI, the .NET SDK's compiler);
- MuseStudio-dll-<tag>.zip.sha256 (the zip's SHA-256);
- release_notes.md (the release's text: what it is, how to install it - tools/install_dll.py - which source it is).
The tag is v<plugin version>-<the commit's first 7>. The DLLs are checked for this PC's paths and names before they are packed
(never published with them). Standard library only (and gh for --publish).
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent                    # MuseStudio/
REPO = ROOT.parent
GAME = ROOT / "game"
OUTDIR = ROOT / "release"
REPO_URL = "https://github.com/mizumori-bit/KK_MuseStudio_alpha"
FILES = ("MuseStudio.dll", "MuseMaker.dll", "MuseStudio.ico")
IN_ZIP = "BepInEx/plugins/MuseStudio/"
MANIFEST = "musestudio_dll.json"
# (the sources the DLLs are made of, and how: clean in the commit)
SOURCE_PATHS = ("MuseStudio/src", "MuseStudio/tools/build.py", "launcher/icons")


def git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=str(REPO), capture_output=True, text=True, encoding="utf-8", check=True).stdout.strip()


def plugin_version() -> str:
    m = re.search(r'public const string Version = "([^"]+)"', (ROOT / "src" / "Plugin.cs").read_text(encoding="utf-8"))
    if not m:
        sys.exit("release: no Version in src/Plugin.cs")
    return m.group(1)


def contract_version() -> str:
    m = re.search(r"\*\*契約の版：(\d+\.\d+)", (REPO / "MUSE_GUIDE.md").read_text(encoding="utf-8"))
    if not m:
        sys.exit("release: no contract version in MUSE_GUIDE.md")
    return m.group(1)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def private_marks() -> list[str]:
    """this PC's own strings that must not be in what is published: the home folder (its user name), the repository's and
    the game's absolute paths"""
    marks = {str(Path.home()), Path.home().name, str(REPO), str(GAME)}
    return sorted(m for m in marks if len(m) >= 3)


def find_private(data: bytes, marks: list[str]) -> list[str]:
    """the marks found in a file's bytes - as ASCII / UTF-8 and as UTF-16 (a .NET string), either slash, any case"""
    found = []
    low = data.lower()
    for m in marks:
        for v in {m, m.replace("\\", "/")}:
            for enc in ("utf-8", "utf-16-le"):
                if v.lower().encode(enc) in low:
                    found.append(m)
    return sorted(set(found))


def file_version(path: Path) -> str | None:
    """a DLL's own version (its assembly's version as Windows reads it) - None when it cannot be read"""
    if not path.is_file() or os.name != "nt":
        return None
    ps = f"(Get-Item -LiteralPath '{path}').VersionInfo.FileVersion"
    try:
        r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, timeout=30)
        v = r.stdout.strip()
        return v or None
    except (OSError, subprocess.SubprocessError):
        return None


def sdk_version() -> str | None:
    try:
        r = subprocess.run(["dotnet", "--version"], capture_output=True, text=True, timeout=60)
        return r.stdout.strip() or None
    except (OSError, subprocess.SubprocessError):
        return None


def build_fresh(out: Path, cmd: list[str] | None = None) -> str:
    """this build's own outputs (GPT's post-review of #74: build.py leaves the maker's part out without game/Koikatu_Data
    and still ends well - an older MuseMaker.dll beside it was packed as this commit's): built into an empty folder, each of
    the files there afterwards - "" or why not"""
    out.mkdir(parents=True, exist_ok=True)
    for n in FILES:
        if (out / n).exists():
            (out / n).unlink()
    r = subprocess.run(cmd or [sys.executable, "-B", str(HERE / "build.py"), "--out", str(out)], cwd=str(REPO))
    if r.returncode != 0:
        return "the build failed"
    missing = [n for n in FILES if not (out / n).is_file()]
    if missing:
        return ("not built now: " + ", ".join(missing) + " (the maker's part needs game/Koikatu_Data, the icon "
                "launcher/icons - tools/setup.py); nothing packed")
    return ""


def manifest(commit: str, files: dict[str, Path]) -> dict:
    return {
        "name": "MuseStudio plugin (built)",
        "plugin_version": plugin_version(),
        "contract": contract_version(),
        "source_commit": commit,
        "source_url": f"{REPO_URL}/tree/{commit}",
        "built": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "built_against": {
            "BepInEx": file_version(GAME / "BepInEx" / "core" / "BepInEx.dll"),
            "KKAPI": file_version(GAME / "BepInEx" / "plugins" / "KKAPI.dll"),
            "dotnet_sdk": sdk_version(),
            "unity_profile": "Unity 5.6 .NET 3.5 (CharaStudio_Data/Managed, Koikatu_Data/Managed for MuseMaker.dll)",
        },
        "files": {n: {"sha256": sha256(p), "size": p.stat().st_size} for n, p in files.items()},
        "install": "python -B MuseStudio/tools/install_dll.py <this zip> (in a clone of the repository at source_commit)",
    }


def pack(out: Path, tag: str, files: dict[str, Path], man: dict) -> Path:
    """the zip: the plugin's folder as it goes into game/, with its manifest"""
    out.mkdir(parents=True, exist_ok=True)
    zp = out / f"MuseStudio-dll-{tag}.zip"
    with zipfile.ZipFile(zp, "w", compression=zipfile.ZIP_DEFLATED) as z:
        for n, p in files.items():
            z.write(p, IN_ZIP + n)
        z.writestr(IN_ZIP + MANIFEST, json.dumps(man, ensure_ascii=False, indent=2) + "\n")
    (out / (zp.name + ".sha256")).write_text(f"{sha256(zp)}  {zp.name}\n", encoding="utf-8")
    return zp


def notes(tag: str, man: dict, zp: Path) -> str:
    rows = "\n".join(f"| `{n}` | `{f['sha256']}` | {f['size']} |" for n, f in man["files"].items())
    return f"""MuseStudio のプラグイン（C#）のビルド済みの DLL です。.NET SDK が無くても MuseStudio を立てられます。

**ソースは公開のままです。** この DLL は、下のコミットのソースから作りました。改変を防ぐためのものでも、ブリッジ・スキル・契約を隠すためのものでもありません（それらはリポジトリにそのままあります）。

- 対応するソース：[`{man['source_commit'][:7]}`]({man['source_url']})（`{man['source_commit']}`）
- プラグインの版：{man['plugin_version']}　契約の版：{man['contract']}
- 作った日時（UTC）：{man['built']}
- 作った相手：BepInEx {man['built_against']['BepInEx']}、KKAPI {man['built_against']['KKAPI']}、.NET SDK {man['built_against']['dotnet_sdk']}（Unity 5.6 の .NET 3.5 の型）

## 入れ方

1. リポジトリを、上のコミットでクローンします（`git clone` のあと `git checkout {man['source_commit'][:7]}`）。DLL は、そのコミットの `MuseStudio/data` などと組で動きます。
2. `MuseStudio/README.md` の「新しい環境で始める」の 1（`setup.py`）を済ませます。
3. この zip（`{zp.name}`）を落として、次を実行します。中の SHA-256 と、手元のソースのコミットを確かめてから、`MuseStudio/game/BepInEx/plugins/MuseStudio/` に入れます。

   ```
   python -B MuseStudio/tools/install_dll.py {zp.name}
   ```

4. あとは README の 3（データを作る）から先へ進みます（2 のビルドは要りません）。
5. うまくいかないときは、導入診断で前提をまとめて確かめられます（読むだけ。鍵の値や場所は出しません）：`python -B MuseStudio/tools/doctor.py`。DLL の目録のコミットが手元と同じかも確かめます。

## 中身

| ファイル | SHA-256 | 大きさ |
|---|---|---|
{rows}

zip の SHA-256 は `{zp.name}.sha256` にあります。

🤖 Generated with [Claude Code](https://claude.com/claude-code)
"""


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser()
    ap.add_argument("--publish", action="store_true", help="make the GitHub release with gh (the owner's word first)")
    ap.add_argument("--out", default=str(OUTDIR))
    a = ap.parse_args()
    # the public source the DLLs are made of: committed, clean, pushed
    commit = git("rev-parse", "HEAD")
    dirty = git("status", "--porcelain", "--", *SOURCE_PATHS)
    if dirty:
        sys.exit("release: the sources have changes not committed - the DLLs would not be this commit's:\n" + dirty)
    if not git("branch", "-r", "--contains", commit):
        sys.exit(f"release: {commit[:7]} is not pushed - its source would not be public")
    # built now, from it - into an empty folder of its own: what is packed is this build's (never one left from before)
    built = Path(tempfile.mkdtemp(prefix="musestudio_release_"))
    why = build_fresh(built)
    if why:
        shutil.rmtree(built, ignore_errors=True)
        sys.exit("release: " + why)
    files = {n: built / n for n in FILES}
    marks = private_marks()
    for n, p in files.items():
        hit = find_private(p.read_bytes(), marks)
        if hit:
            shutil.rmtree(built, ignore_errors=True)
            sys.exit(f"release: {n} has this PC's own paths or names in it ({len(hit)}) - not packed")
    tag = f"v{plugin_version()}-{commit[:7]}"
    man = manifest(commit, files)
    out = Path(a.out)
    zp = pack(out, tag, files, man)
    shutil.rmtree(built, ignore_errors=True)
    nt = out / "release_notes.md"
    nt.write_text(notes(tag, man, zp), encoding="utf-8")
    print(f"release: {zp} ({zp.stat().st_size} bytes)")
    print(f"release: {nt}")
    print(f"release: tag {tag}, source {commit}")
    if a.publish:
        gh = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "gh", "bin", "gh.exe")
        gh = gh if os.path.isfile(gh) else "gh"
        title = f"MuseStudio プラグイン {plugin_version()}（{commit[:7]}、契約 {contract_version()}）"
        cmd = [gh, "release", "create", tag, str(zp), str(out / (zp.name + ".sha256")), "--title", title,
               "--notes-file", str(nt), "--target", commit, "--prerelease"]
        r = subprocess.run(cmd, cwd=str(REPO))
        if r.returncode != 0:
            sys.exit("release: gh release create failed")
        print(f"release: published {tag}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
