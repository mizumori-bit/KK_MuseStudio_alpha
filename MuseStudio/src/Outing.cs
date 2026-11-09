using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // She goes on her own (a block's action toilet / shower; Urges; DECISIONS "Muse 側の資料との読み合わせ"): to the room of
    // urges.json outings (a room kept in the scene, Rooms) while the camera - the user - stays where he is. There she does it
    // with the original's peeping motions (Studio's H特殊: トイレ覗き In / Loop / Out, シャワー覗き), placed by the room's
    // landmark (the toilet: its H point of the seat - the mod rooms mimic the original's stall; the shower: urges.json's
    // "at", the middle of the washing place under the shower on the wall, the Japanese way, not in the bath), her lower
    // clothes off for the toilet (the original's pee while it loops), all off for the shower (her accessories too, but the
    // hair's: BathAccessories); then she
    // comes back to the user's room (Presence: "met" if he is back from away meanwhile). He may follow: in her room he is
    // unseen (Presence.Hidden - the peeping is never found out for now); if he is there when she is done, Muse is told
    // (outing_ended user_there true) and she stays there with him. Her monologue there (Muse's monologue packages after
    // outing_started: she thinks she is alone) is heard only while he is in her room (BlockPlayer.Audible); what is left goes
    // when she is done. Her eyes (the trial run of 2026-11-04): at him only when she knows he is there; else (alone, or he
    // peeps unseen) straight ahead (Gaze) - her face likewise (Look: not together, the neck by the motion).
    // Her own time in her room (Muse's request 3 of 2026-11-04): alone (masturbation: true - the original's solo position
    // there: slow, strong, her climax, after) and change (her clothes off and on, another outfit by number) - the same room
    // for both, so only peeping tells which. While she is out nothing that rests on where he is goes to Muse (Plugin: the
    // state's location stays the room she left him in, location_changed and the catalog wait; BlockPlayer: her monologue
    // goes on its own time); she finds him only when he calls out (Call: user_found), or there when she is done. Out of it
    // his room is known to her again (#101 item 1, the owner's decision: where the two are is open, the app holding back
    // what she cannot know - his moves while she is out): outing_ended tells where he is now, with him or not.
    public class Outing
    {
        readonly MuseStudioPlugin h;
        public string Kind = "", Room = "";
        public bool Out { get { return Kind.Length > 0; } }
        // (out) he is in her room and she knows it: there when she went, not gone since
        public bool SeesHim { get { return Out && userWasThere && h.Stage.Location == Room; } }
        public float SecondsForCheck = -1f;       // (checks) a shorter outing
        public string Phase = "";                 // in / loop / out / climax / after / changing (checks)
        public string KnownRoom = "";             // the room she left him in (what she knows of where he is while she is out)
        public float DressedAt = -1f;             // (checks) the toilet: her clothes on again, seconds into the Out clip
        Dictionary<string, object> opts;
        float endsAt;
        bool userWasThere;
        bool gazeOff;                             // her eyes held off the camera (Gaze)
        readonly Dictionary<int, byte> clothesBefore = new Dictionary<int, byte>();
        Dictionary<int, bool> accBefore = new Dictionary<int, bool>();
        static readonly Dictionary<string, string[]> Landmarks = new Dictionary<string, string[]> {
            { "toilet", new[] { "base/seat_00", "add/lesbSeat_00" } },
            { "shower", new[] { "base/stand_00" } } };
        static readonly int[] LowerClothes = { 1, 3, 5 };     // bottom, shorts, pantyhose

        public Outing(MuseStudioPlugin host) { h = host; }

        static float Now { get { return Time.realtimeSinceStartup; } }

        static readonly Dictionary<string, string> PlaceNames = new Dictionary<string, string> {
            { "toilet", "トイレ" }, { "shower", "シャワー" }, { "alone", "ひとりで" }, { "change", "着替え" } };

        // the outings that take her to this room (SpotEditor's 「行動の位置」)
        public List<string> KindsIn(string room)
        {
            var o = new List<string>();
            var outs = Json.Obj(h.Urges.Doc, "outings");
            if (outs != null) foreach (var kv in outs) if (Json.Str(kv.Value as Dictionary<string, object>, "room", "") == room) o.Add(kv.Key);
            return o;
        }

        // where an outing puts her, as the user adjusted it (SpotEditor: "outing:<kind>" - the user's request, PR #23; the
        // toilet's seat and the shower's stand read the original's points directly before, past the play spots' adjustments)
        public Spot Place(string kind)
        {
            string room = Json.Str(Json.Obj(Json.Obj(h.Urges.Doc, "outings"), kind), "room", "");
            Vector3 pos;
            Quaternion rot;
            Landmark(kind, room, out pos, out rot);
            string nm;
            if (!PlaceNames.TryGetValue(kind, out nm)) nm = kind;
            // (her solo play takes the room's desk corner / chair play spot when it has one - its own adjustments, 「プレイ位置」;
            // standing, or with none, this place: GPT's re-review of #30)
            if (kind == "alone") nm += "（自分でするときは、机・椅子のプレイ位置があればそこ）";
            return new Spot { Id = "outing:" + kind, Name = nm, Pos = pos, Rot = rot };
        }

        bool Landmark(string kind, string room, out Vector3 pos, out Quaternion rot)
        {
            bool marked = LandmarkBase(kind, room, out pos, out rot);
            var sp = new Spot { Id = "outing:" + kind, Pos = pos, Rot = rot };
            if (h.SpotEditor != null) h.SpotEditor.Apply(room, sp);
            pos = sp.Pos;
            rot = sp.Rot;
            return marked;
        }

        // where she stands for it in the original's terms: urges.json's "at" (the room's coordinates), else the room's landmark
        // spot (its H point), else its stand (none: the room's origin)
        bool LandmarkBase(string kind, string room, out Vector3 pos, out Quaternion rot)
        {
            var row = Json.Obj(Json.Obj(Data.Load("locations.json"), "locations"), room);
            Vector3 off = h.Rooms.OffsetOf(room);
            var at = Json.Arr(Json.Obj(Json.Obj(h.Urges.Doc, "outings"), kind), "at");
            if (at != null && at.Count >= 4)
            {
                pos = off + new Vector3(Convert.ToSingle(at[0]), Convert.ToSingle(at[1]), Convert.ToSingle(at[2]));
                rot = Quaternion.Euler(0f, Convert.ToSingle(at[3]), 0f);
                return true;
            }
            string[] ids;
            if (Landmarks.TryGetValue(kind, out ids))           // (alone / change: none - the room's stand; her motion places her)
                foreach (var id in ids)
                    foreach (var sp in HData.Points((int)Json.Num(row, "h_map", -1)))
                        if (sp.Id == id) { pos = sp.Pos + off; rot = sp.Rot; return true; }
            var stand = Json.Arr(row, "stand");
            pos = off + (stand != null && stand.Count >= 3 ? new Vector3(Convert.ToSingle(stand[0]), Convert.ToSingle(stand[1]), Convert.ToSingle(stand[2])) : Vector3.zero);
            rot = Quaternion.Euler(0f, stand != null && stand.Count >= 4 ? Convert.ToSingle(stand[3]) : 0f, 0f);
            return false;
        }

        public Spot MasturbationAt;     // (checks) the play spot her solo play took (null: where 「ひとりで」 put her)

        public bool Masturbating { get { return Out && Kind == "alone" && opts != null && opts.ContainsKey("masturbation") && opts["masturbation"] is bool && (bool)opts["masturbation"]; } }

        public string Go(string kind, Dictionary<string, object> options = null)
        {
            opts = options;
            var def = Json.Obj(Json.Obj(h.Urges.Doc, "outings"), kind);
            string room = Json.Str(def, "room", "");
            if (def == null || !h.Stage.Reachable(room)) return "no_room";
            if (!h.Rooms.Has(room))
            {
                // her outing's room not loaded (PR #46 §4.1): it comes in first, then she goes
                if (goingWhenIn) return "busy";
                goingWhenIn = true;
                MuseStudioPlugin.Instance.StartCoroutine(GoWhenIn(kind, options, room));
                return "";
            }
            if (h.Play.Active) h.Play.End("action");
            h.Stage.StopWalk(false);
            Kind = kind;
            Room = room;
            userWasThere = h.Stage.Location == room;
            fromRoom = h.Presence.HerRoom;                      // (where she goes back to)
            KnownRoom = h.Stage.Location;                       // (his room is known to her till she goes: #101 item 1)
            h.Presence.HerAt = room;
            returnAt = -1f;
            Vector3 pos;
            Quaternion rot;
            bool marked = Landmark(kind, room, out pos, out rot);
            Stage.PlaceAt(h.Stage.Heroine, pos, rot);
            var cha = h.Stage.Cha;
            clothesBefore.Clear();
            for (int k = 0; k < cha.fileStatus.clothesState.Length; k++) clothesBefore[k] = cha.fileStatus.clothesState[k];
            if (kind == "toilet") foreach (int k in LowerClothes) cha.SetClothesState(k, 3, false);
            else if (kind == "shower")
            {
                for (int k = 0; k < cha.fileStatus.clothesState.Length; k++) cha.SetClothesState(k, 3, false);
                accBefore = BathAccessories.Off(cha);
            }
            // (alone / change: dressed as she is - the motion undresses her)
            endsAt = Now + (SecondsForCheck > 0f ? SecondsForCheck : (float)Json.Num(def, "seconds", 60.0));
            MuseStudioPlugin.Instance.StartCoroutine(Motions(kind));
            var started = new Dictionary<string, object> { { "kind", kind }, { "room", h.Stage.LocationInfo(room) },
                { "user_there", userWasThere }, { "presence", h.Presence.Info() } };
            if (kind == "alone") started["masturbation"] = Masturbating;
            h.Bridge.Event("outing_started", started);
            MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: outing {0} to {1} at {2} (landmark {3})", kind, room, pos, marked));
            return "";
        }

        bool goingWhenIn;

        System.Collections.IEnumerator GoWhenIn(string kind, Dictionary<string, object> options, string room)
        {
            yield return h.Stage.EnsureRoom(room);
            goingWhenIn = false;
            if (h.Rooms.Has(room)) Go(kind, options);
            else { MuseStudioPlugin.Log.LogWarning("MuseStudio: outing " + kind + ": its room " + room + " could not be loaded"); h.RoomNotLoaded(room); }
        }

        // the original's peeping motions: the toilet In, Loop (her pee) till the time is up, Out; the shower's loop
        IEnumerator Motions(string kind)
        {
            var oci = h.Stage.Heroine;
            // the original's peeping sounds over the motion (its HSeCtrl lists: the toilet's pee at In's end, the paper and the flush
            // in Out; the shower's water) - the user's note of 2026-11-04: no pee sound
            if (kind == "toilet" || kind == "shower") h.Sounds.PlayStarted(kind == "toilet" ? "se_khn_00" : "se_khn_01", h.Stage.Cha, null);
            if (kind == "toilet")
            {
                Phase = "in";
                oci.LoadAnime(6, 175, 0);
                yield return Clip(10f);           // (its clip is 8.3 s: the pee's sound at 95 % - cut at 6 s it never came)
                if (!Out) yield break;
                Phase = "loop";
                oci.LoadAnime(6, 175, 1);
                HLiquid.Pee(h.Stage.Cha, Mathf.Min(8f, Mathf.Max(1f, endsAt - Now - 4f)));
                while (Out && Now < endsAt - 3f) yield return null;
                if (!Out) yield break;
                Phase = "out";
                oci.LoadAnime(6, 175, 2);
                // her lower clothes on again when she has pulled them up: by time from the Out clip's own start (19 s: she stands at
                // 10, bends to her knees at 11, her hands at her waist at 12 - urges.json outings.toilet.dress_seconds). The user's
                // notes of 2026-11-04: out of the toilet bare; later. (Before, a cap of 3.6 s - the clip taken for a short one - always
                // came first: on at 3.6 s, then 5.1 s, both long before she pulls them up.)
                var animO = h.Stage.Cha.animBody;
                float loadedAt = Now;
                while (Out && Now - loadedAt < 2f && animO != null && !animO.GetCurrentAnimatorStateInfo(0).IsName("Out")) yield return null;
                float outClip = Now, dressAt = outClip + (float)Json.Num(Json.Obj(Json.Obj(h.Urges.Doc, "outings"), "toilet"), "dress_seconds", 12.0);
                while (Out && Now < dressAt) yield return null;
                if (!Out) yield break;
                var cha = h.Stage.Cha;
                foreach (int k in LowerClothes)
                {
                    byte b;
                    if (clothesBefore.TryGetValue(k, out b)) cha.SetClothesState(k, b, false);
                }
                DressedAt = Now - outClip;
                yield return Clip(10f);
            }
            else if (kind == "shower")
            {
                Phase = "loop";
                oci.LoadAnime(6, 176, 0);
                while (Out && Now < endsAt) yield return null;
            }
            else if (kind == "alone" && Masturbating) yield return Masturbation(oci);
            else if (kind == "alone")
            {
                Phase = "alone";
                oci.LoadAnime(0, 8, 3);                   // (Studio: 正座)
                while (Out && Now < endsAt) yield return null;
            }
            else if (kind == "change") yield return Change(oci);
        }

        // her solo play there: the original's position (the room's desk corner / chair if it has one, else standing), its
        // loops slow to strong over the time, OLoop, her climax, after - as the original's states, undressed as it says
        IEnumerator Masturbation(Studio.OCIChar oci)
        {
            var pc = h.Play;
            Dictionary<string, object> row = null;
            Spot at = null;
            var spots = h.Stage.SpotsIn(Room);
            foreach (var id in new[] { "masturbation_00", "masturbation_01" })
            {
                var r = pc.Row(id);
                if (r == null) continue;
                var tg = pc.TogglesOf(r);
                foreach (var sp in spots) foreach (var c in sp.Categories) if (tg.Contains(c) && at == null) at = sp;
                if (at != null) { row = r; break; }
            }
            if (row == null) row = pc.Row("masturbation_02");
            if (row == null) yield break;
            MasturbationAt = at;
            if (at != null) Stage.PlaceAt(oci, at.Pos, at.Rot);
            var fem = Json.Arr(row, "female");
            var states = Json.Obj(row, "female_states");
            var hinfo = HData.Info(3, (int)Json.Num(row, "motion_id"));
            if (hinfo != null)
            {
                GlobalMethod.SetAllClothState(h.Stage.Cha, true, hinfo.numFemaleUpperCloth, false);
                GlobalMethod.SetAllClothState(h.Stage.Cha, false, hinfo.numFemaleLowerCloth, false);
            }
            int g = Convert.ToInt32(fem[0]), cat = Convert.ToInt32(fem[1]);
            float total = Mathf.Max(20f, endsAt - Now);
            var plan = new[] { new KeyValuePair<string, float>("WLoop", 0.35f), new KeyValuePair<string, float>("MLoop", 0.25f), new KeyValuePair<string, float>("SLoop", 0.2f),
                new KeyValuePair<string, float>("OLoop", 0.06f) };
            Phase = "loop";
            foreach (var st in plan)
            {
                if (!Out) yield break;
                if (Json.Has(states, st.Key)) oci.LoadAnime(g, cat, Convert.ToInt32(states[st.Key]));
                float until = Now + total * st.Value;
                while (Out && Now < until) yield return null;
            }
            if (!Out) yield break;
            Phase = "climax";
            if (Json.Has(states, "Orgasm")) { oci.LoadAnime(g, cat, Convert.ToInt32(states["Orgasm"])); yield return Clip(8f); }
            if (!Out) yield break;
            Phase = "after";
            if (Json.Has(states, "Orgasm_A")) oci.LoadAnime(g, cat, Convert.ToInt32(states["Orgasm_A"]));
            while (Out && Now < endsAt) yield return null;
        }

        // her clothes off, another outfit if asked (the card's, by number), on again
        IEnumerator Change(Studio.OCIChar oci)
        {
            Phase = "changing";
            oci.LoadAnime(0, 0, 7);
            var cha = h.Stage.Cha;
            float total = Mathf.Max(6f, endsAt - Now);
            float off = Now + total * 0.25f, on = Now + total * 0.6f;
            while (Out && Now < off) yield return null;
            if (!Out) yield break;
            for (int k = 0; k < cha.fileStatus.clothesState.Length; k++) cha.SetClothesState(k, 3, false);
            while (Out && Now < on) yield return null;
            if (!Out) yield break;
            object o;
            string coord = opts != null ? Json.Str(opts, "coordinate", null) : null;
            if (coord != null)
            {
                // an outfit card (PR #46 §3): worn here, her own change; not there or not readable then - the clothes she had, told
                string why = h.Coords.Wear(coord, "muse", true);
                if (why.Length > 0)
                    h.Bridge.Event("block_refused", new Dictionary<string, object> { { "field", "action.coordinate" }, { "value", coord }, { "reason", why },
                        { "available", new List<object>() } });
            }
            else if (opts != null && opts.TryGetValue("outfit", out o) && o != null)
            {
                int n = Mathf.Clamp(Convert.ToInt32(o), 0, cha.chaFile.coordinate.Length - 1);
                oci.SetCoordinateInfo((ChaFileDefine.CoordinateType)n, true);
            }
            for (int k = 0; k < cha.fileStatus.clothesState.Length; k++) cha.SetClothesState(k, 0, false);
            clothesBefore.Clear();                   // (dressed anew: not put back at the end)
            Phase = "dressed";
            while (Out && Now < endsAt) yield return null;
        }

        // what she is doing now (user_found)
        string Doing()
        {
            if (Kind == "alone" && Masturbating) return Phase == "climax" ? "climax" : Phase == "after" ? "afterglow" : "masturbation";
            if (Kind == "alone") return "alone";
            if (Kind == "change") return "changing";
            return Kind;
        }

        // he is in her room unseen: he may call out (the panel's 声をかける)
        public bool CanCall { get { return Out && h.Stage.Location == Room && h.Presence.Hidden; } }

        // he calls out: the first Muse hears of him there (user_found); she stops where she is - standing, her clothes as they
        // are (caught: dressing is Muse's) - and the outing is over, the two together in her room
        public void Call()
        {
            if (!CanCall) return;
            h.Sounds.PlayEnded();
            string doing = Doing(), kind = Kind, room = Room;
            h.Blocks.DropMonologue("user_found");
            Kind = "";
            Room = "";
            Phase = "";
            opts = null;
            accBefore.Clear();
            h.Presence.HerAt = "";
            h.Presence.Hidden = false;
            if (gazeOff) { h.Stage.Heroine.ChangeLookEyesPtn(1); gazeOff = false; }
            h.Stage.ApplyScene(Json.Str(h.Stage.Cfg, "idle_scene", "idle"), 1, 1f);
            h.Bridge.Event("user_found", new Dictionary<string, object> { { "cause", "called" }, { "kind", kind }, { "room", h.Stage.LocationInfo(room) },
                { "doing", doing }, { "location", h.Stage.LocationInfo(h.Stage.Location) }, { "presence", h.Presence.Info() } });
            h.Rejoined("found", KnownRoom);
            KnownRoom = "";
        }

        // a clip played once: till its end (at most seconds)
        IEnumerator Clip(float most)
        {
            float t0 = Now;
            yield return null;
            yield return null;
            var anim = h.Stage.Cha.animBody;
            while (Out && Now - t0 < most && anim != null && anim.GetCurrentAnimatorStateInfo(0).normalizedTime < 0.98f) yield return null;
        }

        public void Tick()
        {
            if (!Out) return;
            // he came into her room after her: unseen (he left: what she knew of him there is gone)
            bool here = h.Stage.Location == Room;
            if (!here) userWasThere = false;
            h.Presence.Hidden = here && !userWasThere;
            Gaze(here && userWasThere);
            // done when her motion is (the toilet: its Out played out); then, if he is not with her there, a walk back before she is
            // in her room again (the trial run of 2026-11-04: she was back while he saw her still at it)
            bool motionDone = Now >= endsAt && Phase != "in" && Phase != "climax" && (Kind != "toilet" || Phase == "out" && Clipped());
            if (motionDone || Now >= endsAt + 40f)            // (a motion that never ends)
            {
                if (here || returnAt >= 0f && Now >= returnAt) Done();
                else if (returnAt < 0f)
                {
                    returnAt = Now + (float)Json.Num(h.Urges.Doc, "outing_return_seconds", 4.0);
                    Phase = "returning";
                }
            }
        }

        // Where her eyes go while she is out: at him (the camera) only when she knows he is there - the original's dividing
        // line, found out or not; else straight ahead (eye pattern 0, the original's FORWARD; her neck: Look, by the motion) -
        // the original's peeping list (h/list neck_khn_f_00 / 01: id 0, the eyes' target the camera within their range) would
        // have them follow him unseen. Held every frame: a block's face may set the eyes (its eyes_look) meanwhile.
        void Gaze(bool knows)
        {
            var oci = h.Stage.Heroine;
            var st = h.Stage.Cha.fileStatus;
            if (!knows)
            {
                if (st.eyesLookPtn != 0) oci.ChangeLookEyesPtn(0);
                gazeOff = true;
            }
            else if (gazeOff) { oci.ChangeLookEyesPtn(1); gazeOff = false; }
        }

        bool Clipped()
        {
            var anim = h.Stage.Cha.animBody;
            return anim == null || anim.GetCurrentAnimatorStateInfo(0).normalizedTime >= 0.98f;
        }

        string fromRoom = "";
        float returnAt = -1f;

        void Done()
        {
            h.Sounds.PlayEnded();
            h.Blocks.DropMonologue("outing_ended");          // (what she had left to say alone in there - before the relief's mood change)
            string kind = Kind, room = Room;
            bool userThere = h.Stage.Location == room;
            var cha = h.Stage.Cha;
            // dressed again: after the shower the outfit she has on, worn in full (not the card's, not as before she went - #101
            // item 2, the owner's decision); else as she was before she went
            if (kind == "shower") for (int k = 0; k < cha.fileStatus.clothesState.Length; k++) cha.SetClothesState(k, 0, false);
            else foreach (var kv in clothesBefore) cha.SetClothesState(kv.Key, kv.Value, false);
            BathAccessories.Back(cha, accBefore);
            accBefore.Clear();
            Kind = "";
            Room = "";
            Phase = "";
            opts = null;
            h.Presence.Hidden = false;
            if (gazeOff) { h.Stage.Heroine.ChangeLookEyesPtn(1); gazeOff = false; }     // (at the camera again: the stage's)
            // where she is now: with him if he is there (her room or where she went), else back in the room she came from
            if (userThere || fromRoom.Length == 0 || fromRoom == h.Stage.Location)
            {
                h.Presence.HerAt = "";
                h.Stage.Place(h.Stage.Heroine);
            }
            else
            {
                h.Presence.HerAt = fromRoom;
                h.Stage.PlaceIn(h.Stage.Heroine, fromRoom);
            }
            h.Stage.ApplyScene(Json.Str(h.Stage.Cfg, "idle_scene", "idle"), 1, 1f);
            if (kind == "toilet") h.Mood.Relieve("bladder");
            bool with = h.Presence.HerAt.Length == 0;
            h.Bridge.Event("outing_ended", new Dictionary<string, object> { { "kind", kind }, { "room", h.Stage.LocationInfo(room) }, { "user_there", userThere },
                { "location", h.Stage.LocationInfo(h.Stage.Location) }, { "her_location", h.Stage.LocationInfo(h.Presence.HerRoom) }, { "presence", h.Presence.Info() } });
            h.Rejoined(with ? "met" : "outing_ended", KnownRoom);     // (where he is now, known to her again: with him or not - #101 item 1)
            KnownRoom = "";
        }

        public Dictionary<string, object> Info()
        {
            if (!Out) return null;
            return new Dictionary<string, object> { { "kind", Kind }, { "room", Room }, { "seconds_left", Math.Max(0, Math.Round(endsAt - Now)) } };
        }
    }

    // Her accessories in the bath (the user's decision 2026-11-03): off with the clothes, but the hair's - the hair category
    // (the original's 髪) or worn on the hair - since many cards build parts of the hair from accessories. Undressing is the
    // original's eight clothes only; accessories follow it only where the card links them (its hide setting, Accessory States).
    public static class BathAccessories
    {
        public static bool IsHair(ChaFileAccessory.PartsInfo p)
        {
            return p != null && (p.type == 121 || (p.parentKey ?? "").StartsWith("a_n_hair"));
        }

        // all off but the hair's: what they were, to put back
        public static Dictionary<int, bool> Off(ChaControl cha)
        {
            var before = new Dictionary<int, bool>();
            var parts = cha.nowCoordinate.accessory.parts;
            var show = cha.fileStatus.showAccessory;
            for (int i = 0; i < Math.Min(parts.Length, show.Length); i++)
            {
                if (parts[i].type == 120 || IsHair(parts[i])) continue;
                before[i] = show[i];
                cha.SetAccessoryState(i, false);
            }
            return before;
        }

        public static void Back(ChaControl cha, Dictionary<int, bool> before)
        {
            if (cha == null || before == null) return;
            foreach (var kv in before) cha.SetAccessoryState(kv.Key, kv.Value);
        }
    }
}
