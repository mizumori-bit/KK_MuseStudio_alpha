"""Makes MuseStudio's local data files (MuseStudio/data: plays, locations, touch, sounds, talk) from the user's own original
install - the repository holds none of the original's data (the review of 2026-11-05: a fresh clone had no way to make them).

  python -B MuseStudio/tools/derive_data.py --dump      (the first time, and after new mods: runs the game once to read)
  python -B MuseStudio/tools/derive_data.py             (again from the last read)
  ... --out DIR                                          (writes there instead of data/: to compare)

What goes in:
- tools/base/*.json: this project's own design - the names and summaries we wrote, the settings, the place kinds - with the
  identifiers that look things up in the user's install (a position's name, a map's name, a clip's name). Tracked.
- game/studio_lists.json and game/original_lists.json: Studio's lists and the original's tables as the plugin reads them in
  the game (src/Dump.cs, src/Originals.cs; --dump runs game/CharaStudio.exe --muse-dump --muse-quit for them). Local.
- AnimationLoader's manifests in the original's mods (tools/al_plays.py).
The five files written are original-derived: local only, never published (.gitignore). The other data files (faces,
scenes, cameras, gauge, moods, beat, urges, stage, play_categories) are this project's own and tracked as they are.
Standard library only.
"""
from __future__ import annotations

import argparse
import collections
import copy
import json
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
BASE = HERE / "base"
GAME = ROOT / "game"
LISTS = GAME / "studio_lists.json"
ORIGINALS = GAME / "original_lists.json"
# Studio's animation groups of the original's H (female part, male part)
H_GROUPS = {"aibu": (1, None), "houshi": (2, 3), "sonyu": (4, 5), "masturbation": (6, None), "lesbian": (9, 9)}
# Studio's talk motion groups (the touch reactions' poses are looked up there by their state)
POSE_GROUPS = (0, 7, 11, 14)
# the footstep kinds (Illusion.Game.FootStepSE, the footstep list's ids) and each map's own (the game's ActionMap sets it by the
# map's number: the pool and the shower room Pool, the Japanese room Ground, the gymnasium Gymnasium; the rest Default)
FOOTSTEP_KINDS = ["Default", "Pool", "Courtyard", "Ground", "Metal", "Gymnasium"]
FOOTSTEP_MAPS = {37: "Pool", 45: "Pool", 28: "Ground", 32: "Gymnasium"}
# the walk's footstep events: the heroine's action controller, its FootStepBlendTreeEvent behaviours in the controller's own
# order (Locomotion_Anger, Locomotion, Locomotion 0 - Unity 5.6 names no state's behaviours; the order checked 2026-11-05
# against KoikatsuOfGodot's per-state read): the room's walk is Locomotion's
WALK_CONTROLLER = "action/animator/00.unity3d|base"
WALK_BEHAVIOUR = 1


def load(p: Path):
    return json.loads(p.read_text(encoding="utf-8"))


def dump() -> None:
    """the game once with --muse-dump --muse-quit (no bridge: a dead address), for Studio's lists and the original's tables"""
    exe = GAME / "CharaStudio.exe"
    if not exe.is_file():
        sys.exit("derive: no game/CharaStudio.exe (run tools/setup.py, then tools/build.py)")
    t0 = time.time()
    r = subprocess.run([str(exe), "--muse-dump", "--muse-quit", "--muse-bridge=http://127.0.0.1:9"], cwd=str(GAME), timeout=900)
    for p in (LISTS, ORIGINALS):
        if not p.is_file() or p.stat().st_mtime < t0:
            sys.exit(f"derive: the game did not write {p.name} (exit {r.returncode}; see game/BepInEx/LogOutput.log)")
    print(f"derive: read the game in {time.time() - t0:.0f} s")


# ---- plays: the base's rows on Studio's H categories (by the position's name), their states by clip; the AL plays ----

