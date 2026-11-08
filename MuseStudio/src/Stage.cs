using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Studio;
using UnityEngine;

namespace MuseStudio
{
    // The stage on Studio: the location (a Studio map), the character (a card) and, in a play, the partner; her face, the
    // animation of a scene key, the camera presets. Studio's own UI is hidden (the canvases HideAllUI hides; F2 shows them).
    public class Stage
    {
        public OCIChar Heroine;
        public OCIChar Partner;
        public string Location = "";
        public string SceneKey = "";
        public int Intensity = 1;
        public float Speed = 1f;
        public string Emotion = "";
        public string CameraKey = "";
        public string Name = "";
        public bool Ready;
        public bool Moving;                              // a location is loading
        public List<Spot> Spots = new List<Spot>();     // the location's H points ("here" first: where she stands)

        readonly Dictionary<string, object> cfg, faces, scenes, cameras, locations;
        readonly List<GameObject> hidden = new List<GameObject>();
        static readonly string[] StudioCanvases = { "Canvas", "Canvas Object List", "Canvas Main Menu", "Canvas System Menu",
                                                    "Canvas Guide Input", "CvsColor", "Canvas Pattern" };

        public Stage()
        {
            cfg = Data.Load("stage.json");
            faces = Data.Load("faces.json");
            scenes = Data.Load("scenes.json");
            cameras = Data.Load("cameras.json");
            locations = Data.Load("locations.json");
        }

        public Dictionary<string, object> Cfg { get { return cfg; } }

        public ChaControl Cha { get { return Heroine != null ? Heroine.charInfo : null; } }

        public IEnumerator Setup()
        {
            // KKAPI says "loaded" when Studio's scene is in; Studio.Init (its scene info) and the lists come a little later
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 60f && (!Singleton<Studio.Studio>.IsInstance() || Singleton<Studio.Studio>.Instance.sceneInfo == null
                   || !Singleton<Info>.IsInstance() || !Singleton<Info>.Instance.isLoadList)) yield return null;
            for (int i = 0; i < 10; i++) yield return null;
            var studio = Singleton<Studio.Studio>.Instance;
            Step("init scene");
            studio.InitScene(false);
            yield return null;
            // every room, by name (PR #46 §4.1): Studio's maps - before the kept ones come in (their features then)
            MuseStudioPlugin.Instance.Places.Register(locations);
            Step("rooms registered " + MuseStudioPlugin.Instance.Places.Count);
            // the rooms kept in the scene (the setting "Rooms"): loaded once, now
            var keys = new List<string>();
            foreach (var k in MuseStudioPlugin.Instance.RoomKeys.Value.Split(',')) if (k.Trim().Length > 0) keys.Add(k.Trim());
            Step("rooms " + string.Join(",", keys.ToArray()));
            yield return MuseStudioPlugin.Instance.Rooms.Load(keys, locations);
            Step("rooms loaded");
            string loc = Json.Str(cfg, "location", "myroom");
            Step("location " + loc);
            yield return SetLocation(loc);
            var ch = Json.Obj(cfg, "character");
            // the character: chosen now (the chooser waits; the last one with "start with the last character")
            HideStudioUi(true);
            studio.cameraCtrl.noCtrlCondition = () => MuseStudioPlugin.Instance.Launch.Choosing;
            yield return MuseStudioPlugin.Instance.Launch.Choose();
            string card = MuseStudioPlugin.Instance.Launch.Chosen;
            Name = Json.Str(ch, "name", "");
            if (!File.Exists(card)) MuseStudioPlugin.Log.LogWarning("MuseStudio: no character card " + card + " (the default body)");
            Step("character " + card);
            Heroine = AddChara(false, card);
            TakeCardFace(card);                // (her card's own face: the start face and the talk's default)
            MuseStudioPlugin.Instance.Chars.CardPath = File.Exists(card) ? card : "";
            if (File.Exists(card)) Name = Cha.chaFile.parameter.fullname.Trim();     // the card's name (stage.json's for the default body)
            Step("character added");
            yield return null;
            Place(Heroine);
            ApplyScene(Json.Str(cfg, "idle_scene", "idle"), 1, 1f);
            ApplyFace(Json.Str(cfg, "start_face", "service_smile"));
            Heroine.ChangeLookEyesPtn(1);      // at the camera
            Heroine.ChangeLookNeckPtn(3);      // the neck by the motion (the original's talk: TalkScene ChangeLookNeckPtn(3))
            yield return null;
            ApplyCamera(Json.Str(cameras, "default", "medium"), true);
            MuseStudioPlugin.Instance.CameraMemory.Recall(Location);         // (the view the user left this room's camera at)
            HideStudioUi(true);
            Ready = true;
            Step("ready");
        }

