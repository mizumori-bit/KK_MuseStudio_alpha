using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MuseStudio
{
    // 献精室 (#113-#115; the design of #115, the plan of its comment; MUSE_GUIDE 3.15): the content's run in the app. The user
    // sets it up on the panel (コンテンツ… → 献精…) and starts it; the app draws the weak combos from the candidates (the plays
    // of the range this room can start, by their attributes - KenseiAttributes), keeps the gauge, the amounts and the truth to
    // itself and tells Muse only what the run's disclosure lets out (one place: TurnView / Info). A turn is Muse's move (a block's
    // move {run, turn, request, play, pace?}): taken when that block's operations are done, before the block is told shown - the
    // play started or kept, one step of the gauge (KenseiRun.Step), him coming (the finish outside: the app's), the record
    // written, kensei_turn. Her climax is off meanwhile (her gauge held; Muse's anim climax, standby climax, play.finish, plays
    // and moves other than a move refused in_kensei). The record: one file a run beside the matches' (Replay.Dir), written at each
    // move, the score once at the end (kensei_scores.json); an unfinished one found at start is resumed or ended interrupted.
    public class Kensei
    {
        readonly MuseStudioPlugin h;
        public KenseiRules Rules;
        readonly Dictionary<string, object> attrs, cats, playsDoc;
        public KenseiRun Run;                      // the run going on, or the last one ended (its Result set)
        public string RunId = "";
        public readonly Dictionary<string, List<string>> PlaysOf = new Dictionary<string, List<string>>();   // the run's candidates: combo -> plays
        // the panel's settings (the next run's)
        public string Range = "both";              // houshi: the services only / both: services and insertions
        public bool RangeRandom, Vary, DecayOn;
        public int WeakN, Need, Tol;
        public string Disclose = "numbers", Tighten = "standard";
        public uint SeedForCheck;                  // (checks) the next run's seed
        public List<string> PartsForCheck;         // (checks) the next run's range narrowed to these parts
        public double Coef = 1.0;
        public int Points, Score;
        public string Title = "", Room = "", Started = "", Error = "";
        public List<string> RangeParts = new List<string>();     // (the range chosen for the run: its parts)
        public Dictionary<string, object> Pending; // an unfinished run's record found at start: resume or end it
        public string PendingFile = "";
        public KenseiTurn Last;
        readonly HashSet<string> requests = new HashSet<string>();
        object moveIn, cameIn;                     // the package whose move was taken / made him come (its came blocks show)
        string runRange = "", runDisclose = "numbers", runTighten = "standard";
        bool runVary, runDecay, runRandom;
        int runWeak, runNeed, runTol;
        bool scanned;
        public float EndedAt = -100f;
        public int TurnEventsForCheck, RefusedForCheck;

        public Kensei(MuseStudioPlugin host)
        {
            h = host;
            Rules = new KenseiRules(Data.Load("kensei_rules.json"));
            attrs = Data.Load("kensei_attributes.json");
            cats = Data.Load("play_categories.json");
            playsDoc = Data.Load("plays.json");
            WeakN = Rules.WeakDefault;
            Need = Rules.NeedDefault;
            Tol = Rules.TolDefault;
        }

        static float Now { get { return Time.realtimeSinceStartup; } }

        public bool Active { get { return Run != null && Run.Result.Length == 0; } }

        public string FilePath(string id) { return Path.Combine(Replay.Dir, "kensei-" + id + ".json"); }
        static string ScoresPath { get { return Path.Combine(Replay.Dir, "kensei_scores.json"); } }

        public void Tick()
        {
            if (!scanned && h.Stage != null && h.Stage.Ready) { scanned = true; FindPending(); }
        }

        // ---- the candidates ----

        // a play's combo, or null (not a candidate's: left out by the attributes)
        public KenseiCombo? ComboOf(Dictionary<string, object> row)
        {
            string group = Json.Str(row, "group", "");
            bool herGives = Json.Str(Json.Obj(Json.Obj(playsDoc, "groups"), group), "giver", "") == "character";
            return KenseiAttributes.Of(row, h.Play.Category(row), herGives, attrs, cats);
        }

        // the plays of a range this room can start (not reversed), by combo (sorted: the draws' order - tools/kensei_sim.py's)
        public SortedDictionary<string, List<string>> Candidates(string range, List<string> parts = null)
        {
            var o = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var g in range == "houshi" ? new[] { "houshi" } : new[] { "houshi", "sonyu" })
                foreach (var row in h.Play.List(g))
                {
                    var c = ComboOf(row);
                    if (!c.HasValue || (parts != null && !parts.Contains(c.Value.Part))) continue;
                    var av = h.Play.Availability(row);
                    if (!(bool)av["ok"]) continue;
                    List<string> l;
                    if (!o.TryGetValue(c.Value.Key, out l)) o[c.Value.Key] = l = new List<string>();
                    l.Add(Json.Str(row, "id"));
                }
            return o;
        }

        static List<KenseiCombo> CombosOf(IEnumerable<string> keys)
        {
            var l = new List<KenseiCombo>();
            foreach (var k in keys) l.Add(KenseiCombo.Of(k));
            l.Sort((a, b) => { int c = string.CompareOrdinal(a.Part, b.Part); return c != 0 ? c : string.CompareOrdinal(a.Who, b.Who); });
            return l;
        }

        // why a run cannot start now (""; the panel greys its button out with it)
        public string CannotStart(int n)
        {
            if (Active) return "running";
            if (Pending != null) return "pending";
            if (h.Match.Active || h.Match.Loading || h.Match.Replaying || h.Replay.Playing) return "in_match";
            if (h.Outing.Out) return "she_is_out";
            if (h.Presence.Apart) return "apart";
            if (h.Chars.Creating) return "creation";
            if (h.YourBody.Value != "male") return "needs_male_body";
            return KenseiRun.Invalid(n, WeakN, Vary, Rules.WeakMax);
        }

        public static string Why(string code)
        {
            switch (code)
            {
                case "running": return "献精が続いています";
                case "pending": return "終わっていない献精の記録があります（続けるか、終えるかを選んでください）";
                case "in_match": return "対戦・リプレイの間は始められません";
                case "she_is_out": return "彼女がお出かけ中です";
                case "apart": return "彼女と同じ部屋にいるときだけ始められます";
                case "creation": return "キャラメイクの間は始められません";
                case "needs_male_body": return "あなたの体が男性の体のときだけ遊べます（設定「あなたの体」）";
                case "no_candidates": return "この部屋で始められる候補がありません";
                case "weak_out_of_range": return "弱点の数は、候補の数まで（かつ設定の上限まで）にしてください";
                case "vary_needs_2k_le_n": return "弱点変動には、候補が弱点の数の 2 倍以上要ります";
                default: return code;
            }
        }

        // ---- start ----

        public string Start()
        {
            Error = "";
            var parts = PartsForCheck ?? (RangeRandom ? RandomParts() : null);
            PartsForCheck = null;
            var cands = Candidates(Range, parts);
            string why = CannotStart(cands.Count);
            if (why.Length > 0) { Error = Why(why); return why; }
            uint seed = SeedForCheck != 0 ? SeedForCheck : ((uint)DateTime.Now.Ticks ^ (uint)Environment.TickCount ^ 0x9E3779B9u) | 1u;
            SeedForCheck = 0;
            RunId = "k" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + (seed & 0xFFFF).ToString("x4");
            PlaysOf.Clear();
            foreach (var kv in cands) PlaysOf[kv.Key] = kv.Value;
            runRange = Range; runRandom = RangeRandom; runWeak = WeakN; runVary = Vary; runDecay = DecayOn; runDisclose = Disclose;
            runNeed = Need; runTighten = Tighten; runTol = Tol;
            RangeParts = new List<string>();
            foreach (var c in CombosOf(cands.Keys)) if (!RangeParts.Contains(c.Part)) RangeParts.Add(c.Part);
            Run = new KenseiRun(Rules, CombosOf(cands.Keys), seed, runWeak, runVary, runDecay, runTol, runNeed, runTighten);
            Coef = Rules.Coefficient(cands.Count, runWeak, runVary, runTighten, runNeed, runTol, runDisclose, runDecay, runRandom, out Points);
            Score = 0;
            Title = "";
            Room = h.Stage.Location;
            Started = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            requests.Clear();
            moveIn = cameIn = null;
            Last = null;
            Hold();
            if (h.Play.Active) h.Play.End("kensei");
            Save();
            h.Bridge.Event("kensei_started", Info());
            return "";
        }

        // おまかせ: some of the range's parts, drawn by the app (at least what the weak combos need)
        List<string> RandomParts()
        {
            var all = Candidates(Range);
            var parts = new List<string>();
            foreach (var c in CombosOf(all.Keys)) if (!parts.Contains(c.Part)) parts.Add(c.Part);
            var r = new System.Random();
            for (int i = parts.Count - 1; i > 0; i--) { int j = r.Next(i + 1); var t = parts[i]; parts[i] = parts[j]; parts[j] = t; }
            for (int n = Math.Max(1, (parts.Count + 1) / 2); n <= parts.Count; n++)
            {
                var pick = parts.GetRange(0, n);
                if (KenseiRun.Invalid(Candidates(Range, pick).Count, WeakN, Vary, Rules.WeakMax).Length == 0) return pick;
            }
            return null;
        }

        // her side meanwhile (#115 §8): her gauge at 0 and held, a reaction made ready for her climax dropped
        void Hold()
        {
            h.Gauge.Reset();
            h.Gauge.Held = "kensei";
            h.Standby.DropHeld("kensei_started");
        }

        void Release()
        {
            h.Gauge.Held = "";
            h.Gauge.Reset();
        }

        // ---- a move (BlockPlayer, before the block is told shown) ----

        public string MoveRefusal(Dictionary<string, object> mv, Dictionary<string, object> pkg, out KenseiCombo combo, out string detail)
        {
            combo = default(KenseiCombo);
            detail = "";
            if (!Active) return Run != null && Json.Str(mv, "run", "") == RunId ? "run_over" : "no_run";
            if (Json.Str(mv, "run", "") != RunId) { detail = RunId; return "other_run"; }
            string lid = Json.Str(pkg, "launch", null), cid = Json.Str(pkg, "character", null);
            if (lid == null || cid == null) return "untargeted";
            if (lid != Launch.Id) { detail = Launch.Id; return "old_launch"; }
            if (cid != h.CharacterId()) { detail = h.CharacterId(); return "other_character"; }
            if (!Json.Has(mv, "turn")) return "no_turn";
            if ((int)Json.Num(mv, "turn") != Run.Turn) { detail = Run.Turn.ToString(); return "stale_turn"; }
            string req = Json.Str(mv, "request", "");
            if (req.Length == 0 || req.Length > 200) return "no_request";
            if (requests.Contains(req)) return "duplicate_request";
            if (ReferenceEquals(moveIn, pkg)) return "second_move";
            string play = Json.Str(mv, "play", "");
            bool found = false;
            foreach (var kv in PlaysOf) if (kv.Value.Contains(play)) { combo = KenseiCombo.Of(kv.Key); found = true; }
            if (!found) return "not_candidate";
            if (Json.Has(mv, "pace"))
            {
                double p = Json.Num(mv, "pace", 0);
                if (p != Math.Floor(p) || p < 1 || p > 3) return "bad_pace";
            }
            return "";
        }

        void Refuse(string field, object value, string reason, string detail = "")
        {
            RefusedForCheck++;
            h.Bridge.Event("block_refused", new Dictionary<string, object> { { "field", field }, { "value", value }, { "reason", reason },
                { "detail", detail.Length > 0 ? (object)detail : null }, { "available", new List<object>() } });
        }

        public IEnumerator Move(Dictionary<string, object> mv, Dictionary<string, object> pkg)
        {
            KenseiCombo combo;
            string detail;
            string req = Json.Str(mv, "request", "");
            string why = MoveRefusal(mv, pkg, out combo, out detail);
            if (why.Length > 0) { Refuse("move", req, why, detail); yield break; }
            requests.Add(req);              // (refused from here on or not: a late copy of it never runs)
            moveIn = pkg;
            string play = Json.Str(mv, "play", "");
            int pace = Json.Has(mv, "pace") ? (int)Json.Num(mv, "pace") : 2;
            var pc = h.Play;
            float t0 = Now;
            while (pc.Climax.Running && Now - t0 < 30f) yield return null;      // (the finish before it plays out first)
            if (!(pc.Active && Json.Str(pc.Current, "id") == play && !pc.Reversed))
            {
                string err = pc.Begin(play, "kensei", null, false, false, req);
                if (err.Length > 0) { Refuse("move", req, "not_started", err); yield break; }      // (no turn used)
                t0 = Now;
                while (pc.Active && string.IsNullOrEmpty(pc.LastState) && Now - t0 < 5f) yield return null;
            }
            if (!pc.Active) { Refuse("move", req, "not_started", "play ended"); yield break; }
            if (pc.Group == "sonyu")
            {
                if (pc.Condom == null) pc.SetCondom(false, "kensei");
                if (!pc.Inserted)
                {
                    pc.Insert(pace);
                    t0 = Now;
                    while (pc.Active && (!pc.Inserted || pc.Climax.Running) && Now - t0 < 15f) yield return null;
                }
                else pc.SetPace(pace, "kensei");
            }
            else pc.SetPace(pace, "kensei");
            pc.Progress();
            if (!Active) yield break;       // (ended meanwhile: the user's やめる)
            var t = Run.Step(combo, play, req);
            if (t.Came)
            {
                var places = pc.Climax.Places();
                t.FinishPlace = places.Contains("outside") ? "outside" : places.Count > 0 ? places[0] : "";
                if (t.FinishPlace.Length > 0)
                {
                    string fw = pc.Climax.MuseFinish(t.FinishPlace);
                    if (fw.Length > 0) t.FinishPlace += " (" + fw + ")";
                }
                cameIn = pkg;
            }
            t.Package = PackageCopy(pkg);
            t.Told = TurnView(t);
            Last = t;
            Save();
            TurnEventsForCheck++;
            h.Bridge.Event("kensei_turn", t.Told);
            if (Run.Result.Length > 0) Ended();
        }

        // a came block (MUSE_GUIDE 3.15): shown only after a move of its package that made him come
        public bool CameIn(Dictionary<string, object> pkg) { return pkg != null && ReferenceEquals(cameIn, pkg); }

        static Dictionary<string, object> PackageCopy(Dictionary<string, object> pkg)
        {
            var copy = Json.Parse(Json.Write(pkg)) as Dictionary<string, object> ?? new Dictionary<string, object>();
            foreach (var k in new List<string>(copy.Keys)) if (k.StartsWith("_") || k == "launch" || k == "character") copy.Remove(k);
            return copy;
        }

        // ---- what Muse is told (one place: the disclosure) ----

        public Dictionary<string, object> TurnView(KenseiTurn t)
        {
            var d = new Dictionary<string, object> { { "run", RunId }, { "turn", t.Turn }, { "request", t.Request }, { "play", t.Play }, { "combo", t.Combo },
                { "came", t.Came }, { "turns_left", Math.Max(0, Run.Limit - t.Turn) }, { "result", t.Result.Length > 0 ? (object)t.Result : null } };
            if (runDisclose == "numbers")
            {
                if (t.Came) d["amount"] = t.Amount;
                d["total"] = t.Total;
            }
            else if (runDisclose == "levels") d["progress"] = Rules.Progress(t.Total, runNeed);
            return d;
        }

        Dictionary<string, object> Settings()
        {
            return new Dictionary<string, object> { { "range", runRange }, { "range_random", runRandom }, { "parts", new List<object>(RangeParts.ToArray()) },
                { "weak", runWeak }, { "vary", runVary }, { "decay", runDecay }, { "disclose", runDisclose }, { "need", runNeed }, { "tighten", runTighten },
                { "tolerance", runTol } };
        }

        // the state's kensei (null: none) - the run as Muse may see it
        public Dictionary<string, object> Info()
        {
            if (Run == null) return null;
            var cl = new List<object>();
            foreach (var c in Run.Combos)
            {
                List<string> pl;
                PlaysOf.TryGetValue(c.Key, out pl);
                cl.Add(new Dictionary<string, object> { { "combo", c.Key }, { "part", c.Part }, { "who", c.Who },
                    { "plays", new List<object>((pl ?? new List<string>()).ToArray()) } });
            }
            var d = new Dictionary<string, object> { { "run", RunId }, { "phase", Active ? "running" : "ended" }, { "turn", Run.Turn },
                { "turns_max", Run.Limit }, { "turns_left", Math.Max(0, Run.Limit - Run.Turn + 1) }, { "settings", Settings() },
                { "coefficient", Coef }, { "candidates", cl }, { "last", Last != null ? (object)Last.Told : null },
                { "rules_version", Rules.Version } };
            if (runDisclose == "numbers") d["total"] = Run.Total;
            else if (runDisclose == "levels") d["progress"] = Rules.Progress(Run.Total, runNeed);
            if (!Active)
            {
                d["result"] = Run.Result;
                d["total"] = Run.Total;
                d["score"] = Score;
                d["title"] = Title;
            }
            return d;
        }

        // the truth, after the end (kensei_ended's reveal; the record's)
        Dictionary<string, object> Reveal()
        {
            var turns = new List<object>();
            foreach (var t in Run.Turns)
            {
                var d = t.Truth();
                d["told"] = t.Told;
                d["answer"] = t.Answer;
                turns.Add(d);
            }
            return new Dictionary<string, object> { { "seed", (double)Run.Seed }, { "phases", new List<object>(Run.Phases.ToArray()) }, { "turns", turns } };
        }

        // ---- the end ----

        public void Abandon()
        {
            if (!Active) return;
            Run.Result = "abandoned";
            Ended();
        }

        void Ended()
        {
            Score = Run.Result == "success" ? KenseiRules.HalfUp(Run.Total * Coef) : 0;
            Title = Run.Result == "success" ? Rules.Title(Score) : "";
            Release();
            EndedAt = Now;
            Save();
            AddScore();
            var d = Info();
            d["turns_used"] = Run.Turns.Count;
            d["reveal"] = Reveal();
            h.Bridge.Event("kensei_ended", d);
        }

        void AddScore()
        {
            try
            {
                var doc = File.Exists(ScoresPath) ? Json.Parse(File.ReadAllText(ScoresPath, System.Text.Encoding.UTF8)) as Dictionary<string, object> : null;
                doc = doc ?? new Dictionary<string, object> { { "kind", "musestudio_kensei_scores" }, { "version", 1 } };
                var runs = Json.Obj(doc, "runs") ?? new Dictionary<string, object>();
                if (runs.ContainsKey(RunId)) return;          // (once a run)
                runs[RunId] = new Dictionary<string, object> { { "result", Run.Result }, { "total", Run.Total }, { "coefficient", Coef }, { "score", Score },
                    { "title", Title }, { "ended", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") } };
                doc["runs"] = runs;
                Directory.CreateDirectory(Replay.Dir);
                File.WriteAllText(ScoresPath, Json.Write(doc), new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: kensei scores: " + e.Message); }
        }

        public Dictionary<string, object> Scores()
        {
            try { return File.Exists(ScoresPath) ? Json.Obj(Json.Parse(File.ReadAllText(ScoresPath, System.Text.Encoding.UTF8)), "runs") : null; }
            catch (Exception) { return null; }
        }

        // ---- the answers Muse wrote down (#115 §6) ----

        public static bool AnswersOnly(Dictionary<string, object> pkg)
        {
            var b = Json.Arr(pkg, "blocks");
            return pkg != null && (b == null || b.Count == 0) && Json.Arr(pkg, "kensei_answers") != null;
        }

        public void TakeAnswersPackage(Dictionary<string, object> pkg)
        {
            string pid = Json.Str(pkg, "_pid");
            h.Bridge.Ack(pid, "started", null, -1, 0);
            TakeAnswers(pkg);
            h.Bridge.Ack(pid, "finished");
        }

        public void TakeAnswers(Dictionary<string, object> pkg)
        {
            var list = Json.Arr(pkg, "kensei_answers");
            if (list == null) return;
            bool any = false;
            foreach (var o in list)
            {
                var a = o as Dictionary<string, object>;
                int turn = (int)Json.Num(a, "turn", -1);
                if (Run == null || Json.Str(a, "run", "") != RunId) { Refuse("kensei_answers", turn, "other_run", RunId); continue; }
                var t = Run.Turns.Find(x => x.Turn == turn);
                if (t == null) { Refuse("kensei_answers", turn, "no_such_turn"); continue; }
                var ans = new Dictionary<string, object> { { "tap", Json.Str(a, "tap", null) }, { "text", Json.Str(a, "text", null) } };
                if (t.Answer != null)
                {
                    if (Json.Str(t.Answer, "tap", null) == Json.Str(ans, "tap", null) && Json.Str(t.Answer, "text", null) == Json.Str(ans, "text", null)) continue;
                    Refuse("kensei_answers", turn, "already_recorded");
                    continue;
                }
                ans["at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                t.Answer = ans;
                any = true;
            }
            if (any) Save();
        }

        // ---- the record ----

        public void Save()
        {
            if (Run == null || RunId.Length == 0) return;
            try
            {
                Directory.CreateDirectory(Replay.Dir);
                File.WriteAllText(FilePath(RunId), Json.Write(Record()), new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: kensei record: " + e.Message); }
        }

        Dictionary<string, object> Record()
        {
            var cands = new List<object>();
            foreach (var c in Run.Combos)
            {
                List<string> pl;
                PlaysOf.TryGetValue(c.Key, out pl);
                cands.Add(new Dictionary<string, object> { { "combo", c.Key }, { "plays", new List<object>((pl ?? new List<string>()).ToArray()) } });
            }
            var turns = new List<object>();
            foreach (var t in Run.Turns)
            {
                var d = t.Truth();
                d["told"] = t.Told;
                d["answer"] = t.Answer;
                d["package"] = t.Package;
                turns.Add(d);
            }
            return new Dictionary<string, object> { { "kind", "musestudio_kensei_run" }, { "version", 1 }, { "run", RunId }, { "rules_version", Rules.Version },
                { "rules", Rules.Doc }, { "attributes_version", Json.Num(attrs, "version", 0) }, { "started", Started }, { "room", Room },
                { "launch", Launch.Id }, { "character", h.CharacterId() }, { "settings", Settings() }, { "seed", (double)Run.Seed },
                { "candidates", cands }, { "limit", Run.Limit }, { "coefficient", Coef }, { "points", Points }, { "phases", new List<object>(Run.Phases.ToArray()) },
                { "turns", turns }, { "result", Run.Result }, { "score", Score }, { "title", Title },
                { "ended", Run.Result.Length > 0 ? (object)DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") : null } };
        }

        // the runs' records, the newest first
        public List<string> RecordFiles()
        {
            var l = new List<string>();
            try { if (Directory.Exists(Replay.Dir)) l.AddRange(Directory.GetFiles(Replay.Dir, "kensei-*.json")); }
            catch (Exception) { }
            l.Sort((a, b) => string.CompareOrdinal(b, a));
            return l;
        }

        public static Dictionary<string, object> ReadRecord(string file)
        {
            try { return Json.Parse(File.ReadAllText(file, System.Text.Encoding.UTF8)) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        // an unfinished run left by the app before (it closed during one)
        public void FindPending()
        {
            foreach (var f in RecordFiles())
            {
                var doc = ReadRecord(f);
                if (doc != null && Json.Str(doc, "result", "x").Length == 0 && Json.Str(doc, "run", "") != RunId)
                {
                    Pending = doc;
                    PendingFile = f;
                    return;
                }
            }
        }

        // the unfinished run again: the same seed and moves give the same run (its rules as it began; another outcome - the
        // rules changed under it - it cannot go on: ended interrupted). Only in its own room.
        public string Resume()
        {
            Error = "";
            if (Pending == null) return "none";
            if (Json.Str(Pending, "room", "") != h.Stage.Location) { Error = "この献精は「" + Json.Str(h.Stage.LocationInfo(Json.Str(Pending, "room", "")), "name", Json.Str(Pending, "room", "")) + "」で始めたものです。その部屋に移ってから続けてください"; return "other_room"; }
            string why = CannotStartResume();
            if (why.Length > 0) { Error = Why(why); return why; }
            var rules = new KenseiRules(Json.Obj(Pending, "rules"));
            var keys = new List<string>();
            PlaysOf.Clear();
            foreach (var o in Json.Arr(Pending, "candidates") ?? new List<object>())
            {
                keys.Add(Json.Str(o, "combo"));
                var pl = new List<string>();
                foreach (var p in Json.Arr(o, "plays") ?? new List<object>()) pl.Add((string)p);
                PlaysOf[Json.Str(o, "combo")] = pl;
            }
            var st = Json.Obj(Pending, "settings");
            var recorded = new List<KenseiTurn>();
            foreach (var o in Json.Arr(Pending, "turns") ?? new List<object>())
            {
                var t = new KenseiTurn { Turn = (int)Json.Num(o, "turn"), Combo = Json.Str(o, "combo", ""), Play = Json.Str(o, "play", ""), Request = Json.Str(o, "request", ""),
                    Grade = Array.IndexOf(KenseiRules.Grades, Json.Str(o, "grade", "")), Amount = (int)Json.Num(o, "amount", 0), Total = (int)Json.Num(o, "total", 0),
                    Came = True(o, "came"),
                    Told = Json.Obj(o, "told"), Answer = Json.Obj(o, "answer"), FinishPlace = Json.Str(o, "finish", ""), Package = Json.Obj(o, "package") };
                recorded.Add(t);
            }
            var run = KenseiRun.Replayed(rules, CombosOf(keys), (uint)Json.Num(Pending, "seed"), (int)Json.Num(st, "weak", 1), True(st, "vary"), True(st, "decay"),
                                         (int)Json.Num(st, "tolerance", 0), (int)Json.Num(st, "need", 360), Json.Str(st, "tighten", "standard"), recorded);
            if (run == null || run.Result.Length > 0) { EndPending("interrupted"); Error = "記録をそのまま続けられませんでした（中断として終えました）"; return "not_replayed"; }
            Rules = rules;
            Run = run;
            RunId = Json.Str(Pending, "run", "");
            runRange = Json.Str(st, "range", "both"); runRandom = True(st, "range_random"); runWeak = run.Weak; runVary = run.Vary; runDecay = run.Decay;
            runDisclose = Json.Str(st, "disclose", "numbers"); runNeed = run.Need; runTighten = run.Tightening; runTol = run.Tol;
            RangeParts = new List<string>();
            foreach (var p in Json.Arr(st, "parts") ?? new List<object>()) RangeParts.Add((string)p);
            Coef = Json.Num(Pending, "coefficient", 1.0);
            Points = (int)Json.Num(Pending, "points", 0);
            Room = Json.Str(Pending, "room", "");
            Started = Json.Str(Pending, "started", "");
            Score = 0;
            Title = "";
            requests.Clear();
            foreach (var t in run.Turns) requests.Add(t.Request);
            moveIn = cameIn = null;
            Last = run.Turns.Count > 0 ? run.Turns[run.Turns.Count - 1] : null;
            Pending = null;
            PendingFile = "";
            Hold();
            Save();
            var d = Info();
            d["resumed"] = true;
            h.Bridge.Event("kensei_started", d);
            return "";
        }

        string CannotStartResume()
        {
            if (Active) return "running";
            if (h.Match.Active || h.Match.Loading || h.Match.Replaying || h.Replay.Playing) return "in_match";
            if (h.Outing.Out) return "she_is_out";
            if (h.Presence.Apart) return "apart";
            if (h.Chars.Creating) return "creation";
            if (h.YourBody.Value != "male") return "needs_male_body";
            return "";
        }

        static bool True(object v, string key)
        {
            var d = v as Dictionary<string, object>;
            object b;
            return d != null && d.TryGetValue(key, out b) && b is bool && (bool)b;
        }

        // the unfinished run ended as it was (interrupted: no score) - Muse told of it and its truth
        public void EndPending(string result)
        {
            if (Pending == null) return;
            Pending["result"] = result;
            Pending["ended"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            try { File.WriteAllText(PendingFile, Json.Write(Pending), new System.Text.UTF8Encoding(false)); }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: kensei record: " + e.Message); }
            var turns = new List<object>();
            double total = 0;
            foreach (var o in Json.Arr(Pending, "turns") ?? new List<object>())
            {
                var d = Json.Parse(Json.Write(o)) as Dictionary<string, object>;
                if (d == null) continue;
                d.Remove("package");
                total = Json.Num(d, "total", total);
                turns.Add(d);
            }
            h.Bridge.Event("kensei_ended", new Dictionary<string, object> { { "run", Json.Str(Pending, "run", "") }, { "phase", "ended" }, { "result", result },
                { "turns_used", turns.Count }, { "total", total }, { "score", 0 }, { "title", "" },
                { "coefficient", Json.Num(Pending, "coefficient", 1.0) },
                { "reveal", new Dictionary<string, object> { { "seed", Json.Num(Pending, "seed") }, { "phases", Json.Arr(Pending, "phases") }, { "turns", turns } } } });
            Pending = null;
            PendingFile = "";
        }
    }
}
