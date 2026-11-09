using System;
using System.Collections.Generic;

namespace MuseStudio
{
    // 献精室 (#113-#115, the owner's decisions there; the design of #115): its scoring, apart from the game - the rules
    // (data/kensei_rules.json), a play's attributes (data/kensei_attributes.json), the draws (xorshift32 from the run's seed)
    // and a run's turns. tools/kensei_sim.py does the same in the same order (the same seed and moves come out the same:
    // bridge/tests/test_kensei_data.py and the game's checks hold the same numbers). Kensei (the content in the app) holds a
    // run of these and tells Muse only what its disclosure lets out.

    public class KenseiRules
    {
        public readonly Dictionary<string, object> Doc;
        public readonly int Version, Threshold, NeedDefault, WeakDefault, WeakMax, DecayFloor, TolDefault;
        // by grade: 0 non, 1 near (one axis), 2 combo (the same combo, another play - the play unit only), 3 big (the weak itself)
        public readonly int[] Rise = new int[4], Base = new int[4];
        public readonly double DecayFactor, PerAxis, PerPlay, PerRedraw;
        public readonly string Unit;           // what a weak spot is: "play" (version 2, #115 P+) / "combo" (version 1)
        public readonly List<int> NeedChoices = new List<int>(), TolChoices = new List<int>();
        public static readonly string[] Grades = { "non", "near", "combo", "big" };

        public KenseiRules(Dictionary<string, object> doc)
        {
            Doc = doc ?? new Dictionary<string, object>();
            Version = (int)Json.Num(Doc, "version", 0);
            Unit = Json.Str(Doc, "unit", "combo");
            Threshold = (int)Json.Num(Doc, "threshold", 100);
            for (int g = 0; g < 4; g++)
            {
                Rise[g] = (int)Json.Num(Json.Obj(Doc, "rise"), Grades[g], 0);
                Base[g] = (int)Json.Num(Json.Obj(Doc, "base"), Grades[g], 0);
            }
            var need = Json.Obj(Doc, "need");
            NeedDefault = (int)Json.Num(need, "default", 360);
            foreach (var o in Json.Arr(need, "choices") ?? new List<object>()) NeedChoices.Add(Convert.ToInt32(o));
            WeakDefault = (int)Json.Num(Json.Obj(Doc, "weak"), "default", 1);
            WeakMax = (int)Json.Num(Json.Obj(Doc, "weak"), "max", 3);
            DecayFactor = Json.Num(Json.Obj(Doc, "decay"), "factor", 0.7);
            DecayFloor = (int)Json.Num(Json.Obj(Doc, "decay"), "floor", 2);
            var tol = Json.Obj(Doc, "tolerance");
            TolDefault = (int)Json.Num(tol, "default", 0);
            foreach (var o in Json.Arr(tol, "choices") ?? new List<object>()) TolChoices.Add(Convert.ToInt32(o));
            var lim = Json.Obj(Doc, "limit");
            PerAxis = Json.Num(lim, "per_axis_value", 1.0);
            PerPlay = Json.Num(lim, "per_play", 0.0);
            PerRedraw = Json.Num(lim, "per_redraw", 0.5);
        }

        public double Tighten(string t) { return Json.Num(Json.Obj(Json.Obj(Doc, "limit"), "tighten"), t, 1.0); }

        public static int HalfUp(double x) { return (int)Math.Floor(x + 0.5); }

        // the fewest turns to the need knowing the answer (fresh rises, the worst tolerance), and that path's ejaculations
        public int Fewest(int need, int tol, bool bigOnly, out int ejaculations)
        {
            double worst = 1.0 - tol / 100.0;
            var level = new Dictionary<long, int> { { 0L, 0 } };      // (gauge, total) -> the fewest ejaculations there
            ejaculations = 0;
            for (int t = 1; t < 200; t++)
            {
                var next = new Dictionary<long, int>();
                foreach (var kv in level)
                {
                    int g = (int)(kv.Key >> 32), tot = (int)(kv.Key & 0xFFFFFFFF), ej = kv.Value;
                    for (int gr = 3; gr >= (bigOnly ? 3 : 0); gr--)
                    {
                        if (Rise[gr] <= 0) continue;           // (a grade the rules have not: version 1's combo)
                        int ng = g + Rise[gr], nt = tot, ne = ej;
                        if (ng >= Threshold)
                        {
                            nt += HalfUp(Base[gr] * worst) + (ng - Threshold);
                            ng = 0;
                            ne = ej + 1;
                        }
                        if (nt >= need) { ejaculations = ne; return t; }
                        long key = ((long)ng << 32) | (uint)nt;
                        int was;
                        if (!next.TryGetValue(key, out was) || was > ne) next[key] = ne;
                    }
                }
                level = next;
            }
            return 999;
        }

