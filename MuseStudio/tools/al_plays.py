"""The AnimationLoader plays (the user's decision 2026-11-03: "AL F/M houshi / sonyu" are part of this concept): their rows for
data/plays.json, from the mods' AnimationLoader manifests (the original install's Sideloader animation pack, read only) and
Studio's animation list (game/studio_lists.json: the AL groups and categories).

- A row borrows the original pose of its manifest's DonorPoseId (the same mode): its H list row (motion_id: clothes, the
  camera, the place offsets), its who-does-what (parts, lead, targets) - then this file's table sets the Japanese name, who
  gives (the service plays where he does it: fingering, cunnilingus) and the parts.
- States by clip name with "{s}" for the size set (S_ / M_ / L_: the app picks by her height); Studio's numbers of a mod's
  motions change per run, so the app looks the clip up (src/StudioAnim.cs). The pace's WLoop / SLoop are the first loops;
  a service play's finish M_OUT_* are its OUT_*.
- No motion IK (the donor's IK data is for another motion).
"""
from __future__ import annotations

import glob
import os
import re
import xml.etree.ElementTree as ET
import zipfile

from localcfg import original_path      # (this machine's original install: not in the repository)

ORIGINAL = original_path()
AL_GROUPS = {"houshi": ("AL F houshi", "AL M houshi"), "sonyu": ("AL F sonyu", "AL M sonyu")}
# the manifests' categories -> this app's place kinds (the original's H point categories)
PLACES = {"LieDown": "floor", "Stand": "stand", "SitChair": "chair", "Stool": "stool", "SofaBench": "sofa",
          "BacklessBench": "bench", "SchoolDesk": "school_desk", "Desk": "desk", "Wall": "wall"}

