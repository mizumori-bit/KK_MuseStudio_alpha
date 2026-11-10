"""献精室 (#113-#115): the reference of the scoring and an offline trial of the numbers - the same data the app reads
(MuseStudio/data/kensei_rules.json, kensei_attributes.json, plays.json, play_categories.json), the same draws (xorshift32 from
the run's seed) and the same order as the app's KenseiCore.cs, so a run with the same seed and moves comes out the same in both
(bridge/tests/test_kensei_data.py and the game's checks hold the same numbers).

  python -B MuseStudio/tools/kensei_sim.py                 the strategies compared (the author's candidates: every play)
  python -B MuseStudio/tools/kensei_sim.py --range houshi  only the service plays as the range
  python -B MuseStudio/tools/kensei_sim.py --grid 4x2      a made-up full grid of parts x leads instead

The strategies: big_only (knowing the answer, the big combo every turn), search (Muse's guess from the user's answers - the
grade of each turn's rise, misread by one with the chance --noise; in the numbers mode an ejaculation's amount tells its grade
too), random. No game, no network."""
from __future__ import annotations

import argparse
import itertools
import json
import math
import random
import statistics
from pathlib import Path

DATA = Path(__file__).resolve().parent.parent / "data"
GRADES = ("non", "near", "combo", "big")   # 0..3: none / one axis / the same combo, another play (play unit) / the weak itself


def load(name: str) -> dict:
    return json.loads((DATA / name).read_text(encoding="utf-8"))


def half_up(x: float) -> int:
    return int(math.floor(x + 0.5))


# ---- the attributes of a play (kensei_attributes.json) ----

def category(row: dict, cats: dict) -> str:
    giver = row.get("giver", "") + ("mutual" if "mutual" in row else "")
    for rule in cats.get(row["group"], []):
        if any(g in giver for g in rule.get("giver", [])) or any(w in row["name"] for w in rule.get("words", [])):
            return rule["name"]
    return "その他"


def attributes(row: dict, plays: dict, cats: dict, attrs: dict):
    """(part, lead_side, who) or None (left out); who: her / him - the one who drives it"""
    g = attrs["groups"].get(row["group"])
    if g is None or "giver" in row or "mutual" in row:
        return None
    name = row["name"]
    ov = attrs.get("overrides", {})
    if row["id"] in ov:
        o = ov[row["id"]]
        if o is None:
            return None
        part, side = o["part"], o["lead"]
    else:
        cat = category(row, cats)
        if cat not in g["parts"]:
            return None
        part = g["parts"][cat]
        side = g["lead"]
        if any(w in name for w in g.get("receiver_words", [])) or cat in g.get("receiver_categories", []):
            side = "receiver"
    if any(w in name for w in g.get("exclude_words", [])):
        return None
    if attrs.get("exclude_mixed"):
        hits = {r["name"] for r in cats.get(row["group"], []) if any(w in name for w in r.get("words", []))}
        if len(hits) > 1:
            return None
    need = g.get("require_receiver_part")
    if need and need not in row.get("parts", {}).get("receiver", []):
        return None
    giver_is = plays["groups"][row["group"]]["giver"]           # character / player
    her_gives = giver_is == "character"
    who = "her" if (side == "giver") == her_gives else "him"
    return part, side, who


def candidates(group_range=("houshi", "sonyu")) -> dict:
    """combo (part, who) -> [play ids] over every play of the range (the author's plays.json: all of them, mods too)"""
    plays, cats, attrs = load("plays.json"), load("play_categories.json"), load("kensei_attributes.json")
    out: dict = {}
    for r in plays["plays"]:
        if r["group"] not in group_range:
            continue
        a = attributes(r, plays, cats, attrs)
        if a is None:
            continue
        out.setdefault((a[0], a[2]), []).append(r["id"])
    return out


