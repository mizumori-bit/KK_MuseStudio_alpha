"""The original's talk motions -> data/talk.json (and ../for_muse/talk-poses.md, made in every environment) - the user's request
of 2026-11-04. Called by derive_data.py (build); alone:

  python -B MuseStudio/tools/derive_talk.py

From the original's tables the plugin reads in the game (game/original_lists.json, src/Originals.cs: --muse-dump): the
pose tables per personality (communication/info_*: pose_NN - a name, the motion's bundle / asset / state), the
personalities' names, and how often each personality's talk lines use each pose (communication_NN: the pose column; counts
only - no line of the original's text is kept). The base stances ベースNN are the original's personality NN's normal
stance (its 通常立ち). Original-derived: local only, never published.
"""
from __future__ import annotations

import collections
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
DATA = HERE.parent / "data"
ORIGINALS = HERE.parent / "game" / "original_lists.json"
FOR_MUSE = HERE.parent.parent / "for_muse"
KINDS = {"喜": ("joy", "喜び"), "怒": ("anger", "怒り"), "哀": ("sorrow", "悲しみ"), "恥": ("shy", "恥じらい"), "驚": ("surprise", "驚き"), "他": ("other", "その他")}


def clean(name: str) -> str:
    return name.strip().strip("　")


def build(orig: dict) -> tuple[dict, list[str]]:
    """talk.json and the Markdown lines for Muse, from the plugin's read of the original (original_lists.json)"""
    tables: dict[int, dict[str, list[str]]] = collections.defaultdict(dict)
    use: dict[int, collections.Counter] = collections.defaultdict(collections.Counter)
    for no, _bundle, rows in orig["poses"]["tables"]:
        for row in rows:
            if row and row[0] != "name" and len(row) >= 4:
                tables[int(no)][clean(row[0])] = row[1:4]
    pers: dict[int, str] = {int(k): v for k, v in orig["personalities"].items()}
    for no, name, n in orig["poses"]["use"]:
        use[int(no)][clean(name)] += int(n)
    # one motion per name, where every personality has the same; the personal ones by personality
    names: dict[str, set] = collections.defaultdict(set)
    for no, t in tables.items():
        for name, m in t.items():
            names[name].add(tuple(m))
    poses, personal = {}, collections.defaultdict(dict)
    for name, ms in names.items():
        if len(ms) == 1 and name not in ("通常立ち", "怒り立ち"):
            poses[name] = list(next(iter(ms)))
        else:
            for no, t in tables.items():
                if name in t:
                    personal[name][str(no)] = t[name]
    # the base stances: whose normal stance each is
    stance_of = {}
    for no, t in tables.items():
        m = t.get("通常立ち")
        for name, pm in poses.items():
            if name.startswith("ベース") and m and pm == m:
                stance_of[name] = no
    out_poses = {}
    for name in sorted(poses, key=lambda n: (not n.startswith("ベース"), n)):
        b, a, s = poses[name]
        e = {"bundle": b, "asset": a, "state": s}
        if name.startswith("ベース"):
            no = stance_of.get(name, int(name[3:]) if name[3:].isdigit() else -1)
            e.update({"kind": "stance", "of": no, "label": f"{pers.get(no, '?')}の普段の立ち方（原作の性格 {no} の通常立ち）"})
        elif name[0] in KINDS and name[1:].isdigit():
            k, ja = KINDS[name[0]]
            e.update({"kind": k, "label": f"{ja} {int(name[1:]) + 1}"})
        else:
            e.update({"kind": "reaction", "label": name})
        out_poses[name] = e
    out_pers = {}
    for no in sorted(tables):
        top = [n for n, _ in use[no].most_common()]
        stances = [n for n in top if n.startswith("ベース")]
        own = next((n for n, v in stance_of.items() if v == no), None)
        out_pers[str(no)] = {"name": pers.get(no, ""), "stance": own, "stances_used": stances[:5],
                             "poses_used": [n for n in top if not n.startswith("ベース")][:10],
                             "uses": dict(use[no].most_common())}
    doc = {"_note": "The original's talk motions (MUSE_GUIDE 8.7; tools/derive_talk.py from the plugin's read of the original, game/original_lists.json). "
                    "poses: name -> the ADV motion (bundle, asset, state); stance: ベースNN = the original's personality NN's 通常立ち. personal: "
                    "the names whose motion is by personality (通常立ち: her own stance, 怒り立ち...). personalities: the name, the own stance, the "
                    "stances and poses its talk lines use most in the original (uses: the counts). Original-derived: local only.",
           "poses": out_poses, "personal": personal, "personalities": out_pers}
    # for Muse: the reasoning material
    lines = ["# 原作の会話モーション（対戦の会話のターン用）", "",
             "MuseStudio の `data/talk.json` から作った一覧（`MuseStudio/tools/derive_talk.py`）。原作のデータ由来なので、手元だけで使い、公開しないこと。",
             "ブロックの `pose` に名前を書くと、そのブロックの話者の体がその動きになる（MUSE_GUIDE 第 8.7 節）。", "",
             "## 性格ごとの使い方（原作の会話の台詞の表で、その性格の台詞が使っている回数の多い順）", "",
             "| 性格 | 普段の立ち方 | よく使う立ち方 | よく使う動き |", "|---|---|---|---|"]
    for no, p in out_pers.items():
        lines.append(f"| {no} {p['name']} | {p['stance'] or '―'} | {'、'.join(p['stances_used'])} | {'、'.join(p['poses_used'])} |")
    lines += ["", "## 動きの一覧", "", "| 名前 | 種類 | 説明 | よく使う性格（原作の回数の多い順） |", "|---|---|---|---|"]
    users = collections.defaultdict(list)
    for no, p in out_pers.items():
        for n, c in p["uses"].items():
            users[n].append((c, p["name"]))
    kinds_ja = {"stance": "立ち方", "joy": "喜び", "anger": "怒り", "sorrow": "悲しみ", "shy": "恥じらい", "surprise": "驚き", "other": "その他", "reaction": "反応"}
    for name, e in out_poses.items():
        top = "、".join(n for _, n in sorted(users.get(name, []), reverse=True)[:5])
        lines.append(f"| {name} | {kinds_ja[e['kind']]} | {e['label']} | {top} |")
    lines += ["", "- `通常立ち`：その体の性格の普段の立ち方（性格で変わる）。`怒り立ち`：怒ったときの立ち方（性格で 2 種）。",
              "- 反応（恥ずかしがる・胸を見られ恥ずかしがる など）は、原作では触られた・見られたときの動き。"]
    return doc, lines


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    doc, lines = build(json.loads(ORIGINALS.read_text(encoding="utf-8")))
    (DATA / "talk.json").write_text(json.dumps(doc, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    FOR_MUSE.mkdir(parents=True, exist_ok=True)
    (FOR_MUSE / "talk-poses.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"talk: {len(doc['poses'])} poses, {len(doc['personal'])} personal, {len(doc['personalities'])} personalities")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
