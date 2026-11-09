"""献精室 (#113-#115): its data and its scoring - the rules (MuseStudio/data/kensei_rules.json) and the attributes of the plays
(kensei_attributes.json) as the reference tools/kensei_sim.py reads them, and the app's own scoring (src/KenseiCore.cs, built
outside the game with this PC's .NET Framework when the SDK is here) counting the same: every play's attributes, a scripted run
(seed, moves: the big / near / non grades, the decay, the tolerance's amount, the redraws, a failure at the limit), the fewest
turns, the limits, a coefficient, the settings' own check (#115 §0: a full change needs 2K <= N). No game, no network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import glob
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(ROOT / "MuseStudio" / "tools"))
import kensei_sim as ks  # noqa: E402

V1 = json.loads((Path(__file__).resolve().parent / "kensei_rules_v1.json").read_text(encoding="utf-8"))      # (the rules of version 1 - unit combo)
COMBOS = [("手", "her"), ("口", "her"), ("口", "him"), ("胸", "her"), ("足", "her")]
MOVES = [0, 1, 2, 3, 4, 3, 3, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 0, 1]
# the scripted run (seed 12345, one weak combo, vary, decay, tolerance 10 %, need 360): turn, grade, rise, gauge after,
# amount (0: he did not come), total
SCRIPTED = [(1, "big", 45, 45, 0, 0), (2, "near", 20, 65, 0, 0), (3, "non", 8, 73, 0, 0), (4, "near", 20, 93, 0, 0),
            (5, "near", 20, 0, 72, 72), (6, "near", 20, 20, 0, 72), (7, "near", 20, 40, 0, 72), (8, "near", 20, 60, 0, 72),
            (9, "near", 20, 80, 0, 72), (10, "near", 20, 0, 59, 131), (11, "near", 20, 20, 0, 131), (12, "near", 20, 40, 0, 131),
            (13, "non", 8, 48, 0, 131), (14, "non", 6, 54, 0, 131), (15, "near", 20, 74, 0, 131), (16, "near", 20, 94, 0, 131),
            (17, "big", 45, 0, 165, 296)]
# the version 2 run (#115 P+: a weak spot is a play) - seed 4242, one weak play, vary, decay, tolerance 10 %, need 360
ITEMS2 = [("p_b1", ("口", "her")), ("p_b2", ("口", "her")), ("p_c1", ("口", "him")), ("p_a1", ("手", "her")), ("p_a2", ("手", "her")), ("p_d1", ("胸", "her"))]
MOVES2 = [2, 2, 4, 3, 5, 1, 1, 4, 4, 4, 0, 0, 0, 2, 2, 3, 3, 5, 5, 1, 1, 4, 4, 0, 2, 3, 5]
SCRIPTED2 = [(1, "non", 8, 8, 0, 0), (2, "non", 6, 14, 0, 0), (3, "combo", 30, 44, 0, 0), (4, "big", 45, 89, 0, 0), (5, "near", 20, 0, 73, 73),
             (6, "near", 20, 20, 0, 73), (7, "near", 20, 40, 0, 73), (8, "big", 45, 85, 0, 73), (9, "big", 45, 0, 145, 218), (10, "non", 8, 8, 0, 218),
             (11, "near", 20, 28, 0, 218), (12, "near", 20, 48, 0, 218), (13, "near", 20, 68, 0, 218), (14, "big", 45, 0, 125, 343), (15, "near", 20, 20, 0, 343)]


def sim_run():
    items = ks.items_of_combos(COMBOS)
    run = ks.Run(V1, items, 12345, weak=1, vary=True, decay=True, tol=10, need=360, tighten="standard")
    out = []
    for m in MOVES:
        if run.result:
            break
        out.append(run.step(items[m]))
    return V1, run, out


def sim_run2():
    rules = ks.load("kensei_rules.json")
    run = ks.Run(rules, ITEMS2, 4242, weak=1, vary=True, decay=True, tol=10, need=360, tighten="standard")
    out = []
    for m in MOVES2:
        if run.result:
            break
        out.append(run.step(ITEMS2[m]))
    return rules, run, out


class Rules(unittest.TestCase):
    def test_the_rules_hold_together(self):
        r = ks.load("kensei_rules.json")
        self.assertEqual((r["version"], r["unit"]), (2, "play"))           # (#115 P+: a weak spot is a play)
        self.assertTrue(r["rise"]["big"] > r["rise"]["combo"] > r["rise"]["near"] > r["rise"]["non"] > 0)
        self.assertTrue(r["base"]["big"] > r["base"]["combo"] > r["base"]["near"] > r["base"]["non"] > 0)
        self.assertEqual(ks.unit_of(V1), "combo")                           # (a run of version 1 goes on as it began)
        self.assertIn(r["need"]["default"], r["need"]["choices"])
        self.assertIn(r["tolerance"]["default"], r["tolerance"]["choices"])
        self.assertEqual(r["tolerance"]["default"], 0)                       # (the owner's decision: built in, 0 by default)
        below = [lv["below"] for lv in r["levels"]["progress"]]
        self.assertEqual(below, sorted(below))
        self.assertEqual(below[-1], 1.0)
        self.assertEqual(r["levels"]["amount"], [])                          # (one ejaculation's level: wait and see - not sent)
        self.assertEqual([t["min"] for t in r["titles"]], sorted(t["min"] for t in r["titles"]))
        for k in ("loose", "standard", "tight"):
            self.assertIn(k, r["limit"]["tighten"])
            self.assertIn(k, r["difficulty"]["points"]["tighten"])

    def test_the_fewest_turns(self):
        r = ks.load("kensei_rules.json")
        self.assertEqual(ks.fewest_turns(r, 360, 0), (9, 3))                 # (big alone: 465 in 9)
        self.assertEqual(ks.fewest_turns(r, 466, 0)[0], 10)                 # (GPT's example: tuned 10, big alone 12)
        self.assertEqual(ks.fewest_turns(r, 466, 0, True)[0], 12)
        self.assertEqual(ks.fewest_turns(V1, 360, 0), (9, 3))

    def test_the_settings_own_check(self):
        self.assertEqual(ks.invalid(4, 3, True, 3), "vary_needs_2k_le_n")   # (GPT's case: 4 combos, 3 weak)
        self.assertEqual(ks.invalid(4, 2, True, 3), "")                     # (2K = N: allowed - no difficulty point for it)
        self.assertEqual(ks.invalid(1, 1, True, 3), "vary_needs_2k_le_n")
        self.assertEqual(ks.invalid(0, 1, False, 3), "no_candidates")
        self.assertEqual(ks.invalid(3, 4, False, 3), "weak_out_of_range")
        r = ks.load("kensei_rules.json")
        four = ks.items_of_combos([("a", "her"), ("b", "her"), ("c", "her"), ("d", "her")])
        self.assertEqual(ks.coefficient(r, four, 2, True, "standard", 360, 0, "numbers", False, False),
                         ks.coefficient(r, four, 2, False, "standard", 360, 0, "numbers", False, False))


class Attributes(unittest.TestCase):
    def test_some_plays(self):
        plays, cats, attrs = ks.load("plays.json"), ks.load("play_categories.json"), ks.load("kensei_attributes.json")
        rows = {r["id"]: r for r in plays["plays"]}
        want = {"houshi_01": ("手", "her"), "houshi_07": ("口", "her"), "houshi_11": ("胸", "her"),
                "al_houshi_9": ("口", "him"),            # (イラマチオ: he drives it)
                "sonyu_04": ("騎乗位", "her"), "sonyu_00": ("正常位", "him"), "sonyu_02": ("後背位", "him"),
                "sonyu_09": None,                        # (座位: who drives it is not plain - left out of the first prototype)
                "al_houshi_1000": None,                  # (彼から: not for him)
                "al_houshi_1001": None,                  # (mutual)
                "houshi_15": None,                       # (パイズリ+咥え: two categories' words - mixed)
                "al_houshi_5": None,                     # (it does not work on his penis)
                "al_houshi_4": None,                     # (導かれる: who drives it is not plain)
                "al_sonyu_77": None}                     # (ネルソン)
        for pid, w in want.items():
            a = ks.attributes(rows[pid], plays, cats, attrs)
            self.assertEqual(None if a is None else (a[0], a[2]), w, pid)

    def test_the_candidates_of_the_authors_plays(self):
        self.assertEqual(sorted(ks.candidates(("houshi",))),
                         sorted([("手", "her"), ("口", "her"), ("口", "him"), ("胸", "her"), ("足", "her"), ("素股・尻", "her")]))
        both = ks.candidates(("houshi", "sonyu"))
        self.assertEqual(len(both), 11)
        self.assertIn(("騎乗位", "her"), both)
        self.assertIn(("正常位", "him"), both)


class Mirrors(unittest.TestCase):
    def test_the_play_summaries_show_the_attributes(self):
        text = (ROOT / "MUSE_GUIDE_plays.md").read_text(encoding="utf-8")
        at = text.find("## 献精室の属性")
        self.assertGreaterEqual(at, 0, "MUSE_GUIDE_plays.md has no section of the attributes")
        rest = text[at:]
        nxt = rest.find("\n## ", 3)
        section = rest if nxt < 0 else rest[:nxt + 1]
        self.assertEqual(section, ks.summary_section(), "the section drifted from kensei_attributes.json: "
                         "write ks.summary_section() into it again")


class ScriptedRun(unittest.TestCase):
    def test_the_reference_of_version_2(self):
        rules, run, recs = sim_run2()
        self.assertEqual(run.limit, 15)
        self.assertEqual(run.result, "failure")
        got = [(x["turn"], ks.GRADES[x["grade"]], x["rise"], x["after"], x.get("amount", 0), x["total"]) for x in recs]
        self.assertEqual(got, SCRIPTED2)
        self.assertEqual([x["turn"] for x in recs if x.get("redrawn")], [5, 9, 14])
        self.assertEqual(recs[1]["uses"], 1)                                 # (turn 2: the non combo's second use)

    def test_the_reference(self):
        rules, run, recs = sim_run()
        self.assertEqual(run.limit, 17)
        self.assertEqual(run.result, "failure")
        got = [(x["turn"], ks.GRADES[x["grade"]], x["rise"], x["after"], x.get("amount", 0), x["total"]) for x in recs]
        self.assertEqual(got, SCRIPTED)
        self.assertEqual([x["turn"] for x in recs if x.get("redrawn")], [5, 10])
        self.assertEqual(recs[13]["uses"], 1)                                # (turn 14: the non combo's second use under one assignment)


def csc():
    sdks = sorted(glob.glob(r"C:\Program Files\dotnet\sdk\*\Roslyn\bincore\csc.dll"))
    fw = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET" / "Framework64" / "v4.0.30319"
    if not sdks or not (fw / "mscorlib.dll").is_file():
        return None, None
    return ["dotnet", sdks[-1]], fw


class AppCore(unittest.TestCase):
    """src/KenseiCore.cs counts as the reference does"""

    @classmethod
    def setUpClass(cls):
        cmd, fw = csc()
        if cmd is None:
            raise unittest.SkipTest("no .NET SDK compiler or .NET Framework here")
        tmp = Path(tempfile.mkdtemp(prefix="kensei_core_"))
        exe = tmp / "kensei_core.exe"
        src = ROOT / "MuseStudio"
        r = subprocess.run(cmd + ["-nologo", "-noconfig", "-nostdlib+", "-target:exe", "-langversion:7.3", f"-out:{exe}",
                                  f"-r:{fw / 'mscorlib.dll'}", f"-r:{fw / 'System.dll'}", f"-r:{fw / 'System.Core.dll'}",
                                  str(src / "src" / "KenseiCore.cs"), str(src / "src" / "Json.cs"), str(src / "src" / "WholeFile.cs"),
                                  str(src / "tools" / "kensei_core_driver.cs")],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if r.returncode != 0:
            raise AssertionError("KenseiCore did not build: " + r.stdout + r.stderr)
        work = tmp / "files"
        work.mkdir()
        out = subprocess.run([str(exe), str(src / "data"), str(work), str(Path(__file__).resolve().parent / "kensei_rules_v1.json")], capture_output=True, timeout=60)
        if out.returncode != 0:
            raise AssertionError("KenseiCore's driver failed: " + out.stderr.decode("utf-8", "replace"))
        cls.doc = json.loads(out.stdout.decode("utf-8"))

    def test_every_plays_attributes(self):
        plays, cats, attrs = ks.load("plays.json"), ks.load("play_categories.json"), ks.load("kensei_attributes.json")
        for row in plays["plays"]:
            if row["group"] not in ("houshi", "sonyu"):
                continue
            a = ks.attributes(row, plays, cats, attrs)
            self.assertEqual(self.doc["attributes"][row["id"]], None if a is None else f"{a[0]}/{a[2]}", row["id"])

    def test_the_scripted_run(self):
        rules, run, recs = sim_run()
        self.assertEqual(self.doc["limit"], run.limit)
        self.assertEqual(self.doc["result"], run.result)
        self.assertEqual(self.doc["weak_first"], "手/her")
        got = [(t["turn"], t["grade"], t["rise"], t["gauge_after"], t.get("amount", 0), t["total"]) for t in self.doc["turns"]]
        self.assertEqual(got, SCRIPTED)
        self.assertEqual([t["turn"] for t in self.doc["turns"] if t.get("redrawn")], [5, 10])
        for t, x in zip(self.doc["turns"], recs):
            self.assertEqual((t["decay_uses"], t["gauge_before"], t["came"]), (x["uses"], x["before"], x["came"]))
            if x["came"]:
                self.assertEqual(t["overkill"], x["over"])

    def test_the_scripted_run_of_version_2(self):
        rules, run, recs = sim_run2()
        self.assertEqual(self.doc["limit2"], run.limit)
        self.assertEqual(self.doc["result2"], run.result)
        self.assertEqual(self.doc["weak2_first"], ["p_a1"])
        got = [(t["turn"], t["grade"], t["rise"], t["gauge_after"], t.get("amount", 0), t["total"]) for t in self.doc["turns2"]]
        self.assertEqual(got, SCRIPTED2)
        self.assertEqual([t["turn"] for t in self.doc["turns2"] if t.get("redrawn")], [5, 9, 14])
        self.assertEqual([t["item"] for t in self.doc["turns2"]], [ITEMS2[m][0] for m in MOVES2[:len(SCRIPTED2)]])

    def test_a_file_written_whole(self):
        # (GPT's re-review of #119, 2) true once the new text is in place: a new file, over a read-only one (no leftover),
        # with an old .bak held open
        self.assertEqual(self.doc["wholefile"], [True, True, True])

    def test_the_counts(self):
        rules = ks.load("kensei_rules.json")
        f360, f466, f360t, big = ks.fewest_turns(rules, 360, 0)[0], ks.fewest_turns(rules, 466, 0)[0], ks.fewest_turns(rules, 360, 10)[0], ks.fewest_turns(rules, 360, 0, (2,))
        self.assertEqual(self.doc["fewest"], [f360, f466, f360t, big[0], big[1]])

        def shaped(parts, leads, most):
            # (items of that shape: parts x leads combos, the first combo with `most` plays)
            out = []
            for i in range(parts):
                for j in range(leads):
                    for n in range(most if (i, j) == (0, 0) else 1):
                        out.append((f"x{i}{j}{n}", (f"P{i}", f"L{j}")))
            return out
        self.assertEqual(self.doc["limits"], [ks.turn_limit(rules, shaped(10, 2, 26), 1, False, "standard", 360, 0),
                                              ks.turn_limit(rules, shaped(10, 2, 26), 1, True, "standard", 360, 0),
                                              ks.turn_limit(rules, shaped(5, 2, 17), 2, False, "tight", 360, 0),
                                              ks.turn_limit(rules, shaped(5, 2, 17), 1, False, "loose", 480, 10),
                                              ks.turn_limit(V1, shaped(10, 2, 1), 1, False, "standard", 360, 0),
                                              ks.turn_limit(V1, shaped(10, 2, 1), 1, True, "standard", 360, 0)])
        six = [("p%d" % i, (("口", "her") if i < 2 else ("胸", "her"))) for i in range(6)]
        five = [(c, (c, "her")) for c in "abcde"]
        self.assertAlmostEqual(self.doc["coefficient"], ks.coefficient(rules, [(f"q{i}", (f"c{i}", "her")) for i in range(5)] + [("q5", ("c0", "her"))],
                                                                       1, True, "standard", 360, 10, "numbers", True, False))
        self.assertAlmostEqual(self.doc["coefficient2"], ks.coefficient(rules, [(f"r{i}", (f"c{i % 11}", "her")) for i in range(156)],
                                                                        1, False, "standard", 360, 0, "numbers", False, False))
        self.assertEqual(self.doc["invalid"], [ks.invalid(4, 3, True, 3), ks.invalid(4, 2, True, 3), ks.invalid(1, 1, True, 3),
                                               ks.invalid(0, 1, False, 3), ks.invalid(3, 4, False, 3)])
        self.assertEqual(self.doc["progress"], [ks.progress(rules, 0, 360), ks.progress(rules, 130, 360), ks.progress(rules, 300, 360)])
        self.assertEqual(self.doc["title"], [ks.title(rules, 299), ks.title(rules, 300), ks.title(rules, 1500)])


if __name__ == "__main__":
    unittest.main()
