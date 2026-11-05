using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // A play's climaxes and finishes (the Godot app's play_climax.gd; data/plays.json climax). Her climax is the app's: her
    // gauge at 1 plays her climax (sonyu WF_ / SF_IN, aibu Orgasm_*, masturbation / lesbian Orgasm). The ejaculation is
    // declared (the user's decision 2026-11-02): Muse sends play.finish "soon" (hold the OLoop) and / or a place (inside /
    // outside in sonyu, drink / vomit / outside in houshi); from the hold the place's OLoop is skipped. A hold left without a
    // place for hold_wait_seconds (nothing showing) ends in the mode's default finish. Both come together when, at the
    // declaration, her gauge is at the threshold in a play that stimulates her. The user's gauge rises with the pace (shown in
    // the play's info; at 1 it starts nothing: the declaration is Muse's). Each step plays its state once, for some seconds, or
    // holds, on both bodies. The position's siru list (h/list fileSiruPaste) puts the liquid on her as the finish plays.
    public class Climax
    {
        readonly PlayCtl pc;
        readonly Stage stage;
        readonly Bridge bridge;
        readonly Gauge gauge;
        readonly Dictionary<string, object> cfg;
        public float Player;              // (no more: the user's climax is declared - the user's decision of 2026-11-04; kept at 0)
        float playerIdle, playerAfter;
        public int PlayerClimaxes;
        List<object> seq = new List<object>();
        public string SeqName = "";
        public string LastPlace = "";     // the place of the last finish (the drip / spit of the liquid)
        int step = -1;
        float stepT;
        public bool Holding;
        float holdWait;
        bool declared, together, maleCame, busy;
        string declLoop = "";
        string queuedFinish = "";
        readonly List<KeyValuePair<string, List<KeyValuePair<float, List<int>>>>> siruRows = new List<KeyValuePair<string, List<KeyValuePair<float, List<int>>>>>();
        string siruState = "";
        float siruPrev;
        public Func<bool> BlocksIdle;     // no package showing (the hold's wait counts)
        static readonly string[] Flows = { "pull_drip", "pull", "insert" };

        public Climax(PlayCtl pc, Stage stage, Bridge bridge, Gauge gauge, Dictionary<string, object> cfg)
        {
            this.pc = pc;
            this.stage = stage;
            this.bridge = bridge;
            this.gauge = gauge;
            this.cfg = cfg ?? new Dictionary<string, object>();
        }

        public bool Running { get { return step >= 0; } }

        // a sequence shown only (the match's declared climax): no play_finish, no climax, no pace change, not in the state
        bool silent;
        public bool Showing { get { return silent && Running; } }

        // The match (MUSE_GUIDE 8; Muse's request of 2026-11-04): a declared climax shown on the declaring body (role: character
        // / player) - in the female part her climax states of the play, in the male part the mode's default finish (outside when
        // out of her), the pre-climax loop left out; then the loop the play was in again (a pace Muse set meanwhile then). False
        // when the play has none for that body (the caller's face only).
        public bool PresentDeclared(string role, bool noMalePart = false)
        {
            if (Running || !pc.Motion) return false;
            string name = "";
            // (the male part's finish: none in the lesbian format, none with a strapon on - nothing to come from; the face only)
            var roleCha = role == "character" ? stage.Cha : stage.Partner != null ? stage.Partner.charInfo : null;
            bool strapped = roleCha != null && Strapon.On(roleCha);
            if (MalePart() == role) name = noMalePart || strapped ? "" : pc.Mode == 2 && !pc.Inserted ? "outside" : DefaultPlace();
            else if (pc.Mode == 5 && FemalePart() != role) name = "female";     // (女性同士: the two's climax, either declares it)
            else if (FemalePart() == role && !(pc.Mode == 2 && !pc.Inserted))
                name = pc.Mode == 2 ? (pc.BaseState() == "SLoop" ? "female_s" : "female_w") : (pc.Mode == 0 || pc.Mode == 3 || pc.Mode == 5) ? "female" : "";
            if (name.Length == 0) return false;
            var steps = Steps(name);
            while (steps.Count > 0 && (string)((List<object>)steps[0])[0] == "OLoop") steps.RemoveAt(0);
            if (steps.Count == 0) return false;
            silent = true;
            seq = steps;
            SeqName = name;
            step = -1;
            Next();
            return true;
        }

        public void Start()
        {
            seq = new List<object>();
            SeqName = "";
            step = -1;
            ClearFinish();
            queuedFinish = "";
            siruRows.Clear();
            siruState = "";
            siruPrev = 0f;
            string file = pc.HInfo != null ? pc.HInfo.paramFemale.fileSiruPaste : null;
            if (string.IsNullOrEmpty(file)) return;
            string text = GlobalMethod.LoadAllListText("h/list/", file, null);
            if (string.IsNullOrEmpty(text)) return;
            string[,] a;
            GlobalMethod.GetListString(text, out a);
            int cols = a.GetLength(1);
            for (int r = 0; r < a.GetLength(0); r++)
            {
                var timings = new List<KeyValuePair<float, List<int>>>();
                for (int j = 1; j + 1 < cols; j += 2)
                {
                    float t;
                    if (string.IsNullOrEmpty(a[r, j]) || string.IsNullOrEmpty(a[r, j + 1])
                        || !float.TryParse(a[r, j], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t)) break;
                    var parts = new List<int>();
                    foreach (var s in a[r, j + 1].Split(',')) { int v; parts.Add(int.TryParse(s, out v) ? v : 0); }
                    timings.Add(new KeyValuePair<float, List<int>>(t, parts));
                }
                siruRows.Add(new KeyValuePair<string, List<KeyValuePair<float, List<int>>>>(a[r, 0] ?? "", timings));
            }
        }

        void ClearFinish()
        {
            Holding = false;
            holdWait = 0f;
            declared = together = maleCame = false;
            LastPlace = "";
            declLoop = "";
        }

        public void Stop()
        {
            silent = false;
            seq = new List<object>();
            SeqName = "";
            step = -1;
            ClearFinish();
            queuedFinish = "";
            if (Json.Has(cfg, "clear_siru_at_end") && cfg["clear_siru_at_end"] is bool && (bool)cfg["clear_siru_at_end"] && stage.Cha != null)
                for (int k = 0; k < 5; k++) FemaleCha().SetSiruFlags((ChaFileDefine.SiruParts)k, 0);
        }

        // ---- who plays which part (reversed: the character in the male part) ----

        ChaControl FemaleCha() { return pc.FemaleCha ?? stage.Cha; }

        public string MalePart() { return pc.Motion && (pc.Mode == 1 || pc.Mode == 2) ? (pc.Reversed ? "character" : "player") : ""; }
        public string FemalePart() { return pc.Reversed ? "player" : "character"; }
        float GaugeOf(string who) { return who == "character" ? gauge.Value : Player; }

        // the finishes of the play's mode that its position has (more than the OLoop: a hand job has no drink)
        public List<string> Places()
        {
            var o = new List<string>();
            if (!pc.Active) return o;
            foreach (var p in Json.Arr(Json.Obj(cfg, "places"), pc.Mode.ToString()) ?? new List<object>())
                foreach (var st in Steps((string)p))
                    if ((string)((List<object>)st)[0] != "OLoop") { o.Add((string)p); break; }
            return o;
        }

        public string CanFinish()
        {
            if (!pc.Active) return "no_play";
            // a service play where the male part serves her (the AL fingering, cunnilingus: giver player): he does not finish -
            // she is the one it is for (the original's service mode always ends in his finish; the trial run of 2026-11-03)
            if (Json.Str(pc.Current, "giver", "") == "player") return "serving";
            if (MalePart().Length == 0 || Places().Count == 0) return "no_male_part";
            if (pc.Res != null && pc.Res.Strapon == MalePart()) return "strapon";
            return "";
        }

        public List<string> Finishes() { return CanFinish().Length == 0 ? Places() : new List<string>(); }

        string DefaultPlace()
        {
            string d = Json.Str(Json.Obj(cfg, "finish_default"), pc.Mode.ToString(), "");
            var all = Places();
            return all.Contains(d) ? d : all.Count > 0 ? all[0] : "";
        }

        // ---- the climaxes ----

        // her gauge reached 1 during the play: her climax
        public void CharacterClimaxed()
        {
            if (busy || Running) return;
            busy = true;
            if (MalePart() == "character") { if (CanFinish().Length == 0) MaleGauge("character"); }
            else FemaleClimax();
            busy = false;
        }

        // the male part's gauge at 1 (plays.json climax.male_gauge): none (the declaration is Muse's), hold (the OLoop for
        // Muse's place), finish (the default finish at once)
        void MaleGauge(string who)
        {
            string how = Json.Str(Json.Obj(cfg, "male_gauge"), who, "none");
            if (how == "none") return;
            if (who == "character") maleCame = true;
            if (how == "hold") Hold("gauge");
            else if (how == "finish") Finish(DefaultPlace(), "gauge");
        }

        // A block's play.finish: "soon" (hold the OLoop) or a place. "" when taken (or kept for after her climax), else why not.
        public string MuseFinish(string what)
        {
            string why = CanFinish();
            if (why.Length > 0) return why;
            if (what != "soon" && !Places().Contains(what)) return "unknown";
            if (Running && !Holding)
            {
                if (!SeqName.StartsWith("female") && Array.IndexOf(Flows, SeqName) < 0) return "busy";
                queuedFinish = what;
                return "";
            }
            if (what == "soon")
            {
                if (!Holding) Hold("muse");
                return "";
            }
            Finish(what, "muse");
            return "";
        }

        void Declare()
        {
            if (declared) return;
            declared = true;
            declLoop = pc.BaseState();
            var t = Json.Obj(cfg, "together");
            var groups = Json.Arr(t, "groups");
            together = groups != null && groups.Contains(pc.Group) && pc.RateOf("character") > 0f && GaugeOf("character") >= Json.Num(t, "threshold", 0.7);
        }

        void Hold(string source)
        {
            Declare();
            Holding = true;
            holdWait = 0f;
            Run("soon", new List<object> { new List<object> { "OLoop", "hold" } }, "soon", source, MalePart());
        }

        void Finish(string place, string source)
        {
            LastPlace = place;
            bool fromHold = Holding;
            Declare();
            string name = place;
            if (together)
            {
                var alt = Json.Arr(Json.Obj(Json.Obj(Json.Obj(cfg, "together"), "sequences"), pc.Mode.ToString()), place);
                if (alt != null && alt.Count >= 2) name = (string)(declLoop == "SLoop" ? alt[1] : alt[0]);
            }
            var steps = Steps(name);
            if (fromHold) while (steps.Count > 0 && (string)((List<object>)steps[0])[0] == "OLoop") steps.RemoveAt(0);
            Holding = false;
            string mp = MalePart();
            bool tg = together;
            busy = true;
            var cause = new Dictionary<string, object> { { "source", "finish" }, { "finish", place }, { "play", Json.Str(pc.Current, "id") } };
            if (!maleCame) ClimaxOf(mp, cause);
            if (tg) ClimaxOf(FemalePart(), new Dictionary<string, object> { { "source", "finish" }, { "finish", place }, { "together", true }, { "play", Json.Str(pc.Current, "id") } });
            busy = false;
            Run(name, steps, place, source, mp);
        }

        void PlayerClimax(Dictionary<string, object> cause)
        {
            PlayerClimaxes++;
            bridge.Event("climax", new Dictionary<string, object> { { "who", "player" }, { "count", PlayerClimaxes }, { "cause", cause }, { "gauge", gauge.Info() },
                { "location", stage.Location }, { "play", pc.Info() } });
        }

        void ClimaxOf(string who, Dictionary<string, object> cause)
        {
            if (who == "player") PlayerClimax(cause);
            else if (who == "character") gauge.Add(2f, cause);
        }

        void FemaleClimax()
        {
            string name = "";
            if (pc.Mode == 2 && !pc.Inserted) name = "";      // out: no inserted climax motion (the gauge's climax still counts)
            else if (pc.Mode == 2) name = pc.BaseState() == "SLoop" ? "female_s" : "female_w";
            else if (pc.Mode == 0 || pc.Mode == 3 || pc.Mode == 5) name = "female";
            if (name.Length > 0) Run(name, Steps(name), "climax", "app", FemalePart());
        }

        // the mode's sequence of that name: its steps whose state the position has
        List<object> Steps(string name)
        {
            var o = new List<object>();
            foreach (var st in Json.Arr(Json.Obj(Json.Obj(cfg, "sequences"), pc.Mode.ToString()), name) ?? FlowFallback(name))
                if (pc.HasState((string)((List<object>)st)[0])) o.Add(st);
            return o;
        }

        // the plain pull-out (no drip after it): the original's Pull
        static List<object> FlowFallback(string name)
        {
            return name == "pull" ? new List<object> { new List<object> { "Pull", "once" } } : new List<object>();
        }

        // a sequence of the play's flow that is no finish (pull_drip / pull / insert), on the same runner, without play_finish
        public bool RunFlow(string name)
        {
            var s = Steps(name);
            if (s.Count == 0) return false;
            seq = s;
            SeqName = name;
            step = -1;
            Next();
            return true;
        }

        void Run(string name, List<object> steps, string kind, string source, string who)
        {
            seq = steps;
            SeqName = name;
            step = -1;
            pc.OnSequence(name, kind, source, who, together);
            Next();
        }

        void Next()
        {
            step++;
            stepT = 0f;
            if (step >= seq.Count && silent)
            {
                silent = false;
                seq = new List<object>();
                SeqName = "";
                step = -1;
                pc.ResumeAfterShow();
                return;
            }
            if (step >= seq.Count)
            {
                string name = SeqName;
                seq = new List<object>();
                SeqName = "";
                step = -1;
                ClearFinish();
                pc.SequenceDone(name);
                if (queuedFinish.Length > 0 && pc.Active)
                {
                    string q = queuedFinish;
                    queuedFinish = "";
                    MuseFinish(q);
                }
                return;
            }
            string st = (string)((List<object>)seq[step])[0];
            pc.PlayState(st, st.Contains("Loop") ? pc.PaceSpeed(Mathf.Max(pc.Pace, 1)) : 1f);
        }

        public void Tick(float dt)
        {
            if (Running)
            {
                stepT += dt;
                object how = ((List<object>)seq[step])[1];
                bool done = false;
                if (how is string && (string)how == "hold")
                {
                    if (BlocksIdle == null || BlocksIdle()) holdWait += dt;
                    if (holdWait >= Json.Num(cfg, "hold_wait_seconds", 120.0))
                    {
                        string d = DefaultPlace();
                        if (d.Length == 0) done = true;
                        else Finish(d, "default");
                    }
                }
                else if (how is string && (string)how == "once") done = (stepT > 0.1f && pc.NormalizedTime() >= 1f) || stepT > 6f;
                else done = stepT >= Convert.ToSingle(how);
                if (done) Next();
            }
            Siru();
            // (the user's gauge: none - whatever the part, the user's climax is the user's answer to Muse's choices, declared by
            // Muse: play.climax "player" / the finish; the user's decision of 2026-11-04)
        }

        // the user's climax, declared (play.climax "player": the user answered Muse's choice): in the female part her climax
        // motion of the play for the user's body (the women's), in a mutual part the climax counted; the male part's is the
        // finish (play.finish) - refused here. "" or why not.
        public string DeclarePlayer()
        {
            if (!pc.Active) return "no_play";
            if (Running || busy) return "busy";
            if (MalePart() == "player") return "male_part_finishes";      // (the male part: play.finish, as ever)
            busy = true;
            PlayerClimax(new Dictionary<string, object> { { "source", "declared" }, { "play", Json.Str(pc.Current, "id") }, { "pace", pc.Pace } });
            if (FemalePart() == "player") FemaleClimax();
            busy = false;
            return "";
        }

        // SiruPasteCtrl.Proc on her state: each timing passed raises the parts' liquid by one level (at most 2)
        void Siru()
        {
            if (siruRows.Count == 0 || FemaleCha() == null) return;
            string name = pc.LastState;
            float nt = pc.NormalizedTime();
            if (name != siruState) { siruState = name; siruPrev = 0f; }
            foreach (var row in siruRows)
            {
                if (row.Key != name) continue;
                foreach (var t in row.Value)
                    if (siruPrev <= t.Key && t.Key < nt)
                        foreach (var part in t.Value)
                        {
                            if (part < 0 || part > 4) continue;
                            var p = (ChaFileDefine.SiruParts)part;
                            FemaleCha().SetSiruFlags(p, (byte)Mathf.Min(FemaleCha().GetSiruFlags(p) + 1, 2));
                        }
                break;
            }
            siruPrev = nt;
        }
    }
}
