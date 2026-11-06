using System;
using System.Collections;
using System.Collections.Generic;
using Studio;
using UnityEngine;

namespace MuseStudio
{
    // A play (MUSE_GUIDE.md 3.7) on Studio's H animations, as the Godot app's play_controller.gd: the character in the female
    // part, the partner (the user's body: Studio's default male or female body, bare) in the male part - reversed (攻守交代)
    // the other way round, a reversed aibu with the group's scene (the original has no male aibu motion); who may do what
    // is Resolved's - both on a spot of the location (an original H point, or "here"); the position's clothes and camera
    // from the original's H list. The anal insertion: Studio's "<name>アナル" categories (the A_ states) of the 8 positions
    // the original has it for (paramFemale.isAnal), chosen at the start (a male body receiving takes it by itself). The pace picks the state and its speed
    // (data/plays.json pace); the pace and the speed are Muse's. An insertion play starts out: play.insert / a pace >= 1 plays
    // the insertion, play.pull the pull-out. Her gauge rises with the pace; the climaxes and finishes are Climax's. Without a
    // press, a presentation or a request of the beat loop for idle_pause_seconds the play stops (pace 0) by itself. The pace is
    // Muse's (the user's decision 2026-11-02); out of an insertion play only Muse puts it in. The condom of an insertion play
    // (null undecided: Muse asks the user first; the user's setting may fix it on / off) must be decided before it goes in; on,
    // it shows on the penis (the original's visibleGomu), an inside finish does not drip, an outside finish takes it off.
    public class PlayCtl
    {
        readonly Stage stage;
        readonly Bridge bridge;
        readonly Gauge gauge;
        readonly Dictionary<string, object> data;
        public readonly Climax Climax;
        public Dictionary<string, object> Current;
        public Spot CurrentSpot;
        public HSceneProc.AnimationListInfo HInfo;   // the original's row of the position (null: not found)
        public int Pace;
        // one more at every start and end of a play, whoever made it (the state's play_serial; play_started / play_ended
        // carry it): Muse's start names the number it saw, so a start sent before the user changed or stopped the play is
        // refused (GPT's handoff of Alpha PR #1: no late start after a manual stop)
        public int Serial;
        public float SpeedFactor = 1f;
        public bool Inserted;
        public bool CameInside;
        public bool? Condom;           // an insertion play: on / off / undecided (null)
        public bool Reversed;          // the giver changed sides (the character in the original's male part)
        public bool Anal;              // the original's anal insertion (the A_ states)
        public Resolved Res;
        bool charSonBefore;
        HEyeNeck eyeF, eyeM;                  // where she / the partner looks (the original's eye / neck patterns)
        public readonly HLiquid Liquid = new HLiquid();
        public readonly HIK IK = new HIK();     // the original's motion IK of the position
        float maleRatio = 1f;                   // the male part's body: its scale at 0.6 tall / now (1: no shift)
        Transform maleBase;                     // its penis base (the hips' anchor)
        public string View = "position";      // the camera: the position's / pov (the partner's eyes)
        int eyesBefore = 1, neckBefore = -1;
        Transform povHead, povLook;
        public static readonly string[] ClothesKeys = { "dressed", "half", "nude" };
        public bool EyeNeckOn { get { return eyeF != null && eyeF.On; } }
        public bool HerGazeListed { get { var e = Reversed ? eyeM : eyeF; return e != null && e.On; } }       // (checks / probes)
        public string Started = "";
        public float StartedAt;
        public string LastState = "";
        public string StateKey { get { return string.IsNullOrEmpty(LastState) ? "" : Anal ? "A_" + LastState : LastState; } }      // (the original's lists' name of it)
        public float LastInput;
        public float LastMuse = -1e9f;   // the last play block from Muse (her own play follows her gauge unless Muse just spoke)
        int queuedPace = -1;
        int climaxesAtStart;
        readonly float idlePause;
        string touchPrefix = "M";     // aibu: the part last touched (its loop is the pace's state; HAibu)
        float lastTouched = -100f;     // aibu: when the user last touched her
        string reacted = "";          // aibu: the part whose Touch state played (a touch on it again keeps its loop)
        public bool Kissing;
        string kissBack = "M";
        static readonly Dictionary<string, string> AibuTouch = new Dictionary<string, string> { { "chest", "M" }, { "crotch", "A" }, { "hips", "S" }, { "anal", "S" } };
        static readonly Dictionary<string, string> AibuLoop = new Dictionary<string, string> { { "M", "M_Idle" }, { "A", "A_Idle" }, { "S", "S_Idle" }, { "K", "K_Loop" } };
        Vector3 basePos;
        Quaternion baseRot = Quaternion.identity;
        readonly Dictionary<int, byte> clothesBefore = new Dictionary<int, byte>();

        static readonly string[] PaceNames = { "止める", "ゆっくり", "ふつう", "はげしく" };

        public PlayCtl(Stage stage, Bridge bridge, Gauge gauge)
        {
            this.stage = stage;
            this.bridge = bridge;
            this.gauge = gauge;
            data = Data.Load("plays.json");
            idlePause = (float)Json.Num(data, "idle_pause_seconds", 45.0);
            Climax = new Climax(this, stage, bridge, gauge, Json.Obj(data, "climax"));
        }

        public bool Active { get { return Current != null; } }
        public string Group { get { return Json.Str(Current, "group", ""); } }
        public int Mode { get { return (int)Json.Num(Current, "mode", -1); } }
        public Dictionary<string, object> Doc { get { return data; } }

        public List<Dictionary<string, object>> List(string group)
        {
            var o = new List<Dictionary<string, object>>();
            var all = Json.Arr(data, "plays");
            if (all != null) foreach (var p in all) if (group == null || Json.Str(p, "group") == group) o.Add((Dictionary<string, object>)p);
            return o;
        }

        // the play's category (data/play_categories.json: the first rule of its group that fits - its giver, else a word of its
        // name; else その他) - the play menu's second row, the catalog's category
        readonly Dictionary<string, string> categories = new Dictionary<string, string>();

        public string Category(Dictionary<string, object> row)
        {
            string id = Json.Str(row, "id", ""), c;
            if (categories.TryGetValue(id, out c)) return c;
            c = "その他";
            string name = Json.Str(row, "name", ""), giver = Json.Str(row, "giver", "") + (Json.Has(row, "mutual") ? "mutual" : "");
            foreach (var rule in Json.Arr(Data.Load("play_categories.json"), Json.Str(row, "group", "")) ?? new List<object>())
            {
                bool hit = false;
                foreach (var g in Json.Arr(rule, "giver") ?? new List<object>()) if (giver.Contains((string)g)) hit = true;
                foreach (var w in Json.Arr(rule, "words") ?? new List<object>()) if (name.Contains((string)w)) hit = true;
                if (hit) { c = Json.Str(rule, "name", c); break; }
            }
            categories[id] = c;
            return c;
        }

        public List<string> Categories(string group)
        {
            var o = new List<string>();
            foreach (var rule in Json.Arr(Data.Load("play_categories.json"), group) ?? new List<object>()) o.Add(Json.Str(rule, "name", ""));
            if (o.Count > 0) o.Add("その他");
            return o;
        }

        public Dictionary<string, object> Row(string id)
        {
            foreach (var p in List(null)) if (Json.Str(p, "id") == id) return p;
            return null;
        }

        // ---- spots ----

        // The original's H point categories a play takes (its place kinds' toggles).
        public List<int> TogglesOf(Dictionary<string, object> row) { return Toggles(row); }

        List<int> Toggles(Dictionary<string, object> row)
        {
            var o = new List<int>();
            foreach (var k in Json.Arr(row, "places") ?? new List<object>())
                foreach (var t in Json.Arr(Json.Obj(Json.Obj(data, "places"), (string)k), "toggles") ?? new List<object>())
                    o.Add(Convert.ToInt32(t));
            return o;
        }

        // The location's spots the play can use, the nearest to her first (the Godot app's first choice).
        public List<Spot> Spots(Dictionary<string, object> row)
        {
            var t = Toggles(row);
            var o = new List<Spot>();
            foreach (var s in stage.Spots)
                foreach (var c in s.Categories)
                    if (t.Contains(c)) { o.Add(s); break; }
            Vector3 at = stage.Heroine != null ? stage.Heroine.oiCharInfo.changeAmount.pos : Vector3.zero;
            o.Sort((a, b) => Flat(a.Pos - at).CompareTo(Flat(b.Pos - at)));
            return o;
        }

        static float Flat(Vector3 v) { return new Vector2(v.x, v.z).magnitude; }

        // ---- roles and rates (the Godot app's play groups: who gives, the role's gauge target) ----

        public float RateOf(string who)
        {
            float rr;
            if (Res != null) return Res.Rates.TryGetValue(who, out rr) ? rr : 0f;
            var g = Json.Obj(Json.Obj(data, "groups"), Group);
            var t = Json.Obj(g, "targets");
            bool self = g != null && g.ContainsKey("self") && g["self"] is bool && (bool)g["self"];
            string giver = Json.Str(g, "giver", "player");
            if (who == "character") return (float)Json.Num(t, self || giver == "character" ? "giver" : "receiver", 0);
            if (self) return 0f;
            return (float)Json.Num(t, giver == "player" ? "giver" : "receiver", 0);
        }

        public float PaceFactor(int pace)
        {
            var f = Json.Arr(Json.Obj(data, "pace_info"), "factor");
            return f != null && pace >= 0 && pace < f.Count ? Convert.ToSingle(f[pace]) : 1f;
        }

        // ---- who does what ----

        // the bodies and the futanari (the character: a female body with her attribute; the user: the setting)
        public Dictionary<string, Dictionary<string, object>> WhoForCheck;   // (checks) the bodies without touching the settings

        public Dictionary<string, Dictionary<string, object>> Who()
        {
            if (WhoForCheck != null) return WhoForCheck;
            var host = MuseStudioPlugin.Instance;
            if (host.Match.Active)       // (the match: the opponent's body is the other one)
                return new Dictionary<string, Dictionary<string, object>> {
                    { "character", new Dictionary<string, object> { { "body", "female" }, { "futanari", stage.Futanari } } },
                    { "player", new Dictionary<string, object> { { "body", host.Match.OppMale ? "male" : "female" }, { "futanari", host.Match.OppFutanari } } } };
            return new Dictionary<string, Dictionary<string, object>> {
                { "character", new Dictionary<string, object> { { "body", "female" }, { "futanari", stage.Futanari } } },
                { "player", new Dictionary<string, object> { { "body", host.YourBody.Value == "female" ? "female" : "male" }, { "futanari", host.YourFutanari.Value || YourCardFutanari() } } } };
        }

        // the user's made body has the uncensor's penis shown (a female body)
        bool YourCardFutanari()
        {
            var host = MuseStudioPlugin.Instance;
            string card = host.YourBodyCard.Value;
            if (host.YourBody.Value != "female" || string.IsNullOrEmpty(card) || !System.IO.File.Exists(card)) return false;
            if (card != yourCard) { yourCard = card; yourFuta = Chars.CardFutanari(Chars.Load(card)); }
            return yourFuta;
        }

        string yourCard;
        bool yourFuta;

        public bool Reversible(Dictionary<string, object> row)
        {
            var g = Json.Obj(Json.Obj(data, "groups"), Json.Str(row, "group", ""));
            object v;
            bool rev = row.TryGetValue("reversible", out v) ? v is bool && (bool)v : g != null && g.TryGetValue("reversible", out v) && v is bool && (bool)v;
            bool self = g != null && g.TryGetValue("self", out v) && v is bool && (bool)v;
            return rev && !self;
        }

        // the position has the original's anal insertion (AnimationInfo isAnal: 8 of the sonyu positions)
        public bool AnalOk(Dictionary<string, object> row)
        {
            if (row == null || Json.Str(row, "group") != "sonyu" || !Json.Has(row, "anal_female")) return false;
            var hi = HData.Info((int)Json.Num(row, "mode"), (int)Json.Num(row, "motion_id"));
            return hi != null && hi.paramFemale.isAnal;
        }

        // can it start here: ok / reason, reversed_ok / reversed_reason, the spots that fit
        public Dictionary<string, object> Availability(Dictionary<string, object> row)
        {
            var fit = new List<object>();
            foreach (var sp in Spots(row)) fit.Add(sp.Id);
            var r = Resolved.Of(data, row, false, Who(), AnyRoles);
            var rr = Reversible(row) || AnyRoles ? Resolved.Of(data, row, true, Who(), AnyRoles) : new Resolved { Ok = false, Reason = "攻守交代できないプレイです" };
            string reason = r.Reason, rreason = rr.Reason;
            if (fit.Count == 0) reason = rreason = "この場所では選べません";
            if (!MuseStudioPlugin.Instance.Match.InFormat(row))
            {
                r.Ok = rr.Ok = false;
                reason = rreason = "レズプレイの形式では使えません";
            }
            return new Dictionary<string, object> { { "ok", r.Ok && fit.Count > 0 }, { "reversed_ok", rr.Ok && fit.Count > 0 }, { "reason", reason },
                { "reversed_reason", rreason }, { "spots", fit }, { "anal", AnalOk(row) } };
        }

