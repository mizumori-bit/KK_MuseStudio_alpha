// 献精室: the app's scoring (src/KenseiCore.cs) run outside the game - compiled with src/Json.cs by
// bridge/tests/test_kensei_data.py against this PC's .NET Framework, so its numbers are checked against tools/kensei_sim.py
// (the same data, seed and moves). Prints one JSON object: every play's attributes, the scripted run's turns, the limits and
// a coefficient. Not part of the plugin (it is not in src/).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MuseStudio
{
    public static class KenseiCoreDriver
    {
        static Dictionary<string, object> Load(string dir, string name)
        {
            return (Dictionary<string, object>)Json.Parse(File.ReadAllText(Path.Combine(dir, name), Encoding.UTF8));
        }

        // PlayCtl.Category's rule: the group's first rule that fits (its giver, else a word of its name); else その他
        static string Category(Dictionary<string, object> row, Dictionary<string, object> cats)
        {
            string name = Json.Str(row, "name", ""), giver = Json.Str(row, "giver", "") + (Json.Has(row, "mutual") ? "mutual" : "");
            foreach (var rule in Json.Arr(cats, Json.Str(row, "group", "")) ?? new List<object>())
            {
                bool hit = false;
                foreach (var g in Json.Arr(rule, "giver") ?? new List<object>()) if (giver.Contains((string)g)) hit = true;
                foreach (var w in Json.Arr(rule, "words") ?? new List<object>()) if (name.Contains((string)w)) hit = true;
                if (hit) return Json.Str(rule, "name", "その他");
            }
            return "その他";
        }

        public static int Main(string[] args)
        {
            string dir = args[0];
            var plays = Load(dir, "plays.json");
            var cats = Load(dir, "play_categories.json");
            var attrs = Load(dir, "kensei_attributes.json");
            var rules = new KenseiRules(Load(dir, "kensei_rules.json"));
            var outAttrs = new Dictionary<string, object>();
            foreach (var o in Json.Arr(plays, "plays"))
            {
                var row = (Dictionary<string, object>)o;
                string group = Json.Str(row, "group", "");
                if (group != "houshi" && group != "sonyu") continue;
                bool herGives = Json.Str(Json.Obj(Json.Obj(plays, "groups"), group), "giver", "") == "character";
                var c = KenseiAttributes.Of(row, Category(row, cats), herGives, attrs, cats);
                outAttrs[Json.Str(row, "id")] = c.HasValue ? (object)c.Value.Key : null;
            }
            var combos = new List<KenseiCombo> { new KenseiCombo("手", "her"), new KenseiCombo("口", "her"), new KenseiCombo("口", "him"),
                new KenseiCombo("胸", "her"), new KenseiCombo("足", "her") };
            var run = new KenseiRun(rules, combos, 12345u, 1, true, true, 10, 360, "standard");
            int[] moves = { 0, 1, 2, 3, 4, 3, 3, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 0, 1 };
            var turns = new List<object>();
            var weakFirst = run.WeakNow[0].Key;
            foreach (int m in moves)
            {
                if (run.Result.Length > 0) break;
                var t = run.Step(combos[m], "p" + m, "r" + run.Turn);
                turns.Add(t.Truth());
            }
            int ej;
            var fewest = new List<object> { rules.Fewest(360, 0, false, out ej), rules.Fewest(466, 0, false, out ej), rules.Fewest(360, 10, false, out ej),
                rules.Fewest(360, 0, true, out ej), ej };
            int pts;
            double coef = rules.Coefficient(5, 1, true, "standard", 360, 10, "numbers", true, false, out pts);
            var result = new Dictionary<string, object> { { "attributes", outAttrs }, { "limit", run.Limit }, { "weak_first", weakFirst }, { "turns", turns },
                { "result", run.Result }, { "fewest", fewest }, { "coefficient", coef }, { "points", pts },
                { "limits", new List<object> { rules.Limit(10, 2, 1, false, "standard", 360, 0), rules.Limit(10, 2, 1, true, "standard", 360, 0),
                                               rules.Limit(5, 2, 2, false, "tight", 360, 0), rules.Limit(5, 2, 1, false, "loose", 480, 10) } },
                { "invalid", new List<object> { KenseiRun.Invalid(4, 3, true, 3), KenseiRun.Invalid(4, 2, true, 3), KenseiRun.Invalid(1, 1, true, 3),
                                                KenseiRun.Invalid(0, 1, false, 3), KenseiRun.Invalid(3, 4, false, 3) } },
                { "progress", new List<object> { Json.Str(rules.Progress(0, 360), "key"), Json.Str(rules.Progress(130, 360), "key"), Json.Str(rules.Progress(300, 360), "key") } },
                { "title", new List<object> { rules.Title(299), rules.Title(300), rules.Title(1500) } } };
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            stdout.Write(Json.Write(result));
            stdout.Flush();
            return 0;
        }
    }
}