def plays(base: dict, lists: dict) -> tuple[dict, list]:
    missing = []
    cats = collections.defaultdict(dict)
    states = collections.defaultdict(dict)
    for a in lists["anime"]:
        cats[a["group"]][a["category"]] = a["category_name"]
        states[(a["group"], a["category"])][a["clip"]] = a["no"]

    def find(g, n):
        for c, cn in cats[g].items():
            if cn == n:
                return c
        return None

    doc = copy.deepcopy(base)
    doc["plays"] = []
    for p in base["plays"]:
        fg, mg = H_GROUPS[p["group"]]
        name = p["name"]
        if p["group"] == "aibu":
            fc, mc = find(fg, name.replace("愛撫", "")), None
        elif p["group"] == "lesbian":
            fc, mc = find(fg, name + "A"), find(fg, name + "B")
        else:
            fc, mc = find(fg, name), (find(mg, name) if mg is not None else None)
        if fc is None:
            missing.append(f"play {p['id']} {name}")
            continue
        row = {k: p[k] for k in ("id", "name", "group", "mode", "motion_id", "places") if k in p}
        row.update({"female": [fg, fc], "female_states": states[(fg, fc)]})
        if mc is not None:
            row["male"] = [mg, mc]
            row["male_states"] = states[(mg, mc)]
        # who does what (the base: the parts each role uses, reversible, strapon, mutual, lead, targets)
        row.update({k: v for k, v in p.items() if k not in row})
        if p["group"] == "sonyu":
            # the original's anal insertion: Studio's "<name>アナル" categories (their states A_<state>)
            af, am = find(fg, name + "アナル"), find(mg, name + "アナル")
            if af is not None and am is not None:
                row["anal_female"], row["anal_female_states"] = [fg, af], states[(fg, af)]
                row["anal_male"], row["anal_male_states"] = [mg, am], states[(mg, am)]
        doc["plays"].append(row)
    # the AnimationLoader plays (tools/al_plays.py; the user's decision 2026-11-03)
    sys.path.insert(0, str(HERE))
    import al_plays
    al, al_missing = al_plays.rows(lists, doc["plays"])
    doc["plays"].extend(al)
    missing.extend(al_missing)
    doc["_note_al"] = ("AL plays (source al): AnimationLoader's KPlug motions in Studio's AL groups - names translated, who gives by "
                       "tools/al_plays.py; the rest borrowed from the original pose of their DonorPoseId (motion_id). Their states are "
                       "clip names with {s} for the size set (S_ / M_ / L_ by her height; the app looks the clip up: Studio's numbers "
                       "of a mod's motions change per run). No motion IK.")
    return doc, missing


# ---- locations: the base's rooms on Studio's map numbers (by the map's name; the app looks a mod map up by name again) ----

def studio_maps(lists: dict) -> dict:
    by_name = {}
    for m in sorted(lists["maps"], key=lambda m: m["no"], reverse=True):
        by_name[m["name"]] = m          # (the same name twice: the lowest number - the original's)
    return by_name


def locations(base: dict, lists: dict) -> tuple[dict, list]:
    missing = []
    maps = studio_maps(lists)
    doc = copy.deepcopy(base)
    doc["locations"] = {}
    for key, v in base["locations"].items():
        m = maps.get(v.get("map_name", ""))
        if m is None:
            missing.append(f"location {key} ({v.get('map_name')})")
            continue
        row = {"name": v["name"], "map": m["no"]}
        row.update({k: x for k, x in v.items() if k not in row})
        doc["locations"][key] = row
    return doc, missing


# ---- touch: the base's areas and settings; the talk's touch reactions from the game's own read of the talk sheet ----

def latest_rows(sheets: list, key) -> dict:
    """[bundle, asset, rows] in the game's bundle order -> key(asset) -> name -> row (a later bundle's row replaces)"""
    out = collections.defaultdict(dict)
    for _bundle, asset, rows in sheets:
        k = key(asset)
        for r in rows:
            if r and r[0] not in ("", "name"):
                out[k][r[0]] = r
    return out


