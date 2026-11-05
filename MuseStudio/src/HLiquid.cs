using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // The liquid (the original's particle semen - its "simple" setting, MetaballCtrl.Proc with SemenType 2 - shot when the
    // female part's state reaches the timings of the position's metaball list, paramMale.fileMetaballCtrl, MetaballParam
    // "inside/state/time"):
    //  - an outside shot (ID 1 / 4): h/list h_particle 0 on the male part's bone, as the original's simple setting;
    //  - the drip after an inside finish (ID 0 the vagina, 3 the anus) and the spit of a mouth finish (ID 2): the original
    //    draws these with metaballs only (its H scene's own object, which Studio does not have; its simple setting draws
    //    nothing) - here the same particle at the list's shoot axis (ObjDirParent of Dir_parent_sex, along its forward), at
    //    the list's timings, only after a finish inside (flags.isInsideFinish).
    // With a condom the original shoots the used condom instead: nothing here. Where it lands the position's siru list marks
    // her (Climax). Her leak (a mood's "leaked"): the original's pee (h_particle 1, the toilet peeping's) on her.
    public class HLiquid
    {
        class Shot { public int Id; public bool Inside; public string Anim = ""; public float Time, Old, Seen = -1f; }
        readonly List<Shot> shots = new List<Shot>();
        readonly List<GameObject> objs = new List<GameObject>();
        ParticleSystem outside;
        readonly Dictionary<int, ParticleSystem> axis = new Dictionary<int, ParticleSystem>();   // ID -> the particle at its shoot axis
        public int Fired, Dripped;          // (checks)
        public static ParticleSystem LastPee;   // (checks)
        public ParticleSystem LastDrip;         // (checks)
        readonly List<string> axisNames = new List<string>();
        readonly Dictionary<int, bool> falling = new Dictionary<int, bool>();     // ID -> stood in for (true: from the mouth)
        static readonly Dictionary<string, string> SiruBones = new Dictionary<string, string> {
            { "SIRU_kokan", "cf_d_kokan" }, { "SIRU_ana", "cf_d_ana" }, { "SIRU_anal", "cf_d_ana" }, { "SIRU_mouth", "a_n_mouth" }, { "SIRU_kuti", "a_n_mouth" } };

        static string[,] Particles()
        {
            string[,] r;
            GlobalMethod.GetListString(GlobalMethod.LoadAllListText("h/list/", "h_particle", null), out r);
            return r;
        }

        static float F(string s) { float v; return float.TryParse(s, out v) ? v : 0f; }

        // h_particle's row of that id, made under the parent bone of the body (its list offsets; at the axis when given)
        static GameObject Make(int id, Transform body, Transform at, List<GameObject> keep)
        {
            var r = Particles();
            for (int i = 0; i < r.GetLength(0); i++)
            {
                if (r[i, 0] != id.ToString()) continue;
                Transform parent = at ?? HIK.Find(body, r[i, 4]);
                if (parent == null) return null;
                var obj = CommonLib.LoadAsset<GameObject>(r[i, 1], r[i, 2], true, string.Empty);
                if (obj == null) return null;
                obj.transform.parent = parent;
                obj.transform.localPosition = at != null ? Vector3.zero : new Vector3(F(r[i, 5]), F(r[i, 6]), F(r[i, 7]));
                obj.transform.localRotation = at != null ? Quaternion.identity : Quaternion.Euler(F(r[i, 8]), F(r[i, 9]), F(r[i, 10]));
                obj.transform.localScale = Vector3.one;
                // the H scene's camera draws the Default layer, Studio's does not: the body's layer
                int layer = DrawnLayer(body);
                foreach (var t in obj.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
                foreach (var pr in obj.GetComponentsInChildren<ParticleSystemRenderer>(true)) pr.enabled = true;     // (the prefab keeps some off)
                if (keep != null) keep.Add(obj);
                return obj;
            }
            return null;
        }

        // a layer Studio's camera draws: the character's own meshes' (Chara)
        static int DrawnLayer(Transform body)
        {
            var cam = Camera.main;
            foreach (var r in body.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (cam == null || (cam.cullingMask & (1 << r.gameObject.layer)) != 0) return r.gameObject.layer;
            int chara = LayerMask.NameToLayer("Chara");
            return chara >= 0 ? chara : body.gameObject.layer;
        }

        public void Begin(string file, ChaControl male, ChaControl female)
        {
            Stop();
            shots.Clear();
            axisNames.Clear();
            if (male == null || female == null || string.IsNullOrEmpty(file)) return;
            try
            {
                var bodies = new[] { male.objBodyBone.transform, female.objBodyBone.transform };
                foreach (var mp in GlobalMethod.LoadAllFolder<MetaballParam>("h/list/", file, null, null))
                    foreach (var p in mp.param)
                    {
                        if (!p.Enable || p.ID < 0 || p.ID > 4) continue;
                        foreach (string c in p.ShootCondition)
                        {
                            var a = c.Split('/');
                            float t;
                            if (a.Length == 3 && float.TryParse(a[2], out t)) shots.Add(new Shot { Id = p.ID, Inside = a[0] == "1", Anim = a[1], Time = t });
                        }
                        if (p.ID == 1 || p.ID == 4 || axis.ContainsKey(p.ID) || p.Dir_parent_sex < 0 || p.Dir_parent_sex > 1) continue;
                        // the H scene's own helpers (SIRU_kokan ...) are not on a Studio body: the body's bone there, the liquid
                        // falling (from the mouth: forwards and down)
                        var at = HIK.Find(bodies[p.Dir_parent_sex], p.ObjDirParent);
                        string stand = null;
                        if (at == null && SiruBones.TryGetValue(p.ObjDirParent ?? "", out stand)) at = HIK.Find(bodies[p.Dir_parent_sex], stand);
                        axisNames.Add(p.ID + ":" + p.Dir_parent_sex + "/" + p.ObjDirParent + (stand != null ? "->" + stand : "") + (at != null ? "" : "(none)"));
                        var o = at != null ? Make(0, bodies[p.Dir_parent_sex], at, objs) : null;
                        if (o != null) { axis[p.ID] = o.GetComponent<ParticleSystem>(); if (stand != null) falling[p.ID] = stand == "a_n_mouth"; }
                    }
                var po = Make(0, bodies[0], null, objs);
                if (po != null) outside = po.GetComponent<ParticleSystem>();
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: liquid " + file + ": " + e.Message); }
        }

        // (checks) the shots and the axes made
        public string Describe()
        {
            var o = new List<string>();
            foreach (var sh in shots) o.Add(sh.Id + (sh.Inside ? "i" : "") + ":" + sh.Anim + "@" + sh.Time + " seen " + sh.Seen.ToString("F2"));
            var ax = new List<string>();
            foreach (var kv in axis) ax.Add(kv.Key + "=" + (kv.Value != null ? kv.Value.transform.parent.name : "null"));
            return string.Join(" ", o.ToArray()) + " | axis " + string.Join(" ", ax.ToArray()) + " | params " + string.Join(" ", axisNames.ToArray()) + " | outside " + (outside != null);
        }

        public bool On { get { return shots.Count > 0 && (outside != null || axis.Count > 0); } }

        // every frame: the female part's state against the shots' timings (MetaballCtrl.ShootCtrl.IsAnimation); inside: the
        // finish was inside (the vagina, the mouth)
        public void Tick(Animator female, bool condom, bool inside)
        {
            if (!On || female == null) return;
            var ai = Blend.Now(female);          // (the state it fades into)
            foreach (var s in shots)
            {
                if (!ai.IsName(s.Anim)) { s.Old = 0f; continue; }
                s.Seen = Mathf.Max(s.Seen, ai.normalizedTime);
                if (s.Inside && !inside) continue;
                if (s.Old <= s.Time && s.Time < ai.normalizedTime && !condom)
                {
                    ParticleSystem ps;
                    if (s.Id == 1 || s.Id == 4) { if (outside != null) { Play(outside); Fired++; } }
                    else if (axis.TryGetValue(s.Id, out ps))
                    {
                        bool mouth;
                        if (falling.TryGetValue(s.Id, out mouth))
                        {
                            var body = female.transform;
                            ps.transform.rotation = Quaternion.LookRotation(mouth ? body.forward + Vector3.down * 0.6f : Vector3.down + body.forward * 0.35f);
                        }
                        Play(ps);
                        LastDrip = ps;
                        Dripped++;
                    }
                }
                s.Old = ai.normalizedTime;
            }
        }

        static void Play(ParticleSystem ps)
        {
            ps.Simulate(0f);
            ps.Play();
        }

        public void Stop()
        {
            foreach (var o in objs) if (o != null) UnityEngine.Object.Destroy(o);
            objs.Clear();
            axis.Clear();
            falling.Clear();
            outside = null;
        }

        // her leak: the original's pee (h_particle 1) on her for seconds; seen only while her lower body is bare (as the
        // Godot app's): false when covered
        public static bool Pee(ChaControl cha, float seconds)
        {
            if (cha == null || cha.IsKokanHide()) return false;
            GameObject o = null;
            try { o = Make(1, cha.objBodyBone.transform, null, null); }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: pee: " + e.Message); }
            if (o == null) return false;
            var ps = o.GetComponent<ParticleSystem>();
            if (ps != null) Play(ps);
            LastPee = ps;
            MuseStudioPlugin.Instance.StartCoroutine(StopLater(o, ps, seconds));
            return true;
        }

        static System.Collections.IEnumerator StopLater(GameObject o, ParticleSystem ps, float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (ps != null) ps.Stop();
            yield return new WaitForSeconds(4f);
            if (o != null) UnityEngine.Object.Destroy(o);
        }
    }
}
