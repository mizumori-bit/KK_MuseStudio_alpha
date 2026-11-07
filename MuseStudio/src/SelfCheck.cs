using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using BepInEx.Configuration;
using Studio;
using UnityEngine;

namespace MuseStudio
{
    // --muse-check=PATH: checks on the running stage against the test bridge the runner started (tools/run_checks.py:
    // a temporary bridge on a spare port, never the one Muse watches). Packages go in through the bridge like Muse's.
    // Writes CHECK PASS / CHECK FAIL / NOTE lines and CHECK SUMMARY to PATH (screenshots next to it), then quits.
    public class SelfCheck
    {
        readonly MuseStudioPlugin h;
        readonly string path;
        readonly StringBuilder lines = new StringBuilder();
        int pass, fail, turn;

        public SelfCheck(MuseStudioPlugin host, string resultPath)
        {
            h = host;
            path = resultPath;
        }

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            lines.AppendLine((ok ? "CHECK PASS " : "CHECK FAIL ") + name + (ok || detail.Length == 0 ? "" : " -- " + detail));
            Flush();
        }

        void Note(string s) { lines.AppendLine("NOTE " + s); Flush(); }

        void Flush() { File.WriteAllText(path, lines.ToString(), new UTF8Encoding(false)); }

        // a request's status alone, with a role's token or none (body: its answer)
        int HttpStatus(string method, string p, string role, out string body)
        {
            body = "";
            var req = (HttpWebRequest)WebRequest.Create(MuseStudioPlugin.BridgeBase() + p);
            req.Method = method;
            req.Proxy = null;
            req.Timeout = 5000;
            if (role != null)
            {
                string t = MuseStudioPlugin.BridgeToken(role);
                if (t.Length > 0) req.Headers["Authorization"] = "Bearer " + t;
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    body = r.ReadToEnd();
                    return (int)resp.StatusCode;
                }
            }
            catch (WebException we)
            {
                var resp = we.Response as HttpWebResponse;
                if (resp == null) return 0;
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = r.ReadToEnd();
                return (int)resp.StatusCode;
            }
        }

        // a request standing in for Muse (Muse's token: MUSE_GUIDE 7.1)
        string Http(string method, string p, object body, string role = "muse")
        {
            var req = (HttpWebRequest)WebRequest.Create(MuseStudioPlugin.BridgeBase() + p);
            req.Method = method;
            req.Proxy = null;
            req.Timeout = 5000;
            string token = MuseStudioPlugin.BridgeToken(role);
            if (token.Length > 0) req.Headers["Authorization"] = "Bearer " + token;
            if (body != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(Json.Write(body));
                req.ContentType = "application/json; charset=utf-8";
                req.ContentLength = data.Length;
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
            }
            catch (WebException we)
            {
                if (we.Response == null) return "";
                using (var r = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        void Send(params Dictionary<string, object>[] blocks)
        {
            turn++;
            Http("POST", "/api/packages", new Dictionary<string, object> { { "session", "check" }, { "turn", turn }, { "blocks", new List<object>(blocks) } });
        }

        static Dictionary<string, object> Line(string text) { return new Dictionary<string, object> { { "type", "dialogue" }, { "text", text } }; }

        // (the match) a creation made again after a change to its card - the checks' cases
        static string Recreate(string creation, Action<ChaFileControl> change)
        {
            var raw = Match.Unzlib(Convert.FromBase64String(creation));
            string path = Path.Combine(Path.Combine(Path.GetTempPath(), "MuseStudio_match"), "check_re.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, raw);
            var f = new ChaFileControl();
            f.LoadCharaFile(path, 255, true, false);
            change(f);
            using (var ms = new MemoryStream())
            {
                f.SaveCharaFile(ms, false);
                File.Delete(path);
                return Convert.ToBase64String(Match.Zlib(ms.ToArray()));
            }
        }

        // a button of the panel drawn now (none: a point off the window - RealInput then presses nothing)
        static Rect Btn(Ui ui, string key)
        {
            Rect r;
            return ui.Buttons.TryGetValue(key, out r) ? r : new Rect(-200f, -200f, 1f, 1f);
        }

        // block_refused of a field for a reason, so far
        int Refusals(string field, string reason)
        {
            int n = 0;
            foreach (var e in AllEvents()) if (Json.Str(e, "type") == "block_refused" && Json.Str(e, "field") == field && Json.Str(e, "reason") == reason) n++;
            return n;
        }

        int EventCount(string type, string key = null, string value = null)
        {
            int n = 0;
            foreach (var e in AllEvents()) if (Json.Str(e, "type") == type && (key == null || Json.Str(e, key) == value)) n++;
            return n;
        }

        // a package with its own fields (the match's): session / turn added
        void SendPkg(Dictionary<string, object> pkg, params Dictionary<string, object>[] blocks)
        {
            turn++;
            pkg["session"] = "check";
            pkg["turn"] = turn;
            pkg["blocks"] = new List<object>(blocks);
            Http("POST", "/api/packages", pkg);
        }

        static Dictionary<string, object> With(Dictionary<string, object> b, string k, object v) { b[k] = v; return b; }

        static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        // her breasts' sway at a point of the checks (the original's dynamic bones: ChaControl.getDynamicBoneBust)
        readonly List<string> bustLog = new List<string>();

        void BustAt(string when)
        {
            var c = MuseStudioPlugin.Instance.Stage.Cha;
            if (c == null) { bustLog.Add(when + ": no her"); return; }
            var l = c.getDynamicBoneBust(ChaInfo.DynamicBoneKind.BreastL);
            var r = c.getDynamicBoneBust(ChaInfo.DynamicBoneKind.BreastR);
            bustLog.Add(when + ": L " + (l != null && l.enabled ? "on" : "off") + " R " + (r != null && r.enabled ? "on" : "off"));
        }

        IEnumerator Until(Func<bool> cond, float seconds)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!cond() && Time.realtimeSinceStartup - t0 < seconds) yield return null;
        }

        IEnumerator Wait(float seconds)
        {
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < seconds) yield return null;
        }

        IEnumerator Shot(string name)
        {
            yield return null;
            Application.CaptureScreenshot(Path.Combine(Path.GetDirectoryName(path), name));
            yield return null;
            yield return null;
        }

        static int CellInt(object v) { return (int)Convert.ToDouble(v is string ? double.Parse((string)v, System.Globalization.CultureInfo.InvariantCulture) : v); }

        static Dictionary<string, Dictionary<string, object>> Bodies(string player, bool playerFuta, bool herFuta)
        {
            return new Dictionary<string, Dictionary<string, object>> {
                { "character", new Dictionary<string, object> { { "body", "female" }, { "futanari", herFuta } } },
                { "player", new Dictionary<string, object> { { "body", player }, { "futanari", playerFuta } } } };
        }

        static Transform Bone(ChaControl cha, string name)
        {
            foreach (var t in cha.objBodyBone.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }

        // every event in the feed (paged: the bridge gives 500 at most a time - a long run has more)
        List<object> AllEvents()
        {
            var all = new List<object>();
            int after = 0;
            for (int page = 0; page < 20; page++)
            {
                var evs = Json.Arr(Json.Parse(Http("GET", "/api/events?limit=500&after=" + after, null)), "events");
                if (evs == null || evs.Count == 0) break;
                all.AddRange(evs);
                after = (int)Json.Num(evs[evs.Count - 1], "id");
                if (evs.Count < 500) break;
            }
            return all;
        }

        // the camera round her head at this angle from her upper body's front (the neck's reference), this far, looking at her;
        // then time for her neck to settle
        IEnumerator CameraAround(float deg, float dist)
        {
            var st = h.Stage;
            var nl = st.Cha.neckLookCtrl.neckLookScript;
            var head = st.Cha.objHeadBone.transform.position;
            var front = Vector3.ProjectOnPlane(nl.boneCalcAngle.forward, Vector3.up).normalized;
            var pos = head + Quaternion.AngleAxis(deg, Vector3.up) * front * dist;
            st.SetCamera(pos, head, 40f, 0f);
            yield return Wait(3.0f);
        }

        // where her face points: degrees from her upper body's front (the neck's reference) round its up, signed
        float NeckYaw()
        {
            var nl = h.Stage.Cha.neckLookCtrl.neckLookScript;
            var up = nl.boneCalcAngle.up;
            Vector3 a = Vector3.ProjectOnPlane(nl.boneCalcAngle.forward, up), b = Vector3.ProjectOnPlane(nl.transformAim.forward, up);
            return Vector3.Angle(a, b) * (Vector3.Dot(up, Vector3.Cross(a, b)) < 0f ? -1f : 1f);
        }

        // her face from her front-right, close (where her eyes look: at the camera or not)
        // her face's aim (the neck's) to the camera, degrees
        float FaceToCamera()
        {
            var cha = h.Stage.Cha;
            if (cha.neckLookCtrl == null || cha.neckLookCtrl.neckLookScript == null || cha.neckLookCtrl.neckLookScript.transformAim == null) return -1f;
            return Vector3.Angle(cha.neckLookCtrl.neckLookScript.transformAim.forward, Camera.main.transform.position - cha.objHeadBone.transform.position);
        }

        // her eyes' angles to her head (the original's eye look), the larger of both eyes' sideways / up-down, degrees
        float EyeAngles()
        {
            float most = 0f;
            var calc = h.Stage.Cha.eyeLookCtrl != null ? h.Stage.Cha.eyeLookCtrl.eyeLookScript : null;
            if (calc == null || calc.eyeObjs == null) return -1f;
            foreach (var eo in calc.eyeObjs)
            {
                var tr = HarmonyLib.Traverse.Create(eo);
                most = Mathf.Max(most, Mathf.Abs(tr.Field("angleH").GetValue<float>()), Mathf.Abs(tr.Field("angleV").GetValue<float>()));
            }
            return most;
        }

        IEnumerator FaceShot(string name)
        {
            var st = h.Stage;
            var head = st.Cha.objHeadBone != null ? st.Cha.objHeadBone.transform.position : st.Heroine.oiCharInfo.changeAmount.pos + Vector3.up * 1.4f;
            var t = st.Cha.transform;
            st.SetCamera(head + t.forward * 0.45f + t.right * 0.45f + Vector3.up * 0.05f, head + Vector3.up * 0.05f, 30f, 0f);
            yield return Wait(1.2f);
            yield return Shot(name);
        }

        // the clips playing, gathered for a while (sounds started while the checks wait elsewhere)
        static IEnumerator Listen(HashSet<string> heard, float seconds)
        {
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < seconds)
            {
                foreach (var au in UnityEngine.Object.FindObjectsOfType<AudioSource>()) if (au.isPlaying && au.clip != null) heard.Add(au.clip.name);
                yield return null;
            }
        }

        // the bridge's last event of a type (null: none)
        Dictionary<string, object> LastEvent(string type)
        {
            Dictionary<string, object> last = null;
            foreach (var e in AllEvents()) if (Json.Str(e, "type", "") == type) last = (Dictionary<string, object>)e;
            return last;
        }

        List<string> EventTypes()
        {
            var o = new List<string>();
            foreach (var e in AllEvents()) o.Add(Json.Str(e, "type", ""));
            return o;
        }

        IEnumerator Fps(string label)
        {
            int vs = QualitySettings.vSyncCount, tf = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            yield return Wait(1f);
            int f0 = Time.frameCount;
            float t0 = Time.realtimeSinceStartup;
            yield return Wait(4f);
            float fps = (Time.frameCount - f0) / (Time.realtimeSinceStartup - t0);
            QualitySettings.vSyncCount = vs;
            Application.targetFrameRate = tf;
            Note(string.Format("fps {0}: {1:F0} (no vsync, {2}x{3})", label, fps, Screen.width, Screen.height));
        }

        public IEnumerator Run()
        {
            var st = h.Stage;
            var bp = h.Blocks;
            var pc = h.Play;
            Note("stage ready at " + Time.realtimeSinceStartup.ToString("F1") + " s");
            Check("dialogue: 自動送り starts on (stage.json advance.auto, the trial run of 2026-11-04)", bp.Auto, "auto " + bp.Auto);
            BustAt("start");
            yield return null;
            Check("keys: the keys one can press shown at the top (the setting, default on: Space, F1, Studio's UI key, the camera's)", h.KeyHints.Value
                  && h.UiForCheck.KeyHintsNow.Contains("F1") && h.UiForCheck.KeyHintsNow.Contains("Space") && h.UiForCheck.KeyHintsNow.Contains("カメラ"), h.UiForCheck.KeyHintsNow);
            // (PR #46 §8) the clock: on by default - this PC's time; the setting off - none
            string ckNow = DateTime.Now.ToString("HH:mm"), ckShown = h.UiForCheck.ClockShown;
            h.ShowClock.Value = false;
            yield return null;
            yield return null;
            string ckOff = h.UiForCheck.ClockShown;
            h.ShowClock.Value = true;
            yield return null;
            yield return null;
            Check("clock: on by default, this PC's time under the presence; the setting off hides it",
                  (ckShown == ckNow || ckShown == DateTime.Now.ToString("HH:mm")) && ckOff == "" && h.UiForCheck.ClockShown.Length == 5,
                  "shown '" + ckShown + "' now " + ckNow + " off '" + ckOff + "'");
            bp.SetAuto(false);                 // (the checks press for each block themselves)
            yield return Wait(1.5f);
            var locs = Data.Load("locations.json");
            int myroom = (int)Json.Num(Json.Obj(Json.Obj(locs, "locations"), "myroom"), "map", -1);
            bool keptMy = h.Rooms.Has("myroom");
            Check("stage: the start location's map (myroom)", (keptMy ? h.Rooms.Shown == "myroom" : Singleton<Map>.Instance.no == myroom) && st.Location == "myroom",
                  Singleton<Map>.Instance.no + " " + h.Rooms.Shown + " " + st.Location);
            string fullname = st.Cha != null ? st.Cha.chaFile.parameter.fullname : "";
            Check("stage: the character from stage.json's card", st.Heroine != null && fullname.Length > 0, "name '" + fullname + "' card " + h.CharacterCardPath());
            Note("character " + fullname + " from " + h.CharacterCardPath());
            var stand = Json.Arr(Json.Obj(Json.Obj(locs, "locations"), "myroom"), "stand");
            var hp = st.Heroine.oiCharInfo.changeAmount.pos;
            Check("stage: she stands at the location's stand", stand != null && Vector3.Distance(hp, h.Rooms.OffsetOf("myroom") + new Vector3(Convert.ToSingle(stand[0]), Convert.ToSingle(stand[1]), Convert.ToSingle(stand[2]))) < 0.01f,
                  hp.ToString());
            Spot bed = null;
            foreach (var sp in st.Spots) if (sp.Name == "ベッド" && bed == null) bed = sp;
            Check("spots: here and the original's H points of the map (a bed among them)", st.Spots.Count > 1 && st.Spots[0].Here && bed != null,
                  st.Spots.Count + " spots");
            Note("spots at myroom: " + string.Join(", ", st.Spots.ConvertAll(sp => sp.Id + "=" + sp.Name + (sp.Aliases.Count > 0 ? "+" + string.Join("+", sp.Aliases.ToArray()) : "")).ToArray()));
            string dupe = "";
            int aliases = 0;
            for (int i = 0; i < st.Spots.Count; i++)
            {
                aliases += st.Spots[i].Aliases.Count;
                for (int j = i + 1; j < st.Spots.Count; j++)
                {
                    Spot sa = st.Spots[i], sb = st.Spots[j];
                    if (!sa.Here && !sb.Here && sa.Name == sb.Name && (sa.Pos - sb.Pos).magnitude <= 0.35f && Quaternion.Angle(sa.Rot, sb.Rot) <= 60f) dupe += sa.Id + "~" + sb.Id + " ";
                }
            }
            Check("spots: the same name within 0.35 m and 60 degrees is one spot (the Godot app's), the merged points' ids kept", dupe.Length == 0, dupe + " (" + aliases + " merged)");
            var keyPatch = HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(typeof(Studio.ShortcutKeyCtrl), "Update"));
            Check("keys: Studio's own shortcut keys (Z undo, C move, Delete, the number keys' cameras...) are off on the stage (Esc and F11 kept)",
                  StudioKeys.OnStage && keyPatch != null && keyPatch.Prefixes.Count > 0);
            Check("window title MuseStudio (process CharaStudio)", WindowTitle.Get() == "MuseStudio", WindowTitle.Get());
            // events through a failing bridge (the review of 2026-11-05, point 3): two answered 500 before saving (tried again
            // after 1 s, 2 s), two saved and their answers lost (sent again: kept once), one refused (400: dropped, not in the way)
            {
                Http("POST", "/api/test/faults", D("fail_500", 2, "drop_after_save", 2));
                for (int i = 0; i < 4; i++)
                {
                    h.Bridge.Event("check_delivery", D("n", i));
                    if (i == 1) h.Bridge.Event("Check-Refused", D("n", -1));
                }
                yield return Until(() => EventCount("check_delivery") >= 4, 25f);
                yield return Wait(1.5f);
                var got = new List<string>();
                foreach (var e in AllEvents()) if (Json.Str(e, "type") == "check_delivery") got.Add(Convert.ToString(Json.Num(e, "n")));
                var faults = Json.Parse(Http("POST", "/api/test/faults", D())) as Dictionary<string, object>;
                Check("bridge: events through failures - 500 tried again, a lost answer sent again and kept once, a refusal dropped; each event once, in order",
                      string.Join(",", got.ToArray()) == "0,1,2,3" && Json.Num(faults, "duplicates") >= 2 && Json.Num(faults, "fail_500") == 0
                      && Json.Num(faults, "drop_after_save") == 0 && EventCount("Check-Refused") == 0,
                      "got " + string.Join(",", got.ToArray()) + " faults " + Json.Write(faults) + " " + h.Bridge.LastError);
            }
            // a state held on the wire while an event and a newer state come in: the event still goes out (GPT's review of PR #14,
            // P1 - the finished state took the event after it out of the queue, unsent)
            {
                Http("POST", "/api/test/faults", D("slow_state", 1));
                h.Bridge.Post("/api/state", h.State());
                // (the bridge has begun to hold it - its fault used up - then the event: the order aimed at, every time; GPT's
                // review of PR #17)
                float raceT0 = Time.realtimeSinceStartup;
                bool raceHeld = false;                // (the state held on the wire before the event: else the order aimed at was not met)
                while (Time.realtimeSinceStartup - raceT0 < 3f)
                {
                    if (Json.Num(Json.Parse(Http("POST", "/api/test/faults", D())) as Dictionary<string, object>, "slow_state") == 0) { raceHeld = true; break; }
                    yield return Wait(0.05f);
                }
                h.Bridge.Event("check_race", D("n", 1));
                h.Bridge.Post("/api/state", h.State());
                yield return Until(() => EventCount("check_race") >= 1, 10f);
                var fr = Json.Parse(Http("POST", "/api/test/faults", D())) as Dictionary<string, object>;
                Check("bridge: an event behind a state still on the wire goes out when a newer state comes (the item sent is the one finished)",
                      raceHeld && EventCount("check_race") == 1 && Json.Num(fr, "slow_state") == 0, "held " + raceHeld + " " + Json.Write(fr) + " " + h.Bridge.LastError);
            }
            // the bridge's tokens (the security review of 2026-11-05, SEC-01; the checks' bridge asks for them): without one
            // nothing is read, the app's token does not read Muse's state, /health says only that it is alive; the app's own
            // requests carry its token (connected, its events in)
            {
                string tokBody;
                int noTok = HttpStatus("GET", "/api/state", null, out tokBody), appTok = HttpStatus("GET", "/api/state", "app", out tokBody);
                int museTok = HttpStatus("GET", "/api/state", "muse", out tokBody);
                HttpStatus("GET", "/health", null, out tokBody);
                var shortHealth = Json.Parse(tokBody) as Dictionary<string, object>;
                int tokEvents = EventCount("check_token");
                h.Bridge.Event("check_token", D("n", 1));
                yield return Until(() => EventCount("check_token") > tokEvents, 5f);
                Check("bridge tokens: none -> 401, the app's for Muse's state -> 403, Muse's -> 200; /health without one says only alive; the app's requests carry its token",
                      noTok == 401 && appTok == 403 && museTok == 200 && shortHealth != null && shortHealth.Count == 2 && Json.Str(shortHealth, "auth", "") == "required"
                      && h.Bridge.Connected && EventCount("check_token") > tokEvents,
                      noTok + "/" + appTok + "/" + museTok + " health " + tokBody + " " + h.Bridge.LastError);
            }
            // a creation that unpacks past its bound is stopped while it unpacks (the security review of 2026-11-05, SEC-03 B)
            {
                bool stopped = false;
                try { Match.Unzlib(Match.Zlib(new byte[Match.MaxCreationBytes + 1024])); }
                catch (IOException e) { stopped = e.Message.StartsWith("unpacks past"); }
                var small = Match.Unzlib(Match.Zlib(new byte[4096]));
                Check("match: a creation unpacking past its bound is stopped while unpacking; a normal one unpacks", stopped && small.Length == 4096);
            }
            // the UI hidden by its button (a real click) and shown again by its key (a real press): the menu, the key hints and the
            // presence go, the words' box and the button stay (the user's request, PR #11)
            {
                var uih = h.UiForCheck;
                yield return RealInput.Click(Btn(uih, "UI"));
                yield return Wait(0.4f);
                bool gone = uih.UiHidden && uih.KeyHintsNow == "" && uih.ClockShown == "" && !uih.Buttons.ContainsKey("場所") && uih.Buttons.ContainsKey("UI");
                yield return Shot("00_ui_hidden.png");
                yield return RealInput.Key(0x72);                    // (F3)
                yield return Wait(0.4f);
                bool back = !uih.UiHidden && uih.KeyHintsNow.Contains("UI を隠す") && uih.ClockShown.Length == 5 && uih.Buttons.ContainsKey("場所");
                if (uih.UiHidden) uih.UiHidden = false;
                Check("UI: its button hides the menu, the key hints and the presence (the words and the button stay); its key (F3) shows them again",
                      gone && back, "gone " + gone + " back " + back + " " + RealInput.LastError);
                string kh = uih.KeyHintsNow;
                int k1 = kh.IndexOf("F1：設定"), k2 = kh.IndexOf("Studio の画面部品"), k3 = kh.IndexOf("UI を隠す");
                Check("UI: the key hints F1 (settings), F2 (Studio's parts), F3 (the UI) in that order (the user's note of 2026-11-05)",
                      k1 >= 0 && k1 < k2 && k2 < k3, kh);
                // the two lists' windows apart (the user's note of 2026-11-05): the identities', the personas'
                uih.OpenListForCheck("identities");
                yield return Wait(0.3f);
                bool idOpen = uih.ListOpen == "identities" && uih.Modal;
                yield return Shot("00_identity_editor.png");
                uih.OpenListForCheck("personas");
                yield return Wait(0.3f);
                bool peOpen = uih.ListOpen == "personas" && uih.Modal;
                yield return Shot("00_persona_editor.png");
                uih.OpenListForCheck("");
                yield return null;
                Check("UI: the identities' list and the personas' list each in its own window, one at a time",
                      idOpen && peOpen && uih.ListOpen == "" && !uih.Modal, "identities " + idOpen + " personas " + peOpen);
            }
            Check("window icon: the launcher's (MuseStudio.ico beside the plugin) on the game window, big and small",
                  WindowTitle.IconBig != IntPtr.Zero && WindowTitle.IconSmall != IntPtr.Zero && WindowTitle.Icon(true) == WindowTitle.IconBig
                  && WindowTitle.Icon(false) == WindowTitle.IconSmall, "set " + WindowTitle.IconBig + "/" + WindowTitle.IconSmall + " now " + WindowTitle.Icon(true) + "/" + WindowTitle.Icon(false));
            bool menuShown = false;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>()) if (c.name == "Canvas Main Menu" && c.gameObject.activeInHierarchy) menuShown = true;
            Check("Studio's UI and its guide objects hidden", st.StudioUiHidden && !menuShown && !Singleton<Studio.Studio>.Instance.workInfo.visibleAxis);
            var idle = Json.Arr(Json.Obj(Json.Obj(Data.Load("scenes.json"), "scenes"), "idle"), "anime");
            var ai = st.Heroine.oiCharInfo.animeInfo;
            int f0 = Blend.Faded;
            st.ApplyScene("service_main", 1, 1f);
            yield return null;
            yield return null;
            Check("motion: a new scene key fades in (a crossfade, not a cut)", Blend.Faded == f0 + 1 && st.Cha.animBody.IsInTransition(0), "faded " + (Blend.Faded - f0));
            st.ApplyScene("idle", 1, 1f);
            yield return Wait(0.8f);
            float sp0 = st.Heroine.animeSpeed;
            st.ApplyScene("idle", 1, 1.8f);
            yield return null;
            float target = st.SpeedTarget;
            bool ramping = Mathf.Abs(st.Heroine.animeSpeed - target) > 0.05f;
            yield return Wait(0.8f);
            Check("motion: the same motion's speed changes over 0.6 s", target > sp0 + 0.3f && ramping && Mathf.Abs(st.Heroine.animeSpeed - target) < 0.01f,
                  sp0 + " -> " + st.Heroine.animeSpeed + " (target " + target + ")");
            st.ApplyScene("idle", 1, 1f);
            yield return Wait(0.8f);
            Check("idle scene: Studio's animation of the Godot app's idle", st.SceneKey == "idle" && ai.group == Convert.ToInt32(idle[0])
                  && ai.category == Convert.ToInt32(idle[1]) && ai.no == Convert.ToInt32(idle[2]), ai.group + "/" + ai.category + "/" + ai.no);
            yield return Shot("01_stage.png");
            yield return Fps("idle, one character");

            // a face: an emotion label sets the original's patterns
            var teasing = Json.Arr(Json.Obj(Json.Obj(Data.Load("faces.json"), "labels"), "teasing"), "cells");
            st.ApplyFace("teasing");
            yield return null;
            var fs = st.Cha.fileStatus;
            Check("face: an emotion label sets the original's eyebrow / eyes / mouth patterns", teasing != null && fs.eyebrowPtn == CellInt(teasing[0])
                  && fs.eyesPtn == CellInt(teasing[1]) && fs.mouthPtn == CellInt(teasing[2]), fs.eyebrowPtn + "/" + fs.eyesPtn + "/" + fs.mouthPtn);

            // the start-up (MUSE_GUIDE.md 7): the launch's phases, the chooser, the bridge's launch, the character_selected event
            var la = h.Launch;
            string phases = string.Join(",", la.Phases.ToArray());
            Check("launch: app_starting, stage_ready (the chooser shown, waiting), then character_selected", phases == "app_starting,stage_ready,character_selected"
                  && la.ChosenBy == "check" && h.UiForCheck.ChooserDrawn, phases + " by " + la.ChosenBy);
            // the chooser's footer (PR #12 adopted): start and the two lists' buttons together on the left, clear of the Muse panel's
            // window on the screen's right, none over another
            var cb = la.ChooserButtonsForCheck;
            bool cbOk = cb.Count == 3;
            var cbList = new List<Rect>(cb.Values);
            foreach (var r in cbList) cbOk &= r.xMax <= Screen.width - Ui.PanelRoom + 0.5f && r.x >= 0f && r.yMax <= Screen.height;
            for (int a = 0; a < cbList.Count; a++) for (int b = a + 1; b < cbList.Count; b++) cbOk &= !cbList[a].Overlaps(cbList[b]);
            Rect cbStart;
            cbOk &= cb.TryGetValue("このカードで始める", out cbStart) && cbStart.x < Screen.width * 0.25f;
            var cbText = new List<string>();
            foreach (var kv in cb) cbText.Add(kv.Key + " " + kv.Value.x.ToString("F0") + "-" + kv.Value.xMax.ToString("F0"));
            Check("the chooser: 「このカードで始める」 and the two lists' buttons together from the left, clear of the Muse panel on the right, none over another",
                  cbOk, Screen.width + " wide: " + string.Join(", ", cbText.ToArray()));
            var health = Json.Obj(Json.Parse(Http("GET", "/health", null)), "launch");
            var hch = Json.Obj(health, "character");
            Check("launch: the bridge's /health has this launch at character_selected with her id and persona", Json.Str(health, "id") == Launch.Id
                  && Json.Str(health, "phase") == "character_selected" && Json.Str(hch, "id") == h.CharacterId() && hch != null && hch.ContainsKey("persona"), Json.Write(health));
            var csel = LastEvent("character_selected");
            Check("events: character_selected with the character and the launch", csel != null && Json.Str(csel, "launch") == Launch.Id
                  && Json.Str(Json.Obj(csel, "character"), "id") == h.CharacterId() && Json.Str(csel, "by") == "check", csel == null ? "none" : Json.Write(csel));
            // Muse's packages tagged for an older launch or another character are dropped; this launch's greeting plays
            turn++;
            Http("POST", "/api/packages", D("session", "check", "turn", turn, "launch", "older", "blocks", new List<object> { Line("前の起動の挨拶。") }));
            turn++;
            Http("POST", "/api/packages", D("session", "check", "turn", turn, "character", "card:nobody.png", "blocks", new List<object> { Line("別のキャラへの挨拶。") }));
            turn++;
            Http("POST", "/api/packages", D("session", "check", "turn", turn, "launch", Launch.Id, "character", h.CharacterId(), "blocks", new List<object> { Line("この起動の挨拶。") }));
            yield return Until(() => bp.Busy && bp.Text == "この起動の挨拶。", 10f);
            var dropReasons = new List<string>();
            foreach (var e in AllEvents())
                if (Json.Str(e, "type") == "package_dropped") dropReasons.Add(Json.Str(e, "reason") + "/" + Json.Str(e, "by"));
            Check("pool: an older launch's and another character's packages are dropped (package_dropped), this launch's greeting plays",
                  bp.Text == "この起動の挨拶。" && dropReasons.Contains("old_launch/bridge") && dropReasons.Contains("other_character/bridge"),
                  bp.Text + " " + string.Join(",", dropReasons.ToArray()));
            // (the app's own guard: one already taken from the bridge, tagged for another character)
            bool appDrop = !la.Accept(D("session", "check", "turn", 0, "character", "card:nobody.png", "blocks", new List<object> { Line("x") }));
            yield return Wait(0.5f);
            Check("pool: the app drops a package for another character it already has (package_dropped by the app)", appDrop
                  && LastEvent("package_dropped") != null && Json.Str(LastEvent("package_dropped"), "by") == "app");
            // (GPT's post-review of #26: an untagged one the bridge stamped for the character or the launch before - still in the
            // receiving queue at a switch - is checked right before it plays as well; one stamped for now plays)
            bool stampedOld = !la.Accept(D("session", "check", "turn", 0, "_character_at", "card:nobody.png", "blocks", new List<object> { Line("x") }));
            bool stampedOldLaunch = !la.Accept(D("session", "check", "turn", 0, "_launch_at", "an-older-launch", "blocks", new List<object> { Line("x") }));
            bool stampedNow = la.Accept(D("session", "check", "turn", 0, "_character_at", h.CharacterId(), "_launch_at", Launch.Id, "blocks", new List<object> { Line("x") }));
            bool unstamped = la.Accept(D("session", "check", "turn", 0, "blocks", new List<object> { Line("x") }));
            yield return Wait(0.3f);
            Check("pool: an untagged package stamped by the bridge for another character / an older launch is dropped right before it plays; one for now plays",
                  stampedOld && stampedOldLaunch && stampedNow && unstamped && Json.Str(LastEvent("package_dropped"), "reason") == "old_launch",
                  stampedOld + " " + stampedOldLaunch + " " + stampedNow + " " + unstamped);
            bp.Advance(true);
            yield return Wait(0.5f);

            // a package through the bridge: its first block with an emotion, a scene key at intensity 3 and a camera, then a sound block
            Send(With(With(With(Line("確認のセリフです。いかがですか？"), "emotion", D("label", "gentle")), "scene", D("key", "service_main", "intensity", 3)), "camera", "close"),
                 D("type", "sound", "text", "（コポコポ……）"));
            yield return Until(() => bp.Busy && bp.Text == "確認のセリフです。いかがですか？", 10f);
            Check("bridge: a package arrives and its first block shows", bp.Busy && bp.Text == "確認のセリフです。いかがですか？", "busy " + bp.Busy + " text '" + bp.Text + "' connected " + h.Bridge.Connected + " " + h.Bridge.LastError);
            Check("block: its emotion, scene key (intensity 3) and camera", st.Emotion == "gentle" && st.SceneKey == "service_main" && st.Intensity == 3 && st.CameraKey == "close",
                  st.Emotion + " " + st.SceneKey + " " + st.Intensity + " " + st.CameraKey);
            yield return Wait(1.3f);
            yield return Shot("02_dialogue.png");
            bp.Advance(true);
            yield return null;
            Check("block: the sound block in its own style", bp.Busy && bp.IsSound && bp.Text == "（コポコポ……）", bp.Text);
            bp.Advance(true);
            yield return null;
            Check("package end: the idle scene; the package's face is Muse's, held (PR #46 §2: not the end face)",
                  !bp.Busy && st.SceneKey == "idle" && st.Emotion == "gentle" && st.MuseLabel == "gentle", st.SceneKey + " " + st.Emotion + " held " + st.MuseLabel);
            yield return Wait(2.5f);
            var state = Json.Parse(Http("GET", "/api/state", null));
            var stFace = Json.Obj(state, "face");
            Check("bridge: the state's face - the face Muse holds, its time left, the face shown (PR #46 §2)",
                  Json.Str(stFace, "muse") == "gentle" && Json.Num(stFace, "muse_left", -1) > 0 && Json.Str(stFace, "shown") == "gentle", Json.Write(stFace));
            st.ReleaseMuse("check");                 // (the checks after this one start from the talk's own face, as before)
            var stEndFace = st.Face;
            Check("package end: with no face of Muse's held, the end face as before", stEndFace == Json.Str(st.Cfg, "package_end_face", "service_smile"), stEndFace);
            Check("bridge: the state carries the location, the scene and the app", Json.Str(Json.Obj(state, "location"), "key") == "myroom"
                  && Json.Str(Json.Obj(state, "scene"), "key") == "idle" && Json.Obj(state, "app") != null
                  && Json.Obj(state, "app")["connected"] is bool && (bool)Json.Obj(state, "app")["connected"], Json.Write(state).Substring(0, Math.Min(300, Json.Write(state).Length)));

            // the dialogue box: clear of the Muse panel, the text revealed (a press shows the rest, the next one advances), a
            // long text grows the box upwards and then makes the font smaller
            var ui = h.UiForCheck;
            string longText = "";
            for (int i = 0; i < 24; i++) longText += "とても長いセリフが続きます。枠は上に伸びて、それでも収まらなければ字が小さくなります。";
            Send(Line(longText));
            yield return Until(() => bp.Busy && bp.Text == longText, 10f);
            yield return null;
            bool revealing = bp.Revealing && bp.Visible < longText.Length;
            yield return Wait(0.3f);
            bool clear = ui.PanelRight ? ui.BoxNow.xMax <= Screen.width - 440f + 0.5f : ui.BoxNow.x >= 440f - 0.5f;
            Check("dialogue: clear of the Muse panel, a long text grows the box and makes the font smaller",
                  clear && ui.BoxNow.height > Screen.height * 0.3f && ui.FontNow < Mathf.RoundToInt(26 * Mathf.Max(0.6f, Screen.height / 900f)),
                  string.Format("x {0:F0}..{1:F0} h {2:F0} font {3}", ui.BoxNow.x, ui.BoxNow.xMax, ui.BoxNow.height, ui.FontNow));
            Rect colAt = Btn(ui, "場所");
            Check("layout: Muse's panel at the bottom right (the launcher's panel.side) - the dialogue box clear of it, the panel's column on the left; the UI's look (Theme: the system's UI font)",
                  ui.PanelRight && clear && colAt.x >= 0f && colAt.xMax < Screen.width * 0.5f && Theme.FontName.Length > 0 && Theme.Current != null && Theme.Current.button.onNormal.background != null,
                  "column " + colAt + " font '" + Theme.FontName + "'");
            yield return Shot("16_long_text.png");
            bp.Advance();
            yield return null;
            bool shownAll = bp.Busy && bp.Text == longText && !bp.Revealing;
            bp.Advance();
            yield return null;
            Check("dialogue: the text is revealed, a press shows the rest, the next press advances", revealing && shownAll && !bp.Busy, revealing + " " + shownAll + " " + bp.Busy);
            yield return Wait(0.5f);
            // ---- the camera's controls (the user's request of 2026-11-04): Studio's own by default, then the settings' - real
            // drags, the wheel, keys ----
            yield return Until(() => !bp.Busy, 10f);
            var camc = Singleton<Studio.Studio>.Instance.cameraCtrl;
            st.ApplyCamera("medium", true);
            yield return Wait(0.8f);
            var free = new Vector2(Screen.width * 0.86f, Screen.height * 0.42f);
            var c0 = camc.Export();
            yield return RealInput.Drag(0, free, new Vector2(140f, 0f));
            var c1 = camc.Export();
            float turn1 = Mathf.DeltaAngle(c0.rotate.y, c1.rotate.y);
            bool still1 = (c1.pos - c0.pos).magnitude < 0.005f;
            string drag1 = CamInput.LastDrag, cerr = RealInput.LastError;
            c0 = camc.Export();
            yield return RealInput.Drag(1, free, new Vector2(140f, 0f));
            c1 = camc.Export();
            float near2 = c1.distance.z - c0.distance.z;
            string drag2 = CamInput.LastDrag;
            Check("camera (real drags): Studio's own controls by default - the left drag turns, the right drag goes nearer / farther",
                  cerr.Length == 0 && drag1 == "rotate" && Mathf.Abs(turn1) > 1f && still1 && drag2 == "dolly" && Mathf.Abs(near2) > 0.005f,
                  string.Format("{0} turn {1:F2} still {2} / {3} near {4:F3} {5}", drag1, turn1, still1, drag2, near2, cerr + RealInput.LastError));
            h.CamLeftDrag.Value = "pan";
            c0 = camc.Export();
            yield return RealInput.Drag(0, free, new Vector2(140f, 0f));
            c1 = camc.Export();
            bool panned = (c1.pos - c0.pos).magnitude > 0.005f && Mathf.Abs(Mathf.DeltaAngle(c0.rotate.y, c1.rotate.y)) < 0.01f;
            string drag3 = CamInput.LastDrag;
            h.CamRightDrag.Value = "rotate";
            h.CamTurnSpeed.Value = 2f;
            h.CamInvertX.Value = true;
            c0 = camc.Export();
            yield return RealInput.Drag(1, free, new Vector2(140f, 0f));
            c1 = camc.Export();
            float turn4 = Mathf.DeltaAngle(c0.rotate.y, c1.rotate.y);
            Check("camera (real drags): the settings' choices - the left drag set to move across; the right drag set to turn, twice as fast, reversed",
                  panned && drag3 == "pan" && turn4 * turn1 < 0f && Mathf.Abs(turn4) > Mathf.Abs(turn1) * 1.4f && Mathf.Abs(turn4) < Mathf.Abs(turn1) * 2.8f,
                  string.Format("{0} panned {1}; turn {2:F2} against {3:F2}", drag3, panned, turn4, turn1));
            h.CamInvertX.Value = false;
            // (the wheel: away from her first, so that nearer has room; the real wheel, else - when it does not reach Unity here -
            // one turn given to the same code, noted)
            int seen0 = CamInput.WheelSeen;
            yield return RealInput.Wheel(free, -2);
            bool realWheel = CamInput.WheelSeen > seen0;
            if (!realWheel) { CamInput.WheelForCheck = -0.2f; yield return null; yield return null; }
            c0 = camc.Export();
            if (realWheel) yield return RealInput.Wheel(free, 1); else { CamInput.WheelForCheck = 0.1f; yield return null; yield return null; }
            c1 = camc.Export();
            float wh1 = c1.distance.z - c0.distance.z;
            h.CamInvertWheel.Value = true;
            c0 = camc.Export();
            if (realWheel) yield return RealInput.Wheel(free, 1); else { CamInput.WheelForCheck = 0.1f; yield return null; yield return null; }
            c1 = camc.Export();
            float wh2 = c1.distance.z - c0.distance.z;
            Check("camera (the wheel): it zooms; reversed by the setting" + (realWheel ? " (the real wheel)" : " (the real wheel did not reach Unity here: a turn given to the same code)"),
                  Mathf.Abs(wh1) > 0.001f && Mathf.Abs(wh2) > 0.001f && Mathf.Sign(wh1) != Mathf.Sign(wh2),
                  string.Format("{0:F3} then {1:F3} real {2}", wh1, wh2, realWheel));
            if (!realWheel) Note("camera: the real wheel (mouse_event) did not reach Unity's Mouse ScrollWheel here");
            // the free camera remembered per room (the user's request, PR #11): the user's wheel moves it -> the view it left is
            // saved once it settles, even with a preset Muse asks for at once after it (GPT's review of PR #21: the view as the move
            // left it); the room's view comes back, over a preset's move under way (that move stops); the end writes a move just made
            {
                var cm = h.CameraMemory;
                cm.Saving = true;
                var memCam = Singleton<Studio.Studio>.Instance.cameraCtrl;
                if (realWheel) yield return RealInput.Wheel(free, 2); else { CamInput.WheelForCheck = 0.3f; yield return null; yield return null; }
                yield return null;
                yield return null;
                var mine = memCam.Export();
                st.ApplyCamera("far", true);          // (before the move settles)
                yield return Wait(1.5f);
                bool saved = cm.Has(st.Location) && File.Exists(Path.Combine(Path.GetDirectoryName(path), "cameras.json"));
                st.ApplyCamera("medium", false);      // (a preset's move under way...)
                yield return null;
                bool recalled = cm.Recall(st.Location);
                yield return Wait(1.5f);
                bool presetNotSaved = recalled && Vector3.Distance(memCam.Export().pos, mine.pos) < 0.001f
                                      && Mathf.Abs(memCam.Export().distance.z - mine.distance.z) < 0.001f;
                // the end (OnApplicationQuit / OnDestroy: Flush): a move just made, written before it settles
                if (realWheel) yield return RealInput.Wheel(free, -1); else { CamInput.WheelForCheck = -0.2f; yield return null; yield return null; }
                yield return null;
                yield return null;
                var mine2 = memCam.Export();
                cm.Flush();
                var memDoc = Json.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path), "cameras.json"), Encoding.UTF8)) as Dictionary<string, object>;
                var memDist = Json.Arr(Json.Obj(Json.Obj(memDoc, "rooms"), st.Location), "distance");
                bool flushed = memDist != null && memDist.Count == 3 && Mathf.Abs(Convert.ToSingle(memDist[2]) - mine2.distance.z) < 0.001f
                               && Mathf.Abs(mine2.distance.z - mine.distance.z) > 0.01f;
                Check("camera: the user's own move is saved for the room as it left the view (a preset at once after it is not what is kept); the view comes back over a preset's move under way; the end writes a move just made",
                      saved && presetNotSaved && flushed, "saved " + saved + " back " + presetNotSaved + " flushed " + flushed + " dist " + mine.distance.z.ToString("F3")
                      + " now " + memCam.Export().distance.z.ToString("F3") + " then " + mine2.distance.z.ToString("F3"));
                cm.Forget(st.Location);               // (the checks after this one expect the presets)
                // two rooms' moves within the settling time: both kept (GPT's re-review of #25 - the first room's was lost); the
                // first room's newest view when it is recalled before it is written
                string cmA = st.Location, cmB = cmA == "girls_room" ? "myroom" : "girls_room";
                if (realWheel) yield return RealInput.Wheel(free, 1); else { CamInput.WheelForCheck = 0.2f; yield return null; yield return null; }
                yield return null;
                yield return null;
                float cmDistA = memCam.Export().distance.z;
                h.MoveTo(cmB, "check");
                yield return Until(() => st.Location == cmB && !st.Moving, 20f);
                yield return null;
                if (realWheel) yield return RealInput.Wheel(free, -1); else { CamInput.WheelForCheck = -0.2f; yield return null; yield return null; }
                yield return null;
                yield return null;
                float cmDistB = memCam.Export().distance.z;
                yield return Wait(1.2f);
                var cmDocAB = Json.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path), "cameras.json"), Encoding.UTF8)) as Dictionary<string, object>;
                var cmSavedA = Json.Arr(Json.Obj(Json.Obj(cmDocAB, "rooms"), cmA), "distance");
                var cmSavedB = Json.Arr(Json.Obj(Json.Obj(cmDocAB, "rooms"), cmB), "distance");
                bool cmBoth = cmSavedA != null && cmSavedB != null && Mathf.Abs(Convert.ToSingle(cmSavedA[2]) - cmDistA) < 0.001f
                              && Mathf.Abs(Convert.ToSingle(cmSavedB[2]) - cmDistB) < 0.001f;
                h.MoveTo(cmA, "check");
                yield return Until(() => st.Location == cmA && !st.Moving, 20f);
                yield return Wait(0.5f);
                bool cmBack = Mathf.Abs(memCam.Export().distance.z - cmDistA) < 0.001f;
                Check("camera: two rooms moved within the settling time keep each its own view (the first written as the second is taken); going back brings the first",
                      cmBoth && cmBack, "both " + cmBoth + " back " + cmBack + " A " + cmDistA.ToString("F3") + " B " + cmDistB.ToString("F3")
                      + " saved " + (cmSavedA != null ? Convert.ToSingle(cmSavedA[2]).ToString("F3") : "none") + "/" + (cmSavedB != null ? Convert.ToSingle(cmSavedB[2]).ToString("F3") : "none"));
                cm.Forget(cmA);
                cm.Forget(cmB);
                st.ApplyCamera("medium", true);
                cm.Saving = false;
            }
            // the room's play positions edited (the user's request, PR #11): its markers; a spot moved and turned, saved at once (the
            // check's folder), read so by an outing too; "here" takes her along; back to the original's place
            {
                var ed = h.SpotEditor;
                ed.Toggle(true);
                ed.ShowKind("play");
                yield return Wait(0.5f);
                var sp = st.Spots.Find(o => !o.Here);
                var here = st.Spots.Find(o => o.Here);
                Vector3 spotP0 = sp.Pos, h0 = here.Pos, her0 = st.Heroine.oiCharInfo.changeAmount.pos;
                float y0 = sp.Rot.eulerAngles.y;
                ed.Selected = sp.Id;
                ed.Change(sp, new Vector3(0.1f, 0f, 0f), 15f);
                ed.Change(here, new Vector3(0f, 0f, 0.05f), 0f);
                yield return Wait(0.6f);
                // (from above the room, the markers on its floor in sight)
                st.SetCamera(here.Pos + new Vector3(0f, 2.6f, -2.2f), here.Pos + new Vector3(0f, 0f, 0.6f), 55f, 0f);
                yield return Wait(0.4f);
                yield return Shot("00_spot_editor.png");
                st.ApplyCamera("medium", true);
                var again = st.SpotsIn(st.Location).Find(o => o.Id == sp.Id || sp.Aliases.Contains(o.Id));
                bool moved = Vector3.Distance(sp.Pos, spotP0 + new Vector3(0.1f, 0f, 0f)) < 0.001f && Mathf.Abs(Mathf.DeltaAngle(sp.Rot.eulerAngles.y, y0 + 15f)) < 0.1f
                             && again != null && Mathf.Abs(again.Pos.x - (spotP0.x + 0.1f)) < 0.05f && File.Exists(Path.Combine(Path.GetDirectoryName(path), "spots.json"))
                             && Vector3.Distance(st.Heroine.oiCharInfo.changeAmount.pos, here.Pos) < 0.01f && Vector3.Distance(here.Pos, h0 + new Vector3(0f, 0f, 0.05f)) < 0.001f;
                int markers = 0;
                int hiddenParts = 0;                  // (a marker's parts on a layer the camera does not draw)
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>()) if (t.name.StartsWith("MuseStudio spot "))
                {
                    markers++;
                    foreach (var r in t.GetComponentsInChildren<Renderer>()) if ((Camera.main.cullingMask & (1 << r.gameObject.layer)) == 0) hiddenParts++;
                }
                ed.Reset(sp);
                ed.Reset(here);
                bool back = Vector3.Distance(sp.Pos, spotP0) < 0.001f && Vector3.Distance(here.Pos, h0) < 0.001f && !ed.HasOffset(st.Location, sp.Id);
                ed.Toggle(false);
                yield return null;
                Check("play positions: a marker per play spot (drawn by the camera); a spot moved and turned is saved at once (and read so by an outing), \"here\" takes her along, and goes back",
                      markers == st.Spots.Count - 1 && hiddenParts == 0 && moved && back, "markers " + markers + "/" + (st.Spots.Count - 1) + " unseen parts " + hiddenParts + " moved " + moved + " back " + back
                      + " spot " + (sp.Pos - spotP0).ToString("F3") + " outing " + (again != null ? (again.Pos.x - spotP0.x).ToString("F3") : "none") + " her " + (st.Heroine.oiCharInfo.changeAmount.pos - here.Pos).ToString("F3"));
                st.ApplyCamera("medium", true);
            }
            // the play positions by the real mouse (GPT's review of PR #22): a click on a marker selects it - no touch, the words
            // waiting are not sent on; 奥 moves it away from the camera; a drag past the horizon keeps the last good place, in reach
            {
                var pzEd = h.SpotEditor;
                pzEd.Toggle(true);
                pzEd.ShowKind("play");
                var pzHere = st.Spots.Find(o => o.Here);
                st.SetCamera(pzHere.Pos + new Vector3(0f, 2.6f, -2.2f), pzHere.Pos + new Vector3(0f, 0f, 0.6f), 55f, 0f);
                yield return Wait(0.4f);
                var pzCam = Camera.main;
                Spot pzPick = null;
                Vector2 pzAt = Vector2.zero;
                foreach (var o in pzEd.Shown)
                {
                    var sp3 = pzCam.WorldToScreenPoint(o.Pos);
                    if (sp3.z <= 0f || sp3.x < 60f || sp3.x > Screen.width - 60f || sp3.y < 60f || sp3.y > Screen.height - 60f || h.UiOver(new Vector2(sp3.x, sp3.y))) continue;
                    if (pzPick == null || o.Here) { pzPick = o; pzAt = new Vector2(sp3.x, Screen.height - sp3.y); }
                }
                int pzTouches = EventCount("touch");
                Send(Line("プレイ位置の点検です。"));          // (not in （）: a line all inner thought shows no box and goes on at once)
                yield return Until(() => bp.Busy && bp.Text.StartsWith("プレイ位置の点検") && !bp.Revealing, 10f);
                yield return Wait(0.3f);
                yield return RealInput.Click(pzAt);
                yield return Wait(0.5f);
                string pzClickErr = RealInput.LastError + " pick " + (pzPick != null ? pzPick.Id : "none") + " sel " + pzEd.Selected + " busy " + bp.Busy
                                    + " text " + bp.Text + " press " + h.Touch.Pressing + " hover " + h.Touch.Hover + " touches " + (EventCount("touch") - pzTouches);
                bool pzClick = pzPick != null && pzEd.SelectedSpot != null && Vector3.Distance(pzEd.SelectedSpot.Pos, pzPick.Pos) < 0.001f
                               && bp.Busy && bp.Text.StartsWith("プレイ位置の点検") && h.Touch.Pressing.Length == 0 && h.Touch.Hover.Length == 0
                               && EventCount("touch") == pzTouches && !pzEd.HasOffset(st.Location, pzEd.Selected);
                bp.Advance(true);
                yield return Until(() => !bp.Busy, 5f);
                // 奥 2cm by its button: away from the camera along the floor
                var pzSel = pzEd.SelectedSpot;
                Vector3 pzB0 = pzSel != null ? pzSel.Pos : Vector3.zero;
                Rect pzBack;
                pzEd.Buttons.TryGetValue("奥", out pzBack);
                yield return RealInput.Click(pzBack);
                yield return Wait(0.3f);
                Vector3 pzFwd = Vector3.ProjectOnPlane(pzCam.transform.forward, Vector3.up).normalized;
                Vector3 pzMoved = pzSel != null ? pzSel.Pos - pzB0 : Vector3.zero;
                bool pzAway = pzSel != null && Vector3.Dot(pzMoved, pzFwd) > 0.015f && Vector3.ProjectOnPlane(pzMoved - pzFwd * Vector3.Dot(pzMoved, pzFwd), Vector3.up).magnitude < 0.005f;
                if (pzSel != null) pzEd.Reset(pzSel);
                // a drag from the marker up past the horizon (a camera low and level): it stops where the floor was last in reach
                st.SetCamera(pzHere.Pos + new Vector3(0f, 0.4f, -2.5f), pzHere.Pos + new Vector3(0f, 0.4f, 0f), 55f, 0f);
                yield return Wait(0.4f);
                pzEd.ShowKind("stand");               // (where she stands: its own kind since PR #23)
                yield return null;
                var pzH3 = pzCam.WorldToScreenPoint(pzHere.Pos);
                Vector3 pzH0 = pzHere.Pos;
                yield return RealInput.Drag(0, new Vector2(pzH3.x, Screen.height - pzH3.y), new Vector2(0f, -480f));
                Vector3 pzNo;
                bool pzPast = !SpotEditor.FloorAt(pzCam.ScreenPointToRay(Input.mousePosition), pzH0.y, 12f, out pzNo);     // (released past the floor's reach)
                yield return Wait(0.4f);
                string pzDragErr = RealInput.LastError;
                float pzFar = Vector3.Distance(pzCam.transform.position, pzHere.Pos), pzWent = Vector3.Distance(pzHere.Pos, pzH0);
                bool pzKept = pzPast && pzEd.HasOffset(st.Location, pzHere.Id) && pzWent > 0.3f && pzFar <= 12.5f;      // (12 m from the ray's start on the near plane)
                pzEd.Reset(pzHere);
                // the floor's ray alone: along the horizon, behind, far - none; down at 45° - there
                Vector3 pzAt45;
                bool pzRay = !SpotEditor.FloorAt(new Ray(new Vector3(0f, 1.5f, 0f), new Vector3(0f, -0.001f, 1f).normalized), 0f, 12f, out pzAt45)
                             && !SpotEditor.FloorAt(new Ray(new Vector3(0f, 1.5f, 0f), new Vector3(0f, 0.2f, 1f).normalized), 0f, 12f, out pzAt45)
                             && !SpotEditor.FloorAt(new Ray(new Vector3(0f, 1.5f, 0f), Vector3.forward), 0f, 12f, out pzAt45)
                             && SpotEditor.FloorAt(new Ray(new Vector3(0f, 1.5f, 0f), new Vector3(0f, -1f, 1f).normalized), 0f, 12f, out pzAt45)
                             && Vector3.Distance(pzAt45, new Vector3(0f, 0f, 1.5f)) < 0.001f;
                pzEd.Toggle(false);
                st.ApplyCamera("medium", true);
                yield return Wait(0.4f);
                Check("play positions by the real mouse: a click on a marker selects it (no touch, the words not sent on); 奥 moves it away from the camera; a drag past the horizon stops in reach; the floor's ray",
                      pzClick && pzAway && pzKept && pzRay, "click " + pzClick + " " + pzClickErr + " away " + pzAway + " " + pzMoved.ToString("F3") + " kept " + pzKept + " past " + pzPast
                      + " went " + pzWent.ToString("F2") + " far " + pzFar.ToString("F2") + " " + pzDragErr + " ray " + pzRay);
            }
            // the kinds of position references (the user's request, PR #23): one kind's markers at a time, in its colour - the
            // tab by a real click; an outing's place (the toilet's, in its own room) moved and turned is where the outing puts
            // her, and goes back; the settings' resets: every room's adjustments and camera views back to the original's
            {
                var gzEd = h.SpotEditor;
                gzEd.Toggle(true);
                var gzSeen = new List<string>();
                bool gzOnly = true;
                foreach (var k in SpotEditor.Kinds)
                {
                    gzEd.ShowKind(k);
                    yield return null;
                    int mine = 0, others = 0;
                    var col = SpotEditor.KindColors[k];
                    bool coloured = true;
                    foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                    {
                        if (!t.name.StartsWith("MuseStudio spot ")) continue;
                        if (t.name.StartsWith("MuseStudio spot " + k + " ")) mine++; else others++;
                        foreach (var r in t.GetComponentsInChildren<Renderer>())
                            if (r.sharedMaterial == null || Mathf.Abs(r.sharedMaterial.color.r - col.r) > 0.01f || Mathf.Abs(r.sharedMaterial.color.g - col.g) > 0.01f) coloured = false;
                    }
                    gzSeen.Add(k + " " + mine + "/" + gzEd.Refs(k).Count + (others > 0 ? " others " + others : "") + (coloured ? "" : " colour"));
                    if (mine != gzEd.Refs(k).Count || others > 0 || !coloured) gzOnly = false;
                }
                gzEd.ShowKind("play");
                yield return null;
                yield return null;                    // (the panel drawn: its tabs' places)
                Rect gzTab;
                gzEd.Buttons.TryGetValue("kind stand", out gzTab);
                yield return RealInput.Click(gzTab);
                yield return Wait(0.3f);
                bool gzTabbed = gzEd.Kind == "stand" && gzEd.Shown.Count == 1 && gzEd.Shown[0].Here;
                // the toilet's place, its room not this one
                var gzT0 = h.Outing.Place("toilet");
                string gzRoom = Json.Str(Json.Obj(Json.Obj(h.Urges.Doc, "outings"), "toilet"), "room", "");
                gzEd.Change(h.Outing.Place("toilet"), new Vector3(0.08f, 0f, -0.04f), 20f, gzRoom);
                var gzT1 = h.Outing.Place("toilet");
                bool gzOuting = Vector3.Distance(gzT1.Pos, gzT0.Pos + new Vector3(0.08f, 0f, -0.04f)) < 0.001f
                                && Mathf.Abs(Mathf.DeltaAngle(gzT1.Rot.eulerAngles.y, gzT0.Rot.eulerAngles.y + 20f)) < 0.1f;
                gzEd.Reset(gzT1, gzRoom);
                bool gzOutingBack = Vector3.Distance(h.Outing.Place("toilet").Pos, gzT0.Pos) < 0.001f && !gzEd.HasOffset(gzRoom, "outing:toilet");
                // the resets (the settings' buttons do these): a play spot and the approach set, a camera view saved - all gone
                var gzSp = st.Spots.Find(o => !o.Here);
                Vector3 gzSp0 = gzSp.Pos;
                gzEd.ShowKind("play");
                gzEd.Change(gzSp, new Vector3(0.05f, 0f, 0f), 0f);
                var gzAp = gzEd.ApproachSpot(st.Location);
                gzEd.Change(gzAp, new Vector3(0.1f, 0f, 0f), 0f);
                Vector3 gzApPos;
                float gzApYaw;
                bool gzSet = gzEd.ApproachIn(st.Location, out gzApPos, out gzApYaw) && gzEd.HasOffset(st.Location, gzSp.Id);
                gzEd.Toggle(false);                   // (its panel and markers out of the wheel's way)
                var gzCm = h.CameraMemory;
                gzCm.Saving = true;
                if (realWheel) yield return RealInput.Wheel(free, 1); else { CamInput.WheelForCheck = 0.2f; yield return null; yield return null; }
                yield return null;
                yield return null;
                gzCm.Flush();
                bool gzCamSaved = gzCm.Has(st.Location);
                gzEd.ResetAll();
                gzCm.ResetAll();
                gzCm.Saving = false;
                yield return null;
                var gzSpNow = st.Spots.Find(o => o.Id == gzSp.Id);
                var gzDoc = Json.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path), "spots.json"), Encoding.UTF8)) as Dictionary<string, object>;
                var gzCamDoc = Json.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path), "cameras.json"), Encoding.UTF8)) as Dictionary<string, object>;
                bool gzReset = gzSet && gzCamSaved && !gzEd.ApproachIn(st.Location, out gzApPos, out gzApYaw) && !gzEd.HasOffset(st.Location, gzSp.Id)
                               && gzSpNow != null && Vector3.Distance(gzSpNow.Pos, gzSp0) < 0.001f && Json.Obj(gzDoc, "rooms") != null && Json.Obj(gzDoc, "rooms").Count == 0
                               && !gzCm.Has(st.Location) && Json.Obj(gzCamDoc, "rooms") != null && Json.Obj(gzCamDoc, "rooms").Count == 0;
                st.ApplyCamera("medium", true);
                yield return Wait(0.3f);
                Check("position references: one kind's markers at a time in its colour (its tab by a real click); the toilet's place moved and turned is the outing's, and back; the settings' resets clear every room's adjustments and camera views",
                      gzOnly && gzTabbed && gzOuting && gzOutingBack && gzReset, string.Join(", ", gzSeen.ToArray()) + " tab " + gzTabbed + " " + RealInput.LastError
                      + " outing " + gzOuting + "/" + gzOutingBack + " reset " + gzReset + " (set " + gzSet + " cam " + gzCamSaved + ")");
            }
            h.CamKeys["turn_left"].Value = new KeyboardShortcut(KeyCode.J);
            c0 = camc.Export();
            yield return RealInput.Hold(0x4A, 0.4f);
            c1 = camc.Export();
            float kj = Mathf.DeltaAngle(c0.rotate.y, c1.rotate.y);
            c0 = camc.Export();
            yield return RealInput.Hold(0x64, 0.4f);
            c1 = camc.Export();
            float k4 = Mathf.DeltaAngle(c0.rotate.y, c1.rotate.y);
            Check("camera (real keys): a key rebound - 回転：左 set to J turns, the number pad's 4 turns no more", Mathf.Abs(kj) > 1f && Mathf.Abs(k4) < 0.01f,
                  string.Format("J {0:F2} pad 4 {1:F2} {2}", kj, k4, RealInput.LastError));
            h.CamLeftDrag.Value = "rotate";
            h.CamRightDrag.Value = "dolly";
            h.CamTurnSpeed.Value = 1f;
            h.CamInvertWheel.Value = false;
            h.CamKeys["turn_left"].Value = new KeyboardShortcut(KeyCode.Keypad4);
            st.ApplyCamera("medium", true);
            yield return Wait(0.5f);
            // ---- the way into a match (the trial run of 2026-11-04): the panel's 通信対戦 and 招く, real clicks - match_requested to Muse ----
            var uiM = h.UiForCheck;
            int mrq = EventCount("match_requested");
            yield return RealInput.Click(Btn(uiM, "通信対戦"));
            yield return Wait(0.4f);
            yield return RealInput.Click(Btn(uiM, "対戦を招く"));
            yield return Wait(0.6f);
            var mreq = LastEvent("match_requested");
            Check("match (real clicks): the panel's 通信対戦 opens the way in; 招く asks Muse (match_requested: host, the host's format and arousal to start from)",
                  EventCount("match_requested") > mrq && Json.Str(mreq, "role") == "host" && Json.Str(mreq, "format") == h.MatchFormat.Value && Json.Has(mreq, "arousal_start"),
                  RealInput.LastError + " " + Json.Write(mreq));
            yield return RealInput.Click(Btn(uiM, "通信対戦"));       // (閉じる)
            yield return Wait(0.3f);
            // ---- he moves alone (the panel's 場所; the trial run of 2026-11-04): she stays; Muse is told he left her, not where; she
            // comes to him on Muse's location; his move back to her room is together again ----
            string homeRoom = st.Location, awayRoom = homeRoom == "girls_room" ? "bedroom" : "girls_room";
            int lcA = EventCount("location_changed"), tcA = EventCount("together_changed");
            h.MoveTo(awayRoom, "user");
            yield return Until(() => st.Location == awayRoom && !st.Moving, 20f);
            yield return Wait(1.0f);
            var stA2 = Json.Parse(Http("GET", "/api/state", null));
            var tcLeft = LastEvent("together_changed");
            Vector3 herAt = st.Heroine.oiCharInfo.changeAmount.pos;
            bool herStayed = h.Presence.HerRoom == homeRoom && Mathf.Abs(herAt.x - h.Rooms.OffsetOf(homeRoom).x) < 50f;
            Check("apart: he moves alone - she stays in her room; together_changed (false, user_left) and no location_changed; the state's location stays where she saw him last, her_location hers",
                  herStayed && Json.Str(tcLeft, "cause") == "user_left" && !(bool)tcLeft["together"] && EventCount("location_changed") == lcA
                  && Json.Str(Json.Obj(stA2, "location"), "key") == homeRoom && Json.Str(Json.Obj(stA2, "her_location"), "key") == homeRoom
                  && !(bool)Json.Obj(stA2, "presence")["together"], "her " + h.Presence.HerRoom + " at " + herAt + " " + Json.Write(tcLeft));
            bool noPlay = pc.Begin("aibu_00", "check", "here").StartsWith("she is not here");
            Send(With(Line("待って、そっち行く。"), "location", awayRoom));
            yield return Until(() => h.Presence.Together, 10f);
            yield return Wait(0.8f);
            var tcCame = LastEvent("together_changed");
            Check("apart: no play while she is not there; Muse's location takes her to him (together_changed true, she_came) - the stage stays his",
                  noPlay && h.Presence.Together && st.Location == awayRoom && Json.Str(tcCame, "cause") == "she_came" && (bool)tcCame["together"],
                  "no play " + noPlay + " " + Json.Write(tcCame));
            bp.Advance(true);
            h.MoveTo(homeRoom, "check");               // (the two back together)
            yield return Until(() => st.Location == homeRoom && !st.Moving, 20f);
            yield return Wait(0.8f);
            var tcLeft2 = LastEvent("together_changed");
            h.MoveTo(awayRoom, "user");
            yield return Until(() => st.Location == awayRoom && !st.Moving, 20f);
            yield return Wait(0.5f);
            h.MoveTo(homeRoom, "user");
            yield return Until(() => st.Location == homeRoom && !st.Moving, 20f);
            yield return Wait(0.8f);
            var tcBack = LastEvent("together_changed");
            bed = null;                                // (the spots made again by the moves: the bed's anew)
            foreach (var sp in st.Spots) if (sp.Name == "ベッド" && bed == null) bed = sp;
            Check("apart: back in her room he is with her again (together_changed true, user_came)", h.Presence.Together && Json.Str(tcBack, "cause") == "user_came"
                  && (bool)tcBack["together"] && EventCount("together_changed") >= tcA + 4, Json.Write(tcLeft2) + " / " + Json.Write(tcBack));

            // her inner thoughts in full-width （） stay out of the box (the chat keeps them); a block all of it goes by
            Send(Line("見て。（ほんとは恥ずかしい）似合う？"), Line("（言えないけど、うれしい）"), Line("……どう？"));
            yield return Until(() => bp.Busy && bp.Text.StartsWith("見て"), 10f);
            string shownInner = bp.Text;
            bp.Advance(true);
            yield return Wait(0.3f);
            Check("dialogue: her inner thoughts in （） are not shown, a block of only them goes by", shownInner == "見て。似合う？" && bp.Text == "……どう？"
                  && BlockPlayer.Shown("（a）（b）") == "" && BlockPlayer.Shown("前（中）後") == "前後", shownInner + " / " + bp.Text);
            bp.Advance(true);
            yield return Wait(0.5f);

            // an unknown label: shown anyway with the standard face, no event (MUSE_GUIDE.md 4)
            Send(With(Line("知らない表情。"), "emotion", D("label", "no_such_label")));
            yield return Until(() => bp.Busy && bp.Text == "知らない表情。", 10f);
            yield return Wait(1f);
            Check("block: an unknown label shows the standard face (neutral), no event", st.Face == "neutral" && !EventTypes().Contains("block_refused"), st.Face);
            var pkgSt = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "package");
            Check("state: the package while it plays (playing, block, blocks)", pkgSt != null && pkgSt["playing"] is bool && (bool)pkgSt["playing"] && (int)Json.Num(pkgSt, "block") == 1,
                  Json.Write(pkgSt));
            bp.Advance(true);
            yield return Wait(1f);
            var locSt = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "location");
            Check("state: the location with its summary and tags", Json.Str(locSt, "summary", "").Length > 0 && Json.Arr(locSt, "tags") != null && Json.Arr(locSt, "tags").Count > 0, Json.Write(locSt));

            // her climax in the talk (Muse's request of 2026-11-05): anim climax plays the standing caress's climax motion with the
            // climax face, the neck by the motion; a scene sent meanwhile waits for its end, then plays; nothing counted, the
            // gauge left alone, nothing told
            {
                var acRow = pc.Row("aibu_00");
                var acFem = Json.Arr(acRow, "female");
                var acSt = Json.Obj(acRow, "female_states");
                int acG = Convert.ToInt32(acFem[0]), acC = Convert.ToInt32(acFem[1]);
                int climax0 = EventCount("climax"), refused0 = EventCount("block_refused"), gaugeClimaxes0 = h.Gauge.Climaxes;
                int satisfied0 = h.Mood.Log.FindAll(e => Json.Str(e, "event") == "satisfied").Count, noClimax0 = Refusals("mood.arousal", "no_climax");
                float gauge0 = h.Gauge.Value;
                Send(With(With(Line("……っ、だめ、もう……っ！"), "anim", "climax"), "mood", D("arousal", "satisfied")), With(With(Line("……はぁ、はぁ。"), "scene", D("key", "care")), "emotion", D("label", "gentle")));
                yield return Until(() => bp.Busy && bp.Text.StartsWith("……っ"), 10f);
                // (the motion's switch: its first load can take a moment)
                yield return Until(() => { var a = st.Heroine.oiCharInfo.animeInfo; return a.group == acG && a.category == acC && a.no == Convert.ToInt32(acSt["Orgasm_Start"]); }, 3f);
                yield return Wait(0.2f);
                var acAi = st.Heroine.oiCharInfo.animeInfo;
                bool acStart = st.Climaxing && acAi.group == acG && acAi.category == acC && acAi.no == Convert.ToInt32(acSt["Orgasm_Start"]);
                string acFace = st.Face, acWhy = h.Look.Why;
                int acNeck = st.Cha.fileStatus.neckLookPtn;
                yield return Wait(1.2f);
                yield return Shot("10a_talk_climax.png");
                bp.Advance(true);
                yield return Until(() => bp.Busy && bp.Text.StartsWith("……はぁ"), 10f);
                yield return Wait(0.5f);
                var acAi2 = st.Heroine.oiCharInfo.animeInfo;
                bool acWaits = st.Climaxing && acAi2.group == acG && acAi2.category == acC && st.SceneKey == "care" && st.Face == "gentle";
                bool acLoop = false;
                float acT0 = Time.realtimeSinceStartup;
                while (st.Climaxing && Time.realtimeSinceStartup - acT0 < 20f)
                {
                    var a = st.Heroine.oiCharInfo.animeInfo;
                    if (a.group == acG && a.category == acC && (a.no == Convert.ToInt32(acSt["Orgasm_Loop"]) || a.no == Convert.ToInt32(acSt["Orgasm_A"]))) acLoop = true;
                    yield return null;
                }
                yield return Wait(0.4f);
                var acAi3 = st.Heroine.oiCharInfo.animeInfo;
                var careAnime = Json.Arr(Json.Obj(Json.Obj(Data.Load("scenes.json"), "scenes"), "care"), "anime");
                bool acBack = !st.Climaxing && acAi3.group == Convert.ToInt32(careAnime[0]) && acAi3.category == Convert.ToInt32(careAnime[1]) && acAi3.no == Convert.ToInt32(careAnime[2]);
                Check("talk climax: anim climax plays the standing caress's climax motion (aibu_00 Orgasm_Start, then its loop and after) with the climax face, the neck by the motion",
                      acStart && acLoop && acFace == "preset:ecstasy" && acWhy == "climax" && acNeck == 3,
                      acAi.group + "/" + acAi.category + "/" + acAi.no + " face " + acFace + " look " + acWhy + " neck " + acNeck + " loop " + acLoop);
                Check("talk climax: a scene sent meanwhile waits for its end (her face of the block at once), then plays",
                      acWaits && acBack, acAi2.group + "/" + acAi2.category + "/" + acAi2.no + " key " + st.SceneKey + " face " + st.Face + " -> " + acAi3.group + "/" + acAi3.category + "/" + acAi3.no);
                int satisfied1 = h.Mood.Log.FindAll(e => Json.Str(e, "event") == "satisfied").Count;
                BustAt("after the talk climax");
                Check("talk climax: a climax for Muse's mood all the same - satisfied in its own block taken (no no_climax)",
                      satisfied1 == satisfied0 + 1 && Refusals("mood.arousal", "no_climax") == noClimax0, "satisfied " + satisfied0 + " -> " + satisfied1);
                Check("talk climax: nothing counted or told (no climax event, no refusal), the gauge not raised",
                      EventCount("climax") == climax0 && EventCount("block_refused") == refused0 && h.Gauge.Climaxes == gaugeClimaxes0 && h.Gauge.Value <= gauge0 + 0.0001f,
                      "climax " + climax0 + " -> " + EventCount("climax") + " refused " + refused0 + " -> " + EventCount("block_refused") + " gauge " + gauge0 + " -> " + h.Gauge.Value);
                bp.Advance(true);
                yield return Wait(1f);
            }

            // touch: the hit shapes on her bones, her talk reaction, the gauge, one touch event per area
            var tc = h.Touch;
            var cam = Camera.main;
            var headBone = Bone(st.Cha, "cf_j_head");
            var bustBone = Bone(st.Cha, "cf_d_hit_bust_L");
            string pickChest = bustBone != null ? tc.Pick(cam.WorldToScreenPoint(bustBone.position)) : "";
            string pickHead = headBone != null ? tc.Pick(cam.WorldToScreenPoint(headBone.TransformPoint(0f, 0.2f, 0f))) : "";
            Check("touch: the hit shapes on her bones (chest, head)", pickChest == "chest" && pickHead == "head", "chest '" + pickChest + "' head '" + pickHead + "'");
            tc.ShowShapes(true);
            yield return Wait(0.3f);
            int nShapes = tc.ShapesShown;
            yield return Shot("10e_hit_shapes.png");
            tc.ShowShapes(false);
            yield return null;
            Check("touch: F4 shows her hit shapes in colour (one per shape), again hides them", nShapes >= 10 && tc.ShapesShown == 0, nShapes + " shown, after " + tc.ShapesShown);
            Note("hit shapes: shader " + tc.ShapeShader);
            tc.SetCursor(-2);
            bool handCur = tc.CursorNow == -2 && tc.IconReady(-2) && tc.IconReady(0) && tc.IconReady(1);
            tc.SetCursor(-1);
            Check("touch: the cursor over her is the original's hand (H's icon without its label); the original's aibu icons load (lips, the breast's hand)", handCur && tc.CursorNow == -1);
            Check("look: together outside a play her face turns to him (the neck's pattern 1 at the camera - the original's free roam)", h.Look.Turned
                  && st.Cha.fileStatus.neckLookPtn == 1 && st.Cha.neckLookCtrl.target == Camera.main.transform, h.Look.Why + " neck " + st.Cha.fileStatus.neckLookPtn);
            tc.PressForCheck("head", "");
            var cc = Singleton<Studio.Studio>.Instance.cameraCtrl;
            Check("touch: Studio's camera leaves the mouse alone during a press", cc.noCtrlCondition != null && cc.noCtrlCondition());
            tc.Pressing = "";
            tc.Hold = false;
            string faceBefore = st.Face;
            float gv = h.Gauge.Value;
            tc.Gesture("head", "tap", 1, 0.1f);
            yield return null;
            bool posePlaying = st.ReactUntil > 0f;
            Check("look: during a touch's reaction pose the neck goes by the motion (the original's talk: pattern 3)", st.Cha.fileStatus.neckLookPtn == 3 && h.Look.Why == "reaction",
                  h.Look.Why + " neck " + st.Cha.fileStatus.neckLookPtn);
            Check("touch: outside a play the original talk's reaction (her expression and pose)", tc.LastReaction != null && st.Face.StartsWith("expr:"),
                  st.Face + " " + Json.Write(tc.LastReaction));
            Check("touch: the gauge rises", h.Gauge.Value > gv, gv + " -> " + h.Gauge.Value);
            yield return Wait(3.5f);
            Check("touch: her reaction pose plays out past the old 3 s (as the original's talk; back to the stand when it ends)", posePlaying
                  && (st.ReactUntil > 0f || Blend.Now(st.Cha.animBody).normalizedTime >= 0.98f), "reacting " + st.ReactUntil);
            yield return Until(() => st.ReactUntil < 0f, 12f);
            yield return Until(() => tc.Sent.Count > 0, 6f);
            yield return Wait(1.2f);
            Check("touch: one touch event for the area after the window, with her reaction", tc.Sent.Count == 1 && Json.Str(tc.Sent[0], "area") == "head" && tc.Sent[0].ContainsKey("reaction")
                  && EventTypes().Contains("touch"), tc.Sent.Count > 0 ? Json.Write(tc.Sent[0]).Substring(0, 200) : "none");
            yield return Wait(1f);
            Check("touch: her face comes back after the reaction", st.Face == faceBefore, faceBefore + " -> " + st.Face);
            // her face to him within the original's neck limits: at her side (70 degrees) she turns, but not past 80 degrees (the
            // neck's and the head's 40 each); behind her (150) she looks ahead again (beyond its 90 / 100 degree field of view)
            Check("look: the face turns back to him after the reaction", h.Look.Turned && st.Cha.fileStatus.neckLookPtn == 1, h.Look.Why);
            float yawSide = 0f, yawBehind = 0f, yawSideL = 0f;
            yield return CameraAround(70f, 1.4f);
            yawSide = NeckYaw();
            yield return Shot("10c_look_side.png");
            yield return CameraAround(-70f, 1.4f);
            yawSideL = NeckYaw();
            yield return CameraAround(150f, 1.4f);
            yawBehind = NeckYaw();
            yield return Shot("10d_look_behind.png");
            Check("look: at her side she turns to him (about 70 degrees each way), never past the neck's 80; behind her she looks ahead",
                  Mathf.Abs(yawSide) > 45f && Mathf.Abs(yawSide) <= 82f && Mathf.Abs(yawSideL) > 45f && Mathf.Abs(yawSideL) <= 82f && Mathf.Sign(yawSide) != Mathf.Sign(yawSideL)
                  && Mathf.Abs(yawBehind) < 15f, string.Format("side {0:F1} other side {1:F1} behind {2:F1}", yawSide, yawSideL, yawBehind));
            Note(string.Format("look: her face at the camera 70 degrees to her one side {0:F1}, the other {1:F1}, behind her (150) {2:F1} (the neck's limit 80)", yawSide, yawSideL, yawBehind));
            st.ApplyCamera("medium", true);
            yield return Wait(0.5f);

            // approach: she walks up to the camera (the original's walk) and the camera settles close; lean_in leans her forward
            st.ApplyCamera("far", true);
            yield return Wait(0.3f);
            Vector3 pos0 = st.Heroine.oiCharInfo.changeAmount.pos;
            bool walked = st.ApplyScene("approach", 1, 1f) && st.Walking;
            yield return Wait(1.2f);
            var camT = Camera.main.transform;
            float faceAng = Vector3.Angle(camT.forward, st.Cha.objHeadBone.transform.position - camT.position);
            yield return Shot("11_walking.png");
            yield return Until(() => !st.Walking, 10f);
            yield return Wait(1.2f);
            Vector3 camPos = Camera.main.transform.position, at = st.Heroine.oiCharInfo.changeAmount.pos;
            float camDist = new Vector2(camPos.x - at.x, camPos.z - at.z).magnitude;
            Check("approach: she walks up to the camera and stops about 1 m short, the camera close", walked && !st.Walking && Vector3.Distance(pos0, at) > 0.3f
                  && Mathf.Abs(at.y - pos0.y) < 0.13f
                  && (camDist < 1.6f || Json.Str(st.WalkLog[st.WalkLog.Count - 1], "event") == "arrived") && st.CameraKey == "close" && st.ArrivalCamera == "close" && st.SceneKey == "approach",
                  string.Format("walked {0} moved {1:F2} m, {2:F2} m from the camera, {3} {4}", walked, Vector3.Distance(pos0, at), camDist, st.CameraKey, Json.Write(st.WalkLog)));
            Check("sound: footsteps of the walk (the map's floor sound)", h.Sounds.Log.Contains("dse00_01_00_00"), string.Join(",", h.Sounds.Log.ToArray()));
            // the place change's door as the original chooses it: a school room's sliding door, a hinged one elsewhere, going out to
            // the park the left room's closing, nothing between doorless places
            string dHealth = h.Sounds.Moving("park", "health_room"), dHome = h.Sounds.Moving("health_room", "myroom"),
                dOut = h.Sounds.Moving("myroom", "park"), dNone = h.Sounds.Moving("park", "park");
            Check("sound: the place change's door as the original chooses it (a school room's sliding door, a hinged one elsewhere, out to the park the left one closing)",
                  dHealth == "door_open" && dHome == "door2_open" && dOut == "door2_close" && dNone == null && h.Sounds.Log.Contains("se_door1_open") && h.Sounds.Log.Contains("se_door2_close"),
                  string.Join(" / ", new[] { dHealth, dHome, dOut, dNone ?? "none" }));
            Check("approach: while she walks the camera stays and keeps her face in view (the Godot app's); on arrival the preset nearest the distance",
                  faceAng < 3f, "angle " + faceAng.ToString("F1"));
            // a camera asked for during the walk (a block's) waits for her arrival
            st.ApplyCamera("far", true);
            yield return Wait(0.3f);
            bool walked2 = st.ApplyScene("approach", 1, 1f) && st.Walking;
            st.ApplyCamera("medium", false);
            bool keptDuring = st.Walking && st.CameraKey == "far";
            yield return Until(() => !st.Walking, 10f);
            yield return Wait(1.2f);
            Check("approach: a camera asked for during the walk is taken on her arrival", walked2 && keptDuring && st.ArrivalCamera == "medium" && st.CameraKey == "medium",
                  walked2 + " " + keptDuring + " " + st.ArrivalCamera + " " + st.CameraKey);
            // the room's approach place (the user's request, PR #23 - her way past the furniture): set, she walks to it and faces
            // as it says; back, towards the camera again
            {
                st.ApplyScene("idle", 1, 1f);
                st.Place(st.Heroine);
                yield return Wait(0.8f);
                var apEd = h.SpotEditor;
                Vector3 apFrom = st.Heroine.oiCharInfo.changeAmount.pos;
                var apCam = Camera.main.transform.position;
                var apDir = new Vector3(apCam.x - apFrom.x, 0f, apCam.z - apFrom.z).normalized;
                var apRef = apEd.ApproachSpot(st.Location);
                // (0.7 m towards the camera from where she stands, facing across it)
                Vector3 apWant = apFrom + apDir * 0.7f;
                float apYaw = Mathf.Atan2(apDir.x, apDir.z) * Mathf.Rad2Deg + 90f;
                apEd.Change(apRef, new Vector3(apWant.x - apRef.Pos.x, 0f, apWant.z - apRef.Pos.z), Mathf.DeltaAngle(apRef.Rot.eulerAngles.y, apYaw));
                int apLog0 = st.WalkLog.Count;
                bool apWalked = st.ApplyScene("approach", 1, 1f) && st.Walking;
                yield return Until(() => !st.Walking, 10f);
                yield return Wait(0.5f);
                Vector3 apAt = st.Heroine.oiCharInfo.changeAmount.pos;
                float apGot = new Vector2(apAt.x - apWant.x, apAt.z - apWant.z).magnitude;
                float apFacing = Mathf.Abs(Mathf.DeltaAngle(st.Heroine.oiCharInfo.changeAmount.rot.y, apYaw));
                string apTo = st.WalkLog.Count > apLog0 ? Json.Str(st.WalkLog[apLog0], "to", "") : "";
                apEd.Reset(apRef);
                Vector3 apNo;
                float apNoYaw;
                bool apBack = !apEd.ApproachIn(st.Location, out apNo, out apNoYaw);
                Check("approach: to the room's place when the user set one (her facing as it says), towards the camera again once it is reset",
                      apWalked && apTo == "place" && apGot < 0.05f && apFacing < 1f && apBack,
                      string.Format("walked {0} to {1} off {2:F3} m facing off {3:F1} back {4} {5}", apWalked, apTo, apGot, apFacing, apBack, Json.Write(st.WalkLog.GetRange(apLog0, st.WalkLog.Count - apLog0))));
                st.ApplyScene("idle", 1, 1f);
                st.Place(st.Heroine);
                yield return Wait(0.5f);
            }
            st.ApplyScene("lean_in", 3, 1f);
            yield return Wait(1.2f);
            Check("lean_in: the upper body leans forward by the intensity's degrees", Mathf.Abs(st.LeanNow - 17f) < 0.5f, st.LeanNow.ToString("F1"));
            yield return Shot("12_lean_in.png");
            st.ApplyScene("idle", 1, 1f);
            st.Place(st.Heroine);
            st.ApplyCamera("medium", true);
            yield return Wait(0.8f);

            // a play: the partner, Studio's H states, Muse's pace / insert / finish / pull
            var clothes = (byte[])st.Cha.fileStatus.clothesState.Clone();
            string err = pc.Begin("sonyu_00", "check", bed != null ? bed.Id : null);
            yield return Wait(0.5f);
            var row = pc.Current;
            int idleNo = Convert.ToInt32(Json.Obj(row, "female_states")["Idle"]);
            yield return Wait(1.2f);
            Check("play: the original's H row of the position", pc.HInfo != null && pc.HInfo.nameAnimation == "正常位", pc.HInfo != null ? pc.HInfo.nameAnimation : "none");
            Check("play: on the chosen spot (the bed), both bodies there", pc.CurrentSpot == bed && st.Partner != null
                  && Vector3.Distance(st.Heroine.oiCharInfo.changeAmount.pos, st.Partner.oiCharInfo.changeAmount.pos) < 0.001f
                  && Vector3.Distance(st.Heroine.oiCharInfo.changeAmount.pos, bed.Pos) < 0.6f, st.Heroine.oiCharInfo.changeAmount.pos + " bed " + (bed != null ? bed.Pos.ToString() : ""));
            Check("play: the position's clothes (the original's numFemaleLowerCloth)", pc.HInfo != null && st.Cha.fileStatus.clothesState[1] >= Math.Max(0, pc.HInfo.numFemaleLowerCloth),
                  "bottom " + st.Cha.fileStatus.clothesState[1] + " row " + (pc.HInfo != null ? pc.HInfo.numFemaleLowerCloth : -9));
            Check("play: the position's camera from the original's H list", st.CameraKey == "play" && pc.HInfo != null && HData.Camera(pc.HInfo.paramFemale.nameCamera) != null,
                  st.CameraKey + " " + (pc.HInfo != null ? pc.HInfo.paramFemale.nameCamera : ""));
            Check("play: 正常位 starts with the partner, both in Idle", err == "" && st.Partner != null && pc.LastState == "Idle" && st.Heroine.oiCharInfo.animeInfo.no == idleNo
                  && st.Partner.oiCharInfo.animeInfo.no == Convert.ToInt32(Json.Obj(row, "male_states")["Idle"]), err + " " + pc.LastState);
            // the play's spot moved (SpotEditor): both bodies move with it, then back
            {
                Vector3 b0 = st.Heroine.oiCharInfo.changeAmount.pos;
                h.SpotEditor.Change(pc.CurrentSpot, new Vector3(0.05f, 0f, 0f), 0f);
                yield return null;
                bool followed = Vector3.Distance(st.Heroine.oiCharInfo.changeAmount.pos, b0 + new Vector3(0.05f, 0f, 0f)) < 0.01f
                                && Vector3.Distance(st.Partner.oiCharInfo.changeAmount.pos, st.Heroine.oiCharInfo.changeAmount.pos) < 0.001f;
                h.SpotEditor.Reset(pc.CurrentSpot);
                yield return null;
                Check("play positions: the play's spot moved takes both bodies along; back again", followed
                      && Vector3.Distance(st.Heroine.oiCharInfo.changeAmount.pos, b0) < 0.01f, st.Heroine.oiCharInfo.changeAmount.pos + " from " + b0);
                // the settings' reset in a play: the play on the spot's new object - moved again, both bodies still follow (GPT's
                // re-review of #30)
                h.SpotEditor.ResetAll();
                yield return null;
                var rsNew = st.Spots.Find(o => o.Id == pc.CurrentSpot.Id);
                bool rebound = rsNew != null && ReferenceEquals(rsNew, pc.CurrentSpot);
                Vector3 rs0 = st.Heroine.oiCharInfo.changeAmount.pos;
                h.SpotEditor.Change(pc.CurrentSpot, new Vector3(0.04f, 0f, 0f), 0f);
                yield return null;
                bool rsFollowed = Vector3.Distance(st.Heroine.oiCharInfo.changeAmount.pos, rs0 + new Vector3(0.04f, 0f, 0f)) < 0.01f;
                h.SpotEditor.Reset(pc.CurrentSpot);
                yield return null;
                Check("play positions: the settings' reset in a play puts the play on the spot read again; moved after it, both bodies follow",
                      rebound && rsFollowed, "rebound " + rebound + " followed " + rsFollowed);
            }
            // the condom: undecided (the user's setting "ask"; the check starts there whatever the setting) refuses the insertion
            pc.Condom = null;
            int animInPlay0 = Refusals("anim", "in_play");
            Send(With(Line("入れるよ。"), "anim", "climax"), With(Line("入れるよ。"), "play", D("insert", true)));
            yield return Until(() => bp.Busy && bp.Text == "入れるよ。", 10f);
            yield return Wait(0.6f);
            Check("talk climax: in a play anim climax is refused (in_play) and the play goes on", Refusals("anim", "in_play") == animInPlay0 + 1 && pc.Active && !st.Climaxing,
                  Json.Write(LastEvent("block_refused")));
            bp.Advance(true);
            yield return Until(() => bp.Busy && bp.Text == "入れるよ。", 10f);
            yield return Wait(1.2f);
            Check("condom: undecided, the insertion is refused (condom_undecided)", !pc.Inserted && LastEvent("block_refused") != null
                  && Json.Str(LastEvent("block_refused"), "reason") == "condom_undecided", Json.Write(LastEvent("block_refused")));
            bp.Advance(true);
            Send(With(Line("入れて。"), "play", D("condom", false, "insert", true, "pace", 2)));
            yield return Until(() => pc.Inserted && pc.LastState == "WLoop", 15f);
            Check("eyes / neck: the original's patterns of the position (h/list fileMotionNeck) drive her look", pc.EyeNeckOn);
            var fbF = st.Cha.animBody.GetComponent<RootMotion.FinalIK.FullBodyBipedIK>();
            Check("motion IK: the original's MotionIK of the position runs on both bodies (the original's targets on the solver)", pc.IK.On && pc.IK.Calcs > 0 && pc.IK.Enabled
                  && fbF != null && fbF.enabled && fbF.solver.leftHandEffector.target != null && fbF.solver.leftHandEffector.target.name == "cf_t_hand_L",
                  "on " + pc.IK.On + " calcs " + pc.IK.Calcs + " enabled " + pc.IK.Enabled + " target " + (fbF != null && fbF.solver.leftHandEffector.target != null ? fbF.solver.leftHandEffector.target.name : "none"));
            pc.SetPartnerClothes(0);
            yield return null;
            Check("your clothes: dressed puts every garment of your body on (player_clothes_changed)", st.Partner.charInfo.fileStatus.clothesState[0] == 0
                  && Json.Str(pc.Info(), "player_clothes") == "dressed" && LastEvent("player_clothes_changed") != null);
            pc.SetPartnerClothes(2);
            yield return Wait(0.5f);           // (past the crossfade into the loop)
            var wsi = Blend.Now(st.Cha.animBody);
            float wEff = st.Cha.animBody.speed * wsi.speedMultiplier;
            Check("play: insert, then the pace's loop (pace 2: WLoop at speed 2, through the controller's speed as the original)", pc.Inserted && pc.LastState == "WLoop"
                  && Mathf.Abs(wEff - 2f) < 0.01f && Mathf.Abs(st.Heroine.animeSpeed - 1f) < 0.01f
                  && st.Partner.oiCharInfo.animeInfo.no == Convert.ToInt32(Json.Obj(row, "male_states")["WLoop"]), pc.LastState + " effective " + wEff + " animator " + st.Cha.animBody.speed);
            // the pre-climax loop keeps its own speed (2.0) at any pace, as the original's OLoop (it does not follow "speed")
            pc.PlayState("OLoop", pc.PaceSpeed(3));
            yield return Wait(0.6f);
            var osi = st.Cha.animBody.GetCurrentAnimatorStateInfo(0);
            float oEff = st.Cha.animBody.speed * osi.speedMultiplier * osi.speed;
            Check("play: the pre-climax loop (OLoop) plays at the original's own speed, not times the pace's", Mathf.Abs(oEff - 2f) < 0.05f, "effective " + oEff);
            pc.PlayState("WLoop", pc.PaceSpeed(2));
            yield return Wait(0.3f);
            Check("condom: Muse's play.condom (off) before the insertion reached the play (play_condom_changed)", pc.Condom == false && EventTypes().Contains("play_condom_changed")
                  && !st.Partner.charInfo.fileStatus.visibleGomu);
            bp.Advance(true);
            yield return Wait(1.5f);
            yield return Shot("03_play.png");
            // your body as the original H's male: hidden but its penis (Z), a see-through silhouette (C), the penis hidden (X)
            var ptc = st.Partner.charInfo;
            var objBody = ptc.GetReferenceInfo(ChaReference.RefObjKey.ObjBody);
            var son = ptc.GetReferenceInfo(ChaReference.RefObjKey.S_Son);
            var simple = ptc.GetReferenceInfo(ChaReference.RefObjKey.S_SimpleTop);
            pc.SetYourBody("body", false);
            yield return Wait(0.3f);
            bool hidOk = objBody != null && !objBody.activeSelf && son != null && son.activeSelf && ptc.objHead != null && !ptc.objHead.activeSelf;
            yield return Shot("03b_your_body_hidden.png");
            pc.SetYourBody("body", true);
            h.SilhouetteColor.Value = new Color(0.25f, 0.45f, 1f, 0.35f);
            pc.SetYourBody("silhouette", true);
            yield return Wait(0.3f);
            bool silOk = simple != null && simple.activeSelf && !objBody.activeSelf && ptc.fileStatus.visibleSimple;
            var simpleMat = ptc.rendSimpleBody != null ? ptc.rendSimpleBody.material : null;
            Note("silhouette: shader " + (simpleMat != null ? simpleMat.shader.name + " queue " + simpleMat.renderQueue + " colour " + simpleMat.color : "none"));
            yield return Shot("03c_your_silhouette.png");
            pc.SetYourBody("penis", false);
            yield return Wait(0.3f);
            bool sonOff = son != null && !son.activeSelf;
            pc.SetYourBody("penis", true);
            pc.SetYourBody("silhouette", false);
            h.SilhouetteColor.Value = new Color(0f, 0f, 1f, 1f);
            yield return Wait(0.3f);
            {
                var mu = h.UiForCheck;
                yield return RealInput.Click(Btn(mu, "プレイ"));
                yield return Wait(0.4f);
                Rect muR = mu.MenuRect;
                yield return Shot("03d_play_menu.png");
                yield return RealInput.Click(Btn(mu, "プレイ"));
                yield return Wait(0.3f);
                Check("UI: in a play the plays' menu stays within the screen (its list scrolls inside - the user's note of 2026-11-05)",
                      muR.height > 0f && muR.yMax <= Screen.height + 0.5f && mu.MenuRect.height == 0f, muR + " screen " + Screen.height + " " + RealInput.LastError);
            }
            Check("your body: hidden but its penis (the original H's Z), a silhouette in the setting's colour (C), the penis hidden (X), back",
                  hidOk && silOk && sonOff && objBody.activeSelf && son.activeSelf && !simple.activeSelf, "hidden " + hidOk + " silhouette " + silOk + " penis off " + sonOff);
            pc.SetView("pov");
            yield return Wait(0.5f);
            Check("目線へ: the camera at your eyes (your head hidden)", pc.View == "pov" && st.CameraKey == "play_pov" && !st.Partner.charInfo.fileStatus.visibleHeadAlways);
            yield return Shot("14_pov.png");
            // the head turned at your eyes (the user's request of 2026-11-04): the eyes stay, the look turns within its limits
            Vector3 povEye = Camera.main.transform.position, povFwd = Camera.main.transform.forward;
            pc.PovTurn(200f, 0f);
            yield return Wait(0.3f);
            float yawNow = Vector3.Angle(Vector3.ProjectOnPlane(povFwd, Vector3.up), Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up));
            float eyeMoved = Vector3.Distance(povEye, Camera.main.transform.position);
            yield return Shot("14b_pov_turned.png");
            pc.PovTurn(-200f, -200f);
            yield return Wait(0.3f);
            float upNow = Mathf.Asin(Mathf.Clamp(Camera.main.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg - Mathf.Asin(Mathf.Clamp(povFwd.y, -1f, 1f)) * Mathf.Rad2Deg;
            float rollNow = Vector3.Angle(Vector3.Cross(Camera.main.transform.forward, Vector3.up), Vector3.Cross(Camera.main.transform.forward, Camera.main.transform.up));
            pc.SetView("pov");
            yield return Wait(0.3f);
            float backAt = Vector3.Angle(povFwd, Camera.main.transform.forward);
            Check("目線へ: the head turns there (a turning drag / the turn keys) - the eyes stay, at most 60 degrees aside and 35 up (plays.json), never rolled; 目線へ again looks at her",
                  Mathf.Abs(yawNow - 60f) < 5f && Mathf.Abs(upNow - 35f) < 5f && rollNow < 2f && backAt < 4f && pc.PovYaw == 0f,
                  string.Format("aside {0:F1}, up {1:F1}, roll {2:F1}, the eyes moved {3:F3} m (the body's motion), back {4:F1}", yawNow, upNow, rollNow, eyeMoved, backAt));
            pc.SetView("position");
            yield return Wait(0.3f);
            Send(With(Line("もっと。"), "play", D("pace", 3)));
            yield return Until(() => pc.LastState == "SLoop", 8f);
            Check("play: pace 3 is the strong loop", pc.LastState == "SLoop" && pc.Pace == 3, pc.LastState);
            // (watched for a while: at one moment the sway may sit near 0.5 by chance)
            float swayMin = 1f, swayMax = 0f, swayOff = 0f, swayT0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - swayT0 < 6f)
            {
                yield return null;
                swayMin = Mathf.Min(swayMin, pc.motionNow);
                swayMax = Mathf.Max(swayMax, pc.motionNow);
                swayOff = Mathf.Max(swayOff, Mathf.Abs(st.Heroine.animePattern - pc.motionNow), Mathf.Abs(st.Partner.animePattern - pc.motionNow));
            }
            Check("play: the motion's own sway (the original's 'motion' 0..1, a new target every 3-5 s) on both bodies", swayMax - swayMin > 0.02f && swayOff < 0.02f,
                  "motion " + swayMin.ToString("F3") + ".." + swayMax.ToString("F3") + " target " + pc.MotionTarget.ToString("F3") + " bodies off by " + swayOff.ToString("F3"));
            float g0 = h.Gauge.Value, p0 = pc.Climax.Player;
            var heard = new HashSet<string>();
            float tSe = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - tSe < 3f)
            {
                foreach (var au in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                    if (au.isPlaying && au.clip != null) heard.Add(au.clip.name);
                yield return null;
            }
            Check("sound: the original's H sounds with the motion (HSeCtrl over the position's list)", h.Sounds.HOn && heard.Count > 0,
                  "on " + h.Sounds.HOn + " heard: " + string.Join(",", new List<string>(heard).ToArray()));
            Note("H sounds heard: " + string.Join(",", new List<string>(heard).ToArray()));
            yield return Fps("play, two characters (strong loop)");
            var playSt = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "play");
            Check("gauge: hers rises with the pace; the user has none (the user's climax is declared: no player_gauge, nothing counted)",
                  h.Gauge.Value > g0 + 0.02f && pc.Climax.Player == 0f && playSt != null && !playSt.ContainsKey("player_gauge"),
                  string.Format("hers {0:F3}->{1:F3}, the user's {2:F3}", g0, h.Gauge.Value, pc.Climax.Player));

            // the user's climax in the male part is the finish (play.finish), not play.climax
            Send(With(Line("まだだめ。"), "play", D("climax", "player")));
            yield return Until(() => bp.Busy && bp.Text == "まだだめ。", 10f);
            yield return Wait(0.5f);
            var rfc = LastEvent("block_refused");
            Check("your climax: in the male part play.climax is refused (male_part_finishes: the finish is the male part's)", Json.Str(rfc, "field") == "play.climax"
                  && Json.Str(rfc, "reason") == "male_part_finishes", Json.Write(rfc));
            bp.Advance(true);
            // the declared ejaculation: soon holds the OLoop, then a place plays the original's finish
            Send(With(Line("出そう。"), "play", D("finish", "soon")));
            yield return Until(() => pc.Climax.Holding && pc.LastState == "OLoop", 8f);
            Check("finish: soon holds the OLoop", pc.Climax.Holding && pc.LastState == "OLoop", pc.LastState);
            Send(With(Line("中に。"), "play", D("finish", "inside")));
            string seen = "";
            yield return Until(() => { if (pc.LastState.StartsWith("M_IN") || pc.LastState.StartsWith("WS_IN") || pc.LastState.StartsWith("SS_IN")) seen = pc.LastState; return seen.Length > 0; }, 8f);
            Check("finish: inside plays the original's finish from the hold (no second OLoop)", seen.Length > 0 && !pc.Climax.Holding, seen + " " + pc.Climax.SeqName);
            // her eyes through the climax: at your body, never the camera (the setting's default; the user's note of 2026-11-04)
            int camFrames = 0, gazeFrames = 0;
            float tGz = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - tGz < 30f && !(!pc.Climax.Running && pc.LastState == "InsertIdle"))
            {
                gazeFrames++;
                if (st.Cha.eyeLookCtrl.target == Camera.main.transform) camFrames++;
                yield return null;
            }
            Check("gaze: through the finish (the two's climax) her eyes never at the camera - at your body (the setting's default)", gazeFrames > 10 && camFrames == 0 && h.GazeAtYourBody.Value,
                  camFrames + " of " + gazeFrames + " frames at the camera");
            Check("finish: after it, still in and resting at pace 0", !pc.Climax.Running && pc.LastState == "InsertIdle" && pc.Inserted && pc.Pace == 0 && pc.CameInside, pc.LastState);
            Check("finish: the user's climax reached Muse", EventTypes().Contains("climax"));

            // her climax from her gauge (the app's): the female climax states while in
            Send(With(Line("もう一度。"), "play", D("pace", 2)));
            yield return Until(() => pc.LastState == "WLoop", 8f);
            yield return Until(() => h.Gauge.Phase == "rising", 30f);
            h.Gauge.Value = 0.995f;
            string fseq = "";
            yield return Until(() => { if (pc.Climax.Running) fseq = pc.Climax.SeqName; return fseq.Length > 0; }, 10f);
            Check("climax: her gauge at 1 plays her climax (female_w while in at the weak loop)", fseq == "female_w" && h.Gauge.Phase == "afterglow", fseq + " " + h.Gauge.Phase);
            int camF2 = 0, gzF2 = 0;
            float tGz2 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - tGz2 < 25f && pc.Climax.Running)
            {
                gzF2++;
                if (st.Cha.eyeLookCtrl.target == Camera.main.transform) camF2++;
                yield return null;
            }
            Check("gaze: through her climax her eyes never at the camera", gzF2 > 10 && camF2 == 0, camF2 + " of " + gzF2 + " frames at the camera");
            Check("climax: afterwards resting at pace 0", !pc.Climax.Running && pc.Pace == 0 && pc.LastState == "InsertIdle", pc.LastState);

            Send(With(Line("抜いて。"), "play", D("pull", true)));
            yield return Until(() => pc.LastState == "Drop" || pc.Liquid.Dripped > 0, 15f);
            yield return Until(() => pc.Liquid.Dripped > 0, 4f);
            var dk = Bone(st.Cha, "cf_j_kokan");
            st.SetCamera(dk.position + st.Cha.transform.right * 0.8f + Vector3.up * 0.2f, dk.position, 40f, 0f);
            yield return Wait(0.3f);
            var drip = pc.Liquid.LastDrip;
            Note("drip particle: " + (drip != null ? string.Format("playing {0} count {1} at {2}", drip.isPlaying, drip.particleCount, drip.transform.parent.name) : "none"));
            yield return Shot("17_drip.png");
            Check("liquid: after the creampie the pull-out drips (the particle at the metaball list's axis and timing)", pc.Liquid.Dripped > 0, "dripped " + pc.Liquid.Dripped + " " + pc.LastState + " inside " + pc.CameInside + " | " + pc.Liquid.Describe()
                  + string.Format(" | anim cur {0} next {1} trans {2} drop {3} A_Drop {4} nt {5:F2} anime {6}", st.Cha.animBody.GetCurrentAnimatorStateInfo(0).shortNameHash,
                      st.Cha.animBody.GetNextAnimatorStateInfo(0).shortNameHash, st.Cha.animBody.IsInTransition(0), Animator.StringToHash("Drop"), Animator.StringToHash("A_Drop"),
                      st.Cha.animBody.GetCurrentAnimatorStateInfo(0).normalizedTime, st.Heroine.oiCharInfo.animeInfo.no));
            yield return Until(() => !pc.Inserted && pc.LastState == "Idle", 20f);
            Check("play: pull out after the creampie (Pull, Drop, OUT_A), back to Idle", !pc.Inserted && pc.LastState == "Idle", pc.LastState);
            var types = EventTypes();
            Check("events: play_started, play_pace_changed, play_insertion_changed, play_finish, gauge_milestone", types.Contains("play_started") && types.Contains("play_pace_changed")
                  && types.Contains("play_insertion_changed") && types.Contains("play_finish") && types.Contains("gauge_milestone"), string.Join(",", types.ToArray()));

            // the pacing material in the state (option B of 2026-11-03: no beat_request / choice_request): the seconds since the
            // last package was presented, the play's packages, the question point
            bp.Advance(true);
            Send(Line("拍の材料。"));
            yield return Until(() => bp.Busy && bp.Text == "拍の材料。" && bp.PresentedLast, 10f);
            yield return Wait(3.5f);
            var bst = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "beat");
            Check("beat: the state has the seconds since the last package was presented and the play's packages (no beat_request / choice_request)",
                  bst != null && Json.Num(bst, "since_presented", -1) >= 1.0 && (int)Json.Num(bst, "packages_in_play") >= 1
                  && !EventTypes().Contains("beat_request") && !EventTypes().Contains("choice_request"), Json.Write(bst));
            h.Beat.NearReset();
            yield return Until(() => h.Gauge.Phase == "rising", 30f);
            h.Gauge.Value = 0.9f;
            yield return Wait(2.5f);
            bst = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "beat");
            Check("beat: her gauge near - question_due near", Json.Str(bst, "question_due") == "near", Json.Write(bst) + " phase " + h.Gauge.Phase + " " + h.Gauge.Value);
            turn++;
            Http("POST", "/api/packages", D("session", "check", "turn", turn, "beat", 7, "choices", D("kind", "stance", "options", new List<object> { "はい", "いいえ" }),
                "blocks", new List<object> { Line("問いを立てる。") }));
            yield return Until(() => bp.Busy && bp.Text == "問いを立てる。", 10f);
            yield return Wait(2.5f);
            bst = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "beat");
            Check("beat: a package with a question plays (its old beat field ignored) and the near point is done", bp.Text == "問いを立てる。"
                  && Json.Str(bst, "question_due") == null && (int)Json.Num(bst, "since_question", -1) == 0 && !Json.Has(bst, "since_presented") == (bp.Busy && !bp.PresentedLast),
                  bp.Text + " " + Json.Write(bst));
            bp.Advance(true);
            Send(With(Line("次はゴムを付けて。"), "play", D("condom", true)));
            yield return Until(() => pc.Condom == true, 10f);
            yield return null;
            Check("condom: on while out, it shows on the penis (visibleGomu)", pc.Condom == true && st.Partner.charInfo.fileStatus.visibleGomu
                  && Json.Obj(pc.Info(), "condom") == null && pc.Info()["condom"] is bool && (bool)pc.Info()["condom"]);
            yield return Wait(0.5f);
            yield return Shot("07_condom.png");
            bp.Advance(true);
            pc.End("check");
            yield return Wait(0.5f);
            bool same = true;
            for (int i = 0; i < clothes.Length; i++) if (clothes[i] != st.Cha.fileStatus.clothesState[i]) same = false;
            Check("play: ends - partner gone, clothes back, idle scene", !pc.Active && st.Partner == null && same && st.SceneKey == "idle");
            var fbEnd = st.Cha.animBody.GetComponent<RootMotion.FinalIK.FullBodyBipedIK>();
            Check("motion IK: after the play Studio's own IK targets are back", fbEnd != null && !fbEnd.enabled && fbEnd.solver.leftHandEffector.target != null
                  && fbEnd.solver.leftHandEffector.target.name.EndsWith("(work)"), fbEnd != null && fbEnd.solver.leftHandEffector.target != null ? fbEnd.solver.leftHandEffector.target.name : "none");
            Check("play: ends - back where she stands", Vector3.Distance(st.Heroine.oiCharInfo.changeAmount.pos, st.Spots[0].Pos) < 0.001f);

            // every play has a state for each pace (a wait under the original's own name); the service plays, started here, wait
            // in their motion at pace 0 with both bodies (the trial run of 2026-11-03: no Idle - the male body stood in a T pose,
            // she in her talk pose). (Not every play started: switching motions every frame crashed Studio.)
            var noState = new List<string>();
            var paceDoc = Json.Obj(Data.Load("plays.json"), "pace");
            string[] waits = { "Idle", "Stop_Idle", "Oral_Idle", "InsertIdle" };
            foreach (var prow in pc.List(null))
            {
                var fst = Json.Obj(prow, "female_states");
                foreach (var pe in Json.Arr(paceDoc, ((int)Json.Num(prow, "mode")).ToString()) ?? new List<object>())
                {
                    string nm = (string)((List<object>)pe)[0];
                    bool has = fst != null && fst.ContainsKey(nm);
                    if (!has && Array.IndexOf(waits, nm) >= 0) foreach (var w in waits) if (fst != null && fst.ContainsKey(w)) has = true;
                    if (!has) noState.Add(Json.Str(prow, "id") + ":" + nm);
                }
            }
            Check("play: every play has a motion state for each pace (a wait by the original's own name)", noState.Count == 0, string.Join(", ", noState.ToArray()));
            var notWaiting = new List<string>();
            int tried = 0;
            var alSample = new List<string> { "al_houshi_1", "al_houshi_28", "al_houshi_10", "al_houshi_1003", "al_houshi_1001", "al_sonyu_33", "al_sonyu_32", "al_sonyu_57" };
            var waitRows = new List<Dictionary<string, object>>();
            foreach (var prow in pc.List("houshi")) if (Json.Str(prow, "source", "") != "al") waitRows.Add(prow);
            foreach (var aid in alSample) if (pc.Row(aid) != null) waitRows.Add(pc.Row(aid));
            foreach (var prow in waitRows)
            {
                string pid = Json.Str(prow, "id");
                if (!(bool)pc.Availability(prow)["ok"]) { if (pid.StartsWith("al_")) notWaiting.Add(pid + "(not available: " + Json.Str(pc.Availability(prow), "reason") + ")"); continue; }
                string perr2 = pc.Begin(pid, "check", "here");
                if (perr2.Length > 0) perr2 = pc.Begin(pid, "check");
                if (perr2.Length > 0) continue;
                tried++;
                yield return Wait(0.4f);
                var fa = pc.Reversed ? st.Partner.oiCharInfo.animeInfo : st.Heroine.oiCharInfo.animeInfo;
                var fgc = Json.Arr(prow, "female");
                bool ok = pc.LastState.Length > 0 && fgc != null && fa.group == StudioAnim.Group(Convert.ToInt32(fgc[0]), fgc.Count > 2 ? fgc[2] as string : null)
                          && fa.category == Convert.ToInt32(fgc[1]);
                var mgc = Json.Arr(prow, "male");
                var mbody = pc.Reversed ? st.Heroine : st.Partner;
                if (ok && mgc != null && mbody != null)
                {
                    var ma = mbody.oiCharInfo.animeInfo;
                    ok = ma.group == StudioAnim.Group(Convert.ToInt32(mgc[0]), mgc.Count > 2 ? mgc[2] as string : null) && ma.category == Convert.ToInt32(mgc[1]);
                }
                if (pid == "al_sonyu_33") { pc.Camera(true); yield return Wait(0.6f); yield return Shot("05c_al_standing_doggy.png"); }
                if (pid == "al_houshi_1003")
                {
                    pc.Camera(true);
                    yield return Wait(0.6f);
                    yield return Shot("05d_al_cunnilingus.png");
                    Check("play: a service play where he serves her (cunnilingus): she receives (her rate), he has no finish (serving)", pc.Climax.CanFinish() == "serving"
                          && pc.Res.Roles["player"] == "giver" && pc.Res.Rates["character"] > pc.Res.Rates["player"], pc.Climax.CanFinish() + " " + Json.Write(pc.Info()["roles"]));
                }
                if (!ok) notWaiting.Add(pid + "(" + pc.LastState + ")");
                if (pid == "houshi_01") { pc.Camera(true); yield return Wait(0.6f); yield return Shot("05b_houshi_wait.png"); }
                pc.End("check");
                yield return Wait(0.3f);
            }
            // the speed's top: the original's (insertion 3.0, service 2.5) - Muse's speed does not go past it; your height 0.8
            h.YourHeight.Value = 0.8f;
            byte shortsPre = st.Cha.fileStatus.clothesState[3];
            string sperr = pc.Begin("sonyu_00", "check", "here");
            yield return Until(() => pc.PenisAim == Penetration.Vaginal || !Penetration.Available(st.Partner.charInfo), 5f);
            // an insertion: her shorts shifted aside where the garment has the half state (else as the position has them)
            Dictionary<byte, string> shortsKinds = null;
            bool hasHalf = st.Cha.dictStateType != null && st.Cha.dictStateType.TryGetValue(3, out shortsKinds) && shortsKinds.ContainsKey(1) && Clothes.Worn(st.Cha).Contains(3);
            Check("insertion: her shorts (on before the play) shifted aside where the garment has the half state, whatever the position's undressing; the plays' categories (正常位 / 手 / 足) in the catalog too",
                  (!hasHalf || shortsPre >= 3 || st.Cha.fileStatus.clothesState[3] == 1) && pc.Category(pc.Row("sonyu_00")) == "正常位" && pc.Category(pc.Row("houshi_01")) == "手"
                  && pc.Category(pc.Row("al_houshi_1")) == "足" && Json.Str(pc.Row("al_houshi_1"), "name") == "立ったまま足コキ",
                  "half " + hasHalf + " before " + shortsPre + " shorts " + st.Cha.fileStatus.clothesState[3] + " " + pc.Category(pc.Row("sonyu_00")));
            bool bpOn = Penetration.Available(st.Partner.charInfo), bpAimed = Penetration.Get(st.Partner.charInfo) == Penetration.Vaginal;
            pc.SpeedFactor = 2f;
            float s3 = pc.PaceSpeed(3), s1 = pc.PaceSpeed(1);
            pc.SpeedFactor = 1f;
            var bpBody = st.Partner.charInfo;
            float yourHt = bpBody.fileBody.shapeValueBody[0], yourRatio = pc.MaleRatio;
            pc.End("check");
            h.YourHeight.Value = 0.6f;
            Check("your height: the setting's height on your male body (the original's male maker has none), the hips' anchor for it", Mathf.Abs(yourHt - 0.8f) < 0.001f
                  && Mathf.Abs(yourRatio - 1f) > 0.002f, yourHt.ToString("F3") + " ratio " + yourRatio.ToString("F3") + " set back " + HIK.HeightResets);
            yield return Wait(0.3f);
            Check("penis: an insertion aims the partner's penis at her (BetterPenetration's auto target: vaginal), off after", !bpOn || (bpAimed && pc.PenisAim == 0),
                  "plugin " + bpOn + " aimed " + bpAimed);
            Check("play: Muse's speed stops at the original's top (insertion: pace 3 x 2.0 -> 3.0; pace 1 x 2.0 -> 2.0)", sperr == "" && Mathf.Abs(s3 - 3f) < 0.001f && Mathf.Abs(s1 - 2f) < 0.001f,
                  sperr + " " + s3 + " / " + s1);
            Check("play: the service plays (and a sample of the AL plays, their motions found by clip) begin in their own wait at pace 0, both bodies in the play's motion",
                  tried >= 15 && notWaiting.Count == 0,
                  tried + " tried, not waiting: " + string.Join(", ", notWaiting.ToArray()));

            // who does what: the user's body and her attribute decide the plays; reversed she plays the male part
            pc.WhoForCheck = Bodies("male", false, false);
            var avS = pc.Availability(pc.Row("sonyu_00"));
            var avH = pc.Availability(pc.Row("houshi_00"));
            var avL = pc.Availability(pc.Row("lesbian_00"));
            Check("roles: a male user - 正常位 both ways (reversed with a strapon), a reversed hand job needs her penis, no lesbian play",
                  (bool)avS["ok"] && (bool)avS["reversed_ok"] && (bool)avH["ok"] && !(bool)avH["reversed_ok"] && !(bool)avL["ok"],
                  Json.Write(avH["reversed_reason"]) + " " + Json.Write(avL["reason"]));
            err = pc.Begin("sonyu_00", "check", bed != null ? bed.Id : null, true);
            yield return Wait(1.5f);
            var rrow = pc.Current;
            bool strapOn = Strapon.Id() >= 0 && st.Cha.nowCoordinate.clothes.parts[3].id == Strapon.Id() && st.Cha.fileStatus.clothesState[3] == 0;
            Check("reversed: she plays the original's male part, the user's body the female part",
                  err == "" && pc.Reversed && st.Heroine.oiCharInfo.animeInfo.group == Convert.ToInt32(Json.Arr(rrow, "male")[0])
                  && st.Partner != null && st.Partner.oiCharInfo.animeInfo.group == Convert.ToInt32(Json.Arr(rrow, "female")[0]),
                  err + " her group " + st.Heroine.oiCharInfo.animeInfo.group);
            Check("reversed: without a penis she wears the mod strapon (no finish: strapon)", pc.Res.Strapon == "character" && strapOn && pc.Climax.CanFinish() == "strapon",
                  "strapon id " + Strapon.Id() + " shorts " + st.Cha.nowCoordinate.clothes.parts[3].id + " " + pc.Climax.CanFinish());
            var oc = LastEvent("outfit_changed");
            Check("outfit_changed: the strapon in her shorts' slot is told (changed co_shorts, source app); the state's outfit has it", !strapOn
                  || (oc != null && Json.Arr(oc, "changed") != null && Json.Arr(oc, "changed").Contains("co_shorts") && Json.Str(oc, "source") == "app"
                      && Convert.ToInt32(Json.Num(Json.Obj(Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "outfit"), "co_shorts"), "id")) == Strapon.Id()),
                  oc == null ? "none" : Json.Write(oc));
            pc.Condom = false;
            pc.Insert(2);
            yield return Until(() => pc.LastState == "WLoop", 15f);
            yield return Wait(1f);
            float herHt = st.Cha.fileBody.shapeValueBody[0];
            Vector3 dh = st.Heroine.oiCharInfo.changeAmount.pos - st.Partner.oiCharInfo.changeAmount.pos;
            Note("height probe: " + HIK.LastProbe + " / again " + HIK.HeightRatio(st.Cha).ToString("F4") + " " + HIK.LastProbe);
            Check("height: she plays the male part from where a 0.6 tall body would (the hips' anchor)", Mathf.Abs(herHt - 0.6f) < 0.01f || (Mathf.Abs(pc.MaleRatio - 1f) > 0.002f && dh.magnitude > 0.0005f),
                  string.Format("her height {0:F2} ratio {1:F3} shift {2:F3} m", herHt, pc.MaleRatio, dh.magnitude));
            yield return Shot("08_reversed.png");
            // (PR #12 (8)) the user's penis in the female part: laid up along the user's belly, not into her body
            float restAng = Vector3.Angle(pc.Rest.DirForCheck, pc.Rest.UpForCheck);
            Check("reversed: the user's penis (the female part's) is laid up along the user's belly, not run into her body",
                  pc.Rest.On && restAng < 30f && st.Partner.charInfo.fileStatus.visibleSonAlways, "on " + pc.Rest.On + " angle to the spine " + restAng.ToString("F0") + "°");
            // the user in the female part: the user's climax is declared (Muse's play.climax player, the user's answer), never a gauge
            int pcl0 = EventCount("climax", "who", "player");
            Send(With(Line("イっていいよ。"), "play", D("climax", "player")));
            yield return Until(() => EventCount("climax", "who", "player") > pcl0, 10f);
            yield return Wait(0.6f);
            var pclEv = LastEvent("climax");
            bool userMotion = pc.Climax.Running;
            Check("your climax: in the female part it is declared (play.climax player: climax who player, source declared) and her climax motion plays for your body",
                  Json.Str(pclEv, "who") == "player" && Json.Str(Json.Obj(pclEv, "cause"), "source") == "declared" && userMotion && !pclEv.ContainsKey("player_gauge"),
                  Json.Write(pclEv).Substring(0, Math.Min(240, Json.Write(pclEv).Length)) + " running " + userMotion);
            bp.Advance(true);
            yield return Until(() => !pc.Climax.Running, 15f);
            pc.End("check");
            yield return Wait(0.5f);
            Check("reversed: the play's end takes the strapon off (and lets the user's penis be)", !Strapon.On(st.Cha) && st.Cha.nowCoordinate.clothes.parts[3].id != Strapon.Id() && !pc.Rest.On);
            var anrow = pc.Row("sonyu_02");
            var anSpots = pc.Spots(anrow);
            err = pc.Begin("sonyu_02", "check", anSpots.Count > 0 ? anSpots[0].Id : null, false, true);
            yield return Wait(1.2f);
            pc.Condom = false;
            pc.Insert(2);
            yield return Until(() => pc.LastState == "WLoop", 15f);
            Check("anal: the original's anal motion (Studio's アナル category, A_WLoop)", err == "" && pc.Anal
                  && st.Heroine.oiCharInfo.animeInfo.category == Convert.ToInt32(Json.Arr(anrow, "anal_female")[1])
                  && st.Heroine.oiCharInfo.animeInfo.no == Convert.ToInt32(Json.Obj(anrow, "anal_female_states")["A_WLoop"]),
                  err + " " + pc.LastState + " " + st.Heroine.oiCharInfo.animeInfo.category + "/" + st.Heroine.oiCharInfo.animeInfo.no);
            yield return Wait(1f);
            bool danInside = pc.PenisAim == 0 || !pc.Dan.On;        // (inside: BetterPenetration's aim, the original's off)
            yield return Shot("09_anal.png");
            var cd = Singleton<Studio.Studio>.Instance.cameraCtrl.Export();
            Vector3 camEye = cd.pos + Quaternion.Euler(cd.rotate) * cd.distance;
            float wallHit;
            Check("camera: the position's camera stays in front of the map's walls and furniture", !Stage.MapRay(cd.pos, camEye, out wallHit) || wallHit > (camEye - cd.pos).magnitude - 0.05f,
                  string.Format("hit {0:F2} of {1:F2}", wallHit, (camEye - cd.pos).magnitude));
            Send(With(Line("外に。"), "play", D("finish", "outside")));
            bool danOut = false;
            float danT0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - danT0 < 25f && !(pc.Liquid.Fired > 0 || (!pc.Climax.Running && pc.LastState == "Idle" && !pc.Inserted)))
            {
                if (DanLook.IsOutside(pc.FemaleCha) && pc.Dan.On) danOut = true;
                yield return null;
            }
            yield return Wait(0.4f);
            yield return Shot("15_outside.png");
            Check("penis: in the outside finish the original's aim holds it (Lookat_dan with the position's list), inside BetterPenetration's (the user's note, PR #23 (a))",
                  pc.Dan.Active && danOut && danInside, "active " + pc.Dan.Active + " " + pc.Dan.Why + " on outside " + danOut + " off inside " + danInside + " aim " + pc.PenisAim);
            Check("liquid: an outside finish shoots the original's particle semen (h_particle, the metaball list's timings)", pc.Liquid.Fired > 0,
                  "fired " + pc.Liquid.Fired + " on " + pc.Liquid.On + " " + pc.LastState);
            yield return Until(() => !pc.Climax.Running, 25f);
            bp.Advance(true);
            pc.End("check");
            yield return Wait(0.5f);
            pc.WhoForCheck = Bodies("female", false, false);
            var lrow = pc.Row("lesbian_00");
            var lSpots = pc.Spots(lrow);
            err = pc.Begin("lesbian_00", "check", lSpots.Count > 0 ? lSpots[0].Id : null);
            yield return Wait(1.2f);
            Check("roles: a female user - the lesbian play with the default female body", err == "" && st.Partner != null && st.Partner.charInfo.sex == 1, err);
            pc.End("check");
            yield return Wait(0.5f);
            pc.WhoForCheck = Bodies("male", false, false);
            err = pc.Begin("aibu_00", "check", null, true);
            yield return Wait(0.8f);
            Check("reversed aibu: no original motion - the group's scene, no partner", err == "" && !pc.Motion && st.Partner == null && st.SceneKey == "tease", err + " " + st.SceneKey);
            pc.End("check");
            yield return Wait(0.5f);
            pc.WhoForCheck = null;

            // aibu: the original's items on her (HandCtrl's prefabs and layers), her touch states, the kiss
            var hands = h.Hands;
            var arow = pc.Row("aibu_00");
            var aspots = pc.Spots(arow);
            err = pc.Begin("aibu_00", "check", aspots.Count > 0 ? aspots[0].Id : null);
            yield return Wait(1.5f);
            Check("aibu: 愛撫 starts alone in Idle", err == "" && pc.Active && pc.Mode == 0 && st.Partner == null && pc.LastState == "Idle", err + " " + pc.LastState);
            var chestKinds = hands.KindsFor("chest");
            Check("aibu: the chest takes the original's items", chestKinds.Contains("hand") && chestKinds.Count >= 2, string.Join(",", chestKinds.ToArray()));
            int iconL = hands.IconFor("chest", "L"), iconR = hands.IconFor("chest", "R"), iconKiss = hands.IconFor(Json.Str(Json.Obj(tc.Doc, "aibu_hands"), "kiss_area", "cheek"), "");
            Check("aibu: the cursor shows the original's icon of the area's item (the left breast's hand 1, the right's 5, the kiss's lips 0)",
                  hands.Chosen("chest") == "hand" && iconL == 1 && iconR == 5 && iconKiss == 0, hands.Chosen("chest") + " " + iconL + " " + iconR + " " + iconKiss);
            int sentBefore = tc.Sent.Count;
            tc.PressForCheck("chest", "L");
            yield return Wait(0.3f);
            var handObj = hands.ObjectOn("muneL");
            Check("aibu: a press puts the hand on her breast (on her bone) and she plays M_Touch", handObj != null && handObj.gameObject.activeInHierarchy
                  && handObj.IsChildOf(st.Cha.objBodyBone.transform) && pc.LastState == "M_Touch" && hands.Ctrl == "click",
                  (handObj != null ? handObj.name + " under " + handObj.parent.name : "none") + " " + pc.LastState + " " + hands.Ctrl);
            yield return Until(() => hands.Ctrl == "drag", 6f);
            bool dragLayer = false;
            foreach (var e in hands.Log) if (Json.Str(e, "event") == "action" && Convert.ToInt32(e["action"]) == 2) dragLayer = true;
            Check("aibu: held, the click goes on to the drag (layer action 2)", hands.Ctrl == "drag" && dragLayer, hands.Ctrl);
            yield return Wait(0.8f);
            yield return Shot("05_aibu.png");
            tc.Hold = false;
            yield return Wait(0.3f);
            Check("aibu: the release finishes, the hand stays resting there", hands.Ctrl == "" && hands.ObjectOn("muneL") != null, hands.Ctrl);
            yield return Until(() => pc.LastState == "Idle", 10f);
            pc.SetPace(2, "user");
            yield return null;
            Check("aibu: pace 2 is the touched part's loop (M_Idle)", pc.LastState == "M_Idle", pc.LastState);
            hands.Cycle("chest", 1);
            yield return null;
            var fingerObj = hands.ObjectOn("muneL");
            Check("aibu: the wheel swaps the item there (the next the area takes)", fingerObj != null && fingerObj != handObj && Json.Str(Json.Obj(hands.Info(), "attached"), "muneL") == chestKinds[1],
                  Json.Write(hands.Info()));
            tc.PressForCheck("cheek", "");
            yield return Wait(0.3f);
            Check("aibu: a press on her face is the kiss (K_Touch, the original's kiss camera)", pc.Kissing && pc.LastState == "K_Touch" && st.CameraKey == "play_kiss", pc.LastState + " " + st.CameraKey);
            yield return Until(() => pc.LastState == "K_Loop", 10f);
            yield return Wait(0.5f);
            yield return Shot("06_kiss.png");
            Check("aibu: the kiss goes on in K_Loop while held", pc.LastState == "K_Loop", pc.LastState);
            tc.Hold = false;
            yield return Wait(0.5f);
            Check("aibu: the release ends the kiss (back to the loop, the H camera)", !pc.Kissing && pc.LastState == "M_Idle" && st.CameraKey == "play", pc.LastState + " " + st.CameraKey);
            yield return Until(() => tc.Sent.Count >= sentBefore + 2, 6f);
            Dictionary<string, object> chestEv = null, kissEv = null;
            for (int i = sentBefore; i < tc.Sent.Count; i++)
            {
                if (Json.Str(tc.Sent[i], "area") == "chest") chestEv = tc.Sent[i];
                if (Json.Str(tc.Sent[i], "area") == "cheek") kissEv = tc.Sent[i];
            }
            Check("aibu: the touch events carry the item (hand, kiss)", chestEv != null && Json.Str(chestEv, "item") == "hand" && kissEv != null && Json.Str(kissEv, "item") == "kiss",
                  (chestEv != null ? Json.Str(chestEv, "item") : "no chest") + " " + (kissEv != null ? Json.Str(kissEv, "item") : "no cheek"));
            hands.DetachAll();
            yield return null;
            Check("aibu: 外す takes every item off", !hands.Active && fingerObj != null && !fingerObj.gameObject.activeSelf);
            pc.End("check");
            yield return Wait(0.5f);
            BustAt("after the aibu items");

            // the sudden grope: outside a play, holding her chest starts the standing aibu where she is (play_started source touch)
            if (pc.Active) pc.End("check");
            yield return Wait(0.5f);
            tc.PressForCheck("chest", "L");
            yield return Wait(1.0f);
            tc.Pressing = "";
            tc.Hold = false;
            yield return Wait(0.8f);
            var gps = LastEvent("play_started");
            Check("touch: holding her chest outside a play gropes her - the standing aibu starts where she is (play_started source touch)", pc.Active
                  && Json.Str(pc.Current, "id") == "aibu_00" && pc.CurrentSpot != null && pc.CurrentSpot.Here && Json.Str(gps, "source") == "touch",
                  (pc.Active ? Json.Str(pc.Current, "id") : "no play") + " " + (gps == null ? "" : Json.Str(gps, "source")));
            yield return Until(() => pc.LastState == "M_Idle", 8f);
            Check("aibu: her reaction to the touch goes on to the chest's loop (M_Touch then M_Idle) at pace 0 while he is on her", pc.LastState == "M_Idle" && pc.Pace == 0,
                  pc.LastState + " pace " + pc.Pace);
            pc.End("check");
            yield return Wait(0.5f);

            // ---- the trial runs 5 and 6 (GPT's handoff of Alpha PR #1, 2026-11-06) ----
            // 1. a still hold: nothing before it is a hold, one hold's worth as it becomes one, more while it is held, nothing more
            //    at its release (no double count); a hold moved on into a stroke stops counting, the stroke counted at its release
            var hgG = h.Gauge;
            hgG.Value = 0.1f;
            float hgV0 = hgG.Value, hgWorth = hgG.TouchWorth("thigh", "hold", 1);
            int hgSent0 = tc.Sent.Count;
            tc.PressForCheck("thigh", "");
            yield return Wait(0.3f);
            float hgEarly = hgG.Value;
            yield return Wait(0.6f);
            float hgStart = hgG.Value;
            yield return Wait(2.0f);
            float hgHeld = hgG.Value;
            tc.Hold = false;
            yield return Wait(0.3f);
            float hgAfter = hgG.Value;
            yield return Until(() => tc.Sent.Count > hgSent0, 6f);
            var hgEv = tc.Sent.Count > hgSent0 ? tc.Sent[tc.Sent.Count - 1] : null;
            Check("touch: a still hold - nothing before it is one, a hold's worth as it becomes one, more while held, nothing more at its release (the trial run of 2026-11-06)",
                  Mathf.Abs(hgEarly - hgV0) < 1e-4f && hgStart - hgV0 >= hgWorth * 0.99f && hgHeld > hgStart + 0.005f && hgAfter - hgHeld < 0.002f
                  && hgEv != null && Json.Str(hgEv, "area") == "thigh" && Json.Str(hgEv, "kind") == "hold",
                  string.Format("v0 {0:F4} 0.3s {1:F4} 0.9s {2:F4} 2.9s {3:F4} released {4:F4} worth {5:F4} event {6}", hgV0, hgEarly, hgStart, hgHeld, hgAfter, hgWorth,
                      hgEv != null ? Json.Str(hgEv, "kind") : "none"));
            hgG.Value = 0.1f;
            tc.PressForCheck("thigh", "");
            yield return Wait(1.0f);
            float hsCounted = hgG.Value;
            tc.Stroking = true;                 // (the mouse moved on: a stroke)
            yield return Wait(1.0f);
            float hsStroke = hgG.Value;
            tc.Hold = false;
            yield return Wait(0.3f);
            float hsAfter = hgG.Value;
            Check("touch: a hold moved on into a stroke goes on counting while pressed (as a stroke); nothing more at its release (the trial run 7)",
                  hsCounted > 0.1f + hgWorth * 0.99f && hsStroke > hsCounted + 0.005f && hsAfter - hsStroke < 0.002f,
                  string.Format("held 1s {0:F4} stroking 1s {1:F4} released {2:F4}", hsCounted, hsStroke, hsAfter));
            yield return Wait(2.8f);            // (the touches' event goes)
            // the real mouse (Windows' own input; the trial run 7: a real hand is never still - a few pixels made the press a stroke,
            // counted only when let go): a press on her thigh with a hand's small jitter - the gauge rises while it is held,
            // nothing more at its release
            hgG.Value = 0.1f;
            yield return Wait(0.3f);
            var rtWp = Vector3.Lerp(Bone(st.Cha, "cf_j_thigh00_L").position, Bone(st.Cha, "cf_j_leg01_L").position, 0.4f);
            var rtSp = Camera.main.WorldToScreenPoint(rtWp);
            float rt1 = -1f, rt3 = -1f, rtEnd = 0f;
            string rtPressed = "";
            bool rtStroke = false;
            yield return RealInput.Press(new Vector2(rtSp.x, Screen.height - rtSp.y), 3.5f, 2, t =>
            {
                if (rtPressed.Length == 0 && tc.Pressing.Length > 0) rtPressed = tc.Pressing;
                if (tc.Stroking) rtStroke = true;
                if (rt1 < 0f && t >= 1f) rt1 = hgG.Value;
                if (rt3 < 0f && t >= 3f) rt3 = hgG.Value;
                rtEnd = hgG.Value;
            });
            yield return Wait(0.4f);
            float rtAfter = hgG.Value;
            Check("touch (real mouse): a press with a hand's small jitter rises while it is held (as a stroke) - nothing more when let go",
                  RealInput.LastError.Length == 0 && rtPressed == "thigh" && rtStroke && rt1 > 0.1f + 0.005f && rt3 > rt1 + 0.005f && rtAfter - rtEnd < 0.002f,
                  string.Format("{0} pressed '{1}' stroke {2} 1s {3:F4} 3s {4:F4} end {5:F4} let go {6:F4}", RealInput.LastError, rtPressed, rtStroke, rt1, rt3, rtEnd, rtAfter));
            yield return Wait(2.8f);

            // 2. her crotch caressed in the standing aibu: the drag's X / Y moves as the original's HandCtrl (in -1..1, a slow pace,
            //    within its circle) - a hand's stroke keeps it near the middle and her feet on the floor (before: the box's ends,
            //    her hips swung and her feet left the floor); the aibu has the original's motion IK again (its data pins her feet to
            //    her own pivots cf_pv_*; PR #5's re-investigation)
            hgG.Value = 0f;
            string drErr = pc.Begin("aibu_00", "check", "here");
            yield return Wait(1.2f);
            h.Hands.MouseOffForCheck = true;
            tc.PressForCheck("crotch", "");
            yield return Until(() => h.Hands.Ctrl == "drag", 6f);
            h.Hands.MoveForCheck(new Vector2(3000f, 0f));
            var drA = h.Hands.XYForCheck("kokan");
            h.Hands.MoveForCheck(new Vector2(100000f, 100000f));
            var drB = h.Hands.XYForCheck("kokan");
            h.Hands.DragToForCheck(new Vector2(0.5f, 0.5f));
            float drLow = 9f, drHigh = -9f, drT0 = Time.realtimeSinceStartup, drPrev = 0f;
            Vector2 drMin = Vector2.one, drMax = Vector2.zero;
            while (Time.realtimeSinceStartup - drT0 < 3f)
            {
                yield return new WaitForEndOfFrame();
                float drT = Time.realtimeSinceStartup - drT0;
                var drAt = new Vector2(300f * Mathf.Sin(drT * 2f * Mathf.PI), 150f * Mathf.Sin(drT * 4f * Mathf.PI));
                var drWas = new Vector2(300f * Mathf.Sin(drPrev * 2f * Mathf.PI), 150f * Mathf.Sin(drPrev * 4f * Mathf.PI));
                drPrev = drT;
                h.Hands.MoveForCheck(drAt - drWas);
                var drXY = h.Hands.XYForCheck("kokan");
                drMin = Vector2.Min(drMin, drXY);
                drMax = Vector2.Max(drMax, drXY);
                var drRoot = st.Cha.transform;
                foreach (var drToe in new[] { "cf_j_toes_L", "cf_j_toes_R" })
                {
                    float drY = drRoot.InverseTransformPoint(Bone(st.Cha, drToe).position).y;
                    drLow = Mathf.Min(drLow, drY);
                    drHigh = Mathf.Max(drHigh, drY);
                }
            }
            string drState = pc.LastState;
            tc.Hold = false;
            h.Hands.MouseOffForCheck = false;
            yield return Wait(0.5f);
            Check("aibu: her crotch's drag moves as the original's (3000 px across: 0.15; never past its circle); a hand's stroke keeps it near the middle and her feet on the floor; the original's motion IK on",
                  drErr == "" && Mathf.Abs(drA.x - 0.65f) < 0.005f && Mathf.Abs(drA.y - 0.5f) < 0.005f && (drB - new Vector2(0.5f, 0.5f)).magnitude <= 0.5001f
                  && drMin.x > 0.45f && drMax.x < 0.55f && drMin.y > 0.45f && drMax.y < 0.55f && drLow > -0.01f && drHigh < 0.07f && pc.IK.On && pc.IK.Enabled,
                  string.Format("{0} 3000px {1} far {2} stroke x {3:F3}..{4:F3} y {5:F3}..{6:F3} toes {7:F3}..{8:F3} state {9} ik {10}", drErr, drA.ToString("F3"), drB.ToString("F3"),
                      drMin.x, drMax.x, drMin.y, drMax.y, drLow, drHigh, drState, pc.IK.On));
            pc.End("check");
            yield return Wait(0.5f);

            // 3. every face goes back by itself to the scene's default (faces.json return; PR #5 adopted): in the talk her climax
            //    face after 8 s, a teasing face after 12 s, an unlisted one after 20 s - to the talk's default (service_smile), which
            //    stays; in a play to the play's default by her gauge; a newer face wins over the timer; under a touch's face the
            //    timer waits, and put back after its time the face goes straight on to the default (no flash of the old one)
            tc.PackageFace();                   // (no touch reaction's face left to put back meanwhile)
            st.ApplyFace("ecstasy");
            float tfLeft = st.TemporaryLeft;
            st.EndTemporaryForCheck();
            yield return null;
            yield return null;
            string tfTalk = st.Face;
            float tfDefaultLeft = st.TemporaryLeft;
            st.ApplyFace("teasing");
            float tfTeaseLeft = st.TemporaryLeft;
            st.ApplyFace("gentle");
            float tfGentleLeft = st.TemporaryLeft;
            Check("faces: every face goes back by itself to the scene's default - her climax face after 8 s, teasing 12 s, an unlisted one 20 s, in the talk to service_smile (which stays)",
                  tfLeft > 7.5f && tfLeft <= 8.01f && tfTalk == "service_smile" && tfDefaultLeft < 0f && st.FaceScene == "talk"
                  && tfTeaseLeft > 11.5f && tfTeaseLeft <= 12.01f && tfGentleLeft > 19.5f && tfGentleLeft <= 20.01f,
                  string.Format("ecstasy {0:F2} -> {1} (timer {2:F1}) teasing {3:F2} gentle {4:F2} scene {5}", tfLeft, tfTalk, tfDefaultLeft, tfTeaseLeft, tfGentleLeft, st.FaceScene));
            string tfPlayErr = pc.Begin("aibu_00", "check", "here");
            yield return Wait(0.8f);
            tc.PackageFace();
            h.Gauge.Value = 0.6f;
            st.ApplyFace("teasing");
            st.EndTemporaryForCheck();
            yield return null;
            yield return null;
            string tfPlayHigh = st.Face;
            h.Gauge.Value = 0.1f;
            st.ApplyFace("teasing");
            st.EndTemporaryForCheck();
            yield return null;
            yield return null;
            string tfPlayLow = st.Face;
            pc.End("check");
            yield return Wait(0.5f);
            Check("faces: in a play a face goes back to the play's default by her gauge (0.6: aroused, 0.1: flushed)", tfPlayErr == "" && tfPlayHigh == "aroused" && tfPlayLow == "flushed",
                  tfPlayErr + " " + tfPlayHigh + " / " + tfPlayLow);
            tc.PackageFace();
            st.ApplyFace("ecstasy");
            st.ApplyFace("teasing");
            string tfNewer = st.Face;
            float tfNewerLeft = st.TemporaryLeft;
            st.ApplyFace("ecstasy");
            st.ApplyPreset("aroused");          // (a touch's face over it)
            st.EndTemporaryForCheck();
            yield return null;
            yield return null;
            string tfUnder = st.Face;
            st.RestoreFace("ecstasy");          // (the touch's face over: its own put back)
            string tfBack = st.Face;
            Check("faces: a newer face wins over the old one's timer; under a touch's face the timer waits; put back after its time, the scene's default at once",
                  tfNewer == "teasing" && tfNewerLeft > 11.5f && tfUnder == "preset:aroused" && tfBack == "service_smile",
                  tfNewer + " (" + tfNewerLeft.ToString("F1") + ") / " + tfUnder + " / " + tfBack);
            st.ApplyFace("service_smile");

            // 4b. (PR #46 §2) Muse's face held: past its own seconds; a label of a moment (ecstasy) goes back to it; a face of the
            //     app's with nothing to put it back comes back to it; the state tells it; the safety time ends it; so do a change
            //     of scene and another face of Muse's; in a play it stays over the gauge's default
            tc.PackageFace();
            st.MuseFace("teasing");
            st.EndTemporaryForCheck();
            yield return null;
            yield return null;
            bool mhKept = st.Face == "teasing" && st.MuseLabel == "teasing";
            st.MuseFace("ecstasy");
            bool mhMoment = st.MuseLabel == "teasing" && st.Face == "ecstasy";
            st.EndTemporaryForCheck();
            yield return null;
            yield return null;
            bool mhBack = st.Face == "teasing";
            st.ApplyPreset("aroused");
            yield return null;
            st.AgeAppFaceForCheck(5f);
            yield return null;
            yield return null;
            bool mhAppBack = st.Face == "teasing";
            var mhInfo = st.FaceInfo();
            bool mhState = Json.Str(mhInfo, "muse") == "teasing" && Json.Num(mhInfo, "muse_left", -1) > 290;
            st.AgeMuseForCheck(400f);
            yield return null;
            yield return null;
            bool mhSafety = st.MuseLabel == "" && st.Face == st.SceneDefault() && st.MuseReleasedBy == "safety";
            st.MuseFace("gentle");
            st.MuseFace("teasing");
            bool mhNext = st.MuseLabel == "teasing" && st.MuseReleasedBy == "next";
            yield return Wait(1.2f);                 // (past the boundary's grace: not a face given for the next scene)
            h.Gauge.Value = 0.6f;
            string mhPlayErr = pc.Begin("aibu_00", "check", "here");
            yield return Wait(0.8f);
            bool mhScene = st.MuseLabel == "" && st.MuseReleasedBy == "scene";
            tc.PackageFace();
            st.MuseFace("desire");
            st.EndTemporaryForCheck();
            yield return null;
            yield return null;
            bool mhPlay = st.Face == "desire" && st.MuseLabel == "desire";
            pc.End("check");
            yield return Wait(0.5f);
            Check("faces: Muse's face held - past its seconds; ecstasy goes back to it; an app face comes back to it; the state tells it; the safety time, Muse's next face and a change of scene end it; held over the play's gauge default",
                  mhKept && mhMoment && mhBack && mhAppBack && mhState && mhSafety && mhNext && mhPlayErr == "" && mhScene && mhPlay,
                  string.Format("kept {0} moment {1} back {2} app {3} state {4} safety {5} next {6} play '{7}' scene {8} in play {9}",
                                mhKept, mhMoment, mhBack, mhAppBack, Json.Write(mhInfo), mhSafety, mhNext, mhPlayErr, mhScene, mhPlay));
            st.ReleaseMuse("check");
            st.ApplyFace("service_smile");
            tc.PackageFace();

            // 5. Muse starts or changes a normal play (only on the user's wish in the chat): checked again as the block shows - aimed
            //    at this launch and character, a request not seen before, the play_serial Muse saw, the room, the panel's
            //    availability, the spot; refused, none of the block's play fields; started, play_started carries the request
            if (pc.Active) pc.End("check");
            yield return Wait(0.5f);
            pc.WhoForCheck = Bodies("male", false, false);
            var msTag = D("launch", Launch.Id, "character", h.CharacterId());
            int msS0 = pc.Serial;
            int msUnt0 = Refusals("play.start", "untargeted");
            Send(With(Line("宛先なしで始めます。"), "play", D("start", "aibu_00", "spot", "here", "request", "chk-ms-1", "basis", msS0, "location", st.Location, "pace", 1)));
            yield return Until(() => bp.Busy && bp.Text == "宛先なしで始めます。", 10f);
            yield return Wait(0.5f);
            bool msUntargeted = Refusals("play.start", "untargeted") == msUnt0 + 1 && !pc.Active && pc.Serial == msS0;
            bp.Advance(true);
            SendPkg(new Dictionary<string, object>(msTag), With(Line("始めますね。"), "play", D("start", "aibu_00", "spot", "here", "request", "chk-ms-2", "basis", msS0,
                "location", st.Location, "pace", 1)));
            yield return Until(() => bp.Busy && bp.Text == "始めますね。", 10f);
            yield return Wait(0.6f);
            var msEv = LastEvent("play_started");
            bool msStarted = pc.Active && Json.Str(pc.Current, "id") == "aibu_00" && pc.Pace == 1 && msEv != null && Json.Str(msEv, "source") == "muse"
                && Json.Str(msEv, "request") == "chk-ms-2" && (int)Json.Num(msEv, "serial") == msS0 + 1 && pc.Serial == msS0 + 1;
            string msStartedWhy = Json.Write(msEv) + " pace " + pc.Pace;
            bp.Advance(true);
            int msS1 = pc.Serial;
            int msDup0 = Refusals("play.start", "duplicate_request");
            SendPkg(new Dictionary<string, object>(msTag), With(Line("もう一度始めます。"), "play", D("start", "aibu_00", "spot", "here", "request", "chk-ms-2", "basis", msS1,
                "location", st.Location, "pace", 3)));
            yield return Until(() => bp.Busy && bp.Text == "もう一度始めます。", 10f);
            yield return Wait(0.5f);
            bool msDuplicate = Refusals("play.start", "duplicate_request") == msDup0 + 1 && pc.Serial == msS1 && pc.Pace == 1;
            bp.Advance(true);
            int msFit0 = Refusals("play.start", "spot_not_fit");
            SendPkg(new Dictionary<string, object>(msTag), With(Line("ここで変えます。"), "play", D("start", "sonyu_00", "spot", "no-such-spot", "request", "chk-ms-3", "basis", msS1,
                "location", st.Location, "pace", 3)));
            yield return Until(() => bp.Busy && bp.Text == "ここで変えます。", 10f);
            yield return Wait(0.5f);
            bool msKept = Refusals("play.start", "spot_not_fit") == msFit0 + 1 && Json.Str(pc.Current, "id") == "aibu_00" && pc.Pace == 1 && pc.Serial == msS1;
            bp.Advance(true);
            var msSpots = pc.Spots(pc.Row("sonyu_00"));
            string msSpot = msSpots.Count > 0 ? msSpots[0].Id : "here";
            SendPkg(new Dictionary<string, object>(msTag), With(Line("体位を変えましょう。"), "play", D("start", "sonyu_00", "spot", msSpot, "request", "chk-ms-4", "basis", msS1,
                "location", st.Location)));
            yield return Until(() => bp.Busy && bp.Text == "体位を変えましょう。", 10f);
            yield return Wait(0.8f);
            var msEv2 = LastEvent("play_started");
            bool msChanged = pc.Active && Json.Str(pc.Current, "id") == "sonyu_00" && msEv2 != null && Json.Str(msEv2, "request") == "chk-ms-4"
                && Json.Str(Json.Obj(msEv2, "previous"), "id") == "aibu_00" && pc.Serial == msS1 + 2 && pc.Pace == 0;
            string msChangedWhy = Json.Write(msEv2);
            bp.Advance(true);
            int msS2 = pc.Serial;
            pc.End("user");                     // (the user stopped it meanwhile)
            int msStale0 = Refusals("play.start", "stale_basis");
            SendPkg(new Dictionary<string, object>(msTag), With(Line("続けましょう。"), "play", D("start", "aibu_00", "spot", "here", "request", "chk-ms-5", "basis", msS2,
                "location", st.Location, "pace", 2)));
            yield return Until(() => bp.Busy && bp.Text == "続けましょう。", 10f);
            yield return Wait(0.5f);
            bool msStale = Refusals("play.start", "stale_basis") == msStale0 + 1 && !pc.Active;
            bp.Advance(true);
            int msOther0 = Refusals("play.start", "other_location"), msNoReq0 = Refusals("play.start", "no_request");
            SendPkg(new Dictionary<string, object>(msTag), With(Line("あちらで始めます。"), "play", D("start", "aibu_00", "request", "chk-ms-6", "basis", pc.Serial,
                "location", "no_such_room")));
            yield return Until(() => bp.Busy && bp.Text == "あちらで始めます。", 10f);
            yield return Wait(0.4f);
            bp.Advance(true);
            SendPkg(new Dictionary<string, object>(msTag), With(Line("依頼なしで始めます。"), "play", D("start", "aibu_00", "basis", pc.Serial, "location", st.Location)));
            yield return Until(() => bp.Busy && bp.Text == "依頼なしで始めます。", 10f);
            yield return Wait(0.4f);
            bool msOthers = Refusals("play.start", "other_location") == msOther0 + 1 && Refusals("play.start", "no_request") == msNoReq0 + 1 && !pc.Active;
            bp.Advance(true);
            // (GPT's review of PR #3) a start with a move to another room while a play goes on: neither is done - the play and the
            // room as they were; the move and the start in their own blocks of one package: the move, then the start on the number
            // Muse saw before the package (the package's own changes counted in)
            pc.Begin("aibu_00", "check", "here");
            yield return Wait(0.8f);
            int mvS = pc.Serial;
            string mvRoom = st.Location;
            int mvWith0 = Refusals("play.start", "with_move"), mvLoc0 = Refusals("location", "with_start");
            SendPkg(new Dictionary<string, object>(msTag), With(With(Line("トイレで続けましょう。"), "location", "home_toilet"), "play", D("start", "aibu_00", "spot", "here",
                "request", "chk-ms-7", "basis", mvS, "location", "home_toilet")));
            yield return Until(() => bp.Busy && bp.Text == "トイレで続けましょう。", 10f);
            yield return Wait(0.5f);
            bool msWithMove = Refusals("play.start", "with_move") == mvWith0 + 1 && Refusals("location", "with_start") == mvLoc0 + 1 && pc.Active
                && st.Location == mvRoom && pc.Serial == mvS;
            string msWithMoveWhy = "active " + pc.Active + " room " + st.Location + " serial " + pc.Serial + "/" + mvS + " " + Json.Write(LastEvent("block_refused"));
            bp.Advance(true);
            SendPkg(new Dictionary<string, object>(msTag), With(Line("トイレへ行きましょう。"), "location", "home_toilet"),
                With(Line("ここで始めます。"), "play", D("start", "aibu_00", "spot", "here", "request", "chk-ms-8", "basis", mvS, "location", "home_toilet")));
            yield return Until(() => bp.Busy && bp.Text == "トイレへ行きましょう。" && !bp.Moving, 30f);
            yield return Wait(0.3f);
            bp.Advance(true);
            yield return Until(() => bp.Busy && bp.Text == "ここで始めます。", 10f);
            yield return Wait(0.8f);
            var mvEv = LastEvent("play_started");
            bool msSplit = pc.Active && st.Location == "home_toilet" && Json.Str(mvEv, "request") == "chk-ms-8" && pc.Serial == mvS + 2;
            string msSplitWhy = "room " + st.Location + " serial " + pc.Serial + "/" + mvS + " " + Json.Write(mvEv);
            bp.Advance(true);
            if (pc.Active) pc.End("check");
            h.MoveTo(mvRoom, "check");
            yield return Until(() => st.Location == mvRoom && !st.Moving, 30f);
            yield return Wait(0.5f);
            pc.WhoForCheck = null;
            Check("Muse's play start with a move to another room while a play goes on: neither - the play and the room as they were (with_move, the location with_start; GPT's review of PR #3)",
                  msWithMove, msWithMoveWhy);
            Check("Muse's play start after a move in its own block of the same package: the move, then the start on the number seen before the package", msSplit, msSplitWhy);

            // ---- PR #9 adopted (2026-11-06) ----
            // the motion IK reaches her bones (Studio keeps the solver's mappings at 0: it was solved and never applied - her feet
            // followed her hips): in the standing aibu with her crotch held, her ankles stay on their targets
            if (pc.Active) pc.End("check");
            yield return Wait(0.4f);
            string ikErr = pc.Begin("aibu_00", "check", "here");
            yield return Wait(1.0f);
            h.Hands.MouseOffForCheck = true;
            tc.PressForCheck("crotch", "");
            yield return Until(() => h.Hands.Ctrl == "drag", 6f);
            var ikLeg = Bone(st.Cha, "cf_j_leg03_L");
            Transform ikTgt = null;
            foreach (var t in st.Cha.GetComponentsInChildren<Transform>(true)) if (t.name == "cf_t_leg_L") { ikTgt = t; break; }
            float ikOff = 0f, ikMoved = 0f;
            Vector3 ikFirst = Vector3.zero;
            bool ikStarted = false;
            float ikT0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - ikT0 < 2f)
            {
                yield return new WaitForEndOfFrame();
                if (ikTgt == null) break;
                var lp = st.Cha.transform.InverseTransformPoint(ikLeg.position);
                if (!ikStarted) { ikFirst = lp; ikStarted = true; }
                ikMoved = Mathf.Max(ikMoved, (lp - ikFirst).magnitude);
                ikOff = Mathf.Max(ikOff, (ikLeg.position - ikTgt.position).magnitude);
            }
            float ikMap = pc.IK.LegMapping;
            // (the drag speeds the hand up as the mouse moves: HFlag.SpeedUpClickItemAibu)
            float spd0 = h.Hands.SpeedItemForCheck;
            h.Hands.MoveForCheck(new Vector2(30f, 0f));
            float spd1 = h.Hands.SpeedItemForCheck;
            tc.Hold = false;
            h.Hands.MouseOffForCheck = false;
            yield return Wait(0.5f);
            Check("motion IK reaches her bones: the solver's leg mappings 1 in a play; her crotch held, her ankle stays on its target (the user's note: her legs hung from her hips)",
                  ikErr == "" && ikTgt != null && ikMap > 0.99f && ikOff < 0.01f && ikMoved < 0.01f,
                  string.Format("{0} mapping {1:F2} off {2:F3} moved {3:F3}", ikErr, ikMap, ikOff, ikMoved));
            Check("aibu: a drag speeds the hand up as the mouse moves (30 px: +0.3, the original's SpeedUpClickItemAibu)", spd1 - spd0 > 0.29f || spd1 >= 1.49f,
                  string.Format("{0:F2} -> {1:F2}", spd0, spd1));
            pc.End("check");
            yield return Wait(0.6f);
            bool ikBack = true;
            foreach (var fbx in st.Cha.GetComponentsInChildren<RootMotion.FinalIK.FullBodyBipedIK>(true)) if (fbx.solver.leftLegMapping.weight > 0.01f) ikBack = false;
            Check("motion IK: after the play Studio's mappings are back (0)", ikBack);

            // touching while playing (the original's HHoushi / HSonyu call HandCtrl.Proc): in an insertion the user's hand goes on
            // her breast; her crotch takes none while it is in (the play's own table, fileAibuEnableMotion), before it does
            pc.WhoForCheck = Bodies("male", false, false);
            var tpSpots = pc.Spots(pc.Row("sonyu_00"));
            string tpErr = pc.Begin("sonyu_00", "check", tpSpots.Count > 0 ? tpSpots[0].Id : null);
            yield return Wait(1.0f);
            bool tpOn = h.Hands.OnNow, tpCrotchOut = h.Hands.AllowedForCheck("kokan");
            tc.PressForCheck("chest", "L");
            yield return Wait(0.4f);
            var tpHand = h.Hands.ObjectOn("muneL");
            bool tpHandOn = tpHand != null && tpHand.gameObject.activeInHierarchy;
            tc.Hold = false;
            yield return Wait(0.4f);
            pc.Condom = false;
            pc.Insert(0);
            yield return Until(() => pc.Inserted, 8f);
            yield return Wait(0.5f);
            bool tpCrotchIn = h.Hands.AllowedForCheck("kokan"), tpChestIn = h.Hands.AllowedForCheck("muneL");
            tc.PressForCheck("crotch", "");
            yield return Wait(0.4f);
            bool tpCrotchHand = h.Hands.ObjectOn("kokan") != null;
            tc.Hold = false;
            yield return Wait(0.3f);
            h.Hands.DetachAll();
            pc.End("check");
            pc.WhoForCheck = null;
            yield return Wait(0.6f);
            Check("touching while playing: in an insertion the hand goes on her breast; her crotch takes it out, not in (the original's table)",
                  tpErr == "" && tpOn && tpHandOn && tpCrotchOut && !tpCrotchIn && tpChestIn && !tpCrotchHand,
                  string.Format("{0} on {1} hand {2} crotch out {3} in {4} chest in {5} crotch hand {6}", tpErr, tpOn, tpHandOn, tpCrotchOut, tpCrotchIn, tpChestIn, tpCrotchHand));

            // her shoes: no half state - the panel's 靴 goes on <-> off; Muse's half for them is refused (no_half)
            bool shWorn = Clothes.Worn(st.Cha).Contains(7);
            string shNext = Clothes.Next(st.Cha, 7, "on");
            int shRef0 = Refusals("clothes", "no_half");
            if (shWorn) h.ChangeClothes(D("co_shoes", "half"), "muse");
            yield return Wait(0.5f);
            bool shRefused = !shWorn || Refusals("clothes", "no_half") == shRef0 + 1;
            if (shWorn) h.ChangeClothes(D("co_shoes", shNext), "user");
            yield return Wait(0.3f);
            string shState = shWorn ? Clothes.State(st.Cha, 7) : "-";
            // (GPT's post-review of #11) all half with her shoes off: the garments with a half state go half, the shoes stay off
            bool shAllHalf = false;
            string shAllWhy = "";
            if (shWorn)
            {
                int topK = System.Array.IndexOf(Clothes.Keys, "co_top");
                h.ChangeClothes(D("all", "half"), "muse");
                yield return Wait(0.5f);
                shAllHalf = Clothes.State(st.Cha, 7) == "off" && (topK < 0 || !Clothes.Worn(st.Cha).Contains(topK) || !Clothes.HasHalf(st.Cha, topK) || Clothes.State(st.Cha, topK) == "half");
                shAllWhy = Json.Write(Clothes.Info(st.Cha));
                h.ChangeClothes(D("all", "on"), "user");
                yield return Wait(0.3f);
            }
            if (shWorn) h.ChangeClothes(D("co_shoes", "on"), "user");
            Check("clothes: her shoes have no half state - the panel's 靴 takes them off in one press; Muse's half is refused (no_half)",
                  shWorn && !Clothes.HasHalf(st.Cha, 7) && shNext == "off" && shState == "off" && shRefused, "worn " + shWorn + " next " + shNext + " state " + shState + " refused " + shRefused);
            Check("clothes: all half changes only the garments with a half state - her shoes taken off stay off", shAllHalf, shAllWhy);

            // (PR #46 §3) coordinate cards: one made here from what she wears (in a folder of the check's own, removed after) is
            // listed in the catalog (id, name, hash); Muse's block wears it - Studio's outfit loading - and outfit_changed tells it
            // (source coordinate, by muse, the card); an unknown id, a card that no longer reads, and a play refuse it (nothing
            // changed); a file that is no outfit card is not listed
            var co = h.Coords;
            string cdDir = Path.Combine(co.Folder, "_check_" + DateTime.Now.Ticks);
            Directory.CreateDirectory(cdDir);
            string cdPath = Path.Combine(cdDir, "check_outfit.png");
            var cdCoord = st.Cha.nowCoordinate;
            byte[] cdPngWas = cdCoord.pngData;
            string cdNameWas = cdCoord.coordinateName;
            cdCoord.pngData = new Texture2D(8, 8).EncodeToPNG();
            cdCoord.coordinateName = "点検のコーデ";
            cdCoord.SaveFile(cdPath);
            cdCoord.pngData = cdPngWas;
            cdCoord.coordinateName = cdNameWas;
            File.WriteAllBytes(Path.Combine(cdDir, "not_a_card.png"), new byte[] { 1, 2, 3, 4 });
            co.Scan();
            h.SendCatalog();
            yield return Wait(1.0f);
            string cdId = cdDir.Substring(co.Folder.Length).TrimStart('\\', '/').Replace('\\', '/') + "/check_outfit.png";
            var cdCat = Json.Obj(Json.Parse(Http("GET", "/api/catalog", null)), "catalog");
            bool cdListed = false, cdJunk = false;
            foreach (var o in Json.Arr(cdCat, "coordinates") ?? new List<object>())
            {
                if (Json.Str(o, "id") == cdId && Json.Str(o, "name") == "点検のコーデ" && Json.Str(o, "hash", "").Length == 24) cdListed = true;
                if (Json.Str(o, "id", "").EndsWith("not_a_card.png")) cdJunk = true;
            }
            int cdWorn0 = co.Worn;
            SendPkg(D("launch", Launch.Id, "character", h.CharacterId()), With(Line("着替えますね。"), "coordinate", cdId));
            yield return Until(() => bp.Busy && bp.Text == "着替えますね。", 10f);
            yield return Wait(0.6f);
            var cdEv = LastEvent("outfit_changed");
            bool cdWore = co.Worn == cdWorn0 + 1 && Json.Str(cdEv, "source") == "coordinate" && Json.Str(cdEv, "by") == "muse"
                && Json.Str(Json.Obj(cdEv, "coordinate"), "id") == cdId;
            bp.Advance(true);
            yield return Wait(0.3f);
            int cdNo0 = Refusals("coordinate", "no_coordinate");
            SendPkg(D("launch", Launch.Id, "character", h.CharacterId()), With(Line("無いコーデです。"), "coordinate", "nope/none.png"));
            yield return Until(() => bp.Busy && bp.Text == "無いコーデです。", 10f);
            yield return Wait(0.5f);
            bool cdUnknown = Refusals("coordinate", "no_coordinate") == cdNo0 + 1 && co.Worn == cdWorn0 + 1;
            bp.Advance(true);
            yield return Wait(0.3f);
            byte[] cdGood = File.ReadAllBytes(cdPath);
            File.WriteAllBytes(cdPath, new byte[] { 9, 9, 9 });           // (listed, then no longer readable)
            string cdBad = co.Wear(cdId, "user");
            File.WriteAllBytes(cdPath, cdGood);
            string cdPlayErr = pc.Begin("aibu_00", "check", "here");
            yield return Wait(0.8f);
            string cdInPlay = co.Wear(cdId, "user");
            pc.End("check");
            yield return Wait(0.5f);
            try { Directory.Delete(cdDir, true); } catch (Exception) { }
            co.Scan();
            h.SendCatalog();
            Check("coordinates: a card in game/UserData/coordinate is listed (id, name, hash; a file that is no card is not); Muse's block wears it (outfit_changed source coordinate by muse); unknown, unreadable and in a play refused",
                  cdListed && !cdJunk && cdWore && cdUnknown && cdBad == "unreadable" && cdPlayErr == "" && cdInPlay == "in_play" && co.Worn == cdWorn0 + 1,
                  string.Format("listed {0} junk {1} wore {2} unknown {3} bad '{4}' in play '{5}' worn {6} {7}", cdListed, cdJunk, cdWore, cdUnknown, cdBad, cdInPlay,
                                co.Worn - cdWorn0, Json.Write(cdEv)));

            // a face is not carried into another scene: the talk's face becomes the play's default as a play starts; a face given
            // just before the start (a block setting her face and starting the play) stays
            tc.PackageFace();
            h.Gauge.Value = 0.1f;
            st.ApplyFace("teasing");
            yield return Wait(1.3f);
            int fbResets0 = st.SceneResets;
            pc.Begin("aibu_00", "check", "here");
            yield return null;
            yield return null;
            string fbPlay = st.Face;
            pc.End("check");
            yield return null;
            yield return null;
            string fbBack = st.Face;
            yield return Wait(1.3f);
            st.ApplyFace("desire");
            pc.Begin("aibu_00", "check", "here");
            yield return null;
            yield return null;
            string fbGiven = st.Face;
            pc.End("check");
            yield return Wait(0.5f);
            st.ApplyFace("service_smile");
            Check("faces: a face is not carried into another scene - teasing in the talk becomes the play's default (flushed) as it starts, the talk's (service_smile) as it ends; one given just before the start stays",
                  fbPlay == "flushed" && fbBack == "service_smile" && fbGiven == "desire" && st.SceneResets >= fbResets0 + 2, fbPlay + " / " + fbBack + " / " + fbGiven);

            // her look is not carried over either (the user's note): a look away left by a touch's reaction comes back to the user
            // (pattern 1) as its face is put back; a look left from before a play comes back to the user as the play ends
            st.Heroine.ChangeLookEyesPtn(2);
            st.RestoreFace("gentle");
            int gzAfterReaction = st.Cha.fileStatus.eyesLookPtn;
            st.Heroine.ChangeLookEyesPtn(2);
            pc.Begin("aibu_00", "check", "here");
            yield return Wait(0.6f);
            pc.End("check");
            yield return null;
            yield return null;
            int gzAfterPlay = st.Cha.fileStatus.eyesLookPtn;
            yield return Wait(0.4f);
            st.ApplyFace("service_smile");
            Check("faces: her look is not carried over either - after a touch's reaction and after a play her eyes are at the user again (pattern 1; the user's note)",
                  gzAfterReaction == 1 && gzAfterPlay == 1, "after the reaction " + gzAfterReaction + " after the play " + gzAfterPlay);
            // an app's own face nothing will put back (her climax's, eyes shut and not blinking) becomes the scene's default after
            // app_face_seconds - her eyes open and blinking again, at the user (the user's note: her eyes' opening too)
            tc.PackageFace();
            st.ApplyPreset("ecstasy");
            st.Heroine.ChangeLookEyesPtn(2);
            yield return null;
            string afShown = st.Face;
            st.AgeAppFaceForCheck(5f);
            yield return null;
            yield return null;
            yield return null;
            string afBack = st.Face;
            bool afBlink = st.Cha.fileStatus.eyesBlink;
            int afLook = st.Cha.fileStatus.eyesLookPtn;
            Check("faces: an app's own face nothing puts back (her climax's: eyes shut, no blinking) becomes the scene's default by itself - eyes blinking, at the user",
                  afShown == "preset:ecstasy" && afBack == "service_smile" && afBlink && afLook == 1, afShown + " -> " + afBack + " blink " + afBlink + " look " + afLook);

            // the speeds the user sets (快感度・発情の上がる速さ) and Muse told of a real change when the settings close
            float gsWas = h.GaugeSpeed.Value, asWas = h.ArousalSpeed.Value;
            h.Gauge.Value = 0.1f;
            h.GaugeSpeed.Value = 2f;
            h.Gauge.Add(0.01f, D("source", "check"));
            float gsAdded = h.Gauge.Value - 0.1f;
            float arSpeedWas = h.Mood.Values.ContainsKey("arousal") ? h.Mood.Values["arousal"] : -1f;
            h.ArousalSpeed.Value = 2f;
            h.Mood.Add("arousal", 0.01f, "touch");
            float arSpeedAdded = h.Mood.Values.ContainsKey("arousal") ? h.Mood.Values["arousal"] - arSpeedWas : -1f;
            h.GaugeSpeed.Value = gsWas;
            h.ArousalSpeed.Value = asWas;
            h.SettingsOpenedForCheck();
            h.GaugeSpeed.Value = 1.5f;
            int scBefore = EventCount("settings_changed");
            var scChanged = h.SettingsClosed();
            yield return Wait(0.6f);
            var scEv = LastEvent("settings_changed");
            h.SettingsOpenedForCheck();
            var scNone = h.SettingsClosed();             // (nothing changed meanwhile: nothing told)
            h.GaugeSpeed.Value = gsWas;
            yield return Wait(0.4f);
            Check("settings: the pleasure and arousal speeds multiply their rises (2: twice); closing the settings tells Muse only what changed (settings_changed), nothing when nothing did",
                  Mathf.Abs(gsAdded - 0.02f) < 0.0005f && (arSpeedWas < 0f || arSpeedWas > 0.98f || Mathf.Abs(arSpeedAdded - 0.02f) < 0.0005f)
                  && scChanged.Count == 1 && scChanged.ContainsKey("gauge_speed") && EventCount("settings_changed") == scBefore + 1
                  && Json.Has(Json.Obj(scEv, "changed"), "gauge_speed") && !Json.Has(Json.Obj(scEv, "changed"), "arousal_speed") && scNone.Count == 0,
                  string.Format("gauge +{0:F4} arousal +{1:F4} changed {2} event {3}", gsAdded, arSpeedAdded, scChanged.Count, Json.Write(scEv)));

            // ---- PR #12 adopted (2026-11-06) ----
            // Muse's end of a normal play: checked as a start is - aimed, a new request, the serial seen; a stale one (the user
            // changed the play meanwhile), an untargeted one and one with no play refused, the play as it was
            var meTag = D("launch", Launch.Id, "character", h.CharacterId());
            if (pc.Active) pc.End("check");
            yield return Wait(0.3f);
            pc.Begin("aibu_00", "check", "here");
            yield return Wait(0.6f);
            int meS = pc.Serial;
            pc.End("user");
            pc.Begin("aibu_00", "check", "here");          // (the user changed it meanwhile)
            yield return Wait(0.5f);
            int meStale0 = Refusals("play.end", "stale_basis");
            SendPkg(new Dictionary<string, object>(meTag), With(Line("終わりにしましょうか。"), "play", D("end", true, "request", "chk-me-1", "basis", meS)));
            yield return Until(() => bp.Busy && bp.Text == "終わりにしましょうか。", 10f);
            yield return Wait(0.5f);
            bool meStale = Refusals("play.end", "stale_basis") == meStale0 + 1 && pc.Active;
            bp.Advance(true);
            int meUnt0 = Refusals("play.end", "untargeted");
            Send(With(Line("宛先なしで終えます。"), "play", D("end", true, "request", "chk-me-2", "basis", pc.Serial)));
            yield return Until(() => bp.Busy && bp.Text == "宛先なしで終えます。", 10f);
            yield return Wait(0.5f);
            bool meUntargeted = Refusals("play.end", "untargeted") == meUnt0 + 1 && pc.Active;
            bp.Advance(true);
            int meS2 = pc.Serial;
            SendPkg(new Dictionary<string, object>(meTag), With(Line("ここで終わりにします。"), "play", D("end", true, "request", "chk-me-3", "basis", meS2)));
            yield return Until(() => bp.Busy && bp.Text == "ここで終わりにします。", 10f);
            yield return Wait(0.6f);
            var meEv = LastEvent("play_ended");
            bool meEnded = !pc.Active && Json.Str(meEv, "reason") == "muse" && Json.Str(meEv, "request") == "chk-me-3" && pc.Serial == meS2 + 1;
            var shownEv = LastEvent("package_shown");
            bp.Advance(true);
            int meNo0 = Refusals("play.end", "no_play");
            SendPkg(new Dictionary<string, object>(meTag), With(Line("もう一度終えます。"), "play", D("end", true, "request", "chk-me-4", "basis", pc.Serial)));
            yield return Until(() => bp.Busy && bp.Text == "もう一度終えます。", 10f);
            yield return Wait(0.5f);
            bool meNoPlay = Refusals("play.end", "no_play") == meNo0 + 1;
            bp.Advance(true);
            Check("Muse's play end: aimed, a new request and the serial seen - the play ends (play_ended reason muse with the request); stale, untargeted and with no play refused",
                  meStale && meUntargeted && meEnded && meNoPlay, string.Format("stale {0} untargeted {1} ended {2} no play {3} {4}", meStale, meUntargeted, meEnded, meNoPlay, Json.Write(meEv)));
            // package_shown: the bridge's taking and the first block's showing, a wait between them
            long psRecv = shownEv != null && Json.Has(shownEv, "received_ms") ? Convert.ToInt64(shownEv["received_ms"]) : 0L;
            long psWait = shownEv != null && Json.Has(shownEv, "waited_ms") ? Convert.ToInt64(shownEv["waited_ms"]) : -1L;
            Check("package_shown: a package's first block shown tells when the bridge took it and when it showed (the wait between)",
                  shownEv != null && psRecv > 0 && psWait >= 0 && psWait < 20000 && Json.Num(shownEv, "turn") > 0, Json.Write(shownEv));
            // (GPT's post-review of #15) a first block that is only her inner thought tells nothing; the block after it that shows
            // tells it, once; a package with nothing to show sends none
            int psN0 = bp.ShownToldForCheck, psEv0 = EventCount("package_shown");
            SendPkg(D(), Line("（声には出さない）"), Line("見えるほうのセリフです。"));
            yield return Until(() => bp.Busy && bp.Text == "見えるほうのセリフです。", 10f);
            yield return Wait(0.4f);
            int psN1 = bp.ShownToldForCheck, psEv1 = EventCount("package_shown");
            bp.Advance(true);
            yield return Wait(0.3f);
            SendPkg(D(), Line("（これも内心だけ）"));
            yield return Wait(2.5f);
            bool psNone = bp.ShownToldForCheck == psN1;
            if (bp.Busy) bp.Advance(true);
            yield return Wait(0.3f);
            Check("package_shown: not for a block that is only her inner thought - the next block that shows tells it once; nothing shown, none sent",
                  psN1 == psN0 + 1 && psEv1 == psEv0 + 1 && psNone, string.Format("told {0}->{1} events {2}->{3} none {4}", psN0, psN1, psEv0, psEv1, psNone));

            // Muse's change of the speeds (the user asked in the chat): set and told (settings_changed by muse with the request); out
            // of range, unknown names and the same request again refused; untargeted refused
            float msWasG = h.GaugeSpeed.Value;
            SendPkg(D("launch", Launch.Id, "character", h.CharacterId(), "settings", D("gauge_speed", 1.75, "request", "chk-set-1")), Line("少しゆっくりにしますね。"));
            yield return Until(() => bp.Busy && bp.Text == "少しゆっくりにしますね。", 10f);
            yield return Wait(0.5f);
            var setEv = LastEvent("settings_changed");
            bool setOk = Mathf.Abs(h.GaugeSpeed.Value - 1.75f) < 0.001f && Json.Str(setEv, "by") == "muse" && Json.Str(setEv, "request") == "chk-set-1"
                && Json.Has(Json.Obj(setEv, "changed"), "gauge_speed");
            bp.Advance(true);
            int setRange0 = Refusals("settings.gauge_speed", "out_of_range"), setUnknown0 = Refusals("settings.pace_speed", "unknown_setting"), setDup0 = Refusals("settings", "duplicate_request");
            SendPkg(D("launch", Launch.Id, "character", h.CharacterId(), "settings", D("gauge_speed", 5.0, "pace_speed", 2.0, "request", "chk-set-2")), Line("それは無理です。"));
            yield return Until(() => bp.Busy && bp.Text == "それは無理です。", 10f);
            yield return Wait(0.4f);
            bp.Advance(true);
            SendPkg(D("launch", Launch.Id, "character", h.CharacterId(), "settings", D("gauge_speed", 2.0, "request", "chk-set-1")), Line("もう一度です。"));
            yield return Until(() => bp.Busy && bp.Text == "もう一度です。", 10f);
            yield return Wait(0.4f);
            bp.Advance(true);
            int setUnt0 = Refusals("settings", "untargeted");
            SendPkg(D("settings", D("gauge_speed", 2.0, "request", "chk-set-3")), Line("宛先なしの設定です。"));
            yield return Until(() => bp.Busy && bp.Text == "宛先なしの設定です。", 15f);
            yield return Wait(0.4f);
            bp.Advance(true);
            bool setRefused = Refusals("settings.gauge_speed", "out_of_range") == setRange0 + 1 && Refusals("settings.pace_speed", "unknown_setting") == setUnknown0 + 1
                && Refusals("settings", "duplicate_request") == setDup0 + 1 && Refusals("settings", "untargeted") == setUnt0 + 1 && Mathf.Abs(h.GaugeSpeed.Value - 1.75f) < 0.001f;
            h.GaugeSpeed.Value = msWasG;
            Check("Muse's speeds: a change the user asked for is set and told (settings_changed by muse, the request); out of range, unknown, the same request again and untargeted refused",
                  setOk && setRefused, "set " + setOk + " refused " + setRefused + " " + Json.Write(setEv));
            // (GPT's post-review of #15) two requests while the settings window is open: both wait and both are done in order when
            // it closes, each told on its own - none lost; one on a speed the user changed there is refused (user_changed)
            float mwG = h.GaugeSpeed.Value, mwA = h.ArousalSpeed.Value;
            var mwAim = D("launch", Launch.Id, "character", h.CharacterId());
            int mwUser0 = Refusals("settings.arousal_speed", "user_changed");
            MuseStudioPlugin.SettingsShown = true;
            yield return Wait(0.3f);
            bool mwOpen = MuseStudioPlugin.SettingsShown;
            h.MuseSettings(D("gauge_speed", 1.5, "request", "chk-wait-1"), mwAim);
            h.MuseSettings(D("gauge_speed", 2.5, "request", "chk-wait-2"), mwAim);
            h.MuseSettings(D("arousal_speed", 3.0, "request", "chk-wait-3"), mwAim);
            int mwWaiting = h.SettingsWaitingForCheck;
            h.ArousalSpeed.Value = mwA + 0.5f;                   // (the user, in the window: wins over chk-wait-3)
            MuseStudioPlugin.SettingsShown = false;
            yield return Wait(0.5f);
            var mwReqs = new List<string>();
            foreach (var e in AllEvents())
                if (Json.Str(e, "type") == "settings_changed" && Json.Str(e, "by") == "muse" && Json.Str(e, "request") != null && Json.Str(e, "request").StartsWith("chk-wait-"))
                    mwReqs.Add(Json.Str(e, "request"));
            bool mwDone = mwOpen && mwWaiting == 3 && h.SettingsWaitingForCheck == 0 && Mathf.Abs(h.GaugeSpeed.Value - 2.5f) < 0.001f
                && mwReqs.Count >= 2 && mwReqs[0] == "chk-wait-1" && mwReqs[1] == "chk-wait-2"
                && Refusals("settings.arousal_speed", "user_changed") == mwUser0 + 1 && Mathf.Abs(h.ArousalSpeed.Value - (mwA + 0.5f)) < 0.001f;
            h.GaugeSpeed.Value = mwG;
            h.ArousalSpeed.Value = mwA;
            // (GPT's re-review of #41) a request in the very frame the window closes: the closing is settled first - the user's
            // value wins over the request that waited and over the new one (both user_changed), and the user's change is told as theirs
            float cfG = h.GaugeSpeed.Value;
            int cfUser0 = Refusals("settings.gauge_speed", "user_changed");
            MuseStudioPlugin.SettingsShown = true;
            yield return Wait(0.3f);
            bool cfSeen = h.SettingsOpenSeenForCheck;
            h.MuseSettings(D("gauge_speed", 1.5, "request", "chk-close-1"), mwAim);
            h.GaugeSpeed.Value = 1.25f;                         // (the user, in the window)
            MuseStudioPlugin.SettingsShown = false;
            h.MuseSettings(D("gauge_speed", 2.0, "request", "chk-close-2"), mwAim);     // (the same frame as the closing)
            yield return Wait(0.5f);
            Dictionary<string, object> cfUserEv = null;
            foreach (var e in AllEvents()) if (Json.Str(e, "type") == "settings_changed" && Json.Str(e, "by") == "user") cfUserEv = e as Dictionary<string, object>;
            object cfTo = null;
            var cfCh = Json.Obj(Json.Obj(cfUserEv, "changed"), "gauge_speed");
            if (cfCh != null) cfCh.TryGetValue("to", out cfTo);
            bool cfDone = cfSeen && Mathf.Abs(h.GaugeSpeed.Value - 1.25f) < 0.001f && Refusals("settings.gauge_speed", "user_changed") == cfUser0 + 2
                && cfTo != null && Math.Abs(Convert.ToDouble(cfTo) - 1.25) < 0.001;
            h.GaugeSpeed.Value = cfG;
            Check("Muse's speeds in the frame the settings window closes: the user's value wins over the waiting and the new request (user_changed), told as the user's",
                  cfDone, string.Format("seen {0} gauge {1:F2} user's to {2}", cfSeen, h.GaugeSpeed.Value, cfTo));
            // (GPT's re-review of #42) through the player as packages come: one the bridge took before the window closed, played
            // in the frame it closes, loses to the user's change (user_changed); one taken after the closing is done
            float rqG = h.GaugeSpeed.Value;
            int rqUser0 = Refusals("settings.gauge_speed", "user_changed");
            if (bp.Busy) bp.Advance(true);
            yield return Wait(0.3f);
            MuseStudioPlugin.SettingsShown = true;
            yield return Wait(0.3f);
            h.GaugeSpeed.Value = 1.25f;                         // (the user, in the window)
            long rqTook = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            MuseStudioPlugin.SettingsShown = false;
            turn++;
            bp.EnqueueForCheck(D("session", "check", "turn", turn, "launch", Launch.Id, "character", h.CharacterId(), "_received_ms", rqTook,
                                 "settings", D("gauge_speed", 2.0, "request", "chk-rq-1"), "blocks", new List<object> { Line("閉じる瞬間の依頼です。") }));
            yield return Until(() => bp.Busy && bp.Text == "閉じる瞬間の依頼です。", 5f);
            yield return Wait(0.3f);
            bool rqKept = Mathf.Abs(h.GaugeSpeed.Value - 1.25f) < 0.001f && Refusals("settings.gauge_speed", "user_changed") == rqUser0 + 1;
            bp.Advance(true);
            yield return Wait(0.3f);
            turn++;
            bp.EnqueueForCheck(D("session", "check", "turn", turn, "launch", Launch.Id, "character", h.CharacterId(),
                                 "_received_ms", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
                                 "settings", D("gauge_speed", 2.0, "request", "chk-rq-2"), "blocks", new List<object> { Line("閉じたあとの依頼です。") }));
            yield return Until(() => bp.Busy && bp.Text == "閉じたあとの依頼です。", 5f);
            yield return Wait(0.3f);
            bool rqLater = Mathf.Abs(h.GaugeSpeed.Value - 2.0f) < 0.001f;
            bp.Advance(true);
            yield return Wait(0.3f);
            h.GaugeSpeed.Value = rqG;
            Check("Muse's speeds through the player: a request taken before the settings window closed, played in its closing frame, loses to the user's change; one taken after is done",
                  rqKept && rqLater, string.Format("kept {0} later {1}", rqKept, rqLater));
            Check("Muse's speeds while the settings window is open: every request waits and is done in order when it closes (none lost); the user's change there wins",
                  mwDone, string.Format("open {0} waiting {1} requests {2} gauge {3:F2}", mwOpen, mwWaiting, string.Join(",", mwReqs.ToArray()), h.GaugeSpeed.Value));

            // ---- PR #24: the packages' delivery ----
            // a package taken: received and begun at the bridge (its stage, asked by its id); sent again by Muse: one package,
            // its stage told; given again to the app (its received lost on the way): known by its id, not played twice
            yield return Until(() => !bp.Busy, 10f);
            var dlvPkg = D("launch", Launch.Id, "character", h.CharacterId(), "id", "chk-dlv-1");
            SendPkg(dlvPkg, Line("届いたか確かめます。"));
            yield return Until(() => bp.Busy && bp.Text == "届いたか確かめます。", 10f);
            yield return Wait(1.2f);
            var dlvSt = Json.Parse(Http("GET", "/api/packages/status?id=chk-dlv-1", null)) as Dictionary<string, object>;
            bp.Advance(true);
            yield return Until(() => !bp.Busy, 10f);
            var dlvDup = Json.Parse(Http("POST", "/api/packages", dlvPkg)) as Dictionary<string, object>;
            int againBefore = h.Bridge.AgainForCheck;
            var dlvAgain = new Dictionary<string, object>(dlvPkg);
            dlvAgain["_pid"] = "chk-dlv-1";
            h.Bridge.ReceiveForCheck(dlvAgain);
            yield return Wait(1.5f);
            bool notTwice = !(bp.Busy && bp.Text == "届いたか確かめます。") && h.Bridge.AgainForCheck == againBefore + 1;
            Check("delivery: a package taken is received and begun at the bridge (its stage by its id); sent again it is one package (duplicate, started); given again it is not played twice",
                  Json.Str(dlvSt, "stage") == "started" && Json.Num(dlvSt, "deliveries") >= 1 && Json.Has(dlvDup, "duplicate") && Json.Str(dlvDup, "stage") == "started" && notTwice,
                  Json.Write(dlvSt) + " / " + Json.Write(dlvDup) + " / not twice " + notTwice);
            // the character switched: what waits here was for the one before - dropped (other_character); the new one's tagged stays
            yield return Until(() => !bp.Busy, 10f);
            int dpd0 = EventCount("package_dropped", "reason", "other_character");
            bp.EnqueueForCheck(D("session", "check", "turn", 9001, "blocks", new List<object> { Line("前のキャラへ。") }));
            bp.EnqueueForCheck(D("session", "check", "turn", 9002, "character", h.CharacterId(), "blocks", new List<object> { Line("今のキャラへ。") }));
            int dlvDropped = bp.DropPendingFor(h.CharacterId());
            yield return Until(() => bp.Busy && bp.Text == "今のキャラへ。", 10f);
            bool keptNew = bp.Busy && bp.Text == "今のキャラへ。";
            bp.Advance(true);
            yield return Wait(0.8f);
            Check("delivery: at a character switch, what waits for the one before is dropped (package_dropped other_character, by the app); the new one's tagged package plays",
                  dlvDropped == 1 && keptNew && EventCount("package_dropped", "reason", "other_character") == dpd0 + 1, "dropped " + dlvDropped + " kept " + keptNew);
            // a bridge that lost this launch (restarted on an old file): the app's next look at /health reports it again
            Http("POST", "/api/launch", D("launch", "chk-lost-launch", "phase", "app_starting"), "app");
            float lr0 = Time.realtimeSinceStartup;
            Dictionary<string, object> hl = null;
            while (Time.realtimeSinceStartup - lr0 < 15f)
            {
                yield return Wait(1f);
                hl = Json.Obj(Json.Parse(Http("GET", "/health", null)), "launch");
                if (Json.Str(hl, "id") == Launch.Id && Json.Str(hl, "phase") == "character_selected") break;
            }
            Check("delivery: a bridge that lost this launch gets it again from the app (its next /health look: the launch and its phase reported again)",
                  Json.Str(hl, "id") == Launch.Id && Json.Str(hl, "phase") == "character_selected", Json.Write(hl));

            // ---- PR #25: the moods while the app was closed settle, they do not build up ----
            // a long closed time: the bladder low, the arousal everyday (below their urges' marks: no urge from the closed
            // time alone); a short one: hardly moved; the same launch (switched away and back): as she was; an old record
            // (no launch): settled as after a closed time
            var rsCid = h.CharacterId();
            var moodsWas = new Dictionary<string, float>(h.Mood.Values);
            double rsNow = Mood.UnixNowForCheck;
            Func<double, double, double, string, Dictionary<string, object>> rsRec = (ar, bl, minutesAgo, launch) =>
            {
                var r = D("values", D("arousal", ar, "bladder", bl), "saved", rsNow - minutesAgo * 60.0);
                if (launch != null) r["launch"] = launch;
                return r;
            };
            int rsUrgeA0 = EventCount("urge", "urge", "arousal"), rsUrgeB0 = EventCount("urge", "urge", "bladder");
            h.Mood.ResumeForCheck(rsCid, rsRec(0.95, 0.95, 360, "an-old-launch"));
            float rsLA = h.Mood.Values["arousal"], rsLB = h.Mood.Values["bladder"];
            string rsHow = h.Mood.ResumedForCheck;
            h.Urges.Start("check_resume");
            yield return Wait(0.6f);
            bool rsLong = rsLA >= 0.2f && rsLA <= 0.4f && rsLB >= 0.05f && rsLB <= 0.2f && EventCount("urge", "urge", "arousal") == rsUrgeA0 && EventCount("urge", "urge", "bladder") == rsUrgeB0;
            h.Mood.ResumeForCheck(rsCid, rsRec(0.6, 0.6, 5, "an-old-launch"));
            float rsSA = h.Mood.Values["arousal"], rsSB = h.Mood.Values["bladder"];
            bool rsShort = rsSA > 0.55f && rsSA <= 0.6f && rsSB > 0.53f && rsSB <= 0.6f;
            h.Mood.ResumeForCheck(rsCid, rsRec(0.62, 0.58, 360, Launch.Id));
            bool rsSame = Mathf.Abs(h.Mood.Values["arousal"] - 0.62f) < 0.001f && Mathf.Abs(h.Mood.Values["bladder"] - 0.58f) < 0.001f && h.Mood.ResumedForCheck == "same_launch";
            h.Mood.ResumeForCheck(rsCid, rsRec(0.9, 0.9, 360, null));
            bool rsOld = h.Mood.Values["arousal"] <= 0.4f && h.Mood.Values["bladder"] <= 0.2f;
            // (GPT's post-review of #29) the app's own closed time, not the time since her save: B saved 4 hours ago under an
            // older launch, the app then open with another character and closed only 5 minutes - B hardly moves; the same record
            // after 6 hours closed in all settles; a record from before the total, after a 5-minute restart, hardly moves either
            double rsTotalWas = h.Mood.ClosedTotalForCheck, rsGapWas = h.Mood.LastGapForCheck;
            var rsB = rsRec(0.6, 0.6, 240, "an-old-launch");
            rsB["closed_total"] = 100.0;
            h.Mood.SetClosedForCheck(105.0, 5.0);
            h.Mood.ResumeForCheck(rsCid, rsB);
            float rsBA = h.Mood.Values["arousal"], rsBB = h.Mood.Values["bladder"];
            bool rsBShort = rsBA > 0.55f && rsBA <= 0.6f && rsBB > 0.53f && rsBB <= 0.6f && h.Mood.ResumedForCheck == "after_closed 5";
            h.Mood.SetClosedForCheck(460.0, 5.0);
            h.Mood.ResumeForCheck(rsCid, rsB);
            bool rsBLong = h.Mood.Values["arousal"] <= 0.4f && h.Mood.Values["bladder"] <= 0.2f && h.Mood.ResumedForCheck == "after_closed 360";
            h.Mood.SetClosedForCheck(105.0, 5.0);
            h.Mood.ResumeForCheck(rsCid, rsRec(0.6, 0.6, 360, "an-old-launch"));
            bool rsBefore = h.Mood.Values["arousal"] > 0.55f && h.Mood.ResumedForCheck == "after_closed 5";
            h.Mood.SetClosedForCheck(rsTotalWas, rsGapWas);
            foreach (var kv in moodsWas) h.Mood.Values[kv.Key] = kv.Value;
            h.Urges.Start("check_resume_back");
            Check("moods after the app was closed: a long time settles them (bladder low, arousal everyday, no urge from it); a short time hardly moves them; the same launch keeps them; an old record settles",
                  rsLong && rsShort && rsSame && rsOld,
                  string.Format("long {0:F2}/{1:F2} ({2}) short {3:F2}/{4:F2} same {5} old {6}", rsLA, rsLB, rsHow, rsSA, rsSB, rsSame, rsOld));
            // (GPT's re-review of #41) a moods file that cannot be read is never written over: a broken one is copied aside first
            // (the copy the same bytes, the file afresh); one held by another program is left as it is until it reads, then written
            string mfDir = Path.Combine(Path.GetTempPath(), "musestudio_moods_" + DateTime.Now.Ticks);
            Directory.CreateDirectory(mfDir);
            string mfBroken = Path.Combine(mfDir, "broken.json");
            string mfText = "{\"card:a.png\": {\"values\": {\"arousal\": 0.5}, \"saved\": 1}, \"card:b.png\": {\"val";
            File.WriteAllText(mfBroken, mfText);
            Mood.ForFileCheck(h, mfBroken);
            var mfAside = Directory.GetFiles(mfDir, "broken.json.unreadable-*");
            bool mfCopied = mfAside.Length == 1 && File.ReadAllText(mfAside[0]) == mfText && Json.Parse(File.ReadAllText(mfBroken)) is Dictionary<string, object>;
            // (GPT's re-review of #42) the same character chosen while the file is held: her record, not the start values, is
            // kept at the save after it reads; the app's record (closed total) is read before it is written; a .bak left by a
            // stopped save is taken back
            string mfHeldPath = Path.Combine(mfDir, "held.json");
            string mfHeldText = "{\"card:a.png\": {\"values\": {\"arousal\": 0.8, \"bladder\": 0.1}, \"saved\": 1, \"launch\": \"" + Launch.Id + "\"}, \"_app\": {\"alive\": 1, \"closed_total\": 777}}";
            File.WriteAllText(mfHeldPath, mfHeldText);
            bool mfKept, mfAfter;
            string mfWhy = "";
            using (var hold = new FileStream(mfHeldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var mfm = Mood.ForFileCheck(h, mfHeldPath);
                mfm.LoadFor("card:a.png");                    // (held: she starts with the start values)
                mfm.Save();
                hold.Close();
                mfKept = mfm.HeldForCheck && File.ReadAllText(mfHeldPath) == mfHeldText;
                mfm.Save();                                   // (it reads now: her record is read before her save)
                var mfDoc = Json.Parse(File.ReadAllText(mfHeldPath)) as Dictionary<string, object>;
                var mfA = Json.Obj(Json.Obj(mfDoc, "card:a.png"), "values");
                var mfApp = Json.Obj(mfDoc, "_app");
                mfAfter = mfA != null && Math.Abs(Json.Num(mfA, "arousal") - 0.8) < 0.001 && Math.Abs(mfm.ValueForCheck("arousal") - 0.8f) < 0.001f
                    && mfApp != null && Json.Num(mfApp, "closed_total") >= 777 && !mfm.HeldForCheck;
                mfWhy = Json.Write(mfDoc);
            }
            string mfBakPath = Path.Combine(mfDir, "bak.json");
            File.WriteAllText(mfBakPath + ".bak", "{\"card:b.png\": {\"values\": {\"arousal\": 0.6}, \"saved\": 1}}");
            Mood.ForFileCheck(h, mfBakPath);
            var mfBakDoc = File.Exists(mfBakPath) ? Json.Parse(File.ReadAllText(mfBakPath)) as Dictionary<string, object> : null;
            bool mfBak = mfBakDoc != null && mfBakDoc.ContainsKey("card:b.png") && !File.Exists(mfBakPath + ".bak");
            mfAfter = mfAfter && mfBak;
            try { Directory.Delete(mfDir, true); } catch (Exception) { }
            Check("moods file: a broken one is copied aside before anything is written (the same bytes); one held by another program is left as it was, then written keeping its records",
                  mfCopied && mfKept && mfAfter, string.Format("copied {0} kept {1} after {2} bak {3} {4}", mfCopied, mfKept, mfAfter, mfBak, mfWhy));
            Check("moods after the app was closed: the app's own closed time counts, not the time since her save (saved 4 h ago, a 5-minute restart: hardly moved; 6 h closed in all: settled; a record from before, a 5-minute restart: hardly moved)",
                  rsBShort && rsBLong && rsBefore, string.Format("B {0:F2}/{1:F2} long {2} before {3}", rsBA, rsBB, rsBLong, rsBefore));
            Check("Muse's play start: a package not aimed at this launch and character is refused (untargeted), nothing started", msUntargeted, Json.Write(LastEvent("block_refused")));
            Check("Muse's play start: aimed, a new request, the serial Muse saw, the room and the spot - it starts (play_started source muse with the request and the serial), the block's pace on it",
                  msStarted, msStartedWhy);
            Check("Muse's play start: the same request again is refused (duplicate_request) - nothing runs twice, the block's pace not taken", msDuplicate, "serial " + pc.Serial);
            Check("Muse's play change: refused (a spot that does not fit) - the play as it was and the block's pace not taken", msKept, Json.Write(LastEvent("block_refused")));
            Check("Muse's play change: to another play - play_started with the previous one, the serial past the end and the start, at pace 0", msChanged, msChangedWhy);
            Check("Muse's play start: after the user stopped the play, a start on the old number is refused (stale_basis) - no late start", msStale, Json.Write(LastEvent("block_refused")));
            Check("Muse's play start: another room named (other_location) or no request (no_request) is refused", msOthers, Json.Write(LastEvent("block_refused")));

            // her clothes: the panel's 服 (the user) and a block's clothes (Muse) - on / half / off, clothes_changed, the state
            var chU = h.ChangeClothes(D("co_top", "off"), "user");
            yield return Wait(2.5f);
            var clJ = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "clothes");
            bool userOff = chU.Count == 1 && Json.Str(clJ, "co_top") == "off" && Json.Str(LastEvent("clothes_changed"), "by") == "user";
            Send(With(Line("……脱ぐね。"), "clothes", D("all", "off")));
            yield return Until(() => bp.Busy && bp.Text == "……脱ぐね。", 10f);
            yield return Wait(0.8f);
            bool allOff = true;
            foreach (int k in Clothes.Worn(st.Cha)) if (Clothes.State(st.Cha, k) != "off") allOff = false;
            var accPs = st.Cha.nowCoordinate.accessory.parts;
            for (int i = 0; i < Math.Min(accPs.Length, st.Cha.fileStatus.showAccessory.Length); i++)
                if (accPs[i].type != 120 && !BathAccessories.IsHair(accPs[i]) && st.Cha.fileStatus.showAccessory[i]) allOff = false;
            Check("clothes: he takes her top off (服), a block's clothes all off (clothes_changed by muse), in the state", userOff && allOff
                  && Json.Str(LastEvent("clothes_changed"), "by") == "muse", Json.Write(clJ));
            yield return Shot("10b_clothes_off.png");
            bp.Advance(true);
            Send(With(Line("着るね。"), "clothes", D("all", "on", "co_nothing", "off")));
            yield return Until(() => bp.Busy && bp.Text == "着るね。", 10f);
            yield return Wait(0.8f);
            bool allOn = true;
            foreach (int k in Clothes.Worn(st.Cha)) if (Clothes.State(st.Cha, k) != "on") allOn = false;
            int accBackN = 0;
            for (int i = 0; i < Math.Min(accPs.Length, st.Cha.fileStatus.showAccessory.Length); i++) if (accPs[i].type != 120 && st.Cha.fileStatus.showAccessory[i]) accBackN++;
            allOn = allOn && accBackN > 0;
            Check("clothes: all on again; a slot she does not wear is refused (block_refused not_worn)", allOn && Json.Str(LastEvent("block_refused"), "field") == "clothes"
                  && Json.Str(LastEvent("block_refused"), "reason") == "not_worn", Json.Write(LastEvent("block_refused")));
            bp.Advance(true);
            yield return Wait(0.5f);

            // moods: the arousal and the urge to pee in the state, touches raise the arousal, Muse's calls, her own play
            var mood = h.Mood;
            var stMood = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "mood");
            Check("mood: the arousal and the urge to pee with their levels in the state", Json.Obj(stMood, "arousal") != null && Json.Obj(stMood, "bladder") != null
                  && Json.Str(Json.Obj(stMood, "arousal"), "level", "").Length > 0, Json.Write(stMood));
            float ar0 = mood.Values["arousal"];
            tc.Gesture("chest", "tap", 1, 0.1f);
            Check("mood: a touch on her chest raises the arousal", mood.Values["arousal"] > ar0, ar0 + " -> " + mood.Values["arousal"]);
            h.Gauge.Reset();
            mood.ForgetClimaxForCheck();       // (her climax in the play above is still in the window)
            mood.Values["arousal"] = 0.8f;
            Send(With(Line("満たされた？"), "mood", D("arousal", "satisfied")));
            yield return Until(() => bp.Busy && bp.Text == "満たされた？", 10f);
            yield return Wait(1.2f);
            Check("mood: satisfied without a climax of hers is refused (no_climax)", Json.Str(LastEvent("block_refused"), "reason") == "no_climax" && mood.Values["arousal"] >= 0.79f,
                  Json.Write(LastEvent("block_refused")));
            bp.Advance(true);
            h.Gauge.Value = 0.999f;
            h.Gauge.Add(0.01f, D("source", "check"));
            yield return Wait(0.3f);
            Check("mood: after her climax the event says it may be called satisfying", Json.Obj(Json.Obj(LastEvent("climax"), "mood"), "arousal") != null
                  && Json.Obj(Json.Obj(LastEvent("climax"), "mood"), "arousal")["satisfiable"] is bool && (bool)Json.Obj(Json.Obj(LastEvent("climax"), "mood"), "arousal")["satisfiable"]);
            Send(With(Line("すっきりした。"), "mood", D("arousal", "satisfied")));
            yield return Until(() => bp.Busy && bp.Text == "すっきりした。", 10f);
            yield return Wait(0.5f);
            Check("mood: Muse's satisfied lowers the arousal (by the table's amount)", Mathf.Abs(mood.Values["arousal"] - 0.2f) < 0.02f, mood.Values["arousal"].ToString("F3"));
            bp.Advance(true);
            mood.Values["bladder"] = 0.2f;
            Send(With(Line("漏れちゃう。"), "mood", D("bladder", "leaked")));
            yield return Until(() => bp.Busy && bp.Text == "漏れちゃう。", 10f);
            yield return Wait(1.2f);
            Check("mood: leaked with a low urge is refused (too_low)", Json.Str(LastEvent("block_refused"), "reason") == "too_low", Json.Write(LastEvent("block_refused")));
            bp.Advance(true);
            mood.Values["bladder"] = 0.7f;
            var clothesNow = (byte[])st.Cha.fileStatus.clothesState.Clone();
            foreach (int k in new[] { 1, 3, 5 }) st.Cha.SetClothesState(k, 3, true);
            Send(With(Line("もう無理……。"), "mood", D("bladder", "leaked")));
            yield return Until(() => bp.Busy && bp.Text == "もう無理……。", 10f);
            var kokan = Bone(st.Cha, "cf_j_kokan");
            st.SetCamera(kokan.position + st.Cha.transform.forward * 0.9f + Vector3.up * 0.1f, kokan.position + Vector3.down * 0.25f, 40f, 0f);
            yield return Wait(0.6f);
            var pee = HLiquid.LastPee;
            Note("pee layer: " + (pee != null ? pee.gameObject.layer + " (" + LayerMask.LayerToName(pee.gameObject.layer) + ") camera sees " + ((Camera.main.cullingMask & (1 << pee.gameObject.layer)) != 0)
                 + " her layer " + st.Cha.objBodyBone.layer + " size " + pee.startSize + " speed " + pee.startSpeed + " space " + pee.simulationSpace + " scale " + pee.transform.lossyScale : "none"));
            Note("pee particle: " + (pee != null ? string.Format("playing {0} count {1} at {2} shader {3}", pee.isPlaying, pee.particleCount, pee.transform.parent.name,
                 pee.GetComponent<Renderer>() != null && pee.GetComponent<Renderer>().sharedMaterial != null ? pee.GetComponent<Renderer>().sharedMaterial.shader.name : "none") : "none"));
            if (pee != null)
            {
                var parts = new ParticleSystem.Particle[16];
                int np = pee.GetParticles(parts);
                var where = new List<string>();
                for (int i = 0; i < Math.Min(np, 4); i++) where.Add((parts[i].position - kokan.position).ToString("F2") + " size " + parts[i].GetCurrentSize(pee).ToString("F3") + " col " + parts[i].GetCurrentColor(pee));
                var rd = pee.GetComponent<Renderer>();
                Note("pee particles from her crotch: " + string.Join("; ", where.ToArray()) + " renderer " + (rd != null && rd.enabled) + " bounds " + (rd != null ? rd.bounds.ToString() : "")
                     + " tex " + (rd != null && rd.sharedMaterial != null && rd.sharedMaterial.mainTexture != null ? rd.sharedMaterial.mainTexture.name : "none") + " duration " + pee.duration + " loop " + pee.loop);
            }
            yield return Shot("18_leak.png");
            var aiL = st.Heroine.oiCharInfo.animeInfo;
            bool leakMotion = st.Leaking && aiL.group == 1 && aiL.category == 100 && aiL.no == 4, leakSound = h.Sounds.Log.Contains("khse_16");
            Check("leak: her motion while she leaks (the original has none: its standing aibu's crotch idle) and the pee's sound (the toilet peeping's)",
                  leakMotion && leakSound, aiL.group + "/" + aiL.category + "/" + aiL.no + " leaking " + st.Leaking + " sound " + leakSound);
            yield return Wait(0.6f);
            yield return Shot("18b_leak.png");
            var leakEv = LastEvent("mood_applied");
            Check("mood: leaked with her lower body bare shows the original's pee (mood_applied shown, on a layer Studio's camera draws)", leakEv != null && Json.Str(leakEv, "value") == "leaked"
                  && pee != null && (Camera.main.cullingMask & (1 << pee.gameObject.layer)) != 0
                  && leakEv["shown"] is bool && (bool)leakEv["shown"] && mood.Values["bladder"] < 0.001f, Json.Write(leakEv));
            bp.Advance(true);
            yield return Until(() => !st.Leaking, 12f);            // (her clothes back once she is done - PR #23 (b) 2: not while she still leaks)
            for (int k = 0; k < clothesNow.Length; k++) st.Cha.SetClothesState(k, clothesNow[k], true);
            mood.Values["bladder"] = 0.6f;
            Send(With(Line("トイレ行ってくる。"), "mood", D("bladder", "relieved")));
            yield return Until(() => bp.Busy && bp.Text == "トイレ行ってくる。", 10f);
            yield return Wait(1.2f);
            Check("mood: relieved sets the urge to 0 (mood_applied, mood_changed)", mood.Values["bladder"] < 0.001f && EventTypes().Contains("mood_applied") && EventTypes().Contains("mood_changed"),
                  mood.Values["bladder"].ToString("F3"));
            bp.Advance(true);
            h.Gauge.Reset();
            mood.Values["arousal"] = 0.9f;
            h.Gauge.Value = 0.3f;
            mood.AloneForCheck(600f);
            yield return Wait(2.5f);
            var urgeEv = LastEvent("urge");
            bool listed = false;
            foreach (var u in Json.Arr(Json.Parse(Http("GET", "/api/state", null)), "urges") ?? new List<object>()) if (Json.Str(u, "urge") == "arousal") listed = true;
            Check("urges: aroused and left alone she does nothing by herself - Muse is told (urge, rose) and the state lists it", !pc.Active && listed
                  && Json.Str(urgeEv, "urge") == "arousal" && Json.Str(urgeEv, "cause") == "rose", urgeEv == null ? "no urge event" : Json.Write(urgeEv));
            h.Urges.AgainSecondsForCheck = 2f;
            yield return Wait(3.5f);
            h.Urges.AgainSecondsForCheck = -1f;
            Check("urges: unanswered it is told again (cause again)", Json.Str(LastEvent("urge"), "cause") == "again", Json.Write(LastEvent("urge")));
            float sh0 = mood.Values["shower"];
            Send(With(Line("……ちょっと、ひとりでするね。"), "action", "masturbation"));
            yield return Until(() => pc.Active, 10f);
            yield return Wait(0.8f);
            Check("action: Muse's masturbation starts her own play; the urge is answered (urge_cleared, action_started)", pc.Active && pc.Group == "masturbation"
                  && mood.MoodPlay == "arousal" && pc.Pace >= 1 && !h.Urges.Pending("arousal") && Json.Str(LastEvent("urge_cleared"), "cause") == "answered"
                  && LastEvent("action_started") != null, (pc.Active ? Json.Str(pc.Current, "id") : "no play") + " " + mood.MoodPlay);
            yield return Wait(1.5f);
            yield return Shot("10_own_play.png");
            float shDuring = mood.Values["shower"];
            h.Gauge.Value = 0.999f;
            yield return Until(() => !pc.Active, 40f);
            Check("mood: her own play ends after her climax there", !pc.Active && mood.MoodPlay == "", pc.Active ? pc.LastState : "");
            bp.Advance(true);
            yield return Wait(0.5f);
            Check("shower wish: it builds up after a play (added when it ends, not during it)", Mathf.Approximately(shDuring, sh0) && mood.Values["shower"] > sh0 + 0.001f,
                  sh0.ToString("F4") + " / " + shDuring.ToString("F4") + " -> " + mood.Values["shower"].ToString("F4"));
            float sh1 = mood.Values["shower"];
            Send(With(Line("お掃除、終わった。"), "mood", D("shower", "worked")));
            yield return Until(() => bp.Busy && bp.Text == "お掃除、終わった。", 10f);
            yield return Wait(1.0f);
            Check("shower wish: Muse's worked (cleaning) adds to it (mood_applied)", Mathf.Abs(mood.Values["shower"] - sh1 - 0.35f) < 0.01f
                  && Json.Str(LastEvent("mood_applied"), "value") == "worked", mood.Values["shower"].ToString("F3"));
            bp.Advance(true);
            mood.Values["shower"] = 0.75f;
            yield return Wait(2.5f);
            var shU = LastEvent("urge");
            Check("shower wish: at its mark the shower urge is told (cause rose) outside its hours", h.Urges.Pending("shower") && Json.Str(shU, "urge") == "shower"
                  && Json.Str(shU, "cause") == "rose", shU == null ? "none" : Json.Write(shU));
            mood.Values["shower"] = 0.1f;
            yield return Wait(2.5f);
            Check("shower wish: below its mark (and outside its hours) the urge goes (urge_cleared fell)", !h.Urges.Pending("shower")
                  && Json.Str(LastEvent("urge_cleared"), "cause") == "fell", Json.Write(LastEvent("urge_cleared")));

            // the toilet: the bladder's urge; Muse's action toilet - she goes on her own (the camera stays), the original's toilet
            // motions at the room's seat; he follows unseen; done with him there: Muse is told, the urge relieved
            mood.Values["bladder"] = 0.6f;
            yield return Wait(2.5f);
            Check("urges: the bladder at its mark is told (urge bladder)", h.Urges.Pending("bladder") && Json.Str(LastEvent("urge"), "urge") == "bladder", Json.Write(LastEvent("urge")));
            h.Urges.Start("carried_over");
            yield return Wait(0.8f);
            Check("urges: at the start the urges holding are told (carried_over)", Json.Str(LastEvent("urge"), "cause") == "carried_over" && h.Urges.Pending("bladder"), Json.Write(LastEvent("urge")));
            h.Outing.SecondsForCheck = 20f;           // (In plays 8-10 s; the loop till 3 s before the end: room for the peeping checks)
            string camRoom = st.Location;
            Send(With(Line("トイレ、行ってくるね。"), "action", "toilet"));
            var heardT = new HashSet<string>();
            h.StartCoroutine(Listen(heardT, 60f));          // (from her start: the pee sounds at In's end)
            yield return Until(() => h.Outing.Out, 10f);
            yield return Wait(1.0f);
            bool apart = h.Presence.HerRoom == "home_toilet" && st.Location == camRoom && !h.Presence.Together;
            Check("outing: toilet - she goes to the toilet room on her own, the camera stays (outing_started)", h.Outing.Out && apart && LastEvent("outing_started") != null,
                  h.Presence.HerRoom + " cam " + st.Location);
            st.ApplyCamera("medium", true);
            yield return Wait(0.3f);
            var camNow = Camera.main.transform.position;
            Check("outing: while she is out, the camera in his room looks at his room (not at her in the toilet)",
                  Vector3.Distance(camNow, h.Rooms.OffsetOf(st.Location)) < 30f && Vector3.Distance(camNow, h.Rooms.OffsetOf("home_toilet")) > 100f, camNow.ToString());
            bp.Advance(true);
            yield return Wait(0.3f);
            // her monologue in there (she thinks she is alone): unheard while he is in his room, heard when he peeps
            turn++;
            Http("POST", "/api/packages", D("session", "check", "turn", turn, "monologue", true,
                "blocks", new List<object> { Line("……ふう。間に合った。"), Line("今日、なんだか長かったな。"), Line("もうちょっとだけ。"), Line("……よし。"), Line("戻ろうかな。") }));
            yield return Wait(2.0f);
            bool unheard = !bp.Showing;
            bool eyesAlone = st.Cha.fileStatus.eyesLookPtn == 0 && st.Cha.fileStatus.neckLookPtn == 3;
            h.MoveTo("home_toilet", "check");
            yield return Until(() => st.Location == "home_toilet" && !st.Moving, 20f);
            yield return Until(() => h.Outing.Phase == "loop", 10f);
            yield return Wait(1.5f);
            Check("outing: her monologue in there is unheard from his room and heard when he peeps", unheard && bp.Showing && bp.Monologue && bp.Text == "……ふう。間に合った。",
                  "unheard " + unheard + " now " + bp.Text);
            st.ApplyCells(new List<object> { "", "", "", "", "", "", 1, "", "", "", "" });     // (a block's face with its eyes at the camera)
            yield return Wait(0.3f);
            Check("outing: her eyes are not on him while she is alone or he peeps unseen (straight ahead, the neck by the motion; a face's eyes held too)",
                  eyesAlone && st.Cha.fileStatus.eyesLookPtn == 0 && st.Cha.fileStatus.neckLookPtn == 3, "alone " + eyesAlone + " peeping eyes "
                  + st.Cha.fileStatus.eyesLookPtn + " neck " + st.Cha.fileStatus.neckLookPtn);
            yield return FaceShot("15a_toilet_eyes_unseen.png");
            var aiT = st.Heroine.oiCharInfo.animeInfo;
            Check("outing: following her he is unseen (together false) and she is in the original's toilet loop", h.Presence.Hidden && !h.Presence.Together
                  && aiT.group == 6 && aiT.category == 175 && aiT.no == 1, aiT.group + "/" + aiT.category + "/" + aiT.no + " hidden " + h.Presence.Hidden);
            st.ApplyCamera("far", true);
            yield return Wait(0.8f);
            yield return Shot("15_toilet.png");
            // (the original's peeping sounds; in Out her shorts pulled up - on again before she is done)
            bool shortsBack = false, sawOut = false;
            float tT = Time.realtimeSinceStartup;
            while (h.Outing.Out && Time.realtimeSinceStartup - tT < 40f)
            {
                foreach (var au in UnityEngine.Object.FindObjectsOfType<AudioSource>()) if (au.isPlaying && au.clip != null) heardT.Add(au.clip.name);
                if (h.Outing.Phase == "out" || h.Outing.Phase == "returning") sawOut = true;
                if (sawOut && h.Outing.Out && st.Cha.fileStatus.clothesState[3] == 0) shortsBack = true;
                yield return null;
            }
            float dressWant = (float)Json.Num(Json.Obj(Json.Obj(h.Urges.Doc, "outings"), "toilet"), "dress_seconds", 12.0);
            Check("toilet: the original's peeping sounds (the pee at In's end), her shorts on again in Out before she is done (not out of the toilet bare) - when she has pulled them up (dress_seconds into the Out clip)",
                  heardT.Contains("khse_16") && sawOut && shortsBack && Mathf.Abs(h.Outing.DressedAt - dressWant) < 0.3f,
                  "heard " + string.Join(",", new List<string>(heardT).ToArray()) + " out " + sawOut + " shorts back " + shortsBack + " at " + h.Outing.DressedAt.ToString("F2") + " s into Out (want " + dressWant + ")");
            yield return Until(() => !h.Outing.Out, 40f);
            yield return Wait(0.8f);          // (the event reaches the bridge from the sending thread)
            var oe = LastEvent("outing_ended");
            Check("outing: done with him there - outing_ended user_there, the bladder relieved, she is with him (her eyes at him again)", !h.Outing.Out && oe != null
                  && oe["user_there"] is bool && (bool)oe["user_there"] && mood.Values["bladder"] < 0.001f && h.Presence.Together && st.Cha.fileStatus.eyesLookPtn == 1,
                  (oe == null ? "none" : Json.Write(oe)) + " eyes " + st.Cha.fileStatus.eyesLookPtn);
            bool monoGone = false;
            foreach (var e in AllEvents())
                if (Json.Str(e, "type") == "package_dropped" && Json.Str(e, "cause") == "outing_ended") monoGone = true;
            Check("outing: what she had left to say alone in there goes when she is done (package_dropped monologue, outing_ended)", monoGone && bp.MonologueLeft == 0,
                  "left " + bp.MonologueLeft);
            bp.Advance(true);
            h.MoveTo(camRoom, "check");
            yield return Until(() => st.Location == camRoom && !st.Moving, 20f);
            // the shower: real clock time (here 22:00); the action shower - alone, the original's shower motion, back
            h.Urges.ClockForCheck = DateTime.Today.AddHours(22);
            yield return Wait(1.5f);
            Check("urges: at the shower's time the urge is told (cause time)", h.Urges.Pending("shower") && Json.Str(LastEvent("urge"), "cause") == "time", Json.Write(LastEvent("urge")));
            h.Outing.SecondsForCheck = 9f;
            h.MoveTo("bathroom", "check");          // (he is there when she comes: she knows it)
            yield return Until(() => st.Location == "bathroom" && !st.Moving, 20f);
            Send(With(Line("シャワー浴びてくるね。"), "action", "shower"));
            yield return Until(() => h.Outing.Out, 10f);
            yield return Wait(1.0f);
            Check("outing: he is there when she comes - she knows it (together), her eyes and face at him", h.Presence.Together && !h.Presence.Hidden
                  && st.Cha.fileStatus.eyesLookPtn == 1 && st.Cha.fileStatus.neckLookPtn == 1, "together " + h.Presence.Together + " eyes " + st.Cha.fileStatus.eyesLookPtn
                  + " neck " + st.Cha.fileStatus.neckLookPtn + " " + h.Look.Why);
            yield return FaceShot("15c_shower_eyes_known.png");
            float faceKnown = FaceToCamera(), eyeKnown = EyeAngles();
            Check("outing: shower, she knows he is there - her face at the camera off to her side (the measure the unseen check uses: the face's aim, the eyes' angles)",
                  faceKnown < 25f, "face " + faceKnown.ToString("F0") + " deg from the camera, eyes " + eyeKnown.ToString("F1") + " deg");
            var aiS = st.Heroine.oiCharInfo.animeInfo;
            bool bare = true;
            foreach (var cs in st.Cha.fileStatus.clothesState) if (cs != 3) bare = false;
            int accOn = 0, hairOn = 0;
            var accParts = st.Cha.nowCoordinate.accessory.parts;
            for (int i = 0; i < Math.Min(accParts.Length, st.Cha.fileStatus.showAccessory.Length); i++)
                if (accParts[i].type != 120 && st.Cha.fileStatus.showAccessory[i]) { if (BathAccessories.IsHair(accParts[i])) hairOn++; else accOn++; }
            Check("outing: shower - her accessories are off too, the hair's kept (by the hair category / worn on the hair)", accOn == 0
                  && BathAccessories.IsHair(new ChaFileAccessory.PartsInfo { type = 121 }) && BathAccessories.IsHair(new ChaFileAccessory.PartsInfo { type = 122, parentKey = "a_n_hair_pony" })
                  && !BathAccessories.IsHair(new ChaFileAccessory.PartsInfo { type = 126, parentKey = "a_n_waist" }), "on " + accOn + " hair " + hairOn);
            var atS = Json.Arr(Json.Obj(Json.Obj(h.Urges.Doc, "outings"), "shower"), "at");
            Vector3 wantS = h.Rooms.OffsetOf("bathroom") + new Vector3(Convert.ToSingle(atS[0]), Convert.ToSingle(atS[1]), Convert.ToSingle(atS[2]));
            Check("shower wish: her shower sets it to 0", mood.Values["shower"] < 0.001f, mood.Values["shower"].ToString("F3"));
            Check("outing: shower - she goes alone, nude, in the original's shower motion at the washing place; the urge answered", h.Outing.Out && h.Presence.HerRoom == "bathroom"
                  && bare && aiS.group == 6 && aiS.category == 176 && !h.Urges.Pending("shower") && Vector3.Distance(st.Heroine.oiCharInfo.changeAmount.pos, wantS) < 0.01f,
                  aiS.group + "/" + aiS.category + " bare " + bare + " at " + st.Heroine.oiCharInfo.changeAmount.pos);
            // (a look: he follows her in; the camera behind her shoulder towards the shower)
            h.MoveTo("bathroom", "check");
            yield return Until(() => st.Location == "bathroom" && !st.Moving, 20f);
            Vector3 hpS = st.Heroine.oiCharInfo.changeAmount.pos;
            st.SetCamera(hpS + new Vector3(1.7f, 1.5f, -1.3f), hpS + new Vector3(-0.3f, 0.9f, 0f), 45f, 0f);
            yield return Wait(0.8f);
            yield return Shot("15b_shower.png");
            bp.Advance(true);
            yield return Until(() => !h.Outing.Out, 20f);
            yield return Wait(0.8f);
            oe = LastEvent("outing_ended");
            bool dressed = false;
            foreach (var cs in st.Cha.fileStatus.clothesState) if (cs != 3) dressed = true;
            int accBack = 0;
            for (int i = 0; i < Math.Min(accParts.Length, st.Cha.fileStatus.showAccessory.Length); i++) if (accParts[i].type != 120 && st.Cha.fileStatus.showAccessory[i]) accBack++;
            Check("outing: done with him there, dressed again, accessories back (outing_ended user_there true)", !h.Outing.Out && h.Presence.Together && dressed && accBack > 0
                  && oe != null && oe["user_there"] is bool && (bool)oe["user_there"], oe == null ? "none" : Json.Write(oe));
            yield return Wait(2.0f);
            Check("urges: the shower taken today is not told again", !h.Urges.Pending("shower"));
            // the shower unseen (the user's note of 2026-11-04: her gaze looked held at the camera, known or not): he is in his room
            // when she goes, then follows her - her eyes straight ahead (their angles to the head about 0 with the camera off to her
            // side), the neck by the motion; the known one above looks at him by design
            if (st.Location != camRoom) { h.MoveTo(camRoom, "check"); yield return Until(() => st.Location == camRoom && !st.Moving, 20f); }
            yield return Until(() => !bp.Busy, 10f);
            h.Outing.SecondsForCheck = 14f;
            Send(With(Line("もう一回、さっと流してくるね。"), "action", "shower"));
            yield return Until(() => h.Outing.Out, 10f);
            yield return Wait(0.5f);
            bp.Advance(true);
            bool showerApart = h.Presence.HerRoom == "bathroom" && st.Location == camRoom;
            h.MoveTo("bathroom", "check");
            yield return Until(() => st.Location == "bathroom" && !st.Moving, 20f);
            yield return Wait(0.5f);
            yield return FaceShot("15e_shower_eyes_unseen.png");
            float faceR = FaceToCamera(), eyeR = EyeAngles();
            // (the camera to her other side: a face held at the camera would follow it)
            var headU = st.Cha.objHeadBone.transform.position;
            st.SetCamera(headU + st.Cha.transform.forward * 0.45f - st.Cha.transform.right * 0.45f + Vector3.up * 0.05f, headU + Vector3.up * 0.05f, 30f, 0f);
            yield return Wait(1.2f);
            float faceL = FaceToCamera(), eyeL = EyeAngles();
            Check("outing: shower, he follows her unseen - not at the camera: her face by the motion (not following it from one side to the other), her eyes straight ahead",
                  showerApart && h.Presence.Hidden && st.Cha.fileStatus.eyesLookPtn == 0 && st.Cha.fileStatus.neckLookPtn == 3 && Mathf.Max(faceR, faceL) > 30f
                  && Mathf.Max(eyeR, eyeL) < 6f,
                  "apart " + showerApart + " hidden " + h.Presence.Hidden + " eyes " + st.Cha.fileStatus.eyesLookPtn + " neck " + st.Cha.fileStatus.neckLookPtn
                  + string.Format(" face {0:F0} / {1:F0} deg from the camera (right / left), eyes {2:F1} / {3:F1} deg", faceR, faceL, eyeR, eyeL));
            yield return Until(() => !h.Outing.Out, 30f);
            yield return Wait(1.0f);
            // ---- her own time in her room (Muse's request 3): the blush by her arousal; alone with her hand - he follows unseen,
            // nothing of where he is reaches Muse, her monologue goes on unheard; he calls out (a real click): user_found ----
            if (st.Location != camRoom) { h.MoveTo(camRoom, "check"); yield return Until(() => st.Location == camRoom && !st.Moving, 20f); }
            yield return Until(() => !bp.Busy, 10f);
            float arWas;
            mood.Values.TryGetValue("arousal", out arWas);
            mood.Values["arousal"] = 0.0f;
            st.BodyBlush = 0f;                     // (from no blush: her arousal was high in the checks before)
            yield return Wait(0.3f);
            mood.Values["arousal"] = 1.0f;
            yield return Wait(5f);
            float bodyUp = st.BodyBlush, shownUp = st.Cha.fileStatus.hohoAkaRate;
            st.ApplyCells(new List<object> { "", "", "", "", "", "", "", 0.0, "", "", "" });        // (an expression without a blush)
            yield return Wait(0.3f);
            float shownExpr = st.Cha.fileStatus.hohoAkaRate;
            mood.Values["arousal"] = 0.0f;
            yield return Wait(2f);
            float bodyDown = st.BodyBlush;
            Check("blush: her arousal raises her cheeks' blush slowly (the original's 頬赤), an expression without one does not take it away, it falls slower",
                  bodyUp > 0.08f && bodyUp < 0.5f && shownUp >= bodyUp - 0.02f && shownExpr >= bodyUp - 0.02f && bodyDown < bodyUp && bodyDown > bodyUp - 0.06f,
                  string.Format("up {0:F3} shown {1:F3} with a cheekless face {2:F3} down {3:F3}", bodyUp, shownUp, shownExpr, bodyDown));
            mood.Values["arousal"] = arWas;
            int lc0 = EventCount("location_changed");
            h.Outing.SecondsForCheck = 40f;
            Send(With(Line("ちょっと部屋に行ってくるね。"), "action", D("kind", "alone", "masturbation", true)));
            yield return Until(() => h.Outing.Out, 10f);
            yield return Wait(1.0f);
            var osA = LastEvent("outing_started");
            bp.Advance(true);
            // her monologue there, unheard from his room: it goes on all the same
            turn++;
            Http("POST", "/api/packages", D("session", "check", "turn", turn, "monologue", true,
                "blocks", new List<object> { Line("……だめ、我慢できない。"), Line("ん……っ。"), Line("……はぁ。") }));
            yield return Until(() => bp.Busy && bp.Monologue, 10f);
            yield return Wait(13.5f);                 // (its first line: 10 s + 0.15 s a character - stage.json monologue)
            bool monoOn = bp.Monologue && bp.Index >= 1 && !bp.Showing;
            var locNow = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "location");
            Check("alone: she goes to her room on her own (outing alone, masturbation); her monologue goes on unheard (its pace tells nothing)",
                  Json.Str(osA, "kind") == "alone" && osA["masturbation"] is bool && (bool)osA["masturbation"] && h.Presence.HerRoom == "girls_room" && monoOn,
                  Json.Write(osA) + " index " + bp.Index + " showing " + bp.Showing);
            // he follows: not a word of it to Muse
            h.MoveTo("girls_room", "check");
            yield return Until(() => st.Location == "girls_room" && !st.Moving, 20f);
            yield return Wait(2.5f);
            var stA = Json.Parse(Http("GET", "/api/state", null));
            var aiA = st.Heroine.oiCharInfo.animeInfo;
            {
                var mAt = h.Outing.MasturbationAt;
                var alonePlace = h.Outing.Place("alone");
                Vector3 soloAt = st.Heroine.oiCharInfo.changeAmount.pos;
                bool atRight = mAt != null ? Vector3.Distance(soloAt, mAt.Pos) < 0.05f : Vector3.Distance(soloAt, alonePlace.Pos) < 0.05f;
                Check("alone: her solo play at the room's desk / chair play spot when it has one, else at 「ひとりで」's place - as its marker's name says (GPT's re-review of #30)",
                      atRight && alonePlace.Name.Contains("机・椅子"), (mAt != null ? "spot " + mAt.Id : "its place") + " off " + (mAt != null ? Vector3.Distance(soloAt, mAt.Pos) : Vector3.Distance(soloAt, alonePlace.Pos)).ToString("F3") + " " + alonePlace.Name);
            }
            Check("alone: he follows unseen - the state's location stays the room she left him in, no location_changed, together false; she in the original's solo play",
                  EventCount("location_changed") == lc0 && Json.Str(Json.Obj(stA, "location"), "key") == camRoom && Json.Obj(stA, "presence") != null
                  && !(bool)Json.Obj(stA, "presence")["together"] && h.Outing.CanCall && aiA.group == 6 && h.Outing.Phase == "loop",
                  "location " + Json.Str(Json.Obj(stA, "location"), "key") + " events " + lc0 + "->" + EventCount("location_changed") + " anime " + aiA.group + "/" + aiA.category + " " + h.Outing.Phase);
            st.ApplyCamera("medium", true);
            yield return Wait(0.6f);
            yield return Shot("15c_alone_peep.png");
            // he calls out (the panel's 声をかける, a real click): the first Muse hears of him there
            yield return RealInput.Click(Btn(h.UiForCheck, "声をかける"));
            string callErr = RealInput.LastError;
            if (h.Outing.Out && callErr.Length > 0) h.Outing.Call();
            yield return Until(() => !h.Outing.Out, 5f);
            yield return Wait(1.0f);
            var uf = LastEvent("user_found");
            var lcF = LastEvent("location_changed");
            Check("alone: he calls out (a real click on 声をかける) - user_found (cause called, what she was doing), together now, where he is told (location_changed found)",
                  callErr.Length == 0 && uf != null && Json.Str(uf, "cause") == "called" && Json.Str(uf, "doing") == "masturbation" && !h.Outing.Out && h.Presence.Together
                  && Json.Str(lcF, "source") == "found", callErr + " " + Json.Write(uf));
            yield return Shot("15d_alone_found.png");
            bp.Advance(true);
            // a change of clothes (another outfit) while he stays in his room: back to him, dressed in it
            h.MoveTo(camRoom, "check");
            yield return Until(() => st.Location == camRoom && !st.Moving, 20f);
            int coordWas = st.Cha.fileStatus.coordinateType;
            int otherOutfit = coordWas == 1 ? 0 : 1;
            h.Outing.SecondsForCheck = 8f;
            Send(With(Line("着替えてくるね。"), "action", D("kind", "change", "outfit", otherOutfit)));
            yield return Until(() => h.Outing.Out, 10f);
            bp.Advance(true);
            yield return Until(() => !h.Outing.Out, 20f);
            yield return Wait(1.0f);
            bool dressedOn = true;
            foreach (var cs in st.Cha.fileStatus.clothesState) if (cs != 0) dressedOn = false;
            var oeC = LastEvent("outing_ended");
            Check("change: she changes in her room into the outfit asked for and comes back to him dressed (outing_ended, user_there false)",
                  st.Cha.fileStatus.coordinateType == otherOutfit && dressedOn && Json.Str(oeC, "kind") == "change" && oeC["user_there"] is bool && !(bool)oeC["user_there"] && h.Presence.Together,
                  "outfit " + st.Cha.fileStatus.coordinateType + " on " + dressedOn + " " + Json.Write(oeC));
            st.Heroine.SetCoordinateInfo((ChaFileDefine.CoordinateType)coordWas, true);
            for (int k = 0; k < st.Cha.fileStatus.clothesState.Length; k++) st.Cha.SetClothesState(k, 0, false);
            h.Outing.SecondsForCheck = -1f;
            yield return Wait(0.5f);
            // shower together: both to the shower room, nude, a play there
            mood.Values["shower"] = 0.5f;
            Send(With(Line("一緒に入ろ？"), "action", "shower_together"));
            yield return Until(() => pc.Active, 30f);
            yield return Wait(1.0f);
            bool herBare = true;
            foreach (var cs in st.Cha.fileStatus.clothesState) if (cs != 3) herBare = false;
            int accTogether = 0;
            var accP2 = st.Cha.nowCoordinate.accessory.parts;
            for (int i = 0; i < Math.Min(accP2.Length, st.Cha.fileStatus.showAccessory.Length); i++)
                if (accP2[i].type != 120 && !BathAccessories.IsHair(accP2[i]) && st.Cha.fileStatus.showAccessory[i]) accTogether++;
            herBare = herBare && accTogether == 0;
            Check("action: shower together - both in the shower room, nude (her accessories off but the hair's), a play begins", pc.Active && st.Location == "bathroom" && herBare && pc.Info()["player_clothes"] as string == "nude",
                  st.Location + " " + (pc.Active ? Json.Str(pc.Current, "id") + " " + Json.Write(pc.Info()["player_clothes"]) : "no play") + " bare " + herBare);
            yield return Shot("16_shower_together.png");
            bp.Advance(true);
            bool resetTogether = mood.Values["shower"] < 0.001f;
            pc.SetPace(2, "check");
            yield return Wait(2.0f);
            pc.End("check");
            yield return Wait(0.5f);
            Check("shower wish: showering together resets it and adds nothing after (and counts as today's shower)", resetTogether && mood.Values["shower"] < 0.001f
                  && !h.Urges.Pending("shower"), mood.Values["shower"].ToString("F3"));
            h.Urges.ClockForCheck = DateTime.Today.AddHours(12);
            h.Outing.SecondsForCheck = -1f;
            h.MoveTo(camRoom, "check");
            yield return Until(() => st.Location == camRoom && !st.Moving, 20f);
            yield return Wait(0.5f);

            // characters: the state's character and outfit, the card list, switching (character_changed), the creation's result
            var chars = h.Chars;
            var stJ = Json.Parse(Http("GET", "/api/state", null));
            var chJ = Json.Obj(stJ, "character");
            Check("character: the state names her personality and attributes, the outfit by the original's names",
                  Json.Str(Json.Obj(chJ, "personality"), "name", "").Length > 0 && Json.Obj(chJ, "attributes") != null
                  && Json.Str(Json.Obj(Json.Obj(stJ, "outfit"), "co_top"), "name", "").Length > 0, Json.Write(chJ) + " " + Json.Write(Json.Obj(stJ, "outfit")).Substring(0, 120));
            var cardList = chars.List();
            string first = chars.CardPath, other = null;
            foreach (var c in cardList) if (!string.Equals(c.Key, first, StringComparison.OrdinalIgnoreCase) && c.Key.Contains("New Jersey")) other = c.Key;
            if (other == null) foreach (var c in cardList) if (!string.Equals(c.Key, first, StringComparison.OrdinalIgnoreCase)) { other = c.Key; break; }
            Check("character: the card list (the maker's saves, the project's card folder)", cardList.Count >= 2 && other != null, cardList.Count + " cards");
            yield return Wait(2.5f);         // (the state with her card reaches the bridge)
            var apiChars = Json.Parse(Http("GET", "/api/characters", null));
            Check("character: the bridge lists the cards and marks her as the active one (name + updated)", Json.Arr(apiChars, "characters") != null
                  && Json.Arr(apiChars, "characters").Count >= 2 && Json.Str(apiChars, "active", "") == h.CharacterId(), Json.Str(apiChars, "active", "null") + " vs " + h.CharacterId());
            string name0 = st.Name;
            err = other != null ? chars.Switch(other, "user") : "no other card";
            yield return Wait(1.5f);
            string pickAfter = tc.Pick(cam.WorldToScreenPoint(Bone(st.Cha, "cf_d_hit_bust_L").position));
            Check("character: switching to another card (character_changed), touch still finds her", err == "" && chars.CardPath == other && st.Name != ""
                  && LastEvent("character_changed") != null && Json.Str(Json.Obj(LastEvent("character_changed"), "previous"), "name") == name0 && pickAfter == "chest",
                  err + " " + st.Name + " pick '" + pickAfter + "'");
            yield return Shot("13_switched.png");
            var hsw = Json.Obj(Json.Parse(Http("GET", "/health", null)), "launch");
            Check("launch: switching tells the bridge the new character (its pool follows)", Json.Str(Json.Obj(hsw, "character"), "id") == h.CharacterId()
                  && Json.Str(hsw, "phase") == "character_selected", Json.Write(hsw));
            // the registered personas: a slot set on her card - the state's character.persona, persona_changed, the bridge's list
            string cur = h.CharacterId();
            string perr = h.Personas.AddSlot(1, "試しの性格A");
            // (through the card's 性格設定: its list, a slot chosen - the user's note of 2026-11-04)
            h.UiForCheck.OpenLink(h.Chars.CardPath, "persona");
            bool linkOpen = h.UiForCheck.LinkKind == "persona";
            h.UiForCheck.ChooseLink(1);
            Check("card links: 性格設定 opens its list, a choice links the card and closes it", linkOpen && h.UiForCheck.LinkKind == "" && h.Personas.OwnPersona(cur) == 1,
                  "open " + linkOpen + " own " + h.Personas.OwnPersona(cur));
            yield return Wait(2.5f);
            var pch = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "character");
            var pz = Json.Obj(pch, "persona");
            Dictionary<string, object> apiPz = null;
            foreach (var c in Json.Arr(Json.Parse(Http("GET", "/api/characters", null)), "characters") ?? new List<object>())
                if (Json.Str(c, "id") == cur) apiPz = Json.Obj(c, "persona");
            Check("persona: her card's registered persona in the state ({no, name}, source card), persona_changed, and in the bridge's card list", perr == "" && pz != null
                  && (int)Json.Num(pz, "no") == 1 && Json.Str(pz, "name") == "試しの性格A" && Json.Str(pch, "persona_source") == "card" && LastEvent("persona_changed") != null
                  && apiPz != null && (int)Json.Num(apiPz, "no") == 1, perr + " " + Json.Write(pch));
            h.SetSlotName(1, "試しの性格B");
            yield return Wait(2.5f);
            pz = Json.Obj(Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "character"), "persona");
            Check("persona: renaming the slot keeps the number, the name follows", pz != null && (int)Json.Num(pz, "no") == 1 && Json.Str(pz, "name") == "試しの性格B", Json.Write(pz));
            h.SetCardPersona(cur, 0);
            yield return Wait(2.5f);
            pch = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "character");
            Check("persona: none - null", pch != null && pch.ContainsKey("persona") && pch["persona"] == null, Json.Write(pch));

            // the persons: unassigned she is Muse herself (person null); persons are numbered here by their own counter; the
            // user puts her card with one (person_assigned); its default persona when the card has none of its own
            h.SetCardPersona(cur, -1);
            bool selfFirst = pch.ContainsKey("person") && pch["person"] == null;
            int p1 = h.Personas.AddPerson("ニュージャージー"), p2 = h.Personas.AddPerson("別の人");
            h.UiForCheck.OpenLink(h.Chars.CardPath, "person");         // (the card's ID 設定)
            h.UiForCheck.ChooseLink(p1);
            h.SetPersonPersona(p1, 1);
            yield return Wait(2.5f);
            pch = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "character");
            var pa = LastEvent("person_assigned");
            Dictionary<string, object> apiCard = null;
            var apiAll = Json.Parse(Http("GET", "/api/characters", null));
            foreach (var c in Json.Arr(apiAll, "characters") ?? new List<object>()) if (Json.Str(c, "id") == cur) apiCard = (Dictionary<string, object>)c;
            Check("person: unassigned she is Muse herself (null); put with a person (person_assigned), her persona from the person's default (source person)",
                  selfFirst && p2 == p1 + 1 && (int)Json.Num(Json.Obj(pch, "person"), "no") == p1 && Json.Str(Json.Obj(pch, "person"), "name") == "ニュージャージー"
                  && (int)Json.Num(Json.Obj(pch, "persona"), "no") == 1 && Json.Str(pch, "persona_source") == "person"
                  && pa != null && (int)Json.Num(Json.Obj(pa, "person"), "no") == p1 && pa.ContainsKey("previous") && pa["previous"] == null,
                  Json.Write(pch) + " " + (pa == null ? "no event" : Json.Write(pa)));
            Check("person: the bridge's card list has her person and persona, and the persons", apiCard != null && (int)Json.Num(Json.Obj(apiCard, "person"), "no") == p1
                  && (int)Json.Num(Json.Obj(apiCard, "persona"), "no") == 1 && (Json.Arr(apiAll, "persons") ?? new List<object>()).Count >= 2,
                  apiCard == null ? "none" : Json.Write(apiCard));
            h.SetCardPersona(cur, 0);
            yield return Wait(2.5f);
            pch = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "character");
            Check("person: the card's own persona (none) wins over the person's", pch.ContainsKey("persona") && pch["persona"] == null, Json.Write(pch));
            h.SetCardPersona(cur, -1);
            // the relationship: Muse's values for the person on the stage; the last taken in the state; the touch stage from it
            turn++;
            Http("POST", "/api/packages", D("session", "check", "turn", turn, "relationship", D("person", p1, "affection", 80, "trust", 50),
                "blocks", new List<object> { Line("好感度つき。") }));
            yield return Until(() => bp.Busy && bp.Text == "好感度つき。", 10f);
            yield return Wait(2.5f);
            var relJ = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "relationship");
            int talkStage = (int)Json.Num(Json.Obj(h.Touch.Doc, "talk"), "stage", 2);
            Check("relationship: a package's values for her person are taken (the state's relationship) and her touch stage follows (80: girlfriend)", relJ != null
                  && (int)Json.Num(relJ, "person") == p1 && Json.Num(relJ, "affection") == 80 && Json.Num(relJ, "trust") == 50 && (int)Json.Num(relJ, "stage", -1) == 2
                  && h.Relationship.Stage(talkStage) == 2, Json.Write(relJ));
            bp.Advance(true);
            turn++;
            Http("POST", "/api/packages", D("session", "check", "turn", turn, "relationship", D("person", null, "affection", 10, "trust", 10),
                "blocks", new List<object> { Line("別の人物宛て。") }));
            yield return Until(() => bp.Busy && bp.Text == "別の人物宛て。", 10f);
            yield return Wait(1.0f);
            var rr = LastEvent("block_refused");
            Check("relationship: values for another person (here Muse herself) are refused (block_refused other_person), the last taken stays",
                  Json.Str(rr, "field") == "relationship" && Json.Str(rr, "reason") == "other_person" && h.Relationship.Affection == 80, rr == null ? "none" : Json.Write(rr));
            bp.Advance(true);
            turn++;
            Http("POST", "/api/packages", D("session", "check", "turn", turn, "relationship", D("person", p1, "affection", 20, "trust", 30),
                "blocks", new List<object> { Line("下がった。") }));
            yield return Until(() => bp.Busy && bp.Text == "下がった。", 10f);
            yield return Wait(0.3f);
            Check("relationship: 20 - first met; her face no longer turns to him (the original's free roam: friends or more)", h.Relationship.Stage(talkStage) == 0
                  && !h.Look.Turned && h.Look.Why == "relation" && st.Cha.fileStatus.neckLookPtn == 3, h.Relationship.Stage(talkStage) + " " + h.Look.Why);
            bp.Advance(true);
            // retiring a person: kept as retired, its number never issued again, its cards back to Muse herself
            h.RetirePerson(p1);
            int p3 = h.Personas.AddPerson("三人目");
            yield return Wait(2.5f);
            var stR = Json.Parse(Http("GET", "/api/state", null));
            pch = Json.Obj(stR, "character");
            pa = LastEvent("person_assigned");
            Check("person: retired - kept in the table, not reissued, her card back to Muse herself (person_assigned previous)", h.Personas.PersonOf(p1).Retired
                  && p3 == p2 + 1 && pch.ContainsKey("person") && pch["person"] == null && pa != null && (int)Json.Num(Json.Obj(pa, "previous"), "no") == p1
                  && pa.ContainsKey("person") && pa["person"] == null, Json.Write(pch));
            Check("state: her_location - where she is", Json.Str(Json.Obj(stR, "her_location"), "key") == h.Presence.HerRoom, Json.Write(Json.Obj(stR, "her_location")));

            // the settings: ConfigurationManager's window opens (the panel's 設定 / F1) and takes the mouse
            MuseStudioPlugin.OpenSettings();
            yield return Wait(1.0f);
            bool setOpen = MuseStudioPlugin.SettingsShown && MuseStudioPlugin.SettingsSearch == "MuseStudio", setBlocks = h.UiOver(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
            yield return Shot("17_settings.png");
            MuseStudioPlugin.SettingsShown = false;
            yield return Wait(0.5f);
            Check("settings: ConfigurationManager's window opens in MuseStudio on its own settings (searched) and takes the mouse while open", MuseStudioPlugin.SettingsAvailable && setOpen && setBlocks
                  && !MuseStudioPlugin.SettingsShown, "available " + MuseStudioPlugin.SettingsAvailable + " open " + setOpen);

            // the user's presence (在席 / 離席): what reaches her is what she can know of it
            var pres = h.Presence;
            pres.Toggle(false);
            yield return Wait(2.5f);
            var prJ = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "presence");
            var pce = LastEvent("presence_changed");
            Check("presence: 離席 while in her room reaches her (presence_changed, the state's user_present false)", prJ != null && prJ["user_present"] is bool
                  && !(bool)prJ["user_present"] && pce != null && Json.Str(pce, "cause") == "seen", Json.Write(prJ));
            pres.HerAt = "home_toilet";              // (as if she had gone to the toilet)
            yield return Wait(0.3f);
            pres.Toggle(true);
            yield return Wait(2.5f);
            prJ = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "presence");
            bool unseen = prJ != null && prJ["user_present"] is bool && !(bool)prJ["user_present"] && prJ["together"] is bool && !(bool)prJ["together"];
            pres.HerAt = "";
            yield return Wait(2.5f);
            prJ = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "presence");
            pce = LastEvent("presence_changed");
            Check("presence: 在席 made while apart does not reach her until she is back in his room (cause met)", unseen && prJ != null && prJ["user_present"] is bool
                  && (bool)prJ["user_present"] && Json.Str(pce, "cause") == "met", Json.Write(prJ) + " unseen " + unseen);
            // her belief (Muse's): a block's belief, the state's belief; a character switch puts it back
            Send(With(Line("……いないのかな。"), "belief", D("user_away", true)));
            yield return Until(() => bp.Busy && bp.Text == "……いないのかな。", 10f);
            yield return Wait(2.5f);
            var blJ = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "belief");
            Check("belief: a block's belief {user_away: true} is the state's belief", blJ != null && blJ["user_away"] is bool && (bool)blJ["user_away"], Json.Write(blJ));
            bp.Advance(true);
            yield return Wait(0.3f);
            // her monologue: a batch plays by itself at 10-20 s a block; coming back / Muse answering him drop the rest
            pres.Toggle(false);
            turn++;
            Http("POST", "/api/packages", D("session", "check", "turn", turn, "monologue", true,
                "blocks", new List<object> { Line("独り言その一。"), Line("独り言その二。"), Line("独り言その三。") }));
            yield return Until(() => bp.Busy && bp.Text == "独り言その一。", 10f);
            float m1 = Time.realtimeSinceStartup;
            yield return Until(() => bp.Text == "独り言その二。", 25f);
            float took = Time.realtimeSinceStartup - m1;
            Check("monologue: its blocks go on by themselves, 10-20 s a block", bp.Text == "独り言その二。" && bp.Monologue && took >= 9.5f && took <= 21f, took.ToString("F1") + " s " + bp.Text);
            turn++;
            Http("POST", "/api/packages", D("session", "check", "turn", turn, "monologue", true, "blocks", new List<object> { Line("次の独り言。"), Line("その次。") }));
            yield return Wait(2.5f);
            var pkJ = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "package");
            int leftBefore = (int)Json.Num(pkJ, "monologue_left", -1);
            pres.Toggle(true);
            yield return Wait(0.8f);
            var mde = LastEvent("package_dropped");
            Check("monologue: the state counts what is left; the user coming back drops it (package_dropped monologue, user_returned)", leftBefore == 3
                  && bp.MonologueLeft == 0 && Json.Str(mde, "reason") == "monologue" && Json.Str(mde, "cause") == "user_returned",
                  "left " + leftBefore + " now " + bp.MonologueLeft + " " + (mde == null ? "none" : Json.Write(mde)));
            bp.Advance(true);
            yield return Wait(0.3f);
            pres.Toggle(false);
            turn++;
            Http("POST", "/api/packages", D("session", "check", "turn", turn, "monologue", true, "blocks", new List<object> { Line("また独り言。"), Line("まだ続く。") }));
            yield return Until(() => bp.Busy && bp.Text == "また独り言。", 10f);
            Send(Line("会話の返事。"));
            yield return Until(() => bp.Busy && bp.Text == "会話の返事。", 6f);
            yield return Wait(0.8f);          // (the event reaches the bridge from the sending thread)
            mde = LastEvent("package_dropped");
            Check("monologue: Muse answering him cuts in at once and drops the rest (cause conversation)", bp.Text == "会話の返事。" && Json.Str(mde, "cause") == "conversation",
                  bp.Text + " " + (mde == null ? "none" : Json.Write(mde)));
            bp.Advance(true);
            pres.Toggle(true);
            yield return Wait(0.3f);
            chars.CloseForCheck(first, false);        // (the maker "closed" with the first card saved: no maker, no file written)
            yield return Wait(1.5f);
            var cc0 = LastEvent("creation_completed");
            var sum = Json.Obj(cc0, "summary");
            Check("creation: the saved card becomes the character - creation_completed with the summary and what changed",
                  cc0 != null && Json.Str(sum, "schema") == "creation-summary/1" && Json.Str(sum, "name") == name0 && Json.Str(Json.Obj(sum, "personality"), "name", "").Length > 0
                  && Json.Str(Json.Obj(Json.Obj(sum, "body"), "height"), "level", "").Length > 0 && Json.Str(Json.Obj(sum, "outfit"), "co_top", "").Length > 0
                  && (Json.Arr(cc0, "changed") ?? new List<object>()).Count > 0 && chars.CardPath == first,
                  cc0 == null ? "none" : Json.Write(cc0).Substring(0, Math.Min(400, Json.Write(cc0).Length)));
            Check("belief: a character switch (the creation's card) puts her belief back (user_away false)", !h.Presence.BeliefAway);
            var chgA = new ChaFileControl();
            chgA.CopyAll(st.Cha.chaFile);
            var chgB = new ChaFileControl();
            chgB.CopyAll(st.Cha.chaFile);
            chgB.custom.face.moleId = chgA.custom.face.moleId == 1 ? 2 : 1;
            chgB.custom.body.nipColor = new Color(0.9f, 0.2f, 0.3f, 1f);
            chgB.custom.body.underhairId = chgA.custom.body.underhairId == 1 ? 2 : 1;
            chgB.custom.face.hlUpColor = new Color(0.1f, 0.9f, 0.1f, 1f);
            var chgList = CharaSummary.Changes(chgA, chgB, false, false);
            var chgItems = new List<string>();
            foreach (var c in chgList) chgItems.Add(Json.Str(c, "item"));
            Check("creation: the changes list the mole, the nipples, the pubic hair and the highlight too (the Godot app's list)", chgItems.Contains("ほくろ")
                  && chgItems.Contains("乳首の色") && chgItems.Contains("アンダーヘア") && chgItems.Contains("ハイライト上の色"), string.Join(",", chgItems.ToArray()));
            Note("creation changed (first 6): " + (cc0 != null ? Json.Write((Json.Arr(cc0, "changed") ?? new List<object>()).GetRange(0, Math.Min(6, Json.Arr(cc0, "changed").Count))) : ""));
            chars.Creating = true;                    // (as if the maker were open)
            Send(Line("作っている間のセリフ。"));
            yield return Wait(2.5f);
            bool waited = !bp.Busy && Json.Str(Json.Parse(Http("GET", "/api/state", null)), "mode") == "creation";
            chars.Creating = false;
            yield return Until(() => bp.Busy && bp.Text == "作っている間のセリフ。", 10f);
            Check("creation: Muse's packages wait while the maker is open (mode creation), then play", waited && bp.Text == "作っている間のセリフ。", bp.Text);
            bp.Advance(true);
            yield return Wait(0.5f);

            // a block's location: only the kept rooms are places (a Studio map beside them shows broken lightmaps)
            string fromLoc = st.Location;
            Send(With(Line("ホテルへ。"), "location", "love_hotel"));
            yield return Until(() => bp.Busy && bp.Text == "ホテルへ。", 15f);
            yield return Wait(0.8f);
            var lr = LastEvent("block_refused");
            Check("block: a location not kept in the scene is refused (block_refused not_kept) and the block shows where she is", st.Location == fromLoc
                  && Json.Str(lr, "field") == "location" && Json.Str(lr, "reason") == "not_kept" && bp.Text == "ホテルへ。", st.Location + " " + Json.Write(lr));
            bp.Advance(true);
            yield return Wait(0.5f);
            Send(With(Line("わたしの部屋へ。"), "location", "girls_room"));
            yield return Until(() => st.Location == "girls_room" && bp.Text == "わたしの部屋へ。", 20f);
            Check("block: its location (a kept room) moves there first, then shows", st.Location == "girls_room" && bp.Text == "わたしの部屋へ。", st.Location);
            yield return Wait(1.0f);
            yield return Shot("04_girls_room.png");
            bp.Advance(true);
            yield return Wait(1f);
            var keys = new List<string>(st.LocationKeys());
            Check("locations: the places are the kept rooms (the menu lists them only)", keys.Count == new List<string>(h.Rooms.Keys).Count && !keys.Contains("love_hotel"),
                  string.Join(",", keys.ToArray()));
            Check("events: location_changed from the package", EventTypes().Contains("location_changed"));
            Check("sound: the door when the place changes", h.Sounds.Log.Contains("se_door1_open"));
            h.Sounds.Entered("toilet_female");
            Check("sound: a map with the original's ambience (the girls' toilet) gets its playlist", h.Sounds.EnvCount > 0, h.Sounds.EnvCount.ToString());
            h.Sounds.Entered(st.Location);

            // the rooms kept in the scene: loaded at start, instant to move between (the prototype's measures)
            var rooms = h.Rooms;
            long wsNow, privNow = Rooms.MemoryMB(out wsNow);
            Note(string.Format("rooms: {0} loaded in {1:F1} s, failed: {2}; private memory {3} MB before them, {4} MB after, {5} MB now ({6} MB working set)",
                string.Join(",", new List<string>(rooms.Keys).ToArray()), rooms.LoadSeconds, string.Join(",", rooms.Failed.ToArray()),
                rooms.MemBefore, rooms.MemAfter, privNow, wsNow));
            var modMaps = new List<string>();
            foreach (var kv in Singleton<Info>.Instance.dicMapLoadInfo) if (kv.Key > 100000 && modMaps.Count < 30) modMaps.Add(kv.Key + "=" + kv.Value.name);
            Note("studio mod maps now: " + string.Join(", ", modMaps.ToArray()) + " (of " + Singleton<Info>.Instance.dicMapLoadInfo.Count + ")");
            Check("rooms: the kept rooms all loaded at start", rooms.Failed.Count == 0 && new List<string>(rooms.Keys).Count >= 5, string.Join(",", rooms.Failed.ToArray()));
            float slowest = 0f;
            foreach (var key in new List<string>(rooms.Keys))
            {
                float tr = Time.realtimeSinceStartup;
                h.MoveTo(key, "check");
                yield return Until(() => st.Location == key && !st.Moving, 30f);
                slowest = Mathf.Max(slowest, Time.realtimeSinceStartup - tr);
                st.ApplyCamera("far", true);
                yield return Wait(1.2f);
                yield return Shot("room_" + key + ".png");
                yield return Fps("room " + key);
            }
            Check("rooms: moving between the kept rooms is instant", slowest < 1.0f, slowest.ToString("F2") + " s");

            // ---- the Muse-to-Muse match (MUSE_GUIDE 8): her creation out; a match with an opponent built from a creation (here
            // her own card: every part here); the opponent's words and face; the phases and the system's limit; Muse's play by
            // the original's male part; the declared climaxes and the result; leaving; an opponent with a part missing ----
            yield return Until(() => !bp.Busy, 15f);
            string backRoom = st.Location;
            SendPkg(D("match", D("export_creation", true)), Line("書き出すね。"));
            yield return Until(() => LastEvent("match_creation") != null, 15f);
            yield return Wait(0.5f);
            var mcr = LastEvent("match_creation");
            string creation = Json.Str(mcr, "creation", "");
            Note("match: her creation " + creation.Length + " characters (base64 of zlib), extended data left out: " + Json.Write(Json.Arr(mcr, "dropped")));
            var dropped = Json.Arr(mcr, "dropped") ?? new List<object>();
            Check("match: her creation goes out (match_creation: the card's numbers and parts without its picture; the overlays, textures, author and notes left out)",
                  creation.Length > 1000 && !dropped.Contains("moreAccessories") && !dropped.Contains("KKABMPlugin.ABMData"), Json.Write(mcr).Substring(0, Math.Min(200, Json.Write(mcr).Length)));
            var imgs = Json.Arr(mcr, "images") ?? new List<object>();
            int coreImgs = 0;
            foreach (var im in imgs) if (Json.Has(im, "core") && (bool)((Dictionary<string, object>)im)["core"]) coreImgs++;
            Note("match: her pictures go as their hashes: " + imgs.Count + " (" + coreImgs + " the face's / eyes')");
            Check("match: her creation carries her pictures as hashes only (no picture, no mark of missing)", imgs.Count > 0 && creation.Length < 60000, imgs.Count + " pictures, " + creation.Length + " characters");
            Check("match: match_creation gives this user's arousal to start a match from (the host's goes into the invitation)", Json.Has(mcr, "arousal_start")
                  && Math.Abs(Json.Num(mcr, "arousal_start", -1) - h.MatchArousalStart.Value) < 0.001, Json.Write(Json.Num(mcr, "arousal_start", -1)));
            // (this side has her card: the receiver's stock-taking finds her pictures here and puts them back - a body)
            string whole = creation;
            bp.Advance(true);
            yield return Wait(0.3f);
            h.Mood.Values["arousal"] = 0.6f;          // (her mood at home before the match)
            SendPkg(D("match", D("start", D("room", "checkroom01", "side", "host", "opponent", D("name", "対戦相手", "creation", whole),
                "limits", D("negotiation_turns", 1, "sex_turns", 8, "climax", 2, "arousal_start", 0.4)))), Line("はじめまして。"));
            yield return Until(() => h.Match.Active, 40f);
            yield return Wait(2.0f);
            var stM = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "match");
            Check("match: the receiver's stock-taking - her pictures found here by their hashes and put back - so a body is made",
                  h.Match.HasBody && h.Match.PicturesFound == imgs.Count && h.Match.Missing.Count == 0, "found " + h.Match.PicturesFound + " of " + imgs.Count + " missing "
                  + string.Join(", ", h.Match.Missing.ToArray()));
            Check("match: the match's room and two bodies - the opponent built from its creation (all parts here), match_started, the state's match",
                  h.Match.Active && st.Location == "match_room" && st.Partner != null && h.Match.HasBody && st.Partner.charInfo.fileStatus.visibleBodyAlways
                  && LastEvent("match_started") != null && Json.Str(stM, "phase") == "intro" && Json.Str(stM, "side") == "host",
                  st.Location + " body " + h.Match.HasBody + " missing " + string.Join(", ", h.Match.Missing.ToArray()) + " " + Json.Write(stM));
            yield return Shot("19_match.png");
            // the two look at each other, not at the user who watches (the user's note of 2026-11-04)
            yield return Wait(2.0f);
            var oppHead = st.Partner.charInfo.objHeadBone.transform;
            Vector3 herHead = st.Cha.objHeadBone.transform.position, herAim = st.Cha.neckLookCtrl.neckLookScript.transformAim.forward;
            float toOpp = Vector3.Angle(herAim, oppHead.position - herHead), toCam = Vector3.Angle(herAim, Camera.main.transform.position - herHead);
            Check("match: the two look at each other, not at the camera - her eyes and face at the opponent's head, its at hers (the original's patterns 1)",
                  h.Look.Why == "match" && st.Cha.eyeLookCtrl.target == oppHead && st.Cha.neckLookCtrl.target == oppHead && st.Cha.fileStatus.neckLookPtn == 1
                  && st.Partner.charInfo.eyeLookCtrl.target == st.Cha.objHeadBone.transform && st.Partner.charInfo.fileStatus.neckLookPtn == 1 && toOpp < toCam,
                  string.Format("{0}: her face {1:F0} deg from the opponent, {2:F0} from the camera; neck {3}", h.Look.Why, toOpp, toCam, st.Cha.fileStatus.neckLookPtn));
            yield return Shot("19m_match_gaze.png");
            // the original's talk motions (the user's request of 2026-11-04): each stands as its personality does; a block's pose
            // on its speaker's body (hers / the opponent's); an unknown name refused
            var ptk = st.Partner.charInfo;
            string herCtl = st.Cha.animBody.runtimeAnimatorController != null ? st.Cha.animBody.runtimeAnimatorController.name : "";
            bool stoodBoth = h.Talk.NowOf(st.Cha) == "通常立ち" && h.Talk.NowOf(ptk) == "通常立ち" && herCtl.StartsWith("cf_adv");
            bp.Advance(true);
            Send(With(Line("ふふ、楽しみ。"), "pose", "喜03"));
            yield return Until(() => bp.Busy && bp.Text == "ふふ、楽しみ。", 10f);
            yield return Wait(0.8f);
            bool herPose = h.Talk.NowOf(st.Cha) == "喜03" && st.Cha.animBody.GetCurrentAnimatorStateInfo(0).IsName("Stand_Joy_03");
            bp.Advance(true);
            SendPkg(D("speaker", "opponent"), With(Line("負けないから。"), "pose", "ベース14"));
            yield return Until(() => bp.Busy && bp.Text == "負けないから。", 10f);
            yield return Wait(0.8f);
            bool oppPose = h.Talk.NowOf(ptk) == "ベース14" && ptk.animBody.GetCurrentAnimatorStateInfo(0).IsName("Stand_14_00") && h.Talk.NowOf(st.Cha) == "喜03";
            var stP = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "match");
            yield return Shot("19n_match_talk_poses.png");
            bp.Advance(true);
            Send(With(Line("……"), "pose", "存在しない動き"));
            yield return Until(() => bp.Busy && bp.Text == "……", 10f);
            yield return Wait(0.5f);
            var rfP = LastEvent("block_refused");
            Check("match talk: the original's talk motions - each stands as its personality does (通常立ち), a block's pose on its speaker's body (hers 喜03, the opponent's ベース14), the state's match.pose / opponent.pose; an unknown one refused",
                  stoodBoth && herPose && oppPose && Json.Str(stP, "pose") == "喜03" && Json.Str(Json.Obj(stP, "opponent"), "pose") == "ベース14"
                  && Json.Str(rfP, "field") == "pose" && Json.Str(rfP, "reason") == "unknown_pose",
                  string.Format("stood {0} ({1}) hers {2} its {3} state {4}/{5} refused {6}", stoodBoth, herCtl, herPose, oppPose, Json.Str(stP, "pose"),
                      Json.Str(Json.Obj(stP, "opponent"), "pose"), Json.Str(rfP, "reason")));
            bp.Advance(true);
            Send(Line("はじめまして。"));          // (the words showing again, as the panel's checks after expect)
            yield return Until(() => bp.Busy && bp.Text == "はじめまして。", 10f);
            yield return Wait(0.5f);
            float arM;
            h.Mood.Values.TryGetValue("arousal", out arM);
            Check("match: her arousal starts at the match's value (limits.arousal_start: the host user's setting), the state's match has it", Mathf.Abs(arM - 0.4f) < 0.02f
                  && Math.Abs(Json.Num(Json.Obj(stM, "limits"), "arousal_start", -1) - 0.4) < 0.001, arM.ToString("F3") + " " + Json.Write(Json.Obj(stM, "limits")));
            // the urges are not told in a match (the moods build as ever)
            int urge0 = EventCount("urge");
            h.Mood.Values["bladder"] = 0.9f;
            yield return Wait(2.5f);
            Check("match: no urge is told in a match (the bladder past its mark: no urge event); the moods still build", EventCount("urge") == urge0,
                  "urge events " + urge0 + " -> " + EventCount("urge"));
            // the UI as one uses it (the user's wish): real clicks and keys on the game window - the menus a match has not, the
            // broadcast view's switch (not advancing the words), a click off the panel advancing them
            var mui = h.UiForCheck;
            bool menusHidden = !mui.Buttons.ContainsKey("場所") && !mui.Buttons.ContainsKey("プレイ") && !mui.Buttons.ContainsKey("服")
                && mui.Buttons.ContainsKey("対戦を抜ける") && mui.Buttons.ContainsKey("中継風") && mui.Buttons.ContainsKey("カメラを戻す");
            string textNow = bp.Text;
            yield return RealInput.Click(Btn(mui, "中継風"));
            yield return Wait(0.4f);
            bool bcOn = h.MatchBroadcast.Value, kept = bp.Busy && bp.Text == textNow;
            string inErr = RealInput.LastError;
            yield return Shot("19h_match_ui_broadcast.png");
            yield return RealInput.Click(Btn(mui, "中継風"));
            yield return Wait(0.4f);
            bool bcOff = !h.MatchBroadcast.Value;
            Check("match UI (real clicks): the panel has 対戦を抜ける / 中継風 / カメラを戻す and no menus; 中継風 turns on and off and does not advance the words",
                  menusHidden && bcOn && bcOff && kept, "menus hidden " + menusHidden + " on " + bcOn + " off " + bcOff + " kept " + kept + " " + inErr + RealInput.LastError);
            // the camera turned away, then カメラを戻す
            st.SetCamera(st.Cha.objHeadBone.transform.position + new Vector3(0f, 3f, 0.1f), st.Cha.objHeadBone.transform.position, 40f, 0f);
            yield return Wait(0.3f);
            Vector3 away = Camera.main.transform.position;
            yield return RealInput.Click(Btn(mui, "カメラを戻す"));
            yield return Wait(1.4f);
            Check("match UI (real clicks): カメラを戻す brings the two-shot back", Vector3.Distance(Camera.main.transform.position, away) > 1.0f, RealInput.LastError);
            // a click off the panel advances the words (as ever: the users only watch, the click still goes on)
            var offPanel = new Vector2(ui.PanelRight ? Screen.width * 0.74f : Screen.width * 0.2f, Screen.height * 0.32f);
            yield return RealInput.Click(offPanel);
            yield return Wait(0.5f);
            Check("match UI (real clicks): a click off the panel advances the words", !bp.Busy || bp.Text != textNow, bp.Text + " " + RealInput.LastError);
            bp.Advance(true);
            yield return Wait(0.3f);
            SendPkg(D("speaker", "opponent"), With(Line("よろしくね。"), "emotion", D("label", "joy")));
            yield return Until(() => bp.Busy && bp.Text == "よろしくね。", 10f);
            Check("match: the opponent's package (speaker opponent) shows its words under its name, a turn of the other side", bp.SpeakerName == "対戦相手"
                  && h.Match.Turns["guest"] >= 1, bp.SpeakerName + " " + Json.Write(h.Match.Info()["turns"]));
            yield return Shot("19b_match_opponent.png");
            yield return Until(() => !bp.Revealing, 3f);           // (a press while the words come out shows them all instead: the reveal first)
            int space0 = MuseStudioPlugin.SpaceSeen;
            yield return RealInput.Key(0x20);
            yield return Wait(0.4f);
            Note("match UI: Space seen by Unity " + (MuseStudioPlugin.SpaceSeen - space0) + " time(s)");
            Check("match UI (real keys): Space advances the words", !bp.Busy || bp.Text != "よろしくね。",
                  bp.Text + " " + RealInput.LastError + " typing " + Ui.Typing + " (control " + GUIUtility.keyboardControl + ") revealing " + bp.Revealing + " modal " + h.UiForCheck.Modal);
            bp.Advance(true);
            // the phases: Muse moves on to the negotiation; its limit (1 turn each here) moves on to sex by itself
            Send(With(Line("ルールを決めよう。"), "match_phase", "negotiation"));
            yield return Until(() => h.Match.Phase == "negotiation", 10f);
            bp.Advance(true);
            Send(Line("挿れる側はあなたで。"));
            yield return Until(() => bp.Busy && bp.Text == "挿れる側はあなたで。", 10f);
            bp.Advance(true);
            SendPkg(D("speaker", "opponent"), Line("いいよ。"));
            yield return Until(() => h.Match.Phase == "sex", 10f);
            yield return Wait(0.8f);          // (the event reaches the bridge from the sending thread)
            bool bySystem = false;
            foreach (var e in AllEvents()) if (Json.Str(e, "type") == "match_phase" && Json.Str(e, "phase") == "sex" && Json.Str(e, "by") == "system") bySystem = true;
            Check("match: the negotiation's limit (each side's turns) moves on to sex by itself (match_phase by system)", h.Match.Phase == "sex" && bySystem, h.Match.Phase);
            bp.Advance(true);
            // Muse's play: 正常位 with the opponent in the original's male part, then her in it (reversed)
            Send(With(Line("始めよう。"), "play", D("start", "sonyu_00", "male_part", "guest", "spot", "here")));
            yield return Until(() => pc.Active, 10f);
            yield return Wait(1.5f);
            Check("match: Muse starts a play (play.start), the opponent in the male part - its body the partner, she not reversed", pc.Active && !pc.Reversed
                  && st.Partner != null && Json.Str(pc.Current, "id") == "sonyu_00", pc.Active + " reversed " + pc.Reversed);
            Check("match: the opponent in the male part without a penis is drawn futanari (no strapon)", h.Match.Futa(st.Partner.charInfo)
                  && st.Partner.charInfo.fileStatus.visibleSonAlways && !Strapon.On(st.Partner.charInfo), "futa " + h.Match.Futa(st.Partner.charInfo) + " strapon " + Strapon.On(st.Partner.charInfo));
            yield return Shot("19c_match_play.png");
            bp.Advance(true);
            Send(With(Line("交代しよう。"), "play", D("start", "sonyu_00", "male_part", "host", "spot", "here")));
            yield return Until(() => pc.Active && pc.Reversed, 10f);
            yield return Wait(1.0f);
            Check("match: male_part host - she plays the male part (reversed), drawn futanari; the opponent's futanari of the play before undone, no strapon",
                  pc.Active && pc.Reversed && h.Match.Futa(st.Cha) && !h.Match.Futa(st.Partner.charInfo) && !Strapon.On(st.Cha) && !Strapon.On(st.Partner.charInfo),
                  "reversed " + pc.Reversed + " her futa " + h.Match.Futa(st.Cha) + " opponent futa " + h.Match.Futa(st.Partner.charInfo));
            yield return Shot("19i_match_futanari.png");
            bp.Advance(true);
            // a strapon only when the two agreed (play.start strapon true)
            Send(With(Line("合意どおり、着けるね。"), "play", D("start", "sonyu_00", "male_part", "host", "spot", "here", "strapon", true)));
            yield return Until(() => pc.Active && Strapon.On(st.Cha), 10f);
            Check("match: agreed (play.start strapon true), she wears the strapon instead", pc.Active && Strapon.On(st.Cha) && !h.Match.Futa(st.Cha),
                  "strapon " + Strapon.On(st.Cha) + " futa " + h.Match.Futa(st.Cha));
            bp.Advance(true);
            // no gauge in a match (the climaxes are each Muse's declaration): the pace raises nothing; play.finish is refused
            float gm = h.Gauge.Value;
            Send(With(Line("入れて、強くするね。"), "play", D("condom", false, "insert", true, "pace", 3, "finish", "inside")));
            yield return Until(() => pc.Pace == 3 && pc.Inserted && !pc.Climax.Running, 15f);
            yield return Wait(2.0f);
            bool finRefused = false;
            foreach (var e in AllEvents()) if (Json.Str(e, "type") == "block_refused" && Json.Str(e, "field") == "play.finish" && Json.Str(e, "reason") == "in_match") finRefused = true;
            Check("match: no gauge in a match (the pace raises nothing: the climaxes are declared); play.finish refused (in_match)", Mathf.Abs(h.Gauge.Value - gm) < 0.0001f
                  && pc.Pace == 3 && finRefused, h.Gauge.Value + " / " + gm + " pace " + pc.Pace + " refused " + finRefused);
            bp.Advance(true);
            int pf0 = 0, cl0 = 0;
            foreach (var e in AllEvents()) { if (Json.Str(e, "type") == "play_finish") pf0++; if (Json.Str(e, "type") == "climax") cl0++; }
            // the climaxes each Muse declares: hers once, the opponent's twice - the opponent at the limit (2) loses. Hers with the
            // strapon on: no finish to show (nothing to come from: the face only); the opponent's in the female part: its climax shown
            int animInMatch0 = Refusals("anim", "in_match");
            Send(With(With(Line("……っ。"), "declare", "climax"), "anim", "climax"));
            yield return Until(() => h.Match.Tally["host"] == 1, 10f);
            yield return Wait(0.8f);
            Check("talk climax: in a match anim climax is refused (in_match); the declaration counts", Refusals("anim", "in_match") == animInMatch0 + 1 && !st.Climaxing,
                  Json.Write(LastEvent("block_refused")));
            bool noFin = !pc.Climax.Running && st.Face == "preset:ecstasy";
            Check("match: her declared climax with the strapon on shows no finish (nothing to come from) - her climax face only",
                  noFin && Strapon.On(st.Cha), "running " + pc.Climax.Running + " " + pc.Climax.SeqName + " face " + st.Face);
            bp.Advance(true);
            SendPkg(D("speaker", "opponent"), With(Line("あっ……！"), "declare", "climax"));
            yield return Until(() => h.Match.Tally["guest"] == 1, 10f);
            yield return Wait(0.6f);
            bool showing = pc.Climax.Showing;
            string seqHer = pc.Climax.SeqName;
            yield return Wait(2.2f);          // (the state reaches the bridge every 2 s)
            var plNow = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "play");
            bool seqHidden = plNow != null && pc.Climax.Showing && Json.Str(plNow, "finish_sequence", null) == null;
            yield return Shot("19f_match_climax_shown.png");
            yield return Until(() => !pc.Climax.Running, 20f);
            yield return Wait(1.0f);
            int pf1 = 0, cl1 = 0;
            foreach (var e in AllEvents()) { if (Json.Str(e, "type") == "play_finish") pf1++; if (Json.Str(e, "type") == "climax") cl1++; }
            Check("match: a declared climax is shown on the declaring body (the opponent in the female part: its climax in the play), told to neither Muse (no play_finish / climax, not in the state), the loop and its pace again after",
                  showing && seqHer.StartsWith("female") && seqHidden && pf1 == pf0 && cl1 == cl0 && pc.Active && pc.Pace == 3 && !pc.Climax.Running,
                  "showing " + showing + " " + seqHer + " hidden " + seqHidden + " play_finish " + pf0 + "->" + pf1 + " climax " + cl0 + "->" + cl1 + " pace " + pc.Pace);
            bp.Advance(true);
            SendPkg(D("speaker", "opponent"), With(Line("また……っ。"), "declare", "climax"));
            yield return Until(() => h.Match.Result != null, 10f);
            yield return Wait(0.8f);
            var mres = LastEvent("match_result");
            Check("match: the declared climaxes are counted; the one at the limit loses at once (match_result, the host wins, climax_limit)", h.Match.Result != null
                  && Json.Str(h.Match.Result, "winner") == "host" && Json.Str(h.Match.Result, "reason") == "climax_limit" && h.Match.Phase == "end" && mres != null
                  && LastEvent("match_climax") != null, Json.Write(h.Match.Result));
            bp.Advance(true);
            yield return Shot("19d_match_result.png");
            // the broadcast view (each user's own, told to no one): LIVE, the score telop, the camera round the two
            pc.End("check");
            yield return Wait(0.8f);
            h.MatchBroadcast.Value = true;
            yield return Wait(2.0f);
            yield return Shot("19g_match_broadcast.png");
            h.MatchBroadcast.Value = false;
            // leaving at any time - the panel's 対戦を抜ける, a real click: back where she was, the opponent gone
            yield return RealInput.Click(Btn(mui, "対戦を抜ける"));
            string leaveErr = RealInput.LastError;
            if (h.Match.Active && leaveErr.Length > 0)
            {
                Check("match UI (real clicks): 対戦を抜ける ends the match", false, leaveErr + " (left directly instead, for the checks after)");
                h.Match.Leave("user_left");
            }
            yield return Until(() => !h.Match.Active && st.Location == backRoom && !st.Moving, 20f);
            yield return Wait(1.5f);
            float arBack;
            h.Mood.Values.TryGetValue("arousal", out arBack);
            Check("match: after it her arousal is what it was before (a visit), the urges holding are told again (cause match_ended)", Mathf.Abs(arBack - 0.6f) < 0.001f
                  && EventCount("urge", "cause", "match_ended") > 0 && LastEvent("match_left") != null && Json.Str(LastEvent("match_left"), "reason") == "user_left",
                  arBack.ToString("F3") + " urges " + EventCount("urge", "cause", "match_ended") + " " + RealInput.LastError);
            h.Mood.Values["bladder"] = 0f;
            Check("match: leaving ends it at once - back to her room, the opponent gone, match_left, the state's match null; her eyes at the camera again", !h.Match.Active && st.Location == backRoom
                  && st.Partner == null && LastEvent("match_left") != null && Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "match") == null
                  && st.Cha.eyeLookCtrl.target == Camera.main.transform, st.Location + " " + h.Look.Why);
            // ---- the match's record (the user's request of 2026-11-04): saved by itself; rlisted, played and deleted with real
            // clicks; the playback through the live presentation, the numbers as recorded, nothing to Muse ----
            string rfile = h.Replay.LastSaved;
            var rdoc = File.Exists(rfile) ? Json.Parse(File.ReadAllText(rfile, Encoding.UTF8)) as Dictionary<string, object> : null;
            int rpk = 0, rmarks = 0;
            foreach (var re in Json.Arr(rdoc, "entries") ?? new List<object>()) { if (Json.Str(re, "k") == "package") rpk++; else rmarks++; }
            var rres = Json.Obj(rdoc, "result");
            Check("replay: the match's record saved by itself - both creations and names, the limits and format, the result, how it ended, the packages in order, the system's marks",
                  rdoc != null && Json.Str(rdoc, "kind") == "metamuse_match_replay" && Json.Str(Json.Obj(rdoc, "self"), "creation", "").Length > 1000
                  && Json.Str(Json.Obj(rdoc, "opponent"), "creation", "").Length > 1000 && Json.Str(Json.Obj(rdoc, "opponent"), "name") == "対戦相手"
                  && Json.Str(rdoc, "format") == "sex" && Json.Num(Json.Obj(rdoc, "limits"), "climax") == 2 && Json.Str(rres, "winner") == "host"
                  && Json.Str(rdoc, "ended") == "user_left" && rpk >= 8 && rmarks >= rpk, Path.GetFileName(rfile) + " packages " + rpk + " marks " + rmarks);
            string nameBefore = st.Name;
            yield return Wait(1.0f);
            var evAll0 = AllEvents();
            int evBefore = evAll0.Count > 0 ? (int)Json.Num(evAll0[evAll0.Count - 1], "id") : 0;      // (the last event's number: none may come)
            yield return RealInput.Click(Btn(mui, "リプレイ"));
            yield return Wait(0.5f);
            bool rlisted = mui.Buttons.ContainsKey("再生0") && mui.Buttons.ContainsKey("削除0");
            yield return RealInput.Click(Btn(mui, "再生0"));
            string rpErr = RealInput.LastError;
            if (!h.Replay.Playing && rpErr.Length > 0) h.Replay.Play(rfile);
            yield return Until(() => h.Match.Active && h.Match.Replaying, 40f);
            yield return Wait(1.5f);
            var stRp = Json.Parse(Http("GET", "/api/state", null));
            Check("replay (real clicks): リプレイ lists the record, 再生 plays it - the match's room, both bodies from the recorded creations, the state as it was with replay.playing",
                  rlisted && rpErr.Length == 0 && h.Match.Replaying && st.Location == "match_room" && st.Partner != null && h.Match.HasBody && h.Match.SelfHasBody
                  && Json.Obj(stRp, "replay") != null && Json.Obj(stRp, "match") == null && Json.Str(Json.Obj(stRp, "location"), "key") == backRoom,
                  "rlisted " + rlisted + " " + rpErr + " " + st.Location + " body " + h.Match.HasBody + "/" + h.Match.SelfHasBody + " " + Json.Write(Json.Obj(stRp, "replay")));
            yield return Shot("19k_replay.png");
            Send(Line("リプレイの後で。"));            // (Muse's package meanwhile: kept at the bridge)
            yield return RealInput.Click(Btn(mui, "速度"));
            yield return RealInput.Click(Btn(mui, "速度"));
            float speedNow = h.Replay.Speed;
            bool sawShow = false, sawWaiting = false;
            float until = Time.realtimeSinceStartup + 120f;
            while (!h.Replay.Finished && Time.realtimeSinceStartup < until)
            {
                if (pc.Active && pc.Climax.Running) sawShow = true;
                if (bp.Busy && bp.Text == "リプレイの後で。") sawWaiting = true;
                yield return null;
            }
            yield return Wait(0.5f);
            var evAll1 = AllEvents();
            int evDuring = evAll1.Count > 0 ? (int)Json.Num(evAll1[evAll1.Count - 1], "id") : 0;
            var evNew = new List<string>();
            foreach (var en in evAll1) if ((int)Json.Num(en, "id") > evBefore) evNew.Add(Json.Str(en, "type", "") + "/" + Json.Str(en, "cause", Json.Str(en, "reason", "")));
            Check("replay: the packages through the live presentation (a declared climax shown on the declaring body), the numbers as recorded (the same result), at ×4; nothing told, Muse's package kept",
                  h.Replay.Finished && sawShow && speedNow == 4f && h.Match.Tally["guest"] == 2 && Json.Str(h.Match.Result, "winner") == "host" && evDuring == evBefore && !sawWaiting,
                  "finished " + h.Replay.Finished + " shown " + sawShow + " speed " + speedNow + " tally " + Json.Write(h.Match.Info()["tally"]) + " events " + evBefore + "->" + evDuring + " " + string.Join(", ", evNew.ToArray()));
            yield return RealInput.Click(Btn(mui, "リプレイを終える"));
            string endErr = RealInput.LastError;
            if (h.Replay.Playing && endErr.Length > 0) h.Replay.Stop();
            yield return Until(() => !h.Replay.Playing && st.Location == backRoom && !st.Moving, 30f);
            yield return Until(() => bp.Busy && bp.Text == "リプレイの後で。", 10f);
            Check("replay (real click): リプレイを終える - back to her room, her own body and name again, the opponent gone; then Muse's waiting package",
                  !h.Match.Active && !h.Replay.Playing && st.Location == backRoom && st.Partner == null && st.Name == nameBefore && bp.Text == "リプレイの後で。",
                  endErr + " " + st.Location + " " + st.Name + " / " + nameBefore + " text " + bp.Text);
            bp.Advance(true);
            yield return Wait(0.3f);
            yield return RealInput.Click(Btn(mui, "リプレイ"));
            yield return Wait(0.4f);
            yield return RealInput.Click(Btn(mui, "削除0"));
            yield return Wait(0.3f);
            bool keptOnce = File.Exists(rfile);
            yield return RealInput.Click(Btn(mui, "削除0"));
            yield return Wait(0.3f);
            Check("replay (real clicks): 削除 asks again (本当に削除), then the record goes", keptOnce && !File.Exists(rfile), "kept after one " + keptOnce + " " + RealInput.LastError);
            yield return RealInput.Click(Btn(mui, "リプレイ"));       // (閉じる)
            yield return Wait(0.3f);
            Send(With(With(Line("ここでは始めない。"), "play", D("start", "sonyu_00")), "pose", "喜03"));
            yield return Until(() => bp.Busy && bp.Text == "ここでは始めない。", 10f);
            yield return Wait(0.8f);
            var rf = LastEvent("block_refused");
            // (outside a match the match's form is not enough: Muse's normal start needs its request, basis and room - MUSE_GUIDE
            // 1.12; before it was not_in_match)
            Check("match: outside a match the match's play.start (no request) does not start a play - refused (no_request)", !pc.Active && Json.Str(rf, "field") == "play.start"
                  && Json.Str(rf, "reason") == "no_request", Json.Write(rf));
            bool poseOut = false;
            foreach (var re in AllEvents())
                if (Json.Str(re, "type") == "block_refused" && Json.Str(re, "field") == "pose" && Json.Str(re, "reason") == "not_in_match") poseOut = true;
            Check("match talk: outside a match a pose is refused (not_in_match; the stage's scenes as ever)", poseOut && h.Talk.NowOf(st.Cha) == null, "now " + h.Talk.NowOf(st.Cha));
            bp.Advance(true);
            // an opponent with a part missing (a hair this game has not): no body - all or nothing - its words only
            string broken = "";
            try
            {
                var rawC = Match.Unzlib(Convert.FromBase64String(creation));
                string bpth = Path.Combine(Path.Combine(Path.GetTempPath(), "MuseStudio_match"), "check_broken.png");
                File.WriteAllBytes(bpth, rawC);
                var bfc = new ChaFileControl();
                bfc.LoadCharaFile(bpth, 255, true, false);
                bfc.custom.hair.parts[0].id = 98765;
                MatchImages.Unknown(bfc);      // (pictures nobody here has: the face's / eyes' among them)
                using (var ms = new MemoryStream())
                {
                    bfc.SaveCharaFile(ms, false);
                    broken = Convert.ToBase64String(Match.Zlib(ms.ToArray()));
                }
                File.Delete(bpth);
            }
            catch (Exception ex) { Note("match: broken creation: " + ex.Message); }
            h.MatchKeepMood.Value = true;
            SendPkg(D("match", D("start", D("room", "checkroom02", "side", "guest", "opponent", D("name", "姿なしの客", "creation", broken), "limits", D("arousal_start", 0.5, "format", "sex")))), Line("また来たね。"));
            yield return Until(() => h.Match.Active, 40f);
            yield return Wait(1.5f);
            var mop = LastEvent("match_opponent");
            bool hairMiss = false, picMiss = false;
            foreach (var m in h.Match.Missing) { if (m.StartsWith("hair")) hairMiss = true; if (m.StartsWith("picture")) picMiss = true; }
            Check("match: an opponent with a part this game has not - or whose eyes rest on a picture left out - gets no body (all or nothing; match_opponent missing names both)",
                  h.Match.Active && !h.Match.HasBody && hairMiss && picMiss && st.Partner != null && !st.Partner.charInfo.fileStatus.visibleBodyAlways
                  && mop != null && Json.Arr(mop, "missing") != null && Json.Arr(mop, "missing").Count >= 2, string.Join(", ", h.Match.Missing.ToArray()));
            yield return Shot("19e_match_no_body.png");
            bp.Advance(true);
            h.Mood.Values["arousal"] = 0.55f;         // (built up in the match)
            h.Match.Leave("check");
            yield return Until(() => !h.Match.Active && st.Location == backRoom && !st.Moving, 20f);
            yield return Wait(0.8f);
            float arKept;
            h.Mood.Values.TryGetValue("arousal", out arKept);
            h.MatchKeepMood.Value = false;
            Check("match: with 対戦で溜まった発情を持ち帰る on, what built up in the match stays", Mathf.Abs(arKept - 0.55f) < 0.02f, arKept.ToString("F3"));
            // ---- the lesbian format (the user's request of 2026-11-04): the host's, in limits; no male part - the plays two women
            // play, either woman giving (giver), no futanari, no male finish; and a mismatch of the two sides' conditions shown ----
            bp.Advance(true);
            yield return Wait(0.3f);
            SendPkg(D("match", D("start", D("room", "checkroom03", "side", "host", "opponent", D("name", "対戦相手", "creation", whole),
                "limits", D("negotiation_turns", 1, "sex_turns", 12, "climax", 3, "arousal_start", 0.3, "format", "lesbian")))), Line("今日は女の子同士で。"));
            yield return Until(() => h.Match.Active, 40f);
            yield return Wait(1.5f);
            var stL = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "match");
            var lui = h.UiForCheck;
            Check("lesbian: the format from the host's limits - the state's match (format, limits.format); the two-body plays without a penis available (the original's 女性同士, a one-sided AL one too: Muse chooses), the sex plays not",
                  h.Match.Lesbian && Json.Str(stL, "format") == "lesbian" && Json.Str(Json.Obj(stL, "limits"), "format") == "lesbian"
                  && !(bool)pc.Availability(pc.Row("sonyu_00"))["ok"] && (bool)pc.Availability(pc.Row("al_houshi_1003"))["ok"] && (bool)pc.Availability(pc.Row("lesbian_00"))["ok"], Json.Write(stL).Substring(0, Math.Min(300, Json.Write(stL).Length)));
            bp.Advance(true);
            Send(With(Line("挿れてあげる。"), "play", D("start", "sonyu_00")));
            yield return Until(() => bp.Busy && bp.Text == "挿れてあげる。", 10f);
            yield return Wait(0.6f);
            var rfL = LastEvent("block_refused");
            Check("lesbian: a play with a penis is refused (not_in_format), the format's plays listed (the original's 女性同士 among them)", !pc.Active
                  && Json.Str(rfL, "reason") == "not_in_format" && Json.Arr(rfL, "available") != null && Json.Arr(rfL, "available").Contains("lesbian_02")
                  && !Json.Arr(rfL, "available").Contains("sonyu_00"), Json.Write(rfL).Substring(0, Math.Min(300, Json.Write(rfL).Length)));
            bp.Advance(true);
            Send(With(Line("ね、こっち来て。"), "play", D("start", "lesbian_02", "giver", "host", "male_part", "guest")));
            yield return Until(() => pc.Active, 10f);
            yield return Wait(1.5f);
            var rfM = LastEvent("block_refused");
            Check("lesbian: the original's 女性同士 in the match's room - giver host: she takes the second part (either woman in either part); male_part refused (lesbian_format); no one drawn futanari",
                  pc.Active && Json.Str(pc.Current, "id") == "lesbian_02" && pc.Reversed && Json.Str(rfM, "field") == "play.male_part" && Json.Str(rfM, "reason") == "lesbian_format"
                  && !h.Match.Futa(st.Cha) && st.Partner != null && !h.Match.Futa(st.Partner.charInfo) && st.Partner.charInfo.fileStatus.visibleBodyAlways,
                  (pc.Current == null ? "no play" : Json.Str(pc.Current, "id")) + " reversed " + pc.Reversed + " " + Json.Write(rfM));
            yield return Shot("19j_match_lesbian.png");
            bp.Advance(true);
            SendPkg(D("speaker", "opponent"), With(Line("あ、だめ……っ。"), "declare", "climax"));
            yield return Until(() => h.Match.Tally["guest"] == 1, 10f);
            yield return Wait(0.4f);
            bool womenShow = pc.Climax.Running;
            bp.Advance(true);
            yield return Until(() => !pc.Climax.Running, 15f);
            // (a one-sided play: Muse's to choose when the talk goes there - the user's decision of 2026-11-04, the limit lifted)
            Send(With(Line("今度は私が。"), "play", D("start", "al_houshi_1003", "giver", "guest")));
            yield return Until(() => pc.Active && Json.Str(pc.Current, "id") == "al_houshi_1003", 10f);
            yield return Wait(1.0f);
            bool licked = pc.Active && Json.Str(pc.Current, "id") == "al_houshi_1003" && !pc.Reversed;
            bp.Advance(true);
            SendPkg(D("speaker", "opponent"), With(Line("こっちが、先に……っ。"), "declare", "climax"));
            yield return Until(() => h.Match.Tally["guest"] == 2, 10f);
            yield return Wait(0.5f);
            bool noFinish = !pc.Climax.Running;
            bp.Advance(true);
            Send(With(Line("重ねよ……。"), "play", D("start", "lesbian_00", "giver", "guest")));
            yield return Until(() => pc.Active && Json.Str(pc.Current, "id") == "lesbian_00", 10f);
            yield return Wait(1.0f);
            bool rubbing = pc.Active && !pc.Reversed;
            bp.Advance(true);
            Send(With(Line("私も、もう……っ。"), "declare", "climax"));          // (hers: the guest's third would end the match)
            yield return Until(() => h.Match.Tally["host"] == 1, 10f);
            yield return Wait(0.5f);
            bool womenShow2 = pc.Climax.Running;
            Check("lesbian: a declared climax shown as the women's (女性同士: their climax) in either rubbing play; a one-sided AL play starts too, its giver's climax with no male finish (the face only)",
                  womenShow && licked && noFinish && rubbing && womenShow2 && !h.Match.Futa(st.Partner.charInfo), "women " + womenShow + " licked " + licked + " no finish " + noFinish
                  + " rubbing " + rubbing + " women again " + womenShow2);
            yield return Until(() => !pc.Climax.Running, 15f);
            bp.Advance(true);
            // ---- the opponent's clothes (from_muse/request-match-clothes-and-parse.md 1): its package's clothes on its body - a
            // change in a play stays after it; out of a play; its look as it sent the package (clothes_now) restores it ----
            var oppCha = st.Partner.charInfo;
            var wornO = Clothes.Worn(oppCha);
            string sA = Clothes.Keys[wornO[0]], sB = Clothes.Keys[wornO[wornO.Count - 1]];
            string herA = Clothes.State(st.Cha, wornO[0]);
            SendPkg(D("speaker", "opponent"), With(Line("これも、いらないよね。"), "clothes", D(sB, "off")));
            yield return Until(() => Clothes.State(oppCha, wornO[wornO.Count - 1]) == "off", 10f);
            bool inPlayOff = Clothes.State(oppCha, wornO[wornO.Count - 1]) == "off";
            bp.Advance(true);
            pc.End("check");
            yield return Wait(1.0f);
            bool stayed = Clothes.State(oppCha, wornO[wornO.Count - 1]) == "off";
            Check("match talk: after a play in a match she stands as her personality does again (通常立ち, not the idle scene)", h.Talk.NowOf(st.Cha) == "通常立ち",
                  "now " + h.Talk.NowOf(st.Cha));
            SendPkg(D("speaker", "opponent"), With(Line("ほら、見て。"), "clothes", D(sA, "off")));
            yield return Until(() => Clothes.State(oppCha, wornO[0]) == "off", 10f);
            yield return Wait(0.6f);
            var oc1 = Json.Obj(Json.Obj(Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "match"), "opponent"), "clothes");
            bool herKept = Clothes.State(st.Cha, wornO[0]) == herA;
            bp.Advance(true);
            SendPkg(D("speaker", "opponent", "clothes_now", D(sA, "on", sB, "on")), Line("……着直した。"));
            yield return Until(() => Clothes.State(oppCha, wornO[0]) == "on" && Clothes.State(oppCha, wornO[wornO.Count - 1]) == "on", 10f);
            bool restored = Clothes.State(oppCha, wornO[0]) == "on" && Clothes.State(oppCha, wornO[wornO.Count - 1]) == "on";
            Check("match clothes: the opponent's own clothes on its body - a change in a play stays after it, one out of a play, the state's match.opponent.clothes; hers untouched; its clothes_now (as it sent the package) restores its look",
                  inPlayOff && stayed && Json.Str(oc1, sA) == "off" && Json.Str(oc1, sB) == "off" && herKept && restored,
                  string.Format("{0}/{1}: in play {2} stayed {3} state {4} hers kept {5} restored {6}", sA, sB, inPlayOff, stayed, Json.Write(oc1), herKept, restored));
            bp.Advance(true);
            // the two sides' conditions compared by the bridges (muse_match's conditions): a mismatch here as the room's state says it
            h.Bridge.RoomConditions = D("agreed", false, "mismatch", new List<object> { "arousal_start" }, "ours", D("arousal_start", 0.3, "format", "lesbian"),
                "theirs", D("arousal_start", 0.2, "format", "lesbian"));
            yield return Wait(0.6f);
            var stC = Json.Obj(Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "match"), "conditions");
            Check("conditions: a mismatch of the two sides' applied conditions is shown on the panel and in the state's match.conditions",
                  lui.MismatchShown.Contains("発情の開始値") && lui.MismatchShown.Contains("0.3") && lui.MismatchShown.Contains("0.2")
                  && Json.Arr(stC, "mismatch") != null && Json.Arr(stC, "mismatch").Contains("arousal_start"), lui.MismatchShown + " " + Json.Write(stC));
            yield return Shot("19l_match_mismatch.png");
            h.Bridge.RoomConditions = null;
            if (pc.Active) pc.End("check");
            h.Match.Leave("check");
            yield return Until(() => !h.Match.Active && !st.Moving, 20f);
            yield return Wait(0.8f);

            // her breasts both sway (the original's dynamic bones on) wherever she stands between the plays (the user's note of
            // 2026-11-05: in her room her right breast did not sway)
            BustAt("the end");
            Check("bust: both breasts sway at the start, after the talk climax, after the aibu items and at the end (their dynamic bones on)",
                  bustLog.TrueForAll(b => b.EndsWith("L on R on")), string.Join("; ", bustLog.ToArray()));

            lines.AppendLine(string.Format("CHECK SUMMARY pass={0} fail={1}", pass, fail));
            Flush();
            MuseStudioPlugin.Log.LogMessage("MuseStudio: checks done MUSE_PROBE_DONE");
            yield return Wait(0.5f);
            // (the launcher's 全体を終了: its marker, then the quit - Studio's 終了しますか？ skipped; run_checks tells whether it closed)
            try { Directory.CreateDirectory(Path.GetDirectoryName(MuseStudioPlugin.QuitMarker)); File.WriteAllText(MuseStudioPlugin.QuitMarker, "quit"); } catch (Exception) { }
            Application.Quit();
        }
    }
}
