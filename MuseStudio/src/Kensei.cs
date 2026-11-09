using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MuseStudio
{
    // 献精室 (#113-#115; the design of #115, the plan of its comment; MUSE_GUIDE 3.15): the content's run in the app. The user
    // sets it up on the panel (コンテンツ… → 献精…) and starts it; the app draws the weak combos from the candidates (the plays
    // of the range this room can start whose finish outside is known to play - each play tried once and remembered: Scan - by
    // their attributes: KenseiAttributes), keeps the gauge, the amounts and the truth to itself and tells Muse only what the
    // run's disclosure lets out (one place: TurnView / Info). A turn is Muse's move (a block's move {run, turn, request, play,
    // pace?}): taken when that block's operations are done, before the block is told shown - the play started or kept, its
    // finish outside there, one step of the gauge (KenseiRun.Step), the record written first (it cannot be: the step taken back,
    // the move refused save_failed), then him coming outside, kensei_turn. A move waiting for the play is bound to its run and
    // turn: another run meanwhile, it lapses (run_changed). Her climax is off meanwhile (her gauge held; Muse's anim climax,
    // standby climax, play.finish, plays and moves other than a move refused in_kensei). The record: one file a run beside the
    // matches' (Replay.Dir), written whole (a part file, then swapped in) at each change; the end told once it is written, the
    // score once a run (kensei_scores.json, made good again at start from the records); an unfinished one found at start is
    // resumed - with the character it began with only - or ended interrupted (Muse told only when it is that character's)
    // (GPT's post-review of #118: 1 the record before the turn, 2 the lines kept at a resume, 3 the character, 4 the run, 5 outside).
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
        string recLaunch = "", recCharacter = "";  // (the record's own: the launch and the character it began with)
        readonly List<string> resumedIn = new List<string>();
        bool scanned, endDirty;
        float retryAt;
        // a run's score still to be kept: its own id and values, taken at its end (another run started meanwhile is not it -
        // GPT's re-review of #119, 1)
        class ScoreDue { public string Id, Result, Title; public double Total, Coef, Score; }
        readonly List<ScoreDue> scoresDue = new List<ScoreDue>();
        public int ScoresDueForCheck { get { return scoresDue.Count; } }
        public bool FailScoresForCheck;        // (checks) the scores' file is not writable
        public float EndedAt = -100f;
        // the plays' finish outside, tried once (true: it plays) - kept in this PC's file (UserData/MuseStudio/kensei_finishes.json)
        readonly Dictionary<string, bool> finishes = new Dictionary<string, bool>();
        readonly string finishesPath;
        public bool Scanning;
        public int ScanLeft;
        bool scanStop;
        // (checks)
        public uint SeedForCheck;                  // the next run's seed
        public List<string> PartsForCheck;         // the next run's range narrowed to these parts
        public bool FailWritesForCheck;            // the record's writes fail
        public bool HoldMovesForCheck;             // a move waits (as for the play) till this is cleared
        public HashSet<string> NoOutsideForCheck;  // plays taken for having no finish outside at a move
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
            string check;
            finishesPath = MuseStudioPlugin.Args.TryGetValue("check", out check) ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(check)), "kensei_finishes.json")
                : Path.Combine(BepInEx.Paths.GameRootPath, Path.Combine("UserData", Path.Combine("MuseStudio", "kensei_finishes.json")));
            try
            {
                var doc = File.Exists(finishesPath) ? Json.Parse(File.ReadAllText(finishesPath, System.Text.Encoding.UTF8)) as Dictionary<string, object> : null;
                foreach (var kv in Json.Obj(doc, "plays") ?? new Dictionary<string, object>()) if (kv.Value is bool) finishes[kv.Key] = (bool)kv.Value;
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: kensei finishes: " + e.Message); }
        }

        static float Now { get { return Time.realtimeSinceStartup; } }

        public bool Active { get { return Run != null && Run.Result.Length == 0; } }

        // a run going on, or the plays being tried: the panel's room and plays wait
        public bool Busy { get { return Active || Scanning; } }

        public string FilePath(string id) { return Path.Combine(Replay.Dir, "kensei-" + id + ".json"); }
        static string ScoresPath { get { return Path.Combine(Replay.Dir, "kensei_scores.json"); } }

        public void Tick()
        {
            if (!scanned && h.Stage != null && h.Stage.Ready) { scanned = true; FindPending(); ReconcileScores(); }
            if ((endDirty || scoresDue.Count > 0) && Now >= retryAt) FinishEnd();
        }

        // a file written whole (WholeFile: true once the new text is in place - a leftover not cleared does not make it false)
        bool WriteWhole(string path, string text)
        {
            string err;
            if (FailWritesForCheck) err = "check: the write fails";
            else if (WholeFile.Write(path, text, out err)) return true;
            MuseStudioPlugin.Log.LogWarning("MuseStudio: kensei: " + Path.GetFileName(path) + " not written: " + err);
            return false;
        }

        // ---- the candidates ----

        // a play's combo, or null (not a candidate's: left out by the attributes)
        public KenseiCombo? ComboOf(Dictionary<string, object> row)
        {
            string group = Json.Str(row, "group", "");
            bool herGives = Json.Str(Json.Obj(Json.Obj(playsDoc, "groups"), group), "giver", "") == "character";
            return KenseiAttributes.Of(row, h.Play.Category(row), herGives, attrs, cats);
        }

        IEnumerable<Dictionary<string, object>> RangeRows(string range)
        {
            foreach (var g in range == "houshi" ? new[] { "houshi" } : new[] { "houshi", "sonyu" })
                foreach (var row in h.Play.List(g)) yield return row;
        }

        // the plays of a range this room can start (not reversed), by combo (sorted: the draws' order - tools/kensei_sim.py's);
        // verified: only those whose finish outside is known to play (#115: outside only - GPT's post-review of #118, 5)
        public SortedDictionary<string, List<string>> Candidates(string range, List<string> parts = null, bool verified = true)
        {
            var o = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var row in RangeRows(range))
            {
                var c = ComboOf(row);
                if (!c.HasValue || (parts != null && !parts.Contains(c.Value.Part))) continue;
                string id = Json.Str(row, "id");
                bool fin;
                if (verified && !(finishes.TryGetValue(id, out fin) && fin)) continue;
                if (!(bool)h.Play.Availability(row)["ok"]) continue;
                List<string> l;
                if (!o.TryGetValue(c.Value.Key, out l)) o[c.Value.Key] = l = new List<string>();
                l.Add(id);
            }
            return o;
        }

        // the plays of a range this room can start whose finish is not tried yet
        public List<string> Untried(string range)
        {
            var o = new List<string>();
            foreach (var row in RangeRows(range))
            {
                if (!ComboOf(row).HasValue) continue;
                string id = Json.Str(row, "id");
                if (finishes.ContainsKey(id) || !(bool)h.Play.Availability(row)["ok"]) continue;
                o.Add(id);
            }
            return o;
        }

        public bool FinishTried(string id, out bool outside) { return finishes.TryGetValue(id, out outside); }

        void SaveFinishes()
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in finishes) d[kv.Key] = kv.Value;
            bool fail = FailWritesForCheck;
            FailWritesForCheck = false;          // (this PC's knowledge of the plays: not the run's record)
            WriteWhole(finishesPath, Json.Write(new Dictionary<string, object> { { "kind", "musestudio_kensei_finishes" }, { "version", 1 }, { "plays", d } }));
            FailWritesForCheck = fail;
        }

        string CannotScan()
        {
            if (Busy) return Active ? "running" : "scanning";
            if (h.Match.Active || h.Match.Loading || h.Match.Replaying || h.Replay.Playing) return "in_match";
            if (h.Outing.Out) return "she_is_out";
            if (h.Presence.Apart) return "apart";
            if (h.Chars.Creating) return "creation";
            return "";
        }

        // each play of the range not tried yet: started at once, its finish outside looked for (Climax.Places: the play's own
        // motion's states), ended - quietly (no play events to Muse, Muse's packages wait meanwhile); remembered on this PC
        public IEnumerator Scan(string range)
        {
            if (CannotScan().Length > 0) yield break;
            var todo = Untried(range);
            if (todo.Count == 0) yield break;
            Scanning = true;
            scanStop = false;
            ScanLeft = todo.Count;
            var pc = h.Play;
            h.Bridge.Quiet = t => t.StartsWith("play_") || t == "climax";
            bool touched = false;
            try
            {
                foreach (var id in todo)
                {
                    if (scanStop || h.Outing.Out || h.Presence.Apart || h.Match.Active) break;
                    if (pc.Active) { pc.End("kensei_scan", false, false); touched = true; }      // (the one before: the next starts at once)
                    if (pc.Begin(id, "kensei_scan", null).Length == 0)
                    {
                        touched = true;
                        float t0 = Now;
                        while (pc.Active && string.IsNullOrEmpty(pc.LastState) && Now - t0 < 5f) yield return null;
                        yield return null;
                        yield return null;
                        if (pc.Active && !string.IsNullOrEmpty(pc.LastState)) finishes[id] = pc.Climax.Places().Contains("outside");
                    }
                    ScanLeft--;
                    yield return null;
                }
            }
            finally
            {
                // (the last one ended as a play's end does - her stance, the idle scene, the default camera; or, none left on,
                // the same put back: GPT's re-review of #119)
                if (pc.Active) pc.End("kensei_scan", true, false);
                else if (touched) pc.BackToIdle();
                h.Bridge.Quiet = null;
                Scanning = false;
                ScanLeft = 0;
                SaveFinishes();
            }
        }

        public void StopScan() { scanStop = true; }

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
            if (Scanning) return "scanning";
            if (Active) return "running";
            if (endDirty) return "ending";
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
                case "scanning": return "プレイの射精の流れを確かめています";
                case "ending": return "前の献精の記録を書いています（書けしだい、結果を確定します）";
                case "pending": return "終わっていない献精の記録があります（続けるか、終えるかを選んでください）";
                case "in_match": return "対戦・リプレイの間は始められません";
                case "she_is_out": return "彼女がお出かけ中です";
                case "apart": return "彼女と同じ部屋にいるときだけ始められます";
                case "creation": return "キャラメイクの間は始められません";
                case "needs_male_body": return "あなたの体が男性の体のときだけ遊べます（設定「あなたの体」）";
                case "no_candidates": return "この部屋で始められる候補がありません（射精の流れを確かめたプレイだけが候補です）";
                case "weak_out_of_range": return "弱点の数は、候補の数まで（かつ設定の上限まで）にしてください";
                case "vary_needs_2k_le_n": return "弱点変動には、候補が弱点の数の 2 倍以上要ります";
                case "save_failed": return "献精の記録を書けませんでした";
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
            var run = new KenseiRun(Rules, CombosOf(cands.Keys), seed, WeakN, Vary, DecayOn, Tol, Need, Tighten);
            var was = new Dictionary<string, List<string>>(PlaysOf);
            Run = run;
            RunId = "k" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + (seed & 0xFFFF).ToString("x4");
            PlaysOf.Clear();
            foreach (var kv in cands) PlaysOf[kv.Key] = kv.Value;
            runRange = Range; runRandom = RangeRandom; runWeak = WeakN; runVary = Vary; runDecay = DecayOn; runDisclose = Disclose;
            runNeed = Need; runTighten = Tighten; runTol = Tol;
            RangeParts = new List<string>();
            foreach (var c in run.Combos) if (!RangeParts.Contains(c.Part)) RangeParts.Add(c.Part);
            Coef = Rules.Coefficient(cands.Count, runWeak, runVary, runTighten, runNeed, runTol, runDisclose, runDecay, runRandom, out Points);
            Score = 0;
            Title = "";
            Room = h.Stage.Location;
            Started = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            recLaunch = Launch.Id;
            recCharacter = h.CharacterId();
            resumedIn.Clear();
            requests.Clear();
            moveIn = cameIn = null;
            Last = null;
            if (!Save())
            {
                // (not begun: nothing of it is kept - GPT's post-review of #118, 1)
                Run = null;
                RunId = "";
                PlaysOf.Clear();
                foreach (var kv in was) PlaysOf[kv.Key] = kv.Value;
                Error = Why("save_failed") + "（始めませんでした）";
                return "save_failed";
            }
            Hold();
            if (h.Play.Active) h.Play.End("kensei");
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
            // (bound to its run and its turn: a wait that comes back to another run, or a turn already gone, lapses - GPT's
            // post-review of #118, 4; until it is taken, its request is free again)
            var run = Run;
            int turn = run.Turn;
            Func<bool> still = () => ReferenceEquals(Run, run) && run.Result.Length == 0 && run.Turn == turn;
            Action<string, string> lapse = (reason, d) =>
            {
                requests.Remove(req);
                if (ReferenceEquals(moveIn, pkg)) moveIn = null;
                Refuse("move", req, reason, d);
            };
            requests.Add(req);
            moveIn = pkg;
            string play = Json.Str(mv, "play", "");
            int pace = Json.Has(mv, "pace") ? (int)Json.Num(mv, "pace") : 2;
            var pc = h.Play;
            float t0 = Now;
            while ((pc.Climax.Running || HoldMovesForCheck) && Now - t0 < 30f) yield return null;      // (the finish before it plays out first)
            if (!still()) { lapse("run_changed", RunId); yield break; }
            if (!(pc.Active && Json.Str(pc.Current, "id") == play && !pc.Reversed))
            {
                string err = pc.Begin(play, "kensei", null, false, false, req);
                if (err.Length > 0) { lapse("not_started", err); yield break; }
                t0 = Now;
                while (pc.Active && string.IsNullOrEmpty(pc.LastState) && Now - t0 < 5f) yield return null;
                if (!still()) { lapse("run_changed", RunId); yield break; }
            }
            if (!pc.Active) { lapse("not_started", "play ended"); yield break; }
            // (outside only - #115; GPT's post-review of #118, 5: a play whose finish outside does not play here is no candidate
            // from now on, and the move is not taken)
            if (!pc.Climax.Places().Contains("outside") || (NoOutsideForCheck != null && NoOutsideForCheck.Contains(play)))
            {
                finishes[play] = false;
                SaveFinishes();
                foreach (var kv in PlaysOf) kv.Value.Remove(play);
                lapse("no_outside", play);
                yield break;
            }
            if (pc.Group == "sonyu")
            {
                if (pc.Condom == null) pc.SetCondom(false, "kensei");
                if (!pc.Inserted)
                {
                    pc.Insert(pace);
                    t0 = Now;
                    while (pc.Active && (!pc.Inserted || pc.Climax.Running) && Now - t0 < 15f) yield return null;
                    if (!still()) { lapse("run_changed", RunId); yield break; }
                    if (!pc.Active || !pc.Inserted) { lapse("not_started", "not inserted"); yield break; }
                }
                else pc.SetPace(pace, "kensei");
            }
            else pc.SetPace(pace, "kensei");
            pc.Progress();
            if (!still()) { lapse("run_changed", RunId); yield break; }
            // the turn, written before anything of it is told or shown (it cannot be: taken back, refused - GPT's post-review of
            // #118, 1; the same request may come again)
            var snap = run.Snapshot();
            var t = run.Step(combo, play, req);
            if (t.Came) t.FinishPlace = "outside";
            t.Package = PackageCopy(pkg);
            t.Told = TurnView(t);
            if (!Save())
            {
                run.Restore(snap);
                lapse("save_failed", "");
                yield break;
            }
            Last = t;
            if (t.Came)
            {
                string fw = pc.Climax.MuseFinish("outside");
                if (fw.Length > 0) MuseStudioPlugin.Log.LogWarning("MuseStudio: kensei: the finish outside did not start: " + fw);
                cameIn = pkg;
            }
            TurnEventsForCheck++;
            h.Bridge.Event("kensei_turn", t.Told);
            if (run.Result.Length > 0) Ended();
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
            var d = new Dictionary<string, object> { { "run", RunId }, { "phase", Active ? "running" : endDirty ? "ending" : "ended" }, { "turn", Run.Turn },
                { "turns_max", Run.Limit }, { "turns_left", Math.Max(0, Run.Limit - Run.Turn + 1) }, { "settings", Settings() },
                { "coefficient", Coef }, { "candidates", cl }, { "last", Last != null ? (object)Last.Told : null },
                { "rules_version", Rules.Version } };
            if (runDisclose == "numbers") d["total"] = Run.Total;
            else if (runDisclose == "levels") d["progress"] = Rules.Progress(Run.Total, runNeed);
            if (!Active && !endDirty)
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

        // the run over: her gauge free at once; the end told once its record is written, the score kept once (tried again
        // till they are - GPT's post-review of #118, 1)
        void Ended()
        {
            Score = Run.Result == "success" ? KenseiRules.HalfUp(Run.Total * Coef) : 0;
            Title = Run.Result == "success" ? Rules.Title(Score) : "";
            Release();
            EndedAt = Now;
            endDirty = true;
            FinishEnd();
        }

        void FinishEnd()
        {
            if (endDirty)
            {
                if (!Save()) { Error = Why("ending"); retryAt = Now + 5f; return; }
                endDirty = false;
                if (Run.Result == "success")
                    scoresDue.Add(new ScoreDue { Id = RunId, Result = Run.Result, Total = Run.Total, Coef = Coef, Score = Score, Title = Title });
                Error = "";
                var d = Info();
                d["turns_used"] = Run.Turns.Count;
                d["reveal"] = Reveal();
                h.Bridge.Event("kensei_ended", d);
            }
            for (int i = 0; i < scoresDue.Count; i++)
            {
                if (!AddScore(scoresDue[i])) { retryAt = Now + 5f; return; }
                scoresDue.RemoveAt(i--);
            }
        }

        // a successful run's score kept (once a run: its id; an entry there with other values - one wrongly kept - put right)
        bool AddScore(ScoreDue s)
        {
            if (s == null || s.Result != "success" || string.IsNullOrEmpty(s.Id)) return true;      // (only a finished success has a score)
            if (FailScoresForCheck) return false;
            Dictionary<string, object> doc;
            try { doc = File.Exists(ScoresPath) ? Json.Parse(File.ReadAllText(ScoresPath, System.Text.Encoding.UTF8)) as Dictionary<string, object> : null; }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: kensei scores: " + e.Message); return false; }
            doc = doc ?? new Dictionary<string, object> { { "kind", "musestudio_kensei_scores" }, { "version", 1 } };
            var runs = Json.Obj(doc, "runs") ?? new Dictionary<string, object>();
            var was = Json.Obj(runs, s.Id);
            if (was != null && Json.Str(was, "result", "") == "success" && Json.Num(was, "score", -1) == s.Score && Json.Num(was, "total", -1) == s.Total) return true;
            runs[s.Id] = new Dictionary<string, object> { { "result", s.Result }, { "total", s.Total }, { "coefficient", s.Coef }, { "score", s.Score },
                { "title", s.Title }, { "ended", was != null ? Json.Str(was, "ended", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")) : DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") } };
            doc["runs"] = runs;
            return WriteWhole(ScoresPath, Json.Write(doc));
        }

        // the scores' file made good from the records (at start): a successful run's score missing - the app closed, the file was
        // not writable - or kept with other values (an unfinished one's 0 kept by mistake) put in as its record says
        public void ReconcileScores()
        {
            var sc = Scores();
            foreach (var f in RecordFiles())
            {
                var doc = ReadRecord(f);
                if (doc == null || Json.Str(doc, "result", "") != "success" || Json.Num(doc, "score", 0) <= 0) continue;
                string id = Json.Str(doc, "run", "");
                if (id.Length == 0) continue;
                double total = 0;
                var turns = Json.Arr(doc, "turns");
                if (turns != null && turns.Count > 0) total = Json.Num(turns[turns.Count - 1], "total", 0);
                var was = Json.Obj(sc, id);
                if (was != null && Json.Str(was, "result", "") == "success" && Json.Num(was, "score", -1) == Json.Num(doc, "score", 0)) continue;
                var due = new ScoreDue { Id = id, Result = "success", Total = total, Coef = Json.Num(doc, "coefficient", 1.0), Score = Json.Num(doc, "score", 0),
                    Title = Json.Str(doc, "title", "") };
                if (!AddScore(due) && !scoresDue.Exists(x => x.Id == id)) scoresDue.Add(due);
            }
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
            var taken = new List<KenseiTurn>();
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
                taken.Add(t);
            }
            if (taken.Count > 0 && !Save())
            {
                // (not written: not taken - the same may come again)
                foreach (var t in taken) { t.Answer = null; Refuse("kensei_answers", t.Turn, "save_failed"); }
            }
        }

        // ---- the record ----

        public bool Save()
        {
            if (Run == null || RunId.Length == 0) return false;
            return WriteWhole(FilePath(RunId), Json.Write(Record()));
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
                { "launch", recLaunch }, { "character", recCharacter }, { "resumed_in", new List<object>(resumedIn.ToArray()) }, { "settings", Settings() },
                { "seed", (double)Run.Seed }, { "candidates", cands }, { "limit", Run.Limit }, { "coefficient", Coef }, { "points", Points },
                { "phases", new List<object>(Run.Phases.ToArray()) }, { "turns", turns }, { "result", Run.Result }, { "score", Score }, { "title", Title },
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
        // rules changed under it - it cannot go on: ended interrupted). Only in its own room, with its own character (GPT's
        // post-review of #118, 3); its record kept whole - its packages and lines too (2) - and its character its own.
        public string Resume()
        {
            Error = "";
            if (Pending == null) return "none";
            string recChar = Json.Str(Pending, "character", "");
            if (recChar.Length > 0 && recChar != h.CharacterId())
            {
                Error = "この献精は別のキャラ（" + recChar.Replace("card:", "") + "）で始めたものです。そのキャラに替えてから続けてください";
                return "other_character";
            }
            if (Json.Str(Pending, "room", "") != h.Stage.Location)
            {
                Error = "この献精は「" + Json.Str(h.Stage.LocationInfo(Json.Str(Pending, "room", "")), "name", Json.Str(Pending, "room", "")) + "」で始めたものです。その部屋に移ってから続けてください";
                return "other_room";
            }
            string why = CannotResume();
            if (why.Length > 0) { Error = Why(why); return why; }
            var rules = new KenseiRules(Json.Obj(Pending, "rules"));
            var keys = new List<string>();
            var plays = new Dictionary<string, List<string>>();
            foreach (var o in Json.Arr(Pending, "candidates") ?? new List<object>())
            {
                keys.Add(Json.Str(o, "combo"));
                var pl = new List<string>();
                foreach (var p in Json.Arr(o, "plays") ?? new List<object>()) pl.Add((string)p);
                plays[Json.Str(o, "combo")] = pl;
            }
            var st = Json.Obj(Pending, "settings");
            var recorded = new List<KenseiTurn>();
            foreach (var o in Json.Arr(Pending, "turns") ?? new List<object>())
                recorded.Add(new KenseiTurn { Turn = (int)Json.Num(o, "turn"), Combo = Json.Str(o, "combo", ""), Play = Json.Str(o, "play", ""), Request = Json.Str(o, "request", ""),
                    Grade = Array.IndexOf(KenseiRules.Grades, Json.Str(o, "grade", "")), Amount = (int)Json.Num(o, "amount", 0), Total = (int)Json.Num(o, "total", 0),
                    Came = True(o, "came"), Told = Json.Obj(o, "told"), Answer = Json.Obj(o, "answer"), FinishPlace = Json.Str(o, "finish", ""), Package = Json.Obj(o, "package") });
            var run = KenseiRun.Replayed(rules, CombosOf(keys), (uint)Json.Num(Pending, "seed"), (int)Json.Num(st, "weak", 1), True(st, "vary"), True(st, "decay"),
                                         (int)Json.Num(st, "tolerance", 0), (int)Json.Num(st, "need", 360), Json.Str(st, "tighten", "standard"), recorded);
            if (run == null || run.Result.Length > 0)
            {
                EndPending("interrupted");
                if (Pending == null) Error = "記録をそのまま続けられませんでした（中断として終えました）";
                return "not_replayed";
            }
            // (all of the run as it was, the record's own launch and character; then written once - not written: not resumed)
            var keep = new Keep(this);
            Rules = rules;
            Run = run;
            RunId = Json.Str(Pending, "run", "");
            PlaysOf.Clear();
            foreach (var kv in plays) PlaysOf[kv.Key] = kv.Value;
            runRange = Json.Str(st, "range", "both"); runRandom = True(st, "range_random"); runWeak = run.Weak; runVary = run.Vary; runDecay = run.Decay;
            runDisclose = Json.Str(st, "disclose", "numbers"); runNeed = run.Need; runTighten = run.Tightening; runTol = run.Tol;
            RangeParts = new List<string>();
            foreach (var p in Json.Arr(st, "parts") ?? new List<object>()) RangeParts.Add((string)p);
            Coef = Json.Num(Pending, "coefficient", 1.0);
            Points = (int)Json.Num(Pending, "points", 0);
            Room = Json.Str(Pending, "room", "");
            Started = Json.Str(Pending, "started", "");
            recLaunch = Json.Str(Pending, "launch", "");
            recCharacter = recChar.Length > 0 ? recChar : h.CharacterId();
            resumedIn.Clear();
            foreach (var o in Json.Arr(Pending, "resumed_in") ?? new List<object>()) resumedIn.Add((string)o);
            resumedIn.Add(Launch.Id);
            Score = 0;
            Title = "";
            if (!Save()) { keep.Back(); Error = Why("save_failed") + "（続けませんでした）"; return "save_failed"; }
            requests.Clear();
            foreach (var t in run.Turns) requests.Add(t.Request);
            moveIn = cameIn = null;
            Last = run.Turns.Count > 0 ? run.Turns[run.Turns.Count - 1] : null;
            Pending = null;
            PendingFile = "";
            Hold();
            var d = Info();
            d["resumed"] = true;
            h.Bridge.Event("kensei_started", d);
            return "";
        }

        // what a resume changes, put back when its record cannot be written
        class Keep
        {
            readonly Kensei k;
            readonly KenseiRules rules;
            readonly KenseiRun run;
            readonly string id, launch, character, room, started;
            readonly Dictionary<string, List<string>> plays;
            readonly List<string> resumed, parts;
            readonly double coef;

            public Keep(Kensei k)
            {
                this.k = k;
                rules = k.Rules; run = k.Run; id = k.RunId; launch = k.recLaunch; character = k.recCharacter; room = k.Room; started = k.Started;
                plays = new Dictionary<string, List<string>>(k.PlaysOf);
                resumed = new List<string>(k.resumedIn);
                parts = new List<string>(k.RangeParts);
                coef = k.Coef;
            }

            public void Back()
            {
                k.Rules = rules; k.Run = run; k.RunId = id; k.recLaunch = launch; k.recCharacter = character; k.Room = room; k.Started = started; k.Coef = coef;
                k.PlaysOf.Clear();
                foreach (var kv in plays) k.PlaysOf[kv.Key] = kv.Value;
                k.resumedIn.Clear();
                k.resumedIn.AddRange(resumed);
                k.RangeParts = parts;
            }
        }

        string CannotResume()
        {
            if (Scanning) return "scanning";
            if (Active) return "running";
            if (endDirty) return "ending";
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

        // the unfinished run ended as it was (interrupted: no score); its truth told to Muse only when it is this character's run
        // (another character's Muse is not this one - GPT's post-review of #118, 3); not written: left unfinished
        public void EndPending(string result)
        {
            if (Pending == null) return;
            var doc = Json.Parse(Json.Write(Pending)) as Dictionary<string, object>;
            doc["result"] = result;
            doc["ended"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            if (!WriteWhole(PendingFile, Json.Write(doc))) { Error = Why("save_failed") + "（終えられませんでした）"; return; }
            string recChar = Json.Str(Pending, "character", "");
            Pending = null;
            PendingFile = "";
            if (recChar.Length > 0 && recChar != h.CharacterId())
            {
                Error = "別のキャラのランだったので、Muse には知らせずに終えました";
                return;
            }
            var turns = new List<object>();
            double total = 0;
            foreach (var o in Json.Arr(doc, "turns") ?? new List<object>())
            {
                var d = Json.Parse(Json.Write(o)) as Dictionary<string, object>;
                if (d == null) continue;
                d.Remove("package");
                total = Json.Num(d, "total", total);
                turns.Add(d);
            }
            h.Bridge.Event("kensei_ended", new Dictionary<string, object> { { "run", Json.Str(doc, "run", "") }, { "phase", "ended" }, { "result", result },
                { "turns_used", turns.Count }, { "total", total }, { "score", 0 }, { "title", "" },
                { "coefficient", Json.Num(doc, "coefficient", 1.0) },
                { "reveal", new Dictionary<string, object> { { "seed", Json.Num(doc, "seed") }, { "phases", Json.Arr(doc, "phases") }, { "turns", turns } } } });
        }
    }
}