def summary_section() -> str:
    """MUSE_GUIDE_plays.md's section of the attributes (bridge/tests/test_kensei_data.py holds the file to it)"""
    cands = candidates(("houshi", "sonyu"))
    who = {"her": "彼女", "him": "ユーザー"}
    lines = ["## 献精室の属性（契約 第 3.15 節）", "",
             "献精室の候補になるプレイが持つ属性の組（部位 × 主導）と、その組のプレイです。弱点は、この中からプレイ単位で引かれます"
             "（版 1.42。同じ組の別のプレイは「同じ組」の段）。`MuseStudio/data/kensei_attributes.json` から作った表で、"
             "カタログのプレイの `kensei` と同じです（ここに無いプレイは候補になりません）。実際の候補は、ランを始めた部屋で始められるものだけです。",
             "", "| 組 | 部位 | 主導 | プレイ |", "|---|---|---|---|"]
    for (part, w), ids in sorted(cands.items()):
        lines.append(f"| `{part}/{w}` | {part} | {who[w]} | " + "、".join(f"`{i}`" for i in ids) + " |")
    return "\n".join(lines) + "\n"


# ---- the draws ----

class Rng:
    """xorshift32 (the app's KenseiRng): the same seed, the same draws"""

    def __init__(self, seed: int):
        self.s = seed & 0xFFFFFFFF or 2463534242

    def next(self) -> int:
        s = self.s
        s ^= (s << 13) & 0xFFFFFFFF
        s ^= s >> 17
        s ^= (s << 5) & 0xFFFFFFFF
        self.s = s
        return s

    def below(self, n: int) -> int:
        return self.next() % n

    def unit(self) -> float:
        return self.next() / 4294967296.0

    def pick(self, items: list, k: int) -> list:
        a = list(items)
        for i in range(k):
            j = i + self.below(len(a) - i)
            a[i], a[j] = a[j], a[i]
        return a[:k]


def unit_of(rules: dict) -> str:
    """what a weak spot is: "combo" (version 1 - a part and lead) or "play" (version 2, #115 P+ - one play)"""
    return rules.get("unit", "combo")


def rise_of(rules: dict, g: int) -> int:
    return int(rules["rise"].get(GRADES[g], 0))


def base_of(rules: dict, g: int) -> int:
    return int(rules["base"].get(GRADES[g], 0))


def combo_key(c) -> str:
    return f"{c[0]}/{c[1]}"


def items_of_combos(combos) -> list:
    """the items of a combo-unit run (version 1): each combo its own item"""
    return [(combo_key(c), tuple(c)) for c in combos]


def items_of_plays(cands: dict) -> list:
    """the items of a play-unit run (version 2): every play, with its combo - sorted as the app sorts them"""
    out = [(pid, tuple(c)) for c, ids in cands.items() for pid in ids]
    return sorted(out, key=lambda x: (x[1][0], x[1][1], x[0]))


def grade(item, weak) -> int:
    """3 the weak item itself (a combo-unit run: its combo), 2 the same combo - another play (play unit), 1 one axis, 0 none;
    each weak item judged on its own, the highest kept"""
    best = 0
    for w in weak:
        if item[0] == w[0]:
            return 3
        m = (item[1][0] == w[1][0]) + (item[1][1] == w[1][1])
        best = max(best, 2 if m == 2 else m)
    return best


# ---- the limit and the coefficient ----

def fewest_turns(rules: dict, need: int, tol: int, big_only: bool = False) -> tuple[int, int]:
    """(turns, ejaculations of a fewest path) knowing the answer: fresh rises, the worst tolerance"""
    thr, worst = rules["threshold"], 1.0 - tol / 100.0
    grades = [3] if big_only else [g for g in (3, 2, 1, 0) if rise_of(rules, g) > 0]
    level = {(0, 0): 0}
    for t in range(1, 200):
        nxt = {}
        for (g, tot), ej in level.items():
            for gr in grades:
                ng, nt, ne = g + rise_of(rules, gr), tot, ej
                if ng >= thr:
                    nt += half_up(base_of(rules, gr) * worst) + (ng - thr)
                    ng, ne = 0, ej + 1
                if nt >= need:
                    return t, ne
                key = (ng, nt)
                if key not in nxt or nxt[key] > ne:
                    nxt[key] = ne
        level = nxt
    return 999, 0


def shape(items) -> tuple[int, int, int, int]:
    """(parts, leads, combos, the most plays in one combo)"""
    combos = {}
    for _, c in items:
        combos[c] = combos.get(c, 0) + 1
    return len({c[0] for c in combos}), len({c[1] for c in combos}), len(combos), max(combos.values(), default=0)