        // the lesbian format of a match: either woman in either part (the original's 女性同士 has no reversal of its own)
        bool AnyRoles { get { return MuseStudioPlugin.Instance.Match.Lesbian; } }

        // the original's H motion plays (a reversed aibu has none: the group's scene)
        public bool Motion { get { return Active && !(Mode == 0 && Reversed); } }

        bool PartnerShown { get { return Motion && Json.Has(Current, "male"); } }

        // the bodies in the original's female / male part
        OCIChar FemaleBody { get { return Reversed ? stage.Partner : stage.Heroine; } }
        OCIChar MaleBody { get { return Reversed ? stage.Heroine : stage.Partner; } }

        public ChaControl FemaleCha { get { var b = FemaleBody; return b != null ? b.charInfo : null; } }

        // ---- start / end ----

        public bool NudeOnce;          // the next play strips both (shower together)

        public string Begin(string id, string source, string spotId = null, bool reverse = false, bool anal = false, string request = null)
        {
            var row = Row(id);
            if (row == null) return "unknown play " + id;
            if (MuseStudioPlugin.Instance.Outing.Out) return "she is out (" + MuseStudioPlugin.Instance.Outing.Kind + ")";
            if (MuseStudioPlugin.Instance.Presence.Apart) return "she is not here (in " + MuseStudioPlugin.Instance.Presence.HerRoom + ")";
            if (!MuseStudioPlugin.Instance.Match.InFormat(row)) return "not_in_format";
            var res = Resolved.Of(data, row, reverse, Who(), AnyRoles);
            if (!res.Ok) return res.Reason;
            if (anal && !AnalOk(row)) return Json.Str(row, "name") + "には原作のアナルの動きがありません";
            if (anal)
                foreach (var side in res.Parts.Values) { int at = side.IndexOf("vagina"); if (at >= 0) side[at] = "anus"; }
            var fits = Spots(row);
            Spot spot = null;
            foreach (var s in fits) if (spotId == null || s.Id == spotId || s.Aliases.Contains(spotId)) { spot = s; break; }
            if (spot == null) return spotId == null ? "no spot for " + id + " here" : "spot " + spotId + " does not fit " + id;
            var previous = Active ? Info() : null;
            if (Active) End("change", false, false);
            stage.StopWalk(false);            // a play stops the walk where she is
            Res = res;
            nudeNow = NudeOnce;
            NudeOnce = false;
            Reversed = reverse;
            Anal = AnalOk(row) && (res.Parts["character"].Contains("anus") || res.Parts["player"].Contains("anus"));
            Current = row;
            CurrentSpot = spot;
            HInfo = HData.Info(Mode, (int)Json.Num(row, "motion_id"));
            Pace = 0;
            SpeedFactor = 1f;
            Inserted = false;
            CameInside = false;
            queuedPace = -1;
            touchPrefix = "M";
            reacted = "";
            Kissing = false;
            Condom = Group == "sonyu" ? CondomDefault() : null;
            Started = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            StartedAt = LastInput = Time.realtimeSinceStartup;
            climaxesAtStart = gauge.Climaxes;
            var match = MuseStudioPlugin.Instance.Match;
            if (PartnerShown && match.Active)
            {
                // the match: the opponent's body is the partner (there already), dressed for the play
                stage.AddPartner(match.HasBody ? match.CardPath : "", match.OppMale);
                match.ForPlay(HInfo, reverse || match.Lesbian);          // (the lesbian format: a woman in either part)
            }
            else if (PartnerShown)
            {
                // the user's body (the setting: male / female, futanari): bare (the Godot app's default for "あなたの服"); a
                // male body or the futanari has its penis
                var who = Who()["player"];
                bool male = Json.Str(who, "body", "male") == "male";
                // the body the user made (the creation's "あなたの体") when it is of that sex
                string card = MuseStudioPlugin.Instance.YourBodyCard.Value;
                var cf = !string.IsNullOrEmpty(card) && System.IO.File.Exists(card) ? Chars.Load(card) : null;
                stage.AddPartner(cf != null && (cf.parameter.sex == 0) == male ? card : "", male);
                ApplyYourBody();
            }
            // the character's penis while a play has her futanari attribute (in the male part it is the one used)
            charSonBefore = stage.Cha.fileStatus.visibleSonAlways;
            stage.Heroine.SetVisibleSon(stage.Futanari);
            SizeSet = SizeFor(row);
            PlaceAtSpot();
            Undress();
            if (Group == "sonyu" && !nudeNow) ShiftShorts(ReceiverCha(), ShortsBefore(ReceiverCha()));
            AimPenis();
            if (match.Active && !match.StraponAgreed && res.Strapon.Length > 0)
            {
                // the match: the inserting side without a penis drawn futanari (a strapon only when the two agreed)
                if (res.Strapon == "character") match.DrawFuta(stage.Heroine);
                else if (stage.Partner != null) match.DrawFuta(stage.Partner);
            }
            else if (res.Strapon == "character") Strapon.Put(stage.Cha);
            else if (res.Strapon == "player" && stage.Partner != null) Strapon.Put(stage.Partner.charInfo);
            CondomLook();
            Climax.Start();
            PlayState(PaceStateName(0), 1f);
            MuseStudioPlugin.Instance.StartCoroutine(CameraNextFrame());
            if (Motion) MuseStudioPlugin.Instance.Sounds.PlayStarted(HInfo != null ? HInfo.paramFemale.fileSe : null, FemaleCha, MaleBody != null ? MaleBody.charInfo : null);
            View = "position";
            eyesBefore = stage.Cha.fileStatus.eyesLookPtn;
            neckBefore = stage.Cha.fileStatus.neckLookPtn;
            if (Motion && HInfo != null)
            {
                // the male part's body: your male body at the setting's height (HIK.SetMaleHeight: the original keeps males at
                // 0.6 and its male maker has no height), the anchor of its hips at a 0.6 tall body's (the original's motions are
                // made for its 0.6 tall males), the motion IK
                var mb = MaleBody != null ? MaleBody.charInfo : null;
                if (mb != null && mb.sex == 0 && stage.Partner != null && mb == stage.Partner.charInfo && !MuseStudioPlugin.Instance.Match.Active)
                    HIK.SetMaleHeight(mb, Mathf.Clamp(MuseStudioPlugin.Instance.YourHeight.Value, 0.35f, 0.85f));
                maleRatio = mb != null ? HIK.HeightRatio(mb) : 1f;
                maleBase = mb != null ? (HIK.Find(mb.objBodyBone.transform, "cm_J_dan101_00") ?? HIK.Find(mb.objBodyBone.transform, "cf_j_kokan")) : null;
                // (an AL play: no motion IK - the borrowed pose's IK data is for another motion. An aibu has it as the original does:
                // its data - kha_f_NN, binary, so its text reads empty - pins her hands, feet, elbows and knees to her own animated
                // pivots cf_pv_*, which keeps her feet on the floor while the body squirms; without it, "from the hips down she
                // hangs" - the trial runs 5 to 7. The run of 2026-11-04 took it away when the drag, then crossing its whole box,
                // moved those pivots; the drag now moves as the original's - AibuHands.Drag)
                if (!Current.ContainsKey("ik") || !(Current["ik"] is bool) || (bool)Current["ik"])
                    IK.Begin(FemaleCha, HInfo.paramFemale.path.file, mb, HInfo.paramMale.path.file);
                IK.Calc(Anal ? "A_" + LastState : LastState);
                var host = MuseStudioPlugin.Instance;
                if (eyeF == null) { eyeF = host.gameObject.AddComponent<HEyeNeck>(); eyeM = host.gameObject.AddComponent<HEyeNeck>(); }
                int exp = (int)Json.Num(Json.Obj(data, "eye_neck"), "experience", 2);
                eyeF.Begin(FemaleCha, MaleBody != null ? MaleBody.charInfo : null, false, HInfo.paramFemale.fileMotionNeck, exp, Mode);
                if (MaleBody != null) eyeM.Begin(MaleBody.charInfo, FemaleCha, true, HInfo.paramMale.fileMotionNeck, exp, Mode);
                if ((Mode == 1 || Mode == 2) && (Res == null || Res.Strapon != Climax.MalePart())) Liquid.Begin(HInfo.paramMale.fileMetaballCtrl, MaleBody != null ? MaleBody.charInfo : null, FemaleCha);
            }
            Serial++;
            var started = new Dictionary<string, object> { { "play", Info() }, { "previous", previous }, { "source", source }, { "serial", Serial } };
            if (request != null) started["request"] = request;       // (Muse's start: its request, echoed)
            bridge.Event("play_started", started);
            return "";
        }

        // (the spot moved by the user - SpotEditor: both bodies on it again)
        internal void PlaceAgain() { if (Active && CurrentSpot != null) PlaceAtSpot(); }

        // (the room's spots read again - SpotEditor.ResetAll: the play on the same spot's new object, both bodies on it)
        internal void Rebind(List<Spot> spots)
        {
            if (!Active || CurrentSpot == null) return;
            var id = CurrentSpot.Id;
            var again = spots.Find(o => o.Id == id || o.Aliases.Contains(id));
            if (again == null) return;
            CurrentSpot = again;
            PlaceAtSpot();
        }

        // Both bodies on the spot; a position with a local move for the spot's category sits off it (HSceneProc.SetLocalPosition).
        void PlaceAtSpot()
        {
            basePos = CurrentSpot.Pos;
            baseRot = CurrentSpot.Rot;
            if (HInfo != null)
                foreach (var c in HInfo.lstCategory)
                {
                    if (!CurrentSpot.Categories.Contains(c.category) || string.IsNullOrEmpty(c.fileMove)) continue;
                    Vector3 p, r;
                    if (HData.LocalMove(c.fileMove, out p, out r))
                    {
                        basePos = CurrentSpot.Pos + CurrentSpot.Rot * p;
                        baseRot = CurrentSpot.Rot * Quaternion.Euler(r);
                    }
                    break;
                }
            Stage.PlaceAt(stage.Heroine, basePos, baseRot);
            if (stage.Partner != null) Stage.PlaceAt(stage.Partner, basePos, baseRot);
        }

        IEnumerator CameraNextFrame()
        {
            yield return null;
            yield return null;
            Camera(false);
            // (framed again once the motion has settled into its loop: the bodies as they are)
            yield return new WaitForSeconds(0.4f);
            if (Active && View == "position" && stage.CameraKey == "play") Camera(false);
        }

        // The original's H camera of the position (h/list, paramFemale.nameCamera; houshi: nameCameraIdle), relative to where
        // the bodies stand as CameraControl_Ver2 places it; the side view when the position has none.
        public void Camera(bool instant)
        {
            string file = HInfo != null ? HInfo.paramFemale.nameCamera : null;
            if (HInfo != null && Mode == 1 && !string.IsNullOrEmpty(HInfo.nameCameraIdle)) file = HInfo.nameCameraIdle;
            var v = HData.Camera(file);
            if (v == null) { stage.PlayCamera(instant); return; }
            stage.CameraKey = "play";
            var cfg = Json.Obj(data, "camera_frame");
            if (cfg != null && cfg.ContainsKey("on") && cfg["on"] is bool && !(bool)cfg["on"]) { Original(v, instant); return; }
            MuseStudioPlugin.Instance.StartCoroutine(Framed(v, instant, ++frameAsk));
        }

        int frameAsk;

        // the original's H camera as it is (camera_frame.on false), in to 0.15 m before what the map has in between
        void Original(float[] v, bool instant)
        {
            Vector3 target = basePos + baseRot * new Vector3(v[0], v[1], v[2]);
            Quaternion rot = baseRot * Quaternion.Euler(v[6], v[7], v[8]);
            var dist = new Vector3(v[3], v[4], v[5]);
            float hit;
            if (Stage.MapRay(target, target + rot * dist, out hit) && hit > 0.3f) dist *= Mathf.Max(0.15f, hit - 0.15f) / dist.magnitude;
            stage.SetCameraData(target, rot, dist, v[9], instant);
        }