# StudioId -> (Japanese name, giver ("" = the group's: she serves), the giver's parts, the receiver's parts, mutual)
# (the pack's "Massage" is a rub with the feet: 1 / 3 / 16 / 21 / 22 were named handjobs - seen played, footjobs; 2026-11-04)
H = "hands"; M = "mouth"; B = "breasts"; Y = "body"; P = "penis"; V = "vagina"; A = "anus"
HOUSHI = {
    1: ("立ったまま足コキ", "", [Y], [P]), 2: ("寝そべって両足コキ", "", [Y], [P]), 3: ("腰掛けて足コキ", "", [Y], [P]),
    4: ("彼に導かれる膝立ち手コキ", "", [H], [P]), 5: ("寝そべってアナル舐め", "", [M], [A]), 6: ("寝そべって尻コキ", "", [Y], [P]),
    7: ("寝そべって跨り素股", "", [Y], [P]), 8: ("座った彼女のフェラ", "", [M], [P]), 9: ("寝そべってイラマチオ", "", [M], [P]),
    10: ("押し付けパイズリ２", "", [B], [P]), 11: ("寝そべって太もも素股", "", [Y], [P]), 12: ("背面の太もも素股", "", [Y], [P]),
    13: ("膝立ちの尻コキ", "", [Y], [P]), 14: ("立って太もも素股", "", [Y], [P]), 15: ("机で背面の尻コキ", "", [Y], [P]),
    16: ("机に寝て足コキ", "", [Y], [P]), 17: ("シンデレラ（足コキ）", "", [Y], [P]), 18: ("踏みつけ足コキ", "", [Y], [P]),
    19: ("机に寝てフェラ", "", [M], [P]), 20: ("無理やりフェラ", "", [M], [P]), 21: ("壁にもたれて足コキ", "", [Y], [P]),
    22: ("ちんぐり返しで足コキ", "", [Y], [P]), 23: ("四つん這い手コキ", "", [H], [P]), 24: ("後ろから抱いて手コキ", "", [H], [P]),
    25: ("机で四つん這い手コキ", "", [H], [P]), 26: ("寝そべって跨り太もも素股", "", [Y], [P]), 27: ("膝立ちの腋コキ", "", [Y], [P]),
    28: ("ディープスロート", "", [M], [P]), 29: ("立ったまま玉いじり", "", [H], [P]), 30: ("寝そべって胸を掴むパイズリ", "", [B], [P]),
    69: ("寝そべってアナル舐めと足コキ", "", [M, Y], [A, P]), 70: ("座って無理やりディープスロート", "", [M], [P]),
    71: ("机にもたれてディープスロート", "", [M], [P]), 72: ("無理やりディープスロート", "", [M], [P]),
    73: ("机で無理やりディープスロート", "", [M], [P]), 74: ("胸を掴むパイズリ", "", [B], [P]),
    75: ("膝立ちで手コキとアナル舐め", "", [H, M], [P, A]), 88: ("座って足コキ", "", [Y], [P]), 89: ("机の上で手コキ", "", [H], [P]),
    90: ("机に膝立ちでフェラ", "", [M], [P]), 91: ("乳首責め手コキ", "", [H], [P]), 92: ("乳首責め手コキ２", "", [H], [P]),
    1000: ("寝そべって手マン", "player", [H], [V]), 1001: ("シックスナイン", "mutual", [M], [P, V]), 1002: ("陰陽のシックスナイン", "mutual", [M], [P, V]),
    1003: ("立ちクンニ", "player", [M], [V]), 1004: ("寝そべってクンニ", "player", [M], [V]), 1005: ("寝そべってアナル舐め２", "player", [M], [A]),
    1006: ("座ってアナルを指で", "player", [H], [A]), 1007: ("顔面騎乗クンニ", "player", [M], [V]), 1008: ("壁でアナル舐めと手マン", "player", [M, H], [A, V]),
    1009: ("座った彼女をクンニ", "player", [M], [V]), 1010: ("無理やりディープスロート２", "", [M], [P]), 1020: ("膝立ちディープスロート", "", [M], [P]),
    1021: ("机で胸を掴むパイズリ", "", [B], [P]), 1022: ("うつ伏せで足コキ", "", [Y], [P]), 1023: ("立ちクンニと自慰", "player", [M], [V]),
    1024: ("立ちシックスナイン", "mutual", [M], [P, V]), 1025: ("寝そべって片足コキ", "", [Y], [P]), 1026: ("騎乗の尻コキ", "", [Y], [P]),
    1027: ("寝そべって膝コキ", "", [Y], [P]), 1028: ("座って跨り素股", "", [Y], [P]), 1029: ("座って足コキ２", "", [Y], [P]),
    1030: ("机で足コキ", "", [Y], [P]), 1031: ("寝そべって足コキとフェラ", "", [Y, M], [P]),
}
SONYU = {
    31: "壁で抱え上げ（無理やり）", 32: "背面騎乗位", 33: "立ちバック", 34: "抱え上げ正常位", 35: "背面駅弁", 36: "脚を抱える正常位２",
    37: "側位３", 38: "腕を引く立ちバック", 39: "抱え上げ正常位２", 40: "まんぐり返し", 41: "腕を掴む立位", 42: "四つ脚の背面座位",
    43: "背面騎乗位２", 44: "押さえ込む騎乗位", 45: "アマゾン", 46: "押さえ込む騎乗位（アナル）", 47: "机で脚を上げて", 48: "抱き合う騎乗位",
    49: "跨ぐ後背位", 50: "椅子で正常位", 51: "後ろから抱いて突く", 52: "対面座位", 53: "開脚の背面騎乗位", 54: "机で対面座位",
    55: "しゃがむ背面座位", 56: "膝立ちの後背位", 57: "大開脚の正常位", 58: "逆アマゾン", 59: "机で脚を開いて", 60: "深いまんぐり返し",
    61: "立ったまま開脚", 62: "立ちバック２", 63: "しゃがむ座位", 64: "膝に手をつく騎乗位", 65: "髪を引く後背位（無理やり）",
    66: "腕を固めた後背位", 67: "大の字", 68: "椅子で跨る騎乗位", 77: "背面騎乗のネルソン", 78: "抱え上げネルソン",
    79: "手をつなぐ騎乗位", 80: "ブリッジ", 81: "壁で抱え上げ騎乗", 82: "脚を絡める正常位", 83: "机でバック２", 84: "伏せた後背位",
    85: "肩に脚を掛けて跨る", 86: "跨る背面騎乗位", 93: "屈曲位", 94: "正座の後ろから", 95: "立ちブリッジ", 96: "膝を抱えて",
    97: "手をつなぐ騎乗位２", 98: "背面騎乗位３", 2000: "背面騎乗位４", 2001: "重ね寝", 2002: "後ろから腕を固めて立位",
    2003: "手をつなぐ正常位", 2004: "肩に脚を掛ける正常位", 2005: "腕を掴んで後ろから", 2006: "側位２", 2007: "屈曲位２",
    2008: "机で向かい合って", 2009: "机に押しつけて", 2010: "お姫様抱っこ", 2011: "抱え上げ立ちバック", 2012: "乳首責め騎乗位",
    2013: "腰を掴む正常位", 2014: "机の上でバック", 2015: "正常位２",
}
ALIAS = {"WLoop": "WLoop1", "SLoop": "SLoop1"}           # the pace's loops: the first of each
HOUSHI_FINISH = {"M_OUT_Start": "OUT_Start", "M_OUT_Loop": "OUT_Loop"}