def turn_limit(rules: dict, items, weak: int, vary: bool, tighten: str, need: int, tol: int) -> int:
    t_opt, _ = fewest_turns(rules, need, tol)
    t_big, ej = fewest_turns(rules, need, tol, True)
    lim = rules["limit"]
    parts, leads, _, most = shape(items)
    span = (parts + leads) / weak
    a = span * lim["per_axis_value"]
    plays = float(most) if unit_of(rules) == "play" else 0.0      # (the plays of the largest combo, tried one by one: not split by the weak count)
    a += plays * lim.get("per_play", 0.0)
    if vary:
        a += max(0, ej - 1) * lim["per_redraw"] * (span + plays)
    a = max(1, half_up(a * lim["tighten"][tighten]))
    return max(t_opt + a, t_big)


def coefficient(rules: dict, items, weak: int, vary: bool, tighten: str, need: int, tol: int, disclose: str,
                decay: bool, range_random: bool) -> float:
    d = rules["difficulty"]
    p = d["points"]
    _, _, combos, _ = shape(items)
    n = len(items)
    pts = max(0, combos - p["candidate_over"]["from"]) * p["candidate_over"]["each"]
    ps = p.get("plays_step")
    if ps and unit_of(rules) == "play":
        pts += max(0, n - ps["from"]) // ps["each"] * ps["points"]
    pts += p["range_random"] if range_random else 0
    pts += (need - p["need_step"]["from"]) // p["need_step"]["each"] * p["need_step"]["points"]
    pts += p["tighten"][tighten]
    pts += p["vary"] if vary and 2 * weak < n else 0
    pts += p["disclose"][disclose]
    pts += tol // 5 * p["tolerance_each_5"]
    pts += (weak - 1) * p["weak_extra"]
    pts += p.get("decay", 0) if decay else 0
    return max(d["min"], round(1 + d["per_point"] * pts, 3))


def invalid(n: int, weak: int, vary: bool, weak_max: int) -> str:
    """the settings' own check (#115 §0): K within the candidates (combos or plays: the run's unit); a full change needs 2K <= N"""
    if n < 1:
        return "no_candidates"
    if weak < 1 or weak > weak_max or weak > n:
        return "weak_out_of_range"
    if vary and 2 * weak > n:
        return "vary_needs_2k_le_n"
    return ""


def progress(rules: dict, total: int, need: int) -> str:
    share = total / need if need > 0 else 1.0
    levels = rules["levels"]["progress"]
    for lv in levels:
        if share < lv["below"]:
            return lv["key"]
    return levels[-1]["key"]


def title(rules: dict, score: int) -> str:
    t = ""
    for x in rules["titles"]:
        if score >= x["min"]:
            t = x["name"]
    return t


# ---- one run ----

class Run:
    """items: [(id, (part, who))] - the combos of a combo-unit run (items_of_combos), the plays of a play-unit one"""

    def __init__(self, rules: dict, items: list, seed: int, weak: int = 1, vary: bool = False, decay: bool = False,
                 tol: int = 0, need: int | None = None, tighten: str = "standard", disclose: str = "numbers"):
        self.r, self.items, self.rng = rules, list(items), Rng(seed)
        self.k, self.vary, self.decay, self.tol = weak, vary, decay, tol
        self.need = need or rules["need"]["default"]
        self.limit = turn_limit(rules, self.items, weak, vary, tighten, self.need, tol)
        self.weak = self.rng.pick(self.items, weak)
        self.used: dict = {}
        self.gauge = self.total = 0
        self.turn = 1
        self.result = ""
        self.redraws = 0

    def step(self, item) -> dict:
        r = self.r
        gr = grade(item, self.weak)
        rise = rise_of(r, gr)
        uses = 0
        ck = combo_key(item[1])
        if gr == 0 and self.decay:
            uses = self.used.get(ck, 0)
            rise = max(r["decay"]["floor"], half_up(rise * r["decay"]["factor"] ** uses))
            self.used[ck] = uses + 1
        before = self.gauge
        g = before + rise
        rec = {"turn": self.turn, "item": item[0], "combo": item[1], "grade": gr, "rise": rise, "uses": uses, "before": before, "came": False}
        if g >= r["threshold"]:
            over = g - r["threshold"]
            factor = 1.0 + self.tol / 100.0 * (2.0 * self.rng.unit() - 1.0) if self.tol > 0 else 1.0
            amount = half_up(base_of(r, gr) * factor) + over
            self.total += amount
            self.gauge = 0
            rec.update(came=True, amount=amount, over=over)
        else:
            self.gauge = g
        rec["after"], rec["total"] = self.gauge, self.total
        if self.total >= self.need:
            self.result = "success"
        elif self.turn >= self.limit:
            self.result = "failure"
        if rec["came"] and not self.result and self.vary:
            rest = [x for x in self.items if x not in self.weak]
            self.weak = self.rng.pick(rest, self.k)
            self.used = {}
            self.redraws += 1
            rec["redrawn"] = True
        self.turn += 1
        return rec