        // the turn limit: the fewest + the search allowance (#115 §11; version 2: the plays of the largest combo too - tried one
        // by one, not split by the weak count), never below what big moves alone take
        public int Limit(int parts, int leads, int most, int weak, bool vary, string tighten, int need, int tol)
        {
            int ej;
            int opt = Fewest(need, tol, false, out ej);
            int big = Fewest(need, tol, true, out ej);
            double span = (parts + leads) / (double)Math.Max(1, weak);
            double plays = Unit == "play" ? most : 0.0;
            double a = span * PerAxis + plays * PerPlay;
            if (vary) a += Math.Max(0, ej - 1) * PerRedraw * (span + plays);
            int allow = Math.Max(1, HalfUp(a * Tighten(tighten)));
            return Math.Max(opt + allow, big);
        }

        // the difficulty coefficient of a run's settings (#115 §3), and its points
        // (combos: the candidates' combos; n: the items - the combos, or the plays of a play-unit run)
        public double Coefficient(int combos, int n, int weak, bool vary, string tighten, int need, int tol, string disclose, bool decay, bool rangeRandom, out int points)
        {
            var d = Json.Obj(Doc, "difficulty");
            var p = Json.Obj(d, "points");
            var co = Json.Obj(p, "candidate_over");
            int pts = Math.Max(0, combos - (int)Json.Num(co, "from", 6)) * (int)Json.Num(co, "each", 1);
            var ps = Json.Obj(p, "plays_step");
            if (ps != null && Unit == "play")
                pts += Math.Max(0, n - (int)Json.Num(ps, "from", 20)) / Math.Max(1, (int)Json.Num(ps, "each", 20)) * (int)Json.Num(ps, "points", 1);
            if (rangeRandom) pts += (int)Json.Num(p, "range_random", 0);
            var ns = Json.Obj(p, "need_step");
            pts += (int)Math.Floor((need - Json.Num(ns, "from", 360)) / Json.Num(ns, "each", 120)) * (int)Json.Num(ns, "points", 1);
            pts += (int)Json.Num(Json.Obj(p, "tighten"), tighten, 0);
            if (vary && 2 * weak < n) pts += (int)Json.Num(p, "vary", 0);           // (2K = N: the next weak is the only set left - no harder)
            pts += (int)Json.Num(Json.Obj(p, "disclose"), disclose, 0);
            pts += tol / 5 * (int)Json.Num(p, "tolerance_each_5", 0);
            pts += (weak - 1) * (int)Json.Num(p, "weak_extra", 0);
            if (decay) pts += (int)Json.Num(p, "decay", 0);
            points = pts;
            return Math.Max(Json.Num(d, "min", 0.5), Math.Round(1 + Json.Num(d, "per_point", 0.1) * pts, 3));
        }

        public string Title(int score)
        {
            string t = "";
            foreach (var o in Json.Arr(Doc, "titles") ?? new List<object>())
                if (score >= Json.Num(o, "min", 1e9)) t = Json.Str(o, "name", t);
            return t;
        }

        // the progress toward the need as a level (the levels mode): its key and name
        public Dictionary<string, object> Progress(int total, int need)
        {
            double share = need > 0 ? total / (double)need : 1.0;
            Dictionary<string, object> last = null;
            foreach (var o in Json.Arr(Json.Obj(Doc, "levels"), "progress") ?? new List<object>())
            {
                last = o as Dictionary<string, object>;
                if (share < Json.Num(o, "below", 1.0)) return new Dictionary<string, object> { { "key", Json.Str(o, "key") }, { "name", Json.Str(o, "name") } };
            }
            return last != null ? new Dictionary<string, object> { { "key", Json.Str(last, "key") }, { "name", Json.Str(last, "name") } } : null;
        }
    }

    // a weak spot's kind: a part (the play's category, mapped) and who drives it (her / him)
    public struct KenseiCombo
    {
        public string Part, Who;
        public KenseiCombo(string part, string who) { Part = part; Who = who; }
        public string Key { get { return Part + "/" + Who; } }
        public static KenseiCombo Of(string key) { int i = key.LastIndexOf('/'); return new KenseiCombo(key.Substring(0, i), key.Substring(i + 1)); }
        public override string ToString() { return Key; }
    }

