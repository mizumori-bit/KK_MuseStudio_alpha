using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MuseStudio
{
    // 献精室's checks (#113-#115; MUSE_GUIDE 3.15): the app's scoring counts as tools/kensei_sim.py does (the scripted run of
    // bridge/tests/test_kensei_data.py), the candidates and their attributes, the settings' own check (2K <= N), a run through
    // Muse's packages - a move taken when its block's operations are done (the gauge's step, him coming outside, the came
    // lines only then), the refusals that use no turn, her climax off meanwhile (in_kensei), the answers written down (the
    // first of a turn), the end once (its reveal, the score once) - what the levels and the hidden modes let out (by the fields'
    // names and by the run's own numbers), the resume of a run from its record (the same turns, not counted again)
    public partial class SelfCheck
    {
        readonly List<string> kShown = new List<string>();

        static int[] ScriptMoves = { 0, 1, 2, 3, 4, 3, 3, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 0, 1 };
        // (bridge/tests/test_kensei_data.py's SCRIPTED: turn, grade, rise, gauge after, amount, total)
        static readonly int[,] Scripted = { { 1, 2, 45, 45, 0, 0 }, { 2, 1, 20, 65, 0, 0 }, { 3, 0, 8, 73, 0, 0 }, { 4, 1, 20, 93, 0, 0 }, { 5, 1, 20, 0, 72, 72 },
            { 6, 1, 20, 20, 0, 72 }, { 7, 1, 20, 40, 0, 72 }, { 8, 1, 20, 60, 0, 72 }, { 9, 1, 20, 80, 0, 72 }, { 10, 1, 20, 0, 59, 131 }, { 11, 1, 20, 20, 0, 131 },
            { 12, 1, 20, 40, 0, 131 }, { 13, 0, 8, 48, 0, 131 }, { 14, 0, 6, 54, 0, 131 }, { 15, 1, 20, 74, 0, 131 }, { 16, 1, 20, 94, 0, 131 }, { 17, 2, 45, 0, 165, 296 } };

        Dictionary<string, object> KTags() { return D("launch", Launch.Id, "character", h.CharacterId()); }

        Dictionary<string, object> KMove(string line, int turn, string req, string play)
        {
            return With(Line(line), "move", D("run", h.Kensei.RunId, "turn", turn, "request", req, "play", play));
        }

        // a package of a turn sent; its blocks walked one by one as each is presented (the texts shown kept)
        IEnumerator KTurn(Dictionary<string, object> extra, params Dictionary<string, object>[] blocks)
        {
            var bp = h.Blocks;
            kShown.Clear();
            SendPkg(extra, blocks);
            float t0 = Time.realtimeSinceStartup;
            yield return Until(() => bp.Busy, 10f);
            int lastIdx = -1;
            while (bp.Busy && Time.realtimeSinceStartup - t0 < 45f)
            {
                if (!bp.Moving && bp.Index >= 0 && bp.Index != lastIdx && !bp.Revealing && bp.Text.Length > 0)
                {
                    lastIdx = bp.Index;
                    kShown.Add(bp.Text);
                    bp.Advance(true);
                }
                yield return null;
            }
            yield return Wait(0.3f);
        }

        // the package being shown walked to its end (each block pressed on once it is presented)
        IEnumerator KDrain(float seconds)
        {
            var bp = h.Blocks;
            float t0 = Time.realtimeSinceStartup;
            int lastIdx = -1;
            while (bp.Busy && Time.realtimeSinceStartup - t0 < seconds)
            {
                if (!bp.Moving && bp.Index >= 0 && bp.Index != lastIdx && !bp.Revealing && bp.Text.Length > 0) { lastIdx = bp.Index; bp.Advance(true); }
                yield return null;
            }
        }

        // the play of a combo for the checks: an original one first (quick to start)
        string KPlay(string key)
        {
            List<string> l;
            if (!h.Kensei.PlaysOf.TryGetValue(key, out l) || l.Count == 0) return "";
            foreach (var p in l) if (!p.StartsWith("al_")) return p;
            return l[0];
        }

        // a combo of the run's candidates of a grade under the weak now ("" none)
        string KCombo(int grade)
        {
            var k = h.Kensei;
            foreach (var c in k.Run.Combos) if (KenseiRun.GradeOf(c, k.Run.WeakNow) == grade && KPlay(c.Key).Length > 0) return c.Key;
            return "";
        }

        // the numbers a JSON value holds (recursively)
        static void Numbers(object v, List<double> into)
        {
            if (v is double) into.Add((double)v);
            else if (v is Dictionary<string, object>) foreach (var x in ((Dictionary<string, object>)v).Values) Numbers(x, into);
            else if (v is List<object>) foreach (var x in (List<object>)v) Numbers(x, into);
        }

        static void Keys(object v, HashSet<string> into)
        {
            if (v is Dictionary<string, object>) foreach (var kv in (Dictionary<string, object>)v) { into.Add(kv.Key); Keys(kv.Value, into); }
            else if (v is List<object>) foreach (var x in (List<object>)v) Keys(x, into);
        }

        IEnumerator KenseiChecks()
        {
            var k = h.Kensei;
            var pc = h.Play;
            var bp = h.Blocks;
            if (pc.Active) pc.End("check");
            if (k.Pending != null) { Note("kensei: an unfinished run left by an earlier check run - ended interrupted"); k.EndPending("interrupted"); }
            yield return Wait(0.5f);

            // ---- the scoring as the reference counts it ----
            {
                var combos = new List<KenseiCombo> { new KenseiCombo("手", "her"), new KenseiCombo("口", "her"), new KenseiCombo("口", "him"),
                    new KenseiCombo("胸", "her"), new KenseiCombo("足", "her") };
                var run = new KenseiRun(k.Rules, combos, 12345u, 1, true, true, 10, 360, "standard");
                bool same = run.Limit == 17 && run.WeakNow[0].Key == "手/her";
                int n = 0;
                foreach (int m in ScriptMoves)
                {
                    if (run.Result.Length > 0) break;
                    var t = run.Step(combos[m], "p" + m, "r" + run.Turn);
                    if (n >= Scripted.GetLength(0) || t.Turn != Scripted[n, 0] || t.Grade != Scripted[n, 1] || t.Rise != Scripted[n, 2] || t.After != Scripted[n, 3]
                        || t.Amount != Scripted[n, 4] || t.Total != Scripted[n, 5]) same = false;
                    n++;
                }
                Check("献精室: the app's scoring counts the scripted run as tools/kensei_sim.py does (grades, the decay, the tolerance's amount, the redraws, the failure at the limit)",
                      same && n == Scripted.GetLength(0) && run.Result == "failure", "limit " + run.Limit + " turns " + n + " " + run.Result);
            }

            // ---- the plays' finish outside tried once (GPT's post-review of #118, 5): only those that play it are candidates ----
            int untried0 = k.Untried("houshi").Count;
            float scanT = Time.realtimeSinceStartup;
            yield return k.Scan("houshi");
            int outside = 0, none = 0;
            foreach (var kv in k.Candidates("houshi", null, false))
                foreach (var id in kv.Value) { bool o; if (k.FinishTried(id, out o)) { if (o) outside++; else none++; } }
            bool onlyVerified = true;
            foreach (var kv in k.Candidates("houshi")) foreach (var id in kv.Value) { bool o; if (!k.FinishTried(id, out o) || !o) onlyVerified = false; }
            Check("献精室: the service plays here tried once for their finish outside (quietly: no play events) - none left untried, the candidates only those that play it",
                  untried0 > 0 && k.Untried("houshi").Count == 0 && outside > 0 && onlyVerified && !k.Scanning && !pc.Active,
                  untried0 + " tried in " + (Time.realtimeSinceStartup - scanT).ToString("F0") + " s: outside " + outside + ", none " + none);
            // ---- the candidates here and the settings' own check ----
            var all = k.Candidates("both", null, false);
            var hou = k.Candidates("houshi", null, false);
            var a1 = k.ComboOf(pc.Row("houshi_01"));
            var a2 = k.ComboOf(pc.Row("sonyu_04"));
            var a3 = k.ComboOf(pc.Row("sonyu_09"));
            Check("献精室: the attributes - 片手コキ 手/her, 騎乗位 騎乗位/her, 椅子対面 (座位) left out; the candidates of this room by combo",
                  a1.HasValue && a1.Value.Key == "手/her" && a2.HasValue && a2.Value.Key == "騎乗位/her" && !a3.HasValue && hou.Count >= 3 && all.Count > hou.Count,
                  "here " + h.Stage.Location + ": houshi " + string.Join(",", new List<string>(hou.Keys).ToArray()) + " / both " + all.Count);
            var cat = Json.Parse(Http("GET", "/api/catalog", null));
            string catKensei = "";
            foreach (var o in Json.Arr(Json.Obj(cat, "catalog"), "plays") ?? new List<object>())
                if (Json.Str(o, "id") == "houshi_01") catKensei = Json.Write(Json.Obj(o, "kensei"));
            Check("献精室: the catalog's plays carry their attributes (kensei)", catKensei.Contains("手") && catKensei.Contains("her"), catKensei);
            // (a range of two parts: 手/her, 口/her, 口/him here - three combos; two weak ones and vary need four)
            k.Range = "houshi";
            var two = new List<string> { "手", "口" };
            int n2 = k.Candidates("houshi", two).Count;
            k.WeakN = 2;
            k.Vary = true;
            string why2k = k.CannotStart(n2);
            k.PartsForCheck = two;
            string started2k = k.Start();
            k.Vary = false;
            string ok2k = k.CannotStart(n2);
            k.WeakN = 1;
            Check("献精室: a full change of the weak combos needs 2K <= N (GPT on #115) - with fewer than 4 candidates, 2 weak combos and vary are refused (nothing starts); without vary they may",
                  n2 >= 2 && n2 < 4 && why2k == "vary_needs_2k_le_n" && started2k == "vary_needs_2k_le_n" && !k.Active && ok2k == "", n2 + ": " + why2k + " / " + started2k + " / " + ok2k);

            // ---- a run through Muse's packages (numbers, one fixed weak combo, no decay, no tolerance) ----
            k.DecayOn = false;
            k.Tol = 0;
            k.Disclose = "numbers";
            k.Tighten = "standard";
            k.Need = 240;
            k.SeedForCheck = 777u;
            int herClimaxes = h.Gauge.Climaxes;
            string st1 = k.Start();
            yield return Wait(0.6f);
            var stS = Json.Parse(Http("GET", "/api/state", null));
            var sk = Json.Obj(stS, "kensei");
            Check("献精室: the run starts - kensei_started, the state's kensei (its run, turn 1, the candidates), her gauge held at 0",
                  st1 == "" && k.Active && EventCount("kensei_started") >= 1 && Json.Str(sk, "run") == k.RunId && Json.Num(sk, "turn") == 1
                  && Json.Arr(sk, "candidates") != null && Json.Str(Json.Obj(stS, "gauge"), "held") == "kensei" && h.Gauge.Value == 0f,
                  st1 + " " + Json.Write(Json.Obj(stS, "gauge")) + " run " + k.RunId);
            if (!k.Active) yield break;
            string big = KCombo(2), near = KCombo(1), non = KCombo(0);
            Note("kensei: weak " + k.Run.WeakNow[0].Key + " near " + near + " non " + non + " limit " + k.Run.Limit);
            var tags = KTags();
            // turn 1: a near move - no coming; the came line passed by
            yield return KTurn(new Dictionary<string, object>(tags), KMove("どうかな。", 1, "c1", KPlay(near)), With(Line("（出た！）いっぱい出たね。"), "came", true), Line("気持ちいい？"));
            var tv1 = LastEvent("kensei_turn");
            bool skipped = !kShown.Exists(x => x.Contains("いっぱい出たね")) && kShown.Exists(x => x.Contains("気持ちいい"));
            Check("献精室: a near move - taken (kensei_turn turn 1, not come, the total 0), the gauge's one step of 20, the play started, the came line passed by",
                  tv1 != null && Json.Num(tv1, "turn") == 1 && !True(tv1, "came") && Json.Num(tv1, "total") == 0 && !Json.Has(tv1, "amount")
                  && k.Run.Gauge == 20 && k.Run.Turn == 2 && pc.Active && Json.Str(pc.Current, "id") == KPlay(near) && skipped,
                  Json.Write(tv1) + " gauge " + k.Run.Gauge + " shown " + string.Join(" / ", kShown.ToArray()));
            // the refusals that use no turn
            int rf0 = k.RefusedForCheck, te0 = k.TurnEventsForCheck;
            yield return KTurn(new Dictionary<string, object>(tags), KMove("古い番号。", 1, "c-old", KPlay(big)));
            yield return KTurn(new Dictionary<string, object>(tags), KMove("同じ名前。", 2, "c1", KPlay(big)));
            yield return KTurn(new Dictionary<string, object>(tags), With(Line("違うラン。"), "move", D("run", "k-other", "turn", 2, "request", "c-run", "play", KPlay(big))));
            yield return KTurn(new Dictionary<string, object>(tags), KMove("候補でない。", 2, "c-nc", "sonyu_09"));
            yield return KTurn(new Dictionary<string, object>(), KMove("印が無い。", 2, "c-untag", KPlay(big)));
            Check("献精室: refused moves use no turn - stale_turn, duplicate_request, other_run, not_candidate, untargeted (block_refused field move)",
                  k.RefusedForCheck - rf0 == 5 && k.TurnEventsForCheck == te0 && k.Run.Turn == 2 && Refusals("move", "stale_turn") >= 1
                  && Refusals("move", "duplicate_request") >= 1 && Refusals("move", "other_run") >= 1 && Refusals("move", "not_candidate") >= 1 && Refusals("move", "untargeted") >= 1,
                  "refused " + (k.RefusedForCheck - rf0) + " turn " + k.Run.Turn);
            // her climax off meanwhile
            int ik0 = Refusals("anim", "in_kensei") + Refusals("play.finish", "in_kensei") + Refusals("location", "in_kensei") + Refusals("standby", "in_kensei");
            string locWas = h.Stage.Location, elsewhere = locWas == "bedroom" ? "myroom" : "bedroom";
            yield return KTurn(new Dictionary<string, object>(tags), With(Line("イって。"), "anim", "climax"), With(Line("出して。"), "play", D("finish", "outside")),
                               With(Line("向こうへ。"), "location", elsewhere));
            SendPkg(D("launch", Launch.Id, "character", h.CharacterId(), "standby", D("on", "climax", "request", "k-sb", "play_serial", pc.Serial, "count", h.Gauge.Climaxes + 1)), Line("……っ。"));
            yield return Until(() => Refusals("standby", "in_kensei") >= 1, 5f);
            int ik1 = Refusals("anim", "in_kensei") + Refusals("play.finish", "in_kensei") + Refusals("location", "in_kensei") + Refusals("standby", "in_kensei");
            Check("献精室: her climax is off in a run - anim climax, play.finish, a location, standby on her climax refused in_kensei; her gauge still 0, no climax of hers",
                  ik1 - ik0 == 4 && h.Gauge.Value == 0f && h.Gauge.Climaxes == herClimaxes && k.Run.Turn == 2 && h.Stage.Location == locWas,
                  (ik1 - ik0) + " gauge " + h.Gauge.Value + " climaxes " + h.Gauge.Climaxes + "/" + herClimaxes + " turn " + k.Run.Turn + " at " + h.Stage.Location + " (was " + locWas + ")");
            // a move whose record cannot be written: taken back - not counted, nothing told; the same request again goes
            // (GPT's post-review of #118, 1)
            int sf0 = Refusals("move", "save_failed"), tes = k.TurnEventsForCheck;
            int recTurns = (Json.Arr(Kensei.ReadRecord(k.FilePath(k.RunId)), "turns") ?? new List<object>()).Count;
            k.FailWritesForCheck = true;
            yield return KTurn(new Dictionary<string, object>(tags), KMove("ここはどう？", 2, "c2", KPlay(big)), Line("どう？"));
            k.FailWritesForCheck = false;
            int recTurns2 = (Json.Arr(Kensei.ReadRecord(k.FilePath(k.RunId)), "turns") ?? new List<object>()).Count;
            Check("献精室: a move whose record cannot be written is taken back - refused save_failed, the turn, the gauge and the record as they were, no kensei_turn",
                  Refusals("move", "save_failed") - sf0 == 1 && k.Run.Turn == 2 && k.Run.Gauge == 20 && k.TurnEventsForCheck == tes && recTurns == 1 && recTurns2 == 1,
                  "turn " + k.Run.Turn + " gauge " + k.Run.Gauge + " record " + recTurns + "/" + recTurns2);
            // turns 2, 3: big moves - he comes at 3 (20 + 45 + 45 = 110: 120 + 10), outside; the came line shown
            int pf0 = EventCount("play_finish");
            yield return KTurn(new Dictionary<string, object>(tags), KMove("ここはどう？", 2, "c2", KPlay(big)), Line("どう？"));
            Check("献精室: the same request again after save_failed is taken (turn 2)", k.Run.Turn == 3 && k.Run.Gauge == 65, "turn " + k.Run.Turn + " gauge " + k.Run.Gauge);
            yield return KTurn(new Dictionary<string, object>(tags), KMove("もっと。", 3, "c3", KPlay(big)), With(Line("いっぱい出たね。"), "came", true), Line("まだいける？"));
            var tv3 = LastEvent("kensei_turn");
            var last = k.Last;
            Check("献精室: big moves - he comes at the gauge's 100 (kensei_turn came, amount 120 + the 10 past it, the total), the finish outside (play_finish), the came line shown",
                  tv3 != null && Json.Num(tv3, "turn") == 3 && True(tv3, "came") && Json.Num(tv3, "amount") == 130 && Json.Num(tv3, "total") == 130
                  && last != null && last.FinishPlace.StartsWith("outside") && EventCount("play_finish") > pf0 && kShown.Exists(x => x.Contains("いっぱい出たね")) && k.Run.Gauge == 0,
                  Json.Write(tv3) + " finish " + (last != null ? last.FinishPlace : "") + " shown " + string.Join(" / ", kShown.ToArray()));
            // the answers Muse writes down: with the next move, alone, once
            for (int turn = 4; turn <= 6 && k.Active; turn++)
            {
                var ex = new Dictionary<string, object>(tags);
                if (turn == 4) ex["kensei_answers"] = new List<object> { D("run", k.RunId, "turn", 3, "tap", "弱点級", "text", "すごく効いた") };
                yield return KTurn(ex, KMove("続けるね。", turn, "c" + turn, KPlay(big)), Line("どう？"));
            }
            var tvEnd = LastEvent("kensei_ended");
            var reveal = Json.Obj(tvEnd, "reveal");
            string runId = k.RunId;
            int endedN = 0;
            foreach (var e in AllEvents()) if (Json.Str(e, "type") == "kensei_ended" && Json.Str(e, "run") == runId) endedN++;
            Check("献精室: the need reached (240: 130 + 155 at turn 6) - success, kensei_ended once (the total, the score x the coefficient, the reveal's truth), her gauge free again",
                  !k.Active && k.Run.Result == "success" && k.Run.Total == 285 && endedN == 1 && Json.Str(tvEnd, "result") == "success" && Json.Num(tvEnd, "score") == KenseiRules.HalfUp(285 * k.Coef)
                  && Json.Arr(reveal, "turns") != null && Json.Arr(reveal, "turns").Count == 6 && Json.Arr(reveal, "phases") != null && h.Gauge.Held.Length == 0,
                  k.Run.Result + " " + k.Run.Total + " ended " + endedN + " score " + Json.Num(tvEnd, "score"));
            var sc = k.Scores();
            Check("献精室: the score kept once a run (kensei_scores.json), the record written (kensei-<run>.json)", Json.Obj(sc, runId) != null && Json.Num(Json.Obj(sc, runId), "score") == k.Score
                  && File.Exists(k.FilePath(runId)), Json.Write(Json.Obj(sc, runId)));
            SendPkg(D("kensei_answers", new List<object> { D("run", runId, "turn", 6, "tap", "かなり", "text", null) }));
            yield return Wait(1.0f);
            int ar0 = Refusals("kensei_answers", "already_recorded");
            SendPkg(D("kensei_answers", new List<object> { D("run", runId, "turn", 6, "tap", "かなり", "text", null) }));
            SendPkg(D("kensei_answers", new List<object> { D("run", runId, "turn", 6, "tap", "あまり", "text", null), D("run", "k-other", "turn", 1, "tap", "x", "text", null) }));
            yield return Until(() => Refusals("kensei_answers", "other_run") >= 1, 5f);
            var rec = Kensei.ReadRecord(k.FilePath(runId));
            string a3t = "", a6t = "";
            foreach (var o in Json.Arr(rec, "turns") ?? new List<object>())
            {
                if (Json.Num(o, "turn") == 3) a3t = Json.Str(Json.Obj(o, "answer"), "tap", "");
                if (Json.Num(o, "turn") == 6) a6t = Json.Str(Json.Obj(o, "answer"), "tap", "");
            }
            Check("献精室: the answers written down - with the next move (turn 3), alone after the end (turn 6, nothing shown); the same again nothing, another refused already_recorded, another run other_run",
                  a3t == "弱点級" && a6t == "かなり" && Refusals("kensei_answers", "already_recorded") - ar0 == 1 && Refusals("kensei_answers", "other_run") >= 1,
                  "turn 3 '" + a3t + "' turn 6 '" + a6t + "'");
            Check("献精室: his gauge over his screen while the run went / after it (the HUD)", h.UiForCheck.KenseiShown.Contains("献精室"), h.UiForCheck.KenseiShown);

            // ---- what the levels and the hidden modes let out ----
            foreach (var mode in new[] { "levels", "none" })
            {
                k.Disclose = mode;
                k.Need = 360;
                k.SeedForCheck = 4242u;
                int at = AllEvents().Count;
                k.Start();
                yield return Wait(0.4f);
                if (!k.Active) { Check("献精室 (" + mode + "): the run starts", false, k.Error); continue; }
                string b2 = KCombo(2);
                var states = new List<object>();
                for (int turn = 1; turn <= 3; turn++)
                {
                    yield return KTurn(new Dictionary<string, object>(tags), KMove("……", turn, mode + turn, KPlay(b2)), Line("どう？"));
                    states.Add(Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "kensei"));
                }
                var told = new List<object>(states);
                var evs = AllEvents();
                for (int i = at; i < evs.Count; i++)
                    if (Json.Str(evs[i], "type") == "kensei_turn" || Json.Str(evs[i], "type") == "kensei_started") told.Add(evs[i]);
                var keys = new HashSet<string>();
                Keys(told, keys);
                var nums = new List<double>();
                Numbers(told, nums);
                var bad = new List<string>();
                foreach (var key in new[] { "amount", "total", "score", "title", "grade", "rise", "gauge_before", "gauge_after", "seed", "phases", "reveal", "overkill", "factor", "decay_uses" })
                    if (keys.Contains(key)) bad.Add(key);
                if (mode == "none" && keys.Contains("progress")) bad.Add("progress");
                foreach (var t in k.Run.Turns)
                    if (t.Came && (nums.Contains(t.Amount) || nums.Contains(t.Total))) bad.Add("the number " + t.Amount + "/" + t.Total);
                bool came = k.Run.Turns.Exists(t => t.Came);
                Check("献精室 (" + mode + "): what Muse is told in the run holds none of the hidden fields nor the run's amounts" + (mode == "levels" ? " - only the progress level" : " - nothing of the amounts"),
                      bad.Count == 0 && came && (mode == "none" || keys.Contains("progress")), string.Join(",", bad.ToArray()) + " came " + came);
                if (mode == "none")
                {
                    // the run read back from its record (as after a restart): the same turns, nothing counted again
                    var k2 = new Kensei(h);
                    k2.FindPending();
                    bool found = k2.Pending != null && Json.Str(k2.Pending, "run") == k.RunId;
                    var turnsWas = k.Run.Turns.ConvertAll(t => t.Turn + ":" + t.Grade + ":" + t.Amount + ":" + t.Total);
                    string rz = found ? k2.Resume() : "not found";
                    var turnsNow = k2.Run != null ? k2.Run.Turns.ConvertAll(t => t.Turn + ":" + t.Grade + ":" + t.Amount + ":" + t.Total) : new List<string>();
                    int pkgs = 0, lines = 0;
                    foreach (var o in Json.Arr(Kensei.ReadRecord(k.FilePath(k.RunId)), "turns") ?? new List<object>())
                    {
                        var pk = Json.Obj(o, "package");
                        if (pk != null) pkgs++;
                        foreach (var b in Json.Arr(pk, "blocks") ?? new List<object>()) if (Json.Str(b, "text", "") == "……") lines++;
                    }
                    Check("献精室: an unfinished run read back from its record (a restart) - the same seed and moves give the same turns, not counted again, the next turn the same; its packages and lines kept in the record (GPT's post-review of #118, 2)",
                          found && rz == "" && string.Join(",", turnsWas.ToArray()) == string.Join(",", turnsNow.ToArray()) && k2.Run.Turn == k.Run.Turn && k2.Run.Gauge == k.Run.Gauge
                          && pkgs == 3 && lines == 3, rz + " " + string.Join(",", turnsNow.ToArray()) + " packages " + pkgs + " lines " + lines);
                }
                int endAt = AllEvents().Count;
                k.Abandon();
                yield return Wait(0.6f);
                var ab = LastEvent("kensei_ended");
                Check("献精室 (" + mode + "): stopped by the user - kensei_ended abandoned, no score; its reveal tells the truth now",
                      Json.Str(ab, "result") == "abandoned" && Json.Num(ab, "score") == 0 && Json.Arr(Json.Obj(ab, "reveal"), "turns") != null && Json.Arr(Json.Obj(ab, "reveal"), "turns").Count == 3,
                      Json.Str(ab, "result"));
            }
            // ---- a move waiting when its run is stopped and another begins: it lapses, the new run untouched (GPT's post-review of #118, 4) ----
            k.Disclose = "numbers";
            k.SeedForCheck = 99u;
            k.Start();
            var runA = k.Run;
            if (runA != null && k.Active)
            {
                yield return KTurn(new Dictionary<string, object>(tags), KMove("はじめるね。", 1, "a1", KPlay(KCombo(1))), Line("どう？"));
                k.HoldMovesForCheck = true;
                int rc0 = Refusals("move", "run_changed"), teA = k.TurnEventsForCheck;
                SendPkg(new Dictionary<string, object>(tags), KMove("次はこれ。", 2, "a2", KPlay(KCombo(2))), Line("どう？"));
                yield return Until(() => bp.Moving, 10f);
                bool waited = bp.Moving;
                k.Abandon();
                k.SeedForCheck = 100u;
                k.Start();
                var runB = k.Run;
                k.HoldMovesForCheck = false;
                yield return KDrain(20f);
                Check("献精室: a move waiting when its run was stopped and another began lapses (run_changed) - the new run still at turn 1, nothing of the old move in it",
                      waited && Refusals("move", "run_changed") - rc0 == 1 && runB != null && !ReferenceEquals(runA, runB) && runB.Turn == 1 && runB.Turns.Count == 0
                      && runA.Turns.Count == 1 && k.TurnEventsForCheck == teA, "waited " + waited + " B turn " + (runB != null ? runB.Turn : -1));
                // a play found without its finish outside at a move: not taken, no candidate from then on (GPT's post-review of #118, 5)
                if (runB != null && k.Active)
                {
                    string np = KPlay(KCombo(1));
                    k.NoOutsideForCheck = new HashSet<string> { np };
                    int no0 = Refusals("move", "no_outside");
                    yield return KTurn(new Dictionary<string, object>(tags), KMove("これは？", 1, "b1", np), Line("どう？"));
                    k.NoOutsideForCheck = null;
                    bool gone = true;
                    foreach (var kv in k.PlaysOf) if (kv.Value.Contains(np)) gone = false;
                    foreach (var kv in k.Candidates("houshi")) if (kv.Value.Contains(np)) gone = false;
                    bool tried;
                    k.FinishTried(np, out tried);
                    Check("献精室: a play without its finish outside at a move - refused no_outside (no turn used), out of the run's plays and the candidates, remembered so",
                          Refusals("move", "no_outside") - no0 == 1 && runB.Turn == 1 && gone && !tried, np);
                    k.Abandon();
                }
            }
            else Check("献精室: a third run starts (for the run changed while a move waits)", false, k.Error);
            // ---- another character's unfinished run: not resumed, its record not taken over; ended, Muse not told (GPT's post-review of #118, 3) ----
            {
                var src = Kensei.ReadRecord(k.FilePath(runId));
                if (src != null)
                {
                    src["run"] = "k-otherchar";
                    src["result"] = "";
                    src["character"] = "card:someone_else.png";
                    src["room"] = h.Stage.Location;
                    string f = k.FilePath("k-otherchar");
                    File.WriteAllText(f, Json.Write(src), new System.Text.UTF8Encoding(false));
                    var k3 = new Kensei(h);
                    k3.FindPending();
                    bool pend = k3.Pending != null && Json.Str(k3.Pending, "run") == "k-otherchar";
                    int ks0 = EventCount("kensei_started"), ke0 = EventCount("kensei_ended");
                    string rz3 = pend ? k3.Resume() : "not found";
                    string charAfter = Json.Str(Kensei.ReadRecord(f), "character", "");
                    k3.EndPending("interrupted");
                    yield return Wait(0.8f);
                    var after = Kensei.ReadRecord(f);
                    Check("献精室: another character's unfinished run is not resumed (other_character, its record still its own); ended there, it is closed interrupted and Muse is not told",
                          pend && rz3 == "other_character" && charAfter == "card:someone_else.png" && Json.Str(after, "result") == "interrupted"
                          && EventCount("kensei_started") == ks0 && EventCount("kensei_ended") == ke0 && k3.Pending == null,
                          rz3 + " " + charAfter + " " + Json.Str(after, "result"));
                }
                else Check("献精室: another character's unfinished run (no record to copy)", false);
            }
            if (k.Active) k.Abandon();
            if (pc.Active) pc.End("check");
            yield return Until(() => !pc.Climax.Running, 15f);
            yield return Wait(0.5f);
        }

    }
}
