using UnityEngine;

namespace MuseStudio
{
    // The penis aimed as the original H aims it (HSceneProc.lookDan: Lookat_dan with the position's list - paramMale.fileLookAt -
    // read per state of the female's motion), where BetterPenetration does not aim it (Penetration): a play without an aim
    // (the hand, the feet, the breasts ...) and the outside finish (OUT_*: the penis held out in his hand - the user's note,
    // PR #23 (a): it hung down there). Elsewhere the penis as it was, BetterPenetration's.
    public class DanLook
    {
        Lookat_dan dan;
        Transform danBase;
        Quaternion rest;
        public string Why = "";          // (checks) none, or why it is not there

        static readonly string[] Outside = { "M_OUT_Start", "M_OUT_Loop", "OUT_A", "OUT_Start", "OUT_Loop", "A_M_OUT_Start", "A_M_OUT_Loop", "A_M_OUT_A", "A_OUT_A" };

        public bool Active { get { return dan != null; } }
        public bool On { get { return dan != null && dan.enabled; } }

        public void Start(ChaControl male, ChaControl female, HSceneProc.AnimationListInfo info)
        {
            Stop();
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
            Why = "";
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
