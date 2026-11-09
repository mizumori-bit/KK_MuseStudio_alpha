using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // Her urges (DECISIONS "Muse 側の資料との読み合わせ"; data/urges.json): a mood at its mark (the arousal, the bladder) or the
    // shower's time of day is an urge. The app no longer acts on it by itself: it tells Muse (the event urge) and lists it in
    // the state (urges) until it is answered - a block's action that meets it, or the mood falling below the mark (Muse's
    // satisfied / relieved / leaked) - and tells it again every renotify_minutes while it is not. An answered mood urge is not
    // told again until the mood has been below its mark. The moods carry over a restart: at the start (the character chosen)
    // and on a switch the urges holding then are told (carried_over / character_changed). A block's action (Muse's entry for
    // what she does): masturbation (her own play at the spot nearest her, as the mood's trigger did), toilet / shower (she
    // goes on her own: Outing), shower_together (both move to the shower room, nude - the bath: Bath; no play begins with it).
    public class Urges
    {
        public class Urge { public string Key = "", Name = "", Cause = ""; public DateTime Since; public float LastTold; public int Told; public bool ByMood; }

        readonly MuseStudioPlugin h;
        public readonly Dictionary<string, object> Doc;
        readonly Dictionary<string, Urge> pending = new Dictionary<string, Urge>();
        readonly HashSet<string> answered = new HashSet<string>();    // mood urges met: quiet till the mood is below its mark
        float tickT;
        bool started;

        public Urges(MuseStudioPlugin host)
        {
            h = host;
            Doc = Data.Load("urges.json");
            if (MuseStudioPlugin.Args.ContainsKey("check")) ClockForCheck = DateTime.Today.AddHours(12);    // (checks: outside the shower's hours)
        }

        static float Now { get { return Time.realtimeSinceStartup; } }

        Dictionary<string, object> Defs { get { return Json.Obj(Doc, "urges") ?? new Dictionary<string, object>(); } }

        public bool Pending(string key) { return pending.ContainsKey(key); }

        static int Minutes(string hhmm)
        {
            var p = (hhmm ?? "").Split(':');
            int hr, mn;
            return p.Length == 2 && int.TryParse(p[0], out hr) && int.TryParse(p[1], out mn) ? hr * 60 + mn : -1;
        }

        // the time window's start that holds now (null: outside it)
        static DateTime? WindowStart(Dictionary<string, object> def, DateTime now)
        {
            int a = Minutes(Json.Str(def, "from")), b = Minutes(Json.Str(def, "until"));
            if (a < 0 || b < 0) return null;
            int m = now.Hour * 60 + now.Minute;
            var today = now.Date.AddMinutes(a);
            if (a <= b) return m >= a && m < b ? (DateTime?)today : null;
            if (m >= a) return today;                                // (wrapping midnight: from today's start)
            if (m < b) return today.AddDays(-1);                     // (after midnight: yesterday's start)
            return null;
        }

        public DateTime ClockForCheck = DateTime.MinValue;          // (checks) the local time to use
        public float AgainSecondsForCheck = -1f;                    // (checks) told again sooner
        DateTime Clock { get { return ClockForCheck != DateTime.MinValue ? ClockForCheck : DateTime.Now; } }

        // a mood at its mark, or (the shower) its time of day not met today: either holds it
        bool Holds(string key, Dictionary<string, object> def) { bool byMood; return Holds(key, def, out byMood); }

        bool Holds(string key, Dictionary<string, object> def, out bool byMood)
        {
            string mood = Json.Str(def, "mood");
            float v;
            byMood = mood != null && h.Mood.Values.TryGetValue(mood, out v) && v >= Json.Num(def, "at", 2.0);
            var start = WindowStart(def, Clock);
            return byMood || (start.HasValue && h.Mood.DoneAt(key) < start.Value);
        }

        // (Plugin, the character chosen / switched) the urges holding now are told
        public void Start(string cause)
        {
            pending.Clear();
            answered.Clear();
            started = true;
            foreach (var kv in Defs)
                if (Holds(kv.Key, (Dictionary<string, object>)kv.Value)) Add(kv.Key, (Dictionary<string, object>)kv.Value, cause);
        }

        void Add(string key, Dictionary<string, object> def, string cause)
        {
            bool byMood;
            Holds(key, def, out byMood);
            var u = new Urge { Key = key, Name = Json.Str(def, "name", key), Since = DateTime.Now, Cause = cause, ByMood = byMood };
            pending[key] = u;
            Tell(u, cause);
        }

        void Tell(Urge u, string cause)
        {
            u.Told++;
            u.LastTold = Now;
            h.Bridge.Event("urge", new Dictionary<string, object> { { "urge", u.Key }, { "name", u.Name }, { "cause", cause }, { "told", u.Told },
                { "since", u.Since.ToString("yyyy-MM-ddTHH:mm:ss") }, { "actions", Json.Arr(Defs[u.Key], "actions") ?? new List<object>() },
                { "moods", h.Mood.Info() }, { "urges", Info() } });
        }

        void Clear(string key, string cause)
        {
            if (!pending.Remove(key)) return;
            h.Bridge.Event("urge_cleared", new Dictionary<string, object> { { "urge", key }, { "cause", cause }, { "urges", Info() } });
        }

        public void Tick(float dt)
        {
            if (!started) return;
            if (h.Match.Active || h.Match.Loading) return;      // (a match: no urge told - the moods build as ever; told after it)
            tickT += dt;
            if (tickT < 1f) return;
            tickT = 0f;
            float again = AgainSecondsForCheck > 0f ? AgainSecondsForCheck : (float)Json.Num(Doc, "renotify_minutes", 10.0) * 60f;
            foreach (var kv in Defs)
            {
                var def = (Dictionary<string, object>)kv.Value;
                bool byMood;
                bool holds = Holds(kv.Key, def, out byMood);
                Urge u;
                if (!holds)
                {
                    answered.Remove(kv.Key);
                    Clear(kv.Key, pending.TryGetValue(kv.Key, out u) && u.ByMood ? "fell" : "window_passed");
                    continue;
                }
                if (pending.TryGetValue(kv.Key, out u)) { u.ByMood = byMood; if (Now - u.LastTold >= again) Tell(u, "again"); }
                else if (!answered.Contains(kv.Key)) Add(kv.Key, def, byMood ? "rose" : "time");
            }
        }

        // an action of Muse's met the urges it answers
        internal void Answered(string action)
        {
            foreach (var kv in Defs)
            {
                var acts = Json.Arr(kv.Value, "actions");
                if (acts == null || !acts.Contains(action)) continue;
                if (Json.Has(kv.Value, "mood")) answered.Add(kv.Key);
                if (Json.Has(kv.Value, "from")) h.Mood.MarkDone(kv.Key, Clock);
                Clear(kv.Key, "answered");
            }
        }

        // ---- a block's action (Muse's entry for what she does) ----

        public static readonly string[] Actions = { "masturbation", "toilet", "shower", "shower_together", "alone", "change" };

        public void Act(string kind, Dictionary<string, object> opts)
        {
            string why = "";
            if (Array.IndexOf(Actions, kind) < 0) why = "unknown_action";
            else if (h.Outing.Out) why = "she_is_out";
            else if (h.Match.Active || h.Match.Loading) why = "in_match";
            else if (h.Chars.Creating) why = "creation";
            else if (h.Bath.Busy && kind != "masturbation") why = "in_bath";       // (the bath: no outing, no second bath - #70 adopted)
            else if (kind == "masturbation") why = h.Mood.StartMasturbation("arousal", "action");
            else if (kind == "toilet" || kind == "shower" || kind == "alone" || kind == "change") why = h.Outing.Go(kind, opts);
            else if (kind == "shower_together") why = h.Bath.Go(opts);
            if (why.Length == 0 && kind == "shower_together") return;           // (told at the arrival: Bath)
            if (why.Length > 0)
            {
                h.Bridge.Event("block_refused", new Dictionary<string, object> { { "field", "action" }, { "value", kind }, { "reason", why },
                    { "available", new List<object>(Actions) } });
                return;
            }
            h.Bridge.Event("action_started", new Dictionary<string, object> { { "action", kind }, { "location", h.Stage.Location },
                { "her_location", h.Presence.HerRoom } });
            h.Mood.ActionDone(kind);           // (a shower: the wish to shower back to 0)
            Answered(kind);
            if (kind == "alone" && h.Outing.Masturbating) Answered("masturbation");      // (her own time with her hand: the arousal's urge)
        }

        public List<object> Info()
        {
            var o = new List<object>();
            foreach (var u in pending.Values)
                o.Add(new Dictionary<string, object> { { "urge", u.Key }, { "name", u.Name }, { "since", u.Since.ToString("yyyy-MM-ddTHH:mm:ss") }, { "told", u.Told },
                    { "actions", Json.Arr(Defs[u.Key], "actions") ?? new List<object>() } });
            return o;
        }
    }
}
