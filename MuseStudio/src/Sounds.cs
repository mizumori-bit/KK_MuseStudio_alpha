using System;
using System.Collections.Generic;
using Manager;
using UnityEngine;

namespace MuseStudio
{
    // The sound effects (the app plays them, Muse does not name them; data/sounds.json; the Godot app's sound_effects.gd),
    // through the original's sound manager (its volumes): the place change's door, each map's ambience (the original's EnvArea
    // playlist: every clip at its position, again after a random time in its range), the footsteps of a walk (the walk's
    // event times, the map's floor sound), and the H sounds - the original's own HSeCtrl over the position's list (h/list
    // fileSe: the female part's states and their timings, at her / his bones).
    public class Sounds
    {
        readonly MuseStudioPlugin h;
        public readonly Dictionary<string, object> Doc;
        HSeCtrl hse;
        ChaControl hseFemale;
        class Env { public string Bundle, Asset; public Vector3 Pos; public float Min, Max, Left; }
        readonly List<Env> env = new List<Env>();
        readonly System.Random rng = new System.Random();
        public readonly List<string> Log = new List<string>();     // (checks) what played

        public Sounds(MuseStudioPlugin host)
        {
            h = host;
            Doc = Data.Load("sounds.json");
        }

        public static Transform Play(Manager.Sound.Type type, string bundle, string asset, Vector3? at = null)
        {
            if (string.IsNullOrEmpty(bundle) || string.IsNullOrEmpty(asset)) return null;
            Transform t = null;
            try { t = Illusion.Game.Utils.Sound.Play(new Illusion.Game.Utils.Sound.Setting { type = type, assetBundleName = bundle, assetName = asset }); }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: sound " + asset + ": " + e.Message); }
            if (t != null && at.HasValue) t.position = at.Value;
            var inst = MuseStudioPlugin.Instance.Sounds;
            if (inst != null && t != null) { inst.Log.Add(asset); if (inst.Log.Count > 200) inst.Log.RemoveAt(0); }
            return t;
        }

        public bool Effect(string key)
        {
            var e = Json.Obj(Json.Obj(Doc, "effects"), key);
            return e != null && Play(Manager.Sound.Type.GameSE2D, Json.Str(e, "bundle"), Json.Str(e, "asset")) != null;
        }

        // the place changes: the door as the original chooses it (sounds.json location_change: its events open a school room's
        // sliding door, a hinged one elsewhere; its free moves through gates play nothing) - entering a room its door opens,
        // going out to a doorless place the left room's door closes, between doorless places nothing; then the new map's ambience
        public string Moving(string from, string to)
        {
            var lc = Json.Obj(Doc, "location_change");
            if (lc == null) { var one = Json.Str(Doc, "location_change", ""); if (one.Length > 0) Effect(one); return one; }
            var doors = Json.Obj(lc, "doors");
            var rooms = Json.Obj(lc, "rooms");
            string kTo = Json.Str(rooms, to ?? "", null), kFrom = Json.Str(rooms, from ?? "", null);
            string key = null;
            if (kTo != null) { var d = Json.Arr(doors, kTo); if (d != null && d.Count > 0) key = d[0] as string; }
            else if (kFrom != null) { var d = Json.Arr(doors, kFrom); if (d != null && d.Count > 1) key = d[1] as string; }
            if (key != null) Effect(key);
            LastMoveEffect = key ?? "";
            return key;
        }

        public string LastMoveEffect = "";      // (checks) the effect the last place change chose ("" none)

        public void Entered(string location)
        {
            env.Clear();
            Vector3 off = MuseStudioPlugin.Instance.Rooms.OffsetOf(location);
            foreach (var it in Json.Arr(Json.Obj(Doc, "maps"), location) ?? new List<object>())
            {
                var p = Json.Arr(it, "position");
                var d = Json.Arr(it, "delay");
                float a = d != null && d.Count > 1 ? Convert.ToSingle(d[0]) : 20f, b = d != null && d.Count > 1 ? Convert.ToSingle(d[1]) : 60f;
                env.Add(new Env { Bundle = Json.Str(it, "bundle"), Asset = Json.Str(it, "asset"), Min = a, Max = b, Left = Rand(a, b),
                    Pos = off + (p != null && p.Count >= 3 ? new Vector3(Convert.ToSingle(p[0]), Convert.ToSingle(p[1]), Convert.ToSingle(p[2])) : Vector3.zero) });
            }
        }

        float Rand(float a, float b) { return a + (float)rng.NextDouble() * (b - a); }

        public int EnvCount { get { return env.Count; } }

        // the footsteps: the floor's sound at her feet
        public void Footstep(string location, Vector3 at)
        {
            var f = Json.Obj(Json.Obj(Json.Obj(Doc, "footsteps"), "floors"), location);
            if (f != null) Play(Manager.Sound.Type.GameSE3D, Json.Str(f, "bundle"), Json.Str(f, "asset"), at);
        }

        public List<object> FootTimes { get { return Json.Arr(Json.Obj(Doc, "footsteps"), "times") ?? new List<object>(); } }
        public float FootCycle { get { return (float)Json.Num(Json.Obj(Doc, "footsteps"), "cycle", 2.0667); } }

        // ---- the H sounds (the original's HSeCtrl) ----

        public void PlayStarted(string fileSe, ChaControl female, ChaControl male)
        {
            if (hse == null) hse = h.gameObject.AddComponent<HSeCtrl>();
            hseFemale = female;
            bool ok = false;
            try
            {
                ok = !string.IsNullOrEmpty(fileSe) && hse.Load("h/list/", fileSe, male != null ? male.objBodyBone : null, female != null ? female.objBodyBone : null,
                    Singleton<Studio.Map>.Instance.mapRoot);
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: H sounds " + fileSe + ": " + e.Message); }
            if (!ok) hseFemale = null;
        }

        public void PlayEnded()
        {
            hseFemale = null;
            try { Singleton<Manager.Sound>.Instance.Stop(Manager.Sound.Type.GameSE3D); } catch (Exception) { }
        }

        public bool HOn { get { return hseFemale != null; } }

        public void Tick(float dt)
        {
            if (hseFemale != null && hseFemale.animBody != null)
            {
                try { hse.Proc(Blend.Now(hseFemale.animBody), new[] { hseFemale }); }
                catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: H sounds: " + e.Message); hseFemale = null; }
            }
            foreach (var e in env)
            {
                e.Left -= dt;
                if (e.Left > 0f) continue;
                e.Left = Rand(e.Min, e.Max);
                Play(Manager.Sound.Type.ENV, e.Bundle, e.Asset, e.Pos);
            }
        }
    }
}