def manifests(original: str = ORIGINAL) -> dict:
    """(mode, StudioId) -> the manifest's Animation (kpluganim only: the AL groups)"""
    out = {}
    base = os.path.join(original, "mods", "Sideloader Modpack - Animations")
    for f in glob.glob(os.path.join(base, "**", "*.zipmod"), recursive=True):
        try:
            m = zipfile.ZipFile(f).read("manifest.xml").decode("utf-8", "replace")
        except (KeyError, OSError, zipfile.BadZipFile):
            continue
        if "<AnimationLoader>" not in m:
            continue
        root = ET.fromstring(m)
        if root.findtext("guid") != "kpluganim":
            continue
        for a in root.find("AnimationLoader").findall("Animation"):
            r = {c.tag: (c.text or "").strip() for c in a if len(list(c)) == 0}
            r["categories"] = [c.text for c in a.findall("categories/category")]
            out[(r.get("Mode"), int(r["StudioId"]))] = r
    return out


def states_of(clips: list[str], houshi: bool) -> dict:
    """base state -> "{s}<clip>" (the size set chosen by the app), with the pace's and the finish's names"""
    bases = sorted({re.sub(r"^[SML]_", "", c) for c in clips})
    st = {b: "{s}" + b for b in bases}
    for want, have in ALIAS.items():
        if have in st and want not in st:
            st[want] = st[have]
        if "A_" + have in st and "A_" + want not in st:
            st["A_" + want] = st["A_" + have]
    if houshi:
        for want, have in HOUSHI_FINISH.items():
            if have in st and want not in st:
                st[want] = st[have]
    return st


def rows(lists: dict, original_rows: list[dict]) -> tuple[list[dict], list[str]]:
    man = manifests()
    by_group = {}
    clips = {}
    for a in lists["anime"]:
        by_group.setdefault(a["group_name"], a["group"])
        clips.setdefault((a["group"], a["category"]), []).append(a["clip"])
    donors = {(r["mode"], r["motion_id"]): r for r in original_rows}
    out, missing = [], []
    for mode_name, (fname, mname) in AL_GROUPS.items():
        fg, mg = by_group.get(fname), by_group.get(mname)
        mode = 1 if mode_name == "houshi" else 2
        table = HOUSHI if mode_name == "houshi" else SONYU
        for sid in sorted(table):
            m = man.get((mode_name, sid))
            fcl, mcl = clips.get((fg, sid)), clips.get((mg, sid))
            if m is None or not fcl or not mcl:
                missing.append(f"AL {mode_name} {sid}")
                continue
            donor = donors.get((mode, int(m.get("DonorPoseId") or 0))) or donors.get((mode, 0))
            fst = states_of(fcl, mode == 1)
            fsts = {k: v for k, v in fst.items() if not k.startswith("A_")}
            msts = {k: v for k, v in states_of(mcl, mode == 1).items() if not k.startswith("A_")}
            row = {"id": f"al_{mode_name}_{sid}", "name": table[sid] if mode == 2 else table[sid][0], "name_en": m.get("AnimationName", ""),
                   "group": mode_name, "mode": mode, "motion_id": int(m.get("DonorPoseId") or 0), "source": "al", "ik": False,
                   "sizes": ["S_", "M_", "L_"],
                   "places": sorted({PLACES[c] for c in m["categories"] if c in PLACES}),
                   "female": [fg, sid, fname], "female_states": fsts, "male": [mg, sid, mname], "male_states": msts}
            for k in ("parts", "reversible", "strapon", "mutual", "lead", "targets"):
                if donor and k in donor:
                    row[k] = donor[k]
            if mode == 1:
                _, giver, gp, rp = table[sid]
                row["parts"] = {"giver": gp, "receiver": rp}
                if giver == "player":
                    row["giver"] = "player"
                elif giver == "mutual":
                    row["mutual"] = True                     # (69: each mouth on the other)
                    row["parts"] = {"giver": [M, V], "receiver": [M, P]}
                    row["targets"] = {"giver": 1.0, "receiver": 1.0}
                row.pop("lead", None)
            else:
                row["lead"] = "receiver" if m.get("IsFemaleInitiative") == "true" else row.get("lead", "giver")
                if row["lead"] == "giver":
                    row.pop("lead")
                anal = {k: v for k, v in fst.items() if k.startswith("A_")}
                if anal:
                    row["anal_female"], row["anal_female_states"] = [fg, sid, fname], anal
                    row["anal_male"], row["anal_male_states"] = [mg, sid, mname], {k: v for k, v in states_of(mcl, False).items() if k.startswith("A_")}
            out.append(row)
    return out, missing
