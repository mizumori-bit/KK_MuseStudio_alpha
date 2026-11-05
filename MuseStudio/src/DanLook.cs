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
}
