using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MuseStudio
{
    // What the user watches the checks and the play for, written in the log when it changes (the user's notes of 2026-11-07
    // and 2026-11-08: a face seen staying - its parts, not only its label; dressed, the nipples through the clothes at a
    // match's play) - each body on the stage: hers, and the other one (the opponent's in a match, the user's body in a play):
    //   face    - the eyebrow / eyes / mouth patterns (the mouth's corners are its pattern) and their open max, the eyes' blink
    //             on / off and highlight, the mouth fixed (Studio's 口の開き) and whether the words move it, the eyes' and the
    //             neck's look patterns and what the eyes look at, the cheek, the tears; hers with the face's label
    //   clothes - every slot's state (0 on, 1 / 2 half, 3 off)
    //   pushup  - Push Up's bra and top: on and in effect, enabled, flattening; the nipple's and the areola's depth (shapes 13
    //             and 11); looked at every frame for the depths and the top's and the bra's states, so a moment shorter than a
    //             second is written too
    public class Diag
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        class Watch
        {
            public ChaControl Of;
            public MonoBehaviour Pushup;
            public string Face = "", Clothes = "", PushupLine = "", PushupKey = "";
        }

        readonly Dictionary<string, Watch> watch = new Dictionary<string, Watch>();
        float nextFace;

        public void Tick(Stage st)
        {
            if (st == null || !st.Ready) return;
            float now = Time.realtimeSinceStartup;
            bool faceNow = now >= nextFace;
            if (faceNow) nextFace = now + 0.1f;
            Body("her", st.Cha, st, faceNow);
            var host = MuseStudioPlugin.Instance;
            Body(host.Match.Active ? "opponent" : "partner", st.Partner != null ? st.Partner.charInfo : null, st, faceNow);
        }

        void Body(string who, ChaControl cha, Stage st, bool faceNow)
        {
            Watch w;
            if (!watch.TryGetValue(who, out w)) { w = new Watch(); watch[who] = w; }
            if (cha == null || cha.fileStatus == null) { w.Of = null; return; }
            if (!ReferenceEquals(w.Of, cha) || w.Pushup == null)
            {
                bool another = !ReferenceEquals(w.Of, cha);
                w.Of = cha;
                w.Pushup = null;
                if (another) { w.Face = w.Clothes = w.PushupLine = w.PushupKey = ""; }
                foreach (var c in cha.GetComponents<MonoBehaviour>()) if (c != null && c.GetType().FullName == "KK_Plugins.Pushup+PushupController") w.Pushup = c;
            }
            float t = Time.realtimeSinceStartup;
            // Push Up: its depths and the top's and the bra's states each frame, its whole line when they or its flags change
            var cs = cha.fileStatus.clothesState;
            var body = cha.fileBody != null ? cha.fileBody.shapeValueBody : null;
            if (cha.sex == 1 && body != null && body.Length > 13)
            {
                string key = string.Format("{0:F2} {1:F2} {2} {3} {4}", body[13], body[11], cs.Length > 0 ? cs[0] : -1, cs.Length > 2 ? cs[2] : -1, cha.fileStatus.coordinateType);
                if (key != w.PushupKey || faceNow)
                {
                    w.PushupKey = key;
                    string line = PushupLine(w.Pushup, cha);
                    if (line != w.PushupLine)
                    {
                        w.PushupLine = line;
                        MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: pushup {0} {1} at {2:F2} s", who, line, t));
                    }
                }
            }
            if (!faceNow) return;
            string clothes = string.Join(",", Array.ConvertAll(cs, b => b.ToString()));
            if (clothes != w.Clothes)
            {
                w.Clothes = clothes;
                MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: clothes {0} [{1}] (top, bottom, bra, shorts, gloves, pantyhose, socks, shoes, shoes2) at {2:F2} s", who, clothes, t));
            }
            string face = FaceLine(cha, who == "her" ? st.Face : null);
            if (face != w.Face)
            {
                w.Face = face;
                MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: face parts {0} {1} at {2:F2} s", who, face, t));
            }
        }

        static string FaceLine(ChaControl cha, string label)
        {
            var s = cha.fileStatus;
            var look = cha.eyeLookCtrl != null && cha.eyeLookCtrl.target != null ? cha.eyeLookCtrl.target.name : "-";
            var m = Mouth.Instance;
            bool speaking = m != null && m.Active && ReferenceEquals(m.Target, cha);
            float fixedRate = Mouth.FixedOf(cha);
            return string.Format("{0}eyebrow {1} max {2:F2} | eyes {3} max {4:F2} blink {5} highlight {6} | mouth {7} max {8:F2} fixed {9} {10}| look eyes {11} at {12} neck {13} | cheek {14:F2} tears {15}",
                label != null ? "[" + label + "] " : "", s.eyebrowPtn, s.eyebrowOpenMax, s.eyesPtn, s.eyesOpenMax, s.eyesBlink ? "on" : "off", s.hideEyesHighlight ? "off" : "on",
                s.mouthPtn, s.mouthOpenMax, fixedRate < 0f ? "no" : fixedRate.ToString("F2"), speaking ? "speaking " : "", s.eyesLookPtn, look, s.neckLookPtn,
                s.hohoAkaRate, s.tearsLv);
        }

        static string PushupLine(MonoBehaviour pushup, ChaControl cha)
        {
            if (pushup == null) return "no Push Up";
            try
            {
                var bra = Member(pushup, "CurrentBraData");
                var top = Member(pushup, "CurrentTopData");
                var st = cha.fileStatus.clothesState;
                return string.Format("bra on+enabled {0} (enable {1} flatten {2}) top on+enabled {3} (enable {4} flatten {5}) | nipple(13) {6:F2} areola(11) {7:F2} | top state {8} bra state {9} | outfit {10}",
                    Member(pushup, "BraIsOnAndEnabled"), Member(bra, "EnablePushup"), Member(bra, "FlattenNipples"),
                    Member(pushup, "TopIsOnAndEnabled"), Member(top, "EnablePushup"), Member(top, "FlattenNipples"),
                    cha.fileBody.shapeValueBody[13], cha.fileBody.shapeValueBody[11], st.Length > 0 ? st[0] : -1, st.Length > 2 ? st[2] : -1, cha.fileStatus.coordinateType);
            }
            catch (Exception e) { return "not read (" + (e.InnerException ?? e).Message + ")"; }
        }

        static object Member(object o, string name)
        {
            if (o == null) return "-";
            var pr = o.GetType().GetProperty(name, F);
            if (pr != null && pr.GetIndexParameters().Length == 0) return pr.GetValue(o, null);
            var f = o.GetType().GetField(name, F);
            return f != null ? f.GetValue(o) : "-";
        }
    }
}