def touch(base: dict, lists: dict, orig: dict) -> tuple[dict, list]:
    by_clip = {}
    for a in lists["anime"]:
        if a["group"] in POSE_GROUPS:
            by_clip.setdefault(a["clip"], a)
    exprs = latest_rows(orig["expressions"], lambda a: str(int(a[1:])))
    poses = collections.defaultdict(dict)
    for no, _bundle, rows in orig["poses"]["tables"]:
        for r in rows:
            if r and r[0] != "name" and len(r) >= 4:
                poses[int(no)][r[0]] = (r[1], r[2], r[3])
    # (a pose names its controller's state; Studio's list names the clip that state plays - the game's read played each)
    clip_of = {(b, a, st): c for b, a, st, c in orig.get("pose_clips", []) if c}
    rows_out, no_pose, errors = {}, set(), []
    for pers, stages in orig["touch"].items():
        if pers.startswith("_"):
            continue
        if not isinstance(stages, dict):
            errors.append(f"touch {pers}: {stages[:80]}")
            continue
        for st, parts in stages.items():
            for part, rows in parts.items():
                levels = []
                for r in rows[:3]:
                    t0 = r["talk"][0] if r["talk"] else {"pose": "", "facialExpression": ""}
                    ex, pose = t0["facialExpression"], t0["pose"]
                    e = exprs.get(pers, {}).get(ex) or exprs.get("0", {}).get(ex)
                    bundle, asset, state = poses.get(int(pers), {}).get(pose, ("", "", ""))
                    hit = by_clip.get(state) or by_clip.get(clip_of.get((bundle, asset, state), ""))
                    if state and hit is None:
                        no_pose.add(state)
                    levels.append({"row": r["row"], "expression": ex, "cells": e[1:12] if e else None, "pose": pose,
                                   "anime": [hit["group"], hit["category"], hit["no"]] if hit else None})
                rows_out.setdefault(pers, {}).setdefault(st, {})[part] = levels
    doc = copy.deepcopy(base)
    doc["talk"]["rows"] = rows_out
    missing = errors + (["touch poses without a Studio animation: " + ", ".join(sorted(no_pose))] if no_pose else [])
    return doc, missing


# ---- sounds: the base's effects; each room's ambience and footsteps from the original's map tables ----

def map_numbers(lists: dict, orig: dict, locs: dict) -> dict:
    """location -> the original's map number (map/list/mapinfo, by the map's scene); a mod map has none"""
    by_scene = {}
    for m in orig["mapinfo"]:
        by_scene.setdefault(m["scene_bundle"].lower(), m["no"])
    maps = {m["no"]: m for m in lists["maps"]}
    out = {}
    for key, v in locs["locations"].items():
        m = maps.get(v["map"])
        no = by_scene.get((m or {}).get("bundle", "").lower())
        if no is not None:
            out[key] = no
    return out


