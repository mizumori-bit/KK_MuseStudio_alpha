using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // Where she (or the partner) looks during a play: the original's HMotionEyeNeck (its eye / neck patterns of h/list
    // eyeneckbase and their targets: the camera, the partner's head / genitals, her own crotch, a direction), driven like
    // HMotionEyeNeckFemale / Male over the position's list (h/list <param.fileMotionNeck>: per state the pattern id - the
    // female's per experience, the app's column from plays.json eye_neck.experience - and the neck / face bending ranges).
    // The original's components come from the H scene; this one is made on the stage with the targets' bone names the H
    // scene gives them. In aibu (no partner body) the partner's targets are the camera. At the end the look and the ranges
    // the character had come back. Not used: the voice's patterns (no voices), the config's "look at the camera", the male
    // rows' face.
    public class HEyeNeck : HMotionEyeNeck
    {
        class Row { public int[] Ids = { -1, -1, -1, -1 }; public int Id = -1; public bool CfgEye, CfgNeck; public float[] Neck = new float[4], Face = new float[4]; }
        readonly Dictionary<string, Row> rows = new Dictionary<string, Row>();
        bool male;
        int experience = 2;
        float[][] backup;

        // the one who looks (her / the partner), the partner (null: aibu - the camera stands in), the position's list
        // the H scene's flags the original reads (the play's mode: in aibu the partner's targets are the camera); made on an
        // inactive object, so it never runs
        static HFlag hflag;

        public bool Begin(ChaControl who, ChaControl other, bool isMale, string file, int exp, int mode)
        {
            End();
            if (hflag == null)
            {
                var go = new GameObject("MuseStudio HFlag");
                go.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(go);
                hflag = go.AddComponent<HFlag>();
            }
            hflag.mode = (HFlag.EMode)mode;
            flags = hflag;
            chara = who;
            partner = other;
            male = isMale;
            experience = Mathf.Clamp(exp, 0, 3);
            namePartnerHead = "cf_j_head";
            namePartnerGenital = isMale ? "cf_j_kokan" : "cm_J_dan101_00";
            nameGenital = "cf_j_kokan";
            LoadEyeNeckPtn();
            SetPartner(other != null ? other.objBodyBone : null);
            if (other == null && Camera.main != null) objPartnerHead = objPartnerKokan = Camera.main.gameObject;
            objKokan = null;
            foreach (var t in who.objBodyBone.GetComponentsInChildren<Transform>(true)) if (t.name == nameGenital) { objKokan = t.gameObject; break; }
            rows.Clear();
            last = null;
            if (string.IsNullOrEmpty(file)) return false;
            string text = GlobalMethod.LoadAllListText("h/list/", file, null);
            if (string.IsNullOrEmpty(text)) return false;
            string[,] a;
            GlobalMethod.GetListString(text, out a);
            for (int i = 0; i < a.GetLength(0); i++)
            {
                string anim = a[i, 0];
                if (string.IsNullOrEmpty(anim)) continue;
                var r = new Row();
                int n = 1;
                try
                {
                    if (!isMale)
                    {
                        r.CfgEye = a[i, n++] == "1";
                        r.CfgNeck = a[i, n++] == "1";
                        for (int k = 0; k < 4; k++) r.Ids[k] = int.Parse(a[i, n++]);
                        n++;                                  // idEyeNeckNoVoice (the voice's: not used)
                    }
                    else
                    {
                        n += 6;                               // the male rows' face (not used)
                        r.Id = int.Parse(a[i, n++]);
                    }
                    for (int k = 0; k < 4; k++) r.Neck[k] = float.Parse(a[i, n++]);
                    for (int k = 0; k < 4; k++) r.Face[k] = float.Parse(a[i, n++]);
                }
                catch (Exception) { continue; }
                rows[anim] = r;
            }
            Backup();
            return rows.Count > 0;
        }

        public bool On { get { return chara != null && rows.Count > 0; } }

        // every frame: the current state's pattern and ranges
        public void Tick()
        {
            if (!On || chara.animBody == null || Camera.main == null) return;
            var ai = Blend.Now(chara.animBody);
            Row now = null;
            foreach (var kv in rows) if (ai.IsName(kv.Key)) { now = kv.Value; NowKey = kv.Key; NowBy = "state"; break; }
            // (an AnimationLoader play: its states are named otherwise - WLoop1, SLoop1 - and none matched the borrowed list; the
            // play's own state name finds the row: the user's note of 2026-11-04, 133 AL plays at the camera)
            if (now == null)
            {
                Row byName;
                string key = MuseStudioPlugin.Instance.Play.StateKey;
                if (!string.IsNullOrEmpty(key) && rows.TryGetValue(key, out byName)) { now = byName; NowKey = key; NowBy = "name"; }
            }
            // (a state the list has not - a climax's step, a finish's: the last row held, so nothing else - a face's eyes, Studio's
            // pattern change back to the camera - takes the gaze; the user's note of 2026-11-04: at the camera in the climax)
            if (now == null) { now = last; NowBy = "held"; }
            // (nothing matched yet: the list's first row - the position's own gaze, never Studio's camera)
            if (now == null) foreach (var kv in rows) { now = kv.Value; break; }
            if (now == null) return;
            last = now;
            int id = male ? now.Id : now.Ids[experience];
            NowId = id;
            try
            {
                // (in a match the users only watch; else by the setting - default at your body - the original's patterns at the
                // camera look at the partner's head instead; your eyes' camera (pov) is the same either way)
                var host = MuseStudioPlugin.Instance;
                bool atBody = host.Match.Active || (host.GazeAtYourBody.Value && host.Play.View != "pov");
                var cam = atBody && objPartnerHead != null && objPartnerHead != Camera.main.gameObject ? objPartnerHead : Camera.main.gameObject;
                if (id >= 0) SetEyeNeckPtn(id, cam, now.CfgEye, now.CfgNeck);
                Range(now.Neck, now.Face);
            }
            catch (Exception e)
            {
                MuseStudioPlugin.Log.LogWarning("MuseStudio: eyes / neck: " + e.Message + " (stopped for this play)");
                rows.Clear();
            }
        }

        Row last;
        public string NowKey = "", NowBy = "";     // (development: Probe's headjit) the row now, and how it was found - the motion's state, the play's name, held (no new strings a frame)
        public int NowId = -1;

        NeckTypeStateVer2[] States { get { return chara != null && chara.neckLookCtrl != null && chara.neckLookCtrl.neckLookScript != null ? chara.neckLookCtrl.neckLookScript.neckTypeStates : null; } }

        // the neck look's patterns 5 and 6 (HMotionEyeNeckFemale.SetRangeInfo)
        void Range(float[] neck, float[] face)
        {
            Pattern(5, neck, face);
            Pattern(6, neck, face);
        }

        void Pattern(int p, float[] neck, float[] face)
        {
            var st = States;
            if (st == null || p >= st.Length) return;
            var s = st[p];
            s.aParam[0].upBendingAngle = neck[0]; s.aParam[0].downBendingAngle = neck[1]; s.aParam[0].minBendingAngle = neck[2]; s.aParam[0].maxBendingAngle = neck[3];
            s.aParam[1].upBendingAngle = face[0]; s.aParam[1].downBendingAngle = face[1]; s.aParam[1].minBendingAngle = face[2]; s.aParam[1].maxBendingAngle = face[3];
        }

        void Backup()
        {
            var st = States;
            backup = null;
            if (st == null) return;
            backup = new float[2][];
            for (int i = 0; i < 2; i++)
            {
                int p = 5 + i;
                if (p >= st.Length) continue;
                var s = st[p];
                backup[i] = new[] { s.aParam[0].upBendingAngle, s.aParam[0].downBendingAngle, s.aParam[0].minBendingAngle, s.aParam[0].maxBendingAngle,
                                    s.aParam[1].upBendingAngle, s.aParam[1].downBendingAngle, s.aParam[1].minBendingAngle, s.aParam[1].maxBendingAngle };
            }
        }

        // the ranges as they were (the look itself: the caller's)
        public void End()
        {
            var st = States;
            if (st != null && backup != null)
                for (int i = 0; i < 2; i++)
                    if (backup[i] != null)
                        Pattern(5 + i, new[] { backup[i][0], backup[i][1], backup[i][2], backup[i][3] }, new[] { backup[i][4], backup[i][5], backup[i][6], backup[i][7] });
            backup = null;
            rows.Clear();
            chara = null;
            partner = null;
        }
    }
}