    // what a weak spot is, one of the run's: a play with its combo (version 2), or a combo itself (version 1: its id its key)
    public struct KenseiItem
    {
        public string Id;
        public KenseiCombo Combo;
        public KenseiItem(string id, KenseiCombo combo) { Id = id; Combo = combo; }
        public static KenseiItem OfCombo(KenseiCombo c) { return new KenseiItem(c.Key, c); }

        // the order of the draws (tools/kensei_sim.py's): the part, the lead, the id - ordinal
        public static int Order(KenseiItem a, KenseiItem b)
        {
            int c = string.CompareOrdinal(a.Combo.Part, b.Combo.Part);
            if (c == 0) c = string.CompareOrdinal(a.Combo.Who, b.Combo.Who);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        }
    }

    public static class KenseiAttributes
    {
        // a play's attributes (kensei_attributes.json: the group's rules, then the overrides, then what is left out): its combo,
        // or null - category: the play's category (data/play_categories.json, PlayCtl.Category); herGives: the group's giver
        // is the character
        public static KenseiCombo? Of(Dictionary<string, object> row, string category, bool herGives, Dictionary<string, object> attrs,
                                      Dictionary<string, object> categories)
        {
            string group = Json.Str(row, "group", ""), name = Json.Str(row, "name", ""), id = Json.Str(row, "id", "");
            var g = Json.Obj(Json.Obj(attrs, "groups"), group);
            if (g == null || Json.Has(row, "giver") || Json.Has(row, "mutual")) return null;
            string part, side;
            var ov = Json.Obj(attrs, "overrides");
            if (ov != null && ov.ContainsKey(id))
            {
                var o = ov[id] as Dictionary<string, object>;
                if (o == null) return null;
                part = Json.Str(o, "part", "");
                side = Json.Str(o, "lead", "giver");
            }
            else
            {
                var parts = Json.Obj(g, "parts");
                if (parts == null || !parts.ContainsKey(category)) return null;
                part = Json.Str(parts, category, "");
                side = Json.Str(g, "lead", "giver");
                foreach (var w in Json.Arr(g, "receiver_words") ?? new List<object>()) if (name.Contains((string)w)) side = "receiver";
                foreach (var c in Json.Arr(g, "receiver_categories") ?? new List<object>()) if ((string)c == category) side = "receiver";
            }
            foreach (var w in Json.Arr(g, "exclude_words") ?? new List<object>()) if (name.Contains((string)w)) return null;
            if (attrs.ContainsKey("exclude_mixed") && attrs["exclude_mixed"] is bool && (bool)attrs["exclude_mixed"])
            {
                var hits = new HashSet<string>();
                foreach (var rule in Json.Arr(categories, group) ?? new List<object>())
                    foreach (var w in Json.Arr(rule, "words") ?? new List<object>())
                        if (name.Contains((string)w)) hits.Add(Json.Str(rule, "name", ""));
                if (hits.Count > 1) return null;
            }
            string need = Json.Str(g, "require_receiver_part", null);
            if (!string.IsNullOrEmpty(need))
            {
                var recv = Json.Arr(Json.Obj(row, "parts"), "receiver");
                if (recv == null || !recv.Contains(need)) return null;
            }
            return new KenseiCombo(part, (side == "giver") == herGives ? "her" : "him");
        }
    }

    // xorshift32: the same seed, the same draws (tools/kensei_sim.py Rng)
    public class KenseiRng
    {
        public uint S;
        public KenseiRng(uint seed) { S = seed != 0 ? seed : 2463534242u; }
        public uint Next() { uint s = S; s ^= s << 13; s ^= s >> 17; s ^= s << 5; S = s; return s; }
        public int Below(int n) { return (int)(Next() % (uint)n); }
        public double Unit() { return Next() / 4294967296.0; }

        public List<T> Pick<T>(List<T> items, int k)
        {
            var a = new List<T>(items);
            for (int i = 0; i < k; i++)
            {
                int j = i + Below(a.Count - i);
                var t = a[i]; a[i] = a[j]; a[j] = t;
            }
            return a.GetRange(0, k);
        }
    }

    // one turn as it came out (the truth; Kensei keeps what Muse was told beside it)
    public class KenseiTurn
    {
        public int Turn, Grade, Rise, Uses, Before, After, Amount, Over, Total, Phase;
        public bool Came, Redrawn;
        public double Factor = 1.0;
        public string Combo = "", Item = "", Play = "", Request = "", Result = "";
        public Dictionary<string, object> Told;        // (Kensei) what Muse was told of it
        public Dictionary<string, object> Answer;      // (Kensei) the user's answer as Muse wrote it down (null: not yet)
        public Dictionary<string, object> Package;     // (Kensei) the package that carried the move, as Muse sent it
        public string FinishPlace = "";                // (Kensei) where he came (outside; another when the play has none)

