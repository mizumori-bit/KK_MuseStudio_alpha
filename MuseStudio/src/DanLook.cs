using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MuseStudio
{
    // The penis aimed as the original H aims it (HSceneProc.lookDan: Lookat_dan with the position's list - paramMale.fileLookAt -
    // read per state of the female's motion), where BetterPenetration does not aim it (Penetration): a play without an aim
    // (the hand, the feet, the breasts ...) and the outside finish (OUT_*: the penis held out in his hand - the user's note,
    // PR #23 (a): it hung down there). Elsewhere the penis as it was, BetterPenetration's.
    //   An insertion (the user's notes of 2026-11-08): the list's row for each state decides who aims (PlayCtl.AimByState) -
    //   BetterPenetration in the penetrating states, this the rest (its row's point, or none: the penis as the motion has it,
    //   the penis's own shape back first: Snap / PutBack). A body receiving its anus: the rows' crotch point is its anus.
    //   One row for both (GPT's review of #100): the row picked (Row) is the row the original's component aims by - set on it
    //   (its own SetInfo) where it would not pick it itself (a mod's motion: its states by other names).
    public class DanLook
    {
        Lookat_dan dan;
        Transform danBase;
        Quaternion rest;
        public string Why = "";          // (checks) none, or why it is not there

        static readonly string[] Outside = { "M_OUT_Start", "M_OUT_Loop", "OUT_A", "OUT_Start", "OUT_Loop", "A_M_OUT_Start", "A_M_OUT_Loop", "A_M_OUT_A", "A_OUT_A" };

        static readonly MethodInfo SetInfoM = HarmonyLib.AccessTools.Method(typeof(Lookat_dan), "SetInfo");
        static bool warned;
        readonly List<Transform> chain = new List<Transform>();
        readonly List<Vector3> chainPos = new List<Vector3>(), chainScale = new List<Vector3>();
        readonly List<Quaternion> chainRot = new List<Quaternion>();
        public string AnusPoint = "";     // (the point the rows' crotch point became; "" none)

        public bool Active { get { return dan != null; } }
        public bool On { get { return dan != null && dan.enabled; } }
        public string Held { get { return dan != null ? dan.strPlayMotion : ""; } }      // (checks: the row the component aims by)

        public void Start(ChaControl male, ChaControl female, HSceneProc.AnimationListInfo info, bool anus = false)
        {
            Stop();
            Snap(male);
            if (male == null || female == null || info == null || info.paramMale == null || string.IsNullOrEmpty(info.paramMale.fileLookAt))
            {
                Why = "no list";
                return;
            }
            var go = new GameObject("MuseStudio dan look");
            dan = go.AddComponent<Lookat_dan>();
            dan.enabled = false;
            dan.SetMale(male);
            dan.SetFemale(new[] { female, female });           // (a two-body play: its lists name the first)
            if (!dan.LoadList("h/list/", info.paramMale.fileLookAt))        // (HSceneProc.ChangeAnimator: the lists' folder)
            {
                Stop();
                Why = "list not read: " + info.paramMale.fileLookAt;
                return;
            }
            var baseObj = HarmonyLib.AccessTools.Field(typeof(Lookat_dan), "objDanBase").GetValue(dan) as GameObject;     // (the original's own)
            danBase = baseObj != null ? baseObj.transform : null;
            if (danBase != null) rest = danBase.localRotation;
            // a body receiving its anus (your male body: its insertions all the anus - the user's note): the crotch point the
            // rows aim at is its anus (the point the original's anal rows name, else the anus bone)
            AnusPoint = "";
            if (anus && female.objBodyBone != null)
            {
                AnusPoint = HIK.Find(female.objBodyBone.transform, "k_f_ana_00") != null ? "k_f_ana_00" : "cf_j_ana";
                foreach (var l in dan.lstLookAt) if (l.strLookAtNull == "k_f_kokan_00") l.strLookAtNull = AnusPoint;
            }
            // no row till one is found (LoadList set its first row, its point taken before the anus took the crotch's place):
            // the penis as the motion has it, as the play's own aim has no row then (PlayCtl.AimByState)
            Apply(new Lookat_dan.MotionLookAtList { strMotion = "", strLookAtNull = "" });
            Why = "";
        }

        // the list's row for the state now, and the row the original's aim goes by: the female part's state (as Lookat_dan
        // picks it), else the state the play asked for by the list's name of it (asked: PlayCtl.StateKey - an anal play's A_;
        // a motion of a mod whose states go by other names: set on the component, which would not pick it); else the row the
        // component holds (the last found); null: none yet (the penis as the motion has it)
        public Lookat_dan.MotionLookAtList Row(string asked, ChaControl female)
        {
            if (dan == null) return null;
            Lookat_dan.MotionLookAtList hit = null;
            if (female != null && female.animBody != null)
            {
                var st = female.animBody.GetCurrentAnimatorStateInfo(0);
                hit = dan.lstLookAt.Find(l => st.IsName(l.strMotion));
            }
            if (hit == null && !string.IsNullOrEmpty(asked)) hit = dan.lstLookAt.Find(l => l.strMotion == asked);
            if (hit == null && !string.IsNullOrEmpty(dan.strPlayMotion)) hit = dan.lstLookAt.Find(l => l.strMotion == dan.strPlayMotion);
            if (hit != null && dan.strPlayMotion != hit.strMotion) Apply(hit);
            return hit;
        }

        // the row set on the original's component (Lookat_dan.SetInfo: its point, the tip stuck or not, its way of aiming)
        void Apply(Lookat_dan.MotionLookAtList row)
        {
            if (dan == null) return;
            try
            {
                if (SetInfoM == null) throw new System.MissingMethodException("Lookat_dan.SetInfo");
                SetInfoM.Invoke(dan, new object[] { row });
            }
            catch (System.Exception e)
            {
                if (!warned) MuseStudioPlugin.Log.LogWarning("MuseStudio: the original's penis aim: " + e.Message);
                warned = true;
            }
        }

        // the penis's own shape at the play's start (its bones as they are before anything aims them), put back when
        // BetterPenetration lets it go - it leaves the bones as it bent them
        void Snap(ChaControl male)
        {
            chain.Clear(); chainPos.Clear(); chainRot.Clear(); chainScale.Clear();
            if (male == null || male.objBodyBone == null) return;
            var root = HIK.Find(male.objBodyBone.transform, "cm_J_dan100_00");
            if (root == null) return;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                chain.Add(t);
                chainPos.Add(t.localPosition);
                chainRot.Add(t.localRotation);
                chainScale.Add(t.localScale);
            }
        }

        public void PutBack()
        {
            for (int i = 0; i < chain.Count; i++)
            {
                if (chain[i] == null) continue;
                chain[i].localPosition = chainPos[i];
                chain[i].localRotation = chainRot[i];
                chain[i].localScale = chainScale[i];
            }
        }

        // every frame of a play: on where BetterPenetration has no aim (aimed: the play's own) or she is in the outside finish
        public void Tick(bool aimed, ChaControl female)
        {
            if (dan == null) return;
            bool want = !aimed || IsOutside(female);
            if (want == dan.enabled) return;
            dan.enabled = want;
            if (!want && danBase != null) danBase.localRotation = rest;
        }

        public static bool IsOutside(ChaControl female)
        {
            if (female == null || female.animBody == null) return false;
            var st = female.animBody.GetCurrentAnimatorStateInfo(0);
            foreach (var n in Outside) if (st.IsName(n)) return true;
            return false;
        }

        public void Stop()
        {
            if (dan != null)
            {
                if (danBase != null) danBase.localRotation = rest;
                try { dan.Release(); } catch (System.Exception) { }
                Object.Destroy(dan.gameObject);
            }
            dan = null;
            danBase = null;
            AnusPoint = "";
        }
    }

    // The penis of the body in the female part (the user's male body in a reversed play, her futanari penis when she receives):
    // nobody aims it - the original has no such body - and as the motion leaves it, it points out of the crotch into the other
    // body (a cowgirl's rider sits on the other's crotch: its penis ran 20 cm into her belly, so it looked missing - PR #12 (8),
    // the user's report). Laid up along its own belly instead (most of the way from where it points to the body's spine), each
    // frame right before the cameras draw (after the animation, the IK and BetterPenetration); put back when the play ends.
    public class DanRest
    {
        Transform root, kokan, spine;
        Quaternion rest;
        bool hooked;
        public bool On { get { return root != null; } }
        public float Up = 0.8f;           // (how far towards the spine: 0 as it was, 1 along the spine)

        public void Start(ChaControl cha)
        {
            Stop();
            if (cha == null || cha.objBodyBone == null) return;
            root = HIK.Find(cha.objBodyBone.transform, "cm_J_dan100_00");
            kokan = HIK.Find(cha.objBodyBone.transform, "cf_j_kokan") ?? HIK.Find(cha.objBodyBone.transform, "cf_j_waist02");
            spine = HIK.Find(cha.objBodyBone.transform, "cf_j_spine02");
            if (root == null || kokan == null || spine == null) { root = null; return; }
            rest = root.localRotation;
            if (!hooked) { Camera.onPreCull += PreCull; hooked = true; }
        }

        public void Stop()
        {
            if (root != null) root.localRotation = rest;
            root = null;
            if (hooked) { Camera.onPreCull -= PreCull; hooked = false; }
        }

        void PreCull(Camera cam)
        {
            if (root == null) return;
            if (!root.gameObject.activeInHierarchy) return;
            root.localRotation = rest;
            var tip = root.childCount > 0 ? Deepest(root) : null;
            if (tip == null) return;
            Vector3 now = (tip.position - root.position).normalized, up = (spine.position - kokan.position).normalized;
            if (now.sqrMagnitude < 0.5f || up.sqrMagnitude < 0.5f) return;
            Vector3 want = Vector3.Slerp(now, up, Up);
            root.rotation = Quaternion.FromToRotation(now, want) * root.rotation;
        }

        public Vector3 DirForCheck { get { var tip = root != null ? Deepest(root) : null; return tip != null ? (tip.position - root.position).normalized : Vector3.zero; } }
        public Vector3 UpForCheck { get { return root != null ? (spine.position - kokan.position).normalized : Vector3.zero; } }

        static Transform Deepest(Transform t)
        {
            while (t.childCount > 0)
            {
                Transform next = null;
                for (int i = 0; i < t.childCount; i++) if (t.GetChild(i).name.StartsWith("cm_J_dan")) { next = t.GetChild(i); break; }
                if (next == null) break;
                t = next;
            }
            return t;
        }
    }
}