def sounds(base: dict, lists: dict, orig: dict, locs: dict) -> tuple[dict, list]:
    missing = []
    nos = map_numbers(lists, orig, locs)
    clips = {}
    for sheet in orig["env_clips"]:
        for section, _bundle, _asset, rows in sheet["lists"]:
            if section == "#clip":
                for r in rows:
                    if len(r) >= 4 and str(r[0]).isdigit():
                        clips[int(r[0])] = {"bundle": r[2], "asset": r[3]}
    areas = {a["name"].lower(): a for a in orig["env_areas"]}
    doc = {"_note": "Sounds (tools/derive_data.py: tools/base/sounds.json's effects on the original's bundles; each room's ambience "
                    "and footsteps from the original's map tables as the game reads them): effects key -> {bundle, asset}; "
                    "location_change: the effect when the place changes; maps: location -> its map's ambience (clip, position in "
                    "Unity's frame, damping = linear roll-off min / max, delay = seconds between plays); footsteps: the walk's event "
                    "times in its cycle and each location's floor sound. The H sounds are the original's own (HSeCtrl over the "
                    "position's h/list fileSe).",
           "effects": base["effects"], "location_change": base["location_change"], "maps": {}, "footsteps": {}}
    for key in base.get("ambience", []):
        no = nos.get(key)
        a = areas.get("envarea_%02d" % no) if no is not None else None
        if a is None or not a.get("active", True) or not a.get("area"):
            missing.append(f"ambience {key} (map {no})")
            continue
        items = []
        for it in a["area"].get("_playlist") or []:
            c = clips.get(it.get("_clipID"))
            if c is None:
                missing.append(f"ambience {key} clip {it.get('_clipID')}")
                continue
            items.append({"bundle": c["bundle"], "asset": c["asset"], "position": [float(x) for x in it["_transform"]["position"]],
                          "damping": [float(it["_dampingScale"]["min"]), float(it["_dampingScale"]["max"])],
                          "delay": [float(it["_range"]["min"]), float(it["_range"]["max"])]})
        doc["maps"][key] = items
    # the footsteps: the walk's events, each room's floor (its map's kind -> the kind's first clip)
    walk = []
    for c in orig["footstep_events"]:
        if c["controller"] == WALK_CONTROLLER:
            blend = [b for b in c["behaviours"] if b["class"] == "FootStepBlendTreeEvent"]
            if len(blend) > WALK_BEHAVIOUR:
                walk = [float(e["_time"]) for e in blend[WALK_BEHAVIOUR]["fields"].get("_walkEvents") or []]
    if not walk:
        missing.append("footstep walk events")
    kind_clip = {}
    for sheet in orig["footstep_clips"]:
        lists_by = {(b, a): rows for b, a, rows in sheet["lists"]}
        for r in sheet["rows"]:
            if len(r) >= 4 and str(r[0]).isdigit():
                sub = [x for x in lists_by.get((r[2], r[3]), []) if len(x) >= 3 and x[1].startswith("sound/")]
                if sub:
                    kind_clip[int(r[0])] = {"bundle": sub[0][1], "asset": sub[0][2]}
    floors = {}
    for key in locs["locations"]:
        kind = FOOTSTEP_KINDS.index(FOOTSTEP_MAPS.get(nos.get(key, -1), "Default"))
        if kind in kind_clip:
            floors[key] = kind_clip[kind]
    fs = base.get("footsteps", {})
    doc["footsteps"] = {"times": walk, "cycle": fs.get("cycle", 2.0667), "min_distance": fs.get("min_distance", 1.0),
                        "max_distance": fs.get("max_distance", 7.5), "floors": floors}
    return doc, missing


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump", action="store_true", help="run the game once to read Studio's lists and the original's tables first")
    ap.add_argument("--out", default=str(ROOT / "data"), help="the folder to write (default: MuseStudio/data)")
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    if a.dump:
        dump()
    for p in (LISTS, ORIGINALS):
        if not p.is_file():
            sys.exit(f"derive: no game/{p.name} (run with --dump)")
    lists, orig = load(LISTS), load(ORIGINALS)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)

    def save(name: str, doc) -> None:
        (out / name).write_text(json.dumps(doc, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        print("wrote", out / name)

    missing = [f"the game's read: {e}" for e in orig.get("errors", [])]
    doc, m = plays(load(BASE / "plays.json"), lists)
    save("plays.json", doc)
    missing += m
    locs, m = locations(load(BASE / "locations.json"), lists)
    save("locations.json", locs)
    missing += m
    doc, m = touch(load(BASE / "touch.json"), lists, orig)
    save("touch.json", doc)
    missing += m
    doc, m = sounds(load(BASE / "sounds.json"), lists, orig, locs)
    save("sounds.json", doc)
    missing += m
    import derive_talk
    doc, lines = derive_talk.build(orig)
    save("talk.json", doc)
    if out == ROOT / "data":
        # (made in every environment - MUSE_GUIDE points Muse at it; from the original, so never in the repository)
        derive_talk.FOR_MUSE.mkdir(parents=True, exist_ok=True)
        (derive_talk.FOR_MUSE / "talk-poses.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("no match:", "; ".join(missing) if missing else "none")
    return 0


if __name__ == "__main__":
    sys.exit(main())