        public Dictionary<string, object> Truth()
        {
            var d = new Dictionary<string, object> { { "turn", Turn }, { "request", Request }, { "play", Play }, { "combo", Combo }, { "item", Item },
                { "grade", KenseiRules.Grades[Grade] }, { "rise", Rise }, { "decay_uses", Uses }, { "gauge_before", Before }, { "gauge_after", After },
                { "came", Came }, { "total", Total }, { "phase", Phase } };
            if (Came) { d["amount"] = Amount; d["overkill"] = Over; d["factor"] = Math.Round(Factor, 6); d["finish"] = FinishPlace; }
            if (Redrawn) d["redrawn"] = true;
            return d;
        }
    }

    public class KenseiRun
    {
        public readonly KenseiRules R;
        public readonly List<KenseiItem> Items;          // the candidates the weak are drawn from (plays, or combos: the rules' unit)
        public readonly List<KenseiCombo> Combos;        // (the items' combos, sorted)
        public readonly uint Seed;
        public readonly KenseiRng Rng;
        public readonly int Weak, Need, Tol, Limit;
        public readonly bool Vary, Decay;
        public readonly string Tightening;
        public List<KenseiItem> WeakNow;
        public readonly List<Dictionary<string, object>> Phases = new List<Dictionary<string, object>>();     // the assignments, each from its turn
        public readonly List<KenseiTurn> Turns = new List<KenseiTurn>();
        readonly Dictionary<string, int> used = new Dictionary<string, int>();
        public int Gauge, Total, Turn = 1;
        public string Result = "";      // "": going on; success / failure / abandoned / interrupted

        public KenseiRun(KenseiRules rules, List<KenseiItem> items, uint seed, int weak, bool vary, bool decay, int tol, int need, string tighten)
        {
            R = rules;
            Items = new List<KenseiItem>(items);
            Combos = new List<KenseiCombo>();
            var seen = new HashSet<string>();
            foreach (var it in Items) if (seen.Add(it.Combo.Key)) Combos.Add(it.Combo);
            Combos.Sort((a, b) => { int c = string.CompareOrdinal(a.Part, b.Part); return c != 0 ? c : string.CompareOrdinal(a.Who, b.Who); });
            Seed = seed;
            Rng = new KenseiRng(seed);
            Weak = weak;
            Vary = vary;
            Decay = decay;
            Tol = tol;
            Need = need;
            Tightening = tighten;
            var parts = new HashSet<string>();
            var leads = new HashSet<string>();
            var per = new Dictionary<string, int>();
            int most = 0;
            foreach (var it in Items)
            {
                parts.Add(it.Combo.Part);
                leads.Add(it.Combo.Who);
                int n;
                per.TryGetValue(it.Combo.Key, out n);
                per[it.Combo.Key] = ++n;
                most = Math.Max(most, n);
            }
            Limit = R.Limit(parts.Count, leads.Count, most, weak, vary, tighten, need, tol);
            WeakNow = Rng.Pick(Items, weak);
            Phases.Add(PhaseOf(1));
        }

        Dictionary<string, object> PhaseOf(int from)
        {
            var w = new List<object>();
            foreach (var c in WeakNow) w.Add(c.Id);
            return new Dictionary<string, object> { { "from_turn", from }, { "weak", w } };
        }

        // the settings' own check (#115 §0): K within the candidates, a full change needs 2K <= N
        public static string Invalid(int n, int weak, bool vary, int weakMax)
        {
            if (n < 1) return "no_candidates";
            if (weak < 1 || weak > weakMax || weak > n) return "weak_out_of_range";
            if (vary && 2 * weak > n) return "vary_needs_2k_le_n";
            return "";
        }

        // 3 the weak item itself (a combo-unit run: its combo), 2 the same combo - another play, 1 one axis, 0 none; each weak item
        // judged on its own, the highest kept (never one weak's part with another's lead - #115, GPT's handoff)
        public static int GradeOf(KenseiItem c, List<KenseiItem> weak)
        {
            int g = 0;
            foreach (var w in weak)
            {
                if (c.Id == w.Id) return 3;
                int m = (c.Combo.Part == w.Combo.Part ? 1 : 0) + (c.Combo.Who == w.Combo.Who ? 1 : 0);
                g = Math.Max(g, m == 2 ? 2 : m);
            }
            return g;
        }

