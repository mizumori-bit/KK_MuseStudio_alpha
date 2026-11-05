using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // The pacing material in the state (MUSE_GUIDE.md 3.10; option B of the consultation with Muse, 2026-11-03): the app no
    // longer asks Muse for beats (no beat_request / choice_request - nothing on Muse's side wakes within seconds; a play is
    // driven from the chat). The state's "beat" says how long since the last package was all presented (since_presented,
    // seconds; null while one is being presented or before the first), and whether a question would fit now (question_due:
    // "near" - her gauge near since her last climax, "mood" - her mood high for some packages, "beats" - several packages
    // since the last question; null: none; in a play only). Muse reads them to decide whether to speak and whether to pose a
    // question. The play's idle stop (no press, no block of Muse's for a while) stays (PlayCtl). data/beat.json "choice".
    public class Beat
    {
        readonly MuseStudioPlugin h;
        readonly Dictionary<string, object> doc;
        public float Clock = -1f;             // seconds since the last presentation ended (< 0: one is being presented / none yet)
        public int InPlay, SinceChoice, MoodPackages;
        bool nearDone, moodDone;
        public string Context = "";

        public Beat(MuseStudioPlugin host)
        {
            h = host;
            doc = Data.Load("beat.json");
        }

        public string ContextNow()
        {
            var pc = h.Play;
            string ch = h.CharacterId();
            if (h.Chars.Creating) return "creation|" + ch;
            if (pc == null || !pc.Active) return "talk|" + ch + "|" + h.Stage.Location;
            return ch + "|" + h.Stage.Location + "|" + Json.Str(pc.Current, "id") + "|" + pc.Started;
        }

        bool InPlayNow { get { return Context.Length > 0 && !Context.StartsWith("talk|") && !Context.StartsWith("creation|"); } }

        public void NearReset() { nearDone = false; }      // a new near point after her climax

        // a block of Muse's shown / a presentation ended: the play is going on (its idle stop counts from here)
        public void Progress() { h.Play.Progress(); }

        // the package's last block is all out (no voice: the presentation's end)
        public void Presented(Dictionary<string, object> pkg)
        {
            Clock = 0f;
            Progress();
            if (InPlayNow && !Json.Has(pkg, "choices"))
            {
                InPlay++;
                SinceChoice++;
                if (MoodLevel()) MoodPackages++;
            }
        }

        // a package starts; one with choices poses the question (the points holding now are done)
        public void Played(Dictionary<string, object> pkg)
        {
            Clock = -1f;
            if (!Json.Has(pkg, "choices")) return;
            SinceChoice = 0;
            if (NearHolds()) nearDone = true;
            if (MoodHolds()) moodDone = true;
        }

        public void Tick(float dt)
        {
            string ctx = ContextNow();
            if (ctx != Context)
            {
                Context = ctx;
                InPlay = SinceChoice = MoodPackages = 0;
                nearDone = moodDone = false;
            }
            var bp = h.Blocks;
            if (bp.Busy && !bp.PresentedLast) Clock = -1f;
            else if (Clock >= 0f) Clock += dt;
        }

        bool NearHolds()
        {
            float near = (float)Json.Num(Json.Obj(h.Gauge.Doc, "milestones"), "near", 0.85);
            return h.Gauge.Phase == "rising" && h.Gauge.Value >= near;
        }

        // the mood at level_min (beat.json choice.mood) / for after_beats packages
        bool MoodLevel()
        {
            var m = Json.Obj(Json.Obj(doc, "choice"), "mood");
            float v;
            return m != null && h.Mood.Values.TryGetValue(Json.Str(m, "key", ""), out v) && v >= Json.Num(m, "level_min", 2.0);
        }

        bool MoodHolds() { return MoodLevel() && MoodPackages >= (int)Json.Num(Json.Obj(Json.Obj(doc, "choice"), "mood"), "after_beats", 2); }

        public string Due()
        {
            if (!InPlayNow) return "";
            var c = Json.Obj(doc, "choice");
            object n;
            bool nearOn = c == null || !c.TryGetValue("near", out n) || !(n is bool) || (bool)n;
            if (nearOn && !nearDone && NearHolds()) return "near";
            if (!moodDone && MoodHolds()) return "mood";
            int every = (int)Json.Num(c, "every_beats", 0);
            if (every > 0 && SinceChoice >= every) return "beats";
            return "";
        }

        public Dictionary<string, object> Info()
        {
            string due = Due();
            return new Dictionary<string, object> { { "since_presented", Clock >= 0f ? (object)Math.Round(Clock, 1) : null },
                { "question_due", due.Length > 0 ? due : null }, { "packages_in_play", InPlay }, { "since_question", SinceChoice } };
        }
    }
}
