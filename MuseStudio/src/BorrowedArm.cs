using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // One arm moved as the original moves it in another motion of its own, over the motion her body plays (the user's note
    // of 2026-11-08: a cup held standing; "the original's way of using it where it does is to be read, and from it the
    // point the item is put on and the hand that holds it"). The original holds its cup only seated - the wait points'
    // Drink (f_suwari_00_08_00 held, then f_suwari_00_08_01 drunk: Studio's 椅子・机 飲む待機 / 飲む), the cup
    // p_o_koi_ei_cup00 on the right hand's item point, no offsets - and drinks standing only from the bottle (f_tachi_00_09
    // / _10): with the bottle's hand the cup went through it. The motion places the item point under the hand as well as
    // the fingers (--muse-probe=gripview: in the hand at (0.067, -0.065, -0.017) turned (348, 275, 290) in the cup's, at
    // (-0.007, -0.050, -0.050) turned (273, 246, 341) in the bottle's), and the arm holds the cup upright where the bottle's
    // arm laid it on its side. So: her body stands as the original's standing drink holds; her right arm - shoulder to
    // fingers, its item point - is the cup's motion's: a copy of the bones from the animator down the arm with an Animator
    // of its own (the cup's controller and states, her height, her speed) plays it beside hers, held and a sip in turn, and
    // each frame after the animation its turns (the item point's place too) are put on hers (Stage.LateTick).
    public class BorrowedArm
    {
        readonly List<Transform> real = new List<Transform>();
        readonly List<Transform> mine = new List<Transform>();
        readonly List<bool> moves = new List<bool>();      // (the item points: placed too; the rest only turned)
        readonly List<Vector3> placeWas = new List<Vector3>();
        readonly List<Quaternion> turnWas = new List<Quaternion>();
        GameObject copy;
        Animator an;
        public string HoldState = "", SipState = "";
        public int Bones { get { return real.Count; } }
        public string Now { get { return an == null ? "" : an.GetCurrentAnimatorStateInfo(0).IsName(SipState) ? SipState : HoldState; } }

        // hands: "R", "L" or "LR" (the original's tea bowl is held in both)
        public static BorrowedArm Make(ChaControl cha, RuntimeAnimatorController rac, string hold, string sip, string hands)
        {
            if (cha == null || cha.animBody == null || rac == null || string.IsNullOrEmpty(hold)) return null;
            var top = cha.animBody.transform;
            var body = cha.objBodyBone.transform;
            var starts = new List<Transform>();
            foreach (var side in new[] { "_R", "_L" })
            {
                if ((hands ?? "R").IndexOf(side[1]) < 0) continue;
                var s = HIK.Find(body, "cf_d_shoulder" + side) ?? HIK.Find(body, "cf_j_shoulder" + side);
                if (s != null) starts.Add(s);
            }
            if (starts.Count == 0) return null;
            var a = new BorrowedArm { HoldState = hold, SipState = sip ?? "" };
            a.copy = new GameObject("MuseStudio_arm");
            a.copy.SetActive(false);
            var made = new Dictionary<Transform, Transform>();
            foreach (var start in starts)
            foreach (var t in start.GetComponentsInChildren<Transform>(true))
            {
                bool point = t.name.StartsWith("k_f_");
                if (!(t.name.StartsWith("cf_j_") || t.name.StartsWith("cf_d_") || point)) continue;
                a.real.Add(t);
                a.mine.Add(CopyOf(t, top, a.copy.transform, made));
                a.moves.Add(point);
                a.placeWas.Add(t.localPosition);
                a.turnWas.Add(t.localRotation);
            }
            a.an = a.copy.AddComponent<Animator>();
            a.an.runtimeAnimatorController = rac;
            a.copy.SetActive(true);
            if (!a.an.HasState(0, Animator.StringToHash(hold))) { a.Destroy(); return null; }
            if (a.SipState.Length > 0 && !a.an.HasState(0, Animator.StringToHash(a.SipState))) a.SipState = "";
            foreach (var p in a.an.parameters)
                if (p.name == "height" && p.type == AnimatorControllerParameterType.Float) a.an.SetFloat("height", cha.GetShapeBodyValue(0));
            a.an.speed = cha.animBody.speed;
            a.an.Play(hold, 0, 0f);
            a.an.Update(0f);
            return a;
        }

        // the bone's copy under the copy's root, by the same path from the animator (its parents copied on the way, each with
        // the real one's local place and turn)
        static Transform CopyOf(Transform t, Transform top, Transform root, Dictionary<Transform, Transform> made)
        {
            Transform c;
            if (made.TryGetValue(t, out c)) return c;
            var parent = t.parent == top || t.parent == null ? root : CopyOf(t.parent, top, root, made);
            c = new GameObject(t.name).transform;
            c.SetParent(parent, false);
            c.localPosition = t.localPosition;
            c.localRotation = t.localRotation;
            c.localScale = t.localScale;
            made[t] = c;
            return c;
        }

        public void Sip() { if (an != null && SipState.Length > 0) an.CrossFadeInFixedTime(SipState, 0.3f, 0, 0f); }

        public void Back() { if (an != null) an.CrossFadeInFixedTime(HoldState, 0.4f, 0, 0f); }

        public bool SipOver
        {
            get
            {
                if (an == null || SipState.Length == 0) return true;
                var si = an.GetCurrentAnimatorStateInfo(0);
                return si.IsName(SipState) && si.normalizedTime >= 0.97f && !an.IsInTransition(0);
            }
        }

        public void Apply(float speed)
        {
            if (an == null) return;
            an.speed = speed;
            for (int i = 0; i < real.Count; i++)
            {
                if (real[i] == null || mine[i] == null) continue;
                real[i].localRotation = mine[i].localRotation;
                if (moves[i]) real[i].localPosition = mine[i].localPosition;
            }
        }

        public void Destroy()
        {
            // (the item points as they were: her motions may not place them)
            for (int i = 0; i < real.Count; i++)
                if (moves[i] && real[i] != null) { real[i].localPosition = placeWas[i]; real[i].localRotation = turnWas[i]; }
            if (copy != null) UnityEngine.Object.Destroy(copy);
            copy = null;
            an = null;
        }
    }
}
