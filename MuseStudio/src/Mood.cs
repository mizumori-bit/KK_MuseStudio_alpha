using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MuseStudio
{
    // The character's moods (MUSE_GUIDE.md 3.9; data/moods.json; the Godot app's mood_controller.gd), apart from the climax
    // gauge: the arousal, the urge to pee. Each 0..1 moves by its rules: per minute (also over the time the app was closed,
    // capped), by touches (per area), per second of a play by its pace. Her climax does not lower it by itself: whether it
    // satisfied her is Muse's call (a block's mood {key: "satisfied"} within satisfy_window_seconds after a climax of hers);
    // a mood's "muse" table holds the other values a block may send (relieved, leaked). Its level is in the state and a change
    // of level is the event mood_changed. Nothing acts by itself any more (the user's decision 2026-11-03): a mood at its mark
    // is an urge told to Muse (Urges); Muse's action masturbation starts her own play at the spot nearest her (the trigger's
    // plays), its pace rising with her gauge unless Muse set one lately; it ends after her climax there. Kept per character
    // in game/UserData/MuseStudio/moods.json with when an urge without a mood (the shower) was last met (checks: their own
    // file). The leak's look: the original's pee (HLiquid.Pee).
    public class Mood
    {
        readonly MuseStudioPlugin h;
        public readonly Dictionary<string, object> Doc;
        public readonly Dictionary<string, float> Values = new Dictionary<string, float>();
        readonly Dictionary<string, string> levels = new Dictionary<string, string>();
        readonly Dictionary<string, float> satisfiable = new Dictionary<string, float>();   // key -> when her climax was
        readonly Dictionary<string, double> done = new Dictionary<string, double>();     // urge -> when it was last met (unix)
        readonly Dictionary<string, float> afterPlay = new Dictionary<string, float>();   // what the running play adds when it ends
        public string MoodPlay = "";        // the mood whose trigger started the running play
        float idleSince, saveT, aliveT;
        string charId = "";
        // The app's own closed time (GPT's post-review of #29), in the same file under "_app": when the app was last alive and
        // the minutes it has been closed in all. A character's record keeps that total at its save, so the time closed since
        // is the total now minus it - not the time since her save, which also counted an open app with another character on
        // the stage, and the time before she was chosen again.
        const string AppKey = "_app";
        double closedTotal;                 // minutes the app has been closed in all, as far as the file knows
        double lastGap = -1.0;              // minutes it was closed just before this launch (-1: not known - no "_app" before)
        readonly string savePath;
        public readonly List<Dictionary<string, object>> Log = new List<Dictionary<string, object>>();

        public Mood(MuseStudioPlugin host, string path)
        {
            h = host;
            savePath = path;
            Doc = Data.Load("moods.json");
            idleSince = Now;
            startedAt = Unix;
            var all = ReadAll();
            if (all != null) TakeApp(all);
            Alive();
        }

        // the app's record as the file has it: the closed time before this launch (from its start - not when it is read)
        readonly double startedAt;
        bool appRead;                       // false: the file was not readable yet - its record is read before anything is written
        bool charRead = true;               // false: her record was not readable when she was chosen - read before her save

        bool closedTaken;                   // (the closed time before this launch: taken once, at its first read)

        void TakeApp(Dictionary<string, object> all)
        {
            appRead = true;
            if (h.Items != null) h.Items.Import(Json.Obj(all, ItemsKey));     // (the items' ledger: in the same file - PR #46 §7)
            if (closedTaken) return;
            closedTaken = true;                 // (at the first read, whatever the file has: none - not known, -1)
            var app = Json.Obj(all, AppKey);
            if (app == null || !Json.Has(app, "alive")) return;
            lastGap = Math.Max(0.0, (startedAt - Json.Num(app, "alive")) / 60.0);
            closedTotal = Math.Max(0.0, Json.Num(app, "closed_total", 0)) + lastGap;
        }

        Dictionary<string, object> AppDoc()
        {
            return new Dictionary<string, object> { { "alive", Unix }, { "closed_total", Math.Round(closedTotal, 3) }, { "launch", Launch.Id } };
        }

        // the app still alive (every 30 s, at each save, at the start): what its next start counts its closed time from
        bool Alive()
        {
            try
            {
                var all = ReadAll();
                if (all == null) return false;         // (not readable: left as it is)
                if (!appRead) TakeApp(all);            // (its record first: never written over unread - GPT's re-review of #42)
                all[AppKey] = AppDoc();
                if (h.Items != null && h.Items.Imported) all[ItemsKey] = h.Items.Export();
                WriteWhole(Json.Write(all));
                return true;
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: moods: " + e.Message); return false; }
        }

        public bool FailWritesForCheck;        // (checks) the next writes fail, as a full disk would

        // (checks) a moods file at another path: what the start does to it
        public static Mood ForFileCheck(MuseStudioPlugin host, string path) { return new Mood(host, path); }
        public float ValueForCheck(string key) { float v; Values.TryGetValue(key, out v); return v; }

        // (checks) the app's closed time as if: the total and the last closed time (-1: not known)
        public void SetClosedForCheck(double total, double gap) { closedTotal = total; lastGap = gap; }
        public double ClosedTotalForCheck { get { return closedTotal; } }
        public double LastGapForCheck { get { return lastGap; } }

        static float Now { get { return Time.realtimeSinceStartup; } }

        Dictionary<string, object> Moods { get { return Json.Obj(Doc, "moods") ?? new Dictionary<string, object>(); } }

        static double Unix { get { return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; } }

        // The file as it is: missing - empty (the first start); there but not readable now (held by another program) - null, and
        // nothing is written over it until it reads; not a moods file - copied aside first (moods.json.unreadable-<time>),
        // then an empty one; no copy - null, left as it is (GPT's re-review of #41: a broken file became {} and the start's
        // first write replaced the characters' records)
        public bool HeldForCheck;          // (checks) the file is kept from being written
        bool told;

        public bool FailReadsForCheck;     // (checks) the file cannot be read, as when another program holds it

        // The file's records taken in now if they could not be read before - the app's (the items' ledger with it) and hers:
        // true when both are in. An item's block is done on them, never on what a later read would put over it (GPT's re-review
        // of #71: the file readable again, the first item's write read the ledger and her effects back over the change just
        // made, and said done)
        public bool EnsureRead()
        {
            if (appRead && charRead) return true;
            var all = ReadAll();
            if (all == null) return false;
            if (!appRead) TakeApp(all);
            if (!charRead)
            {
                charRead = true;
                var mine = Json.Obj(all, charId);
                if (mine != null) Apply(charId, mine);
            }
            return true;
        }

        internal void ForgetReadForCheck(bool app, bool hers) { if (app) appRead = false; if (hers && charId.Length > 0) charRead = false; }     // (checks)

        Dictionary<string, object> ReadAll()
        {
            if (FailReadsForCheck) { Hold("could not be read (check)"); return null; }
            if (!File.Exists(savePath) && File.Exists(savePath + ".bak"))
            {
                try { File.Move(savePath + ".bak", savePath); }       // (a save stopped between its moves: the old file back)
                catch (Exception e) { Hold("is only there as .bak and could not be put back (" + e.GetType().Name + ")"); return null; }
            }
            if (!File.Exists(savePath)) { HeldForCheck = false; return new Dictionary<string, object>(); }
            string text;
            try { text = File.ReadAllText(savePath); }
            catch (Exception e) { Hold("could not be read (" + e.GetType().Name + ")"); return null; }
            Dictionary<string, object> doc = null;
            try { doc = Json.Parse(text) as Dictionary<string, object>; } catch (Exception) { doc = null; }
            if (doc != null) { HeldForCheck = false; told = false; return doc; }
            string aside = savePath + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Copy(savePath, aside, false);
                WriteWhole("{}");
                MuseStudioPlugin.Log.LogWarning("MuseStudio: moods: the file was not a moods file - copied aside as " + Path.GetFileName(aside) + ", started afresh");
                HeldForCheck = false;
                return new Dictionary<string, object>();
            }
            catch (Exception e) { Hold("is not a moods file and could not be copied aside (" + e.GetType().Name + ")"); return null; }
        }

        void Hold(string why)
        {
            HeldForCheck = true;
            if (told) return;
            told = true;
            MuseStudioPlugin.Log.LogWarning("MuseStudio: moods: the file " + why + " - not written over (the moods of this launch are not kept until it reads)");
        }

        // a whole new file first, then put in its place: a write cut off leaves the old file as it was
        // a whole new file first (.part); the old one moved aside (.bak) and the new one moved in; the old one removed last. A
        // stop at any point leaves a whole file: the old one in place, or as .bak (taken back by the next read). Never written
        // into in place (GPT's re-review of #42: the fallback copied over it)
        // (WholeFile: done once the new text is in place - a .bak not cleared no longer makes a written record a failed one,
        // which put the items back while the file held them: the same as 献精室's, GPT's re-review of #119, 2)
        void WriteWhole(string text)
        {
            if (FailWritesForCheck) throw new IOException("check: the write fails");
            string err;
            if (!WholeFile.Write(savePath, text, out err)) throw new IOException(err);
        }

        // The character's moods as saved. While the app was closed she lived her day (PR #25, the owner's decision of
        // 2026-11-06): a mood with "away" settles towards its everyday value (a value drawn once in "toward" - the bladder low,
        // the arousal 0.2-0.4, all below their urges' marks), the further the longer it was closed (all of it after
        // settle_minutes); nothing builds up from the closed time alone. The closed time is the app's own (its closed total
        // now minus the one kept in her record - GPT's post-review of #29), not the time since she was saved. Only for time the app was closed: a character saved
        // under this launch (switched away and back) comes back as she was - no settling, nothing drawn again. A mood without
        // "away": as before (per_minute x the closed minutes, at most away_minutes_max).
        public void LoadFor(string id)
        {
            if (charId.Length > 0) Save();
            var all = ReadAll();
            if (all != null && !appRead) TakeApp(all);
            charRead = all != null;                // (not readable: she starts as new, and her record is read before her save)
            Apply(id, all != null ? Json.Obj(all, id) : null);
        }

        public string ResumedForCheck = "";       // (checks) how the last load came back: "new" / "same_launch" / "after_closed <minutes>"

        void Apply(string id, Dictionary<string, object> mine)
        {
            charId = id;
            Values.Clear(); levels.Clear(); satisfiable.Clear(); done.Clear();
            foreach (var kv in Json.Obj(mine, "done") ?? new Dictionary<string, object>()) if (kv.Value is double) done[kv.Key] = (double)kv.Value;
            bool saved = Json.Has(mine, "saved");
            bool sameLaunch = saved && Json.Str(mine, "launch") == Launch.Id;
            // the minutes the app was closed since her record was saved: the app's total then and now; a record from before the
            // total was kept - the last closed time (all that is known); nothing of the app's either - the time since her save
            double away = 0.0;
            if (saved && !sameLaunch)
                away = Json.Has(mine, "closed_total") ? Math.Max(0.0, closedTotal - Json.Num(mine, "closed_total"))
                     : lastGap >= 0.0 ? lastGap : Math.Max(0.0, (Unix - Json.Num(mine, "saved")) / 60.0);
            ResumedForCheck = !saved ? "new" : sameLaunch ? "same_launch" : "after_closed " + away.ToString("F0");
            // an item's effect still flowing in (PR #46 §7): kept as it was in the same launch; after the app was closed she lived
            // her day - what had not come in yet is gone (the moods settle as below), never added again
            pending.Clear();
            if (sameLaunch)
                foreach (var po in Json.Arr(mine, "pending") ?? new List<object>())
                {
                    var pd = po as Dictionary<string, object>;
                    if (pd != null && Json.Num(pd, "left") > 0)
                        pending.Add(new Pending { Mood = Json.Str(pd, "mood", ""), Left = (float)Json.Num(pd, "left"), Rate = (float)Json.Num(pd, "rate"), Item = Json.Str(pd, "item", ""),
                                                  Cap = (float)Json.Num(pd, "cap", 1.0) });
                }
            foreach (var kv in Moods)
            {
                var vals = Json.Obj(mine, "values");
                double v = Json.Has(vals, kv.Key) ? Json.Num(vals, kv.Key) : Json.Num(kv.Value, "start", 0);
                var aw = Json.Obj(kv.Value, "away");
                if (saved && !sameLaunch && aw != null)
                {
                    var tw = Json.Arr(aw, "toward");
                    double lo = tw != null && tw.Count > 1 ? Convert.ToDouble(tw[0]) : 0.0, hi = tw != null && tw.Count > 1 ? Convert.ToDouble(tw[1]) : lo;
                    double target = lo + rng.NextDouble() * (hi - lo);
                    double t = Math.Min(1.0, away / Math.Max(1.0, Json.Num(aw, "settle_minutes", 120)));
                    v += (target - v) * t;
                }
                else if (saved && !sameLaunch)
                    v += Json.Num(kv.Value, "per_minute", 0) * Math.Min(away, Json.Num(kv.Value, "away_minutes_max", 0));
                Values[kv.Key] = Mathf.Clamp01((float)v);
                levels[kv.Key] = LevelOf(kv.Key);
            }
        }

        readonly System.Random rng = new System.Random();

        // (checks) a saved record given as it would be read: a long / short closed time, the same launch, an old record
        public void ResumeForCheck(string id, Dictionary<string, object> mine) { Apply(id, mine); }
        public static double UnixNowForCheck { get { return Unix; } }

        public void Save() { Write(); }

        // written now: true when the file has it (false: not readable - left as it was - or the write failed)
        bool Write()
        {
            if (charId.Length == 0) return Alive();      // (no character yet: the app's own time still told)
            try
            {
                var all = ReadAll();
                if (all == null) return false;         // (not readable: left as it is)
                if (!appRead) TakeApp(all);
                if (!charRead)
                {
                    // her record could not be read when she was chosen: read now - hers, not the start values, are kept (GPT's
                    // re-review of #42: the start values replaced her record at the first save after the file read)
                    charRead = true;
                    var mine = Json.Obj(all, charId);
                    if (mine != null) Apply(charId, mine);
                }
                var vals = new Dictionary<string, object>();
                foreach (var kv in Values) vals[kv.Key] = Math.Round(kv.Value, 4);
                var dn = new Dictionary<string, object>();
                foreach (var kv in done) dn[kv.Key] = kv.Value;
                var pend = new List<object>();
                foreach (var pe in pending) pend.Add(new Dictionary<string, object> { { "mood", pe.Mood }, { "left", Math.Round(pe.Left, 5) }, { "rate", Math.Round(pe.Rate, 7) }, { "item", pe.Item },
                                                                      { "cap", Math.Round(pe.Cap, 4) } });
                all[charId] = new Dictionary<string, object> { { "values", vals }, { "saved", Unix }, { "launch", Launch.Id }, { "done", dn },
                    { "closed_total", Math.Round(closedTotal, 3) }, { "pending", pend } };
                all[AppKey] = AppDoc();
                if (h.Items != null && h.Items.Imported) all[ItemsKey] = h.Items.Export();
                WriteWhole(Json.Write(all));
                return true;
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: moods: " + e.Message); return false; }
        }

        public string LevelOf(string key)
        {
            float v;
            Values.TryGetValue(key, out v);
            string o = "";
            foreach (var row in Json.Arr(Json.Obj(Moods, key), "levels") ?? new List<object>())
            {
                var r = (List<object>)row;
                if (v >= Convert.ToSingle(r[0])) o = (string)r[1];
            }
            return o;
        }

        public void Add(string key, float amount, string cause)
        {
            if (!Values.ContainsKey(key) || amount == 0f) return;
            // the arousal's rise x the user's setting (発情の上がる速さ, PR #9 adopted): time, touches, plays - not its fall
            if (key == "arousal" && amount > 0f && h.ArousalSpeed != null) amount *= h.ArousalSpeed.Value;
            float v = Mathf.Clamp01(Values[key] + amount);
            Values[key] = v;
            string lv = LevelOf(key);
            if (lv == levels[key]) return;
            levels[key] = lv;
            Log.Add(new Dictionary<string, object> { { "event", "level" }, { "mood", key }, { "level", lv }, { "cause", cause } });
            h.Bridge.Event("mood_changed", new Dictionary<string, object> { { "mood", key }, { "level", lv }, { "value", Math.Round(v, 3) }, { "cause", cause }, { "moods", Info() } });
            if (h.Blocks != null) h.Blocks.DropMonologue("mood_changed");
        }

        // an urge without a mood (the shower) was met now / when it last was (local time; MinValue: never)
        public void MarkDone(string key, DateTime when)
        {
            done[key] = (when.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            Save();
        }

        public DateTime DoneAt(string key)
        {
            double u;
            return done.TryGetValue(key, out u) ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(u).ToLocalTime() : DateTime.MinValue;
        }

        // she went to the toilet (an outing): the urge to 0
        public void Relieve(string key)
        {
            float v;
            if (Values.TryGetValue(key, out v) && v > 0f) Add(key, -v, "relieved");
        }

        public void OnTouch(string area)
        {
            foreach (var kv in Moods) Add(kv.Key, (float)Json.Num(Json.Obj(kv.Value, "touch"), area, 0), "touch");
        }

        // her climax: Muse may call it satisfying for a while (MuseMood); nothing falls by itself
        public void OnClimax()
        {
            foreach (var kv in Moods) if (Json.Has(kv.Value, "satisfied")) satisfiable[kv.Key] = Now;
        }

        bool CanSatisfy(string key)
        {
            float at;
            return satisfiable.TryGetValue(key, out at) && Now - at <= Json.Num(Json.Obj(Moods, key), "satisfy_window_seconds", 600.0);
        }

        // A block's "mood" (MUSE_GUIDE.md 3.9): {key: "satisfied"} (her climax satisfied that mood), or a value of the mood's
        // muse table (relieved, leaked). Refused (block_refused): an unknown mood / value, no climax in the window, too low.
        public void MuseMood(Dictionary<string, object> d)
        {
            if (d == null) return;
            foreach (var kv in d)
            {
                string key = kv.Key, what = Convert.ToString(kv.Value);
                var m = Json.Obj(Moods, key);
                var row = Json.Obj(Json.Obj(m, "muse"), what);
                float v;
                Values.TryGetValue(key, out v);
                string why = "";
                if (m == null) why = "unknown_mood";
                else if (what == "satisfied" && Json.Has(m, "satisfied")) { if (!CanSatisfy(key)) why = "no_climax"; }
                else if (row == null) why = "unknown_value";
                else if (v < Json.Num(row, "min", 0)) why = "too_low";
                if (why.Length > 0)
                {
                    h.Bridge.Event("block_refused", new Dictionary<string, object> { { "field", "mood." + key }, { "value", what }, { "reason", why } });
                    continue;
                }
                if (what == "satisfied" && row == null)
                {
                    satisfiable.Remove(key);
                    Log.Add(new Dictionary<string, object> { { "event", "satisfied" }, { "mood", key } });
                    Add(key, (float)Json.Num(m, "satisfied", 0), "satisfied");
                    continue;
                }
                if (Json.Has(row, "set")) Add(key, (float)Json.Num(row, "set") - v, what);
                else if (Json.Has(row, "add")) Add(key, (float)Json.Num(row, "add"), what);
                Log.Add(new Dictionary<string, object> { { "event", what }, { "mood", key } });
                // the leak's look: the original's pee on her (seen while her lower body is bare)
                bool shown = Json.Str(row, "effect", "") == "leak" && HLiquid.Pee(h.Stage.Cha, (float)Json.Num(row, "seconds", 6.0));
                if (Json.Str(row, "effect", "") == "leak") h.StartCoroutine(h.Stage.Leak((float)Json.Num(row, "seconds", 6.0)));      // (her motion and the pee's sound)
                h.Bridge.Event("mood_applied", new Dictionary<string, object> { { "mood", key }, { "value", what }, { "level", levels[key] }, { "shown", shown } });
            }
        }

        public Dictionary<string, object> Info()
        {
            var o = new Dictionary<string, object>();
            foreach (var kv in Moods)
            {
                float v;
                Values.TryGetValue(kv.Key, out v);
                string lv;
                levels.TryGetValue(kv.Key, out lv);
                o[kv.Key] = new Dictionary<string, object> { { "name", Json.Str(kv.Value, "name", kv.Key) }, { "value", Math.Round(v, 3) }, { "level", lv ?? "" },
                    { "satisfiable", CanSatisfy(kv.Key) } };
            }
            return o;
        }

        // a sign of the user or Muse (a press, a key, a block shown)
        public void Acted() { idleSince = Now; }

        // (checks) as if she had been left alone that long
        public void AloneForCheck(float seconds) { idleSince = Now - seconds; }

        // (checks) no climax of hers to call satisfying
        public void ForgetClimaxForCheck() { satisfiable.Clear(); }

        // ---- items' effects (PR #46 §7): an amount flowing into a mood over its minutes ----
        public class Pending { public string Mood = "", Item = ""; public float Left, Rate, Cap = 1f; }
        readonly List<Pending> pending = new List<Pending>();
        const string ItemsKey = "_items";

        public void AddPending(string mood, float amount, float minutes, string item, float cap = 1f)
        {
            if (amount <= 0f) return;
            pending.Add(new Pending { Mood = mood, Left = amount, Rate = amount / Mathf.Max(1f, minutes * 60f), Item = item, Cap = cap });
        }

        // (Items: a block's item put back when its write failed) the effects to come as they were before it
        public int PendingCount { get { return pending.Count; } }
        public void TruncatePending(int n) { if (n >= 0 && n < pending.Count) pending.RemoveRange(n, pending.Count - n); }

        public float PendingOf(string mood) { float s = 0f; foreach (var pe in pending) if (pe.Mood == mood) s += pe.Left; return s; }

        public int PendingCountForCheck { get { return pending.Count; } }
        public void ClearPendingForCheck() { pending.Clear(); }

        // (checks) the effects come in now
        public void FlowForCheck(float seconds) { foreach (var pe in pending) pe.Rate = pe.Left / Mathf.Max(0.01f, seconds); }

        // written now (the items' ledger changed): her record and the app's, or the app's alone before a character - true when
        // the file has it
        public bool Persist() { return Write(); }

        public void Tick(float dt)
        {
            aliveT += dt;
            if (aliveT >= 30f) { aliveT = 0f; if (Values.Count == 0) Alive(); }     // (with a character: her save below says it too)
            if (Values.Count == 0) return;
            var pc = h.Play;
            foreach (var kv in Moods)
            {
                Add(kv.Key, (float)Json.Num(kv.Value, "per_minute", 0) * dt / 60f, "time");
                if (pc.Active && pc.Pace > 0) Add(kv.Key, (float)Json.Num(kv.Value, "play_per_second", 0) * pc.PaceFactor(pc.Pace) * dt, "play");
                if (pc.Active && pc.Pace > 0 && Json.Has(kv.Value, "after_play_per_second"))
                {
                    float a;
                    afterPlay.TryGetValue(kv.Key, out a);
                    afterPlay[kv.Key] = a + (float)Json.Num(kv.Value, "after_play_per_second") * pc.PaceFactor(pc.Pace) * dt;
                }
            }
            // an item's effect flowing in at its rate (PR #46 §7)
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var pe = pending[i];
                float step = Mathf.Min(pe.Left, pe.Rate * dt);
                if (step > 0f)
                {
                    // never past the item's grade cap (GPT's post-review of #65): the arousal's speed multiplies what comes in
                    // (Add) - the room under the cap measured after it, as the multiplier is now
                    float mult = pe.Mood == "arousal" && h.ArousalSpeed != null ? Mathf.Max(0f, h.ArousalSpeed.Value) : 1f;
                    float room = Values.ContainsKey(pe.Mood) ? Mathf.Max(0f, pe.Cap - Values[pe.Mood]) : 0f;
                    float give = Mathf.Min(step * mult, room);
                    if (give > 0f && mult > 0f) Add(pe.Mood, give / mult, "item");
                }
                pe.Left -= step;
                if (pe.Left <= 1e-6f) pending.RemoveAt(i);
            }
            MoodPace();
            saveT += dt;
            if (saveT >= 30f) { saveT = 0f; Save(); }
        }

        // (Muse's action masturbation) the first play of the mood trigger's list that can start here, at the spot nearest
        // her: "" or why not
        public string StartMasturbation(string key, string source)
        {
            var pc = h.Play;
            var t = Json.Obj(Json.Obj(Moods, key), "trigger");
            idleSince = Now;
            foreach (var po in Json.Arr(t, "plays") ?? new List<object>())
            {
                var row = pc.Row((string)po);
                if (row == null || !(bool)pc.Availability(row)["ok"]) continue;
                var spots = pc.Spots(row);
                if (spots.Count == 0 || pc.Begin((string)po, source, spots[0].Id).Length > 0) continue;
                MoodPlay = key;
                pc.SetPace((int)Json.Num(t, "pace", 1), source);
                Log.Add(new Dictionary<string, object> { { "event", "trigger" }, { "mood", key }, { "play", (string)po }, { "spot", spots[0].Id } });
                return "";
            }
            return "no_play";
        }

        // her own play's pace rises with her climax gauge, unless Muse set one lately
        void MoodPace()
        {
            if (MoodPlay.Length == 0) return;
            var pc = h.Play;
            var t = Json.Obj(Json.Obj(Moods, MoodPlay), "trigger");
            if (Now - pc.LastMuse < Json.Num(t, "muse_hold_seconds", 30.0) || pc.Climax.Running) return;
            int want = pc.Pace;
            foreach (var row in Json.Arr(t, "pace_by_gauge") ?? new List<object>())
            {
                var r = (List<object>)row;
                if (h.Gauge.Value >= Convert.ToSingle(r[0])) want = Math.Max(want, Convert.ToInt32(r[1]));
            }
            if (want != pc.Pace) pc.SetPace(want, "mood");
        }

        // a climax sequence ended: the mood's own play ends with her climax
        public void SequenceDone(string name)
        {
            if (MoodPlay.Length == 0 || !name.StartsWith("female")) return;
            h.Play.End("mood");
        }

        // the play ended: her own play is over; what builds up after a play is added (not after showering together)
        public void PlayEnded(bool showerTogether)
        {
            MoodPlay = "";
            foreach (var kv in new List<KeyValuePair<string, float>>(afterPlay))
                if (!showerTogether && kv.Value > 0f) Add(kv.Key, kv.Value, "after_play");
            afterPlay.Clear();
        }

        // she showered (an action): the moods it resets go to 0
        public void ActionDone(string action)
        {
            foreach (var kv in Moods)
            {
                var by = Json.Arr(kv.Value, "reset_by");
                float v;
                if (by != null && by.Contains(action) && Values.TryGetValue(kv.Key, out v) && v > 0f) Add(kv.Key, -v, "showered");
            }
        }
    }
}
