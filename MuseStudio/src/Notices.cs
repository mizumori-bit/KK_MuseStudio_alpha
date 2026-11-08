using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // The app's technical notices on the screen, and Muse told of the ones shown (#70 adopted, 3: before, such errors were in
    // the log only). A notice is the app's own words about the machinery - never her words, nothing said for her (the
    // owner's view and GPT's: no stand-in line of the character's). Each one shown is told to Muse as app_notice - phase
    // "shown" when it is first drawn, "cleared" when it goes, with what it said and when (one not drawn - the UI hidden all
    // the while - is not told: Muse learns only what the user saw). The events go the app's one ordered line to the bridge,
    // so a notice of the bridge out of reach arrives once it is back.
    //   bridge_unreachable  the bridge not answering for unreachable_seconds (cleared as it answers again)
    //   muse_unread         the events Muse has not read waiting past the setting's minutes (the bridge's /health event_feed;
    //                       0: off) - Muse not reading, its side to look at (cleared once Muse reads)
    //   room_load_failed    a room that would not load (its seconds, then gone)
    public class Notices
    {
        class Note
        {
            public string Kind, Text, ShownAt = "";
            public bool Displayed;
            public float Until = -1f;          // (realtime; -1: until cleared)
            public float DisplayedAt;
        }

        readonly MuseStudioPlugin h;
        readonly List<Note> notes = new List<Note>();
        float unreachableSince = -1f;
        public int Told;                       // (checks) app_notice events sent
        public float UnreachableSeconds = 5f;
        public bool UnreachableForCheck;       // (checks) the bridge taken for out of reach

        public Notices(MuseStudioPlugin host) { h = host; }

        public bool Has(string kind) { return notes.Exists(n => n.Kind == kind); }
        public string TextOf(string kind) { var n = notes.Find(x => x.Kind == kind); return n != null ? n.Text : ""; }

        // a notice up (the same kind again: its words and time renewed); seconds < 0: until cleared
        public void Raise(string kind, string text, float seconds = -1f)
        {
            var n = notes.Find(x => x.Kind == kind);
            if (n == null)
            {
                n = new Note { Kind = kind };
                notes.Add(n);
            }
            n.Text = text;
            n.Until = seconds >= 0f ? Time.realtimeSinceStartup + seconds : -1f;
        }

        public void Clear(string kind)
        {
            var n = notes.Find(x => x.Kind == kind);
            if (n == null) return;
            notes.Remove(n);
            if (!n.Displayed) return;
            Tell(n, "cleared");
        }

        void Tell(Note n, string phase)
        {
            var e = new Dictionary<string, object> { { "phase", phase }, { "kind", n.Kind }, { "text", n.Text }, { "shown", n.ShownAt } };
            if (phase == "cleared")
            {
                e["cleared"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                e["seconds"] = Math.Round(Time.realtimeSinceStartup - n.DisplayedAt, 1);
            }
            h.Bridge.Event("app_notice", e);
            Told++;
        }

        public void Tick()
        {
            float now = Time.realtimeSinceStartup;
            // the bridge out of reach (not when another window has it: that one is in use)
            var b = h.Bridge;
            bool out_ = UnreachableForCheck || (b != null && !b.Connected && !b.Superseded);
            if (out_)
            {
                if (unreachableSince < 0f) unreachableSince = now;
                if (now - unreachableSince >= UnreachableSeconds && !Has("bridge_unreachable"))
                    Raise("bridge_unreachable", "ブリッジにつながりません（つながるまで試し続けます。出来事は、つながってから Muse に届きます）");
            }
            else
            {
                unreachableSince = -1f;
                if (Has("bridge_unreachable")) Clear("bridge_unreachable");
            }
            // Muse not reading its events
            float mins = h.MuseUnreadMinutes != null ? h.MuseUnreadMinutes.Value : 3f;
            int waited = b != null && b.Connected && !out_ ? b.OldestUnreadSeconds : -1;
            if (mins > 0f && waited >= mins * 60f)
                Raise("muse_unread", string.Format("Muse が出来事を {0} 分読んでいません（Muse の側の接続を確かめてください）", Mathf.FloorToInt(waited / 60f)));
            else if (Has("muse_unread") && (waited < 0 || mins <= 0f || waited < mins * 60f)) Clear("muse_unread");
            // the timed ones over
            foreach (var n in notes.ToArray())
                if (n.Until >= 0f && now >= n.Until) Clear(n.Kind);
        }

        // drawn at the top, in the middle (only while the UI shows: the first time drawn, told as shown)
        public void Draw(float s, GUIStyle style, GUIStyle boxStyle, List<Rect> drawing)
        {
            if (notes.Count == 0) return;
            float w = Mathf.Min(Screen.width - 40f * s, 620f * s), lh = 30f * s, x = (Screen.width - w) / 2f, y = 10f * s;
            foreach (var n in notes)
            {
                var r = new Rect(x, y, w, lh);
                GUI.Box(r, GUIContent.none, boxStyle);
                GUI.Label(new Rect(x + 10f * s, y + 3f * s, w - 20f * s, lh - 6f * s), "⚠ " + n.Text, style);
                drawing.Add(r);
                y += lh + 4f * s;
                if (!n.Displayed && Event.current.type == EventType.Repaint)
                {
                    n.Displayed = true;
                    n.DisplayedAt = Time.realtimeSinceStartup;
                    n.ShownAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                    Tell(n, "shown");
                }
            }
        }

        internal void ExpireForCheck(string kind) { var n = notes.Find(x => x.Kind == kind); if (n != null) n.Until = Time.realtimeSinceStartup; }
    }
}