        // Framed on her (the user's request of 2026-11-04: she - and in a lesbian play both - seen well, whichever part she plays and
        // whatever her size). Read at the frame's end: earlier in a frame her bones stand in a rest pose, the motion's pose is put on
        // them later (they read 1 m too high in a doggy position). The look: the original's angle for the position, turned to the
        // side her face is on (within face_side degrees of where her face looks; two women: across the line between their heads),
        // looking down between pitch_min and pitch_max; a wider field (fov) so the room's walls leave room; the bodies (head, hands,
        // chest, hips, knees) fitted in it, the middle a little towards her face; a wall in the way - the turn tried either side
        // for a clear line before coming in.
        IEnumerator Framed(float[] v, bool instant, int ask)
        {
            yield return new WaitForEndOfFrame();
            if (ask != frameAsk || !Active || stage.CameraKey != "play" || View != "position") yield break;
            var cfg = Json.Obj(data, "camera_frame");
            Quaternion orig = baseRot * Quaternion.Euler(v[6], v[7], v[8]);
            Vector3 target; Quaternion rot; float dist, fov = (float)Json.Num(cfg, "fov", 36.0);
            if (!Frame(orig, ref fov, out target, out rot, out dist)) { Original(v, instant); yield break; }
            stage.SetCameraData(target, rot, new Vector3(0f, 0f, -dist), fov, instant);
        }

        // (a service play leaves her knees out: her upper body and where it touches fill the picture)
        static readonly string[] FrameBones = { "cf_j_head", "cf_j_hand_L", "cf_j_hand_R", "cf_j_hips", "cf_j_leg01_L", "cf_j_leg01_R", "cf_j_spine03" };
        public string FramedOn = "", FrameInfo = "";        // (checks / probes) whose bodies the camera framed, its numbers

        // (Unity 5.6 has no Vector3.SignedAngle) degrees from a to b about the up axis
        static float Signed(Vector3 a, Vector3 b) { return Vector3.Angle(a, b) * Mathf.Sign(Vector3.Dot(Vector3.up, Vector3.Cross(a, b))); }

        static Vector3 Level(Vector3 d) { d.y = 0f; return d.sqrMagnitude < 1e-6f ? Vector3.forward : d.normalized; }

        // How much of what matters is hidden from a camera there: her (and a second woman's) body taken out of the picture, the
        // rest - your body, the room - drawn small up to just before the nearest key point; a key point on something drawn is
        // hidden. Weighted: her face 0.5, her chest 0.3, where the play touches 0.2 (a second face like hers).
        // a body as capsules on its bones: [from, to, radius]
        static readonly object[][] Capsules = {
            new object[] { "cf_j_hips", "cf_j_spine02", 0.14f }, new object[] { "cf_j_spine02", "cf_j_neck", 0.13f }, new object[] { "cf_j_neck", "cf_j_head", 0.07f },
            new object[] { "cf_j_head", "cf_j_head", 0.12f },
            new object[] { "cf_j_thigh00_L", "cf_j_leg01_L", 0.08f }, new object[] { "cf_j_leg01_L", "cf_j_leg03_L", 0.06f },
            new object[] { "cf_j_thigh00_R", "cf_j_leg01_R", 0.08f }, new object[] { "cf_j_leg01_R", "cf_j_leg03_R", 0.06f },
            new object[] { "cf_j_arm00_L", "cf_j_forearm01_L", 0.05f }, new object[] { "cf_j_forearm01_L", "cf_j_hand_L", 0.045f },
            new object[] { "cf_j_arm00_R", "cf_j_forearm01_R", 0.05f }, new object[] { "cf_j_forearm01_R", "cf_j_hand_R", 0.045f } };

        static List<Vector3[]> BodyCapsules(ChaControl cha)
        {
            var at = new Dictionary<string, Vector3>();
            foreach (var t in cha.objBodyBone.GetComponentsInChildren<Transform>(true)) at[t.name] = t.position;
            var o = new List<Vector3[]>();
            foreach (var c in Capsules)
            {
                Vector3 a, b;
                if (!at.TryGetValue((string)c[0], out a) || !at.TryGetValue((string)c[1], out b)) continue;
                if ((string)c[0] == "cf_j_head") { a += Vector3.up * 0.08f; b = a; }
                o.Add(new[] { a, b, new Vector3((float)c[2], 0f, 0f) });
            }
            return o;
        }

