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
            var row = Json.Obj(Json.Obj(locations, "locations"), key);
            if (row == null) { MuseStudioPlugin.Log.LogWarning("MuseStudio: unknown location " + key); yield break; }
            int no = (int)Json.Num(row, "map", -1);
            Info.MapLoadInfo mi;
            Rooms.Find(row, ref no, out mi);         // (a mod map: its number this run)
            var map = Singleton<Map>.Instance;
            var rooms = MuseStudioPlugin.Instance.Rooms;
            Moving = true;
            StopWalk(false);
            if (rooms.Has(key))
            {
                // a room kept in the scene: instant (Studio's own map, if one is in, goes)
                if (Ready && key != Location) MuseStudioPlugin.Instance.Sounds.Moving();
                if (map.no != -1) { map.ReleaseMap(); yield return null; }
                rooms.Show(key);
                yield return null;
            }
            else if (map.no != no)
            {
                rooms.Show(null);
                if (Ready) MuseStudioPlugin.Instance.Sounds.Moving();      // the door (not at the start)
                Singleton<Studio.Studio>.Instance.AddMap(no, true, false, true);
                float t0 = Time.realtimeSinceStartup;
                yield return null;
                while ((map.isLoading || map.no != no) && Time.realtimeSinceStartup - t0 < 30f) yield return null;
                Step("map " + no + " loaded=" + (map.no == no));
                yield return null;
            }
            Moving = false;
            Location = key;
            LoadSpots(row);
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
            return new Dictionary<string, object> { { "key", key }, { "name", row != null ? Json.Str(row, "name", key) : key },
                { "summary", Json.Str(row, "summary", "") }, { "tags", Json.Arr(row, "tags") ?? new List<object>() } };
        }

        // the places one can go: the rooms kept in the scene (the setting Rooms), in its order. A location outside them would
        // be Studio's own map loaded beside the kept rooms, and that shows broken (its lightmaps among theirs: the trial run of
        // 2026-11-03) - more places are added to the setting.
        public IEnumerable<string> LocationKeys()
        {
            var d = Json.Obj(locations, "locations");
            if (d == null) yield break;
            var rooms = MuseStudioPlugin.Instance.Rooms;
            bool any = false;
            foreach (var k in rooms.Keys) if (d.ContainsKey(k)) { any = true; yield return k; }
            if (!any) foreach (var k in d.Keys) yield return k;      // (no kept rooms: every location, one at a time)
        }

        public bool Reachable(string key)
        {
            foreach (var k in LocationKeys()) if (k == key) return true;
            return false;
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
            if (Partner == null) Partner = AddChara(male, card);
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
            return who != null && ApplyCells(Json.Arr(Json.Obj(Json.Obj(faces, "labels"), label), "cells"), who);
        }

        public bool ApplyPresetOn(OCIChar who, string name)
        {
            return who != null && ApplyCells(Json.Arr(Json.Obj(faces, "presets"), name), who);
        }

        public Dictionary<string, object> Locations { get { return locations; } }
        public Dictionary<string, object> Cameras { get { return cameras; } }

        public bool ApplyFace(string label)
        {
            if (!ApplyCells(Json.Arr(Json.Obj(Json.Obj(faces, "labels"), label), "cells"))) return false;
            Emotion = label;
            Face = label;
            return true;
        }

        // A face preset by name (a touch's face in a play, the climax, the afterglow); the emotion stays Muse's label.
        public bool ApplyPreset(string name)
        {
            if (!ApplyCells(Json.Arr(Json.Obj(faces, "presets"), name))) return false;
            Face = "preset:" + name;
            return true;
        }

        // The face shown now: an emotion label, "preset:<name>" or "expr:<name>" (a touch reaction's expression).
        public string Face = "";

        // Puts back a face Face named.
        public void RestoreFace(string face)
        {
            if (string.IsNullOrEmpty(face)) return;
            if (face.StartsWith("preset:")) ApplyPreset(face.Substring(7));
            else if (!face.StartsWith("expr:")) ApplyFace(face);
        }

        // The original's face parameters (the expression table's columns: eyebrow / eyes / mouth pattern, their open max,
        // eyes_look, cheek, highlight, tears, blink); an empty cell keeps that part.
        public bool ApplyCells(List<object> c, OCIChar who = null)
        {
            var oci = who ?? Heroine;
            if (c == null || c.Count < 11 || oci == null) return false;
            var cha = oci.charInfo;
            if (Set(c[0])) cha.ChangeEyebrowPtn(Int(c[0]), true);
            if (Set(c[1])) cha.ChangeEyesPtn(Int(c[1]), true);
            if (Set(c[2])) cha.ChangeMouthPtn(Int(c[2]), true);
            if (Set(c[3])) cha.ChangeEyebrowOpenMax(Float(c[3]));
            if (Set(c[4])) cha.ChangeEyesOpenMax(Float(c[4]));
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

        void BlushTick(float dt)
        {
            if (Cha == null) return;
            var b = Json.Obj(cfg, "blush");
            float ar;
            MuseStudioPlugin.Instance.Mood.Values.TryGetValue("arousal", out ar);
            float from = (float)Json.Num(b, "from", 0.3), top = (float)Json.Num(b, "max", 0.85);
            float t = Mathf.Clamp01((ar - from) / Mathf.Max(0.01f, 1f - from));
            float want = top * t * t * (3f - 2f * t);
            float rate = want > BodyBlush ? top / (float)Json.Num(b, "rise_seconds", 30.0) : top / (float)Json.Num(b, "fall_seconds", 60.0);
            BodyBlush = Mathf.MoveTowards(BodyBlush, want, rate * dt);
            float show = Mathf.Max(ExprBlush, BodyBlush);
            if (Mathf.Abs(show - blushShown) > 0.004f) { Cha.ChangeHohoAkaRate(show); blushShown = show; }
        }

        public void Tick()
        {
            WalkTick(Time.deltaTime);
            BlushTick(Time.deltaTime);
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