# ---- the strategies ----

def play(run: Run, strategy: str, noise: float, disclose: str, rng: random.Random) -> int | None:
    items = run.items
    hyps = [frozenset(h) for h in itertools.combinations(items, run.k)]
    post = {h: 1.0 for h in hyps}

    def observe(c, seen):
        for h in post:
            if post[h] == 0.0:
                continue
            g = grade(c, h)
            post[h] *= (1 - noise) if seen == g else (noise / 2 if abs(seen - g) == 1 else 1e-9)
        s = sum(post.values()) or 1.0
        for h in post:
            post[h] /= s

    while not run.result:
        if strategy == "random":
            c = rng.choice(items)
        elif strategy == "big_only":
            c = run.weak[0]
        else:
            score = {}
            for h, p in post.items():
                if p:
                    for x in h:
                        score[x] = score.get(x, 0.0) + p
            c = max(items, key=lambda x: (score.get(x, 0.0), -items.index(x)))
        rec = run.step(c)
        if strategy == "search":
            seen = rec["grade"]
            if noise and rng.random() < noise:
                seen = min(3, max(0, seen + rng.choice((-1, 1))))
            observe(c, seen)
            if rec["came"] and disclose == "numbers":
                observe(c, rec["grade"])          # (the amount's band is the grade's: an exact second look)
            if rec.get("redrawn"):
                # (a redraw: what was learnt is gone; the item that just made him come, if it read as big, is not weak now)
                for h in post:
                    post[h] = 0.0 if seen == 3 and c in h else 1.0
                s = sum(post.values()) or 1.0
                for h in post:
                    post[h] /= s
    return run.turn - 1 if run.result == "success" else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--range", choices=("all", "houshi"), default="all")
    ap.add_argument("--grid", default="")
    ap.add_argument("--runs", type=int, default=300)
    ap.add_argument("--rules", default="", help="another rules file (e.g. bridge/tests/kensei_rules_v1.json)")
    a = ap.parse_args()
    rules = json.loads(Path(a.rules).read_text(encoding="utf-8")) if a.rules else load("kensei_rules.json")
    if a.grid:
        p, l = (int(x) for x in a.grid.split("x"))
        items = items_of_combos([(i, j) for i in range(p) for j in range(l)])
        print(f"grid {a.grid}: {len(items)} combos")
    else:
        cands = candidates(("houshi",) if a.range == "houshi" else ("houshi", "sonyu"))
        items = items_of_plays(cands) if unit_of(rules) == "play" else items_of_combos(sorted(cands))
        print(f"unit {unit_of(rules)}: {len(items)} items from", ", ".join(f"{c[0]}x{c[1]}({len(v)})" for c, v in sorted(cands.items())))
    need = rules["need"]["default"]
    print(f"fewest turns to {need}: {fewest_turns(rules, need, 0)[0]} (big moves alone {fewest_turns(rules, need, 0, True)[0]})")
    rng = random.Random(5)
    print("weak vary disclose noise | search: success / mean turns | random: success | limit")
    for weak, vary in ((1, False), (1, True), (2, False)):
        if weak > len(items) or (vary and 2 * weak > len(items)):
            continue
        for disclose in ("numbers", "none"):
            for noise in (0.0, 0.15, 0.3):
                ok, turns, rok = 0, [], 0
                lim = 0
                for i in range(a.runs):
                    run = Run(rules, items, 1000 + i, weak, vary, disclose=disclose)
                    lim = run.limit
                    t = play(run, "search", noise, disclose, rng)
                    if t:
                        ok += 1
                        turns.append(t)
                    if play(Run(rules, items, 5000 + i, weak, vary), "random", 0, disclose, rng):
                        rok += 1
                print(f"{weak}    {int(vary)}    {disclose:8} {noise:4.2f}  | {ok / a.runs:5.0%} / "
                      f"{statistics.mean(turns) if turns else 0:5.1f}       | {rok / a.runs:5.0%} | {lim}")


if __name__ == "__main__":
    main()
