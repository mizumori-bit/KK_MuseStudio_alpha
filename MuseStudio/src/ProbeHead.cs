using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace MuseStudio
{
    // (development: #70, session 12's note - her head jerks while a play waits at pace 0) --muse-probe=headjit:PLAY,PLAY: each play
    // started at pace 0 where it can, then a few seconds sampled every frame at its end (after the neck look's LateUpdate) in
    // phases that leave one cause out at a time - as it is; her gaze at the camera; at your body; the eye / neck patterns not set
    // (HEyeNeck not ticked); the motion's sway off; the neck's pattern an animation one (no look). Per frame: the frame time, the
    // motion's state and time, the row the eye / neck table gave, the neck's pattern and target, the head's and the neck's
    // rotations, the neck look's own flags. Per phase: how unevenly the head turns (its step's change frame to frame, the steps
    // that turn back), the frames that took long, and how often each of those changed. Written to probe.txt / headjit_*.csv
    public partial class Probe
    {
        IEnumerator HeadJitter(string spec)
        {
            yield return new WaitForSeconds(1.5f);
            var st = h.Stage;
            var pc = h.Play;
            var cha = st.Cha;
            lines.AppendLine("SETTING gaze at your body " + h.GazeAtYourBody.Value + " fps target " + Application.targetFrameRate + " vsync " + QualitySettings.vSyncCount);
            // the neck look's types (its pattern that leaves the neck to the motion: ANIMATION)
            int animPtn = -1;
            var ns = cha.neckLookCtrl != null && cha.neckLookCtrl.neckLookScript != null ? cha.neckLookCtrl.neckLookScript.neckTypeStates : null;
            if (ns != null)
                for (int i = 0; i < ns.Length; i++)
                {
                    lines.AppendLine("NECK PTN " + i + ": " + ns[i].lookType + " leap " + ns[i].leapSpeed + " hLimit " + ns[i].hAngleLimit + " vLimit " + ns[i].vAngleLimit + " break " + ns[i].limitBreakCorrectionValue);
                    if (animPtn < 0 && ns[i].lookType.ToString().ToUpperInvariant().Contains("ANIM")) animPtn = i;
                }
            // the neck look's own fields (their names once; the flags and counts sampled)
            var calc = cha.neckLookCtrl != null ? (object)cha.neckLookCtrl.neckLookScript : null;
            var flagFields = new List<FieldInfo>();
            var floatFields = new List<FieldInfo>();
            if (calc != null)
            {
                foreach (var f in calc.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    lines.AppendLine("CALC FIELD " + f.FieldType.Name + " " + f.Name);
                    if (f.FieldType == typeof(bool) || f.FieldType == typeof(int)) flagFields.Add(f);
                    else if (f.FieldType == typeof(float)) floatFields.Add(f);
                }
            }
            foreach (var id in spec.Split(','))
            {
                var row = pc.Row(id);
                if (row == null) { lines.AppendLine("PLAY " + id + " unknown"); continue; }
                string at = null;
                foreach (var key in new List<string>(st.LocationKeys()))
                {
                    if (st.Location != key)
                    {
                        h.MoveTo(key, "probe");
                        float t0 = Time.realtimeSinceStartup;
                        while ((st.Location != key || st.Moving) && Time.realtimeSinceStartup - t0 < 20f) yield return null;
                        yield return new WaitForSeconds(0.6f);
                    }
                    if ((bool)pc.Availability(row)["ok"] && pc.Spots(row).Count > 0) { at = key; break; }
                }
                if (at == null) { lines.AppendLine("PLAY " + id + " no room"); continue; }
                string err = pc.Begin(id, "probe", pc.Spots(row)[0].Id);
                yield return new WaitForSeconds(3f);
                lines.AppendLine("PLAY " + id + " in " + at + " " + (err.Length == 0 ? "started" : err) + " state " + pc.StateKey + " pace " + pc.Pace + " mode " + pc.Mode
                    + " eye/neck row " + (pc.EyeFForProbe != null ? pc.EyeFForProbe.NowBy + ":" + pc.EyeFForProbe.NowKey + " id " + pc.EyeFForProbe.NowId : "-"));
                if (err.Length > 0) continue;
                var csv = new StringBuilder("phase,frame,t,dt,state,norm,statekey,row,rowid,neckptn,ctrlptn,target,motion,head_step,neck_step,headlocal_step,flags\n");
                bool gazeWas = h.GazeAtYourBody.Value;
                foreach (var phase in new[] { "as_is", "gaze_camera", "gaze_body", "eyeneck_off", "sway_off", "neck_anim" })
                {
                    h.GazeAtYourBody.Value = phase == "gaze_camera" ? false : phase == "gaze_body" ? true : gazeWas;
                    pc.EyeNeckOffForProbe = phase == "eyeneck_off" || phase == "neck_anim";
                    pc.SwayOffForProbe = phase == "sway_off";
                    if (phase == "neck_anim" && animPtn >= 0) cha.ChangeLookNeckPtn(animPtn);
                    yield return new WaitForSeconds(1.5f);
                    yield return SampleHead(id, phase, 8f, csv, calc, flagFields, floatFields);
                }
                h.GazeAtYourBody.Value = gazeWas;
                pc.EyeNeckOffForProbe = pc.SwayOffForProbe = false;
                yield return ClipKeys(id);
                File.WriteAllText(System.IO.Path.Combine(outDir, "headjit_" + id + ".csv"), csv.ToString());
                pc.End("probe");
                yield return new WaitForSeconds(1f);
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        static Transform FindBone(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var f = FindBone(t.GetChild(i), name); if (f != null) return f; }
            return null;
        }

        // a rotation's change as a vector (its axis times its angle, degrees)
        static Vector3 Step(Quaternion from, Quaternion to)
        {
            var d = to * Quaternion.Inverse(from);
            float ang;
            Vector3 axis;
            d.ToAngleAxis(out ang, out axis);
            if (ang > 180f) ang -= 360f;
            if (float.IsNaN(axis.x) || float.IsInfinity(axis.x)) return Vector3.zero;
            return axis * ang;
        }

        IEnumerator SampleHead(string id, string phase, float seconds, StringBuilder csv, object calc, List<FieldInfo> flagFields, List<FieldInfo> floatFields)
        {
            var cha = h.Stage.Cha;
            var pc = h.Play;
            var head = FindBone(cha.objBodyBone.transform, "cf_j_head");
            var neck = FindBone(cha.objBodyBone.transform, "cf_j_neck");
            var oci = h.Stage.Heroine;
            var dts = new List<float>();
            var headSteps = new List<Vector3>();
            var neckSteps = new List<Vector3>();
            var localSteps = new List<Vector3>();
            int stateChanges = 0, backJumps = 0, rowChanges = 0, ptnChanges = 0, targetChanges = 0, flagChanges = 0, frames = 0;
            int lastHash = 0, lastPtn = -99;
            float lastNorm = -1f;
            string lastRow = null, lastTarget = null, lastFlags = null;
            Quaternion? hw = null, nl = null, hl = null;
            var flagCounts = new Dictionary<string, int>();
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < seconds)
            {
                yield return new WaitForEndOfFrame();
                frames++;
                float dt = Time.unscaledDeltaTime;
                dts.Add(dt);
                var ai = Blend.Now(cha.animBody);
                int hash = ai.fullPathHash;
                if (lastHash != 0 && hash != lastHash) stateChanges++;
                if (hash == lastHash && lastNorm >= 0f && ai.normalizedTime < lastNorm - 0.01f) backJumps++;
                lastHash = hash;
                lastNorm = ai.normalizedTime;
                var ef = pc.EyeFForProbe;
                string rowKey = ef != null ? ef.NowBy + ":" + ef.NowKey : "-";
                if (lastRow != null && rowKey != lastRow) rowChanges++;
                lastRow = rowKey;
                int ptn = cha.fileStatus.neckLookPtn;
                if (lastPtn != -99 && ptn != lastPtn) ptnChanges++;
                lastPtn = ptn;
                string target = cha.neckLookCtrl != null && cha.neckLookCtrl.target != null ? cha.neckLookCtrl.target.name : "-";
                if (lastTarget != null && target != lastTarget) targetChanges++;
                lastTarget = target;
                var sb = new StringBuilder();
                if (calc != null)
                    foreach (var f in flagFields) sb.Append(f.Name).Append('=').Append(Convert.ToString(f.GetValue(calc))).Append(';');
                string flags = sb.ToString();
                if (lastFlags != null && flags != lastFlags)
                {
                    flagChanges++;
                    // which of them moved
                    var a = lastFlags.Split(';');
                    var b = flags.Split(';');
                    for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) { string k = b[i].Split('=')[0]; int c; flagCounts.TryGetValue(k, out c); flagCounts[k] = c + 1; }
                }
                lastFlags = flags;
                Vector3 hs = Vector3.zero, ns = Vector3.zero, ls = Vector3.zero;
                if (head != null && neck != null)
                {
                    if (hw.HasValue) { hs = Step(hw.Value, head.rotation); ns = Step(nl.Value, neck.localRotation); ls = Step(hl.Value, head.localRotation); }
                    hw = head.rotation;
                    nl = neck.localRotation;
                    hl = head.localRotation;
                    if (frames > 1) { headSteps.Add(hs); neckSteps.Add(ns); localSteps.Add(ls); }
                }
                csv.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2:F3},{3:F4},{4},{5:F3},{6},{7},{8},{9},{10},{11},{12:F3},{13:F3},{14:F3},{15:F3},\"{16}\"",
                    phase, frames, Time.realtimeSinceStartup - t0, dt, hash, ai.normalizedTime, pc.StateKey, rowKey, ef != null ? ef.NowId : -1, ptn,
                    cha.neckLookCtrl != null ? cha.neckLookCtrl.ptnNo : -1, target, oci != null ? oci.animePattern : -1f, hs.magnitude, ns.magnitude, ls.magnitude, flags));
            }
            dts.Sort();
            float med = dts.Count > 0 ? dts[dts.Count / 2] : 0f, max = dts.Count > 0 ? dts[dts.Count - 1] : 0f;
            int hitches = dts.FindAll(d => d > med * 2.5f).Count;
            var fc = new List<string>();
            foreach (var kv in flagCounts) fc.Add(kv.Key + " " + kv.Value);
            lines.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "PHASE {0} {1}: frames {2} dt median {3:F4} max {4:F4} hitches {5} | head {6} | neck(local) {7} | head(local) {8} | state changes {9} time back {10} row changes {11} neck ptn changes {12} target changes {13} flag changes {14} [{15}]",
                id, phase, frames, med, max, hitches, Uneven(headSteps), Uneven(neckSteps), Uneven(localSteps), stateChanges, backJumps, rowChanges, ptnChanges, targetChanges, flagChanges, string.Join(", ", fc.ToArray())));
        }

        // (#70: which plays wait jerkily) --muse-probe=headscan:al|all|ID,ID: every play started at pace 0 (at any spot of the room -
        // the clip does not care), its waiting motion's clip sampled straight at its keys (the animator off for it): the head in
        // the body's space (its turn and its place), the neck and the chest - how evenly each moves key to key (the step's change
        // from one key to the next: its mean, its 95th; the keys where it stops between moving ones). One line a play in
        // headscan.csv - the original's plays are the measure the others are read against
        IEnumerator HeadScan(string spec)
        {
            yield return new WaitForSeconds(1.5f);
            var pc = h.Play;
            pc.AnySpotForProbe = true;
            var rowsAll = Json.Arr(Data.Load("plays.json"), "plays") ?? new List<object>();
            var ids = new List<string>();
            var only = new HashSet<string>(spec.Split(','));
            foreach (var o in rowsAll)
            {
                string id = Json.Str(o, "id", ""), src = Json.Str(o, "source", "original");
                if (spec == "all" || (spec == "al" && src == "al") || only.Contains(id)) ids.Add(id);
            }
            var csv = new StringBuilder("id,name,source,group,result,clip,weight,length,rate,keys,head_step,head_jerk,head_p95,head_stalls,pos_step,pos_jerk,neck_step,neck_jerk,chest_step,chest_jerk\n");
            int done = 0;
            foreach (var id in ids)
            {
                var row = pc.Row(id);
                if (row == null) continue;
                var spots = pc.Spots(row);
                string err = spots.Count > 0 ? pc.Begin(id, "probe", spots[0].Id) : "no spot";
                string head = string.Format("{0},\"{1}\",{2},{3}", id, Json.Str(row, "name", "").Replace("\"", "'"), Json.Str(row, "source", "original"), Json.Str(row, "group", ""));
                if (err.Length > 0) { csv.AppendLine(head + ",\"" + err.Replace("\"", "'") + "\""); continue; }
                yield return new WaitForSeconds(1.2f);
                yield return new WaitForEndOfFrame();
                csv.AppendLine(head + ",ok," + ClipStats());
                pc.End("probe");
                done++;
                yield return null;
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "headscan.csv"), csv.ToString());
            lines.AppendLine("HEADSCAN " + done + " of " + ids.Count + " measured");
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // the waiting clip's numbers (HeadScan): its keys sampled straight, the animator off meanwhile
        string ClipStats()
        {
            var cha = h.Stage.Cha;
            var an = cha.animBody;
            if (an == null) return "no animator";
            AnimationClip clip = null;
            float wmax = -1f;
            foreach (var ci in an.GetCurrentAnimatorClipInfo(0)) if (ci.clip != null && ci.weight > wmax) { wmax = ci.weight; clip = ci.clip; }
            if (clip == null) return "no clip";
            var neck = FindBone(cha.objBodyBone.transform, "cf_j_neck");
            var head = FindBone(cha.objBodyBone.transform, "cf_j_head");
            var chest = FindBone(cha.objBodyBone.transform, "cf_j_spine03");
            if (neck == null || head == null || chest == null) return "no bones";
            float rate = clip.frameRate > 0f ? clip.frameRate : 30f;
            int n = Mathf.Clamp(Mathf.RoundToInt(clip.length * rate), 4, 2000);
            var hq = new List<Quaternion>(); var hp = new List<Vector3>(); var nq = new List<Quaternion>(); var cq = new List<Quaternion>();
            bool was = an.enabled;
            an.enabled = false;
            var root = an.transform;
            for (int i = 0; i <= n; i++)
            {
                clip.SampleAnimation(an.gameObject, clip.length * i / n);
                hq.Add(Quaternion.Inverse(root.rotation) * head.rotation);
                hp.Add(root.InverseTransformPoint(head.position) * 1000f);
                nq.Add(neck.localRotation);
                cq.Add(chest.localRotation);
            }
            an.enabled = was;
            var hs = new List<Vector3>(); var ps = new List<Vector3>(); var ns = new List<Vector3>(); var cs = new List<Vector3>();
            for (int i = 1; i < hq.Count; i++) { hs.Add(Step(hq[i - 1], hq[i])); ps.Add(hp[i] - hp[i - 1]); ns.Add(Step(nq[i - 1], nq[i])); cs.Add(Step(cq[i - 1], cq[i])); }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1:F2},{2:F3},{3},{4},{5},{6},{7},{8}",
                clip.name.Replace(",", " "), wmax, clip.length, rate, n, Even(hs, true), Even(ps, false), Even(ns, false), Even(cs, false));
        }

        // a series' mean step and how unevenly it goes (mean change of the step key to key; with all: its 95th and the keys
        // where it stops between moving ones)
        static string Even(List<Vector3> s, bool all)
        {
            float sum = 0f;
            foreach (var v in s) sum += v.magnitude;
            float mean = sum / Math.Max(1, s.Count);
            var j = new List<float>();
            int stalls = 0;
            for (int i = 1; i < s.Count; i++)
            {
                j.Add((s[i] - s[i - 1]).magnitude);
                if (i + 1 < s.Count && s[i].magnitude < 0.2f * mean && s[i - 1].magnitude > mean && s[i + 1].magnitude > mean) stalls++;
            }
            float jm = 0f;
            foreach (var x in j) jm += x;
            jm /= Math.Max(1, j.Count);
            j.Sort();
            float p95 = j.Count > 0 ? j[Math.Min(j.Count - 1, (int)(j.Count * 0.95f))] : 0f;
            return all ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2:F4},{3}", mean, jm, p95, stalls)
                       : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F4},{1:F4}", mean, jm);
        }

        // the motion's own clip, sampled straight (the animator off for it): its name, length, rate; every 1/240 s the neck's,
        // the head's and the chest's local rotations (headclip_*.csv) - how its keys lie, apart from the frames and the neck look
        IEnumerator ClipKeys(string id)
        {
            var cha = h.Stage.Cha;
            var an = cha.animBody;
            if (an == null) yield break;
            var infos = an.GetCurrentAnimatorClipInfo(0);
            AnimationClip clip = null;
            float wmax = -1f;
            foreach (var ci in infos)
            {
                lines.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "CLIP {0} {1} weight {2:F2} length {3:F3} rate {4} legacy {5} wrap {6}",
                    id, ci.clip != null ? ci.clip.name : "-", ci.weight, ci.clip != null ? ci.clip.length : 0f, ci.clip != null ? ci.clip.frameRate : 0f, ci.clip != null && ci.clip.legacy, ci.clip != null ? ci.clip.wrapMode.ToString() : "-"));
                if (ci.clip != null && ci.weight > wmax) { wmax = ci.weight; clip = ci.clip; }
            }
            if (clip == null) yield break;
            var neck = FindBone(cha.objBodyBone.transform, "cf_j_neck");
            var head = FindBone(cha.objBodyBone.transform, "cf_j_head");
            var chest = FindBone(cha.objBodyBone.transform, "cf_j_spine03");
            if (neck == null || head == null || chest == null) yield break;
            yield return new WaitForEndOfFrame();
            bool was = an.enabled;
            an.enabled = false;
            var csv = new StringBuilder("t,nx,ny,nz,nw,hx,hy,hz,hw,cx,cy,cz,cw\n");
            int n = Mathf.Clamp(Mathf.CeilToInt(clip.length * 240f), 10, 4000);
            for (int i = 0; i <= n; i++)
            {
                float tt = clip.length * i / n;
                clip.SampleAnimation(an.gameObject, tt);
                Quaternion a = neck.localRotation, b = head.localRotation, c = chest.localRotation;
                csv.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F5},{1:F6},{2:F6},{3:F6},{4:F6},{5:F6},{6:F6},{7:F6},{8:F6},{9:F6},{10:F6},{11:F6},{12:F6}",
                    tt, a.x, a.y, a.z, a.w, b.x, b.y, b.z, b.w, c.x, c.y, c.z, c.w));
            }
            an.enabled = was;
            File.WriteAllText(System.IO.Path.Combine(outDir, "headclip_" + id + ".csv"), csv.ToString());
            yield return null;
        }

        // how unevenly it turns: its mean step, the step's change from frame to frame (mean, 95th, max), the steps turning back
        static string Uneven(List<Vector3> steps)
        {
            if (steps.Count < 3) return "-";
            float sum = 0f;
            var jerks = new List<float>();
            int back = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                sum += steps[i].magnitude;
                if (i == 0) continue;
                jerks.Add((steps[i] - steps[i - 1]).magnitude);
                if (steps[i].magnitude > 0.05f && steps[i - 1].magnitude > 0.05f && Vector3.Dot(steps[i], steps[i - 1]) < 0f) back++;
            }
            float jm = 0f;
            foreach (var j in jerks) jm += j;
            jm /= jerks.Count;
            jerks.Sort();
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "step {0:F3} jerk {1:F3} p95 {2:F3} max {3:F3} back {4}",
                sum / steps.Count, jm, jerks[(int)(jerks.Count * 0.95f)], jerks[jerks.Count - 1], back);
        }
    }
}
