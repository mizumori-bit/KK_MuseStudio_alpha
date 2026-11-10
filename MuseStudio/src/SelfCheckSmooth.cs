using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // The keys evened out (KeySmooth; #70, session 12's note 3): an added play's wait at pace 0 - both bodies, in within the
    // fade, the turn laid small, the motion's state and time left as they were (its keys are taken with the animator on);
    // not its loops (pace 1), in again at the next wait; not an original play; nothing after the play
    public partial class SelfCheck
    {
        static readonly string[] SmoothPlays = { "al_sonyu_58", "al_sonyu_33", "al_sonyu_32", "al_sonyu_57" };

        IEnumerator KeySmoothChecks()
        {
            var st = h.Stage;
            var pc = h.Play;
            string id = PlayHere(SmoothPlays);
            if (id == null)
                foreach (var key in new List<string>(st.LocationKeys()))
                {
                    if (st.Location == key) continue;
                    h.MoveTo(key, "check");
                    yield return Until(() => st.Location == key && !st.Moving, 20f);
                    yield return Wait(0.5f);
                    id = PlayHere(SmoothPlays);
                    if (id != null) break;
                }
            if (id == null) { Check("keys: an added play to try (al_sonyu_58 / 33 / 32 / 57)", false, "none can start in any room"); yield break; }
            int taken0 = KeySmooth.Taken;
            string err = pc.Begin(id, "check", pc.Spots(pc.Row(id))[0].Id);
            yield return Wait(0.6f);
            var fem = pc.FemaleCha;
            var male = st.Partner != null ? st.Partner.charInfo : null;
            var an = fem != null ? fem.animBody : null;
            // the state and its time over a second: the same state, the time only going on, her chest moving (the animator
            // still moving the bones)
            int hash0 = an != null ? an.GetCurrentAnimatorStateInfo(0).fullPathHash : 0, hashChanges = 0, timeBack = 0, frames = 0, chestMoved = 0;
            float lastNt = -1f, maxDeg = 0f;
            var chest = fem != null ? Bone(fem.objBodyBone.transform, "cf_j_spine03") : null;
            Quaternion lastChest = chest != null ? chest.localRotation : Quaternion.identity;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 1.2f)
            {
                yield return new WaitForEndOfFrame();
                if (an == null) break;
                frames++;
                var si = an.GetCurrentAnimatorStateInfo(0);
                if (si.fullPathHash != hash0) hashChanges++;
                if (lastNt >= 0f && si.normalizedTime < lastNt - 0.0001f) timeBack++;
                lastNt = si.normalizedTime;
                maxDeg = Mathf.Max(maxDeg, KeySmooth.LastDeg(fem));
                if (chest != null && !Exactly(chest.localRotation, lastChest)) chestMoved++;
                if (chest != null) lastChest = chest.localRotation;
            }
            Check("keys: an added play's wait at pace 0 evens out her keys and his (in, bones moved, the turn laid under 2 degrees)",
                  err.Length == 0 && pc.Pace == 0 && KeySmooth.Weight(fem) >= 0.999f && KeySmooth.Bones(fem) > 0 && maxDeg > 0f && maxDeg < 2f
                  && (male == null || (KeySmooth.Weight(male) >= 0.999f && KeySmooth.Bones(male) > 0)),
                  string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} {1} pace {2} weight {3:F2} bones {4} max {5:F4} his {6:F2}/{7}", id, err, pc.Pace,
                      KeySmooth.Weight(fem), KeySmooth.Bones(fem), maxDeg, KeySmooth.Weight(male), KeySmooth.Bones(male)));
            Check("keys: its motion goes on as it was (the same state, its time never back, her chest moving most frames) while its keys are taken",
                  an != null && hashChanges == 0 && timeBack == 0 && chestMoved * 2 > frames,
                  "changes " + hashChanges + " back " + timeBack + " chest moved " + chestMoved + "/" + frames + " taken " + (KeySmooth.Taken - taken0));
            // its loops: not evened out; back to a wait (in her, InsertIdle): in again
            pc.SetPace(1, "check");
            yield return Wait(1f);
            float atPace1 = KeySmooth.Weight(fem);
            pc.SetPace(0, "check");
            yield return Wait(1.5f);
            Check("keys: not its loops (pace 1: out); back at pace 0 in again",
                  atPace1 <= 0.001f && pc.Pace == 0 && KeySmooth.Weight(fem) >= 0.999f,
                  string.Format(System.Globalization.CultureInfo.InvariantCulture, "pace1 {0:F2} now {1:F2} state {2}", atPace1, KeySmooth.Weight(fem), pc.LastState));
            pc.End("check");
            yield return Wait(0.6f);
            Check("keys: none after the play", KeySmooth.Weight(fem) <= 0.001f && KeySmooth.Weight(male) <= 0.001f,
                  KeySmooth.Weight(fem) + " " + KeySmooth.Weight(male));
            // an original play: not handed over
            string orig = PlayHere(new[] { "sonyu_00", "sonyu_04", "sonyu_01", "sonyu_18" });
            if (orig != null)
            {
                string oerr = pc.Begin(orig, "check", pc.Spots(pc.Row(orig))[0].Id);
                yield return Wait(1f);
                int oFrames = 0, oMoved = 0;
                Quaternion oLast = chest != null ? chest.localRotation : Quaternion.identity;
                float o0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - o0 < 1f)
                {
                    yield return new WaitForEndOfFrame();
                    oFrames++;
                    if (chest != null && !Exactly(chest.localRotation, oLast)) oMoved++;
                    if (chest != null) oLast = chest.localRotation;
                }
                Check("keys: an original play's wait is left as it is (her chest moving most frames after an added play's keys were taken)",
                      oerr.Length == 0 && pc.Pace == 0 && KeySmooth.Weight(pc.FemaleCha) <= 0.001f && oMoved * 2 > oFrames,
                      orig + " " + oerr + " " + KeySmooth.Weight(pc.FemaleCha) + " chest moved " + oMoved + "/" + oFrames);
                pc.End("check");
                yield return Wait(0.6f);
            }
        }

        static bool Exactly(Quaternion a, Quaternion b) { return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w; }

        static Transform Bone(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var f = Bone(t.GetChild(i), name); if (f != null) return f; }
            return null;
        }
    }
}
