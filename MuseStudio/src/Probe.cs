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
