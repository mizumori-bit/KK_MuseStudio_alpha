using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Studio;
using UnityEngine;

namespace MuseStudio
{
    // (development: #70, session 12's note - her head jerks while a play waits at pace 0) --muse-probe=headjit:PLAY,PLAY: each play
    // started at pace 0 where it can, then a few seconds sampled every frame at its end (after the neck look's LateUpdate) in
    // phases that leave one cause out at a time (at 60 frames a second) - as it is; without the keys evened out (KeySmooth: an
    // added play's wait); her gaze at the camera; at your body; the eye / neck patterns not set
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
            // (as a play runs: 60 frames a second, not the check machine's 30 of its screen)
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
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
                foreach (var phase in new[] { "as_is", "keysmooth_off", "gaze_camera", "gaze_body", "eyeneck_off", "sway_off", "neck_anim" })
                {
                    h.GazeAtYourBody.Value = phase == "gaze_camera" ? false : phase == "gaze_body" ? true : gazeWas;
                    pc.EyeNeckOffForProbe = phase == "eyeneck_off" || phase == "neck_anim";
                    pc.SwayOffForProbe = phase == "sway_off";
                    KeySmooth.OffForProbe = phase == "keysmooth_off";
                    if (phase == "neck_anim" && animPtn >= 0) cha.ChangeLookNeckPtn(animPtn);
                    yield return new WaitForSeconds(1.5f);
                    lines.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "KEYSMOOTH {0} {1}: weight {2:F2} bones {3} last turn {4:F4} deg, the partner's weight {5:F2} bones {6}",
                        id, phase, KeySmooth.Weight(cha), KeySmooth.Bones(cha), KeySmooth.LastDeg(cha),
                        h.Stage.Partner != null ? KeySmooth.Weight(h.Stage.Partner.charInfo) : 0f, h.Stage.Partner != null ? KeySmooth.Bones(h.Stage.Partner.charInfo) : 0));
                    yield return SampleHead(id, phase, 8f, csv, calc, flagFields, floatFields);
                }
                h.GazeAtYourBody.Value = gazeWas;
                pc.EyeNeckOffForProbe = pc.SwayOffForProbe = KeySmooth.OffForProbe = false;
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

        // (#70: the owner sees no jerk when Studio itself plays the added play) --muse-probe=studiocmp:PLAY: its states loaded on
        // her the way Studio's own animation list does (OCIChar.LoadAnime of the entry; no play of the app - no eye / neck table,
        // no speed kept, no sway), each size set's (S_ / M_ / L_) wait and loop: their clips (names, weights), the clip's keys
        // (ClipStats), a few seconds on the screen (SampleHead); then the same play started by the app at pace 0
        IEnumerator StudioCompare(string id)
        {
            yield return new WaitForSeconds(1.5f);
            var pc = h.Play;
            var st = h.Stage;
            var oci = st.Heroine;
            var row = pc.Row(id);
            if (row == null) { lines.AppendLine("PLAY " + id + " unknown"); yield return Finish(); yield break; }
            var gc = Json.Arr(row, "female");
            var states = Json.Obj(row, "female_states");
            int g = StudioAnim.Group(Convert.ToInt32(gc[0]), gc.Count > 2 ? gc[2] as string : null), c = Convert.ToInt32(gc[1]);
            lines.AppendLine("PLAY " + id + " group " + g + " category " + c + " her height " + st.Cha.fileBody.shapeValueBody[0].ToString("F2")
                + " sizes " + string.Join(" ", (Json.Arr(row, "sizes") ?? new List<object>()).ConvertAll(x => (string)x).ToArray()));
            // the category's entries (what Studio's list shows)
            Dictionary<int, Dictionary<int, Info.AnimeLoadInfo>> cats;
            Dictionary<int, Info.AnimeLoadInfo> nos;
            if (Singleton<Info>.Instance.dicAnimeLoadInfo.TryGetValue(g, out cats) && cats.TryGetValue(c, out nos))
                foreach (var kv in nos) lines.AppendLine("  ENTRY " + kv.Key + " '" + kv.Value.name + "' clip " + kv.Value.clip + " bundle " + kv.Value.bundlePath + " / " + kv.Value.fileName);
            var csv = new StringBuilder("phase,frame,t,dt,state,norm,statekey,row,rowid,neckptn,ctrlptn,target,motion,head_step,neck_step,headlocal_step,flags\n");
            var sizes = Json.Arr(row, "sizes") ?? new List<object> { "" };
            foreach (var so in sizes)
                foreach (var stName in new[] { "Idle", "WLoop1" })
                {
                    object clipName;
                    if (!states.TryGetValue(stName, out clipName)) continue;
                    string clip = ((string)clipName).Replace("{s}", (string)so);
                    int n = StudioAnim.No(g, c, clip);
                    if (n < 0) { lines.AppendLine("STUDIO " + clip + ": no entry"); continue; }
                    oci.LoadAnime(g, c, n, 0f);
                    oci.animeSpeed = 1f;
                    yield return new WaitForSeconds(1.5f);
                    yield return new WaitForEndOfFrame();
                    var an = st.Cha.animBody;
                    var infos = new List<string>();
                    foreach (var ci in an.GetCurrentAnimatorClipInfo(0)) infos.Add((ci.clip != null ? ci.clip.name : "-") + "@" + ci.weight.ToString("F2"));
                    lines.AppendLine("STUDIO " + clip + " (entry " + n + "): clips " + string.Join(" + ", infos.ToArray()) + " animator speed " + an.speed.ToString("F2")
                        + " pattern " + oci.animePattern.ToString("F2") + " neck ptn " + st.Cha.fileStatus.neckLookPtn + " | keys: " + ClipStats());
                    yield return SampleHead(id, "studio_" + clip, 6f, csv, null, new List<System.Reflection.FieldInfo>(), new List<System.Reflection.FieldInfo>());
                }
            // the same by the app's play
            var spots = pc.Spots(row);
            pc.AnySpotForProbe = true;
            spots = pc.Spots(row);
            string err = spots.Count > 0 ? pc.Begin(id, "probe", spots[0].Id) : "no spot";
            yield return new WaitForSeconds(1.5f);
            yield return new WaitForEndOfFrame();
            if (err.Length == 0)
            {
                var an = st.Cha.animBody;
                var infos = new List<string>();
                foreach (var ci in an.GetCurrentAnimatorClipInfo(0)) infos.Add((ci.clip != null ? ci.clip.name : "-") + "@" + ci.weight.ToString("F2"));
                lines.AppendLine("APP " + id + " size set " + pc.SizeSet + " state " + pc.StateKey + ": clips " + string.Join(" + ", infos.ToArray()) + " animator speed " + an.speed.ToString("F2")
                    + " pattern " + oci.animePattern.ToString("F2") + " neck ptn " + st.Cha.fileStatus.neckLookPtn + " | keys: " + ClipStats());
                yield return SampleHead(id, "app_play", 6f, csv, null, new List<System.Reflection.FieldInfo>(), new List<System.Reflection.FieldInfo>());
                pc.End("probe");
            }
            else lines.AppendLine("APP " + id + ": " + err);
            File.WriteAllText(System.IO.Path.Combine(outDir, "studiocmp_" + id + ".csv"), csv.ToString());
            yield return Finish();
        }

        // (#70: "frames seem skipped") --muse-probe=framepace:PLAY: at 60 frames a second (no vsync, the target 60 - the owner's
        // play, not this PC's 30 when nobody watches), the frame times over 15 s each: her wait loaded as Studio's list does, then
        // the app's play of it at pace 0 - the frames that took long (a frame lost at 60: over 25 ms), when, and the garbage
        // collections in those frames
        IEnumerator FramePace(string id)
        {
            yield return new WaitForSeconds(1.5f);
            int vs = QualitySettings.vSyncCount, tf = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            var pc = h.Play;
            var st = h.Stage;
            var oci = st.Heroine;
            var row = pc.Row(id);
            if (row == null) { lines.AppendLine("PLAY " + id + " unknown"); yield return Finish(); yield break; }
            var gc = Json.Arr(row, "female");
            var states = Json.Obj(row, "female_states");
            int g = StudioAnim.Group(Convert.ToInt32(gc[0]), gc.Count > 2 ? gc[2] as string : null), c = Convert.ToInt32(gc[1]);
            string clip = ((string)states["Idle"]).Replace("{s}", "M_");
            int n = StudioAnim.No(g, c, clip);
            oci.LoadAnime(g, c, n, 0f);
            oci.animeSpeed = 1f;
            yield return new WaitForSeconds(2f);
            yield return Pace("studio_" + clip, 15f);
            pc.AnySpotForProbe = true;
            var spots = pc.Spots(row);
            string err = spots.Count > 0 ? pc.Begin(id, "probe", spots[0].Id) : "no spot";
            yield return new WaitForSeconds(2f);
            if (err.Length == 0) { yield return Pace("app_play", 15f); pc.End("probe"); }
            else lines.AppendLine("APP " + id + ": " + err);
            yield return new WaitForSeconds(1f);
            yield return Pace("app_idle_stage", 10f);
            QualitySettings.vSyncCount = vs;
            Application.targetFrameRate = tf;
            yield return Finish();
        }

        IEnumerator Pace(string phase, float seconds)
        {
            var dts = new List<float>();
            var longs = new List<string>();
            int gc0 = GC.CollectionCount(0), gcLong = 0;
            float t0 = Time.realtimeSinceStartup;
            int last = GC.CollectionCount(0);
            yield return null;
            while (Time.realtimeSinceStartup - t0 < seconds)
            {
                yield return null;
                float dt = Time.unscaledDeltaTime;
                dts.Add(dt);
                int gcNow = GC.CollectionCount(0);
                bool collected = gcNow != last;
                last = gcNow;
                if (dt > 0.025f)
                {
                    if (collected) gcLong++;
                    longs.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}s {1:F0}ms{2}", Time.realtimeSinceStartup - t0, dt * 1000f, collected ? " gc" : ""));
                }
            }
            dts.Sort();
            lines.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "PACE {0}: frames {1} median {2:F1}ms p99 {3:F1}ms max {4:F1}ms | over 25ms {5} (with a collection {6}) | collections {7} | {8}",
                phase, dts.Count, dts[dts.Count / 2] * 1000f, dts[(int)(dts.Count * 0.99f)] * 1000f, dts[dts.Count - 1] * 1000f, longs.Count, gcLong,
                GC.CollectionCount(0) - gc0, string.Join(", ", longs.ToArray())));
        }

        IEnumerator Finish()
        {
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
