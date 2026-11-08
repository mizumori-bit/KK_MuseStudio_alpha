using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace MuseStudio
{
    // (development) --muse-check=PATH --muse-probe=ROOM: instead of the self checks (with their isolation: the check's own
    // bridge, moods, personas, no settings written), look at a kept room - its meshes by size (room coordinates), pictures
    // from above and from its corners - and her accessories (slot, parent, the original's hide setting, shown), then quit.
    public class Probe
    {
        readonly MuseStudioPlugin h;
        readonly string outDir, room;
        readonly StringBuilder lines = new StringBuilder();

        public Probe(MuseStudioPlugin host, string checkPath, string room)
        {
            h = host;
            outDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(checkPath));
            this.room = room;
        }

        public IEnumerator Run()
        {
            if (room.StartsWith("al:")) { yield return AlSizes(room.Substring(3)); yield break; }
            if (room.StartsWith("speed:")) { yield return Speeds(room.Substring(6)); yield break; }
            if (room.StartsWith("eyeneck:")) { yield return EyeNecks(room.Substring(8)); yield break; }
            if (room == "icons") { yield return Icons(); yield break; }
            if (room == "poses") { yield return Poses(); yield break; }
            if (room == "aibufeet") { yield return AibuFeet(); yield break; }
            if (room == "aibukokan") { yield return AibuKokan(); yield break; }
            if (room == "aibuheight") { yield return AibuHeight(); yield break; }
            if (room.StartsWith("aibuik")) { yield return AibuIk(room.Length > 7 ? room.Substring(7) : "aibu_00"); yield break; }
            if (room == "aibulegs") { yield return AibuLegs(); yield break; }
            if (room == "realtouch") { yield return RealTouch(); yield break; }
            if (room.StartsWith("aibufloor")) { yield return AibuFloor(room.Length > 10 ? room.Substring(10) : "aibu_00"); yield break; }
            if (room.StartsWith("ikdata:")) { yield return IkData(room.Substring(7)); yield break; }
            if (room.StartsWith("touchenable:")) { yield return TouchEnable(room.Substring(12)); yield break; }
            if (room == "shoes") { yield return ShoesTry(); yield break; }
            if (room == "drinks") { yield return DrinksTry(); yield break; }
            if (room == "pushup") { yield return PushupTry(); yield break; }
            if (room == "mouth") { yield return MouthTry(); yield break; }
            if (room == "mouth2") { yield return Mouth2Try(); yield break; }
            if (room.StartsWith("maps")) { yield return MapsTry(room.Length > 5 ? room.Substring(5) : "8"); yield break; }
            if (room.StartsWith("aibuuser:")) { yield return AibuUser(room.Substring(9)); yield break; }
            if (room == "gropehand") { yield return GropeHand(); yield break; }
            if (room == "ikfight") { yield return IkFight(); yield break; }
            if (room == "reversed") { yield return ReversedPenis(); yield break; }
            if (room.StartsWith("hold:")) { yield return Hold(float.Parse(room.Substring(5), System.Globalization.CultureInfo.InvariantCulture)); yield break; }
            if (room.StartsWith("photosany:")) { h.Play.AnySpotForProbe = true; yield return Photos(room.Substring(10)); yield break; }
            if (room.StartsWith("photos")) { yield return Photos(room.Length > 7 ? room.Substring(7) : ""); yield break; }
            if (room == "screen") { yield return ScreenTry(); yield break; }
            if (room == "toilet") { yield return ToiletTry(); yield break; }
            if (room == "cardlinks") { yield return CardLinks(); yield break; }
            if (room.StartsWith("playcams:")) { yield return PlayCams(room.Substring(9)); yield break; }
            if (room == "gazes") { yield return Gazes(); yield break; }
            if (room.StartsWith("camall")) { yield return CamAll(room.Length > 7 ? room.Substring(7) : ""); yield break; }
            if (room.StartsWith("hse:")) { yield return HSeList(room.Substring(4)); yield break; }
            if (room.StartsWith("plays:")) { yield return PlayShots(room.Substring(6)); yield break; }
            var st = h.Stage;
            yield return new WaitForSeconds(1.5f);
            // her accessories
            var cha = st.Cha;
            var parts = cha.nowCoordinate.accessory.parts;
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                if (p.type == 120) continue;      // (none)
                string obj = cha.objAccessory != null && i < cha.objAccessory.Length && cha.objAccessory[i] != null ? cha.objAccessory[i].name : "-";
                lines.AppendLine(string.Format("ACC {0}: type {1} id {2} parent {3} hideCategory {4} shown {5} obj {6}", i, p.type, p.id, p.parentKey, p.hideCategory,
                    cha.fileStatus.showAccessory[i], obj));
            }
            var asm = new List<string>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { string n = a.GetName().Name; if (n.Contains("Accessor") || n.Contains("ANAL")) asm.Add(n); }
            lines.AppendLine("ACC plugins: " + string.Join(", ", asm.ToArray()));
            // the room
            if (st.Reachable(room)) h.MoveTo(room, "probe");
            else h.StartCoroutine(st.SetLocation(room));          // (a room not kept: the match's - loaded directly)
            float t0 = Time.realtimeSinceStartup;
            while ((st.Location != room || st.Moving) && Time.realtimeSinceStartup - t0 < 30f) yield return null;
            yield return new WaitForSeconds(1f);
            Vector3 off = h.Rooms.OffsetOf(room);
            var rows = new List<KeyValuePair<float, string>>();
            Bounds all = new Bounds(off, Vector3.zero);
            bool first = true;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || Vector3.Distance(r.bounds.center, off) > 40f) continue;
                if (r.GetComponentInParent<ChaControl>() != null) continue;
                var b = r.bounds;
                if (first) { all = b; first = false; } else all.Encapsulate(b);
                rows.Add(new KeyValuePair<float, string>(b.size.x * b.size.z, string.Format("MESH {0} center {1} size {2}", PathOf(r.transform), (b.center - off).ToString("F2"), b.size.ToString("F2"))));
            }
            rows.Sort((ra, rb) => rb.Key.CompareTo(ra.Key));
            lines.AppendLine("ROOM " + room + " offset " + off + " bounds center " + (all.center - off).ToString("F2") + " size " + all.size.ToString("F2"));
            foreach (var r in rows) lines.AppendLine(r.Value);
            var hp = Json.Obj(Json.Obj(Data.Load("locations.json"), "locations"), room);
            // the inside: the extent of the room's H points (they stand at its walls), not the map's sky
            bool any = false;
            foreach (var sp in HData.Points((int)Json.Num(hp, "h_map", -1)))
            {
                lines.AppendLine(string.Format("HPOINT {0} at {1} yaw {2:F0}", sp.Id, sp.Pos.ToString("F2"), sp.Rot.eulerAngles.y));
                var at = sp.Pos + off;
                if (!any) { all = new Bounds(at, Vector3.zero); any = true; } else all.Encapsulate(at);
            }
            if (any) { all.Expand(new Vector3(0.2f, 0f, 0.2f)); all.Encapsulate(all.center + Vector3.up * 2.6f); }
            // pictures: from above, and from the four sides at eye height
            Vector3 c = all.center;
            float top = all.max.y;
            yield return Shot(new Vector3(c.x, top - 0.05f, c.z - 0.01f), new Vector3(c.x, all.min.y, c.z), 100f, "probe_" + room + "_top.png");
            float y = all.min.y + 1.6f;
            var ex = all.extents;
            yield return Shot(new Vector3(c.x, y, all.max.z - 0.15f), new Vector3(c.x, all.min.y + 0.6f, all.min.z), 75f, "probe_" + room + "_from_zmax.png");
            yield return Shot(new Vector3(c.x, y, all.min.z + 0.15f), new Vector3(c.x, all.min.y + 0.6f, all.max.z), 75f, "probe_" + room + "_from_zmin.png");
            yield return Shot(new Vector3(all.max.x - 0.15f, y, c.z), new Vector3(all.min.x, all.min.y + 0.6f, c.z), 75f, "probe_" + room + "_from_xmax.png");
            yield return Shot(new Vector3(all.min.x + 0.15f, y, c.z), new Vector3(all.max.x, all.min.y + 0.6f, c.z), 75f, "probe_" + room + "_from_xmin.png");
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // --muse-probe=al:<female group>,<male group>,<category>: the AnimationLoader motion's S_ / M_ / L_ sets on her and the
        // partner (a play started for the partner's body), measured (her hips, his hips) and pictured
        // plays started one by one (at their first fitting spot), a picture of each from her side - what the motion is
        IEnumerator PlayShots(string ids)
        {
            yield return new WaitForSeconds(1.5f);
            var pc = h.Play;
            foreach (var id in ids.Split(','))
            {
                var row = pc.Row(id);
                if (row == null) { lines.AppendLine(id + " unknown"); continue; }
                var spots = pc.Spots(row);
                if (spots.Count == 0)
                {
                    // (no spot here: another kept room that has one)
                    foreach (var room in h.Stage.LocationKeys())
                    {
                        if (room == h.Stage.Location) continue;
                        yield return h.Stage.SetLocation(room);
                        spots = pc.Spots(row);
                        if (spots.Count > 0) break;
                    }
                }
                string err = spots.Count > 0 ? pc.Begin(id, "probe", spots[0].Id) : "no spot";
                lines.AppendLine(id + " " + Json.Str(row, "name") + " at " + (spots.Count > 0 ? spots[0].Name : "-") + " " + err);
                if (err.Length > 0) continue;
                pc.Apply(new Dictionary<string, object> { { "pace", 1 } });
                yield return new WaitForSeconds(2.5f);
                var st = h.Stage;
                var c = st.Cha.objBodyBone.transform.position + Vector3.up * 0.6f;
                var side = st.Cha.transform.right;
                yield return Shot(c + side * 2.4f + Vector3.up * 0.4f, c - Vector3.up * 0.2f, 45f, "play_" + id + ".png");
                yield return Shot(c + st.Cha.transform.forward * 2.4f + Vector3.up * 0.4f, c - Vector3.up * 0.2f, 45f, "play_" + id + "_front.png");
                pc.End("probe");
                yield return new WaitForSeconds(0.5f);
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // the standing aibu: her crotch's drag (kokan_X / _Y on her animator) to its ends - her hips and feet, with the motion IK
        // and without; pictures
        IEnumerator AibuFeet()
        {
            yield return new WaitForSeconds(1.5f);
            var st = h.Stage;
            var pc = h.Play;
            string err = pc.Begin("aibu_00", "probe", "here");
            lines.AppendLine("begin aibu_00 " + err + " ik " + pc.IK.On);
            yield return new WaitForSeconds(2.0f);
            var an = st.Cha.animBody;
            Transform hip = null, fl = null, fr = null;
            foreach (var t in st.Cha.objBodyBone.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "cf_j_hips") hip = t;
                if (t.name == "cf_j_foot_L") fl = t;
                if (t.name == "cf_j_foot_R") fr = t;
            }
            var fb = an.GetComponent<RootMotion.FinalIK.FullBodyBipedIK>();
            var names = new List<string>();
            foreach (var prm in an.parameters) names.Add(prm.name + ":" + prm.type);
            lines.AppendLine("params " + string.Join(",", names.ToArray()));
            var eff = fb != null ? new[] { fb.solver.leftFootEffector, fb.solver.rightFootEffector } : null;
            if (eff != null) lines.AppendLine("fbik " + fb.enabled + " feet w " + eff[0].positionWeight + "/" + eff[1].positionWeight + " targets " + (eff[0].target != null ? eff[0].target.name + " under " + eff[0].target.parent.name : "none"));
            var c = st.Cha.objBodyBone.transform.position + Vector3.up * 0.7f;
            var side = st.Cha.transform.right;
            foreach (var pass in new[] { "ik", "noik" })
            {
                if (pass == "noik" && fb != null) fb.enabled = false;
                foreach (var xy in new[] { new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(1f, 0f) })
                {
                    an.SetFloat("kokan_X", xy.x);
                    an.SetFloat("kokan_Y", xy.y);
                    yield return new WaitForSeconds(0.8f);
                    lines.AppendLine(string.Format("{0} xy {1:F1},{2:F1} hip {3} footL {4} footR {5}", pass, xy.x, xy.y, hip != null ? hip.position.ToString("F3") : "-",
                        fl != null ? fl.position.ToString("F3") : "-", fr != null ? fr.position.ToString("F3") : "-"));
                    if (xy.x != 0.5f) yield return Shot(c + side * 2.2f, c - Vector3.up * 0.3f, 45f, string.Format("aibu_{0}_{1:F0}{2:F0}.png", pass, xy.x, xy.y));
                }
            }
            if (fb != null) fb.enabled = true;
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // the standing aibu, her crotch caressed as the user does it (a press held, then let go; the trial run of 2026-11-06:
        // "from the hips down she hangs") - her hips, knees and feet against the floor before, at the touch's start, while it
        // goes on and after; without the motion IK (as now) and with the original's (HActionBase.SetPlay: every state's
        // MotionIK.Calc, the female's data for every mode); her IK data's text; pictures
        IEnumerator AibuKokan()
        {
            yield return new WaitForSeconds(1.5f);
            var st = h.Stage;
            var pc = h.Play;
            var tc = h.Touch;
            string ikFile = "";
            h.Hands.MouseOffForCheck = true;
            foreach (var pass in new[] { "noik" })
            {
                string err = pc.Begin("aibu_00", "probe", "here");
                ikFile = pc.HInfo != null ? pc.HInfo.paramFemale.path.file : "";
                lines.AppendLine(pass + " begin " + err + " female ik file '" + ikFile + "' male '" + (pc.HInfo != null ? pc.HInfo.paramMale.path.file : "") + "'");
                yield return new WaitForSeconds(1.5f);
                yield return new WaitForEndOfFrame();
                KokanMeasure(pass, "idle");
                yield return KokanShots(pass + "_idle");
                tc.PressForCheck("crotch", "");
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 1.5f) yield return null;
                // a stroke as a hand does it: the mouse back and forth (±300 px across, ±150 px up and down, a round a second) for
                // 6 s through the drag's own mapping (HandCtrl.DragAction's, AibuHands.Drag) - the drag's X / Y, her toes' height
                // and her feet's slide over it
                h.Hands.DragToForCheck(new Vector2(0.5f, 0.5f));
                float ts = Time.realtimeSinceStartup, prevT = 0f;
                float lowL = 9f, lowR = 9f, highL = -9f, highR = -9f, minX = 9f, maxX = -9f, minY = 9f, maxY = -9f;
                Vector3 f0L = Vector3.zero, f0R = Vector3.zero;
                float slideL = 0f, slideR = 0f;
                bool firstF = true;
                while (Time.realtimeSinceStartup - ts < 6f)
                {
                    yield return new WaitForEndOfFrame();
                    float t = Time.realtimeSinceStartup - ts;
                    var at = new Vector2(300f * Mathf.Sin(t * 2f * Mathf.PI), 150f * Mathf.Sin(t * 4f * Mathf.PI));
                    var was = new Vector2(300f * Mathf.Sin(prevT * 2f * Mathf.PI), 150f * Mathf.Sin(prevT * 4f * Mathf.PI));
                    prevT = t;
                    h.Hands.MoveForCheck(at - was);
                    var xyNow = h.Hands.XYForCheck("kokan");
                    minX = Mathf.Min(minX, xyNow.x); maxX = Mathf.Max(maxX, xyNow.x); minY = Mathf.Min(minY, xyNow.y); maxY = Mathf.Max(maxY, xyNow.y);
                    var root = st.Cha.transform;
                    var tl = root.InverseTransformPoint(HIK.Find(st.Cha.objBodyBone.transform, "cf_j_toes_L").position);
                    var tr = root.InverseTransformPoint(HIK.Find(st.Cha.objBodyBone.transform, "cf_j_toes_R").position);
                    lowL = Mathf.Min(lowL, tl.y); lowR = Mathf.Min(lowR, tr.y); highL = Mathf.Max(highL, tl.y); highR = Mathf.Max(highR, tr.y);
                    if (firstF) { f0L = tl; f0R = tr; firstF = false; }
                    slideL = Mathf.Max(slideL, new Vector2(tl.x - f0L.x, tl.z - f0L.z).magnitude);
                    slideR = Mathf.Max(slideR, new Vector2(tr.x - f0R.x, tr.z - f0R.z).magnitude);
                    if (Mathf.Abs(t - 3f) < 0.02f) yield return KokanShots(pass + "_stroke3");
                }
                KokanMeasure(pass, string.Format("stroke 6s xy x {0:F3}..{1:F3} y {2:F3}..{3:F3} toesY L {4:F3}..{5:F3} R {6:F3}..{7:F3} slide L {8:F3} R {9:F3}",
                    minX, maxX, minY, maxY, lowL, highL, lowR, highR, slideL, slideR));
                yield return KokanShots(pass + "_stroke6");
                tc.Hold = false;
                t0 = Time.realtimeSinceStartup;
                foreach (var at in new[] { 1.0f, 3.0f })
                {
                    while (Time.realtimeSinceStartup - t0 < at) yield return null;
                    KokanMeasure(pass, "after " + at.ToString("F1"));
                }
                yield return KokanShots(pass + "_after");
                pc.End("probe");
                yield return new WaitForSeconds(1.0f);
            }
            if (ikFile.Length > 0)
            {
                var ta = GlobalMethod.LoadAllFolderInOneFile<TextAsset>("h/list/", ikFile, null);
                lines.AppendLine("IK DATA " + ikFile + ":");
                lines.AppendLine(ta != null ? ta.text : "(none)");
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // the standing aibu at her body's heights 0..1 (the shape's first value; in memory only): her toes' height over 1 s without
        // the animator's "height" (as MuseStudio was) and with it (the original's HActionBase.SetDefaultAnimatorFloat: the
        // female's shape 0 every frame) - at rest and with her crotch held; the animator's parameters
        IEnumerator AibuHeight()
        {
            yield return new WaitForSeconds(1.5f);
            var st = h.Stage;
            var pc = h.Play;
            var tc = h.Touch;
            var cha = st.Cha;
            float was = cha.GetShapeBodyValue(0);
            string err = pc.Begin("aibu_00", "probe", "here");
            yield return new WaitForSeconds(1.5f);
            var an = cha.animBody;
            var names = new List<string>();
            foreach (var prm in an.parameters) names.Add(prm.name + ":" + prm.type + "=" + (prm.type == AnimatorControllerParameterType.Float ? an.GetFloat(prm.name).ToString("F2") : ""));
            lines.AppendLine("begin " + err + " body height " + was.ToString("F3") + " params " + string.Join(",", names.ToArray()));
            foreach (var held in new[] { false, true })
            {
                if (held) { h.Hands.MouseOffForCheck = true; tc.PressForCheck("crotch", ""); yield return new WaitForSeconds(1.5f); }
                foreach (var v in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
                {
                    cha.SetShapeBodyValue(0, v);
                    foreach (var withParam in new[] { false, true })
                    {
                        float t0 = Time.realtimeSinceStartup, low = 9f, high = -9f, hipY = 0f;
                        while (Time.realtimeSinceStartup - t0 < 1.2f)
                        {
                            an.SetFloat("height", withParam ? v : 0f);
                            yield return new WaitForEndOfFrame();
                            if (Time.realtimeSinceStartup - t0 < 0.4f) continue;     // (the change settles)
                            var root = cha.transform;
                            foreach (var toe in new[] { "cf_j_toes_L", "cf_j_toes_R", "cf_j_foot_L", "cf_j_foot_R" })
                            {
                                float y = root.InverseTransformPoint(HIK.Find(cha.objBodyBone.transform, toe).position).y;
                                low = Mathf.Min(low, y);
                                high = Mathf.Max(high, y);
                            }
                            hipY = root.InverseTransformPoint(HIK.Find(cha.objBodyBone.transform, "cf_j_hips").position).y;
                        }
                        lines.AppendLine(string.Format("{0} body {1:F2} param height {2} -> feet y {3:F3}..{4:F3} hips {5:F3} state {6}", held ? "crotch held" : "at rest", v,
                            withParam ? v.ToString("F2") : "0 (unset)", low, high, hipY, pc.LastState));
                        if (v == 0f || v == 1f) yield return KokanShots(string.Format("h{0}_{1}_{2}", held ? "held" : "rest", v.ToString("F0"), withParam ? "param" : "unset"));
                    }
                }
            }
            tc.Hold = false;
            h.Hands.MouseOffForCheck = false;
            cha.SetShapeBodyValue(0, was);
            pc.End("probe");
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // the standing aibu with the original's motion IK (as now) and without it (IK.End after the start): her feet at the end of
        // each frame (after the IK's solve) at rest, her crotch held, stroked (the drag's own mapping) and after; pictures
        IEnumerator AibuIk(string ids)
        {
            yield return new WaitForSeconds(1.5f);
            var pc = h.Play;
            var tc = h.Touch;
            h.Hands.MouseOffForCheck = true;
            foreach (var id in ids.Split(','))
            foreach (var pass0 in new[] { "ik", "noik" })
            {
                string pass = id + " " + pass0;
                h.Gauge.Value = 0f;
                var prow = pc.Row(id);
                var psp = prow != null ? pc.Spots(prow) : new List<Spot>();
                string err = psp.Count > 0 ? pc.Begin(id, "probe", psp[0].Id) : "no spot";
                yield return new WaitForSeconds(1.5f);
                if (pass0 == "noik") pc.IK.End();
                lines.AppendLine(pass + " begin " + err + " ik on " + pc.IK.On + " enabled " + pc.IK.Enabled);
                yield return new WaitForEndOfFrame();
                {
                    var fb = h.Stage.Cha.animBody.GetComponent<RootMotion.FinalIK.FullBodyBipedIK>();
                    var rt = h.Stage.Cha.transform;
                    var lf = fb != null ? fb.solver.leftFootEffector : null;
                    var foot = HIK.Find(h.Stage.Cha.objBodyBone.transform, "cf_j_foot_L");
                    var pv = HIK.Find(h.Stage.Cha.objBodyBone.transform, "cf_pv_leg_L");
                    if (fb != null)
                        lines.AppendLine(string.Format("  solver IKPositionWeight {0:F2} iterations {1} initiated {2} fixTransforms {3} LF bone {4} pull {5:F2} reach {6:F2} body w {7:F2} updated-by {8}",
                            fb.solver.IKPositionWeight, fb.solver.iterations, fb.solver.initiated, fb.fixTransforms, lf.bone != null ? lf.bone.name : "none",
                            fb.solver.leftLegChain.pull, fb.solver.leftLegChain.reach, fb.solver.bodyEffector.positionWeight, string.Join(",", Array.ConvertAll(fb.GetComponents<MonoBehaviour>(), c => c.GetType().Name))));
                    lines.AppendLine(string.Format("  fbik {0} LF weight {1:F2} target {2} at {3} | foot at {4} | cf_pv_leg_L at {5}", fb != null && fb.enabled,
                        lf != null ? lf.positionWeight : -1f, lf != null && lf.target != null ? lf.target.name : "none",
                        lf != null && lf.target != null ? rt.InverseTransformPoint(lf.target.position).ToString("F3") : "-",
                        rt.InverseTransformPoint(foot.position).ToString("F3"), pv != null ? rt.InverseTransformPoint(pv.position).ToString("F3") : "none"));
                }
                yield return FeetSample(pass, "rest", 1.5f, false);
                yield return KokanShots(pass.Replace(' ', '_') + "_rest");
                tc.PressForCheck("crotch", "");
                float t0 = Time.realtimeSinceStartup;
                while (h.Hands.Ctrl != "drag" && Time.realtimeSinceStartup - t0 < 6f) yield return null;
                yield return FeetSample(pass, "held", 3f, false);
                yield return KokanShots(pass.Replace(' ', '_') + "_held");
                yield return FeetSample(pass, "stroke", 4f, true);
                yield return KokanShots(pass.Replace(' ', '_') + "_stroke");
                tc.Hold = false;
                yield return new WaitForSeconds(0.3f);
                yield return FeetSample(pass, "after", 2f, false);
                yield return KokanShots(pass.Replace(' ', '_') + "_after");
                pc.End("probe");
                yield return new WaitForSeconds(1.0f);
            }
            h.Hands.MouseOffForCheck = false;
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // --muse-probe=aibufloor[:<play ids>]: every kept room, every spot the play fits (and "here"): the play started there, her
        // root, toes and the floor straight below (a ray down against the map's colliders, the characters' left out) - the gap
        // between her feet and the floor; a picture where the gap is large
        IEnumerator AibuFloor(string ids)
        {
            yield return new WaitForSeconds(1.5f);
            var st = h.Stage;
            var pc = h.Play;
            foreach (var room in new List<string>(st.LocationKeys()))
            {
                if (room != st.Location) yield return st.SetLocation(room);
                yield return new WaitForSeconds(0.8f);
                foreach (var id in ids.Split(','))
                {
                    var row = pc.Row(id);
                    if (row == null) continue;
                    var seen = new HashSet<string>();
                    foreach (var sp in pc.Spots(row))
                    {
                        if (!seen.Add(sp.Name)) continue;
                        h.Gauge.Value = 0f;
                        string err = pc.Begin(id, "probe", sp.Id);
                        if (err.Length > 0) { lines.AppendLine(string.Format("{0} {1} {2}: {3}", room, id, sp.Name, err)); continue; }
                        yield return new WaitForSeconds(1.2f);
                        yield return new WaitForEndOfFrame();
                        var cha = st.Cha;
                        var root = cha.transform.position;
                        float toeL = HIK.Find(cha.objBodyBone.transform, "cf_j_toes_L").position.y, toeR = HIK.Find(cha.objBodyBone.transform, "cf_j_toes_R").position.y;
                        var hips = HIK.Find(cha.objBodyBone.transform, "cf_j_hips").position;
                        string floor = "no floor below";
                        float gap = 0f;
                        foreach (var hit in Physics.RaycastAll(new Vector3(hips.x, hips.y + 0.2f, hips.z), Vector3.down, 5f))
                        {
                            if (hit.collider.GetComponentInParent<ChaControl>() != null) continue;
                            if (floor == "no floor below" || hit.point.y > gap)
                            {
                                if (hit.point.y > Mathf.Min(toeL, toeR) + 0.15f) continue;      // (a table or bed above her feet: not her floor)
                                gap = hit.point.y;
                                floor = hit.collider.name;
                            }
                        }
                        float g = floor == "no floor below" ? float.NaN : Mathf.Min(toeL, toeR) - gap;
                        lines.AppendLine(string.Format("{0} {1} {2} ({3}): root y {4:F3} toes {5:F3}/{6:F3} floor {7} at {8:F3} -> gap {9:F3}", room, id, sp.Name, sp.Id,
                            root.y, toeL, toeR, floor, gap, g));
                        if (!float.IsNaN(g) && Mathf.Abs(g) > 0.06f) yield return KokanShots(string.Format("floor_{0}_{1}_{2}", room, id, seen.Count));
                        pc.End("probe");
                        yield return new WaitForSeconds(0.4f);
                    }
                }
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // the standing aibu with her legs made shorter / longer (both thighs scaled 0.85 / 1.15, as a card whose legs a bone mod
        // such as ABMX changed): her feet with the original's motion IK and without - at rest and with her crotch held
        IEnumerator AibuLegs()
        {
            yield return new WaitForSeconds(1.5f);
            var pc = h.Play;
            var tc = h.Touch;
            var cha = h.Stage.Cha;
            var thighs = new[] { HIK.Find(cha.objBodyBone.transform, "cf_j_thigh00_L"), HIK.Find(cha.objBodyBone.transform, "cf_j_thigh00_R") };
            var scale0 = thighs[0].localScale;
            h.Hands.MouseOffForCheck = true;
            foreach (var s in new[] { 0.85f, 1f, 1.15f })
            {
                foreach (var t in thighs) t.localScale = scale0 * s;
                foreach (var pass in new[] { "ik", "noik" })
                {
                    h.Gauge.Value = 0f;
                    string err = pc.Begin("aibu_00", "probe", "here");
                    yield return new WaitForSeconds(1.2f);
                    if (pass == "noik") pc.IK.End();
                    string tag = string.Format("legs x{0:F2} {1}", s, pass);
                    yield return FeetSample(tag, "rest", 1.2f, false);
                    if (s != 1f) yield return KokanShots(string.Format("legs{0:F0}_{1}_rest", s * 100f, pass));
                    tc.PressForCheck("crotch", "");
                    float t0 = Time.realtimeSinceStartup;
                    while (h.Hands.Ctrl != "drag" && Time.realtimeSinceStartup - t0 < 6f) yield return null;
                    yield return FeetSample(tag, "held", 2.5f, false);
                    if (s != 1f) yield return KokanShots(string.Format("legs{0:F0}_{1}_held", s * 100f, pass));
                    tc.Hold = false;
                    yield return new WaitForSeconds(0.3f);
                    pc.End("probe");
                    yield return new WaitForSeconds(0.8f);
                }
            }
            foreach (var t in thighs) t.localScale = scale0;
            h.Hands.MouseOffForCheck = false;
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // presses with the real mouse (Windows' own input on the game window): on her thigh outside a play and on her chest in the
        // standing aibu, held 4 s still and with a hand's small jitter - the gauge while it is held and after it is let go
        IEnumerator RealTouch()
        {
            yield return new WaitForSeconds(1.5f);
            var st = h.Stage;
            var pc = h.Play;
            var tc = h.Touch;
            foreach (var where in new[] { "thigh", "chest" })
            {
                if (where == "chest") { pc.Begin("aibu_00", "probe", "here"); yield return new WaitForSeconds(1.5f); }
                foreach (var jitter in new[] { 0, 2 })
                {
                    h.Gauge.Value = 0.1f;
                    yield return new WaitForSeconds(0.5f);
                    var cha = st.Cha;
                    Vector3 wp = where == "thigh"
                        ? Vector3.Lerp(HIK.Find(cha.objBodyBone.transform, "cf_j_thigh00_L").position, HIK.Find(cha.objBodyBone.transform, "cf_j_leg01_L").position, 0.4f)
                        : HIK.Find(cha.objBodyBone.transform, "cf_d_bust01_L") != null ? HIK.Find(cha.objBodyBone.transform, "cf_d_bust01_L").position : HIK.Find(cha.objBodyBone.transform, "cf_j_spine03").position;
                    var sp = Camera.main.WorldToScreenPoint(wp);
                    var gui = new Vector2(sp.x, Screen.height - sp.y);
                    var log = new StringBuilder();
                    float next = 0f;
                    string pressed = "";
                    yield return RealInput.Press(gui, 4f, jitter, t =>
                    {
                        if (pressed.Length == 0 && tc.Pressing.Length > 0) pressed = tc.Pressing;
                        if (t >= next) { log.Append(string.Format(" {0:F1}s {1:F3}{2}", t, h.Gauge.Value, tc.Stroking ? "s" : "")); next += 0.5f; }
                    });
                    float atRelease = h.Gauge.Value;
                    yield return new WaitForSeconds(0.4f);
                    lines.AppendLine(string.Format("{0} jitter {1} (pressed '{2}', {3}): {4} | let go +0.4s {5:F3} (jump {6:+0.000;-0.000})", where, jitter, pressed,
                        RealInput.LastError.Length > 0 ? RealInput.LastError : "ok", log, h.Gauge.Value, h.Gauge.Value - atRelease));
                    yield return new WaitForSeconds(2.8f);
                }
                if (pc.Active) pc.End("probe");
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // her feet (ankles and toes) over a while, read at the end of each frame: their lowest and highest above her root (the
        // floor), how far each foot slid; a stroke moves the mouse through the drag's own mapping meanwhile (±300 / ±150 px, a
        // round a second)
        IEnumerator FeetSample(string pass, string what, float secs, bool stroke)
        {
            var cha = h.Stage.Cha;
            float t0 = Time.realtimeSinceStartup, prev = 0f;
            var names = new[] { "cf_j_foot_L", "cf_j_toes_L", "cf_j_foot_R", "cf_j_toes_R" };
            var low = new float[4];
            var high = new float[4];
            var first = new Vector3[4];
            var slide = new float[4];
            for (int i = 0; i < 4; i++) { low[i] = 9f; high[i] = -9f; }
            bool started = false;
            pivSlide = new float[3];
            pivOff = 0f;
            var pivNames = new[] { "cf_j_leg03_L", "cf_t_leg_L", "cf_pv_leg_L" };
            var pivFirst = new Vector3[3];
            while (Time.realtimeSinceStartup - t0 < secs)
            {
                yield return new WaitForEndOfFrame();
                float t = Time.realtimeSinceStartup - t0;
                {
                    var rt = cha.transform;
                    var pp = new Vector3[3];
                    for (int k = 0; k < 3; k++)
                    {
                        var tr = HIK.Find(cha.objBodyBone.transform, pivNames[k]) ?? HIK.Find(cha.transform, pivNames[k]);
                        pp[k] = tr != null ? rt.InverseTransformPoint(tr.position) : Vector3.zero;
                        if (!started) pivFirst[k] = pp[k];
                        pivSlide[k] = Mathf.Max(pivSlide[k], (pp[k] - pivFirst[k]).magnitude);
                    }
                    pivOff = Mathf.Max(pivOff, (pp[0] - pp[1]).magnitude);
                }
                if (stroke)
                {
                    var at = new Vector2(300f * Mathf.Sin(t * 2f * Mathf.PI), 150f * Mathf.Sin(t * 4f * Mathf.PI));
                    var was = new Vector2(300f * Mathf.Sin(prev * 2f * Mathf.PI), 150f * Mathf.Sin(prev * 4f * Mathf.PI));
                    h.Hands.MoveForCheck(at - was);
                }
                prev = t;
                var root = cha.transform;
                for (int i = 0; i < 4; i++)
                {
                    var p = root.InverseTransformPoint(HIK.Find(cha.objBodyBone.transform, names[i]).position);
                    low[i] = Mathf.Min(low[i], p.y);
                    high[i] = Mathf.Max(high[i], p.y);
                    if (!started) first[i] = p;
                    slide[i] = Mathf.Max(slide[i], new Vector2(p.x - first[i].x, p.z - first[i].z).magnitude);
                }
                started = true;
            }
            lines.AppendLine(string.Format("{0} {1} state {2}: ankle L {3:F3}..{4:F3} toes L {5:F3}..{6:F3} ankle R {7:F3}..{8:F3} toes R {9:F3}..{10:F3} slide L {11:F3} R {12:F3}",
                pass, what, h.Play.LastState, low[0], high[0], low[1], high[1], low[2], high[2], low[3], high[3], slide[0], slide[2]));
            lines.AppendLine(string.Format("    leg03_L moved {0:F3}, its IK target cf_t_leg_L moved {1:F3}, the pivot cf_pv_leg_L moved {2:F3}; leg03 off its target at most {3:F3}",
                pivSlide[0], pivSlide[1], pivSlide[2], pivOff));
        }

        // (FeetSample) the ankle (the foot effector's bone), its IK target and the animated pivot it links to: how far each moved
        float[] pivSlide = new float[3];
        float pivOff;

        // --muse-probe=touchenable:<play id prefixes>: per play its female part's fileAibuEnableMotion (the original's
        // HandCtrl.LoadMotionEnableState: per state which touch areas take the user's hand - mouth, breasts, crotch, anal, hips)
        // and, the play started, her body animator's layers (the hand's reaction layers the aibu rows name)
        IEnumerator TouchEnable(string prefixes)
        {
            yield return new WaitForSeconds(1.0f);
            var pc = h.Play;
            var pre = prefixes.Split(',');
            foreach (var row in pc.List(null))
            {
                string id = Json.Str(row, "id");
                if (System.Array.Find(pre, p => id.StartsWith(p)) == null) continue;
                var hi = HData.Info((int)Json.Num(row, "mode"), (int)Json.Num(row, "motion_id"));
                if (hi == null) { lines.AppendLine(id + ": no row"); continue; }
                string file = hi.paramFemale.fileAibuEnableMotion;
                var sb = new StringBuilder(string.Format("== {0} {1} mode {2} enable '{3}'", id, Json.Str(row, "name"), Json.Num(row, "mode"), file));
                if (!string.IsNullOrEmpty(file))
                {
                    List<AibuEnableState> list = null;
                    try { list = GlobalMethod.LoadAllFolder<AibuEnableState>("h/list/", file, null, null); } catch (Exception e) { sb.Append(" read: " + e.Message); }
                    if (list != null)
                        foreach (var es in list)
                            foreach (var p in es.param)
                                sb.Append(string.Format("\n  {0}: mouth {1} bustL {2} bustR {3} kokan {4} anal {5} hipL {6} hipR {7} state {8}", p.AnimationName, p.Mouth, p.Bust_L, p.Bust_R,
                                    p.Kokan, p.Anal, p.Hip_L, p.Hip_R, p.MotionState));
                }
                lines.AppendLine(sb.ToString());
                var spots = pc.Spots(row);
                if (spots.Count == 0) continue;
                pc.WhoForCheck = new Dictionary<string, Dictionary<string, object>> {
                    { "character", new Dictionary<string, object> { { "body", "female" }, { "futanari", false } } },
                    { "player", new Dictionary<string, object> { { "body", "male" }, { "futanari", false } } } };
                string err = pc.Begin(id, "probe", spots[0].Id);
                if (err.Length == 0)
                {
                    yield return new WaitForSeconds(0.5f);
                    var an = h.Stage.Cha.animBody;
                    var ls = new List<string>();
                    for (int i = 0; i < an.layerCount; i++) ls.Add(i + ":" + an.GetLayerName(i));
                    lines.AppendLine("  layers " + string.Join(", ", ls.ToArray()));
                    pc.End("probe");
                    yield return new WaitForSeconds(0.3f);
                }
                else lines.AppendLine("  begin: " + err);
                pc.WhoForCheck = null;
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // the standing aibu as the user meets it (PR #9 adopted: the same card, place, way in and pace): a card given by its path
        // (a copy - read only; the check's isolation keeps the settings and moods), in her room; the sudden grope (the real mouse
        // long-pressing her crotch, a hand's jitter) and the menu's start at the floor; pace 0 and 2; her feet at the end of each
        // frame right after the start, while pressed and after; the shoes (服) too; pictures
        bool feetOn;
        string feetTag = "";
        float[] feetLow = new float[2], feetHigh = new float[2];

        IEnumerator FeetWatch()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                if (!feetOn) continue;
                var cha = h.Stage.Cha;
                var root = cha.transform;
                for (int i = 0; i < 2; i++)
                {
                    float y = Mathf.Min(root.InverseTransformPoint(HIK.Find(cha.objBodyBone.transform, i == 0 ? "cf_j_toes_L" : "cf_j_toes_R").position).y,
                        root.InverseTransformPoint(HIK.Find(cha.objBodyBone.transform, i == 0 ? "cf_j_foot_L" : "cf_j_foot_R").position).y);
                    feetLow[i] = Mathf.Min(feetLow[i], y);
                    feetHigh[i] = Mathf.Max(feetHigh[i], y);
                }
            }
        }

        void FeetStart(string tag) { feetTag = tag; feetLow = new[] { 9f, 9f }; feetHigh = new[] { -9f, -9f }; feetOn = true; }

        void FeetEnd()
        {
            feetOn = false;
            var pc = h.Play;
            lines.AppendLine(string.Format("{0}: lowest foot point L {1:F3}..{2:F3} R {3:F3}..{4:F3} | play {5} state {6} pace {7} ik {8} hands {9} root y {10:F3}", feetTag,
                feetLow[0], feetHigh[0], feetLow[1], feetHigh[1], pc.Active ? Json.Str(pc.Current, "id") : "-", pc.LastState, pc.Pace, pc.IK.On && pc.IK.Enabled, h.Hands.Ctrl,
                h.Stage.Cha.transform.position.y));
        }

        IEnumerator AibuUser(string card)
        {
            yield return new WaitForSeconds(1.5f);
            var st = h.Stage;
            var pc = h.Play;
            var tc = h.Touch;
            string err = h.Chars.Switch(card, "probe", false);
            yield return new WaitForSeconds(4f);
            float t0 = Time.realtimeSinceStartup;
            while ((!st.Ready || st.Cha == null) && Time.realtimeSinceStartup - t0 < 20f) yield return null;
            lines.AppendLine("card switch '" + err + "' name " + st.Name + " height " + st.Cha.GetShapeBodyValue(0).ToString("F2"));
            h.StartCoroutine(FeetWatch());
            FeetStart("talk, standing");
            yield return new WaitForSeconds(1.5f);
            FeetEnd();
            yield return KokanShots("user_talk");
            // the sudden grope: the real mouse on her crotch, held 3 s with a hand's jitter (the standing aibu starts where she is)
            var kokan = HIK.Find(st.Cha.objBodyBone.transform, "cf_j_kokan") ?? HIK.Find(st.Cha.objBodyBone.transform, "cf_j_hips");
            var sp = Camera.main.WorldToScreenPoint(kokan.position);
            FeetStart("grope (real mouse, crotch, 3 s)");
            yield return RealInput.Press(new Vector2(sp.x, Screen.height - sp.y), 3f, 2);
            FeetEnd();
            lines.AppendLine("  press: " + (RealInput.LastError.Length > 0 ? RealInput.LastError : "ok") + " play started " + pc.Active);
            yield return KokanShots("user_grope_after");
            FeetStart("grope, after the release");
            yield return new WaitForSeconds(2f);
            FeetEnd();
            foreach (int pace in new[] { 0, 2 })
            {
                if (!pc.Active) break;
                pc.SetPace(pace, "probe");
                yield return new WaitForSeconds(1f);
                FeetStart("grope's aibu, pace " + pace + ", crotch held (2.5 s)");
                tc.PressForCheck("crotch", "");
                yield return new WaitForSeconds(2.5f);
                FeetEnd();
                yield return KokanShots("user_pace" + pace + "_held");
                tc.Hold = false;
                yield return new WaitForSeconds(0.5f);
                FeetStart("grope's aibu, pace " + pace + ", after");
                yield return new WaitForSeconds(1.5f);
                FeetEnd();
            }
            if (pc.Active) pc.End("probe");
            yield return new WaitForSeconds(1f);
            // the menu's start at the floor
            var row = pc.Row("aibu_00");
            var floor = pc.Spots(row).Find(s => !s.Here) ?? pc.Spots(row)[0];
            string berr = pc.Begin("aibu_00", "probe", floor.Id);
            FeetStart("menu start at " + floor.Name + " (" + berr + "), first 1.5 s");
            yield return new WaitForSeconds(1.5f);
            FeetEnd();
            yield return KokanShots("user_menu_start");
            FeetStart("menu's aibu, crotch held (3 s)");
            tc.PressForCheck("crotch", "");
            yield return new WaitForSeconds(3f);
            FeetEnd();
            yield return KokanShots("user_menu_held");
            tc.Hold = false;
            FeetStart("menu's aibu, after");
            yield return new WaitForSeconds(1.5f);
            FeetEnd();
            pc.End("probe");
            yield return new WaitForSeconds(1f);
            // her shoes on this card
            var parts = st.Cha.nowCoordinate.clothes.parts;
            lines.AppendLine(string.Format("shoes: parts 7 id {0} / 8 id {1}; shoesType {2}; worn {3}; half? {4}", parts[7].id, parts.Length > 8 ? parts[8].id : -1, st.Cha.fileStatus.shoesType,
                string.Join(",", Clothes.Worn(st.Cha).ConvertAll(k => Clothes.Keys[k]).ToArray()), Clothes.HasHalf(st.Cha, 7)));
            yield return ShoeShot("user_before");
            string next = Clothes.Next(st.Cha, 7, Clothes.State(st.Cha, 7));
            h.ChangeClothes(new Dictionary<string, object> { { "co_shoes", next } }, "user");
            yield return new WaitForSeconds(0.8f);
            yield return ShoeShot("user_after_one_press_" + next);
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // every play that can be reversed (PR #12 adopted, the user's note: a reversed cowgirl, your male body without its penis),
        // started reversed with your body male: your body's penis - the setting, its visible flag, its renderers shown - and hers
        // (her strapon / futanari); a picture of each without one
        IEnumerator ReversedPenis()
        {
            yield return new WaitForSeconds(1.5f);
            var st = h.Stage;
            var pc = h.Play;
            pc.WhoForCheck = new Dictionary<string, Dictionary<string, object>> {
                { "character", new Dictionary<string, object> { { "body", "female" }, { "futanari", false } } },
                { "player", new Dictionary<string, object> { { "body", "male" }, { "futanari", false } } } };
            lines.AppendLine("setting: your penis shown " + h.YourPenisShown.Value + ", your clothes " + h.YourClothes.Value + ", silhouette " + h.YourSilhouette.Value + " (off for the shots)");
            bool silWas = h.YourSilhouette.Value;
            h.YourSilhouette.Value = false;
            foreach (var row in pc.List(null))
            {
                string id = Json.Str(row, "id"), group = Json.Str(row, "group");
                if (!pc.Reversible(row) || (group != "sonyu" && group != "houshi")) continue;
                var spots = pc.Spots(row);
                if (spots.Count == 0) { lines.AppendLine(id + ": no spot here"); continue; }
                string err = pc.Begin(id, "probe", spots[0].Id, true);
                if (err.Length > 0) { lines.AppendLine(id + " " + Json.Str(row, "name") + ": " + err); continue; }
                yield return new WaitForSeconds(2.5f);
                var pt = st.Partner;
                string mine = "no partner";
                bool shown = false;
                if (pt != null)
                {
                    int on = 0, all = 0;
                    var names = new List<string>();
                    foreach (var r in pt.charInfo.GetComponentsInChildren<Renderer>(true))
                        if (r.name.Contains("dan") || r.name.Contains("gomu") || r.transform.parent != null && r.transform.parent.name.Contains("dan"))
                        {
                            all++;
                            bool vis = r.enabled && r.gameObject.activeInHierarchy;
                            if (vis) on++;
                            names.Add(r.name + (vis ? "+" : "-"));
                        }
                    shown = pt.charInfo.fileStatus.visibleSonAlways && on > 0;
                    var root = HIK.Find(pt.charInfo.objBodyBone.transform, "cm_J_dan101_00");
                    var tip = HIK.Find(pt.charInfo.objBodyBone.transform, "cm_J_dan109_00");
                    var hips = HIK.Find(pt.charInfo.objBodyBone.transform, "cf_j_kokan") ?? HIK.Find(pt.charInfo.objBodyBone.transform, "cf_j_waist02");
                    string bones = root != null && tip != null ? string.Format("dan root->tip {0:F3} m, root from crotch {1:F3} m, root scale {2:F2}, tip in her body {3:F3} m from her crotch",
                        (tip.position - root.position).magnitude, hips != null ? (root.position - hips.position).magnitude : -1f, root.lossyScale.x,
                        (tip.position - (HIK.Find(st.Cha.objBodyBone.transform, "cf_j_kokan") ?? st.Cha.transform).position).magnitude) : "no dan bones";
                    // the penis inside her body? its bones against her torso (a capsule from her waist to her chest, 13 cm round)
                    var w1 = HIK.Find(st.Cha.objBodyBone.transform, "cf_j_waist01");
                    var s3 = HIK.Find(st.Cha.objBodyBone.transform, "cf_j_spine03");
                    int inside = 0, bonesN = 0;
                    if (w1 != null && s3 != null)
                        for (int k = 1; k <= 9; k++)
                        {
                            var bb = HIK.Find(pt.charInfo.objBodyBone.transform, "cm_J_dan10" + k + "_00");
                            if (bb == null) continue;
                            bonesN++;
                            Vector3 ab = s3.position - w1.position, ap = bb.position - w1.position;
                            float tt = Mathf.Clamp01(Vector3.Dot(ap, ab) / ab.sqrMagnitude);
                            if ((w1.position + ab * tt - bb.position).magnitude < 0.13f) inside++;
                        }
                    mine = string.Format("your body sex {0} visibleSon {1} gomu {2} dan renderers {3}/{4} [{5}] | {6} | BP aim you {7} her {8} | penis bones inside her torso {9}/{10}", pt.charInfo.sex, pt.charInfo.fileStatus.visibleSonAlways,
                        pt.charInfo.fileStatus.visibleGomu, on, all, string.Join(" ", names.ToArray()), bones, Penetration.Get(pt.charInfo), Penetration.Get(st.Cha), inside, bonesN);
                }
                lines.AppendLine(string.Format("{0} {1}: {2} | her visibleSon {3} strapon {4} | parts {5}", id, Json.Str(row, "name"), mine, st.Cha.fileStatus.visibleSonAlways,
                    pc.Res != null ? pc.Res.Strapon : "-", pc.Res != null ? Json.Write(pc.Res.Parts) : "-"));
                if (!shown || id == "sonyu_04" || id == "sonyu_00" || id == "sonyu_02")
                {
                    Vector3 c = st.Cha.transform.position + Vector3.up * 0.55f;
                    yield return Shot(c + st.Cha.transform.right * 2.0f, c, 45f, "reversed_" + id + "_side.png");
                    yield return Shot(c + st.Cha.transform.forward * 2.0f, c, 45f, "reversed_" + id + "_front.png");
                }
                pc.End("probe");
                yield return new WaitForSeconds(0.3f);
            }
            pc.WhoForCheck = null;
            h.YourSilhouette.Value = silWas;
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0" + "\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // who takes her feet off their IK targets (the user's note: her legs follow her hips, the soles lose the floor - the targets
        // stay, her ankles do not): Studio's PVCopy (each LateUpdate it copies its pv objects onto its bones) - its entries; her
        // left ankle's distance from its target with her crotch held, as it is and with PVCopy's entries off
        IEnumerator IkFight()
        {
            yield return new WaitForSeconds(1.5f);
            var pc = h.Play;
            var tc = h.Touch;
            var cha = h.Stage.Cha;
            string err = pc.Begin("aibu_00", "probe", "here");
            yield return new WaitForSeconds(1.2f);
            var pv = cha.GetComponentInChildren<Studio.PVCopy>(true);
            if (pv == null) pv = h.Stage.Heroine.charInfo.GetComponentInChildren<Studio.PVCopy>(true);
            var en = pv != null ? (bool[])HarmonyLib.AccessTools.Field(typeof(Studio.PVCopy), "_enable").GetValue(pv) : null;
            var pvs = pv != null ? (GameObject[])HarmonyLib.AccessTools.Field(typeof(Studio.PVCopy), "pv").GetValue(pv) : null;
            var bones = pv != null ? (GameObject[])HarmonyLib.AccessTools.Field(typeof(Studio.PVCopy), "bone").GetValue(pv) : null;
            if (pvs != null)
                for (int i = 0; i < pvs.Length; i++)
                    lines.AppendLine(string.Format("PVCopy {0}: pv {1} -> bone {2} enabled {3}", i, pvs[i] != null ? pvs[i].name : "-", bones != null && bones[i] != null ? bones[i].name : "-", en != null && en[i]));
            else lines.AppendLine("no PVCopy");
            h.Hands.MouseOffForCheck = true;
            tc.PressForCheck("crotch", "");
            yield return new WaitForSeconds(1.0f);
            var fbk = cha.animBody.GetComponent<RootMotion.FinalIK.FullBodyBipedIK>();
            int solves = 0;
            float offAtSolve = 0f;
            var legS = HIK.Find(cha.objBodyBone.transform, "cf_j_leg03_L");
            var tgtS = HIK.Find(cha.transform, "cf_t_leg_L");
            RootMotion.FinalIK.IKSolver.UpdateDelegate onSolve = () => { solves++; offAtSolve = Mathf.Max(offAtSolve, (legS.position - tgtS.position).magnitude); };
            if (fbk != null) fbk.solver.OnPostUpdate += onSolve;
            lines.AppendLine("fbik " + (fbk != null) + " enabled " + (fbk != null && fbk.enabled) + " animator update mode " + cha.animBody.updateMode + " culling " + cha.animBody.cullingMode);
            if (fbk != null)
                lines.AppendLine(string.Format("mapping weights: arm L {0:F2} R {1:F2} leg L {2:F2} R {3:F2} spine twist {4:F2}; effector weights LF {5:F2} RF {6:F2} LH {7:F2} RH {8:F2}",
                    fbk.solver.leftArmMapping.weight, fbk.solver.rightArmMapping.weight, fbk.solver.leftLegMapping.weight, fbk.solver.rightLegMapping.weight, fbk.solver.spineMapping.twistWeight,
                    fbk.solver.leftFootEffector.positionWeight, fbk.solver.rightFootEffector.positionWeight, fbk.solver.leftHandEffector.positionWeight, fbk.solver.rightHandEffector.positionWeight));
            foreach (var pass in new[] { "as is", "PVCopy off" })
            {
                solves = 0;
                offAtSolve = 0f;
                if (pass == "PVCopy off" && en != null) for (int i = 0; i < en.Length; i++) en[i] = false;
                float off = 0f, moved = 0f;
                Vector3 first = Vector3.zero;
                bool started = false;
                float t0 = Time.realtimeSinceStartup;
                var leg = HIK.Find(cha.objBodyBone.transform, "cf_j_leg03_L");
                var tgt = HIK.Find(cha.transform, "cf_t_leg_L");
                while (Time.realtimeSinceStartup - t0 < 2.5f)
                {
                    yield return new WaitForEndOfFrame();
                    var lp = cha.transform.InverseTransformPoint(leg.position);
                    if (!started) { first = lp; started = true; }
                    moved = Mathf.Max(moved, (lp - first).magnitude);
                    off = Mathf.Max(off, (leg.position - tgt.position).magnitude);
                }
                lines.AppendLine(string.Format("{0}: left ankle moved {1:F3}, off its target at most {2:F3} (state {3}); the solver ran {4} times in 2.5 s, the ankle off its target right after a solve at most {5:F3}",
                    pass, moved, off, pc.LastState, solves, offAtSolve));
                yield return KokanShots("ikfight_" + pass.Replace(' ', '_'));
            }
            tc.Hold = false;
            h.Hands.MouseOffForCheck = false;
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // the sudden grope with the real mouse (PR #9 adopted, "the hand does not follow the mouse"): her left breast long-pressed
        // and moved about (a hand's circles, 12 px), 4 s - when the play starts, when the hand is on her, the drag, the hand's
        // speed and the force on her breast's sway as it goes; then let go
        IEnumerator GropeHand()
        {
            yield return new WaitForSeconds(1.5f);
            var st = h.Stage;
            var pc = h.Play;
            var tc = h.Touch;
            var hands = h.Hands;
            var bust = HIK.Find(st.Cha.objBodyBone.transform, "cf_d_bust01_L") ?? HIK.Find(st.Cha.objBodyBone.transform, "cf_j_spine03");
            var sp = Camera.main.WorldToScreenPoint(bust.position);
            var log = new StringBuilder();
            float next = 0f;
            string pressed = "";
            yield return RealInput.Press(new Vector2(sp.x, Screen.height - sp.y), 4f, 6, t =>
            {
                if (pressed.Length == 0 && tc.Pressing.Length > 0) pressed = tc.Pressing;
                if (t >= next)
                {
                    var on = hands.ObjectOn("muneL") ?? hands.ObjectOn("muneR");
                    log.Append(string.Format("\n  {0:F1}s play {1} hand {2} ctrl {3} speed {4:F2} force {5:F4} xy {6}", t, pc.Active ? Json.Str(pc.Current, "id") : "-",
                        on != null && on.gameObject.activeInHierarchy, hands.Ctrl, hands.SpeedItemForCheck, hands.BustForceForCheck, hands.XYForCheck("muneL").ToString("F3")));
                    next += 0.25f;
                }
            });
            yield return new WaitForSeconds(0.5f);
            lines.AppendLine("grope on '" + pressed + "' (" + (RealInput.LastError.Length > 0 ? RealInput.LastError : "ok") + "):" + log + "\n  let go: ctrl " + hands.Ctrl
                + " force " + hands.BustForceForCheck.ToString("F4"));
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // her shoes (PR #9 adopted, "no way to take her shoes off"): the outfit's indoor (7) and outdoor (8) shoes, the shoes type
        // shown, whether 服 lists 靴, and the panel's change half / off / on - the states, the shoes' objects shown, pictures
        // --muse-probe=pushup: the Push Up plugin (KK_Pushup) and a coordinate card worn (Coordinates.Wear): her nipple and areola
        // depth (the body's shapes 13 and 11) and the plugin's bra / top data - at the start, the card saved here from what she
        // wears as a coordinate saves itself outside the maker (only the sideloader's data) and as the maker saves it (each
        // plugin's controller writing its coordinate data first), each worn (2026-11-07: the first turns Push Up off - its
        // defaults for a card without its data - the nipples through the clothes; the second keeps it)
        const System.Reflection.BindingFlags PF = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        IEnumerator PushupTry()
        {
            yield return new WaitForSeconds(2f);
            var cha = h.Stage.Cha;
            lines.AppendLine("heroine " + cha.fileParam.fullname + " coordinate " + cha.fileStatus.coordinateType + " clothes " + string.Join(",", Array.ConvertAll(cha.fileStatus.clothesState, b => b.ToString())));
            MonoBehaviour ctl = null;
            foreach (var c in cha.GetComponents<MonoBehaviour>()) if (c != null && c.GetType().FullName.IndexOf("Pushup", StringComparison.OrdinalIgnoreCase) >= 0) ctl = c;
            lines.AppendLine("controller " + (ctl != null ? ctl.GetType().FullName : "none"));
            if (ctl != null)
                foreach (var m in ctl.GetType().GetMembers(PF | System.Reflection.BindingFlags.DeclaredOnly))
                    lines.AppendLine("  member " + m.MemberType + " " + m.Name);
            PushupState("start", cha, ctl);
            lines.AppendLine("chara ext: " + ExtKeys(cha.chaFile) + " | nowCoordinate ext: " + ExtKeys(cha.nowCoordinate));
            // the card, saved as the check saves it
            var co = h.Coords;
            string dir = System.IO.Path.Combine(co.Folder, "_probe_" + DateTime.Now.Ticks);
            Directory.CreateDirectory(dir);
            string card = System.IO.Path.Combine(dir, "probe_outfit.png");
            var now = cha.nowCoordinate;
            byte[] png = now.pngData;
            now.pngData = new Texture2D(8, 8).EncodeToPNG();
            now.SaveFile(card);
            now.pngData = png;
            var back = new ChaFileCoordinate();
            back.LoadFile(card);
            lines.AppendLine("saved card ext: " + ExtKeys(back));
            co.Scan();
            string id = dir.Substring(co.Folder.Length).TrimStart('\\', '/').Replace('\\', '/') + "/probe_outfit.png";
            // (a) the card saved as the maker saves it: each plugin's controller writes its coordinate data first
            var withData = new ChaFileCoordinate();
            withData.LoadFile(card);
            var saved = new List<string>();
            foreach (var c in cha.GetComponents<MonoBehaviour>())
            {
                if (c == null) continue;
                for (var t = c.GetType(); t != null; t = t.BaseType)
                    if (t.FullName == "KKAPI.Chara.CharaCustomFunctionController") { saved.Add(c.GetType().Name + " " + Invoke(c, "OnCoordinateBeingSaved", new object[] { withData })); break; }
            }
            string card2 = System.IO.Path.Combine(dir, "probe_outfit_data.png");
            withData.SaveFile(card2);
            var back2 = new ChaFileCoordinate();
            back2.LoadFile(card2);
            lines.AppendLine("saved by the controllers: " + string.Join("; ", saved.ToArray()));
            lines.AppendLine("card with data ext: " + ExtKeys(back2));
            co.Scan();
            string id2 = dir.Substring(co.Folder.Length).TrimStart('\\', '/').Replace('\\', '/') + "/probe_outfit_data.png";
            string err = co.Wear(id2, "user");
            yield return new WaitForSeconds(1.0f);
            PushupState("(a) the card with data worn '" + err + "'", cha, ctl);
            // (b) the card without (saved as a coordinate saves itself, outside the maker): the plugin's own defaults
            err = co.Wear(id, "user");
            yield return new WaitForSeconds(1.0f);
            PushupState("(b) the card without data worn '" + err + "'", cha, ctl);
            // back as she was: the card with the data again
            err = co.Wear(id2, "user");
            yield return new WaitForSeconds(1.0f);
            PushupState("the card with data again '" + err + "'", cha, ctl);
            try { Directory.Delete(dir, true); } catch (Exception) { }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        void PushupState(string when, ChaControl cha, MonoBehaviour ctl)
        {
            var sb = new StringBuilder(when + ": file shape 11 " + cha.fileBody.shapeValueBody[11].ToString("F3") + " 13 " + cha.fileBody.shapeValueBody[13].ToString("F3")
                                       + " | now 11 " + cha.GetShapeBodyValue(11).ToString("F3") + " 13 " + cha.GetShapeBodyValue(13).ToString("F3"));
            if (ctl != null)
                foreach (var name in new[] { "CurrentBraData", "CurrentTopData", "BaseData" })
                {
                    object d = null;
                    var p = ctl.GetType().GetProperty(name, PF);
                    if (p != null) d = p.GetValue(ctl, null);
                    else { var f = ctl.GetType().GetField(name, PF); if (f != null) d = f.GetValue(ctl); }
                    sb.Append(" | " + name + " " + Fields(d));
                }
            lines.AppendLine(sb.ToString());
        }

        static string Fields(object d)
        {
            if (d == null) return "null";
            var parts = new List<string>();
            foreach (var f in d.GetType().GetFields(PF)) if (f.FieldType == typeof(bool) || f.FieldType == typeof(float)) parts.Add(f.Name + "=" + Convert.ToString(f.GetValue(d), System.Globalization.CultureInfo.InvariantCulture));
            foreach (var p in d.GetType().GetProperties(PF)) if ((p.PropertyType == typeof(bool) || p.PropertyType == typeof(float)) && p.GetIndexParameters().Length == 0)
                parts.Add(p.Name + "=" + Convert.ToString(p.GetValue(d, null), System.Globalization.CultureInfo.InvariantCulture));
            return "{" + string.Join(" ", parts.ToArray()) + "}";
        }

        static string ExtKeys(object file)
        {
            try
            {
                Type es = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (es == null) es = a.GetType("ExtensibleSaveFormat.ExtendedSave");
                if (es == null) return "no ExtendedSave";
                var m = es.GetMethod("GetAllExtendedData", new[] { file.GetType() });
                if (m == null && file is ChaFileControl) m = es.GetMethod("GetAllExtendedData", new[] { typeof(ChaFile) });
                if (m == null) return "no GetAllExtendedData(" + file.GetType().Name + ")";
                var all = m.Invoke(null, new[] { file }) as System.Collections.IDictionary;
                if (all == null) return "none";
                var keys = new List<string>();
                foreach (var k in all.Keys) keys.Add(Convert.ToString(k));
                keys.Sort();
                return string.Join(",", keys.ToArray());
            }
            catch (Exception e) { return "error " + e.Message; }
        }

        static string Invoke(object o, string name, object[] args)
        {
            if (o == null) return "no controller";
            foreach (var m in o.GetType().GetMethods(PF))
            {
                if (m.Name != name || m.GetParameters().Length != args.Length) continue;
                try { m.Invoke(o, args); return "ok " + m.DeclaringType.Name; }
                catch (Exception e) { return "error " + (e.InnerException ?? e).Message; }
            }
            return "no " + name;
        }

        // --muse-probe=mouth (PR #46 §6, PR #47: the mouth with the words, openness only): the original's mouth controls - the
        // character's mouth / voice / lip members, the face blend shape (fbsCtrl) and its mouth control (MouthCtrl: open min /
        // max, fixed rate, open rate) - then the openness driven a while by each way found (the voice value, the mouth's open
        // rate), under her face now and under one with a small open max, and whether the character puts it back each frame
        IEnumerator MouthTry()
        {
            yield return new WaitForSeconds(2f);
            var cha = h.Stage.Cha;
            const System.Reflection.BindingFlags A = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
            for (var t = cha.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (var m in t.GetMembers(A))
                {
                    string n = m.Name.ToLowerInvariant();
                    if (n.Contains("mouth") || n.Contains("voice") || n.Contains("lip") || n.Contains("fbs") || n.Contains("tongue"))
                        lines.AppendLine("CHA " + t.Name + " " + m.MemberType + " " + m);
                }
            object fbs = MemberOf(cha, "fbsCtrl");
            lines.AppendLine("fbsCtrl " + (fbs != null ? fbs.GetType().FullName : "none"));
            if (fbs != null)
                for (var t = fbs.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                    foreach (var m in t.GetMembers(A)) lines.AppendLine("FBS " + t.Name + " " + m.MemberType + " " + m);
            object mouth = fbs != null ? MemberOf(fbs, "MouthCtrl") : null;
            lines.AppendLine("MouthCtrl " + (mouth != null ? mouth.GetType().FullName : "none"));
            if (mouth != null)
                for (var t = mouth.GetType(); t != null && t != typeof(object); t = t.BaseType)
                    foreach (var m in t.GetMembers(A)) lines.AppendLine("MOUTH " + t.Name + " " + m.MemberType + " " + m);
            lines.AppendLine("status: mouthPtn " + cha.fileStatus.mouthPtn + " mouthOpenMax " + cha.fileStatus.mouthOpenMax + " mouthFixed " + cha.fileStatus.mouthFixed);
            MouthValues("start", mouth);
            // the voice value (FaceBlendShape's), set each frame for a second
            var setVoice = fbs != null ? FindMethod(fbs.GetType(), "voice") : null;
            lines.AppendLine("voice setter: " + (setVoice != null ? setVoice.ToString() : "none"));
            foreach (float max in new[] { 1f, 0.3f })
            {
                cha.ChangeMouthOpenMax(max);
                cha.ChangeMouthFixed(false);
                yield return null;
                MouthValues("open max " + max + " before", mouth);
                if (setVoice != null)
                {
                    float until = Time.realtimeSinceStartup + 1f;
                    var seen = new List<string>();
                    while (Time.realtimeSinceStartup < until)
                    {
                        try { setVoice.Invoke(fbs, new object[] { 1f }); } catch (Exception e) { seen.Add("error " + (e.InnerException ?? e).Message); break; }
                        yield return new WaitForEndOfFrame();
                        seen.Add(MouthNow(mouth));
                    }
                    lines.AppendLine("voice 1 each frame, open max " + max + ": " + string.Join(" | ", seen.GetRange(Math.Max(0, seen.Count - 3), Math.Min(3, seen.Count)).ToArray()));
                    yield return new WaitForSeconds(0.5f);
                    MouthValues("voice no longer set, open max " + max, mouth);
                }
                // the mouth's open rate itself, set each frame
                var rate = mouth != null ? FindSetter(mouth.GetType(), "openrate") : null;
                lines.AppendLine("open rate setter: " + (rate != null ? rate.ToString() : "none"));
                if (rate != null)
                {
                    float until = Time.realtimeSinceStartup + 1f;
                    var seen = new List<string>();
                    while (Time.realtimeSinceStartup < until)
                    {
                        try { rate.Invoke(mouth, new object[] { 1f }); } catch (Exception e) { seen.Add("error " + (e.InnerException ?? e).Message); break; }
                        yield return new WaitForEndOfFrame();
                        seen.Add(MouthNow(mouth));
                    }
                    lines.AppendLine("open rate 1 each frame, open max " + max + ": " + string.Join(" | ", seen.GetRange(Math.Max(0, seen.Count - 3), Math.Min(3, seen.Count)).ToArray()));
                }
            }
            // a fixed mouth: whether either way still moves it
            cha.ChangeMouthOpenMax(0.6f);
            cha.ChangeMouthFixed(true);
            yield return null;
            MouthValues("fixed at 0.6", mouth);
            if (setVoice != null)
            {
                for (int i = 0; i < 20; i++) { setVoice.Invoke(fbs, new object[] { 0f }); yield return new WaitForEndOfFrame(); }
                MouthValues("fixed, voice 0 for 20 frames", mouth);
            }
            cha.ChangeMouthFixed(false);
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        static object MemberOf(object o, string name)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, PF | System.Reflection.BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
                var f = t.GetField(name, PF | System.Reflection.BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        static System.Reflection.MethodInfo FindMethod(Type t, string part)
        {
            foreach (var m in t.GetMethods(PF))
                if (m.Name.ToLowerInvariant().Contains(part) && m.Name.StartsWith("Set") && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(float)) return m;
            return null;
        }

        static System.Reflection.MethodInfo FindSetter(Type t, string part)
        {
            foreach (var m in t.GetMethods(PF))
                if (m.Name.ToLowerInvariant().Replace("_", "").Contains(part) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(float)) return m;
            return null;
        }

        // the mouth control's numbers now (each float / int / bool field)
        static string MouthNow(object mouth)
        {
            if (mouth == null) return "-";
            var parts = new List<string>();
            for (var t = mouth.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(PF | System.Reflection.BindingFlags.DeclaredOnly))
                    if (f.FieldType == typeof(float) || f.FieldType == typeof(int) || f.FieldType == typeof(bool))
                        parts.Add(f.Name + "=" + Convert.ToString(f.GetValue(mouth), System.Globalization.CultureInfo.InvariantCulture));
            return string.Join(" ", parts.ToArray());
        }

        void MouthValues(string when, object mouth) { lines.AppendLine(when + ": " + MouthNow(mouth)); }

        // --muse-probe=mouth2: the voice value set where the original sets it (a postfix on ChaControl.UpdateBlendShapeVoice),
        // and Studio's own mouth settings on the character (its lip sync, its mouth opening) - whether the mouth follows: fixed
        // as it starts, unfixed, a 4 Hz open / close; two small pictures of her face (shut, open)
        static float probeVoice = -1f;
        static int voiceCalls;

        static void AfterVoiceForProbe(ChaControl __instance)
        {
            voiceCalls++;
            if (probeVoice >= 0f && __instance.fbsCtrl != null) __instance.fbsCtrl.SetVoiceVaule(probeVoice);
        }

        IEnumerator Mouth2Try()
        {
            yield return new WaitForSeconds(2f);
            var oci = h.Stage.Heroine;
            var cha = h.Stage.Cha;
            const System.Reflection.BindingFlags A = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var t in new[] { oci.GetType(), oci.oiCharInfo.GetType() })
                for (var u = t; u != null && u != typeof(object); u = u.BaseType)
                    foreach (var m in u.GetMembers(A))
                    {
                        string n = m.Name.ToLowerInvariant();
                        if (n.Contains("mouth") || n.Contains("lip") || n.Contains("voice")) lines.AppendLine("STUDIO " + u.Name + " " + m.MemberType + " " + m);
                    }
            lines.AppendLine("oiCharInfo lipSync " + MemberOf(oci.oiCharInfo, "lipSync") + " mouthOpen " + MemberOf(oci.oiCharInfo, "mouthOpen"));
            var harmony = new HarmonyLib.Harmony("musestudio.probe.mouth");
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(ChaControl), "UpdateBlendShapeVoice"), null, new HarmonyLib.HarmonyMethod(typeof(Probe), "AfterVoiceForProbe"));
            var mouth = cha.mouthCtrl;
            voiceCalls = 0;
            yield return new WaitForSeconds(1f);
            lines.AppendLine("UpdateBlendShapeVoice calls in 1 s: " + voiceCalls + " (frames " + (int)(1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f)) + "/s)");
            lines.AppendLine("start: " + MouthNow(mouth));
            probeVoice = 1f;
            yield return new WaitForSeconds(0.5f);
            lines.AppendLine("voice 1 as it starts (fixed?): " + MouthNow(mouth));
            cha.ChangeMouthFixed(false);
            yield return new WaitForSeconds(0.5f);
            lines.AppendLine("voice 1, unfixed: " + MouthNow(mouth));
            yield return FaceShot("mouth_open.png");
            probeVoice = 0f;
            yield return new WaitForSeconds(0.5f);
            lines.AppendLine("voice 0, unfixed: " + MouthNow(mouth));
            yield return FaceShot("mouth_shut.png");
            // a 4 Hz open / close: the open rate read at each frame's end
            var seen = new List<string>();
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 1f)
            {
                float t = Time.realtimeSinceStartup - t0;
                probeVoice = Mathf.Repeat(t * 4f, 1f) < 0.5f ? 1f : 0f;
                yield return new WaitForEndOfFrame();
                var f = mouth.GetType().GetField("openRate", PF);
                seen.Add(t.ToString("F2") + ":" + probeVoice + "->" + (f != null ? Convert.ToSingle(f.GetValue(mouth)).ToString("F2") : "?"));
            }
            lines.AppendLine("4 Hz: " + string.Join(" ", seen.ToArray()));
            // her face's open max small: the opening scaled
            cha.ChangeMouthOpenMax(0.3f);
            probeVoice = 1f;
            yield return new WaitForSeconds(0.5f);
            lines.AppendLine("open max 0.3, voice 1: " + MouthNow(mouth));
            // Studio's lip sync off / on (the character's own setting) and its mouth opening
            var lipOff = oci.GetType().GetMethod("ChangeLipSync", PF);
            if (lipOff != null)
            {
                lipOff.Invoke(oci, new object[] { false });
                yield return new WaitForSeconds(0.5f);
                lines.AppendLine("Studio lip sync off, voice 1: " + MouthNow(mouth));
                lipOff.Invoke(oci, new object[] { true });
                yield return new WaitForSeconds(0.5f);
                lines.AppendLine("Studio lip sync on, voice 1: " + MouthNow(mouth));
            }
            probeVoice = -1f;
            harmony.UnpatchSelf();
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        IEnumerator FaceShot(string name)
        {
            var cha = h.Stage.Cha;
            var head = HIK.Find(cha.objBodyBone.transform, "cf_j_head").position + Vector3.up * 0.06f;
            yield return Shot(head + cha.transform.forward * 0.45f, head, 30f, name);
        }

        // --muse-probe=maps (PR #46 §4.1, PR #47: the rooms registered at start-up): Studio's map list (number, name, bundle,
        // scene); the sideloader's studio resolution entries for the mod maps (their GUID and original slot: the rooms' stable
        // ids, D3); the game's own map list (map/list/mapinfo: its numbers name the H points' prefabs) matched to Studio's by
        // the scene; then the lazy load measured: rooms not kept loaded one after another beside the scene as Rooms.Load does
        // (seconds, private memory after each), then unloaded (the memory after) - the limit's first numbers
        IEnumerator MapsTry(string arg)
        {
            yield return new WaitForSeconds(2f);
            var maps = Singleton<Studio.Info>.Instance.dicMapLoadInfo;
            var sbm = new StringBuilder();
            int mods = 0;
            foreach (var kv in maps)
            {
                var m = kv.Value;
                if (kv.Key >= 100000000) mods++;
                sbm.AppendLine("MAP\t" + kv.Key + "\t" + m.name + "\t" + m.bundlePath + "\t" + m.fileName + "\t" + Fields(m));
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "maps.txt"), sbm.ToString());
            lines.AppendLine("studio maps " + maps.Count + " (numbers from 100000000: " + mods + ")");
            // the sideloader's studio entries
            Type uar = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (uar == null) uar = a.GetType("Sideloader.AutoResolver.UniversalAutoResolver");
            var studio = uar != null ? (MemberOfType(uar, "LoadedStudioResolutionInfo") as System.Collections.IEnumerable) : null;
            int n = 0, mapsHit = 0;
            var sbs = new StringBuilder();
            if (studio != null)
                foreach (var info in studio)
                {
                    n++;
                    var t = info.GetType();
                    int local = Convert.ToInt32(MemberOfAny(info, "LocalSlot") ?? -1);
                    bool isMap = maps.ContainsKey(local);
                    if (n <= 3 || isMap)
                    {
                        var parts = new List<string>();
                        foreach (var f in t.GetFields(PF)) parts.Add(f.Name + "=" + Convert.ToString(f.GetValue(info)));
                        foreach (var p in t.GetProperties(PF)) if (p.GetIndexParameters().Length == 0) { try { parts.Add(p.Name + "=" + Convert.ToString(p.GetValue(info, null))); } catch (Exception) { } }
                        sbs.AppendLine((isMap ? "MAPRES\t" : "RES\t") + string.Join(" ", parts.ToArray()) + (isMap ? "\tname=" + maps[local].name : ""));
                    }
                    if (isMap) mapsHit++;
                }
            File.WriteAllText(System.IO.Path.Combine(outDir, "resolve.txt"), sbs.ToString());
            lines.AppendLine("sideloader studio entries " + n + ", whose LocalSlot is a Studio map number " + mapsHit + " (type " + (studio != null ? studio.GetType().FullName : "none") + ")");
            // the game's map list
            Type mi = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { var t = a.GetType("MapInfo"); if (t != null && mi == null) mi = t; }
            lines.AppendLine("MapInfo type " + (mi != null ? mi.FullName : "none"));
            var sbg = new StringBuilder();
            var gameByScene = new Dictionary<string, int>();
            if (mi != null)
            {
                List<string> bundles = null;
                try { bundles = CommonLib.GetAssetBundleNameListFromPath("map/list/mapinfo/", true); } catch (Exception e) { lines.AppendLine("mapinfo list: " + e.Message); }
                lines.AppendLine("mapinfo bundles " + (bundles != null ? bundles.Count : -1));
                if (bundles != null)
                    foreach (var bundle in bundles)
                    {
                        UnityEngine.Object[] assets = null;
                        try { assets = AssetBundleManager.LoadAllAsset(bundle, mi, null).GetAllAssets<UnityEngine.Object>(); }
                        catch (Exception e) { sbg.AppendLine("BUNDLE\t" + bundle + "\terror " + e.Message); continue; }
                        foreach (var asset in assets ?? new UnityEngine.Object[0])
                        {
                            var param = MemberOfAny(asset, "param") as System.Collections.IEnumerable;
                            if (param == null) continue;
                            foreach (var p in param)
                            {
                                int no = Convert.ToInt32(MemberOfAny(p, "No") ?? -1);
                                string ab = Convert.ToString(MemberOfAny(p, "AssetBundleName")), an = Convert.ToString(MemberOfAny(p, "AssetName"));
                                sbg.AppendLine("GAME\t" + bundle + "\t" + no + "\t" + MemberOfAny(p, "MapName") + "\t" + ab + "\t" + an);
                                gameByScene[(ab + "|" + an).ToLowerInvariant()] = no;
                            }
                        }
                    }
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "gamemaps.txt"), sbg.ToString());
            int matched = 0, withPoints = 0;
            var sbx = new StringBuilder();
            foreach (var kv in maps)
            {
                int g;
                if (!gameByScene.TryGetValue((kv.Value.bundlePath + "|" + kv.Value.fileName).ToLowerInvariant(), out g)) continue;
                matched++;
                int pts = HData.Points(g).Count;
                if (pts > 0) withPoints++;
                sbx.AppendLine("MATCH\t" + kv.Key + "\t" + kv.Value.name + "\tgame " + g + "\tpoints " + pts);
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "matched.txt"), sbx.ToString());
            lines.AppendLine("studio maps matched to a game map by the scene " + matched + ", with H points " + withPoints);
            // the lazy load: arg = how many rooms (default 8), the largest first by nothing in particular - the list's order
            // (arg: how many - "mods:N" the mod maps only)
            bool modsOnly = arg.StartsWith("mods:");
            int want = 8;
            int.TryParse(modsOnly ? arg.Substring(5) : arg, out want);
            var tried = new List<int>();
            foreach (var kv in maps)
            {
                if (tried.Count >= want) break;
                if (modsOnly && kv.Key < 100000000) continue;
                if (!h.Rooms.HasMap(kv.Key) && kv.Key != Singleton<Studio.Map>.Instance.no) tried.Add(kv.Key);
            }
            long ws;
            long mem0 = Rooms.MemoryMB(out ws);
            lines.AppendLine("lazy: before " + mem0 + " MB private, " + ws + " MB working set; kept rooms " + string.Join(",", new List<string>(h.Rooms.Keys).ToArray()));
            var loaded = new List<UnityEngine.SceneManagement.Scene>();
            foreach (int no in tried)
            {
                var info = maps[no];
                var before = new HashSet<int>();
                for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++) before.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(s).GetHashCode());
                float t0 = Time.realtimeSinceStartup;
                try { AssetBundleManager.LoadLevel(info.bundlePath, info.fileName, true, info.manifest); }
                catch (Exception e) { lines.AppendLine("lazy " + no + " error " + e.Message); continue; }
                UnityEngine.SceneManagement.Scene sc = new UnityEngine.SceneManagement.Scene();
                while (Time.realtimeSinceStartup - t0 < 60f)
                {
                    for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++) { var c = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s); if (!before.Contains(c.GetHashCode()) && c.isLoaded) sc = c; }
                    if (sc.IsValid()) break;
                    yield return null;
                }
                float took = Time.realtimeSinceStartup - t0;
                if (sc.IsValid()) { foreach (var go in sc.GetRootGameObjects()) go.SetActive(false); loaded.Add(sc); }
                yield return null;
                long mem = Rooms.MemoryMB(out ws);
                lines.AppendLine(string.Format("lazy {0} {1}: {2:F2} s, {3} MB private (+{4}), {5} MB working set", no, info.name, took, mem, mem - mem0, ws));
            }
            long memAll = Rooms.MemoryMB(out ws);
            foreach (var sc in loaded) { var op = UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(sc); while (op != null && !op.isDone) yield return null; }
            var un = Resources.UnloadUnusedAssets();
            while (!un.isDone) yield return null;
            GC.Collect();
            yield return new WaitForSeconds(1f);
            long memAfter = Rooms.MemoryMB(out ws);
            lines.AppendLine(string.Format("lazy: {0} loaded {1} MB, unloaded {2} MB ({3} MB working set)", loaded.Count, memAll, memAfter, ws));
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        static object MemberOfType(Type t, string name)
        {
            const System.Reflection.BindingFlags S = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var p = t.GetProperty(name, S);
            if (p != null) return p.GetValue(null, null);
            var f = t.GetField(name, S);
            return f != null ? f.GetValue(null) : null;
        }

        static object MemberOfAny(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, PF | System.Reflection.BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
                var p = t.GetProperty(name, PF | System.Reflection.BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            }
            return null;
        }

        // --muse-probe=drinks: every original map loaded in turn, its wait points' motions with items written - the motion's
        // layer, id, state and motion (the action controller's state), each item's bundle / asset / type / offsets / own motion;
        // then the personalities' action controllers (action/list/chara)
        IEnumerator DrinksTry()
        {
            yield return new WaitForSeconds(2f);
            // (the original's own wait points: map/waitpoint/ - prefabs, each map's points under them; ActionGame.CharacterPosSet)
            int wps = 0, withItems = 0;
            List<string> files = null;
            try { files = CommonLib.GetAssetBundleNameListFromPath("map/waitpoint/", true); files.Sort(); }
            catch (Exception e) { lines.AppendLine("waitpoint list: " + e.Message); }
            lines.AppendLine("waitpoint bundles " + (files != null ? files.Count : 0));
            foreach (var file in files ?? new List<string>())
            {
                GameObject[] prefabs = null;
                try { prefabs = AssetBundleManager.LoadAllAsset(file, typeof(GameObject), null).GetAllAssets<GameObject>(); }
                catch (Exception e) { lines.AppendLine("BUNDLE " + file + ": " + e.Message); continue; }
                foreach (var pf in prefabs ?? new GameObject[0])
                {
                    GameObject go = null;
                    try
                    {
                        go = UnityEngine.Object.Instantiate(pf);
                        go.SetActive(false);
                        foreach (var wp in go.GetComponentsInChildren<ActionGame.WaitPoint>(true))
                        {
                            wps++;
                            foreach (var par in wp.parameterList)
                                foreach (var m in par.motionList)
                                {
                                    bool any = m.itemList.Count > 0 || (m.motionElse != null && m.motionElse.itemList.Count > 0);
                                    if (!any) continue;
                                    withItems++;
                                    lines.AppendLine(string.Format("WP {0} map {1} {2} layer '{3}' id {4} state {5} motion '{6}' else '{7}' offset {8} {9}", file, wp.MapNo, wp.name, par.layer, m.ID, m.state,
                                        m.motion, m.motionElse != null ? m.motionElse.motion : "", m.offsetPos.ToString("F3"), m.offsetAngle.ToString("F1")));
                                    foreach (var it in m.itemList) lines.AppendLine("  ITEM " + ItemLine(it));
                                    if (m.motionElse != null) foreach (var it in m.motionElse.itemList) lines.AppendLine("  ELSE ITEM " + ItemLine(it));
                                }
                        }
                    }
                    catch (Exception e) { lines.AppendLine("PREFAB " + file + " " + (pf != null ? pf.name : "-") + ": " + e.Message); }
                    if (go != null) UnityEngine.Object.Destroy(go);
                }
                yield return null;
            }
            lines.AppendLine("wait points " + wps + ", motions with items " + withItems);
            // the map items' bundle: every prefab's name (a can, a glass?)
            try
            {
                foreach (var go in AssetBundleManager.LoadAllAsset("action/mapitem/00.unity3d", typeof(GameObject), null).GetAllAssets<GameObject>())
                    lines.AppendLine("MAPITEM " + go.name);
            }
            catch (Exception e) { lines.AppendLine("mapitem: " + e.Message); }
            // her personality's action controller: its drink states and clips
            try
            {
                var a = AssetBundleManager.LoadAsset("action/animator/00.unity3d", "c00", typeof(RuntimeAnimatorController), null);
                var rac = a != null ? a.GetAsset<RuntimeAnimatorController>() : null;
                if (rac != null)
                {
                    foreach (var c in rac.animationClips) if (c != null && c.name.ToLower().Contains("drink")) lines.AppendLine("CLIP " + c.name + " " + c.length.ToString("F2") + "s");
                    var go = new GameObject("MuseStudio_drinks");
                    var an = go.AddComponent<Animator>();
                    an.runtimeAnimatorController = rac;
                    foreach (var s in new[] { "Drink", "Drink1", "Drink2", "Drink3", "Drink4", "Drink5", "Drink_Can", "Drink3_End" })
                        lines.AppendLine("STATE " + s + " " + an.HasState(0, Animator.StringToHash(s)));
                    UnityEngine.Object.Destroy(go);
                }
                else lines.AppendLine("c00: none");
            }
            catch (Exception e) { lines.AppendLine("controller: " + e.Message); }
            // the personalities' action controllers
            try
            {
                foreach (var bn in CommonLib.GetAssetBundleNameListFromPath("action/list/chara/", true))
                    foreach (var ci in AssetBundleManager.LoadAllAsset(bn, typeof(ActionGame.CharaInfo), null).GetAllAssets<ActionGame.CharaInfo>())
                        foreach (var pa in ci.param)
                            lines.AppendLine(string.Format("CHARA {0} personality {1} motion {2} | {3}", bn, pa.Personality, pa.MotionBundle, pa.MotionAsset));
            }
            catch (Exception e) { lines.AppendLine("charainfo: " + e.Message); }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        static string ItemLine(Illusion.Game.Elements.EasyLoader.Item.Data it)
        {
            if (it == null) return "-";
            var mo = it.motion;
            return string.Format("{0} | {1} type {2} pos {3} angle {4} motion {5} | {6} | {7}", it.bundle, it.asset, it.type, it.offsetPos.ToString("F4"), it.offsetAngle.ToString("F2"),
                                 mo != null ? mo.bundle : "", mo != null ? mo.asset : "", mo != null ? mo.state : "");
        }

        IEnumerator ShoesTry()
        {
            yield return new WaitForSeconds(1.5f);
            var cha = h.Stage.Cha;
            var parts = cha.nowCoordinate.clothes.parts;
            lines.AppendLine(string.Format("parts 7 id {0} / 8 id {1}; shoesType {2}; worn {3}", parts[7].id, parts.Length > 8 ? parts[8].id : -1, cha.fileStatus.shoesType,
                string.Join(",", Clothes.Worn(cha).ConvertAll(k => Clothes.Keys[k]).ToArray())));
            yield return ShoeShot("before");
            foreach (var v in new[] { "half", "off", "on" })
            {
                var ch = h.ChangeClothes(new Dictionary<string, object> { { "co_shoes", v } }, "user");
                yield return new WaitForSeconds(0.8f);
                yield return ShoeShot(v);
                lines.AppendLine("  -> changed " + string.Join(",", ch.ConvertAll(o => Convert.ToString(o)).ToArray()));
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        IEnumerator ShoeShot(string when)
        {
            var cha = h.Stage.Cha;
            var sb = new StringBuilder(when + ": state 7 " + cha.fileStatus.clothesState[7] + " 8 " + (cha.fileStatus.clothesState.Length > 8 ? cha.fileStatus.clothesState[8].ToString() : "-"));
            foreach (int k in new[] { 7, 8 })
            {
                if (cha.objClothes == null || k >= cha.objClothes.Length || cha.objClothes[k] == null) { sb.Append(" | obj " + k + " none"); continue; }
                var o = cha.objClothes[k];
                int shown = 0, all = 0;
                foreach (var r in o.GetComponentsInChildren<Renderer>(true)) { all++; if (r.enabled && r.gameObject.activeInHierarchy) shown++; }
                sb.Append(string.Format(" | obj {0} {1} active {2} renderers {3}/{4}", k, o.name, o.activeInHierarchy, shown, all));
            }
            lines.AppendLine(sb.ToString());
            var foot = HIK.Find(cha.objBodyBone.transform, "cf_j_foot_L").position;
            yield return Shot(foot + cha.transform.forward * 0.9f + Vector3.up * 0.25f, foot, 40f, "shoes_" + when + ".png");
        }

        // --muse-probe=ikdata:<play id>,...: the original's motion IK data of the plays' female (and male) parts as MotionIKData
        // reads it (a binary TextAsset: its text is empty) - per state the hands' and feet's target (whose body by sex, which
        // bone, the weights) and the bends
        IEnumerator IkData(string ids)
        {
            yield return new WaitForSeconds(1.0f);
            foreach (var id in ids.Split(','))
            {
                var row = h.Play.Row(id);
                var hi = row == null ? null : HData.Info((int)Json.Num(row, "mode"), (int)Json.Num(row, "motion_id"));
                if (hi == null) { lines.AppendLine(id + ": no row"); continue; }
                foreach (var part in new[] { "female", "male" })
                {
                    string file = part == "female" ? hi.paramFemale.path.file : hi.paramMale.path.file;
                    lines.AppendLine(string.Format("== {0} {1} {2} file '{3}'", id, Json.Str(row, "name"), part, file));
                    if (string.IsNullOrEmpty(file)) continue;
                    var ta = GlobalMethod.LoadAllFolderInOneFile<TextAsset>("h/list/", file, null);
                    if (ta == null) { lines.AppendLine("  (no asset)"); continue; }
                    lines.AppendLine("  bytes " + ta.bytes.Length);
                    var mik = new MotionIK(h.Stage.Cha, null);
                    bool ok = false;
                    try { ok = mik.LoadData(ta); } catch (Exception e) { lines.AppendLine("  read: " + e.Message); }
                    if (!ok || mik.data == null || mik.data.states == null) { lines.AppendLine("  read failed"); continue; }
                    foreach (var s in mik.data.states)
                    {
                        var sb = new StringBuilder("  " + s.name + ":");
                        var names = new[] { "LH", "RH", "LF", "RF" };
                        var parts = new[] { s.leftHand, s.rightHand, s.leftFoot, s.rightFoot };
                        for (int i = 0; i < 4; i++)
                        {
                            var p = parts[i];
                            if (p == null) { sb.Append(" " + names[i] + " -"); continue; }
                            sb.Append(string.Format(" {0}[sex {1} {2} pos {3:F2} ang {4:F2} | bend {5} {6:F2}]", names[i], p.param2.sex, p.param2.target, p.param2.weightPos,
                                p.param2.weightAng, p.param3.chein, p.param3.weight));
                        }
                        sb.Append(" frames " + (s.frames != null ? s.frames.Length : 0));
                        lines.AppendLine(sb.ToString());
                    }
                }
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        void KokanMeasure(string pass, string when)
        {
            var st = h.Stage;
            var root = st.Cha.transform;
            var sb = new StringBuilder();
            sb.Append(string.Format("{0} {1} state {2} hands {3} root {4}", pass, when, h.Play.LastState, h.Hands.Ctrl, root.position.ToString("F3")));
            foreach (var n in new[] { "cf_j_hips", "cf_j_waist01", "cf_j_thigh00_L", "cf_j_leg01_L", "cf_j_foot_L", "cf_j_toes_L", "cf_j_leg01_R", "cf_j_foot_R", "cf_j_toes_R" })
            {
                var t = HIK.Find(st.Cha.objBodyBone.transform, n);
                if (t == null) continue;
                var local = root.InverseTransformPoint(t.position);        // (her own frame: y above her root - the floor)
                sb.Append(string.Format(" | {0} {1}", n.Substring(5), local.ToString("F3")));
            }
            lines.AppendLine(sb.ToString());
        }

        IEnumerator KokanShots(string name)
        {
            var st = h.Stage;
            var c = st.Cha.transform.position + Vector3.up * 0.55f;
            yield return Shot(c + st.Cha.transform.right * 2.0f, c, 45f, "kokan_" + name + "_side.png");
            yield return Shot(c + st.Cha.transform.forward * 2.0f, c, 45f, "kokan_" + name + "_front.png");
        }

        // every play (or those whose id starts with one of the comma-separated prefixes): the play camera as the play starts
        // (cam_<id>.png), its numbers in probe.txt
        IEnumerator CamAll(string only)
        {
            yield return new WaitForSeconds(1.5f);
            var pc = h.Play;
            var done = new HashSet<string>();
            var all = pc.List(null);
            var prefixes = only.Length > 0 ? only.Split(',') : new string[0];
            foreach (var room in h.Stage.LocationKeys())
            {
                if (room != h.Stage.Location) yield return h.Stage.SetLocation(room);
                yield return new WaitForSeconds(0.5f);
                foreach (var row in all)
                {
                    string id = Json.Str(row, "id");
                    if (done.Contains(id)) continue;
                    if (prefixes.Length > 0 && System.Array.Find(prefixes, p => id.StartsWith(p)) == null) continue;
                    var spots = pc.Spots(row);
                    if (spots.Count == 0) continue;
                    string err = "";
                    foreach (var body in Json.Str(row, "group") == "lesbian" ? new[] { "female" } : new[] { "male", "female" })
                    {
                        pc.WhoForCheck = new Dictionary<string, Dictionary<string, object>> {
                            { "character", new Dictionary<string, object> { { "body", "female" }, { "futanari", false } } },
                            { "player", new Dictionary<string, object> { { "body", body }, { "futanari", false } } } };
                        err = pc.Begin(id, "probe", spots[0].Id);
                        if (err.Length == 0) break;
                    }
                    if (err.Length > 0) continue;
                    done.Add(id);
                    yield return new WaitForSeconds(1.6f);
                    pc.Camera(true);
                    yield return new WaitForSeconds(0.5f);
                    Application.CaptureScreenshot(System.IO.Path.Combine(outDir, "cam_" + id + ".png"));
                    lines.AppendLine(id + " " + Json.Str(row, "name") + " [" + Json.Str(row, "group") + "] " + room + " | " + pc.FrameInfo);
                    yield return new WaitForSeconds(0.3f);
                    pc.End("probe");
                    yield return new WaitForSeconds(0.2f);
                }
            }
            pc.WhoForCheck = null;
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MuseStudioPlugin.QuitMarker)); File.WriteAllText(MuseStudioPlugin.QuitMarker, "quit"); } catch (Exception) { }
            Application.Quit();
        }

        // the game kept open for some seconds and closed (the launcher's hover focus tried against a real game window, PR #19)
        IEnumerator Hold(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MuseStudioPlugin.QuitMarker)); File.WriteAllText(MuseStudioPlugin.QuitMarker, "quit"); } catch (Exception) { }
            Application.Quit();
        }

        // every play photographed for Muse (the user's request of 2026-11-06, a collection, not a check: Muse writes each play's
        // summary from them for the play table). Each play where some room has a spot for it, the bodies it takes (the user's
        // body male, else female; her futanari only when nothing else does), in its main motion (an insertion put in, the rest
        // at the slow pace), the app's UI off and the user's body as a body (no silhouette): the play's own camera
        // (<id>_<name>.png) and from the side, both bodies in (<id>_<name>_横.png); photos.json lists them and the plays no room
        // takes. Original-derived pictures: kept on this machine (for_muse/, never the repository).
        IEnumerator Photos(string only)
        {
            yield return new WaitForSeconds(1.5f);
            var pc = h.Play;
            var st = h.Stage;
            var ui = h.UiForCheck;
            bool silWas = h.YourSilhouette.Value, shownWas = h.YourBodyShown.Value;
            h.YourSilhouette.Value = false;
            h.YourBodyShown.Value = true;
            ui.ShotMode = true;
            var done = new HashSet<string>();
            var list = new List<object>();
            var all = pc.List(null);
            var bodies = new[] { new[] { "male", "no" }, new[] { "female", "no" }, new[] { "male", "futa" }, new[] { "female", "futa" } };
            foreach (var room in st.LocationKeys())
            {
                if (room != st.Location) yield return st.SetLocation(room);
                yield return new WaitForSeconds(0.8f);
                string roomName = Json.Str(st.LocationInfo(room), "name", room);
                foreach (var row in all)
                {
                    string id = Json.Str(row, "id"), name = Json.Str(row, "name"), group = Json.Str(row, "group");
                    if (done.Contains(id)) continue;
                    if (only.Length > 0 && Array.Find(only.Split(','), q => id.StartsWith(q)) == null) continue;
                    var spots = pc.Spots(row);
                    if (spots.Count == 0) continue;
                    string err = "", used = "";
                    foreach (var b in bodies)
                    {
                        if (group == "lesbian" && b[0] == "male") continue;
                        pc.WhoForCheck = new Dictionary<string, Dictionary<string, object>> {
                            { "character", new Dictionary<string, object> { { "body", "female" }, { "futanari", b[1] == "futa" } } },
                            { "player", new Dictionary<string, object> { { "body", b[0] }, { "futanari", false } } } };
                        err = pc.Begin(id, "probe", spots[0].Id);
                        if (err.Length == 0) { used = "あなた：" + (b[0] == "male" ? "男性の体" : "女性の体") + (b[1] == "futa" ? "、彼女：ふたなり" : ""); break; }
                    }
                    if (err.Length > 0) continue;
                    done.Add(id);
                    yield return new WaitForSeconds(1.2f);
                    if (group == "sonyu") pc.Insert(1);
                    else pc.SetPace(1, "probe");
                    float t0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - t0 < 10f && (pc.LastState ?? "").IndexOf("Loop", StringComparison.Ordinal) < 0) yield return null;
                    yield return new WaitForSeconds(1.5f);
                    string state = pc.LastState;
                    string file = FileName(id + "_" + name);
                    pc.Camera(true);
                    yield return new WaitForSeconds(1.0f);
                    // (the bones read at the frame's end: earlier in a frame they stand in a rest pose)
                    yield return new WaitForEndOfFrame();
                    var pts = BodyPoints();
                    Vector3 c = Middle(pts);
                    var cut = Cutaway(Camera.main.transform.position, pts, c);
                    yield return new WaitForEndOfFrame();
                    Application.CaptureScreenshot(System.IO.Path.Combine(outDir, file + ".png"));
                    yield return new WaitForSeconds(0.5f);
                    foreach (var r in cut) if (r != null) r.enabled = true;
                    // from the side, both bodies filling the picture: the distance from how far they spread, the first side (her
                    // right, left, front, back) with no wall within it; failing all, her right pulled in front of the wall
                    float spread = 0f;
                    foreach (var q in pts) spread = Mathf.Max(spread, (q - c).magnitude);
                    float fov = 40f, want = Mathf.Clamp(spread * 1.25f / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), 1.2f, 3.2f);
                    var t = st.Cha.transform;
                    Vector3 eye = Vector3.zero;
                    bool found = false;
                    foreach (var dir in new[] { t.right, -t.right, t.forward, -t.forward })
                    {
                        Vector3 e = c + dir * want + Vector3.up * (0.45f + want * 0.3f);       // (from above the low furniture, looking down)
                        float hitAt;
                        if (!Stage.MapRay(c, e, out hitAt)) { eye = e; found = true; break; }
                    }
                    if (!found)
                    {
                        Vector3 e = c + t.right * want + Vector3.up * 0.35f;
                        float hitAt;
                        if (Stage.MapRay(c, e, out hitAt)) { float keep = Mathf.Max(0.8f, hitAt - 0.15f); fov = Mathf.Clamp(fov * want / keep, 40f, 80f); e = c + (e - c).normalized * keep; }
                        eye = e;
                    }
                    st.SetCamera(eye, c, fov, 0f);
                    yield return new WaitForSeconds(0.6f);
                    yield return new WaitForEndOfFrame();
                    cut = Cutaway(Camera.main.transform.position, pts, c);
                    yield return new WaitForEndOfFrame();
                    Application.CaptureScreenshot(System.IO.Path.Combine(outDir, file + "_横.png"));
                    yield return new WaitForSeconds(0.5f);
                    foreach (var r in cut) if (r != null) r.enabled = true;
                    list.Add(new Dictionary<string, object> { { "id", id }, { "name", name }, { "group", group }, { "room", roomName }, { "spot", spots[0].Id },
                        { "bodies", used }, { "state", state }, { "photos", new List<object> { file + ".png", file + "_横.png" } } });
                    lines.AppendLine(id + " " + name + " [" + group + "] " + roomName + " / " + spots[0].Id + " | " + used + " | " + state);
                    pc.End("probe");
                    yield return new WaitForSeconds(0.3f);
                }
            }
            var none = new List<object>();
            foreach (var row in all)
                if (!done.Contains(Json.Str(row, "id"))) none.Add(new Dictionary<string, object> { { "id", Json.Str(row, "id") }, { "name", Json.Str(row, "name") }, { "group", Json.Str(row, "group") } });
            pc.WhoForCheck = null;
            ui.ShotMode = false;
            h.YourSilhouette.Value = silWas;
            h.YourBodyShown.Value = shownWas;
            File.WriteAllText(System.IO.Path.Combine(outDir, "photos.json"), Json.Write(new Dictionary<string, object> { { "photographed", list }, { "not_photographed", none } }), new System.Text.UTF8Encoding(false));
            lines.AppendLine("photographed " + list.Count + ", no room for " + none.Count);
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MuseStudioPlugin.QuitMarker)); File.WriteAllText(MuseStudioPlugin.QuitMarker, "quit"); } catch (Exception) { }
            Application.Quit();
        }

        // the bodies' points (hips, head, hands, knees, feet of both), read at a frame's end
        List<Vector3> BodyPoints()
        {
            var pts = new List<Vector3>();
            foreach (var cha in new[] { h.Stage.Cha, h.Stage.Partner != null ? h.Stage.Partner.charInfo : null })
            {
                if (cha == null || cha.objBodyBone == null) continue;
                foreach (var n in new[] { "cf_j_hips", "cf_j_head", "cf_j_hand_L", "cf_j_hand_R", "cf_j_leg01_L", "cf_j_leg01_R", "cf_j_leg03_L", "cf_j_leg03_R" })
                {
                    var tr = HIK.Find(cha.objBodyBone.transform, n);
                    if (tr != null) pts.Add(tr.position);
                }
            }
            if (pts.Count == 0) pts.Add(h.Stage.Cha.transform.position + Vector3.up * 0.6f);
            return pts;
        }

        static Vector3 Middle(List<Vector3> pts)
        {
            Vector3 min = pts[0], max = pts[0];
            foreach (var q in pts) { min = Vector3.Min(min, q); max = Vector3.Max(max, q); }
            return (min + max) * 0.5f;
        }

        // the room's things between the camera and the bodies (a shelf, a desk, a wall seen from outside) hidden for the shot: a
        // renderer of the scene, not of a body, whose bounds a line from the camera to a body point (or the middle) meets well
        // before it, holding neither end
        static List<Renderer> Cutaway(Vector3 eye, List<Vector3> pts, Vector3 middle)
        {
            var hidden = new List<Renderer>();
            var targets = new List<Vector3>(pts) { middle };
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || r.GetComponentInParent<ChaControl>() != null) continue;
                var b = r.bounds;
                bool holds = false;           // (a bed, a chair, the floor under a body: kept)
                foreach (var target in targets) if (b.Contains(target)) { holds = true; break; }
                if (holds) continue;
                if (b.Contains(eye))
                {
                    // the camera inside a thing's box: a piece of furniture hidden (the room itself - over 3 m - kept)
                    if (Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) < 3f) { r.enabled = false; hidden.Add(r); }
                    continue;
                }
                foreach (var target in targets)
                {
                    Vector3 d = target - eye;
                    float dist = d.magnitude, hit;
                    if (dist < 0.3f) continue;
                    if (b.IntersectRay(new Ray(eye, d / dist), out hit) && hit < dist - 0.3f) { r.enabled = false; hidden.Add(r); break; }
                }
            }
            return hidden;
        }

        static string FileName(string s)
        {
            var bad = System.IO.Path.GetInvalidFileNameChars();
            var b = new System.Text.StringBuilder();
            foreach (char ch in s) b.Append(Array.IndexOf(bad, ch) >= 0 || ch == ' ' ? '_' : ch);
            return b.ToString();
        }

        // every play: where her eyes and face go a moment in (the original's eye / neck list or not; at the camera or not)
        IEnumerator Gazes()
        {
            yield return new WaitForSeconds(1.5f);
            var pc = h.Play;
            var done = new HashSet<string>();
            var all = pc.List(null);
            foreach (var room in h.Stage.LocationKeys())
            {
                if (room != h.Stage.Location) yield return h.Stage.SetLocation(room);
                yield return new WaitForSeconds(0.5f);
                foreach (var row in all)
                {
                    string id = Json.Str(row, "id");
                    if (done.Contains(id)) continue;
                    var spots = pc.Spots(row);
                    if (spots.Count == 0) continue;
                    string err = "";
                    foreach (var body in new[] { "male", "female" })
                    {
                        pc.WhoForCheck = new Dictionary<string, Dictionary<string, object>> {
                            { "character", new Dictionary<string, object> { { "body", "female" }, { "futanari", false } } },
                            { "player", new Dictionary<string, object> { { "body", body }, { "futanari", false } } } };
                        err = pc.Begin(id, "probe", spots[0].Id);
                        if (err.Length == 0) break;
                    }
                    if (err.Length > 0) continue;
                    done.Add(id);
                    pc.Apply(new Dictionary<string, object> { { "pace", 1 } });
                    yield return new WaitForSeconds(1.3f);
                    var cha = h.Stage.Cha;
                    var cam = Camera.main != null ? Camera.main.transform : null;
                    var et = cha.eyeLookCtrl != null ? cha.eyeLookCtrl.target : null;
                    var nt = cha.neckLookCtrl != null ? cha.neckLookCtrl.target : null;
                    bool eyeCam = cha.fileStatus.eyesLookPtn == 1 && et == cam, neckCam = cha.fileStatus.neckLookPtn == 1 && nt == cam;
                    lines.AppendLine(string.Format("{0}{1} {2} [{3}] listed {4} partner {5} eyes {6}:{7} neck {8}:{9}", eyeCam || neckCam ? "CAM " : "    ", id,
                        Json.Str(row, "name"), Json.Str(row, "group"), pc.HerGazeListed, h.Stage.Partner != null,
                        cha.fileStatus.eyesLookPtn, et == null ? "-" : et == cam ? "camera" : et.name, cha.fileStatus.neckLookPtn, nt == null ? "-" : nt == cam ? "camera" : nt.name));
                    pc.End("probe");
                    yield return new WaitForSeconds(0.3f);
                }
            }
            foreach (var row in all) if (!done.Contains(Json.Str(row, "id"))) lines.AppendLine("NOT RUN " + Json.Str(row, "id") + " " + Json.Str(row, "name"));
            pc.WhoForCheck = null;
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MuseStudioPlugin.QuitMarker)); File.WriteAllText(MuseStudioPlugin.QuitMarker, "quit"); } catch (Exception) { }
            Application.Quit();
        }

        // the plays' framing cameras (id, id!r reversed, id~f your woman's body), your see-through body, the open menu's colour
        IEnumerator PlayCams(string ids)
        {
            yield return new WaitForSeconds(1.5f);
            var pc = h.Play;
            var ui = h.UiForCheck;
            bool bodyShots = false;
            foreach (var spec in ids.Split(','))
            {
                string id = spec.Replace("!r", "").Replace("~f", "");
                bool rev = spec.Contains("!r"), fem = spec.Contains("~f");
                pc.WhoForCheck = new Dictionary<string, Dictionary<string, object>> {
                    { "character", new Dictionary<string, object> { { "body", "female" }, { "futanari", false } } },
                    { "player", new Dictionary<string, object> { { "body", fem ? "female" : "male" }, { "futanari", false } } } };
                var row = pc.Row(id);
                var spots = row != null ? pc.Spots(row) : new List<Spot>();
                if (row != null && spots.Count == 0)
                    foreach (var room in h.Stage.LocationKeys())
                    {
                        if (room == h.Stage.Location) continue;
                        yield return h.Stage.SetLocation(room);
                        spots = pc.Spots(row);
                        if (spots.Count > 0) break;
                    }
                string err = row == null ? "unknown" : spots.Count > 0 ? pc.Begin(id, "probe", spots[0].Id, rev) : "no spot";
                lines.AppendLine(spec + " " + Json.Str(row, "name") + " " + err);
                if (err.Length > 0) continue;
                pc.Apply(new Dictionary<string, object> { { "pace", 1 } });
                yield return new WaitForSeconds(2.5f);
                pc.Camera(true);
                yield return new WaitForSeconds(0.6f);
                lines.AppendLine("  framed on " + pc.FramedOn + " | " + pc.FrameInfo + " | camera at " + Camera.main.transform.position + " fwd " + Camera.main.transform.forward
                    + " | her hips " + h.Stage.Cha.objBodyBone.transform.position + " base " + h.Stage.Heroine.oiCharInfo.changeAmount.pos + " key " + h.Stage.CameraKey);
                foreach (var t in h.Stage.Cha.objBodyBone.GetComponentsInChildren<Transform>(true))
                    if (t.name == "cf_j_head" || t.name == "cf_j_hips" || t.name == "cf_j_leg01_L" || t.name == "cf_j_hand_R") lines.AppendLine("    bone " + t.name + " " + t.position + " path " + t.parent.name);
                lines.AppendLine("    animator culling " + h.Stage.Cha.animBody.cullingMode + " state " + pc.LastState);
                yield return new WaitForEndOfFrame();
                foreach (var t in h.Stage.Cha.objBodyBone.GetComponentsInChildren<Transform>(true))
                    if (t.name == "cf_j_head" || t.name == "cf_j_hips") lines.AppendLine("    end of frame: bone " + t.name + " " + t.position);
                Application.CaptureScreenshot(System.IO.Path.Combine(outDir, "cam_" + spec.Replace("!", "_").Replace("~", "_") + ".png"));
                yield return new WaitForSeconds(0.5f);
                if (!bodyShots && h.Stage.Partner != null)
                {
                    bodyShots = true;
                    pc.SetYourBody("translucent", true);
                    yield return new WaitForSeconds(0.5f);
                    lines.AppendLine("  translucent: " + h.YourTranslucent.Value + " silhouette " + h.YourSilhouette.Value);
                    Application.CaptureScreenshot(System.IO.Path.Combine(outDir, "body_translucent.png"));
                    yield return new WaitForSeconds(0.5f);
                    pc.SetYourBody("translucent", false);
                    ui.MenuForCheck("play");
                    yield return new WaitForSeconds(0.5f);
                    Application.CaptureScreenshot(System.IO.Path.Combine(outDir, "ui_open.png"));
                    yield return new WaitForSeconds(0.5f);
                    ui.MenuForCheck("");
                }
                pc.End("probe");
                yield return new WaitForSeconds(0.6f);
            }
            pc.WhoForCheck = null;
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MuseStudioPlugin.QuitMarker)); File.WriteAllText(MuseStudioPlugin.QuitMarker, "quit"); } catch (Exception) { }
            Application.Quit();
        }

        // the character menu: the picked card's picture with ID 設定・性格設定, then each list
        IEnumerator CardLinks()
        {
            yield return new WaitForSeconds(1.5f);
            var ui = h.UiForCheck;
            ui.PickForCheck(h.Chars.CardPath);
            ui.MenuForCheck("chara");
            yield return new WaitForSeconds(1.0f);
            Application.CaptureScreenshot(System.IO.Path.Combine(outDir, "cards_menu.png"));
            yield return new WaitForSeconds(0.5f);
            ui.OpenLink(h.Chars.CardPath, "person");
            yield return new WaitForSeconds(0.5f);
            Application.CaptureScreenshot(System.IO.Path.Combine(outDir, "cards_person.png"));
            yield return new WaitForSeconds(0.5f);
            ui.OpenLink(h.Chars.CardPath, "persona");
            yield return new WaitForSeconds(0.5f);
            Application.CaptureScreenshot(System.IO.Path.Combine(outDir, "cards_persona.png"));
            yield return new WaitForSeconds(0.5f);
            ui.LinkKind = "";
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MuseStudioPlugin.QuitMarker)); File.WriteAllText(MuseStudioPlugin.QuitMarker, "quit"); } catch (Exception) { }
            Application.Quit();
        }

        // the toilet outing: its phases, the clip's length and time, the sounds started (the pee at In's end)
        IEnumerator ToiletTry()
        {
            yield return new WaitForSeconds(1.5f);
            h.Outing.SecondsForCheck = 16f;
            lines.AppendLine("GO " + h.Outing.Go("toilet"));
            float t0 = Time.realtimeSinceStartup;
            string lastPhase = "";
            var seen = new HashSet<string>();
            float lastLog = -1f, outAt = -1f, shotWaitUntil = -1f;
            int shotN = 0;
            bool loopShot = false;
            var outTimes = new[] { 0.5f, 10.0f, 11.0f, 11.5f, 11.9f, 12.2f, 12.6f, 13.5f };
            while (h.Outing.Out && Time.realtimeSinceStartup - t0 < 60f)
            {
                var anim = h.Stage.Cha.animBody;
                var ai = anim != null ? anim.GetCurrentAnimatorStateInfo(0) : default(AnimatorStateInfo);
                var ci = anim != null ? anim.GetCurrentAnimatorClipInfo(0) : null;
                string clip = ci != null && ci.Length > 0 && ci[0].clip != null ? ci[0].clip.name + " " + ci[0].clip.length.ToString("F2") + "s" : "-";
                float t = Time.realtimeSinceStartup - t0;
                if (h.Outing.Phase != lastPhase || t - lastLog > 0.5f)
                {
                    lines.AppendLine(string.Format("{0:F2} phase {1} norm {2:F3} clip {3} In {4} Loop {5} Out {6}", t, h.Outing.Phase, ai.normalizedTime, clip, ai.IsName("In"), ai.IsName("Loop"), ai.IsName("Out")));
                    lastPhase = h.Outing.Phase;
                    lastLog = t;
                }
                foreach (var au in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                    if (au.isPlaying && au.clip != null && seen.Add(au.clip.name)) lines.AppendLine(string.Format("{0:F2} SOUND {1} (phase {2} norm {3:F3})", t, au.clip.name, h.Outing.Phase, ai.normalizedTime));
                // (in the loop: her lap close, from before her)
                if (ai.IsName("Loop") && !loopShot && ai.normalizedTime > 0.3f)
                {
                    loopShot = true;
                    var hpL = h.Stage.Heroine.oiCharInfo.changeAmount.pos;
                    var fwL = h.Stage.Cha.transform.forward;
                    h.Stage.SetCamera(hpL + fwL * 0.9f + Vector3.up * 0.75f, hpL + Vector3.up * 0.45f + fwL * 0.15f, 40f, 0f);
                    shotWaitUntil = t + 0.5f;
                }
                if (loopShot && shotWaitUntil > 0f && t >= shotWaitUntil)
                {
                    shotWaitUntil = -1f;
                    Application.CaptureScreenshot(System.IO.Path.Combine(outDir, "loop_lap.png"));
                    lines.AppendLine(string.Format("{0:F2} SHOT loop_lap lower {1}", t, string.Join(",", Array.ConvertAll(h.Stage.Cha.fileStatus.clothesState, b => b.ToString()))));
                }
                // (the Out motion photographed from its clip's start, the camera before her: when she pulls her shorts up)
                if (ai.IsName("Out"))
                {
                    if (outAt < 0f)
                    {
                        outAt = t;
                        var hp = h.Stage.Heroine.oiCharInfo.changeAmount.pos;
                        var fw = h.Stage.Cha.transform.forward;
                        h.Stage.SetCamera(hp + fw * 1.3f + Vector3.up * 1.0f + h.Stage.Cha.transform.right * 0.3f, hp + Vector3.up * 0.75f, 45f, 0f);
                    }
                    if (t - outAt >= outTimes[Math.Min(shotN, outTimes.Length - 1)] && shotN < outTimes.Length)
                    {
                        Application.CaptureScreenshot(System.IO.Path.Combine(outDir, string.Format("out_{0:00}_{1:F1}s.png", shotN, outTimes[shotN])));
                        lines.AppendLine(string.Format("{0:F2} SHOT out_{1:00} norm {2:F3} lower {3}", t, shotN, ai.normalizedTime, string.Join(",", Array.ConvertAll(h.Stage.Cha.fileStatus.clothesState, b => b.ToString()))));
                        shotN++;
                    }
                }
                yield return null;
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MuseStudioPlugin.QuitMarker)); File.WriteAllText(MuseStudioPlugin.QuitMarker, "quit"); } catch (Exception) { }
            Application.Quit();
        }

        // the full screen (the checks keep their window): full, then framed, then full - each one's size
        IEnumerator ScreenTry()
        {
            yield return new WaitForSeconds(1.0f);
            foreach (bool f in new[] { true, false, true })
            {
                yield return h.StartCoroutine(ScreenMode.Apply(f));
                yield return new WaitForSeconds(1.5f);
                lines.AppendLine(string.Format("SCREEN full {0}: {1}x{2} (display {3}x{4}) fullScreen {5}", f, Screen.width, Screen.height,
                    Display.main.systemWidth, Display.main.systemHeight, Screen.fullScreen));
                Application.CaptureScreenshot(System.IO.Path.Combine(outDir, "screen_" + (f ? "full" : "framed") + ".png"));
                yield return new WaitForSeconds(0.5f);
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            // (a real quit, so the window's registry values are put back: ScreenMode.Restore)
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MuseStudioPlugin.QuitMarker)); File.WriteAllText(MuseStudioPlugin.QuitMarker, "quit"); } catch (Exception) { }
            Application.Quit();
        }

        // a mode's positions: their sound lists (paramFemale.fileSe) and the lists' rows (the state, the timing, the sound)
        IEnumerator HSeList(string spec)
        {
            yield return new WaitForSeconds(1.0f);
            int mode = int.Parse(spec);
            for (int id = 0; id < 200; id++)
            {
                var a = HData.Info(mode, id);
                if (a == null) continue;
                lines.AppendLine(string.Format("POS {0} '{1}' se '{2}' female {3}", a.id, a.nameAnimation, a.paramFemale.fileSe, a.paramFemale.path.file));
                if (string.IsNullOrEmpty(a.paramFemale.fileSe)) continue;
                string text = GlobalMethod.LoadAllListText("h/list/", a.paramFemale.fileSe, null);
                if (string.IsNullOrEmpty(text)) continue;
                string[,] rows;
                GlobalMethod.GetListString(text, out rows);
                for (int i = 0; i < rows.GetLength(0); i++)
                {
                    var cells = new List<string>();
                    for (int j = 0; j < rows.GetLength(1); j++) cells.Add(rows[i, j]);
                    lines.AppendLine("  ROW " + string.Join(" | ", cells.ToArray()));
                }
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        IEnumerator AlSizes(string spec)
        {
            var st = h.Stage;
            var p = spec.Split(',');
            int fg = int.Parse(p[0]), mg = int.Parse(p[1]), cat = int.Parse(p[2]);
            yield return new WaitForSeconds(1.5f);
            string err = h.Play.Begin("sonyu_00", "probe", "here");
            lines.AppendLine("begin " + err);
            yield return new WaitForSeconds(1.0f);
            var noF = new Dictionary<string, int>();
            var noM = new Dictionary<string, int>();
            foreach (var pre in new[] { "S_", "M_", "L_" })
                foreach (var stn in new[] { "Idle", "WLoop1" })
                {
                    noF[pre + stn] = StudioAnim.No(fg, cat, pre + stn);
                    noM[pre + stn] = StudioAnim.No(mg, cat, pre + stn);
                }
            lines.AppendLine("states: " + string.Join(",", new List<string>(noF.Keys).ToArray()));
            foreach (var size in new[] { "S_", "M_", "L_" })
            {
                foreach (var state in new[] { "Idle", "WLoop1" })
                {
                    int nf, nm;
                    if (!noF.TryGetValue(size + state, out nf) || !noM.TryGetValue(size + state, out nm) || nf < 0 || nm < 0) { lines.AppendLine(size + state + " missing"); continue; }
                    st.Heroine.LoadAnime(fg, cat, nf, 0f);
                    st.Partner.LoadAnime(mg, cat, nm, 0f);
                    yield return new WaitForSeconds(1.2f);
                    var fh = Find(st.Cha.objBodyBone.transform, "cf_j_hips");
                    var mh = Find(st.Partner.charInfo.objBodyBone.transform, "cf_j_hips");
                    var root = st.Cha.transform.position;
                    lines.AppendLine(string.Format("{0}{1}: her hips {2} his hips {3} apart {4:F3} her height {5:F2}", size, state,
                        (fh.position - root).ToString("F3"), (mh.position - root).ToString("F3"), Vector3.Distance(fh.position, mh.position), st.Cha.fileBody.shapeValueBody[0]));
                    if (state == "WLoop1")
                    {
                        h.Play.Camera(true);
                        yield return new WaitForSeconds(0.6f);
                        Application.CaptureScreenshot(System.IO.Path.Combine(outDir, "probe_al_" + cat + "_" + size + ".png"));
                        yield return new WaitForSeconds(0.6f);
                    }
                }
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // --muse-probe=speed:<play id>,<state>,...: each state at animeSpeed 1 - the animator's "speed" parameter, the state's
        // speed and its multiplier (1 when the state does not use the parameter), its length
        IEnumerator Speeds(string spec)
        {
            var parts = spec.Split(',');
            yield return new WaitForSeconds(1.5f);
            string err = h.Play.Begin(parts[0], "probe", "here");
            lines.AppendLine("begin " + parts[0] + " " + err);
            yield return new WaitForSeconds(1.0f);
            var anim = h.Stage.Cha.animBody;
            foreach (var p in anim.parameters) lines.AppendLine("param " + p.name + " " + p.type + " default " + p.defaultFloat);
            for (int i = 1; i < parts.Length; i++)
            {
                h.Play.PlayState(parts[i], 1f);
                yield return new WaitForSeconds(1.0f);
                var si = anim.GetCurrentAnimatorStateInfo(0);
                float sp = 0f;
                try { sp = anim.GetFloat("speed"); } catch (Exception) { }
                lines.AppendLine(string.Format("{0}: param speed {1:F2}, state speed {2:F2}, multiplier {3:F2}, length {4:F2}, animator.speed {5:F2}",
                    parts[i], sp, si.speed, si.speedMultiplier, si.length, anim.speed));
                // the same with the parameter at 3 (as the original at its top): does the state follow it?
                try { anim.SetFloat("speed", 3f); } catch (Exception) { }
                yield return new WaitForSeconds(0.3f);
                si = anim.GetCurrentAnimatorStateInfo(0);
                lines.AppendLine(string.Format("{0} with speed 3: multiplier {1:F2}", parts[i], si.speedMultiplier));
                try { anim.SetFloat("speed", sp); } catch (Exception) { }
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // --muse-probe=eyeneck:<mode>: the original's positions of an H mode (4: peeping) - their motion, control kind and
        // eye / neck list (h/list <fileMotionNeck>), and the eyeneckbase rows those lists use
        // the original's talk poses per personality (communication/info_*: pose_NN - a name, then the motion's bundle / asset /
        // state and its IK), and the ADV's talk controller's states
        IEnumerator Poses()
        {
            yield return new WaitForSeconds(1.0f);
            var names = new HashSet<string>();
            var talk = new List<KeyValuePair<string, ExcelData>>();
            foreach (var path in CommonLib.GetAssetBundleNameListFromPath("communication/", true))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path), "info_([0-9]*)")) continue;
                foreach (var name in AssetBundleCheck.GetAllAssetName(path, false, null, false))
                {
                    string an = System.IO.Path.GetFileNameWithoutExtension(name);
                    var m = System.Text.RegularExpressions.Regex.Match(an, "^pose_([0-9]*)$");
                    var c = System.Text.RegularExpressions.Regex.Match(an, "^communication_([0-9]*)$");
                    if (!m.Success && !c.Success) continue;
                    var ex = AssetBundleManager.LoadAsset(path, an, typeof(ExcelData), null).GetAsset<ExcelData>();
                    if (ex == null) continue;
                    if (m.Success)
                        foreach (var prm in ex.list)
                        {
                            lines.AppendLine("POSE " + m.Groups[1].Value + " | " + path + " | " + string.Join(" | ", prm.list.ToArray()));
                            if (prm.list.Count > 0) names.Add(prm.list[0]);
                        }
                    else
                    {
                        // (a personality's list in two bundles - the later version has more rows: that one)
                        int at = talk.FindIndex(tk => tk.Key == c.Groups[1].Value);
                        if (at < 0) talk.Add(new KeyValuePair<string, ExcelData>(c.Groups[1].Value, ex));
                        else if (talk[at].Value.list.Count < ex.list.Count) talk[at] = new KeyValuePair<string, ExcelData>(c.Groups[1].Value, ex);
                    }
                }
                AssetBundleManager.UnloadAssetBundle(path, false, null, false);
            }
            // the original's talk: how often each personality's lines use each pose (the cell after the line's voice / text)
            var count = new Dictionary<string, int>();
            foreach (var kv in talk)
                foreach (var prm in kv.Value.list)
                    for (int k = 3; k < prm.list.Count; k++)
                        if (names.Contains(prm.list[k]) && prm.list[k - 3] != null && prm.list[k - 3].StartsWith("sound/"))
                        {
                            string key = kv.Key + " | " + prm.list[k];
                            int n;
                            count.TryGetValue(key, out n);
                            count[key] = n + 1;
                        }
            foreach (var kv in count) lines.AppendLine("USE " + kv.Key + " | " + kv.Value);
            // the personalities' names (the game's voice list)
            for (int no = 0; no < 60; no++)
            {
                string nm = CharaSummary.PersonalityName(no);
                if (nm.Length > 0) lines.AppendLine("PERS " + no + " | " + nm);
            }
            // the original's talk motions played on her (the ADV controller, as TalkScene's AnimePlay), pictures
            var st = h.Stage;
            var head = st.Cha.objHeadBone.transform.position;
            var t = st.Cha.transform;
            foreach (var spec in new[] { "adv/motion/controller/adv/00.unity3d|cf_adv_00_00|Stand_14_00|base14", "adv/motion/controller/adv/00.unity3d|cf_adv_00_00|Stand_Joy_03|joy03",
                                          "adv/motion/controller/adv/00.unity3d|cf_adv_00_00|Stand_Ang_00|angry", "adv/motion/controller/adv/14.unity3d|cf_adv_30_00|Stand_30_00|base30" })
            {
                var sp = spec.Split('|');
                string err = "";
                try
                {
                    var mo = new Illusion.Game.Elements.EasyLoader.Motion(sp[0], sp[1], sp[2]);
                    mo.Setting(st.Cha.animBody);
                    mo.Play(st.Cha.animBody);
                    var rac = st.Cha.animBody.runtimeAnimatorController;
                    lines.AppendLine("PLAY " + spec + " controller " + (rac != null ? rac.name + " clips " + rac.animationClips.Length : "none"));
                }
                catch (Exception e) { err = e.Message; }
                if (err.Length > 0) lines.AppendLine("PLAY " + spec + " error " + err);
                yield return new WaitForSeconds(1.5f);
                var info = st.Cha.animBody.GetCurrentAnimatorStateInfo(0);
                lines.AppendLine("  state " + sp[2] + " is " + info.IsName(sp[2]) + " t " + info.normalizedTime.ToString("F2"));
                yield return Shot(head + t.forward * 2.2f + Vector3.up * -0.3f, head + Vector3.up * -0.45f, 40f, "pose_" + sp[3] + ".png");
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        IEnumerator EyeNecks(string spec)
        {
            yield return new WaitForSeconds(1.0f);
            int mode = int.Parse(spec);
            var used = new HashSet<string>();
            for (int id = 0; id < 100; id++)
            {
                var a = HData.Info(mode, id);
                if (a == null) continue;
                lines.AppendLine(string.Format("POS {0} '{1}' numCtrl {2} female {3} / {4} neck '{5}'", a.id, a.nameAnimation, a.numCtrl,
                    a.paramFemale.path.file, a.pathFemaleBase.file, a.paramFemale.fileMotionNeck));
                string text = string.IsNullOrEmpty(a.paramFemale.fileMotionNeck) ? null : GlobalMethod.LoadAllListText("h/list/", a.paramFemale.fileMotionNeck, null);
                if (string.IsNullOrEmpty(text)) continue;
                string[,] rows;
                GlobalMethod.GetListString(text, out rows);
                for (int i = 0; i < rows.GetLength(0); i++)
                {
                    var cells = new List<string>();
                    for (int j = 0; j < rows.GetLength(1); j++) cells.Add(rows[i, j]);
                    lines.AppendLine("  ROW " + string.Join(" | ", cells.ToArray()));
                    for (int j = 3; j <= 7 && j < rows.GetLength(1); j++) used.Add(rows[i, j]);
                }
            }
            // her eye / neck look patterns: their kind (NO_LOOK / TARGET / AWAY / FORWARD; ANIMATION / TARGET / AWAY / FORWARD / FIX)
            var cha = h.Stage.Cha;
            var es = cha.eyeLookCtrl != null && cha.eyeLookCtrl.eyeLookScript != null ? cha.eyeLookCtrl.eyeLookScript.eyeTypeStates : null;
            if (es != null) for (int i = 0; i < es.Length; i++) lines.AppendLine("EYE PTN " + i + ": " + es[i].lookType);
            var ns = cha.neckLookCtrl != null && cha.neckLookCtrl.neckLookScript != null ? cha.neckLookCtrl.neckLookScript.neckTypeStates : null;
            if (ns != null)
                for (int i = 0; i < ns.Length; i++)
                {
                    var n = ns[i];
                    lines.AppendLine(string.Format("NECK PTN {0}: {1} hLimit {2} vLimit {3} breakCorr {4} speed {5} away {6}", i, n.lookType, n.hAngleLimit, n.vAngleLimit,
                        n.limitBreakCorrectionValue, n.leapSpeed, n.limitAway));
                    foreach (var pa in n.aParam)
                        lines.AppendLine(string.Format("    bone {0}: up {1} down {2} min {3} max {4}", pa.name, pa.upBendingAngle, pa.downBendingAngle, pa.minBendingAngle, pa.maxBendingAngle));
                }
            var nl = cha.neckLookCtrl != null ? cha.neckLookCtrl.neckLookScript : null;
            if (nl != null)
            {
                lines.AppendLine("NECK calcAngle " + (nl.boneCalcAngle ? nl.boneCalcAngle.name : "-") + " aim " + (nl.transformAim ? nl.transformAim.name : "-")
                    + " target " + (cha.neckLookCtrl.target ? cha.neckLookCtrl.target.name : "-"));
                foreach (var b in nl.aBones) lines.AppendLine("NECK bone " + (b.neckBone ? b.neckBone.name : "-") + " ref " + (b.referenceCalc ? b.referenceCalc.name : "-"));
            }
            lines.AppendLine("NOW eyes " + cha.fileStatus.eyesLookPtn + " neck " + cha.fileStatus.neckLookPtn);
            string bt = GlobalMethod.LoadAllListText("h/list/", "eyeneckbase", null);
            if (!string.IsNullOrEmpty(bt))
            {
                string[,] b;
                GlobalMethod.GetListString(bt, out b);
                for (int i = 0; i < b.GetLength(0); i++)
                    if (used.Contains(b[i, 0]))
                        lines.AppendLine(string.Format("BASE {0}: behaviour {1} targetNeck {2} targetEye {3} rateNeck {4} rateEye {5}", b[i, 0], b[i, 1], b[i, 2], b[i, 3], b[i, 4], b[i, 5]));
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // --muse-probe=icons: the original's aibu cursor icons (h/list AibuIconTexture: id, 3 sizes) as PNGs, and which icon
        // each aibu layer row (aibulayer_NN: id, icon, use, visible, objects) shows
        IEnumerator Icons()
        {
            yield return new WaitForSeconds(1.0f);
            string text = GlobalMethod.LoadAllListText("h/list/", "AibuIconTexture", null);
            string[,] a;
            GlobalMethod.GetListString(text, out a);
            for (int i = 0; i < a.GetLength(0); i++)
            {
                var cells = new List<string>();
                for (int j = 0; j < a.GetLength(1); j++) cells.Add(a[i, j]);
                lines.AppendLine("ICON " + string.Join(" | ", cells.ToArray()));
                try
                {
                    var tex = CommonLib.LoadAsset<Texture2D>(a[i, 1], a[i, 2], false, string.Empty);
                    if (tex == null) continue;
                    var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(tex, rt);
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var copy = new Texture2D(tex.width, tex.height, TextureFormat.ARGB32, false);
                    copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                    copy.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    File.WriteAllBytes(System.IO.Path.Combine(outDir, "icon_" + a[i, 0] + ".png"), copy.EncodeToPNG());
                    lines.AppendLine("  size " + tex.width + "x" + tex.height);
                }
                catch (Exception e) { lines.AppendLine("  " + e.Message); }
            }
            for (int n = 0; n < 6; n++)
            {
                string lt = GlobalMethod.LoadAllListText("h/list/", "aibulayer_" + n.ToString("00"), null);
                string[,] l;
                GlobalMethod.GetListString(lt, out l);
                for (int i = 0; i < l.GetLength(0); i++) lines.AppendLine(string.Format("LAYER {0} row {1}: icon {2} use {3} objects {4}", n, l[i, 0], l[i, 1], l[i, 2], l[i, 4]));
            }
            File.WriteAllText(System.IO.Path.Combine(outDir, "probe.txt"), lines.ToString());
            File.WriteAllText(System.IO.Path.Combine(outDir, "results.txt"), "CHECK SUMMARY pass=0 fail=0\n");
            MuseStudioPlugin.Log.LogMessage("MuseStudio: probe done MUSE_PROBE_DONE");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var f = Find(t.GetChild(i), name); if (f != null) return f; }
            return null;
        }

        static string PathOf(Transform t)
        {
            string s = t.name;
            for (var p = t.parent; p != null && s.Length < 120; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        IEnumerator Shot(Vector3 eye, Vector3 target, float fov, string name)
        {
            h.Stage.SetCamera(eye, target, fov, 0f);
            yield return new WaitForSeconds(0.8f);
            Application.CaptureScreenshot(System.IO.Path.Combine(outDir, name));
            yield return new WaitForSeconds(0.6f);
        }
    }
}