        // the closest distance between two segments
        static float SegSeg(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r), s1, t1;
            if (a < 1e-8f && e < 1e-8f) return (p1 - p2).magnitude;
            if (a < 1e-8f) { s1 = 0f; t1 = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e < 1e-8f) { t1 = 0f; s1 = Mathf.Clamp01(-c / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2), den = a * e - b * b;
                    s1 = den > 1e-8f ? Mathf.Clamp01((b * f - c * e) / den) : 0f;
                    t1 = (b * s1 + f) / e;
                    if (t1 < 0f) { t1 = 0f; s1 = Mathf.Clamp01(-c / a); }
                    else if (t1 > 1f) { t1 = 1f; s1 = Mathf.Clamp01((b - c) / a); }
                }
            }
            return ((p1 + d1 * s1) - (p2 + d2 * t1)).magnitude;
        }

        // how much of the key points another body hides from the eye (the same weights as Seen); capsules right at a key point (the
        // penis's root in his own pelvis) do not count for it
        static float Blocked(Vector3 eye, List<Vector3> keys, List<List<Vector3[]>> bodies, List<int> owners)
        {
            float hidden = 0f;
            for (int i = 0; i < keys.Count; i++)
            {
                float w = i == 0 ? 0.5f : i == 1 ? 0.3f : i == 2 ? 0.2f : 0.5f;
                bool hit = false;
                for (int bi = 0; bi < bodies.Count && !hit; bi++)
                {
                    if (owners[i] == bi) continue;
                    foreach (var cap in bodies[bi])
                    {
                        float rad = cap[2].x;
                        if (SegSeg(cap[0], cap[1], keys[i], keys[i]) < rad + 0.06f) continue;      // (at the key point itself)
                        // (the ray up to just before the key point)
                        Vector3 end = keys[i] + (eye - keys[i]).normalized * 0.08f;
                        if (SegSeg(eye, end, cap[0], cap[1]) < rad) { hit = true; break; }
                    }
                }
                if (hit) hidden += w;
            }
            return hidden;
        }

        // how much a body that is not hers fills the near part of the picture: its capsules' points in view, the nearest of them
        // against the distance to the aim (a back or a shoulder close to the camera, her beyond it)
        static float Foreground(Vector3 eye, Quaternion rot, float fov, float aimDist, List<List<Vector3[]>> bodies, List<int> theirs)
        {
            var inv = Quaternion.Inverse(rot);
            float tv = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), th = tv * (UnityEngine.Camera.main != null ? UnityEngine.Camera.main.aspect : 16f / 9f);
            float nearest = float.MaxValue;
            int inView = 0;
            foreach (int bi in theirs)
                foreach (var cap in bodies[bi])
                    for (int k = 0; k <= 2; k++)
                    {
                        var p = Vector3.Lerp(cap[0], cap[1], k * 0.5f);
                        var q = inv * (p - eye);
                        if (q.z <= 0.05f || Mathf.Abs(q.x) > q.z * th || Mathf.Abs(q.y) > q.z * tv) continue;
                        inView++;
                        nearest = Mathf.Min(nearest, q.z);
                    }
            if (inView == 0) return 0f;
            return Mathf.Clamp01((0.75f * aimDist - nearest) / (0.75f * aimDist));
        }

        // How much of her part of the picture another body covers: drawn with it and without it (her own body out of both), the
        // pixels that differ inside her points' box over the box (a body drawn dark still differs from what is behind it)
        static Color32[] coverA, coverB;

        public static float RoomCover;      // (the last Coverage's) the share of her box the room's things cover in front of her
        public static float Clutter;        // (the last Coverage's) the share of the free picture the room's things fill close to the camera
        public static float Void;           // (the last Coverage's) the share of the free picture where nothing is drawn (outside the room)

        static float Coverage(Vector3 eye, Quaternion rot, float fov, List<Vector3> herPts, List<Renderer> hide, List<Renderer> others)
        {
            RoomCover = 0f;
            Clutter = 0f;
            Void = 0f;
            if (UnityEngine.Camera.main == null) return 0f;
            SeenSetup(eye, rot, fov);
            float far = 0.2f, nearest = float.MaxValue;
            float x0 = 1f, x1 = 0f, y0 = 1f, y1 = 0f;
            foreach (var p in herPts)
            {
                var vp = seenCam.WorldToViewportPoint(p);
                if (vp.z <= 0.05f) continue;
                far = Mathf.Max(far, vp.z + 0.15f);
                nearest = Mathf.Min(nearest, vp.z);
                x0 = Mathf.Min(x0, vp.x); x1 = Mathf.Max(x1, vp.x); y0 = Mathf.Min(y0, vp.y); y1 = Mathf.Max(y1, vp.y);
            }
            x0 = Mathf.Clamp01(x0); x1 = Mathf.Clamp01(x1); y0 = Mathf.Clamp01(y0); y1 = Mathf.Clamp01(y1);
            if (x1 - x0 < 0.02f || y1 - y0 < 0.02f) return 0f;
            Color32[] front = null;
            foreach (var r in hide) if (r != null) r.enabled = false;
            try
            {
                seenCam.farClipPlane = far;
                if (others.Count > 0) coverA = Draw();
                foreach (var r in others) if (r != null) r.enabled = false;
                if (others.Count > 0) coverB = Draw();
                seenCam.farClipPlane = Mathf.Max(0.1f, nearest - 0.15f);
                front = Draw();
                // (the near half of the way: a chair's back, a shelf's post right before the camera)
                seenCam.farClipPlane = Mathf.Max(0.1f, nearest * 0.55f);
                var close = Draw();
                var fv = MuseStudioPlugin.Instance.UiForCheck != null ? MuseStudioPlugin.Instance.UiForCheck.FreeView : new Rect(0f, 0f, 1f, 1f);
                int fx0 = Mathf.FloorToInt(fv.xMin * 127), fx1 = Mathf.CeilToInt(fv.xMax * 127), fy0 = Mathf.FloorToInt(fv.yMin * 71), fy1 = Mathf.CeilToInt(fv.yMax * 71), fa = 0, fc = 0;
                for (int y = fy0; y <= fy1; y++)
                    for (int x = fx0; x <= fx1; x++)
                    {
                        fa++;
                        var c = close[Mathf.Clamp(y, 0, 71) * 128 + Mathf.Clamp(x, 0, 127)];
                        if (c.r + c.g + c.b > 12) fc++;
                    }
                Clutter = fa > 0 ? (float)fc / fa : 0f;
                // (all of the room as far as it goes: black where the camera looks out of the room - behind a wall the map left open)
                seenCam.farClipPlane = 60f;
                var whole = Draw();
                int va = 0, vc = 0;
                for (int y = fy0; y <= fy1; y++)
                    for (int x = fx0; x <= fx1; x++)
                    {
                        va++;
                        var c = whole[Mathf.Clamp(y, 0, 71) * 128 + Mathf.Clamp(x, 0, 127)];
                        if (c.r + c.g + c.b < 4) vc++;
                    }
                Void = va > 0 ? (float)vc / va : 0f;
            }
            finally
            {
                foreach (var r in hide) if (r != null) r.enabled = true;
                foreach (var r in others) if (r != null) r.enabled = true;
            }
            int ix0 = Mathf.FloorToInt(x0 * 127), ix1 = Mathf.CeilToInt(x1 * 127), iy0 = Mathf.FloorToInt(y0 * 71), iy1 = Mathf.CeilToInt(y1 * 71), all = 0, cov = 0, room = 0;
            for (int y = iy0; y <= iy1; y++)
                for (int x = ix0; x <= ix1; x++)
                {
                    all++;
                    var f = front[y * 128 + x];
                    if (f.r + f.g + f.b > 12) room++;
                    if (others.Count == 0) continue;
                    var a = coverA[y * 128 + x];
                    var b = coverB[y * 128 + x];
                    if (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 12) cov++;
                }
            if (all == 0) return 0f;
            RoomCover = (float)room / all;
            return (float)cov / all;
        }

        static void SeenSetup(Vector3 eye, Quaternion rot, float fov)
        {
            var main = UnityEngine.Camera.main;
            if (seenCam == null)
            {
                var go = new GameObject("MuseStudio framing look");
                UnityEngine.Object.DontDestroyOnLoad(go);
                seenCam = go.AddComponent<Camera>();
                seenCam.enabled = false;
                seenRt = new RenderTexture(128, 72, 16);
                seenTex = new Texture2D(128, 72, TextureFormat.RGB24, false);
            }
            seenCam.CopyFrom(main);
            seenCam.targetTexture = seenRt;
            seenCam.clearFlags = CameraClearFlags.SolidColor;
            seenCam.backgroundColor = Color.black;
            seenCam.fieldOfView = fov;
            seenCam.aspect = main.aspect;
            seenCam.transform.position = eye;
            seenCam.transform.rotation = rot;
            seenCam.nearClipPlane = 0.05f;
        }

        static Color32[] Draw()
        {
            seenCam.Render();
            var was = RenderTexture.active;
            RenderTexture.active = seenRt;
            seenTex.ReadPixels(new Rect(0, 0, 128, 72), 0, 0, false);
            RenderTexture.active = was;
            return seenTex.GetPixels32();
        }

        static Camera seenCam;
        static RenderTexture seenRt;
        static Texture2D seenTex;

        static float Seen(Vector3 eye, Quaternion rot, float fov, List<Vector3> keys, List<Renderer> hide)
        {
            if (UnityEngine.Camera.main == null) return 0f;
            SeenSetup(eye, rot, fov);
            // (each key point on its own: drawn up to just before it - what lies between - and only its own few pixels looked at:
            // the floor or the bed beside a lying body is not in the way)
            seenCam.nearClipPlane = 0.05f;
            float hidden = 0f;
            foreach (var r in hide) if (r != null) r.enabled = false;
            try
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    float w = i == 0 ? 0.5f : i == 1 ? 0.3f : i == 2 ? 0.2f : 0.5f;
                    var vp = seenCam.WorldToViewportPoint(keys[i]);
                    if (vp.z <= 0.1f || vp.x < 0.02f || vp.x > 0.98f || vp.y < 0.02f || vp.y > 0.98f) { hidden += w; continue; }     // (out of the picture)
                    seenCam.farClipPlane = Mathf.Max(0.1f, vp.z - 0.1f);
                    seenCam.Render();
                    var was = RenderTexture.active;
                    RenderTexture.active = seenRt;
                    seenTex.ReadPixels(new Rect(0, 0, 128, 72), 0, 0, false);
                    RenderTexture.active = was;
                    int px = Mathf.RoundToInt(vp.x * 127), py = Mathf.RoundToInt(vp.y * 71), on = 0;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            var c = seenTex.GetPixel(Mathf.Clamp(px + dx, 0, 127), Mathf.Clamp(py + dy, 0, 71));
                            if (c.r + c.g + c.b > 0.04f) on++;
                        }
                    hidden += w * Mathf.Clamp01((on - 1) / 7f);
                }
            }
            finally { foreach (var r in hide) if (r != null) r.enabled = true; }
            return hidden;
        }

        // where the play touches: the penis (a man's, a futanari's or a strapon's) when the play's parts have one, else her crotch
        Vector3 ActionPoint(List<ChaControl> chas)
        {
            bool penis = false;
            var parts = Json.Obj(Current, "parts");
            foreach (var side in new[] { "giver", "receiver" })
            {
                var ps = Json.Arr(parts, side);
                if (ps != null && ps.Contains("penis")) penis = true;
            }
            var bodies = new List<ChaControl>();
            if (stage.Partner != null && stage.Partner.charInfo != null) bodies.Add(stage.Partner.charInfo);
            bodies.Add(stage.Cha);
            if (penis)
                foreach (var cha in bodies)
                {
                    if (cha == null || cha.objBodyBone == null) continue;
                    foreach (var t in cha.objBodyBone.GetComponentsInChildren<Transform>(true))
                        if (t.name == "cm_J_dan101_00" && t.gameObject.activeInHierarchy) return t.position;
                }
            foreach (var t in stage.Cha.objBodyBone.GetComponentsInChildren<Transform>(true)) if (t.name == "cf_j_kokan") return t.position;
            return chas[0].objBodyBone.transform.position;
        }

        // a body's size whatever its pose: its bones' lengths, hips to head and down a leg (+ the head)
        static readonly string[] Chain = { "cf_j_hips", "cf_j_spine01", "cf_j_spine02", "cf_j_spine03", "cf_j_neck", "cf_j_head" };
        static readonly string[] Leg = { "cf_j_thigh00_L", "cf_j_leg01_L", "cf_j_leg03_L" };

        static float Size(ChaControl cha)
        {
            var at = new Dictionary<string, Vector3>();
            foreach (var t in cha.objBodyBone.GetComponentsInChildren<Transform>(true)) at[t.name] = t.position;
            float n = 0f;
            foreach (var chain in new[] { Chain, Leg })
                for (int i = 1; i < chain.Length; i++)
                {
                    Vector3 a, b;
                    if (at.TryGetValue(chain[i - 1], out a) && at.TryGetValue(chain[i], out b)) n += Vector3.Distance(a, b);
                }
            return n > 0.3f ? n + 0.22f : 1.5f;
        }

        bool Frame(Quaternion orig, ref float fov, out Vector3 target, out Quaternion rot, out float distance)
        {
            target = Vector3.zero;
            rot = orig;
            distance = 0f;
            var cfg = Json.Obj(data, "camera_frame");
            var chas = new List<ChaControl> { stage.Cha };
            var pt = stage.Partner;
            if (pt != null && pt.charInfo != null && (MuseStudioPlugin.Instance.Match.Active || pt.charInfo.sex == 1)) chas.Add(pt.charInfo);
            var pts = new List<Vector3>();
            var heads = new List<Transform>();
            foreach (var cha in chas)
            {
                if (cha == null || cha.objBodyBone == null) continue;
                foreach (var t in cha.objBodyBone.GetComponentsInChildren<Transform>(true))
                    if (System.Array.IndexOf(FrameBones, t.name) >= 0 && !(Group == "houshi" && cha == stage.Cha && t.name.StartsWith("cf_j_leg01")))
                    {
                        pts.Add(t.name == "cf_j_head" ? t.position + Vector3.up * 0.12f : t.position);
                        if (t.name == "cf_j_head") heads.Add(t);
                    }
            }
            if (pts.Count < 4 || heads.Count == 0) return false;
            var b = new Bounds(pts[0], Vector3.zero);
            foreach (var p in pts) b.Encapsulate(p);
            // what the eye goes to (the user's criteria of 2026-11-04: she looks good, and it is lewd): her face and where the play
            // touches - the penis of whoever has it when the play has one (an insertion, a service), else her crotch - the aim
            // between the two (focus), a little up
            Vector3 face = heads[0].position;
            Vector3 act = ActionPoint(chas);
            pts.Add(act);
            Vector3 faceAt = heads.Count >= 2 ? (heads[0].position + heads[1].position) * 0.5f : face;
            Vector3 focusAt = Vector3.Lerp(faceAt, act, (float)Json.Num(cfg, "focus", 0.5));
            var bb = new Bounds(pts[0], Vector3.zero);
            foreach (var p in pts) bb.Encapsulate(p);
            target = Vector3.Lerp(bb.center, focusAt, (float)Json.Num(cfg, "focus_weight", 0.35)) + Vector3.up * (float)Json.Num(cfg, "lift", 0.03);
            // the side, from her body: upright (standing, sitting, kneeling) - a three-quarter view of her face (upright_turn off
            // her face's way); lying or on all fours - from her side, turned towards her head (lying_turn), so her face and her
            // hips are both seen along her body; two women - across the line between their heads, turned by pair_turn. Of the
            // two mirror sides, the original camera's.
            Vector3 camSide = Level(-(orig * Vector3.forward));
            Transform hips = null, shL = null, shR = null, thL = null, thR = null, kneeL = null, kneeR = null;
            foreach (var t in stage.Cha.objBodyBone.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "cf_j_hips") hips = t;
                else if (t.name == "cf_j_shoulder_L") shL = t;
                else if (t.name == "cf_j_shoulder_R") shR = t;
                else if (t.name == "cf_j_thigh00_L") thL = t;
                else if (t.name == "cf_j_thigh00_R") thR = t;
                else if (t.name == "cf_j_leg01_L") kneeL = t;
                else if (t.name == "cf_j_leg01_R") kneeR = t;
            }
            Vector3 spine = hips != null ? face - hips.position : Vector3.up;
            float upright = Mathf.Abs(spine.normalized.y);
            // her face's way: her neck's aim (the original's look), else her head's forward
            var aimT = stage.Cha.neckLookCtrl != null && stage.Cha.neckLookCtrl.neckLookScript != null ? stage.Cha.neckLookCtrl.neckLookScript.transformAim : null;
            Vector3 faceFwd = aimT != null ? aimT.forward : heads[0].forward;
            // her chest's front: across her shoulders, up her spine
            Vector3 chestFront = shL != null && shR != null ? Vector3.Cross(shR.position - shL.position, spine).normalized : heads[0].forward;
            // upright: the knees left out of the fit (her middle thighs instead) - from the thighs up fills the view
            if (heads.Count < 2 && upright >= (float)Json.Num(cfg, "upright_from", 0.6) && kneeL != null && kneeR != null && thL != null && thR != null)
            {
                pts.RemoveAll(p => (p - kneeL.position).sqrMagnitude < 1e-6f || (p - kneeR.position).sqrMagnitude < 1e-6f);
                if (Group != "houshi")         // (a service: her upper body and where it touches only)
                {
                    pts.Add(Vector3.Lerp(thL.position, kneeL.position, 0.55f));
                    pts.Add(Vector3.Lerp(thR.position, kneeR.position, 0.55f));
                }
            }
            float sideMax;
            Vector3 want;
            if (heads.Count >= 2)
            {
                Vector3 across = Level(Vector3.Cross(Vector3.up, heads[1].position - heads[0].position));
                if (Vector3.Dot(across, camSide) < 0f) across = -across;
                want = Quaternion.AngleAxis((float)Json.Num(cfg, "pair_turn", 20.0) * Mathf.Sign(Signed(across, camSide) + 0.001f), Vector3.up) * across;
                sideMax = (float)Json.Num(cfg, "pair_side", 40.0);
            }
            else if (upright >= (float)Json.Num(cfg, "upright_from", 0.6))
            {
                Vector3 faceWay = Level(chestFront);
                want = Quaternion.AngleAxis((float)Json.Num(cfg, "upright_turn", 40.0) * Mathf.Sign(Signed(faceWay, camSide) + 0.001f), Vector3.up) * faceWay;
                sideMax = (float)Json.Num(cfg, "face_side", 70.0);
            }
            else
            {
                Vector3 along = Level(spine);                               // (hips to head, level)
                Vector3 side = Level(Vector3.Cross(Vector3.up, along));
                if (Vector3.Dot(side, camSide) < 0f) side = -side;
                want = Quaternion.AngleAxis((float)Json.Num(cfg, "lying_turn", 35.0) * Mathf.Sign(Signed(side, along) + 0.001f), Vector3.up) * side;
                sideMax = (float)Json.Num(cfg, "face_side", 70.0);
            }
            float off = Signed(want, camSide);
            float yaw0 = 0f;
            // looking down: more on a lying body, less on an upright one
            float pitch = Mathf.Lerp((float)Json.Num(cfg, "pitch_lying", 32.0), (float)Json.Num(cfg, "pitch_upright", 12.0), Mathf.InverseLerp(0.2f, 0.9f, upright));
            if (Group == "houshi") pitch = Mathf.Min(pitch, (float)Json.Num(cfg, "pitch_service", 16.0));
            float margin = (float)Json.Num(cfg, "margin", 0.8);
            // (fitted in the part of the picture our column, Muse's panel and the dialogue box leave free)
            var view = MuseStudioPlugin.Instance.UiForCheck != null ? MuseStudioPlugin.Instance.UiForCheck.FreeView : new Rect(0f, 0f, 1f, 1f);
            float tanV = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), tanH = tanV * (UnityEngine.Camera.main != null ? UnityEngine.Camera.main.aspect : 16f / 9f);
            float tv = tanV * margin * view.height, th = tanH * margin * view.width;
            float dMin = (float)Json.Num(cfg, "min", 0.8), dMax = (float)Json.Num(cfg, "max", 4.0);
            // the turn tried either side for a clear line (a wall in the way: the clearest)
            // her size whatever her pose (bones' lengths): at least body_share of it fills the view's height - a curled pose's
            // bones lie close together, and a smaller or taller woman comes out alike
            float size = 0f;
            foreach (var cha in chas) if (cha != null && cha.objBodyBone != null) size = Mathf.Max(size, Size(cha));
            float atLeast = size * (float)Json.Num(cfg, "body_share", 0.36) / tv;
            string tries = string.Format(" size {0:F2} bounds {1} act {2} face {3} pts {4}", size, b.size, act.ToString("F2"), face.ToString("F2"), pts.Count);
            // the sides tried around the chosen one, at its height and higher; each looked through (Seen): what hides her face, her
            // chest or where the play touches - your body, furniture or a wall without a collider - counts against it; her back
            // (her chest turned away) too
            var keys = new List<Vector3> { face, act };
            foreach (var t in stage.Cha.objBodyBone.GetComponentsInChildren<Transform>(true)) if (t.name == "cf_j_spine03") { keys.Insert(1, t.position); break; }
            if (heads.Count >= 2) keys.Add(heads[1].position);
            // (the bodies that may be in the way: every body there; each key point's own body left out for it)
            var bodies = new List<List<Vector3[]>>();
            var allBodies = new List<ChaControl> { stage.Cha };
            if (stage.Partner != null && stage.Partner.charInfo != null) allBodies.Add(stage.Partner.charInfo);
            foreach (var cha in allBodies) bodies.Add(BodyCapsules(cha));
            var owners = new List<int>();
            for (int i = 0; i < keys.Count; i++) owners.Add(0);
            if (keys.Count >= 3)
            {
                // (where the play touches: the penis's body when it is the partner's)
                bool partnerPenis = false;
                if (stage.Partner != null && stage.Partner.charInfo != null)
                    foreach (var t in stage.Partner.charInfo.objBodyBone.GetComponentsInChildren<Transform>(true))
                        if (t.name == "cm_J_dan101_00" && (t.position - act).sqrMagnitude < 1e-6f) { partnerPenis = true; break; }
                owners[2] = partnerPenis ? 1 : 0;
            }
            if (heads.Count >= 2 && keys.Count >= 4) owners[3] = 1;
            // (the bodies that are not hers - your body; a second woman's is framed with her)
            var others = new List<int>();
            if (allBodies.Count > 1 && heads.Count < 2) others.Add(1);
            var offs = new List<float> { 0f, 12f, -12f, 25f, -25f, 50f, -50f, 75f, -75f, 105f, -105f };
            if (heads.Count >= 2) offs.AddRange(new[] { 180f, 155f, -155f });
            float bestScore = float.MinValue, bestNeed = 0f, bestFree = 0f;
            var hide = new List<Renderer>();
            foreach (var cha in chas) if (cha != null) foreach (var rr in cha.GetComponentsInChildren<Renderer>()) if (rr.enabled) hide.Add(rr);
            var otherR = new List<Renderer>();
            if (heads.Count < 2 && stage.Partner != null && stage.Partner.charInfo != null)
                foreach (var rr in stage.Partner.charInfo.GetComponentsInChildren<Renderer>()) if (rr.enabled) otherR.Add(rr);
            var herPts = new List<Vector3>(pts);
            foreach (float lift in new[] { 0f, (float)Json.Num(cfg, "pitch_up_try", 18.0), -(float)Json.Num(cfg, "pitch_down_try", 12.0) })
                foreach (float tryOff in offs)
                {
                    float yaw = yaw0 + tryOff;
                    Vector3 side = Quaternion.AngleAxis(yaw, Vector3.up) * want;
                    var r = Quaternion.LookRotation(-side, Vector3.up) * Quaternion.Euler(Mathf.Clamp(pitch + lift, 2f, 70f), 0f, 0f);
                    var inv = Quaternion.Inverse(r);
                    float d = atLeast;
                    string why = "least";
                    foreach (var p in pts)
                    {
                        var q = inv * (p - target);
                        float need = Mathf.Max(Mathf.Abs(q.y) / tv - q.z, Mathf.Abs(q.x) / th - q.z);
                        if (need > d) { d = need; why = q.ToString("F2"); }
                    }
                    if (tryOff == 0f && lift == 0f) tries += " fit " + why + " least " + atLeast.ToString("F2") + " tv " + tv.ToString("F3") + " th " + th.ToString("F3");
                    d = Mathf.Clamp(d, dMin, dMax);
                    // (the aim as it will be: moved so she sits in the middle of the free part)
                    Vector3 aimAt = target - r * new Vector3((view.center.x - 0.5f) * 2f * d * tanH, (view.center.y - 0.5f) * 2f * d * tanV, 0f);
                    float hit, free = d;
                    if (Stage.MapRay(aimAt, aimAt + r * new Vector3(0f, 0f, -d), out hit)) free = Mathf.Max(0.3f, hit - 0.15f);
                    float dist = Mathf.Min(d, free);
                    Vector3 eyeAt = aimAt + r * new Vector3(0f, 0f, -dist);
                    float hidden = Mathf.Min(1f, Seen(eyeAt, r, fov, keys, hide) + Blocked(eyeAt, keys, bodies, owners));
                    float near = Foreground(eyeAt, r, fov, dist, bodies, others);
                    float cover = Coverage(eyeAt, r, fov, herPts, hide, otherR), roomCover = RoomCover, clutter = Clutter, outside = Void;
                    // her chest's way against the camera's (seen from behind: her face and front lost)
                    float away = Vector3.Angle(Level(chestFront), Level(-(r * Vector3.forward)));
                    // her face's way against the camera (the back of her head: her face not seen) - past face_away degrees
                    float faceAway = Vector3.Angle(faceFwd, eyeAt - face);
                    float score = (free >= d - 0.01f ? 100f : free / d * 60f) - Mathf.Abs(tryOff) * 0.15f - Mathf.Abs(lift) * 0.5f - hidden * 90f - near * 45f - cover * 70f - roomCover * 80f - clutter * 70f - outside * 90f - Mathf.Clamp01((faceAway - (float)Json.Num(cfg, "face_away", 95.0)) / 45f) * 45f
                        - (heads.Count < 2 && upright >= 0.6f && away > 100f ? 40f : 0f);
                    tries += string.Format(" [{0:F0}/{1:F0}: d {2:F2} free {3:F2} hid {4:F2} near {5:F2} cov {6:F2} room {7:F2} clut {8:F2} void {9:F2}]", yaw, lift, d, free, hidden, near, cover, roomCover, clutter, outside);
                    if (score > bestScore) { bestScore = score; rot = r; distance = dist; bestNeed = d; bestFree = free; }
                }
            // walled in on every side: a wider field so the bodies still fit from where there is room
            if (bestFree < bestNeed - 0.01f)
            {
                float t2 = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * bestNeed / Mathf.Max(0.3f, bestFree);
                fov = Mathf.Min((float)Json.Num(cfg, "fov_max", 62.0), 2f * Mathf.Atan(t2) * Mathf.Rad2Deg);
            }
            // (the aim moved so she sits in the middle of the free part, not of the whole picture)
            float dx = (view.center.x - 0.5f) * 2f * distance * tanH, dy = (view.center.y - 0.5f) * 2f * distance * tanV;
            target -= rot * new Vector3(dx, dy, 0f);
            FramedOn = chas.Count > 1 ? "both" : "her";
            FrameInfo = string.Format("target {0} dist {1:F2} yaw off {2:F0} (orig {3:F0}) pitch {4:F0} fov {5:F0}", target, distance, Signed(want, Level(-(rot * Vector3.forward))), off, pitch, fov) + tries;
            return true;
        }

        public void End(string reason, bool backToIdle = true, bool tell = true)
        {
            if (!Active) return;
            var info = Info();
            Serial++;
            Climax.Stop();
            MuseStudioPlugin.Instance.Sounds.PlayEnded();
            if (eyeF != null) { eyeF.End(); eyeM.End(); }
            Liquid.Stop();
            IK.End();
            maleRatio = 1f;
            maleBase = null;
            View = "position";
            povHead = povLook = null;
            // her own look again: its pattern, at the camera (the patterns pointed it at bones)
            if (UnityEngine.Camera.main != null)
            {
                if (stage.Cha.eyeLookCtrl != null) stage.Cha.eyeLookCtrl.target = UnityEngine.Camera.main.transform;
                if (stage.Cha.neckLookCtrl != null) stage.Cha.neckLookCtrl.target = UnityEngine.Camera.main.transform;
            }
            stage.Heroine.ChangeLookEyesPtn(eyesBefore, true);
            if (neckBefore >= 0) stage.Heroine.ChangeLookNeckPtn(neckBefore);
            MuseStudioPlugin.Instance.Hands.Clear();
            Kissing = false;
            // the partner's strapon: forgotten with the body it goes with; the match's opponent stays - taken off
            if (stage.Partner != null) { if (MuseStudioPlugin.Instance.Match.Active) Strapon.Off(stage.Partner.charInfo); else Strapon.Forget(stage.Partner.charInfo); }
            if (PenisAim != 0) { Penetration.Target(MaleBody != null ? MaleBody.charInfo : null, Penetration.Off); PenisAim = 0; }
            Dan.Stop();
            bool matchOn = MuseStudioPlugin.Instance.Match.Active;
            if (matchOn) MuseStudioPlugin.Instance.Match.UndoFuta();
            if (!matchOn) stage.RemovePartner();
            var cha = stage.Cha;
            Strapon.Off(cha);
            stage.Heroine.SetVisibleSon(charSonBefore);
            foreach (var kv in clothesBefore) cha.SetClothesState(kv.Key, kv.Value, false);
            BathAccessories.Back(cha, accBefore);
            accBefore = null;
            bool together = nudeNow;
            nudeNow = false;
            clothesBefore.Clear();
            Current = null;
            CurrentSpot = null;
            HInfo = null;
            Res = null;
            Reversed = Anal = false;
            Inserted = false;
            queuedPace = -1;
            stage.Place(stage.Heroine);
            if (matchOn) MuseStudioPlugin.Instance.Match.AfterPlay();       // (the opponent stands again, facing her)
            // (a match: she stands as her personality does - the original's talk - else the idle scene)
            if (backToIdle && !(matchOn && MuseStudioPlugin.Instance.Talk.Stand(stage.Cha))) stage.ApplyScene(Json.Str(stage.Cfg, "idle_scene", "idle"), 1, 1f);
            if (backToIdle && matchOn) MuseStudioPlugin.Instance.Match.Camera();
            else if (backToIdle) MuseStudioPlugin.Instance.StartCoroutine(IdleCamera());
            if (tell) bridge.Event("play_ended", new Dictionary<string, object> { { "play", info }, { "reason", reason },
                { "seconds", Math.Round(Time.realtimeSinceStartup - StartedAt, 1) }, { "climaxes", gauge.Climaxes - climaxesAtStart }, { "gauge", gauge.Info() },
                { "serial", Serial } });
            MuseStudioPlugin.Instance.Mood.PlayEnded(together);
        }

        IEnumerator IdleCamera()
        {
            yield return null;
            yield return null;
            stage.ApplyCamera(Json.Str(Data.Load("cameras.json"), "default", "medium"), false);
        }

        // the penis of the male part along her (Penetration): the insertion's way in, a mouth service's mouth
        public int PenisAim = 0;
        public readonly DanLook Dan = new DanLook();      // (the original's aim where BetterPenetration has none: DanLook)
        void AimPenis()
        {
            var mb = MaleBody != null ? MaleBody.charInfo : null;
            int aim = Penetration.Off;
            var mine = Res != null && Res.Parts.ContainsKey(Reversed ? "character" : "player") ? Res.Parts[Reversed ? "character" : "player"] : null;
            var hers = Res != null && Res.Parts.ContainsKey(Reversed ? "player" : "character") ? Res.Parts[Reversed ? "player" : "character"] : null;
            if (Group == "sonyu") aim = Anal ? Penetration.Anal : Penetration.Vaginal;
            else if (Group == "houshi" && hers != null && hers.Contains("mouth")) aim = Penetration.Oral;
            aimWant = aim;
            aimUntil = Time.realtimeSinceStartup + 5f;          // (its controller gets ready a few frames after the body comes)
            PenisAim = Penetration.Target(mb, aim) ? aim : 0;
            Dan.Start(mb, FemaleCha, HInfo);
        }

        // HSceneProc.SetClothStateStartMotion: the position's numFemaleUpperCloth / LowerCloth through the original's
        // GlobalMethod.SetAllClothState (only towards bare). Put back when the play ends, as the original after H.
        // an insertion: the receiver's shorts shifted aside (the trial run of 2026-11-04) where the garment has a half state (the
        // original's 半脱 of its state kinds); else as the position has them
        ChaControl ReceiverCha()
        {
            if (Res == null) return FemaleCha;
            string side = "";
            foreach (var kv in Res.Roles) if (kv.Value == "receiver") side = kv.Key;
            if (side == "player") return stage.Partner != null ? stage.Partner.charInfo : null;
            if (side == "character") return stage.Cha;
            return FemaleCha;
        }

        // the shorts as they were before the play's undressing (hers: kept by Undress; your body: bare by your setting = off)
        byte ShortsBefore(ChaControl cha)
        {
            byte b;
            if (cha == stage.Cha) return clothesBefore.TryGetValue(3, out b) ? b : cha.fileStatus.clothesState[3];
            return MuseStudioPlugin.Instance.YourClothes.Value >= 2 ? (byte)3 : (byte)0;
        }

        // (shorts that were on before the play - whatever the position's undressing made of them - shifted aside; shorts already off
        // - the bath's nude, taken off before - stay off: the trial run's notes of 2026-11-04)
        public static bool ShiftShorts(ChaControl cha, byte before)
        {
            if (cha == null || cha.fileStatus.clothesState.Length <= 3 || !Clothes.Worn(cha).Contains(3) || before >= 3) return false;
            Dictionary<byte, string> kinds;
            if (cha.dictStateType == null || !cha.dictStateType.TryGetValue(3, out kinds) || !kinds.ContainsKey(1)) return false;
            cha.SetClothesState(3, 1, false);
            return true;
        }

        void Undress()
        {
            var cha = stage.Cha;
            clothesBefore.Clear();
            for (int k = 0; k < cha.fileStatus.clothesState.Length; k++) clothesBefore[k] = cha.fileStatus.clothesState[k];
            if (nudeNow)
            {
                for (int k = 0; k < cha.fileStatus.clothesState.Length; k++) cha.SetClothesState(k, 3, false);
                accBefore = BathAccessories.Off(cha);        // (the bath: her accessories too, but the hair's)
                return;
            }
            if (HInfo == null) return;
            GlobalMethod.SetAllClothState(cha, true, HInfo.numFemaleUpperCloth, false);
            GlobalMethod.SetAllClothState(cha, false, HInfo.numFemaleLowerCloth, false);
        }

        // ---- the pace ----

        List<object> PaceEntry(int pace)
        {
            var table = Json.Arr(Json.Obj(data, "pace"), Mode.ToString());
            if (table == null || pace < 0 || pace >= table.Count) return new List<object> { "Idle", 1.0 };
            return (List<object>)table[pace];
        }

        string PaceStateName(int pace)
        {
            if (Group == "sonyu" && pace == 0) return Inserted ? "InsertIdle" : "Idle";
            string name = (string)PaceEntry(pace)[0];
            if (Mode == 0 && Kissing) return "K_Loop";
            if (Mode == 0 && name == "M_Idle") return AibuLoop.ContainsKey(touchPrefix) ? AibuLoop[touchPrefix] : name;
            return name;
        }

        // ---- aibu: her answer to a touch, the kiss (HAibu) ----

        // a touch during an aibu play: the part's Touch state, then its loop while the play runs (Idle when stopped)
        public void Touched(string area)
        {
            if (!Active || Mode != 0 || Climax.Running || !AibuTouch.ContainsKey(area)) return;
            LastInput = Time.realtimeSinceStartup;
            string p = AibuTouch[area];
            lastTouched = Time.realtimeSinceStartup;
            if (Kissing) return;
            if (p == reacted && (LastState == p + "_Touch" || LastState == AibuLoop[p])) return;     // already on it (the hand stays)
            touchPrefix = p;
            reacted = p;
            PlayState(p + "_Touch", 1f);
        }

        // HAibu's kiss: K_Touch, then K_Loop while it lasts; at its end back to the pace's state
        public bool Kiss(bool on)
        {
            if (!Active || Mode != 0) return false;
            if (on)
            {
                if (Climax.Running || !HasState("K_Touch")) return false;
                if (!Kissing) kissBack = touchPrefix;
                Kissing = true;
                touchPrefix = reacted = "K";
                LastInput = Time.realtimeSinceStartup;
                PlayState("K_Touch", 1f);
                return true;
            }
            if (!Kissing) return false;
            Kissing = false;
            touchPrefix = kissBack;
            reacted = "";
            PlayState(PaceStateName(Pace), Pace == 0 ? 1f : PaceSpeed(Pace));
            return true;
        }

        // HandCtrl.KissCameraDataLoad: h/list <nameCameraKiss>_00 / _01 / _02 lerped by her height (0..0.5 the first two,
        // 0.5..1 the last two), placed as the H camera
        public void KissCamera()
        {
            if (HInfo == null || string.IsNullOrEmpty(HInfo.nameCameraKiss)) return;
            var vs = new float[3][];
            for (int k = 0; k < 3; k++) if ((vs[k] = HData.Camera(HInfo.nameCameraKiss + "_" + k.ToString("00"))) == null) return;
            float ht = Mathf.Clamp01(stage.Cha.GetShapeBodyValue(0));
            float[] a = ht < 0.5f ? vs[0] : vs[1], b = ht < 0.5f ? vs[1] : vs[2];
            float u = ht < 0.5f ? Mathf.InverseLerp(0f, 0.5f, ht) : Mathf.InverseLerp(0.5f, 1f, ht);
            var v = new float[10];
            for (int i = 0; i < 10; i++) v[i] = Mathf.Lerp(a[i], b[i], u);
            Vector3 target = basePos + baseRot * new Vector3(v[0], v[1], v[2]);
            stage.SetCameraData(target, baseRot * Quaternion.Euler(v[6], v[7], v[8]), new Vector3(v[3], v[4], v[5]), v[9], false);
            stage.CameraKey = "play_kiss";
        }

        // the pace's speed x Muse's speed, at most the original's own top for the mode (plays.json speed_max: insertion 3.0,
        // service 2.5, caress 1.5 - the original H scene's curves)
        public float PaceSpeed(int pace)
        {
            float sp = Convert.ToSingle(PaceEntry(pace)[1]) * SpeedFactor;
            double top = Json.Num(Json.Obj(data, "speed_max"), Mode.ToString(), 0.0);
            return top > 0.0 ? Mathf.Min(sp, (float)top) : sp;
        }

        public float SpeedMax { get { return (float)Json.Num(Json.Obj(data, "speed_max"), Mode.ToString(), 0.0); } }

        // the loop playing (WLoop / SLoop / MLoop), else the pace's state
        public string BaseState()
        {
            if (LastState == "WLoop" || LastState == "SLoop" || LastState == "MLoop") return LastState;
            return PaceStateName(Pace);
        }

        public void SetPace(int pace, string source)
        {
            if (!Active) return;
            if (source == "user") LastInput = Time.realtimeSinceStartup;
            if (source == "user" && pace > 0 && Group == "sonyu" && !Inserted) return;      // out: only Muse puts it in
            if (Climax.Running) { queuedPace = pace; return; }     // after the finish / the insertion
            PaceTo(pace, source);
        }

        // after a shown sequence (Climax.PresentDeclared): the pace Muse set meanwhile, else the pace's loop again - nothing told
        public void ResumeAfterShow()
        {
            if (!Active) return;
            int qp = queuedPace;
            queuedPace = -1;
            if (qp >= 0 && qp != Pace) PaceTo(qp, "muse");
            else PlayState(PaceStateName(Pace), Pace == 0 ? 1f : PaceSpeed(Pace));
        }

        void PaceTo(int pace, string source)
        {
            int prev = Pace;
            Pace = pace;
            PlayState(PaceStateName(pace), pace == 0 ? 1f : PaceSpeed(pace));
            if (prev != pace)
                bridge.Event("play_pace_changed", new Dictionary<string, object> { { "play", Info() }, { "pace", pace }, { "pace_name", PaceNames[pace] },
                    { "previous_pace", prev }, { "source", source } });
        }

        // A block's play (MUSE_GUIDE.md 5.2): pace / speed / insert / pull / finish. Returns the refused fields with why.
        public List<KeyValuePair<string, string>> Apply(Dictionary<string, object> play)
        {
            var refused = new List<KeyValuePair<string, string>>();
            if (!Active || play == null) return refused;
            LastInput = LastMuse = Time.realtimeSinceStartup;
            if (Json.Has(play, "speed")) SpeedFactor = Mathf.Clamp((float)Json.Num(play, "speed", 1), 0.5f, 2f);
            int pace = Json.Has(play, "pace") ? Mathf.Clamp((int)Json.Num(play, "pace"), 0, 3) : -1;
            object v;
            bool pull = play.TryGetValue("pull", out v) && v is bool && (bool)v;
            bool insert = play.TryGetValue("insert", out v) && v is bool && (bool)v;
            string fin = Json.Str(play, "finish", null);
            // the user's climax, declared (the user answered Muse's choice; the user's decision of 2026-11-04: no gauge decides it)
            string pcl = Json.Str(play, "climax", null);
            if (pcl != null)
            {
                string whyp = pcl != "player" ? "unknown" : MuseStudioPlugin.Instance.Match.Active ? "in_match" : Climax.DeclarePlayer();
                if (whyp.Length > 0) refused.Add(new KeyValuePair<string, string>("play.climax", whyp));
            }
            if (fin != null && MuseStudioPlugin.Instance.Match.Active)
            {
                refused.Add(new KeyValuePair<string, string>("play.finish", "in_match"));      // (a match: declare the climax instead)
                fin = null;
            }
            if (play.TryGetValue("condom", out v) && v is bool)
            {
                string whyc = SetCondom((bool)v, "muse");
                if (whyc.Length > 0) refused.Add(new KeyValuePair<string, string>("play.condom", whyc));
            }
            if (pull)
            {
                if (Group != "sonyu") refused.Add(new KeyValuePair<string, string>("play.pull", "not_insertion"));
                else if (!Inserted) refused.Add(new KeyValuePair<string, string>("play.pull", "not_inserted"));
                else if (Climax.Running) refused.Add(new KeyValuePair<string, string>("play.pull", "busy"));
                else { Pull(); return refused; }
            }
            if (insert && Group != "sonyu") refused.Add(new KeyValuePair<string, string>("play.insert", "not_insertion"));
            if (fin != null)
            {
                // (a finish while out goes in: the condom must be decided first)
                string why = Group == "sonyu" && !Inserted && Condom == null ? "condom_undecided" : Climax.MuseFinish(fin);
                if (why.Length > 0) refused.Add(new KeyValuePair<string, string>("play.finish", why));
                if (pace >= 0) queuedPace = pace;
                return refused;
            }
            if (Group == "sonyu" && !Inserted && (insert || pace >= 1))
            {
                string whyi = InsertRefusal();
                if (whyi.Length == 0) Insert(pace >= 0 ? pace : 0);
                else refused.Add(new KeyValuePair<string, string>(insert ? "play.insert" : "play.pace", whyi));
            }
            else if (pace >= 0) SetPace(pace, "muse");
            else if (Json.Has(play, "speed") && Pace > 0) PlayState(PaceStateName(Pace), PaceSpeed(Pace));
            return refused;
        }

        public void Insert(int pace)
        {
            queuedPace = pace;
            if (!Climax.RunFlow("insert")) { SetInserted(true, "muse"); SequenceDone("insert"); }
        }

        public void Pull()
        {
            // with a condom nothing drips (HSonyu: Pull -> OUT_A)
            if (!Climax.RunFlow(CameInside && Condom != true ? "pull_drip" : "pull")) SequenceDone("pull");
        }

        // ---- the condom ----

        string CondomModeNow { get { return MuseStudioPlugin.Instance.CondomMode.Value; } }

        bool? CondomDefault() { return CondomModeNow == "on" ? true : CondomModeNow == "off" ? (bool?)false : null; }

        // why the penis cannot go in now ("" when it can)
        public string InsertRefusal()
        {
            if (!Active) return "no_play";
            if (Group != "sonyu") return "not_insertion";
            if (Inserted) return "inserted";
            if (Condom == null) return "condom_undecided";
            return "";
        }

        // Muse's play.condom after asking the user (the original's insert menu: with or without), only while out
        public string SetCondom(bool? on, string source)
        {
            if (!Active) return "no_play";
            if (Group != "sonyu") return "not_insertion";
            if (Inserted && source == "muse") return "inserted";
            if (on == Condom) return "";
            var before = Condom;
            Condom = on;
            CondomLook();
            bridge.Event("play_condom_changed", new Dictionary<string, object> { { "condom", on.HasValue ? (object)on.Value : null },
                { "previous", before.HasValue ? (object)before.Value : null }, { "source", source }, { "play", Info() } });
            return "";
        }

        // the condom on the male part's penis while it is on (ChaFileStatus.visibleGomu: ChaControl shows S_GOMU)
        void CondomLook()
        {
            if (stage.Partner != null) stage.Partner.charInfo.fileStatus.visibleGomu = Group == "sonyu" && Condom == true;
        }

        void SetInserted(bool on, string source)
        {
            if (Inserted == on) return;
            Inserted = on;
            bridge.Event("play_insertion_changed", new Dictionary<string, object> { { "play", Info() }, { "inserted", on },
                { "drip", !on && CameInside }, { "source", source } });
        }

        // ---- sequences (Climax's runner) ----

        // a finish / climax sequence begins: Muse hears which (kind: soon / a place / climax; who: the one who comes)
        public void OnSequence(string name, string kind, string source, string who, bool together)
        {
            if (Mode == 2)
            {
                if (!Inserted && kind != "climax") SetInserted(true, source);
                if (name == "inside" || name == "same_w" || name == "same_s") CameInside = true;
            }
            bridge.Event("play_finish", new Dictionary<string, object> { { "finish", kind }, { "sequence", name }, { "who", who }, { "together", together },
                { "source", source }, { "play", Info() }, { "gauge", gauge.Info() } });
        }

        // after a sequence: the insertion goes on at the pace it was for; the pull-out rests out; a finish rests at pace 0 (its
        // after state) until Muse sets another (or the one it asked for meanwhile)
        public void SequenceDone(string name)
        {
            int qp = queuedPace;
            queuedPace = -1;
            if (name == "insert")
            {
                SetInserted(true, "muse");
                PaceTo(qp >= 0 ? qp : 0, "muse");
                return;
            }
            if (name == "pull" || name == "pull_drip")
            {
                SetInserted(false, "muse");
                CameInside = false;
                PaceTo(0, "muse");
                return;
            }
            if (Mode == 2 && name == "outside")
            {
                SetInserted(false, "finish");
                if (Condom == true) SetCondom(CondomDefault(), "finish");    // an outside finish takes it off (HSonyu M_OUT_Start)
            }
            string after = Json.Str(Json.Obj(Json.Obj(Json.Obj(data, "climax"), "after"), Mode.ToString()), name, "");
            PaceTo(0, "finish");
            if (after.Length > 0) PlayState(after, 1f);
            if (qp > 0) PaceTo(qp, "muse");
            MuseStudioPlugin.Instance.Mood.SequenceDone(name);      // her own play ends with her climax
        }

        public bool HasState(string state)
        {
            var st = Json.Obj(Current, Anal ? "anal_female_states" : "female_states");
            return st != null && st.ContainsKey(Anal ? "A_" + state : state);
        }

        // A wait the play's motion does not have under that name: the original's service plays wait in Stop_Idle (hands,
        // breasts) or Oral_Idle (mouth) - no Idle (the trial run of 2026-11-03: a service play at pace 0 loaded nothing, the
        // male body stood in a T pose). null: none.
        static readonly string[] Waits = { "Idle", "Stop_Idle", "Oral_Idle", "InsertIdle" };

        string IdleOf(string state)
        {
            if (Array.IndexOf(Waits, state) < 0) return null;
            foreach (var w in Waits) if (HasState(w)) return w;
            return null;
        }

        public void PlayState(string state, float speed)
        {
            if (!Motion)
            {
                // a reversed aibu: the group's scene, its intensity by the pace
                var g = Json.Obj(Json.Obj(data, "groups"), Group);
                stage.ApplyScene(Json.Str(g, "scene", "tease"), Mathf.Clamp(Pace, 1, 3), 1f);
                LastState = "";
                return;
            }
            if (!HasState(state)) state = IdleOf(state);
            if (state == null) { MuseStudioPlugin.Log.LogWarning("MuseStudio: no state in " + Json.Str(Current, "id")); return; }
            LastState = state;
            string fk = Anal ? "anal_female" : "female", mk = Anal ? "anal_male" : "male", st = Anal ? "A_" + state : state;
            if (FemaleBody != null) Load(FemaleBody, Json.Arr(Current, fk), Json.Obj(Current, fk + "_states"), st, speed, SizeSet);
            if (MaleBody != null && Json.Has(Current, mk)) Load(MaleBody, Json.Arr(Current, mk), Json.Obj(Current, mk + "_states"), st, speed, SizeSet);
            if (IK.On) IK.Calc(st);                 // HActionBase.SetPlay: the motion IK of the state
        }

        // an AL play's size set (its clips S_ / M_ / L_): by the female part's height (provisional marks: what the sets fit
        // is not known yet - the KPlug motions' S / M / L)
        public string SizeSet = "M_";

        string SizeFor(Dictionary<string, object> row)
        {
            var sizes = Json.Arr(row, "sizes");
            if (sizes == null || sizes.Count == 0) return "";
            float ht = FemaleCha != null ? FemaleCha.fileBody.shapeValueBody[0] : 0.5f;
            string want = ht < 0.4f ? "S_" : ht < 0.7f ? "M_" : "L_";
            return sizes.Contains(want) ? want : (string)sizes[0];
        }

        static void Load(OCIChar oci, List<object> gc, Dictionary<string, object> states, string state, float speed, string size)
        {
            object no;
            if (gc == null || states == null || !states.TryGetValue(state, out no)) return;
            if (no is string) no = ((string)no).Replace("{s}", size);
            // (a mod's motion: its group by name, its state by clip - their numbers are the sideloader's of this run)
            int g = StudioAnim.Group(Convert.ToInt32(gc[0]), gc.Count > 2 ? gc[2] as string : null), c = Convert.ToInt32(gc[1]);
            int n = StudioAnim.No(g, c, no);
            if (n < 0) { MuseStudioPlugin.Log.LogWarning("MuseStudio: no motion " + no + " in " + g + "/" + c); return; }
            Blend.Next(oci.charInfo, 0.25f);         // a play's state fades in 0.25 s (the Godot app's blend_seconds)
            oci.LoadAnime(g, c, n, 0f);
            // the speed as the original gives it: its controller's "speed" parameter (HSonyu / HHoushi SetAnimatorFloat), which
            // only the loops follow - a state with its own speed (the OLoop: 2.0) keeps it. (Studio's animeSpeed is the whole
            // animator's: it made the pre-climax loop 2.0 x 3.0 - the trial run of 2026-11-03.) No such parameter: the animator's.
            var an = oci.charInfo.animBody;
            bool hasSpeed = false;
            if (an != null) foreach (var pa in an.parameters) if (pa.name == "speed" && pa.type == AnimatorControllerParameterType.Float) hasSpeed = true;
            if (hasSpeed) { oci.animeSpeed = 1f; an.SetFloat("speed", speed); }
            else oci.animeSpeed = speed;
            MuseStudioPlugin.Instance.Play.speedParam = speed;
            if (oci.isAnimeMotion) oci.animePattern = MuseStudioPlugin.Instance.Play.motionNow;
        }

        // ---- the motion's own sway (HSonyu / HHoushi / HMasturbation / HLesbian: the animator's "motion" 0..1 - a new target
        // every 3-5 s, once the last is reached, reached in 2-3 s along curveMotion; up by 0.1 or more till 1, then down till 0;
        // HFlag's timeAutoMotion 3 / 5, timeMotion 2 / 3, rateMotionMin 0.1). Not in aibu (the original's HAibu has none). ----
        internal float motionNow = 0.5f;
        float motionFrom, motionTo, motionT, motionDur, motionWait;
        bool motionUp = true, motionMoving;
        static readonly AnimationCurve MotionCurve = new AnimationCurve(new Keyframe(0f, 0f, 0f, 0f),
            new Keyframe(0.5006298f, 0.5009448f, 2.874899f, 2.874899f), new Keyframe(1f, 1f, 0f, 0f));
        public float MotionTarget { get { return motionTo; } }      // (checks)

        void SwayTick(float dt)
        {
            if (!Motion || (Mode != 1 && Mode != 2 && Mode != 3 && Mode != 5)) return;
            motionWait -= dt;
            if (!motionMoving && motionWait <= 0f)
            {
                motionWait = UnityEngine.Random.Range(3f, 5f);
                float to;
                if (motionUp)
                {
                    float room = 1f - motionNow;
                    to = room <= 0.1f ? 1f : motionNow + UnityEngine.Random.Range(0.1f, room);
                    if (to >= 1f) motionUp = false;
                }
                else
                {
                    to = motionNow <= 0.1f ? 0f : motionNow - UnityEngine.Random.Range(0.1f, motionNow);
                    if (to <= 0f) motionUp = true;
                }
                motionFrom = motionNow;
                motionTo = to;
                motionT = 0f;
                motionDur = UnityEngine.Random.Range(2f, 3f);
                motionMoving = true;
            }
            if (motionMoving)
            {
                motionT = Mathf.Min(motionT + dt, motionDur);
                motionNow = Mathf.Lerp(motionFrom, motionTo, MotionCurve.Evaluate(motionT / motionDur));
                if (motionT >= motionDur) motionMoving = false;
            }
            foreach (var oci in new[] { FemaleBody, MaleBody })
                if (oci != null && oci.isAnimeMotion && Mathf.Abs(oci.animePattern - motionNow) > 0.0005f) oci.animePattern = motionNow;
        }

        public float NormalizedTime()
        {
            var a = stage.Cha != null ? stage.Cha.animBody : null;
            return a != null ? Blend.Now(a).normalizedTime : 0f;      // (the state it fades into)
        }

        // the state's scene during a play: the group's provisional scene, its intensity the pace (MUSE_GUIDE.md 3.7; the body
        // moves by the H motion)
        public Dictionary<string, object> SceneInfo()
        {
            var g = Json.Obj(Json.Obj(data, "groups"), Group);
            return new Dictionary<string, object> { { "key", Json.Str(g, "scene", "") }, { "intensity", Mathf.Clamp(Pace, 1, 3) }, { "speed", SpeedFactor } };
        }

        // the male part's body stands so that its penis base is where a 0.6 tall body's would be (the Godot app's _anchor):
        // the base's position from the root x (k - 1), every frame (the motion moves the base)
        void Anchor()
        {
            var mb = MaleBody;
            if (mb == null || maleBase == null || Mathf.Abs(maleRatio - 1f) < 0.002f) return;
            var root = mb.charInfo.transform;
            Vector3 local = Quaternion.Inverse(root.rotation) * (maleBase.position - root.position);
            Stage.PlaceAt(mb, basePos + baseRot * (local * (maleRatio - 1f)), baseRot);
        }

        public float MaleRatio { get { return maleRatio; } }

        // ---- the user's body: its clothes, its eyes ----

        bool nudeNow;                  // this play strips both (shower together)
        Dictionary<int, bool> accBefore;   // her accessories taken off for it
        int PartnerClothes { get { return nudeNow ? 2 : Mathf.Clamp(MuseStudioPlugin.Instance.YourClothes.Value, 0, 2); } }

        // 脱ぐ (the default) / 着たまま / 半脱ぎ: every garment of the user's body; the penis shows through only when nude
        void ApplyPartnerClothes()
        {
            var pt = stage.Partner;
            if (pt == null) return;
            int n = PartnerClothes;
            byte st = (byte)(n == 0 ? 0 : n == 1 ? 1 : 3);
            for (int k = 0; k < pt.charInfo.fileStatus.clothesState.Length; k++) pt.charInfo.SetClothesState(k, st, true);
            var who = Who()["player"];
            pt.SetVisibleSon(n == 2 && (Json.Str(who, "body", "male") == "male" || (bool)who["futanari"]) && MuseStudioPlugin.Instance.YourPenisShown.Value);
        }

        // Your body as the original H shows the male (HSceneProc.SetConfig, ChaControl.UpdateVisible; the settings, kept): Z its
        // body hidden - head, body, clothes - but its penis; X the penis hidden; C a silhouette in the setting's colour, see-
        // through by its alpha (Studio's simple body: a male body's only - a female body of yours stays as it is).
        public void ApplyYourBody()
        {
            var pt = stage.Partner;
            if (pt == null || MuseStudioPlugin.Instance.Match.Active) return;     // (the match: the opponent's body is not yours)
            var p = MuseStudioPlugin.Instance;
            pt.charInfo.fileStatus.visibleBodyAlways = p.YourBodyShown.Value;
            var m = pt as Studio.OCICharMale;
            if (m != null)
            {
                // 透過体 (the user's request of 2026-11-04, the original's): the silhouette in its colour, see-through by the
                // setting 透過体の濃さ (V); the silhouette (C) as its colour is
                bool see = p.YourTranslucent.Value;
                m.SetVisibleSimple(p.YourSilhouette.Value || see);
                var c = p.SilhouetteColor.Value;
                m.SetSimpleColor(see ? new Color(c.r, c.g, c.b, p.TranslucentAlpha.Value) : c);
            }
            ApplyPartnerClothes();
        }

        public bool SilhouetteAvailable { get { return stage.Partner is Studio.OCICharMale; } }

        // the original H's keys (Z / X / C; the settings' keys): changed, kept, told
        public void YourBodyKeys()
        {
            var p = MuseStudioPlugin.Instance;
            if (p.BodyShownKey.Value.IsDown()) SetYourBody("body", !p.YourBodyShown.Value);
            else if (p.PenisShownKey.Value.IsDown()) SetYourBody("penis", !p.YourPenisShown.Value);
            else if (p.SilhouetteKey.Value.IsDown()) SetYourBody("silhouette", !p.YourSilhouette.Value);
            else if (p.TranslucentKey.Value.IsDown() && SilhouetteAvailable) SetYourBody("translucent", !p.YourTranslucent.Value);
        }

        public void SetYourBody(string what, bool on)
        {
            var p = MuseStudioPlugin.Instance;
            if (what == "body") p.YourBodyShown.Value = on;
            else if (what == "penis") p.YourPenisShown.Value = on;
            else if (what == "silhouette") { p.YourSilhouette.Value = on; if (on) p.YourTranslucent.Value = false; }
            else if (what == "translucent") { p.YourTranslucent.Value = on; if (on) { p.YourSilhouette.Value = false; p.YourBodyShown.Value = true; } }
            ApplyYourBody();
        }

        public Dictionary<string, object> YourBodyInfo()
        {
            var p = MuseStudioPlugin.Instance;
            return new Dictionary<string, object> { { "shown", p.YourBodyShown.Value }, { "penis_shown", p.YourPenisShown.Value }, { "silhouette", p.YourSilhouette.Value },
                { "translucent", p.YourTranslucent.Value } };
        }

        public void SetPartnerClothes(int n)
        {
            MuseStudioPlugin.Instance.YourClothes.Value = Mathf.Clamp(n, 0, 2);
            ApplyPartnerClothes();
            if (Active && stage.Partner != null)
                bridge.Event("player_clothes_changed", new Dictionary<string, object> { { "play", Info() }, { "player_clothes", ClothesKeys[PartnerClothes] } });
        }

        // 目線へ / 体位カメラへ: the camera at the partner's eyes (its head hidden), or the position's camera again
        public void SetView(string m)
        {
            PovYaw = PovPitch = 0f;           // (目線へ again / カメラを戻す: straight at her)
            View = m == "pov" && stage.Partner != null && Motion ? "pov" : "position";
            if (stage.Partner != null) stage.Partner.charInfo.fileStatus.visibleHeadAlways = View != "pov";
            if (View == "position") Camera(false);
        }

        // the head turned at the partner's eyes (the user's request of 2026-11-04): a turning drag or the turn keys (CamInput) - the
        // eyes stay, the look turns from straight at her within plays.json partner.pov turn_yaw / turn_up / turn_down
        public float PovYaw, PovPitch;

        public void PovTurn(float yaw, float pitch)
        {
            var pov = Json.Obj(Json.Obj(data, "partner"), "pov");
            float ym = (float)Json.Num(pov, "turn_yaw", 60.0);
            PovYaw = Mathf.Clamp(PovYaw + yaw, -ym, ym);
            PovPitch = Mathf.Clamp(PovPitch + pitch, -(float)Json.Num(pov, "turn_up", 35.0), (float)Json.Num(pov, "turn_down", 45.0));
        }

        // the partner's eyes: from its head up `up` metres and `forward` towards her look_bone, looking at that bone (turned by the user)
        void PovCamera()
        {
            var pt = stage.Partner;
            if (pt == null) { View = "position"; return; }
            var pov = Json.Obj(Json.Obj(data, "partner"), "pov");
            if (povHead == null || povLook == null)
            {
                povHead = Find(pt.charInfo.objBodyBone.transform, "cf_j_head");
                povLook = Find(stage.Cha.objBodyBone.transform, Json.Str(pov, "look_bone", "cf_j_spine02"));
                if (povHead == null || povLook == null) { View = "position"; return; }
            }
            Vector3 target = povLook.position, to = target - povHead.position;
            Vector3 eye = povHead.position + Vector3.up * (float)Json.Num(pov, "up", 0.04);
            if (to.magnitude > 0.2f) eye += to.normalized * (float)Json.Num(pov, "forward", 0.12);
            if ((target - eye).magnitude < 0.05f) return;
            float dist = (target - eye).magnitude;
            var look = Quaternion.LookRotation(target - eye);
            // (turned: yaw about the world's up, pitch about the look's right - a head's turn, never rolled)
            var dir = Quaternion.AngleAxis(PovYaw, Vector3.up) * (target - eye).normalized;
            float downNow = Mathf.Asin(Mathf.Clamp(-dir.y, -1f, 1f)) * Mathf.Rad2Deg;      // (never over the top: within 85 degrees of level)
            dir = Quaternion.AngleAxis(Mathf.Clamp(PovPitch, -85f - downNow, 85f - downNow), Vector3.Cross(Vector3.up, dir).normalized) * dir;
            if (PovYaw != 0f || PovPitch != 0f) { look = Quaternion.LookRotation(dir, Vector3.up); target = eye + dir * dist; }
            stage.SetCameraData(target, look, new Vector3(0f, 0f, -dist), (float)Json.Num(pov, "fov", 50.0), true);
            stage.CameraKey = "play_pov";
        }

        static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var f = Find(t.GetChild(i), name); if (f != null) return f; }
            return null;
        }

        // ---- every frame ----

        public void Progress() { if (Active) LastInput = Time.realtimeSinceStartup; }

        // the play's speed on the controllers' "speed" parameter, every frame (a new controller starts at its default), and the
        // penis's aim until BetterPenetration's controller is ready to take it
        internal float speedParam = 1f;
        int aimWant;
        float aimUntil;

        void KeepSpeed(OCIChar oci)
        {
            var an = oci != null ? oci.charInfo.animBody : null;
            if (an == null || an.runtimeAnimatorController == null) return;
            foreach (var pa in an.parameters)
                if (pa.name == "speed" && pa.type == AnimatorControllerParameterType.Float)
                {
                    if (Mathf.Abs(an.GetFloat("speed") - speedParam) > 0.001f) an.SetFloat("speed", speedParam);
                    if (oci.animeSpeed != 1f) oci.animeSpeed = 1f;
                    return;
                }
        }

        public void Tick(float dt)
        {
            if (!Active) return;
            if (Motion) { KeepSpeed(FemaleBody); KeepSpeed(MaleBody); SwayTick(dt); }
            // your male body's height (HIK.SetMaleHeight), should anything have set it back without the original's setters
            var you = stage.Partner != null && !MuseStudioPlugin.Instance.Match.Active ? stage.Partner.charInfo : null;
            if (you != null && you.sex == 0 && Mathf.Abs(you.chaFile.custom.body.shapeValueBody[0] - Mathf.Clamp(MuseStudioPlugin.Instance.YourHeight.Value, 0.35f, 0.85f)) > 0.0001f)
            {
                HIK.HeightResets++;
                HIK.SetMaleHeight(you, Mathf.Clamp(MuseStudioPlugin.Instance.YourHeight.Value, 0.35f, 0.85f));
            }
            if (aimWant != PenisAim && Time.realtimeSinceStartup < aimUntil && Penetration.Target(MaleBody != null ? MaleBody.charInfo : null, aimWant)) PenisAim = aimWant;
            Dan.Tick(PenisAim != Penetration.Off, FemaleCha);      // (no BetterPenetration: the original's aim throughout)
            Climax.Tick(dt);
            if (eyeF != null) { eyeF.Tick(); eyeM.Tick(); }
            // (inside: the finish was inside - the vagina, the mouth: the drip / spit shots; flags.isInsideFinish)
            if (FemaleCha != null) Liquid.Tick(FemaleCha.animBody, Condom == true, CameInside || Climax.LastPlace == "drink" || Climax.LastPlace == "vomit");
            if (View == "pov") PovCamera();
            Anchor();
            // a Touch state that played through goes on to the part's loop (HAibu.Proc: the reaction, then the touching loop) -
            // while the pace is up, the kiss lasts, or the user is on her (pressing, an item on her, a touch lately); with
            // none of these for a while, back to Idle (the trial run of 2026-11-03: it went back to standing at once)
            bool touching = MuseStudioPlugin.Instance.Touch.Pressing.Length > 0 || MuseStudioPlugin.Instance.Hands.Active
                            || Time.realtimeSinceStartup - lastTouched < (float)Json.Num(Json.Obj(data, "aibu"), "touch_loop_seconds", 6.0);
            if (Mode == 0 && LastState.EndsWith("_Touch") && !Climax.Running && NormalizedTime() >= 1f)
            {
                if (Pace > 0 || Kissing || touching) PlayState(AibuLoop.ContainsKey(touchPrefix) ? AibuLoop[touchPrefix] : "M_Idle", Pace > 0 ? PaceSpeed(Pace) : 1f);
                else PlayState("Idle", 1f);
            }
            else if (Mode == 0 && Pace == 0 && !Kissing && !touching && !Climax.Running && LastState.Length > 0 && LastState != "Idle"
                     && AibuLoop.ContainsValue(LastState))
            {
                reacted = "";
                PlayState("Idle", 1f);
            }
            if (Pace <= 0 || Climax.Running) return;
            // (not in her own play: she is alone)
            if (MuseStudioPlugin.Instance.Mood.MoodPlay.Length == 0 && Time.realtimeSinceStartup - LastInput > idlePause) { SetPace(0, "idle"); return; }
            if (stage.Moving) return;
            float rate = (float)Json.Num(data, "rate", 0.008) * PaceFactor(Pace) * RateOf("character");
            if (rate > 0f && !MuseStudioPlugin.Instance.Match.Active)      // (a match: no gauge - the climaxes are each Muse's declaration)
                gauge.Add(rate * dt, new Dictionary<string, object> { { "source", "play" }, { "play", Json.Str(Current, "id") }, { "pace", Pace }, { "reversed", Reversed } });
        }

        public Dictionary<string, object> Info()
        {
            if (!Active) return null;
            var g = Json.Obj(Json.Obj(data, "groups"), Group);
            int p = Mathf.Clamp(Pace, 0, 3);
            var fin = new List<object>();
            foreach (var f in Climax.Finishes()) fin.Add(f);
            return new Dictionary<string, object> {
                { "id", Json.Str(Current, "id") }, { "name", Json.Str(Current, "name") }, { "group", Group }, { "source", Json.Str(Current, "source", "original") },
                { "group_name", Json.Str(g, "name", Group) }, { "pace", p }, { "pace_name", PaceNames[p] },
                { "inserted", Group == "sonyu" ? (object)Inserted : null }, { "finishes", fin },
                { "can_pull", Group == "sonyu" && Inserted && (!Climax.Running || Climax.Showing) },
                { "condom", Group == "sonyu" && Condom.HasValue ? (object)Condom.Value : null }, { "condom_mode", CondomModeNow },
                { "can_insert", Group == "sonyu" && InsertRefusal().Length == 0 }, { "insert_refusal", Group == "sonyu" && !Inserted ? InsertRefusal() : "" },
                { "finish_sequence", Climax.Running && !Climax.Showing ? Climax.SeqName : null }, { "holding", Climax.Holding },
                { "reversed", Reversed }, { "anal", Anal }, { "roles", RolesInfo() }, { "parts", PartsInfo() }, { "lead", Res != null ? Res.Lead : "" },
                { "strapon", Res != null && Res.Strapon.Length > 0 ? Res.Strapon : null }, { "motion", Motion }, { "view", View }, { "speed", SpeedFactor },
                { "motion_speed", Math.Round(PaceSpeed(Pace), 2) }, { "speed_max", SpeedMax },
                { "player_clothes", stage.Partner != null ? ClothesKeys[PartnerClothes] : null },
                { "rates", new Dictionary<string, object> { { "character", RateOf("character") }, { "player", RateOf("player") } } },
                { "partner", stage.Partner != null }, { "location", stage.Location }, { "spot", SpotInfo(CurrentSpot, Current) },
                { "started", Started }, { "seconds", Math.Round(Time.realtimeSinceStartup - StartedAt, 1) } };
        }

        Dictionary<string, object> RolesInfo()
        {
            var o = new Dictionary<string, object>();
            if (Res != null) foreach (var kv in Res.Roles) o[kv.Key] = kv.Value;
            return o;
        }

        // the parts each side uses, by name
        Dictionary<string, object> PartsInfo()
        {
            var o = new Dictionary<string, object>();
            if (Res == null) return o;
            foreach (var kv in Res.Parts)
            {
                var names = new List<object>();
                foreach (var k in kv.Value) names.Add(Json.Str(Json.Obj(Json.Obj(data, "parts"), k), "name", k));
                o[kv.Key] = names;
            }
            return o;
        }

        // {id, name, place, place_name, tags}: place = the play's first place kind the spot takes
        public Dictionary<string, object> SpotInfo(Spot s, Dictionary<string, object> row)
        {
            if (s == null) return null;
            string place = null;
            foreach (var k in Json.Arr(row, "places") ?? new List<object>())
            {
                foreach (var t in Json.Arr(Json.Obj(Json.Obj(data, "places"), (string)k), "toggles") ?? new List<object>())
                    if (s.Categories.Contains(Convert.ToInt32(t))) { place = (string)k; break; }
                if (place != null) break;
            }
            return new Dictionary<string, object> { { "id", s.Id }, { "name", s.Name }, { "place", place },
                { "place_name", place != null ? Json.Str(Json.Obj(Json.Obj(data, "places"), place), "name", place) : null }, { "tags", new List<object>(s.Tags.ToArray()) } };
        }
    }
}
