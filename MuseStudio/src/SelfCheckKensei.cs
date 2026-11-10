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

        static int[] ScriptMoves = { 2, 2, 4, 3, 5, 1, 1, 4, 4, 4, 0, 0, 0, 2, 2, 3, 3, 5, 5, 1, 1, 4, 4, 0, 2, 3, 5 };
        // (bridge/tests/test_kensei_data.py's SCRIPTED2 - version 2, a weak spot a play: turn, grade, rise, gauge after, amount, total)
        static readonly int[,] Scripted = { { 1, 0, 8, 8, 0, 0 }, { 2, 0, 6, 14, 0, 0 }, { 3, 2, 30, 44, 0, 0 }, { 4, 3, 45, 89, 0, 0 }, { 5, 1, 20, 0, 73, 73 },
            { 6, 1, 20, 20, 0, 73 }, { 7, 1, 20, 40, 0, 73 }, { 8, 3, 45, 85, 0, 73 }, { 9, 3, 45, 0, 145, 218 }, { 10, 0, 8, 8, 0, 218 },
            { 11, 1, 20, 28, 0, 218 }, { 12, 1, 20, 48, 0, 218 }, { 13, 1, 20, 68, 0, 218 }, { 14, 3, 45, 0, 125, 343 }, { 15, 1, 20, 20, 0, 343 } };

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
            if (key.Length > 0 && !key.Contains("/")) return key;          // (a play already: version 2's items)
            if (!h.Kensei.PlaysOf.TryGetValue(key, out l) || l.Count == 0) return "";
            foreach (var p in l) if (!p.StartsWith("al_")) return p;
            return l[0];
        }

        // a combo of the run's candidates of a grade under the weak now ("" none)
        // a play of the run's items of a grade under the weak now (3 the weak play itself, 2 its combo's other plays, 1 one axis,
        // 0 none; "" none) - an original play first (quick to start)
        string KCombo(int grade)
        {
            var k = h.Kensei;
            string any = "";
            foreach (var it in k.Run.Items)
            {
                if (KenseiRun.GradeOf(it, k.Run.WeakNow) != grade) continue;
                string p = KPlay(it.Id);
                bool listed = false;
                foreach (var kv in k.PlaysOf) if (kv.Value.Contains(p)) listed = true;
                if (!listed) continue;
                if (!p.StartsWith("al_")) return p;
                if (any.Length == 0) any = p;
            }
            return any;
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
                var items = new List<KenseiItem> { new KenseiItem("p_b1", new KenseiCombo("口", "her")), new KenseiItem("p_b2", new KenseiCombo("口", "her")),
                    new KenseiItem("p_c1", new KenseiCombo("口", "him")), new KenseiItem("p_a1", new KenseiCombo("手", "her")),
                    new KenseiItem("p_a2", new KenseiCombo("手", "her")), new KenseiItem("p_d1", new KenseiCombo("胸", "her")) };
                var run = new KenseiRun(k.Rules, items, 4242u, 1, true, true, 10, 360, "standard");
                bool same = run.Limit == 15 && run.WeakNow[0].Id == "p_a1" && k.Rules.Unit == "play";
                int n = 0;
                foreach (int m in ScriptMoves)
                {
                    if (run.Result.Length > 0) break;
                    var t = run.Step(items[m], items[m].Id, "q" + run.Turn);
                    if (n >= Scripted.GetLength(0) || t.Turn != Scripted[n, 0] || t.Grade != Scripted[n, 1] || t.Rise != Scripted[n, 2] || t.After != Scripted[n, 3]
                        || t.Amount != Scripted[n, 4] || t.Total != Scripted[n, 5]) same = false;
                    n++;
                }
                Check("献精室: the app's scoring counts the scripted run as tools/kensei_sim.py does (version 2, a weak spot a play: every grade - the play, its combo, one axis, none - the decay, the tolerance's amount, the redraws, the failure at the limit)",
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
            yield return Wait(0.6f);
            string idleScene = Json.Str(h.Stage.Cfg, "idle_scene", "idle"), idleCam = Json.Str(Data.Load("cameras.json"), "default", "medium");
            bool idle1 = h.Stage.SceneKey == idleScene && h.Stage.CameraKey == idleCam;
            Check("献精室: the service plays here tried once for their finish outside (quietly: no play events) - none left untried, the candidates only those that play it; then back to the idle (her stance, the default camera)",
                  untried0 > 0 && k.Untried("houshi").Count == 0 && outside > 0 && onlyVerified && !k.Scanning && !pc.Active && idle1,
                  untried0 + " tried in " + (Time.realtimeSinceStartup - scanT).ToString("F0") + " s: outside " + outside + ", none " + none + "; scene " + h.Stage.SceneKey + " camera " + h.Stage.CameraKey);
            {
                // stopped midway (the panel's 止める): back to the idle all the same, the rest left untried
                int left0 = k.Untried("both").Count;
                MuseStudioPlugin.Instance.StartCoroutine(k.Scan("both"));
                yield return Until(() => k.Scanning && k.ScanLeft <= left0 - 2, 20f);
                k.StopScan();
                yield return Until(() => !k.Scanning, 15f);
                yield return Wait(0.6f);
                int left1 = k.Untried("both").Count;
                Check("献精室: a trial stopped midway (止める) - back to the idle too, the rest left untried",
                      left0 > 2 && left1 > 0 && left1 < left0 && !pc.Active && h.Stage.SceneKey == idleScene && h.Stage.CameraKey == idleCam,
                      left0 + " -> " + left1 + " scene " + h.Stage.SceneKey + " camera " + h.Stage.CameraKey);
            }
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
            // (N is what the weak are drawn from - the plays now (#115 P+): the count's own check, as the panel and the start use it)
            k.Range = "houshi";
            k.WeakN = 2;
            k.Vary = true;
            string why2k = k.CannotStart(3), ok2k4 = k.CannotStart(4);
            k.Vary = false;
            string ok2k = k.CannotStart(3);
            k.WeakN = 1;
            int nPlays = k.CountOf(k.Candidates("houshi"));
            Check("献精室: a full change of the weak spots needs 2K <= N (GPT on #115) - with 3 candidates, 2 weak ones and vary are refused, with 4 not, without vary not; N is the plays (version 2)",
                  why2k == "vary_needs_2k_le_n" && ok2k4 == "" && ok2k == "" && !k.Active && nPlays > hou.Count, why2k + " / " + ok2k4 + " / " + ok2k + " plays " + nPlays);

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
            string big = KCombo(3), sameCombo = KCombo(2), near = KCombo(1), non = KCombo(0);
            Note("kensei: weak " + k.Run.WeakNow[0].Id + " (" + k.Run.WeakNow[0].Combo.Key + ") same combo " + sameCombo + " near " + near + " non " + non + " limit " + k.Run.Limit);
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
            // the answers Muse writes down: with the next move, alone, once. (Turn 4 with the record read-only: written all the
            // same - the old one aside, the new one in, the leftover cleared - GPT's re-review of #119, 2; the scores' file not
            // writable at the end: kept for later, 1)
            k.FailScoresForCheck = true;
            string recPath = k.FilePath(k.RunId);
            for (int turn = 4; turn <= 6 && k.Active; turn++)
            {
                var ex = new Dictionary<string, object>(tags);
                if (turn == 4) ex["kensei_answers"] = new List<object> { D("run", k.RunId, "turn", 3, "tap", "弱点級", "text", "すごく効いた") };
                if (turn == 4) File.SetAttributes(recPath, FileAttributes.ReadOnly);
                yield return KTurn(ex, KMove("続けるね。", turn, "c" + turn, KPlay(big)), Line("どう？"));
                if (turn == 4)
                {
                    int rt = (Json.Arr(Kensei.ReadRecord(recPath), "turns") ?? new List<object>()).Count;
                    Check("献精室: a read-only record (GPT's re-review of #119, 2) - the move written and taken all the same: the turn on, the record with it, no leftover",
                          k.Run.Turn == 5 && rt == 4 && !File.Exists(recPath + ".bak") && !File.Exists(recPath + ".part"), "turn " + k.Run.Turn + " record " + rt);
                }
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
            int scoreA = k.Score;
            Check("献精室: the scores' file not writable at the end - the score kept for later (its own run's), the record written (kensei-<run>.json)",
                  Json.Obj(sc, runId) == null && k.ScoresDueForCheck == 1 && scoreA > 0 && File.Exists(k.FilePath(runId)), Json.Write(Json.Obj(sc, runId)) + " due " + k.ScoresDueForCheck);
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
                if (mode == "levels")
                {
                    // the score of the run before, tried again while this one goes: its own id and values, nothing of this one
                    // (GPT's re-review of #119, 1); a wrong entry put right from its record
                    k.FailScoresForCheck = false;
                    yield return Until(() => k.ScoresDueForCheck == 0, 8f);
                    var scB = k.Scores();
                    var ea = Json.Obj(scB, runId);
                    Check("献精室: the score of the run before kept when the file can be written again - its own run's (success, its score), the run going on now not in it",
                          k.ScoresDueForCheck == 0 && ea != null && Json.Str(ea, "result") == "success" && Json.Num(ea, "score") == scoreA && Json.Obj(scB, k.RunId) == null,
                          Json.Write(ea) + " now " + k.RunId + " " + Json.Write(Json.Obj(scB, k.RunId)));
                    var sdoc = Json.Parse(File.ReadAllText(Path.Combine(Replay.Dir, "kensei_scores.json"), System.Text.Encoding.UTF8)) as Dictionary<string, object>;
                    Json.Obj(sdoc, "runs")[runId] = D("result", "", "total", 0, "coefficient", 1, "score", 0, "title", "", "ended", "x");
                    File.WriteAllText(Path.Combine(Replay.Dir, "kensei_scores.json"), Json.Write(sdoc), new System.Text.UTF8Encoding(false));
                    k.ReconcileScores();
                    var ea2 = Json.Obj(k.Scores(), runId);
                    Check("献精室: a score kept wrong (an unfinished 0) put right from the run's record", Json.Str(ea2, "result") == "success" && Json.Num(ea2, "score") == scoreA,
                          Json.Write(ea2));
                }
                string b2 = KCombo(3);
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
                SendPkg(new Dictionary<string, object>(tags), KMove("次はこれ。", 2, "a2", KPlay(KCombo(3))), Line("どう？"));
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
            // ---- a run of the rules before (version 1) resumed and ended: the next run is of the rules shipped now, the old record
            // still the old (GPT's post-review of #122) ----
            {
                if (k.Active) k.Abandon();
                yield return Wait(0.4f);
                k.Vary = false;
                k.WeakN = 1;
                k.Range = "houshi";
                k.Disclose = "numbers";
                k.SeedForCheck = 4321u;
                string sv = k.Start();
                string v1Id = "k-v1-" + DateTime.Now.Ticks;
                string v1File = k.FilePath(v1Id);
                bool made = false;
                if (sv.Length == 0 && k.Active)
                {
                    string srcId = k.RunId;
                    k.Abandon();
                    yield return Wait(0.6f);
                    var src = Kensei.ReadRecord(k.FilePath(srcId));
                    if (src != null)
                    {
                        // (the record as version 1 wrote it: its rules - the combo unit, three grades, its limit - its combos as the items, no turn yet)
                        var v1 = Json.Parse(Json.Write(Json.Obj(src, "rules"))) as Dictionary<string, object>;
                        v1["version"] = 1.0;
                        v1.Remove("unit");
                        Json.Obj(v1, "rise").Remove("combo");
                        Json.Obj(v1, "base").Remove("combo");
                        var lim = Json.Obj(v1, "limit");
                        lim["per_axis_value"] = 0.75;
                        lim["per_redraw"] = 0.25;
                        lim.Remove("per_play");
                        var its = new List<object>();
                        foreach (var o in Json.Arr(src, "candidates") ?? new List<object>()) its.Add(D("id", Json.Str(o, "combo"), "combo", Json.Str(o, "combo")));
                        src["rules"] = v1;
                        src["rules_version"] = 1.0;
                        src["unit"] = "combo";
                        src["items"] = its;
                        src["turns"] = new List<object>();
                        src["run"] = v1Id;
                        src["result"] = "";
                        File.WriteAllText(v1File, Json.Write(src), new System.Text.UTF8Encoding(false));
                        made = true;
                    }
                }
                k.Pending = made ? Kensei.ReadRecord(v1File) : null;
                k.PendingFile = v1File;
                string rz = made ? k.Resume() : "no record";
                bool oldRun = rz == "" && k.Active && k.Run.R.Unit == "combo" && k.Run.R.Version == 1 && Json.Str(k.Info(), "unit") == "combo";
                if (k.Active) k.Abandon();
                yield return Wait(0.6f);
                var oldRec = Kensei.ReadRecord(v1File);
                bool oldKept = Json.Num(oldRec, "rules_version") == 1 && Json.Str(oldRec, "unit") == "combo" && Json.Num(Json.Obj(oldRec, "rules"), "version") == 1
                               && Json.Str(oldRec, "result") == "abandoned";
                k.SeedForCheck = 4322u;
                string sn = k.Start();
                var newRec = k.Active ? Kensei.ReadRecord(k.FilePath(k.RunId)) : null;
                bool newRun = sn.Length == 0 && k.Active && k.Run.R.Unit == "play" && k.Run.R.Version == 2 && k.Rules.Unit == "play" && Json.Str(k.Info(), "unit") == "play"
                              && Json.Num(newRec, "rules_version") == 2 && Json.Str(newRec, "unit") == "play";
                Check("献精室 (GPT's post-review of #122): a version 1 run resumed goes on by its own rules; ended, the next run in the same launch is of the rules shipped now (unit play, version 2) - the old record still version 1",
                      oldRun && oldKept && newRun, "start " + sv + " resume " + rz + " old run " + oldRun + " old record " + oldKept + " new " + sn + " " + newRun);
                if (k.Active) k.Abandon();
                yield return Wait(0.4f);
            }
            if (k.Active) k.Abandon();
            if (pc.Active) pc.End("check");
            yield return Until(() => !pc.Climax.Running, 15f);
            yield return Wait(0.5f);
        }

    }
}
