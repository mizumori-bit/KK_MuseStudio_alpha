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
GRADES = ("non", "near", "big")          # 0, 1, 2: how many axes of one weak combo match


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
             "献精室で弱点になる組（部位 × 主導）と、その組のプレイです。`MuseStudio/data/kensei_attributes.json` から作った表で、"
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


def grade(c, weak) -> int:
    return max(((c[0] == w[0]) + (c[1] == w[1]) for w in weak), default=0)


# ---- the limit and the coefficient ----

def fewest_turns(rules: dict, need: int, tol: int, grades=(2, 1, 0)) -> tuple[int, int]:
    """(turns, ejaculations of a fewest path) knowing the answer: fresh rises, the worst tolerance"""
    thr, worst = rules["threshold"], 1.0 - tol / 100.0
    level = {(0, 0): 0}
    for t in range(1, 200):
        nxt = {}
        for (g, tot), ej in level.items():
            for gr in grades:
                ng, nt, ne = g + rules["rise"][GRADES[gr]], tot, ej
                if ng >= thr:
                    nt += half_up(rules["base"][GRADES[gr]] * worst) + (ng - thr)
                    ng, ne = 0, ej + 1
                if nt >= need:
                    return t, ne
                key = (ng, nt)
                if key not in nxt or nxt[key] > ne:
                    nxt[key] = ne
        level = nxt
    return 999, 0


def turn_limit(rules: dict, parts: int, leads: int, weak: int, vary: bool, tighten: str, need: int, tol: int) -> int:
    t_opt, _ = fewest_turns(rules, need, tol)
    t_big, ej = fewest_turns(rules, need, tol, (2,))
    lim = rules["limit"]
    axis = (parts + leads) / weak
    a = axis * lim["per_axis_value"]
    if vary:
        a += max(0, ej - 1) * lim["per_redraw"] * axis
    a = max(1, half_up(a * lim["tighten"][tighten]))
    return max(t_opt + a, t_big)


def coefficient(rules: dict, n: int, weak: int, vary: bool, tighten: str, need: int, tol: int, disclose: str,
                decay: bool, range_random: bool) -> float:
    d = rules["difficulty"]
    p = d["points"]
    pts = max(0, n - p["candidate_over"]["from"]) * p["candidate_over"]["each"]
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
    """the settings' own check (#115 §0): K within the candidates; a full change needs 2K <= N"""
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
    def __init__(self, rules: dict, combos: list, seed: int, weak: int = 1, vary: bool = False, decay: bool = False,
                 tol: int = 0, need: int | None = None, tighten: str = "standard", disclose: str = "numbers"):
        self.r, self.combos, self.rng = rules, list(combos), Rng(seed)
        self.k, self.vary, self.decay, self.tol = weak, vary, decay, tol
        self.need = need or rules["need"]["default"]
        parts = len({c[0] for c in combos})
        leads = len({c[1] for c in combos})
        self.limit = turn_limit(rules, parts, leads, weak, vary, tighten, self.need, tol)
        self.weak = self.rng.pick(self.combos, weak)
        self.used: dict = {}
        self.gauge = self.total = 0
        self.turn = 1
        self.result = ""
        self.redraws = 0

    def step(self, c) -> dict:
        r = self.r
        gr = grade(c, self.weak)
        rise = r["rise"][GRADES[gr]]
        uses = 0
        if gr == 0 and self.decay:
            uses = self.used.get(c, 0)
            rise = max(r["decay"]["floor"], half_up(rise * r["decay"]["factor"] ** uses))
            self.used[c] = uses + 1
        before = self.gauge
        g = before + rise
        rec = {"turn": self.turn, "combo": c, "grade": gr, "rise": rise, "uses": uses, "before": before, "came": False}
        if g >= r["threshold"]:
            over = g - r["threshold"]
            factor = 1.0 + self.tol / 100.0 * (2.0 * self.rng.unit() - 1.0) if self.tol > 0 else 1.0
            amount = half_up(r["base"][GRADES[gr]] * factor) + over
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
            rest = [x for x in self.combos if x not in self.weak]
            self.weak = self.rng.pick(rest, self.k)
            self.used = {}
            self.redraws += 1
            rec["redrawn"] = True
        self.turn += 1
        return rec


