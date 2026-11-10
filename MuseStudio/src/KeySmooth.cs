using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MuseStudio
{
    // Evens out a motion whose keys step unevenly (#70, session 12's note 3, the owner's call of 2026-11-10). The added (AL)
    // plays' waits join their keys with straight lines and change speed from key to key (her neck: 0.12, 0.10, 0.13, 0.14, 0.34
    // degrees a key); the original's change speed smoothly. Joining the same keys with curves does not help (the keys themselves
    // are uneven); averaging each key with the two on either side (1:4:6:4:1, round the loop) brings them to the original's, the
    // motion's reach kept (her neck 1.76 to 1.73 degrees). So: the clip's keys taken once (sampled at its own rate, the pose
    // put back), and every frame the difference to the averaged keys laid on the pose the animator gave - no delay, the whole loop
    // being known - before the neck look (it still turns the neck where the eye / neck table says). Only a looping single-clip
    // state on layer 0 (the other layers at 0); in 0.2 s in and out, and along the animator's own crossfades. Who asks decides
    // when (PlayCtl: an AL play's wait at pace 0); any other motion found to step so can be handed to it the same way.
    public static class KeySmooth
    {
        static readonly float[] Taps = { 1f, 4f, 6f, 4f, 1f };
        const float FadeSeconds = 0.2f;
        public static bool OffForProbe;                 // (development: the probe's phase without it)
        public static int Taken;                        // (checks) clips taken

        class Table
        {
            public Transform[] Bones;
            public Quaternion[][] Rot;                  // per bone, per key: the turn from the clip's key to the averaged one
            public Vector3[][] Pos;                     // per bone, per key: the same for its place (null: the clip keeps it)
            public int Keys;
            public float Rate;
        }

        class Body
        {
            public ChaControl Cha;
            public bool Want;
            public float Weight;
            public AnimationClip Clip;
            public Table T;
            public readonly Dictionary<AnimationClip, Table> Tables = new Dictionary<AnimationClip, Table>();
            public int Frame = -1;
            public float LastDeg;                       // (checks) the largest turn laid on this frame
            // what the bones had and were given last frame (a frame the animator left the pose: the same given again, not twice)
            public Quaternion[] InR, OutR;
            public Vector3[] InP, OutP;
        }

        static readonly List<Body> bodies = new List<Body>();

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(NeckLookControllerVer2), "LateUpdate"), new HarmonyMethod(typeof(KeySmooth), "BeforeNeck"));
        }

        // these bodies evened out (on) or let go; every other body lets go
        public static void Want(ChaControl a, ChaControl b, bool on)
        {
            for (int i = bodies.Count - 1; i >= 0; i--)
            {
                var x = bodies[i];
                if (x.Cha == null) { bodies.RemoveAt(i); continue; }
                x.Want = on && (x.Cha == a || x.Cha == b);
            }
            if (!on) return;
            if (a != null && Find(a) == null) bodies.Add(new Body { Cha = a, Want = true });
            if (b != null && Find(b) == null) bodies.Add(new Body { Cha = b, Want = true });
        }

        static Body Find(ChaControl c)
        {
            foreach (var x in bodies) if (x.Cha == c) return x;
            return null;
        }

        // (checks) how far it is in (0..1), how many bones it moves, the largest turn laid on the last frame
        public static float Weight(ChaControl c) { var b = c != null ? Find(c) : null; return b != null ? b.Weight : 0f; }
        public static int Bones(ChaControl c) { var b = c != null ? Find(c) : null; return b != null && b.T != null ? b.T.Bones.Length : 0; }
        public static float LastDeg(ChaControl c) { var b = c != null ? Find(c) : null; return b != null ? b.LastDeg : 0f; }

        // the neck look of a body it evens out: first the keys, then the neck
        static void BeforeNeck(NeckLookControllerVer2 __instance)
        {
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].Cha != null && bodies[i].Cha.neckLookCtrl == __instance) Apply(bodies[i]);
        }

        // (the plugin's LateUpdate) a body whose neck look came later or does not run
        public static void Late()
        {
            for (int i = 0; i < bodies.Count; i++) if (bodies[i].Cha != null) Apply(bodies[i]);
        }

        static AnimationClip One(AnimatorClipInfo[] infos)
        {
            AnimationClip c = null;
            foreach (var ci in infos)
            {
                if (ci.clip == null || ci.weight < 0.001f) continue;
                if (c != null || ci.weight < 0.999f) return null;
                c = ci.clip;
            }
            return c;
        }

        static bool OthersOff(Animator an)
        {
            for (int l = 1; l < an.layerCount; l++) if (an.GetLayerWeight(l) > 0.001f) return false;
            return true;
        }

        static void Apply(Body b)
        {
            if (b.Frame == Time.frameCount) return;
            b.Frame = Time.frameCount;
            b.LastDeg = 0f;
            if (!b.Want && b.Weight <= 0f) return;
            var an = b.Cha.animBody;
            if (an == null || !an.isActiveAndEnabled || an.runtimeAnimatorController == null || an.layerCount < 1) { b.Weight = 0f; return; }
            AnimationClip clip = One(an.GetCurrentAnimatorClipInfo(0));
            float nt = an.GetCurrentAnimatorStateInfo(0).normalizedTime, share = 1f;
            bool fading = an.IsInTransition(0);
            if (fading)
            {
                // a crossfade: the clip it has, by its share (out of it, or into it); no new clip taken in one
                float p = Mathf.Clamp01(an.GetAnimatorTransitionInfo(0).normalizedTime);
                share = 1f - p;
                if (clip == null || clip != b.Clip)
                {
                    clip = One(an.GetNextAnimatorClipInfo(0));
                    nt = an.GetNextAnimatorStateInfo(0).normalizedTime;
                    share = p;
                }
                if (clip != b.Clip) clip = null;
            }
            bool ok = b.Want && !OffForProbe && clip != null && clip.isLooping && OthersOff(an);
            if (ok && clip != b.Clip)
            {
                Table t;
                if (!b.Tables.TryGetValue(clip, out t))
                {
                    if (b.Tables.Count >= 16) b.Tables.Clear();
                    t = Take(an, clip);
                    b.Tables[clip] = t;
                }
                b.Clip = clip;
                b.T = t;
                b.Weight = 0f;
                int n = t.Bones.Length;
                b.InR = new Quaternion[n]; b.OutR = new Quaternion[n]; b.InP = new Vector3[n]; b.OutP = new Vector3[n];
                for (int i = 0; i < n; i++) { b.OutR[i] = new Quaternion(9f, 9f, 9f, 9f); b.OutP[i] = new Vector3(float.NaN, 0f, 0f); }
            }
            b.Weight = Mathf.MoveTowards(b.Weight, ok ? 1f : 0f, Time.deltaTime / FadeSeconds);
            var tb = b.T;
            if (tb == null || clip == null || clip != b.Clip || b.Weight <= 0f || tb.Bones.Length == 0) return;
            float w = b.Weight * share;
            if (w <= 0f) return;
            float x = Mathf.Repeat(nt, 1f) * tb.Keys;
            int k0 = Mathf.FloorToInt(x), k1;
            float u = x - k0;
            k0 = ((k0 % tb.Keys) + tb.Keys) % tb.Keys;
            k1 = (k0 + 1) % tb.Keys;
            for (int i = 0; i < tb.Bones.Length; i++)
            {
                var bone = tb.Bones[i];
                if (bone == null) continue;
                Quaternion c = Quaternion.Slerp(tb.Rot[i][k0], tb.Rot[i][k1], u);
                if (w < 1f) c = Quaternion.Slerp(Quaternion.identity, c, w);
                Quaternion cur = bone.localRotation;
                if (Same(cur, b.OutR[i])) cur = b.InR[i];
                b.InR[i] = cur;
                bone.localRotation = cur * c;
                b.OutR[i] = bone.localRotation;
                float deg = Deg(c);
                if (deg > b.LastDeg) b.LastDeg = deg;
                var ps = tb.Pos[i];
                if (ps == null) continue;
                Vector3 cp = Vector3.Lerp(ps[k0], ps[k1], u) * w;
                Vector3 pos = bone.localPosition;
                if (pos.x == b.OutP[i].x && pos.y == b.OutP[i].y && pos.z == b.OutP[i].z) pos = b.InP[i];
                b.InP[i] = pos;
                bone.localPosition = pos + cp;
                b.OutP[i] = bone.localPosition;
            }
        }

        static bool Same(Quaternion a, Quaternion b) { return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w; }

        // a turn's angle (degrees), exact for the small ones (Quaternion.Angle rounds them to 0)
        static float Deg(Quaternion q)
        {
            float v = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            return 2f * Mathf.Atan2(v, Mathf.Abs(q.w)) * Mathf.Rad2Deg;
        }

        // the clip's keys (at its own rate) on every bone under the animator, the pose put back afterwards (on the body itself:
        // a bare copy of the bones, without an animator, takes no keys - the probe of 2026-11-10); per bone that the averaging
        // moves, the turn (and the shift) from each key to its average
        static Table Take(Animator an, AnimationClip clip)
        {
            Taken++;
            float rate = clip.frameRate > 1f && clip.frameRate <= 120f ? clip.frameRate : 30f;
            int n = Mathf.Clamp(Mathf.RoundToInt(clip.length * rate), 0, 600);
            var t = new Table { Keys = n, Rate = rate, Bones = new Transform[0], Rot = new Quaternion[0][], Pos = new Vector3[0][] };
            if (n < Taps.Length) return t;
            var all = an.GetComponentsInChildren<Transform>(true);
            int m = all.Length;
            var r0 = new Quaternion[m];
            var p0 = new Vector3[m];
            for (int i = 0; i < m; i++) { r0[i] = all[i].localRotation; p0[i] = all[i].localPosition; }
            var R = new Quaternion[n][];
            var P = new Vector3[n][];
            for (int k = 0; k < n; k++)
            {
                clip.SampleAnimation(an.gameObject, k / rate);
                R[k] = new Quaternion[m];
                P[k] = new Vector3[m];
                for (int i = 0; i < m; i++) { R[k][i] = all[i].localRotation; P[k][i] = all[i].localPosition; }
            }
            for (int i = 0; i < m; i++) { all[i].localRotation = r0[i]; all[i].localPosition = p0[i]; }
            var bones = new List<Transform>();
            var rots = new List<Quaternion[]>();
            var poss = new List<Vector3[]>();
            int h = Taps.Length / 2;
            float sum = 0f;
            foreach (var tp in Taps) sum += tp;
            for (int i = 1; i < m; i++)                 // (not the animator's own object: Studio places it)
            {
                var cr = new Quaternion[n];
                var cp = new Vector3[n];
                float maxDeg = 0f, maxPos = 0f;
                for (int k = 0; k < n; k++)
                {
                    Quaternion c0 = R[k][i];
                    Vector4 acc = Vector4.zero;
                    Vector3 pacc = Vector3.zero;
                    for (int j = -h; j <= h; j++)
                    {
                        int kk = ((k + j) % n + n) % n;
                        Quaternion q = R[kk][i];
                        float wj = Taps[j + h];
                        if (Quaternion.Dot(q, c0) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                        acc += new Vector4(q.x, q.y, q.z, q.w) * wj;
                        pacc += P[kk][i] * wj;
                    }
                    acc.Normalize();
                    cr[k] = Quaternion.Inverse(c0) * new Quaternion(acc.x, acc.y, acc.z, acc.w);
                    cp[k] = pacc / sum - P[k][i];
                    maxDeg = Mathf.Max(maxDeg, Deg(cr[k]));
                    maxPos = Mathf.Max(maxPos, cp[k].magnitude);
                }
                if (maxDeg < 0.001f && maxPos < 0.00001f) continue;        // (the clip keeps it, or moves it evenly)
                bones.Add(all[i]);
                rots.Add(cr);
                poss.Add(maxPos >= 0.00001f ? cp : null);
            }
            t.Bones = bones.ToArray();
            t.Rot = rots.ToArray();
            t.Pos = poss.ToArray();
            return t;
        }
    }
}