        static void Step(string s) { MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: setup {0} ({1:F1} s)", s, Time.realtimeSinceStartup)); }

        public IEnumerator SetLocation(string key)
        {
            var places = MuseStudioPlugin.Instance.Places;
            key = places != null ? (places.KeyOf(key) ?? key) : key;      // (named by its id: its key)
            var row = Json.Obj(Json.Obj(locations, "locations"), key);
            if (row == null) { MuseStudioPlugin.Log.LogWarning("MuseStudio: unknown location " + key); yield break; }
            var map = Singleton<Map>.Instance;
            var rooms = MuseStudioPlugin.Instance.Rooms;
            Moving = true;
            StopWalk(false);
            if (!rooms.Has(key))
            {
                // a room not loaded (PR #46 §4.1): in beside the others now, as the kept ones came in - Studio's own map beside
                // them showed broken (the trial run of 2026-11-03); not usable before it is in
                yield return rooms.LoadLazy(key, row);
                if (!rooms.Has(key))
                {
                    Moving = false;
                    MuseStudioPlugin.Log.LogWarning("MuseStudio: room " + key + " could not be loaded: the place stays");
                    yield break;
                }
            }
            // instant from here (Studio's own map, if one is in, goes)
            if (Ready && key != Location) MuseStudioPlugin.Instance.Sounds.Moving(Location, key);
            if (map.no != -1) { map.ReleaseMap(); yield return null; }
            rooms.Show(key);
            rooms.Touch(key);
            yield return null;
            Moving = false;
            Location = key;
            LoadSpots(row);
            TrimRooms();
            if (Ready) MuseStudioPlugin.Instance.Sounds.Entered(key);
            if (Heroine != null && !MuseStudioPlugin.Instance.Outing.Out && MuseStudioPlugin.Instance.Presence.HerAt.Length == 0) Place(Heroine);     // (out on her own, or left where she is: she stays there)
            if (Partner != null)
            {
                if (MuseStudioPlugin.Instance.Match.Active) MuseStudioPlugin.Instance.Match.PlaceOpponent();     // (facing her)
                else Place(Partner);
            }
        }

        // the room's references read again (SpotEditor.ResetAll): its spots, and she where she stands outside a play
        public void ReloadSpots()
        {
            if (string.IsNullOrEmpty(Location)) return;
            LoadSpots(Json.Obj(Json.Obj(locations, "locations"), Location));
            var h = MuseStudioPlugin.Instance;
            h.Play.Rebind(Spots);                       // (a play going on: its spot's new object - GPT's re-review of #30)
            if (Heroine != null && !h.Play.Active && !h.Outing.Out && h.Presence.HerAt.Length == 0 && !Walking) Place(Heroine);
        }

        // "here" (where she stands: the original's HPointBase) and the H points of the location's original map (h_map).
        void LoadSpots(Dictionary<string, object> row)
        {
            Spots = new List<Spot>();
            var plays = Data.Load("plays.json");
            var here = new Spot { Id = "here", Name = Json.Str(Json.Obj(plays, "here"), "name", "その場"), Here = true };
            Vector3 off = MuseStudioPlugin.Instance.Rooms.OffsetOf(Location);
            var hereKinds = Json.Arr(row, "here_places") ?? Json.Arr(Json.Obj(plays, "here"), "places");     // (a location's own: the match's room)
            if (hereKinds != null)
                foreach (var k in hereKinds)
                    foreach (var t in Json.Arr(Json.Obj(Json.Obj(plays, "places"), (string)k), "toggles") ?? new List<object>())
                        here.Categories.Add(Convert.ToInt32(t));
            Vector3 pos; float yaw;
            StandOf(out pos, out yaw);
            here.Pos = pos;
            here.Rot = Quaternion.Euler(0f, yaw, 0f);
            Spots.Add(here);                     // (StandOf: as the user adjusted it)
            if (!Json.Has(row, "h_map")) return;
            var names = Json.Obj(row, "spots");
            var generic = Json.Obj(plays, "spot_names");
            int merged = 0;
            foreach (var sp in HData.Points((int)Json.Num(row, "h_map")))
            {
                sp.Pos += off;           // (a room kept in the scene sits off the origin)
                var nm = Json.Obj(names, sp.Id);
                sp.Name = Json.Str(nm, "name", null) ?? Generic(generic, sp.Object);
                var tags = Json.Arr(nm, "tags");
                if (tags != null) foreach (var t in tags) sp.Tags.Add((string)t);
                // the same name within 0.35 m and 60 degrees is one spot (the Godot app's: the original has a point per kind of
                // play at one seat; the stall's two walls face apart and stay two); its id still names it (Aliases)
                var same = Spots.Find(o => !o.Here && o.Name == sp.Name && (o.Pos - sp.Pos).magnitude <= 0.35f && Quaternion.Angle(o.Rot, sp.Rot) <= 60f);
                if (same != null)
                {
                    foreach (var c in sp.Categories) if (!same.Categories.Contains(c)) same.Categories.Add(c);
                    foreach (var t in sp.Tags) if (!same.Tags.Contains(t)) same.Tags.Add(t);
                    same.Aliases.Add(sp.Id);
                    merged++;
                    continue;
                }
                Spots.Add(sp);
            }
            foreach (var sp in Spots) if (!sp.Here) MuseStudioPlugin.Instance.SpotEditor.Apply(Location, sp);
            MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: {0} spots at {1} (h_map {2}, {3} merged)", Spots.Count, Location, Json.Num(row, "h_map"), merged));
        }

        // the H points of a room (kept in the scene: its offset), named as LoadSpots names them (no merging)
        public List<Spot> SpotsIn(string room)
        {
            var o = new List<Spot>();
            var row = Json.Obj(Json.Obj(locations, "locations"), room);
            if (!Json.Has(row, "h_map")) return o;
            Vector3 off = MuseStudioPlugin.Instance.Rooms.OffsetOf(room);
            foreach (var sp in HData.Points((int)Json.Num(row, "h_map"))) { sp.Pos += off; MuseStudioPlugin.Instance.SpotEditor.Apply(room, sp); o.Add(sp); }
            return o;
        }

        static string Generic(Dictionary<string, object> names, string obj)
        {
            string best = null;
            string lower = obj.ToLowerInvariant();
            if (names != null)
                foreach (var kv in names)
                    if (lower.StartsWith(kv.Key) && (best == null || kv.Key.Length > best.Length)) best = kv.Key;
            return best != null ? (string)names[best] : obj;
        }

        public Spot SpotById(string id)
        {
            foreach (var s in Spots) if (s.Id == id) return s;
            return null;
        }

        void StandOf(out Vector3 pos, out float yaw) { StandOf(Location, out pos, out yaw); }

        // her leak (a mood's leaked): the pee's sound (the toilet peeping's, khse_16) at her for its seconds; out of a play and an
        // outing her motion too - the original has no leak motion: its standing aibu's crotch idle (aibu_00 A_Idle), then idle
        // again (the user's note of 2026-11-04)
        public bool Leaking;

        public System.Collections.IEnumerator Leak(float seconds)
        {
            var h = MuseStudioPlugin.Instance;
            Transform at = null;
            foreach (var t in Cha.objBodyBone.GetComponentsInChildren<Transform>(true)) if (t.name == "cf_n_pee" || (at == null && t.name == "cf_j_kokan")) at = t;
            var snd = Sounds.Play(Manager.Sound.Type.GameSE3D, "sound/data/se/h/00/00_00.unity3d", "khse_16", at != null ? at.position : (Vector3?)null);
            if (snd != null && at != null) snd.SetParent(at, true);
            bool moved = !h.Play.Active && !h.Outing.Out && !h.Match.Active;
            var row = h.Play.Row("aibu_00");
            var fem = Json.Arr(row, "female");
            int no = Convert.ToInt32(Json.Num(Json.Obj(row, "female_states"), "A_Idle", 4));
            if (moved && fem != null && fem.Count >= 2) Heroine.LoadAnime(Convert.ToInt32(fem[0]), Convert.ToInt32(fem[1]), no, 0f);
            Leaking = true;
            yield return new WaitForSeconds(seconds);
            Leaking = false;
            if (snd != null) UnityEngine.Object.Destroy(snd.gameObject);
            yield return new WaitForSeconds(1.0f);
            if (moved && !h.Play.Active && !h.Outing.Out && !h.Match.Active) ApplyScene(Json.Str(cfg, "idle_scene", "idle"), 1, 1f);
        }

        // her place in a room that is not the stage's (she stays there / goes there on her own)
        public void PlaceIn(OCIChar oci, string room)
        {
            Vector3 pos;
            float yaw;
            StandOf(room, out pos, out yaw);
            PlaceAt(oci, pos, Quaternion.Euler(0f, yaw, 0f));
        }

        void StandOf(string room, out Vector3 pos, out float yaw)
        {
            var spot = Json.Arr(Json.Obj(cfg, "spots"), room) ?? Json.Arr(Json.Obj(Json.Obj(locations, "locations"), room), "stand");
            pos = Vector3.zero;
            yaw = 0f;
            if (spot != null && spot.Count >= 3)
            {
                pos = new Vector3(Convert.ToSingle(spot[0]), Convert.ToSingle(spot[1]), Convert.ToSingle(spot[2]));
                if (spot.Count >= 4) yaw = Convert.ToSingle(spot[3]);
            }
            pos += MuseStudioPlugin.Instance.Rooms.OffsetOf(room);
            // (where she stands, as the user adjusted it: SpotEditor's "here" - a play there and her place outside one)
            var here = new Spot { Id = "here", Pos = pos, Rot = Quaternion.Euler(0f, yaw, 0f) };
            if (MuseStudioPlugin.Instance.SpotEditor != null) MuseStudioPlugin.Instance.SpotEditor.Apply(room, here);
            pos = here.Pos;
            yaw = here.Rot.eulerAngles.y;
        }

        public Dictionary<string, object> LocationInfo(string key)
        {
            var row = Json.Obj(Json.Obj(locations, "locations"), key);
            var places = MuseStudioPlugin.Instance.Places;
            return new Dictionary<string, object> { { "key", key }, { "id", places != null ? places.IdOf(key) : key }, { "name", row != null ? Json.Str(row, "name", key) : key },
                { "summary", Json.Str(row, "summary", "") }, { "tags", Json.Arr(row, "tags") ?? new List<object>() } };
        }

        // the extra rooms past the setting's number let go - never the room shown, hers, her outing's (PR #46 §4.1)
        public void TrimRooms()
        {
            var h = MuseStudioPlugin.Instance;
            var keep = new HashSet<string>();
            if (!string.IsNullOrEmpty(Location)) keep.Add(Location);
            if (!string.IsNullOrEmpty(h.Presence.HerRoom)) keep.Add(h.Presence.HerRoom);
            if (h.Outing.Out && !string.IsNullOrEmpty(h.Outing.Room)) keep.Add(h.Outing.Room);
            h.Rooms.Trim(h.ExtraRooms != null ? h.ExtraRooms.Value : 6, keep);
        }

        // a room loaded, without showing it (she goes there on her own, her outing's): PR #46 §4.1
        public IEnumerator EnsureRoom(string key)
        {
            var rooms = MuseStudioPlugin.Instance.Rooms;
            if (rooms.Has(key)) { rooms.Touch(key); yield break; }
            var row = Json.Obj(Json.Obj(locations, "locations"), key);
            if (row == null) yield break;
            yield return rooms.LoadLazy(key, row);
            rooms.Touch(key);
        }

        // every room one can go to (PR #46 §4.1: all registered): the kept rooms first (the home - the setting's order: the
        // user's note of 2026-11-07, not lost among many), then the other curated ones, the original's maps, the mods'
        public List<string> AllLocationKeys()
        {
            var o = new List<string>();
            var places = MuseStudioPlugin.Instance.Places;
            if (places == null) return o;
            foreach (var k in LocationKeys()) if (places.ByKey(k) != null && !o.Contains(k)) o.Add(k);
            var all = places.All;
            foreach (var e in all) if (e.Curated && !o.Contains(e.Key)) o.Add(e.Key);
            var orig = all.FindAll(e => !e.Curated && e.Id.StartsWith("orig:"));
            orig.Sort((a, c) => a.MapNo.CompareTo(c.MapNo));
            foreach (var e in orig) o.Add(e.Key);
            var mods = all.FindAll(e => !e.Curated && !e.Id.StartsWith("orig:"));
            mods.Sort((a, c) => string.Compare(a.Name, c.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var e in mods) o.Add(e.Key);
            return o;
        }

        // the usual places (いつもの部屋): the rooms kept in the scene (the setting Rooms), in its order (none kept: the curated
        // ones). Every other registered room is a place too (AllLocationKeys), loaded when one goes there (PR #46 §4.1).
        public IEnumerable<string> LocationKeys()
        {
            var d = Json.Obj(locations, "locations");
            if (d == null) yield break;
            var rooms = MuseStudioPlugin.Instance.Rooms;
            bool any = false;
            foreach (var k in rooms.Keys) if (d.ContainsKey(k)) { any = true; yield return k; }
            if (!any) foreach (var k in d.Keys) if (!Json.Has(Json.Obj(d, k), "auto")) yield return k;
        }

        // a place one can go: a registered room, named by its key or its id
        public bool Reachable(string key)
        {
            var places = MuseStudioPlugin.Instance.Places;
            return places != null && places.KeyOf(key) != null;
        }

        // Where the characters stand on this map: stage.json spots (location -> [x, y, z, yaw]), else the location's stand
        // (locations.json), else the origin.
        public void Place(OCIChar oci)
        {
            Vector3 pos;
            float yaw;
            StandOf(out pos, out yaw);
            PlaceAt(oci, pos, Quaternion.Euler(0f, yaw, 0f));
        }

        public static void PlaceAt(OCIChar oci, Vector3 pos, Quaternion rot)
        {
            oci.oiCharInfo.changeAmount.pos = pos;
            oci.oiCharInfo.changeAmount.rot = rot.eulerAngles;
        }

        public OCIChar AddPartner(string card, bool male = true)
        {
            if (Partner != null && (Partner.charInfo.sex == 0) != male) RemovePartner();
            if (Partner == null)
            {
                Partner = AddChara(male, card);
                if (Partner != null) TakeCardFace(Partner.charInfo, card);       // (its own face for card - GPT's post-review of #63)
            }
            return Partner;
        }

        // the character's futanari attribute (her card's, or stage.json's character.attributes)
        public bool Futanari { get { return MuseStudioPlugin.Instance.Chars.Futanari; } }

        // after another card on the same character (Chars.Switch): her scene, face and gaze again, the bones looked up anew
        public void Rebuilt()
        {
            spine = null;
            string key = SceneKey.Length > 0 ? SceneKey : Json.Str(cfg, "idle_scene", "idle");
            SceneKey = "";
            ApplyScene(key, Intensity, Speed);
            if (!ApplyFace(Emotion)) ApplyFace(Json.Str(cfg, "start_face", "service_smile"));
            Heroine.ChangeLookEyesPtn(1);
            Heroine.ChangeLookNeckPtn(3);      // (the original's talk)
        }

        // A character from a card, or the default body when there is none (Studio's Add(path) needs a file: the same steps
        // without it, through its private Add).
        static OCIChar AddChara(bool male, string card)
        {
            if (!string.IsNullOrEmpty(card) && File.Exists(card)) return male ? (OCIChar)AddObjectMale.Add(card) : AddObjectFemale.Add(card);
            var space = Singleton<Manager.Scene>.Instance.commonSpace;
            var cha = male ? Singleton<Manager.Character>.Instance.CreateMale(0, space, -1, null, true)
                           : Singleton<Manager.Character>.Instance.CreateFemale(space, -1, null, true);
            var info = new OICharInfo(cha.chaFile, global::Studio.Studio.GetNewIndex());
            var add = HarmonyLib.AccessTools.Method(male ? typeof(AddObjectMale) : typeof(AddObjectFemale), "Add",
                new[] { typeof(ChaControl), typeof(OICharInfo), typeof(ObjectCtrlInfo), typeof(TreeNodeObject), typeof(bool), typeof(int) });
            return (OCIChar)add.Invoke(null, new object[] { cha, info, null, null, true, global::Studio.Studio.optionSystem.initialPosition });
        }

        public void RemovePartner()
        {
            if (Partner == null) return;
            OpponentFaceReset(false);
            if (Partner.charInfo != null) cardFaces.Remove(Partner.charInfo);
            Singleton<Studio.Studio>.Instance.treeNodeCtrl.DeleteNode(Partner.treeNodeObject);
            Partner = null;
        }

        // A scene key (MUSE_GUIDE.md 3.2): "{key}#{intensity}" wins over the plain key; the intensity is otherwise a speed. A
        // key with "move": "approach" walks up to the camera first (Walk), its own pose on arrival.
        public bool ApplyScene(string key, int intensity, float speed)
        {
            return ApplyScene(key, intensity, speed, true);
        }

        public bool ApplyScene(string key, int intensity, float speed, bool allowMove)
        {
            var sc = Json.Obj(scenes, "scenes");
            var row = Json.Obj(sc, key + "#" + intensity);
            bool exact = row != null;
            if (row == null) row = Json.Obj(sc, key);
            if (row == null) return false;
            if (climaxRun != null)
            {
                // (her climax in the talk: the scene after it)
                SceneKey = key;
                Intensity = intensity;
                Speed = speed;
                return true;
            }
            ReactUntil = -1f;              // a scene ends a touch's reaction pose
            if (Walking) StopWalk(false);  // a new scene stops the walk where she is
            if (allowMove && Json.Str(row, "move", "") == "approach" && StartWalk(key, intensity, speed)) return true;
            var a = Json.Arr(row, "anime");
            float sp = speed;
            if (!exact) sp *= (float)Json.Num(Json.Obj(scenes, "intensity_speed"), intensity.ToString(), 1.0);
            var ai = Heroine.oiCharInfo.animeInfo;
            bool same = ai.group == Convert.ToInt32(a[0]) && ai.category == Convert.ToInt32(a[1]) && ai.no == Convert.ToInt32(a[2]);
            if (!same)
            {
                // another motion: a new key fades in 0.5 s, another intensity of the same key 0.4 s from the same phase
                Blend.Next(Cha, key == SceneKey && SceneKey.Length > 0 ? 0.4f : 0.5f, key == SceneKey && SceneKey.Length > 0);
                Heroine.LoadAnime(Convert.ToInt32(a[0]), Convert.ToInt32(a[1]), Convert.ToInt32(a[2]), 0f);
                Heroine.animeSpeed = sp;
                speedTarget = sp;
            }
            else SpeedTo(sp);             // the same motion at another speed: it changes over 0.6 s
            SceneKey = key;
            Intensity = intensity;
            Speed = speed;
            return true;
        }

        // An emotion label (MUSE_GUIDE.md 3.1) -> the original's face parameters.
        // another body's face by a label (the match: the opponent's)
        public bool ApplyFaceOn(OCIChar who, string label)
        {
            return who != null && ApplyCells(CellsOf(label, who), who);
        }

        // The opponent's face in a match, held and put back by the rules its own side follows for it (the user's choice of
        // 2026-11-08, plan B: on the opponent's own app that body is "her", her face's rules hers; before, its face stayed as
        // its last block left it, and its declared climax's face went to neutral where its own side puts the face before it
        // back). All it takes is here already: its Muse's labels come in its blocks, the default and the seconds are the same
        // faces.json on both sides - no face sent between them. A label of its block is held (contract 1.27: till its next one,
        // the match's end, or the safety time - this user's setting, so the two sides' may differ); a label of a moment
        // (return.transient: ecstasy) shows for its seconds and the held one comes back; its declared climax's face goes back
        // to the held one; none held: the match's default (return.defaults.match, neutral) - which it also takes as the match
        // begins, as its own side's scene changes into the match.
        string oppHeld = "";
        float oppHeldSince = -1f, oppTempUntil = -1f;
        public string OpponentHeld { get { return oppHeld; } }

        string MatchDefault { get { return Json.Str(Json.Obj(FaceReturn, "defaults"), "match", "neutral"); } }

        // its declared climax's face (Match): a face of a moment like the labels' - on the same timer, so a newer face wins and
        // an older end never cuts a newer one short (GPT's post-review of #92)
        public void OpponentClimaxFace(float seconds)
        {
            if (Partner == null || !ApplyPresetOn(Partner, "ecstasy")) return;
            oppTempUntil = Time.realtimeSinceStartup + seconds;
        }

        public void OpponentFace(string label)
        {
            if (Partner == null) return;
            if (!ApplyFaceOn(Partner, label))
            {
                if (!ApplyFaceOn(Partner, "neutral")) return;     // (an unknown label: the standard face - MUSE_GUIDE 4)
                label = "neutral";
            }
            if (Transient(label)) { oppTempUntil = Time.realtimeSinceStartup + FaceSeconds(label); return; }
            oppHeld = label;
            oppHeldSince = Time.realtimeSinceStartup;
            oppTempUntil = -1f;
        }

        // its face back: the held one, else the match's default
        public void OpponentFaceBack()
        {
            oppTempUntil = -1f;
            if (Partner == null) return;
            if (oppHeld.Length == 0 || !ApplyFaceOn(Partner, oppHeld)) ApplyFaceOn(Partner, MatchDefault);
        }

        // a match's start (and a replay's): nothing held, the match's default on it
        public void OpponentFaceReset(bool show)
        {
            oppHeld = "";
            oppHeldSince = oppTempUntil = -1f;
            if (show && Partner != null) ApplyFaceOn(Partner, MatchDefault);
        }

        void OpponentFaceTick()
        {
            if (Partner == null || !MuseStudioPlugin.Instance.Match.Active) return;
            float now = Time.realtimeSinceStartup;
            if (oppTempUntil >= 0f && now >= oppTempUntil) OpponentFaceBack();
            if (oppHeld.Length > 0 && now - oppHeldSince >= MuseHoldSeconds)
            {
                // (the hold over: what it comes back to is the default now; a face of a moment showing stays its time)
                oppHeld = "";
                if (oppTempUntil < 0f) OpponentFaceBack();
            }
        }

        // Her card's own face (the owner's choice of 2026-11-07: the face her card was saved with is her ordinary face - the
        // start face, the talk's default, the package's end face; faces.json's labels stay Muse's emotions): the label "card".
        // Taken from her status as she is loaded (and when the user switches characters), before any face is put on: the
        // eyebrow / eyes / mouth patterns and their openings, the cheek, the highlight, the tears, the blink (her gaze stays
        // the look's). Each body its own (GPT's post-review of #63: the opponent's card was the standard face): hers, and the
        // opponent's taken as its body is made from its card (a match) - card on a body is that body's own face.
        readonly Dictionary<ChaControl, List<object>> cardFaces = new Dictionary<ChaControl, List<object>>();
        public List<object> CardCells { get { return CardCellsOf(Cha); } }

        public List<object> CardCellsOf(ChaControl c) { List<object> l; return c != null && cardFaces.TryGetValue(c, out l) ? l : null; }

        // (read from the card itself, with its status: Studio's ChangeChara loads a card without it - LoadCharaFile(path, 255,
        // true, true) - and the body keeps the face it had, so the body's status after a switch or a replay's swap was the face
        // shown then, the climax's among them: her eyes shut on as her "card" face - the user's sighting of 2026-11-08. No card
        // file: the body's own status, as it was made)
        public string CardFaceFrom = "";      // (checks) where the last card face came from: "card" / "body"

        public void TakeCardFace(string card) { TakeCardFace(Cha, card); }

        public void TakeCardFace(ChaControl c, string card)
        {
            if (c == null) return;
            ChaFileStatus s = null;
            if (!string.IsNullOrEmpty(card) && File.Exists(card))
            {
                try
                {
                    var cf = new ChaFileControl();
                    if (cf.LoadCharaFile(card, 255, true, false)) s = cf.status;
                }
                catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: the card's face: " + e.Message); }
            }
            CardFaceFrom = s != null ? "card" : "body";
            if (s == null) s = c.fileStatus;
            if (s == null) return;
            // (the mouth's open max 1, as every face of faces.json: it is the voice's reach - her mouth at rest is Studio's own
            // 口の開き, fixed; a card saved with it 0 would keep her lips still as she speaks)
            cardFaces[c] = new List<object> { s.eyebrowPtn, s.eyesPtn, s.mouthPtn, s.eyebrowOpenMax, s.eyesOpenMax, 1f, -1,
                                              s.hohoAkaRate, !s.hideEyesHighlight, (int)s.tearsLv, s.eyesBlink };
        }

        internal void SetCardFaceForCheck(ChaControl c, List<object> cells) { if (c != null) cardFaces[c] = cells; }      // (checks)

        // a label's cells: faces.json's, the body's own card face for "card" (hers when none named; none taken: the standard
        // face's)
        List<object> CellsOf(string label, OCIChar who = null)
        {
            if (label == "card")
            {
                var own = CardCellsOf(who != null ? who.charInfo : Cha);
                if (own != null) return own;
            }
            return Json.Arr(Json.Obj(Json.Obj(faces, "labels"), label == "card" ? "neutral" : label), "cells");
        }

        public bool ApplyPresetOn(OCIChar who, string name)
        {
            return who != null && ApplyCells(Json.Arr(Json.Obj(faces, "presets"), name), who);
        }

        public Dictionary<string, object> Locations { get { return locations; } }
        public Dictionary<string, object> Cameras { get { return cameras; } }

        public bool ApplyFace(string label) { return ApplyFace(label, false); }

        bool ApplyFace(string label, bool restoring)
        {
            // (put back after a reaction or her climax when its own time is over: the scene's default, not a flash of it)
            if (restoring && tempLabel == label && Time.realtimeSinceStartup >= tempUntil) return ApplyFace(FaceBack(), false);
            if (!ApplyCells(CellsOf(label))) return false;
            Emotion = label;
            Face = label;
            faceSerial++;
            if (!restoring) faceAppliedAt = Time.realtimeSinceStartup;
            if (!(restoring && tempLabel == label))
            {
                tempLabel = label;
                tempUntil = Time.realtimeSinceStartup + FaceSeconds(label);
            }
            if (label == MuseLabel) tempLabel = "";       // (Muse's face held: no timer of its own)
            tempSerial = faceSerial;
            return true;
        }

        // Every face goes back by itself (faces.json "return"; GPT's handoff of Alpha PR #1 and PR #5 adopted - the trial runs
        // 5 and 7: her climax face stayed, eyes shut, till the next package; then the user saw any last face stay on, and asked
        // for each scene's default face to come back after its seconds): a label's seconds after it was put on (seconds per
        // label, else default_seconds - the stronger the face, the sooner), the face becomes the default of the scene she is in
        // then (defaults: talk / play - by her gauge - / afterglow / outing / match); the default itself stays. Another face
        // meanwhile - a newer label, a touch's or her climax's face - wins: an old timer never puts its face over it. Put back
        // after those (RestoreFace), a label keeps its own end. Muse is not told (the state's emotion shows the face now).
        string tempLabel = "";
        float tempUntil = -1f;
        int faceSerial, tempSerial;

        Dictionary<string, object> FaceReturn { get { return Json.Obj(faces, "return"); } }

        // ---- Muse's face held (PR #46 §2: the owner's decision of 2026-11-07, way A; handed over in PR #47) ----
        // A face Muse gives (a block's emotion) is held: the scene's default - her gauge's in a play - does not take it over, its
        // seconds do not end it, and the app's faces of a moment (a touch's reaction, her climax in the talk, a face with nothing
        // to put it back) come back to it. It ends at Muse's next face, at a change of scene (the afterglow is one, as before:
        // its own face then), at another character, or after the safety time (the setting, 300 s by default) from when it began
        // to show; then what was kept to come back to it goes to the scene's default instead. A label of a moment (faces.json
        // return.transient: ecstasy) is not held: after its seconds it goes back to the face held under it. The app's own
        // faces (ApplyFace from the app) keep their timers as before - the fallback when Muse holds nothing.
        public string MuseLabel = "";        // the face held ("": none)
        float museSince = -1f;
        public float MuseHoldSeconds        // (the setting "表情を保つ安全時間", 300 s by default)
        {
            get { var e = MuseStudioPlugin.Instance != null ? MuseStudioPlugin.Instance.FaceHoldSeconds : null; return e != null ? e.Value : 300f; }
        }
        public int MuseReleases;              // (checks)
        public string MuseReleasedBy = "";    // (checks) why the last hold ended: next / scene / character / safety

        public bool Transient(string label)
        {
            var t = Json.Arr(FaceReturn, "transient");
            return t != null && label != null && t.Contains(label);
        }

        // Muse's face (a block's emotion): shown, and held unless it is a label of a moment
        public bool MuseFace(string label)
        {
            if (!ApplyFace(label))
            {
                if (!ApplyFace("neutral")) return false;      // (an unknown label: the standard face - MUSE_GUIDE 4)
                label = "neutral";
            }
            if (Transient(label)) return true;                // (its seconds as ever; then the face held under it)
            if (MuseLabel.Length > 0 && MuseLabel != label) { MuseReleases++; MuseReleasedBy = "next"; }
            MuseLabel = label;
            museSince = Time.realtimeSinceStartup;
            tempLabel = "";
            return true;
        }

        // what a face of the app's comes back to: the face Muse holds, else the scene's default
        public string FaceBack() { return MuseLabel.Length > 0 ? MuseLabel : SceneDefault(); }

        // the hold ends: what was kept to come back to it goes to the scene's default; shown, it changes to that now
        public void ReleaseMuse(string why, bool show = true)
        {
            if (MuseLabel.Length == 0) return;
            string was = MuseLabel, to = SceneDefault();
            MuseLabel = "";
            museSince = -1f;
            MuseReleases++;
            MuseReleasedBy = why;
            var host = MuseStudioPlugin.Instance;
            if (host.Touch.RestoreFace == was) host.Touch.RestoreFace = to;
            if (climaxFaceBack == was) climaxFaceBack = to;
            host.HeldFaceReleased(was, to);
            if (!show || Face != was || Heroine == null) return;
            if (!ApplyFace(to, false)) ApplyFace("neutral", false);
            if (Face == to) tempLabel = "";
            faceAppliedAt = -9f;
            SceneGaze();
        }

        public float MuseLeft { get { return MuseLabel.Length > 0 ? Mathf.Max(0f, MuseHoldSeconds - (Time.realtimeSinceStartup - museSince)) : -1f; } }
        internal void AgeMuseForCheck(float seconds) { if (MuseLabel.Length > 0) museSince -= seconds; }

        void MuseHoldTick()
        {
            if (MuseLabel.Length > 0 && Time.realtimeSinceStartup - museSince >= MuseHoldSeconds) ReleaseMuse("safety");     // (museSince is a time while MuseLabel is held, whatever its sign)
        }

        // the state's face (MUSE_GUIDE 7.4): the face Muse holds and its time left, the face shown, and a face of a moment over
        // it (what it is and what ends it)
        public Dictionary<string, object> FaceInfo()
        {
            var host = MuseStudioPlugin.Instance;
            object temp = null;
            if (AppFace && host.Gauge.Phase != "afterglow")
            {
                string kind = Climaxing || host.ClimaxFaceShown ? "climax" : host.Touch.RestoreFace.Length > 0 || ReactUntil >= 0f ? "touch" : "app";
                temp = new Dictionary<string, object> { { "kind", kind }, { "ends", kind == "climax" ? "climax" : kind == "touch" ? "reaction" : "seconds" } };
            }
            else if (MuseLabel.Length > 0 && Face != MuseLabel && tempLabel.Length > 0 && Face == tempLabel && Transient(Face))
                temp = new Dictionary<string, object> { { "kind", "short_label" }, { "ends", "seconds" },
                    { "left", Math.Round(Mathf.Max(0f, tempUntil - Time.realtimeSinceStartup), 1) } };
            return new Dictionary<string, object> { { "muse", MuseLabel.Length > 0 ? (object)MuseLabel : null },
                { "muse_left", MuseLabel.Length > 0 ? (object)Math.Round(MuseLeft, 1) : null }, { "shown", Face }, { "temporary", temp } };
        }

        float FaceSeconds(string label)
        {
            var r = FaceReturn;
            var per = Json.Obj(r, "seconds");
            return (float)(per != null && per.ContainsKey(label) ? Json.Num(per, label) : Json.Num(r, "default_seconds", 20.0));
        }

        // the scene she is in now, and its default face
        public string FaceScene
        {
            get
            {
                var h = MuseStudioPlugin.Instance;
                if (h.Match.Active) return "match";
                if (h.Outing.Out) return "outing";
                if (h.Gauge.Phase == "afterglow") return "afterglow";
                return h.Play.Active ? "play" : "talk";
            }
        }

        public string DefaultFaceForCheck { get { return SceneDefault(); } }     // (checks)

        public string SceneDefault()
        {
            var d = Json.Obj(FaceReturn, "defaults");
            object v;
            if (d == null || !d.TryGetValue(FaceScene, out v) || v == null) return Json.Str(cfg, "package_end_face", "service_smile");
            if (v is string) return (string)v;
            // [[from gauge, label], ...]: the last whose from her gauge has reached
            string label = Json.Str(cfg, "package_end_face", "service_smile");
            var rows = v as List<object>;
            if (rows != null)
                foreach (var row in rows)
                {
                    var rr = row as List<object>;
                    if (rr != null && rr.Count >= 2 && MuseStudioPlugin.Instance.Gauge.Value >= Convert.ToSingle(rr[0])) label = Convert.ToString(rr[1]);
                }
            return label;
        }

        public float TemporaryLeft { get { return tempLabel.Length > 0 ? tempUntil - Time.realtimeSinceStartup : -1f; } }      // (checks)

        internal void EndTemporaryForCheck() { if (tempLabel.Length > 0) tempUntil = Time.realtimeSinceStartup; }

        // A face is not carried over into another scene (PR #9 adopted: the timers alone kept the last face too long): when the
        // scene changes - the talk into a play and back, an outing, a match - the face becomes the new scene's default; within a
        // scene it stays (the timers only help). A face given just before the change (boundary_grace_seconds: a block that sets
        // her face and starts the play) is the new scene's own and stays; her climax's faces and the afterglow keep their own
        // course (a change to or from the afterglow, a touch's or her climax's face on her: left alone).
        string faceSceneWas = "";
        float faceAppliedAt = -9f;

        void SceneFaceTick()
        {
            string now = FaceScene;
            if (faceSceneWas.Length == 0) { faceSceneWas = now; return; }
            if (now == faceSceneWas) return;
            string was = faceSceneWas;
            faceSceneWas = now;
            SceneGaze();                                                    // (the look of the scene before: not carried over)
            bool givenNow = Time.realtimeSinceStartup - faceAppliedAt < (float)Json.Num(FaceReturn, "boundary_grace_seconds", 1.0);
            if (!(givenNow && MuseLabel.Length > 0 && Face == MuseLabel)) ReleaseMuse("scene", false);
            if (now == "afterglow" || Heroine == null) return;              // (her climax's own faces)
            bool appFace = AppFace;
            if (appFace && AppFacePending) return;                          // (a reaction's / her climax's face that puts its own back)
            if (was == "afterglow" && !appFace) return;                     // (the face put back after her climax: kept)
            if (!appFace && Time.realtimeSinceStartup - faceAppliedAt < (float)Json.Num(FaceReturn, "boundary_grace_seconds", 1.0)) return;
            string to = SceneDefault();
            if (Face == to) return;
            ApplyFace(to, false);
            if (Face == to) tempLabel = "";                                 // (a default stays: no timer of its own)
            faceAppliedAt = -9f;                                            // (the app's own default: not a face given for the next scene)
            SceneResets++;
        }

        public int SceneResets;                                             // (checks)

        // In a play her default face follows her gauge as it rises (faces.json return.defaults.play: [[from, label], ...]), not
        // only as the scene changes (the user's note of 2026-11-08: flushed through the 40 s to her climax, past 0.5's
        // aroused - the default was chosen only at the play's start): when nothing holds her face (Muse's hold none, no face of
        // the app's, no face of a moment) and the face shown is one of the play's defaults, the gauge's default once it has
        // stood for gauge_face_seconds (a gauge about a step: no flicker)
        float playDefaultSince = -1f;
        string playDefaultWant = "";
        public int GaugeFaceSwitches;                                       // (checks)

        void PlayDefaultTick()
        {
            if (Heroine == null || FaceScene != "play" || MuseLabel.Length > 0 || AppFace || tempLabel.Length > 0) { playDefaultSince = -1f; return; }
            var rows = Json.Arr(Json.Obj(FaceReturn, "defaults"), "play");
            bool isDefault = false;
            if (rows != null)
                foreach (var row in rows) { var rr = row as List<object>; if (rr != null && rr.Count >= 2 && Convert.ToString(rr[1]) == Face) isDefault = true; }
            string want = SceneDefault();
            if (!isDefault || want == Face) { playDefaultSince = -1f; return; }
            float now = Time.realtimeSinceStartup;
            if (want != playDefaultWant || playDefaultSince < 0f) { playDefaultWant = want; playDefaultSince = now; return; }
            if (now - playDefaultSince < (float)Json.Num(FaceReturn, "gauge_face_seconds", 1.0)) return;
            playDefaultSince = -1f;
            if (!ApplyFace(want, false)) return;
            tempLabel = "";                                                 // (a default stays: no timer of its own)
            faceAppliedAt = -9f;
            GaugeFaceSwitches++;
        }

        internal void DefaultFaceNowForCheck() { if (ApplyFace(SceneDefault(), false)) { tempLabel = ""; faceAppliedAt = -9f; } }    // (checks)

        // The app's own faces - a touch's reaction (a preset in a play, the original talk's expression outside), her climax's and
        // the afterglow's - put their face back themselves; when nothing will (the afterglow ended while a package was showing -
        // in a play nearly always, its last line stays; a reaction over a reaction kept the first one's face as the one to put
        // back) the face, her eyes' opening, blinking and look stayed on (the user's note, PR #9: "her eyes' opening too"). Left
        // with nothing to put it back for app_face_seconds, it becomes the scene's default (eyes, blinking and look with it).
        public bool AppFace { get { return Face.StartsWith("preset:") || Face.StartsWith("expr:"); } }

        public bool AppFacePending
        {
            get
            {
                var host = MuseStudioPlugin.Instance;
                return host.Touch.RestoreFace.Length > 0 || Climaxing || ReactUntil >= 0f || host.Gauge.Phase == "afterglow";
            }
        }

        float appFaceSince = -1f;
        public int AppFaceReturns;                                          // (checks)

        internal void AgeAppFaceForCheck(float seconds) { if (appFaceSince >= 0f) appFaceSince -= seconds; }

        void AppFaceTick()
        {
            if (Heroine == null || !AppFace) { appFaceSince = -1f; return; }
            float now = Time.realtimeSinceStartup;
            if (AppFacePending) { appFaceSince = now; return; }
            if (appFaceSince < 0f) { appFaceSince = now; return; }
            if (now - appFaceSince < (float)Json.Num(FaceReturn, "app_face_seconds", 3.0)) return;
            appFaceSince = -1f;
            string to = FaceBack();
            if (!ApplyFace(to, false)) ApplyFace("neutral", false);
            if (Face == to) tempLabel = "";
            faceAppliedAt = -9f;
            SceneGaze();
            AppFaceReturns++;
        }

        void TempFaceTick()
        {
            AppFaceTick();
            if (tempLabel.Length == 0 || Heroine == null || Time.realtimeSinceStartup < tempUntil) return;
            if (tempSerial != faceSerial || Face != tempLabel) return;       // (another face on it now: when that one is put back)
            string was = tempLabel, to = FaceBack();
            tempLabel = "";
            if (to == was) return;                                          // (the scene's default already: it stays)
            if (!ApplyFace(to, false)) ApplyFace("neutral", false);
            if (Face == to) tempLabel = "";                                 // (a default stays: no timer of its own)
            faceAppliedAt = -9f;
            SceneGaze();
        }

        // Her look as the scene's own when its face comes back - a scene's change, a face's time over, a reaction's or her
        // climax's face put back (the user's note, PR #9: her look, like her face, was carried over - a touch's reaction looks
        // away or ahead, and the look stayed after it): in the talk her eyes at the user again (pattern 1 at the camera; the
        // neck is Look's). In a play the position's (HEyeNeck); out on her own and in a match their own.
        public int GazeResets;                                              // (checks)

        public void SceneGaze()
        {
            var host = MuseStudioPlugin.Instance;
            if (Heroine == null || Cha == null || Camera.main == null || host.Play.Active || host.Outing.Out || host.Match.Active || ReactUntil >= 0f || Climaxing) return;
            if (Cha.eyeLookCtrl != null && Cha.eyeLookCtrl.target != Camera.main.transform) Cha.eyeLookCtrl.target = Camera.main.transform;
            if (Cha.fileStatus.eyesLookPtn != 1) { Heroine.ChangeLookEyesPtn(1); GazeResets++; }
        }

        // A face preset by name (a touch's face in a play, the climax, the afterglow); the emotion stays Muse's label.
        public bool ApplyPreset(string name)
        {
            if (!ApplyCells(Json.Arr(Json.Obj(faces, "presets"), name))) return false;
            Face = "preset:" + name;
            faceSerial++;
            return true;
        }

        // The face shown now: an emotion label, "preset:<name>" or "expr:<name>" (a touch reaction's expression). Each change
        // written in the log with what changed it (the user's note of 2026-11-07: a face seen staying - matched in the log)
        string face = "";
        public string Face
        {
            get { return face; }
            set
            {
                if (value == face) return;
                MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: face {0} -> {1} ({2}) at {3:F1} s", face, value, Callers(), Time.realtimeSinceStartup));
                face = value;
            }
        }

        static string Callers()
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(2, false);
                var parts = new List<string>();
                for (int i = 0; i < st.FrameCount && parts.Count < 3; i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    if (m != null) parts.Add((m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name);
                }
                return string.Join(" < ", parts.ToArray());
            }
            catch (Exception) { return "?"; }
        }

        // Puts back a face Face named.
        public void RestoreFace(string face)
        {
            if (string.IsNullOrEmpty(face)) return;
            if (face.StartsWith("preset:")) ApplyPreset(face.Substring(7));
            else if (!face.StartsWith("expr:")) { ApplyFace(face, true); SceneGaze(); }     // (a reaction's look not kept either)
        }

        // The original's face parameters (the expression table's columns: eyebrow / eyes / mouth pattern, their open max,
        // eyes_look, cheek, highlight, tears, blink); an empty cell keeps that part.
        public bool ApplyCells(List<object> c, OCIChar who = null)
        {
            var oci = who ?? Heroine;
            if (c == null || c.Count < 11 || oci == null) return false;
            var cha = oci.charInfo;
            if (oci == Heroine)
                MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: face cells eyebrow {0} eyes {1} mouth {2} ({3}) at {4:F1} s", c[0], c[1], c[2], Callers(), Time.realtimeSinceStartup));
            if (Set(c[0])) cha.ChangeEyebrowPtn(Int(c[0]), true);
            if (Set(c[1])) cha.ChangeEyesPtn(Int(c[1]), true);
            if (Set(c[2])) cha.ChangeMouthPtn(Int(c[2]), true);
            if (Set(c[3])) cha.ChangeEyebrowOpenMax(Float(c[3]));
            if (Set(c[4]))
            {
                if (oci == Heroine) { eyesCell = Float(c[4]); eyesShownFactor = DrunkEyes; cha.ChangeEyesOpenMax(eyesCell * eyesShownFactor); }     // (hers: her drink's - PR #46 §7)
                else cha.ChangeEyesOpenMax(Float(c[4]));
            }
            if (Set(c[5])) cha.ChangeMouthOpenMax(Float(c[5]));
            var pl = MuseStudioPlugin.Instance.Play;
            if (Set(c[6]) && Int(c[6]) >= 0 && !(pl.Active && pl.Motion)) oci.ChangeLookEyesPtn(Int(c[6]));      // (a play's gaze is the position's: HEyeNeck)
            if (Set(c[7]))
            {
                if (oci == Heroine) { ExprBlush = Float(c[7]); blushShown = -1f; }      // (hers: with her body's - Blush)
                else cha.ChangeHohoAkaRate(Float(c[7]));
            }
            if (Set(c[8])) cha.HideEyeHighlight(!Bool(c[8]));
            if (Set(c[9])) cha.tearsLv = (byte)Mathf.Clamp(Int(c[9]), 0, 3);
            if (Set(c[10])) cha.ChangeEyesBlinkFlag(Bool(c[10]));
            return true;
        }

        static bool Set(object v) { return v != null && !(v is string && ((string)v).Length == 0); }

        // A short pose (a touch reaction outside a play: the original's ADV pose as Studio's animation), then the scene again.
        // a touch's reaction pose: played out, as the original's talk does (TalkScene.TouchEnd: back to the stand when it is
        // over - there the voice's end), seconds at most
        public void React(List<object> anime, float seconds)
        {
            if (anime == null || anime.Count < 3 || Heroine == null || climaxRun != null) return;
            Blend.Next(Cha, 0.5f);
            Heroine.LoadAnime(Convert.ToInt32(anime[0]), Convert.ToInt32(anime[1]), Convert.ToInt32(anime[2]), 0f);
            Heroine.animeSpeed = speedTarget = 1f;
            reactFrom = Time.realtimeSinceStartup;
            ReactUntil = reactFrom + seconds;
        }

        public float ReactUntil = -1f;
        float reactFrom;

        // Her climax in the talk (a block's anim "climax" - Muse's request of 2026-11-05, for a pair's own cues such as a heart
        // the user sends): the standing caress's climax motion whatever she was doing (the user's decision; stage.json
        // talk_climax: aibu_00's Orgasm_Start played through, Orgasm_Loop and Orgasm_A for their seconds) with the climax face
        // unless the block has its own, then her scene again. A scene asked meanwhile waits for its end (as the match's declared
        // climax keeps the pace for after); a play, an outing or a match takes her body from it. Nothing counted, the gauge left
        // alone, nothing told.
        public bool Climaxing { get { return climaxRun != null; } }
        public Action AfterClimax;         // (the package's end meanwhile: its face once the climax is over)
        Coroutine climaxRun;
        string climaxFaceBack = "";

        public bool TalkClimax(bool face)
        {
            var c = Json.Obj(cfg, "talk_climax");
            var row = MuseStudioPlugin.Instance.Play.Row(Json.Str(c, "play", "aibu_00"));
            var fem = Json.Arr(row, "female");
            var states = Json.Obj(row, "female_states");
            var steps = Json.Arr(c, "steps");
            if (Heroine == null || Cha == null || fem == null || fem.Count < 2 || states == null || steps == null) return false;
            if (Walking) StopWalk(false);
            ReactUntil = -1f;
            if (climaxRun != null) MuseStudioPlugin.Instance.StopCoroutine(climaxRun);
            if (face)
            {
                if (!Face.StartsWith("preset:")) climaxFaceBack = Face;
                ApplyPreset(Json.Str(c, "face", "ecstasy"));
            }
            climaxRun = MuseStudioPlugin.Instance.StartCoroutine(TalkClimaxRun(Convert.ToInt32(fem[0]), Convert.ToInt32(fem[1]), states, steps, c));
            return true;
        }

        System.Collections.IEnumerator TalkClimaxRun(int g, int cat, Dictionary<string, object> states, List<object> steps, Dictionary<string, object> c)
        {
            float most = (float)Json.Num(c, "once_max_seconds", 8.0), blend = (float)Json.Num(c, "blend_seconds", 0.3);
            foreach (var o in steps)
            {
                var step = o as List<object>;
                if (step == null || step.Count < 2 || !Json.Has(states, Convert.ToString(step[0]))) continue;
                Blend.Next(Cha, blend);
                Heroine.LoadAnime(g, cat, Convert.ToInt32(states[Convert.ToString(step[0])]), 0f);
                Heroine.animeSpeed = speedTarget = 1f;
                bool once = step[1] is string;
                float t0 = Time.realtimeSinceStartup, until = once ? most : Convert.ToSingle(step[1]);
                while (Time.realtimeSinceStartup - t0 < until)
                {
                    yield return null;
                    if (ClimaxTaken()) { climaxRun = null; climaxFaceBack = ""; AfterClimax = null; yield break; }
                    if (once && Time.realtimeSinceStartup - t0 > 0.3f && Blend.Now(Cha.animBody).normalizedTime >= 0.98f) break;
                }
            }
            climaxRun = null;
            var after = AfterClimax;
            AfterClimax = null;
            if (after != null) after();
            else if (Face == "preset:" + Json.Str(c, "face", "ecstasy")) RestoreFace(climaxFaceBack);
            climaxFaceBack = "";
            string key = SceneKey.Length > 0 ? SceneKey : Json.Str(cfg, "idle_scene", "idle");
            SceneKey = "";
            ApplyScene(key, Intensity, Speed);
        }

        static bool ClimaxTaken()
        {
            var h = MuseStudioPlugin.Instance;
            return h.Play.Active || h.Outing.Out || h.Match.Active || h.Stage.Heroine == null;
        }

        // ---- the speed of the same motion changes smoothly (the Godot app's 0.6 s) ----

        float speedTarget = 1f, speedRate;

        void SpeedTo(float sp)
        {
            speedTarget = sp;
            speedRate = Mathf.Abs(Heroine.animeSpeed - sp) / 0.6f;
        }

        public float SpeedTarget { get { return speedTarget; } }

        // ---- the approach (the Godot app's approach.gd): she walks up to the player (the camera) ----

        public bool Walking;
        Vector3 walkTarget;
        float walkFloor, walkPhase;
        float walkYaw;
        string walkKey = "";
        int walkIntensity = 1;
        float walkSpeed = 1f;
        public readonly List<Dictionary<string, object>> WalkLog = new List<Dictionary<string, object>>();

        Dictionary<string, object> WalkCfg { get { return Json.Obj(scenes, "walking"); } }

        // where the approach walks her when the room has no place of its own: towards the camera, stop_distance short of it (at
        // most max_distance), facing the camera (SpotEditor's marker for an unset approach)
        public void ApproachDefault(out Vector3 pos, out float yaw)
        {
            Vector3 from = Heroine != null ? Heroine.oiCharInfo.changeAmount.pos : Vector3.zero;
            var cam = Camera.main;
            Vector3 c = cam != null ? cam.transform.position : from;
            var toCam = new Vector3(c.x - from.x, 0f, c.z - from.z);
            float dist = toCam.magnitude;
            float len = Mathf.Max(0f, Mathf.Min(dist - (float)Json.Num(WalkCfg, "stop_distance", 1.0), (float)Json.Num(WalkCfg, "max_distance", 3.5)));
            var dir = dist > 1e-4f ? toCam / dist : Vector3.forward;
            pos = from + dir * len;
            yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        }

        float arriveYaw = float.NaN;           // (the room's approach place: her facing there)

        // true when she walks: she turns to the camera and walks there on the floor with the original's walk, stopping
        // stop_distance short of it (at most max_distance) - or, when the user set the room's approach place (SpotEditor: the
        // user's request, PR #23 - her way past the furniture), to that place, facing as it says; already close: no walk. The
        // camera stays.
        bool StartWalk(string key, int intensity, float speed)
        {
            var walk = Json.Arr(scenes, "walk");
            var cam = Camera.main;
            if (walk == null || cam == null || Heroine == null) return false;
            Vector3 from = Heroine.oiCharInfo.changeAmount.pos, c = cam.transform.position;
            Vector3 set = Vector3.zero;
            float setYaw = 0f;
            bool placed = MuseStudioPlugin.Instance.SpotEditor != null && MuseStudioPlugin.Instance.SpotEditor.ApproachIn(Location, out set, out setYaw);
            var toCam = placed ? new Vector3(set.x - from.x, 0f, set.z - from.z) : new Vector3(c.x - from.x, 0f, c.z - from.z);
            float dist = toCam.magnitude;
            float len = placed ? dist : Mathf.Min(dist - (float)Json.Num(WalkCfg, "stop_distance", 1.0), (float)Json.Num(WalkCfg, "max_distance", 3.5));
            if (len < Json.Num(WalkCfg, "min_walk", 0.15))
            {
                WalkLog.Add(new Dictionary<string, object> { { "event", "near" }, { "distance", Math.Round(dist, 2) } });
                if (placed)
                {
                    // (the room's place a step away: she stands on it, facing as it says - on her floor, as a walk would leave her;
                    // GPT's re-review of #30)
                    Heroine.oiCharInfo.changeAmount.pos = new Vector3(set.x, from.y, set.z);
                    Heroine.oiCharInfo.changeAmount.rot = new Vector3(0f, setYaw, 0f);
                }
                return false;
            }
            var dir = toCam / dist;
            walkTarget = from + dir * len;
            arriveYaw = placed ? setYaw : float.NaN;
            walkFloor = from.y;
            walkPhase = 0f;
            walkYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            walkKey = key;
            walkIntensity = intensity;
            walkSpeed = speed;
            walkCamera = null;
            Walking = true;
            SceneKey = key;
            Intensity = intensity;
            Speed = speed;
            Blend.Next(Cha, 0.5f);
            Heroine.LoadAnime(Convert.ToInt32(walk[0]), Convert.ToInt32(walk[1]), Convert.ToInt32(walk[2]), 0f);
            Heroine.animeSpeed = speedTarget = speed;
            WalkLog.Add(new Dictionary<string, object> { { "event", "walk" }, { "distance", Math.Round(len, 2) }, { "to", placed ? "place" : "camera" } });
            return true;
        }

        // stops where she is; pose: the approach's own pose follows (a change of place, a play), else the caller plays one
        public void StopWalk(bool pose)
        {
            if (!Walking) return;
            Walking = false;
            WalkLog.Add(new Dictionary<string, object> { { "event", "stopped" } });
            if (pose) Arrive(false);
        }

        // the camera while she walks up (the Godot app's approach): it stays where it is and keeps her face in view; a camera
        // asked for meanwhile (a block's) waits for her arrival, else the preset nearest to the new distance follows
        string walkCamera;
        bool walkCameraNow;

        void FollowFace()
        {
            var cam = Camera.main;
            if (cam == null || Cha == null || Cha.objHeadBone == null) return;
            SetCamera(cam.transform.position, Cha.objHeadBone.transform.position, cam.fieldOfView, 0f);
        }

        string NearestPreset()
        {
            var cam = Camera.main;
            string best = "close";
            if (cam == null || Heroine == null) return best;
            Vector3 c = cam.transform.position, at = Heroine.oiCharInfo.changeAmount.pos;
            float d = new Vector2(c.x - at.x, c.z - at.z).magnitude, bestD = float.MaxValue;
            foreach (var kv in Json.Obj(cameras, "presets") ?? new Dictionary<string, object>())
            {
                if (!Json.Has(kv.Value, "distance") || kv.Key.StartsWith("play")) continue;
                float e = Mathf.Abs((float)Json.Num(kv.Value, "distance") - d);
                if (e < bestD) { bestD = e; best = kv.Key; }
            }
            return best;
        }

        void WalkTick(float dt)
        {
            if (!Walking || Heroine == null) return;
            FollowFace();
            var ca = Heroine.oiCharInfo.changeAmount;
            float yaw = Mathf.MoveTowardsAngle(ca.rot.y, walkYaw, (float)Json.Num(WalkCfg, "turn_deg_per_s", 500.0) * dt);
            Vector3 pos = ca.pos;
            var rem = new Vector3(walkTarget.x - pos.x, 0f, walkTarget.z - pos.z);
            float step = (float)Json.Num(WalkCfg, "speed", 1.2) * walkSpeed * dt;
            bool done = rem.magnitude <= step;
            Vector3 next = done ? new Vector3(walkTarget.x, pos.y, walkTarget.z) : pos + rem.normalized * step;
            // furniture in the way (a map collider standing more than max_step above the floor she started on) stops her
            // before it; her height stays (Studio maps' floor colliders do not all lie at the drawn floor)
            RaycastHit hit;
            if (MapHit(next, out hit) && hit.point.y > walkFloor + Json.Num(WalkCfg, "max_step", 0.12))
            {
                WalkLog.Add(new Dictionary<string, object> { { "event", "blocked" }, { "top", Math.Round(hit.point.y, 3) }, { "by", hit.collider.name } });
                Arrive(true);
                return;
            }
            next.y = walkFloor;
            ca.pos = next;
            // the footsteps at the walk's event times (its cycle at the walk's speed)
            var snd = MuseStudioPlugin.Instance.Sounds;
            float cyc = snd.FootCycle, p0 = Mathf.Repeat(walkPhase, cyc);
            walkPhase += dt * walkSpeed;
            float p1 = Mathf.Repeat(walkPhase, cyc);
            foreach (var to in snd.FootTimes)
            {
                float at = Convert.ToSingle(to);
                if ((p1 >= p0 && at > p0 && at <= p1) || (p1 < p0 && (at > p0 || at <= p1))) snd.Footstep(Location, next);
            }
            ca.rot = new Vector3(0f, yaw, 0f);
            if (done) Arrive(true);
        }

        // the nearest of the map's own colliders between two points (the play's camera kept in front of a wall)
        public static bool MapRay(Vector3 from, Vector3 to, out float dist)
        {
            dist = 0f;
            var root = Singleton<Map>.Instance.mapRoot;
            var rooms = MuseStudioPlugin.Instance.Rooms;
            var d = to - from;
            if (d.magnitude < 1e-4f) return false;
            bool any = false;
            // (Studio's map, or the kept room shown - its own scene: the walls the play's camera was missing)
            foreach (var hh in Physics.RaycastAll(from, d.normalized, d.magnitude))
                if (!hh.collider.isTrigger && ((root != null && hh.collider.transform.IsChildOf(root.transform)) || (rooms != null && rooms.InShown(hh.collider.transform)))
                    && (!any || hh.distance < dist)) { dist = hh.distance; any = true; }
            return any;
        }

        // the highest of the map's own colliders under the point (Studio's handles and other objects left out)
        static bool MapHit(Vector3 at, out RaycastHit best)
        {
            best = new RaycastHit();
            var root = Singleton<Map>.Instance.mapRoot;
            if (root == null) return false;
            bool any = false;
            foreach (var hh in Physics.RaycastAll(at + Vector3.up * 0.6f, Vector3.down, 1.0f))
                if (!hh.collider.isTrigger && hh.collider.transform.IsChildOf(root.transform) && (!any || hh.point.y > best.point.y)) { best = hh; any = true; }
            return any;
        }

        void Arrive(bool camera)
        {
            Walking = false;
            if (Heroine != null) Heroine.oiCharInfo.changeAmount.rot = new Vector3(0f, float.IsNaN(arriveYaw) ? walkYaw : arriveYaw, 0f);
            WalkLog.Add(new Dictionary<string, object> { { "event", "arrived" } });
            SceneKey = "";
            ApplyScene(walkKey, walkIntensity, walkSpeed, false);
            if (camera) MuseStudioPlugin.Instance.StartCoroutine(CloseCamera());
        }

        IEnumerator CloseCamera()
        {
            yield return null;
            string key = walkCamera ?? NearestPreset();
            walkCamera = null;
            walkCameraNow = true;
            ApplyCamera(key, false);
            walkCameraNow = false;
        }

        public string ArrivalCamera = "";     // (checks) the preset taken on arrival

        // ---- the lean forward (a scene's "lean": degrees by intensity) ----

        float leanNow;
        Transform[] spine;

        // the upper body leans towards where she faces, shared by the three spine bones, the neck taking a share back (after
        // the animation: called from LateUpdate)
        public void LateTick(float dt)
        {
            if (Heroine == null) return;
            if (propArm != null && heldProp != null) propArm.Apply(Cha.animBody.speed);   // (the arm that holds the item as the original does: BorrowedArm)
            float want = 0f;
            var row = Json.Obj(Json.Obj(scenes, "scenes"), SceneKey);
            var l = Json.Arr(row, "lean");
            if (l != null && l.Count > 0 && !Walking && !MuseStudioPlugin.Instance.Play.Active) want = Convert.ToSingle(l[Mathf.Clamp(Intensity - 1, 0, l.Count - 1)]);
            leanNow = Mathf.MoveTowards(leanNow, want, 30f * dt);
            if (Mathf.Abs(leanNow) < 0.01f) return;
            if (spine == null || spine.Length < 4 || spine[0] == null)
            {
                spine = new Transform[4];
                string[] names = { "cf_j_spine01", "cf_j_spine02", "cf_j_spine03", "cf_j_neck" };
                foreach (var t in Cha.objBodyBone.GetComponentsInChildren<Transform>(true))
                    for (int i = 0; i < 4; i++) if (t.name == names[i]) spine[i] = t;
                if (Array.IndexOf(spine, null) >= 0) { spine = null; return; }
            }
            Vector3 axis = Cha.transform.right;
            for (int i = 0; i < 3; i++) spine[i].rotation = Quaternion.AngleAxis(leanNow / 3f, axis) * spine[i].rotation;
            spine[3].rotation = Quaternion.AngleAxis(-leanNow * 0.6f, axis) * spine[3].rotation;
        }

        public float LeanNow { get { return leanNow; } }

        // after a reaction pose: the scene playing before it; the walk (called every frame)
        // ---- her blush, her body's (Muse's request 3 of 2026-11-04: the body honest): the original's 頬赤 (ChangeHohoAkaRate, the
        // original moves it only with expressions) - her arousal's (from 0.3, 0.85 at 1.0, along a smooth curve; up in about
        // 30 s, down in about 60: stage.json blush) and the expression's cheek, the larger shown; told to no one ----
        public float ExprBlush, BodyBlush;
        float blushShown = -1f;

        // her intoxication (PR #46 §7: a drink's) shown in her eyes: the face's opening narrowed, a third at the most
        float eyesCell = 1f, eyesShownFactor = 1f;
        float Intoxication { get { float v; return MuseStudioPlugin.Instance.Mood != null && MuseStudioPlugin.Instance.Mood.Values.TryGetValue("intoxication", out v) ? v : 0f; } }
        public float DrunkEyes { get { return 1f - 0.3f * Mathf.Clamp01((Intoxication - 0.3f) / 0.7f); } }

        // Her eyes wander a little with her drink (PR #46 §7, its gaze; GPT's post-review of #65: adopted, still to be done): in
        // the talk, while her eyes are at the camera (pattern 1), from tipsy on her gaze is at a point drifting slowly about
        // the camera - up to gaze_sway_degrees off it when she is very drunk (Perlin noise, two slow channels); sober again,
        // at the camera itself. Never over another look (a match's, a play's, out on her own, a reaction's turn).
        Transform swayAt;
        public Transform SwayTarget { get { return swayAt; } }          // (checks)

        void GazeSwayTick()
        {
            var host = MuseStudioPlugin.Instance;
            var cam = Camera.main;
            if (Cha == null || Cha.eyeLookCtrl == null || cam == null) return;
            var look = Cha.eyeLookCtrl;
            bool mine = swayAt != null && look.target == swayAt;
            float s = Mathf.Clamp01((Intoxication - 0.2f) / 0.6f);
            bool may = s > 0f && !host.Play.Active && !host.Match.Active && !host.Outing.Out && host.Presence.HerAt.Length == 0
                       && ReactUntil < 0f && !Climaxing && Cha.fileStatus.eyesLookPtn == 1 && (look.target == cam.transform || mine);
            if (!may)
            {
                if (mine) look.target = cam.transform;
                return;
            }
            if (swayAt == null) swayAt = new GameObject("MuseGazeSway").transform;
            float deg = (float)Json.Num(cfg, "gaze_sway_degrees", 6.0) * s, tt = Time.realtimeSinceStartup * 0.35f;
            float ax = (Mathf.PerlinNoise(tt, 0.3f) - 0.5f) * 2f * deg, ay = (Mathf.PerlinNoise(0.7f, tt + 11f) - 0.5f) * 2f * deg * 0.6f;
            var head = Cha.objHeadBone != null ? Cha.objHeadBone.transform.position : Cha.transform.position;
            float d = Mathf.Max(0.3f, Vector3.Distance(head, cam.transform.position));
            swayAt.position = cam.transform.position + cam.transform.right * (d * Mathf.Tan(ax * Mathf.Deg2Rad)) + cam.transform.up * (d * Mathf.Tan(ay * Mathf.Deg2Rad));
            if (look.target != swayAt) look.target = swayAt;
        }

        // Her drink held as the original holds one (PR #46 §7; the owner's note on #70: not as the original did). The original's
        // wait points (ActionGame.WaitPoint, map/waitpoint/: read by --muse-probe=drinks) drink standing with the state Drink3 and
        // the bottle p_o_koi_ei_petboto00 on the right hand's item point k_f_handR_00, no offsets, the item's own motion none
        // (seated: Drink with the cup p_o_koi_ei_cup00). So: the item's hold (data/items.json: the original's map item - the
        // bundle, the asset, the hand's point, the offsets) loaded by the original's own loader (EasyLoader.Item.Data.Load: its
        // parent by the type, its offsets, its own motion); in the talk, her body the original's drink (hold.motion) on her
        // personality's action controller (action/list/chara), her scene again when it is over (drink_seconds) or a play takes
        // her. A hand's point not there (a body without it): the hand's bone.
        // Her drinking (the user's note of 2026-11-08: she held it and never drank - --muse-probe=drinkplay): the original's
        // Drink3 holds the bottle (f_tachi_00_09, Studio's standing 飲む待機) for two loops, some 14 s, and only then drinks
        // (f_tachi_00_10, Studio's 飲む: the bottle to her mouth) - past the drink's 12 s. So the two clips are played in turn
        // (items.json drink_motion: Studio's own controller of them, its states the clips): held, after sip_after seconds a
        // sip (the drinking clip once), held again until the time is over - a sip under way finished first (sip_max_seconds
        // at most).
        GameObject heldProp;
        float propUntil = -1f;
        bool propMotion;
        int propPhase;                       // (0 held before the sip, 1 the sip, 2 held after it)
        float propSipAt, propSipSince;
        string propHoldState = "", propSipState = "";
        float propSipMax = 10f;
        BorrowedArm propArm;                 // (the arm holding the item as the original holds it, where her body's own drink does not - BorrowedArm.cs)
        public string PropArmNow = "";       // (checks) the arm's state now ("" none)
        public int PropArmBones;             // (checks)
        public float PropArmHead { get { return propArm != null ? propArm.SipWeight : 0f; } }     // (checks) the neck and head the sip's
        bool propSeenSip;
        public int PropSips;                 // (checks) sips begun
        public int PropSipsDone;             // (checks) sips played to their end
        RuntimeAnimatorController propRacWas, propRacDrink;      // (her motion before the drink, and the drink's controller)
        int propStateWas;
        float propTimeWas;
        public string PropShown = "";        // (checks) the item held now ("" none)
        public string PropParent = "";       // (checks) what it is held on
        public string PropMotionNow = "";    // (checks) the motion she drinks with ("" none)
        static Dictionary<int, string[]> actionControllers;

        // her personality's action controller (the original's ActionScene: action/list/chara - [bundle, asset])
        internal static string[] ActionController(int personality)
        {
            if (actionControllers == null)
            {
                actionControllers = new Dictionary<int, string[]>();
                try
                {
                    foreach (var bn in CommonLib.GetAssetBundleNameListFromPath("action/list/chara/", true))
                        foreach (var ci in AssetBundleManager.LoadAllAsset(bn, typeof(ActionGame.CharaInfo), null).GetAllAssets<ActionGame.CharaInfo>())
                            foreach (var pa in ci.param) actionControllers[pa.Personality] = new[] { pa.MotionBundle, pa.MotionAsset };
                }
                catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: the action controllers: " + e.Message); }
            }
            string[] c;
            return actionControllers.TryGetValue(personality, out c) ? c : null;
        }

        public void HoldProp(Dictionary<string, object> hold, float seconds, Dictionary<string, object> drinking = null)
        {
            DropProp();
            if (hold == null || Cha == null) return;
            var host = MuseStudioPlugin.Instance;
            try
            {
                var data = new Illusion.Game.Elements.EasyLoader.Item.Data();
                data.bundle = Json.Str(hold, "bundle", "action/mapitem/00.unity3d");
                data.asset = Json.Str(hold, "asset", "");
                var type = Illusion.Game.Elements.EasyLoader.Item.Data.Type.k_f_handR_00;
                try { type = (Illusion.Game.Elements.EasyLoader.Item.Data.Type)Enum.Parse(typeof(Illusion.Game.Elements.EasyLoader.Item.Data.Type), Json.Str(hold, "type", "k_f_handR_00")); }
                catch (Exception) { }
                data.type = type;
                var pos = Json.Arr(hold, "pos");
                var ang = Json.Arr(hold, "angle");
                if (pos != null && pos.Count >= 3) data.offsetPos = new Vector3(Float(pos[0]), Float(pos[1]), Float(pos[2]));
                if (ang != null && ang.Count >= 3) data.offsetAngle = new Vector3(Float(ang[0]), Float(ang[1]), Float(ang[2]));
                if (data.asset.Length == 0) return;
                bool hasPoint = Illusion.Game.Elements.EasyLoader.Item.Data.GetParent(type, Cha) != null;
                if (hasPoint) heldProp = data.Load(Cha);
                else
                {
                    // (no such point on this body: on the hand's bone, as the original's offsets say)
                    var prefab = CommonLib.LoadAsset<GameObject>(data.bundle, data.asset, true, string.Empty);
                    var hand = HIK.Find(Cha.objBodyBone.transform, type == Illusion.Game.Elements.EasyLoader.Item.Data.Type.k_f_handL_00 ? "cf_j_hand_L" : "cf_j_hand_R");
                    if (prefab == null || hand == null) return;
                    heldProp = UnityEngine.Object.Instantiate(prefab, hand, false);
                    heldProp.transform.localPosition += data.offsetPos;
                    heldProp.transform.localEulerAngles += data.offsetAngle;
                }
                if (heldProp == null) return;
                foreach (var col in heldProp.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(col);
                PropParent = heldProp.transform.parent != null ? heldProp.transform.parent.name : "";
                PropShown = data.asset;
                propUntil = Time.realtimeSinceStartup + seconds;
                // her body the original's drinking, in the talk only (a play, a match, out on her own, walking: the item alone)
                var dm = Json.Obj(hold, "drinking") ?? drinking;
                string holdState = Json.Str(dm, "hold", ""), sipState = Json.Str(dm, "drink", "");
                if (holdState.Length > 0 && !host.Play.Active && !host.Match.Active && !host.Outing.Out && !Moving && !Walking && climaxRun == null
                    && host.Presence.HerAt.Length == 0)
                {
                    // (what she was doing kept: put back as it was when the drink is over - Studio's motion or a talk pose, played
                    // by its own controller; Studio's bookkeeping of its motion does not know of the drink)
                    var an = Cha.animBody;
                    var racWas = an.runtimeAnimatorController;
                    var si = an.GetCurrentAnimatorStateInfo(0);
                    Blend.Next(Cha, 0.4f);
                    // (her personality's action controller - the original's own states, their speeds, their look table - unless
                    // a controller is given)
                    var ctl = Json.Has(dm, "asset") ? new[] { Json.Str(dm, "bundle", "studio/anime/00.unity3d"), Json.Str(dm, "asset", "") }
                                                    : (ActionController(Cha.chaFile.parameter.personality) ?? ActionController(0));
                    if (ctl == null) return;
                    var mo = new Illusion.Game.Elements.EasyLoader.Motion(ctl[0], ctl[1], holdState);
                    mo.Setting(an);
                    if (an.HasState(0, Animator.StringToHash(holdState)))
                    {
                        mo.Play(an);
                        propRacWas = racWas;
                        propStateWas = si.fullPathHash;
                        propTimeWas = si.normalizedTime % 1f;
                        propRacDrink = an.runtimeAnimatorController;
                        propMotion = true;
                        PropMotionNow = holdState;
                        propHoldState = holdState;
                        propSipState = an.HasState(0, Animator.StringToHash(sipState)) ? sipState : "";
                        propPhase = 0;
                        propSeenSip = false;
                        propSipAt = Time.realtimeSinceStartup + (float)Json.Num(dm, "sip_after", 1.5);
                        propSipMax = (float)Json.Num(dm, "sip_max_seconds", 10.0);
                        // the arm as the original holds this item, where her standing drink's is the bottle's (hold.arm: the
                        // controller and the states of the original's motion that holds it - the cup's: the seated drink's,
                        // Studio's 椅子・机 飲む待機 / 飲む); her body then holds still and the arm takes the sip
                        var arm = Json.Obj(hold, "arm");
                        if (arm != null)
                        {
                            try
                            {
                                var actl = Json.Has(arm, "asset") ? new[] { Json.Str(arm, "bundle", "studio/anime/00.unity3d"), Json.Str(arm, "asset", "") }
                                                                  : (ActionController(Cha.chaFile.parameter.personality) ?? ActionController(0));
                                var rac = actl != null ? CommonLib.LoadAsset<RuntimeAnimatorController>(actl[0], actl[1], false, string.Empty) : null;
                                var headBones = new List<string>();
                                var hb = Json.Arr(arm, "head");
                                if (hb != null) foreach (var x in hb) headBones.Add(Convert.ToString(x));
                                propArm = BorrowedArm.Make(Cha, rac, Json.Str(arm, "hold", ""), Json.Str(arm, "drink", ""), Json.Str(arm, "hand", "R"), headBones);
                                if (propArm != null)
                                {
                                    propSipMax = (float)Json.Num(arm, "sip_max_seconds", 15.0);
                                    PropArmNow = propArm.HoldState;
                                    PropArmBones = propArm.Bones;
                                }
                                else MuseStudioPlugin.Log.LogWarning("MuseStudio: the drink's arm: no " + Json.Str(arm, "hold", "") + " in " + Json.Str(arm, "asset", ""));
                            }
                            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: the drink's arm: " + e.Message); propArm = null; }
                        }
                    }
                    else
                    {
                        an.runtimeAnimatorController = racWas;          // (no such state: her motion as it was)
                        an.Play(si.fullPathHash, 0, si.normalizedTime % 1f);
                        MuseStudioPlugin.Log.LogWarning("MuseStudio: the drinking motion: no state " + holdState);
                    }
                }
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: the drink held: " + e.Message); DropProp(); }
        }

        // Her look while she drinks, as the original's (#79: her head did not drink with her arm - the talk's look turned her
        // neck to the user all through): the original's table of the neck's look per motion state (action/list/talklookneck/:
        // ActionScene.talkLookNeckDic; read by --muse-probe=looktable) lets the hold turn it to the one she talks to (Drink3,
        // Drink, Drink4: isLook) and not the sip (Drink3 0, Drink 0, Drink4 0); there the original looks ahead by the motion
        // (Base.LookForDefault: the eyes' pattern 0, the neck's 3) - her head drinks as the motion has it. The state is her
        // body's, or the arm's when an arm holds the item.
        static Dictionary<int, bool> lookTable;

        static bool LookAllowed(int stateHash)
        {
            if (lookTable == null)
            {
                lookTable = new Dictionary<int, bool>();
                try
                {
                    foreach (var bn in CommonLib.GetAssetBundleNameListFromPath("action/list/talklookneck/", true))
                        foreach (var tl in AssetBundleManager.LoadAllAsset(bn, typeof(ActionGame.TalkLookNeck), null).GetAllAssets<ActionGame.TalkLookNeck>())
                            foreach (var p in tl.param) lookTable[Animator.StringToHash(p.StateName)] = p.isLook;
                }
                catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: the look table: " + e.Message); }
            }
            bool ok;
            return !lookTable.TryGetValue(stateHash, out ok) || ok;
        }

        public bool DrinkLooksAway
        {
            get
            {
                if (!propMotion || Cha == null || Cha.animBody == null || Cha.animBody.runtimeAnimatorController != propRacDrink) return false;
                int hash = propArm != null ? propArm.StateHash : Cha.animBody.GetCurrentAnimatorStateInfo(0).shortNameHash;
                return hash != 0 && !LookAllowed(hash);
            }
        }

        bool drinkEyes;
        public int DrinkLooks;               // (checks) times her look went ahead for a sip

        void DrinkLookTick()
        {
            if (Heroine == null || Cha == null) return;
            if (DrinkLooksAway)
            {
                if (!drinkEyes) DrinkLooks++;
                drinkEyes = true;
                if (Cha.fileStatus.eyesLookPtn != 0) Heroine.ChangeLookEyesPtn(0);
            }
            else if (drinkEyes)
            {
                drinkEyes = false;
                SceneGaze();                 // (her eyes at the user again)
            }
        }

        internal void EndPropForCheck() { if (propUntil > 0f) { propUntil = Time.realtimeSinceStartup; propSipMax = 0f; } }     // (checks) the drink over now
        internal void SipNowForCheck() { if (propMotion && propPhase == 0) propSipAt = Time.realtimeSinceStartup; }             // (checks) the sip now

        // held, a sip, held again (her body still the drink's: another motion that took it ends the sips - DropProp leaves it)
        void PropSipTick()
        {
            if (!propMotion || Cha == null) return;
            var an = Cha.animBody;
            if (an == null || an.runtimeAnimatorController != propRacDrink) return;
            float now = Time.realtimeSinceStartup;
            if (propArm != null)
            {
                // (the arm's sip - her body holds still, her neck and head the sip's: BorrowedArm)
                if (propPhase == 0 && propArm.SipState.Length > 0 && now >= propSipAt) { propArm.Sip(); propPhase = 1; propSipSince = now; PropSips++; }
                else if (propPhase == 1 && now - propSipSince > 0.5f && (propArm.SipOver || now - propSipSince >= propSipMax)) { propArm.Back(); propPhase = 2; PropSipsDone++; }
                propArm.Tick(Time.deltaTime);
                PropArmNow = propArm.Now;
                return;
            }
            if (propPhase == 0 && propSipState.Length > 0 && now >= propSipAt)
            {
                an.CrossFadeInFixedTime(propSipState, 0.3f, 0, 0f);    // (seconds: CrossFade's is a part of the state's length - 2 s of the sip blended)
                propPhase = 1;
                propSipSince = now;
                PropMotionNow = propSipState;
                PropSips++;
            }
            else if (propPhase == 1 && now - propSipSince > 0.5f)
            {
                // (over: the original's own way back to the hold done - or its sip at its end, or the time past)
                var si = an.GetCurrentAnimatorStateInfo(0);
                bool inSip = si.IsName(propSipState);
                if (inSip) propSeenSip = true;
                bool over = !an.IsInTransition(0) && propSeenSip && (!inSip || si.normalizedTime >= 0.97f);
                if (over || now - propSipSince >= propSipMax)
                {
                    if (!si.IsName(propHoldState)) an.CrossFadeInFixedTime(propHoldState, 0.4f, 0, 0f);
                    propPhase = 2;
                    PropMotionNow = propHoldState;
                    PropSipsDone++;
                }
            }
        }

        void DropProp()
        {
            if (heldProp != null) UnityEngine.Object.Destroy(heldProp);
            heldProp = null;
            propUntil = -1f;
            PropShown = "";
            PropParent = "";
            propPhase = 0;
            if (propArm != null) propArm.Destroy();
            propArm = null;
            PropArmNow = "";
            PropArmBones = 0;
            if (propMotion)
            {
                // (what she was doing again - not when another motion took her body meanwhile: a scene, a play, her climax)
                propMotion = false;
                PropMotionNow = "";
                var an = Cha != null ? Cha.animBody : null;
                if (an != null && propRacWas != null && an.runtimeAnimatorController == propRacDrink && !MuseStudioPlugin.Instance.Play.Active && climaxRun == null)
                {
                    Blend.Next(Cha, 0.4f);
                    an.runtimeAnimatorController = propRacWas;
                    an.Play(propStateWas, 0, propTimeWas);
                }
                propRacWas = propRacDrink = null;
            }
        }

        void BlushTick(float dt)
        {
            if (Cha == null) return;
            var b = Json.Obj(cfg, "blush");
            float ar;
            MuseStudioPlugin.Instance.Mood.Values.TryGetValue("arousal", out ar);
            float from = (float)Json.Num(b, "from", 0.3), top = (float)Json.Num(b, "max", 0.85);
            float t = Mathf.Clamp01((ar - from) / Mathf.Max(0.01f, 1f - from));
            float want = top * t * t * (3f - 2f * t);
            // (her drink's too: the intoxication's cheeks, the larger shown - PR #46 §7)
            float ix = Intoxication, ti = Mathf.Clamp01((ix - 0.1f) / 0.7f);
            want = Mathf.Max(want, 0.75f * ti * ti * (3f - 2f * ti));
            float ef = DrunkEyes;
            if (Mathf.Abs(ef - eyesShownFactor) > 0.03f) { eyesShownFactor = ef; Cha.ChangeEyesOpenMax(eyesCell * ef); }
            float rate = want > BodyBlush ? top / (float)Json.Num(b, "rise_seconds", 30.0) : top / (float)Json.Num(b, "fall_seconds", 60.0);
            BodyBlush = Mathf.MoveTowards(BodyBlush, want, rate * dt);
            float show = Mathf.Max(ExprBlush, BodyBlush);
            if (Mathf.Abs(show - blushShown) > 0.004f) { Cha.ChangeHohoAkaRate(show); blushShown = show; }
        }

        // Push Up kept to its own result (PushupKeeper.cs) - hers and the other body's (a match's opponent: a body of its own);
        // what the user watches written in the log when it changes - each body's face parts, clothes, Push Up (Diag.cs)
        public readonly PushupKeeper Pushup = new PushupKeeper();
        public readonly PushupKeeper PartnerPushup = new PushupKeeper { Who = "other body" };
        readonly Diag diag = new Diag();

        public void Tick()
        {
            WalkTick(Time.deltaTime);
            BlushTick(Time.deltaTime);
            GazeSwayTick();                // (her drink: her eyes wander a little - PR #46 §7)
            Pushup.Tick(Cha);              // (Push Up kept to its own result: PushupKeeper.cs)
            if (Partner != null) PartnerPushup.Tick(Partner.charInfo);
            diag.Tick(this);
            PropSipTick();
            DrinkLookTick();
            if (propUntil > 0f && (MuseStudioPlugin.Instance.Play.Active || (Time.realtimeSinceStartup >= propUntil
                && (propPhase != 1 || Time.realtimeSinceStartup - propSipSince >= propSipMax)))) DropProp();
            SceneFaceTick();
            TempFaceTick();
            MuseHoldTick();
            OpponentFaceTick();
            PlayDefaultTick();
            if (Heroine != null && !MuseStudioPlugin.Instance.Play.Active && !Walking && Heroine.animeSpeed != speedTarget)
                Heroine.animeSpeed = Mathf.MoveTowards(Heroine.animeSpeed, speedTarget, Mathf.Max(speedRate, 0.01f) * Time.deltaTime);
            if (ReactUntil < 0f) return;
            float tr = Time.realtimeSinceStartup;
            bool over = tr - reactFrom > 0.8f && Cha != null && Blend.Now(Cha.animBody).normalizedTime >= 0.98f;
            if (!over && tr < ReactUntil) return;
            ReactUntil = -1f;
            if (MuseStudioPlugin.Instance.Play.Active) return;
            string key = SceneKey;
            SceneKey = "";
            ApplyScene(key, Intensity, Speed);
        }

        // A camera preset (MUSE_GUIDE.md 3.3) in her frame; moved over move_time unless instant.
        public bool ApplyCamera(string key, bool instant)
        {
            var row = Json.Obj(Json.Obj(cameras, "presets"), key);
            if (row == null || Heroine == null) return false;
            if (Walking && !walkCameraNow) { walkCamera = key; return true; }     // (after her arrival)
            if (walkCameraNow) ArrivalCamera = key;
            Transform root = Cha.transform;
            Vector3 feet = root.position, fwd = root.forward, right = root.right, up = Vector3.up;
            float head = Cha.objHeadBone != null ? Cha.objHeadBone.transform.position.y - feet.y : 1.4f;
            if (MuseStudioPlugin.Instance.Presence.HerRoom != Location)
            {
                // she is out in another room (the toilet, the shower): the user's room as seen from where she would stand
                float yaw;
                StandOf(out feet, out yaw);
                var q = Quaternion.Euler(0f, yaw, 0f);
                fwd = q * Vector3.forward;
                right = q * Vector3.right;
                head = 1.4f;
            }
            float h = Json.Has(row, "height_head") ? head + (float)Json.Num(row, "height_head") : (float)Json.Num(row, "height", 1.3);
            float look = Json.Has(row, "look_head") ? head + (float)Json.Num(row, "look_head") : (float)Json.Num(row, "look", 1.0);
            Vector3 eye = feet + fwd * (float)Json.Num(row, "distance", 1.75) + up * h + right * (float)Json.Num(row, "side", 0);
            Vector3 target = feet + up * look;
            SetCamera(eye, target, (float)Json.Num(cameras, "fov", 40), instant ? 0f : (float)Json.Num(cameras, "move_time", 0.9));
            CameraKey = key;
            return true;
        }

        // The play's camera: from her right side onto the middle of the two bodies (both hips).
        public void PlayCamera(bool instant)
        {
            if (Heroine == null) return;
            Vector3 a = Bone(Cha, "cf_j_hips"), target = a;
            if (Partner != null) target = (a + Bone(Partner.charInfo, "cf_j_hips")) * 0.5f;
            Transform root = Cha.transform;
            Vector3 eye = target + root.right * 2.2f + Vector3.up * 0.7f + root.forward * 0.4f;
            SetCamera(eye, target, (float)Json.Num(cameras, "fov", 40), instant ? 0f : (float)Json.Num(cameras, "move_time", 0.9));
            CameraKey = "play";
        }

        static Vector3 Bone(ChaControl cha, string name)
        {
            var t = Find(cha.transform, name);
            return t != null ? t.position : cha.transform.position + Vector3.up * 0.9f;
        }

        static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = Find(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        public void SetCamera(Vector3 eye, Vector3 target, float fov, float seconds)
        {
            SetCameraData(target, Quaternion.LookRotation(target - eye), new Vector3(0f, 0f, -(target - eye).magnitude), fov, seconds);
        }

        // Studio's camera: the point looked at, the camera's rotation, its offset from that point in its own frame, the fov.
        public void SetCameraData(Vector3 target, Quaternion rot, Vector3 distance, float fov, bool instant)
        {
            SetCameraData(target, rot, distance, fov, instant ? 0f : (float)Json.Num(cameras, "move_time", 0.9));
        }

        public void SetCameraData(Vector3 target, Quaternion rot, Vector3 distance, float fov, float seconds)
        {
            var cc = Singleton<Studio.Studio>.Instance.cameraCtrl;
            var to = new global::Studio.CameraControl.CameraData();
            to.Set(target, rot.eulerAngles, distance, fov);
            if (cameraMove != null) { MuseStudioPlugin.Instance.StopCoroutine(cameraMove); cameraMove = null; }    // (a move under way: this one wins)
            if (seconds <= 0f) { cc.Import(to); return; }
            cameraMove = MuseStudioPlugin.Instance.StartCoroutine(MoveCamera(cc, cc.Export(), to, seconds));
        }

        Coroutine cameraMove;

        // a whole camera at once (CameraMemory's view): a move under way stops, as for any camera set
        public void ImportCamera(global::Studio.CameraControl.CameraData cd)
        {
            if (cameraMove != null) { MuseStudioPlugin.Instance.StopCoroutine(cameraMove); cameraMove = null; }
            Singleton<Studio.Studio>.Instance.cameraCtrl.Import(cd);
        }

        IEnumerator MoveCamera(global::Studio.CameraControl cc, global::Studio.CameraControl.CameraData from, global::Studio.CameraControl.CameraData to, float seconds)
        {
            float t = 0f;
            var cur = new global::Studio.CameraControl.CameraData();
            while (t < 1f)
            {
                t = Mathf.Min(1f, t + Time.deltaTime / seconds);
                float k = Mathf.SmoothStep(0f, 1f, t);
                cur.Set(Vector3.Lerp(from.pos, to.pos, k), Quaternion.Slerp(from.rotation, to.rotation, k).eulerAngles,
                        Vector3.Lerp(from.distance, to.distance, k), Mathf.Lerp(from.parse, to.parse, k));
                cc.Import(cur);
                yield return null;
            }
            cameraMove = null;
        }

        bool axisBefore = true;

        // Studio's canvases and its guide objects (the white handles of the bones: Studio's axis toggle, workInfo.visibleAxis)
        public void HideStudioUi(bool hide)
        {
            var studio = Singleton<Studio.Studio>.Instance;
            if (hide && StudioUiHidden) return;
            if (hide)
            {
                axisBefore = studio.workInfo.visibleAxis;
                studio.workInfo.visibleAxis = false;
                studio.cameraCtrl.ReflectOption();
                hidden.Clear();
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                    if (Array.IndexOf(StudioCanvases, c.name) >= 0 && c.gameObject.activeSelf)
                    {
                        c.gameObject.SetActive(false);
                        hidden.Add(c.gameObject);
                    }
            }
            else
            {
                foreach (var g in hidden) if (g != null) g.SetActive(true);
                hidden.Clear();
                studio.workInfo.visibleAxis = axisBefore;
                studio.cameraCtrl.ReflectOption();
            }
        }

        public bool StudioUiHidden { get { return hidden.Count > 0; } }

        // (checks) any of Studio's canvases on now (whoever set it on)
        public bool StudioCanvasShown()
        {
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                if (Array.IndexOf(StudioCanvases, c.name) >= 0 && c.gameObject.activeInHierarchy) return true;
            return false;
        }

        static int Int(object v) { return (int)Convert.ToDouble(v is string ? double.Parse((string)v, System.Globalization.CultureInfo.InvariantCulture) : v); }
        static float Float(object v) { return (float)(v is string ? double.Parse((string)v, System.Globalization.CultureInfo.InvariantCulture) : Convert.ToDouble(v)); }
        static bool Bool(object v) { return v is bool ? (bool)v : string.Equals(Convert.ToString(v), "true", StringComparison.OrdinalIgnoreCase); }
    }

    // MuseStudio/data files (JSON).
    public static class Data
    {
        public static Dictionary<string, object> Load(string name)
        {
            string p = System.IO.Path.Combine(MuseStudioPlugin.DataDir, name);
            try { return Json.Parse(File.ReadAllText(p, System.Text.Encoding.UTF8)) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
            catch (Exception e)
            {
                MuseStudioPlugin.Log.LogError("MuseStudio: data " + p + ": " + e.Message);
                return new Dictionary<string, object>();
            }
        }

        // a path in a data file: relative to the MuseStudio folder
        public static string Path(string p)
        {
            if (string.IsNullOrEmpty(p) || System.IO.Path.IsPathRooted(p)) return p;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.Combine(MuseStudioPlugin.DataDir, ".."), p));
        }
    }
}