# ---- the strategies ----

def play(run: Run, strategy: str, noise: float, disclose: str, rng: random.Random) -> int | None:
    combos = run.combos
    hyps = [frozenset(h) for h in itertools.combinations(combos, run.k)]
    post = {h: 1.0 for h in hyps}

    def observe(c, seen):
        for h in post:
            g = grade(c, h)
            post[h] *= (1 - noise) if seen == g else (noise / 2 if abs(seen - g) == 1 else 1e-9)
        s = sum(post.values()) or 1.0
        for h in post:
            post[h] /= s

    while not run.result:
        if strategy == "random":
            c = rng.choice(combos)
        elif strategy == "big_only":
            c = run.weak[0]
        else:
            score = {x: sum(p for h, p in post.items() if x in h) for x in combos}
            c = max(combos, key=lambda x: (score[x], -combos.index(x)))
        rec = run.step(c)
        if strategy == "search":
            seen = rec["grade"]
            if noise and rng.random() < noise:
                seen = min(2, max(0, seen + rng.choice((-1, 1))))
            observe(c, seen)
            if rec["came"] and disclose == "numbers":
                observe(c, rec["grade"])          # (the amount's band is the grade's: an exact second look)
            if rec.get("redrawn"):
                # (a redraw: what was learnt is gone; the combo that just made him come, if it read as big, is not weak now)
                for h in post:
                    post[h] = 0.0 if seen == 2 and c in h else 1.0
                s = sum(post.values()) or 1.0
                for h in post:
                    post[h] /= s
    return run.turn - 1 if run.result == "success" else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--range", choices=("all", "houshi"), default="all")
    ap.add_argument("--grid", default="")
    ap.add_argument("--runs", type=int, default=300)
    a = ap.parse_args()
    rules = load("kensei_rules.json")
    if a.grid:
        p, l = (int(x) for x in a.grid.split("x"))
        combos = [(i, j) for i in range(p) for j in range(l)]
        print(f"grid {a.grid}: {len(combos)} combos")
    else:
        cands = candidates(("houshi",) if a.range == "houshi" else ("houshi", "sonyu"))
        combos = sorted(cands)
        print(f"{len(combos)} combos from the plays:", ", ".join(f"{c[0]}x{c[1]}({len(v)})" for c, v in sorted(cands.items())))
    print(f"fewest turns to {rules['need']['default']}: {fewest_turns(rules, rules['need']['default'], 0)[0]} "
          f"(big moves alone {fewest_turns(rules, rules['need']['default'], 0, (2,))[0]})")
    rng = random.Random(5)
    print("weak vary disclose noise | search: success / mean turns | random: success | limit")
    for weak, vary in ((1, False), (1, True), (2, False)):
        if weak > len(combos) or (vary and 2 * weak > len(combos)):
            continue
        for disclose in ("numbers", "none"):
            for noise in (0.0, 0.15, 0.3):
                ok, turns, rok = 0, [], 0
                lim = 0
                for i in range(a.runs):
                    run = Run(rules, combos, 1000 + i, weak, vary, disclose=disclose)
                    lim = run.limit
                    t = play(run, "search", noise, disclose, rng)
                    if t:
                        ok += 1
                        turns.append(t)
                    if play(Run(rules, combos, 5000 + i, weak, vary), "random", 0, disclose, rng):
                        rok += 1
                print(f"{weak}    {int(vary)}    {disclose:8} {noise:4.2f}  | {ok / a.runs:5.0%} / "
                      f"{statistics.mean(turns) if turns else 0:5.1f}       | {rok / a.runs:5.0%} | {lim}")


if __name__ == "__main__":
    main()
