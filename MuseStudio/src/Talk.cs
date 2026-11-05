using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // The original's talk motions (the user's request of 2026-11-04; data/talk.json from tools/derive_talk.py): the poses of
    // the original's talk (communication pose_NN - 通常立ち, 怒り立ち, the base stances ベース00..38 = each personality's normal
    // stance, 喜 / 怒 / 哀 / 恥 / 驚 / 他 and the reactions), played as the original's TalkScene plays them (its ADV controller
    // and state, Illusion's Motion loader). In a match's talk (not in a play) a block's pose moves the body of the block's
    // speaker; without one each body stands as its personality does (its 通常立ち). Not the IK lists (the hands' fit).
    public class Talk
    {
        readonly MuseStudioPlugin h;
        readonly Dictionary<string, object> doc;

        public Talk(MuseStudioPlugin host)
        {
            h = host;
            doc = Data.Load("talk.json") ?? new Dictionary<string, object>();
        }

        public Dictionary<string, object> Poses { get { return Json.Obj(doc, "poses") ?? new Dictionary<string, object>(); } }

        static string Clean(string name) { return (name ?? "").Trim().Trim('　'); }

        public bool Known(string name)
        {
            name = Clean(name);
            return Poses.ContainsKey(name) || Json.Obj(Json.Obj(doc, "personal"), name) != null;
        }

        public List<object> Names()
        {
            var o = new List<object>();
            foreach (var k in Json.Obj(doc, "personal") ?? new Dictionary<string, object>()) o.Add(k.Key);
            foreach (var k in Poses) o.Add(k.Key);
            return o;
        }

        // the motion of a name for a personality: [bundle, asset, state] (personal names by the personality, else the common)
        string[] Motion(string name, int personality)
        {
            name = Clean(name);
            var per = Json.Obj(Json.Obj(doc, "personal"), name);
            if (per != null)
            {
                var m = Json.Arr(per, personality.ToString()) ?? Json.Arr(per, "0");
                return m != null && m.Count >= 3 ? new[] { (string)m[0], (string)m[1], (string)m[2] } : null;
            }
            var e = Json.Obj(Poses, name);
            return e != null ? new[] { Json.Str(e, "bundle"), Json.Str(e, "asset"), Json.Str(e, "state") } : null;
        }

        public string NowOf(ChaControl cha) { string s; return cha != null && now.TryGetValue(cha, out s) ? s : null; }

        readonly Dictionary<ChaControl, string> now = new Dictionary<ChaControl, string>();

        // a body to a pose (true: played)
        public bool Play(ChaControl cha, string name)
        {
            if (cha == null || cha.animBody == null) return false;
            var m = Motion(name, cha.chaFile.parameter.personality);
            if (m == null) return false;
            try
            {
                var mo = new Illusion.Game.Elements.EasyLoader.Motion(m[0], m[1], m[2]);
                mo.Setting(cha.animBody);
                mo.Play(cha.animBody);
                now[cha] = Clean(name);
                return true;
            }
            catch (Exception e)
            {
                MuseStudioPlugin.Log.LogWarning("MuseStudio: talk pose " + name + ": " + e.Message);
                return false;
            }
        }

        // her personality's own stance (the original's 通常立ち)
        public bool Stand(ChaControl cha) { return Play(cha, "通常立ち"); }

        public void Forget(ChaControl cha) { if (cha != null) now.Remove(cha); }

        public Dictionary<string, object> PersonalityInfo(ChaControl cha)
        {
            if (cha == null) return null;
            int no = cha.chaFile.parameter.personality;
            var p = Json.Obj(Json.Obj(doc, "personalities"), no.ToString());
            return new Dictionary<string, object> { { "no", no }, { "name", CharaSummary.PersonalityName(no) }, { "stance", Json.Str(p, "stance", null) } };
        }
    }
}