        // one move confirmed (#115 §6, §7, §9): its grade under the assignment now, the rise (a non-weak combo's decay), the gauge,
        // him coming - the amount with its tolerance and overkill - the total, the result, then a redraw (vary, he came, not done)
        public KenseiTurn Step(KenseiItem c, string play, string request)
        {
            var t = new KenseiTurn { Turn = Turn, Combo = c.Combo.Key, Item = c.Id, Play = play ?? "", Request = request ?? "", Phase = Phases.Count - 1 };
            t.Grade = GradeOf(c, WeakNow);
            t.Rise = R.Rise[t.Grade];
            if (t.Grade == 0 && Decay)
            {
                int u;
                used.TryGetValue(c.Combo.Key, out u);           // (a combo's uses: another pose of it is the same - #115 §9)
                t.Uses = u;
                t.Rise = Math.Max(R.DecayFloor, KenseiRules.HalfUp(t.Rise * Math.Pow(R.DecayFactor, u)));
                used[c.Combo.Key] = u + 1;
            }
            t.Before = Gauge;
            int g = Gauge + t.Rise;
            if (g >= R.Threshold)
            {
                t.Over = g - R.Threshold;
                t.Factor = Tol > 0 ? 1.0 + Tol / 100.0 * (2.0 * Rng.Unit() - 1.0) : 1.0;
                t.Amount = KenseiRules.HalfUp(R.Base[t.Grade] * t.Factor) + t.Over;
                Total += t.Amount;
                Gauge = 0;
                t.Came = true;
            }
            else Gauge = g;
            t.After = Gauge;
            t.Total = Total;
            if (Total >= Need) Result = "success";
            else if (Turn >= Limit) Result = "failure";
            if (t.Came && Result.Length == 0 && Vary)
            {
                var rest = Items.FindAll(x => !WeakNow.Exists(w => w.Id == x.Id));
                WeakNow = Rng.Pick(rest, Weak);
                used.Clear();
                t.Redrawn = true;
                Phases.Add(PhaseOf(Turn + 1));
            }
            t.Result = Result;
            Turns.Add(t);
            Turn++;
            return t;
        }

        // the run as it is (a move's step taken back when its record cannot be written - GPT's post-review of #118, 1)
        public class Snap
        {
            public int Gauge, Total, Turn, Phases, Turns;
            public string Result;
            public uint Rng;
            public List<KenseiItem> Weak;
            public Dictionary<string, int> Used;
        }

        public Snap Snapshot()
        {
            return new Snap { Gauge = Gauge, Total = Total, Turn = Turn, Phases = Phases.Count, Turns = Turns.Count, Result = Result, Rng = Rng.S,
                Weak = new List<KenseiItem>(WeakNow), Used = new Dictionary<string, int>(used) };
        }

        public void Restore(Snap s)
        {
            Gauge = s.Gauge; Total = s.Total; Turn = s.Turn; Result = s.Result; Rng.S = s.Rng;
            WeakNow = new List<KenseiItem>(s.Weak);
            used.Clear();
            foreach (var kv in s.Used) used[kv.Key] = kv.Value;
            if (Phases.Count > s.Phases) Phases.RemoveRange(s.Phases, Phases.Count - s.Phases);
            if (Turns.Count > s.Turns) Turns.RemoveRange(s.Turns, Turns.Count - s.Turns);
        }

        // a run read back from its record (Kensei's resume): the same seed and the same moves give the same run - checked
        // against the record's own numbers (a different outcome: the rules changed under it - null)
        public static KenseiRun Replayed(KenseiRules rules, List<KenseiItem> items, uint seed, int weak, bool vary, bool decay, int tol, int need,
                                         string tighten, List<KenseiTurn> recorded)
        {
            var run = new KenseiRun(rules, items, seed, weak, vary, decay, tol, need, tighten);
            foreach (var r in recorded)
            {
                string id = r.Item.Length > 0 ? r.Item : r.Combo;            // (a version 1 record: its combo the item)
                var it = items.Find(x => x.Id == id);
                if (it.Id == null) return null;
                var t = run.Step(it, r.Play, r.Request);
                if (t.Grade != r.Grade || t.Amount != r.Amount || t.Total != r.Total || t.Came != r.Came) return null;
                t.Told = r.Told;
                t.Answer = r.Answer;
                t.FinishPlace = r.FinishPlace;
                t.Package = r.Package;          // (the move's package and its lines kept - GPT's post-review of #118, 2)
            }
            return run;
        }
    }
}
