using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // A reaction made ready (#102 adopted, plan D - a trial: the owner looks at it in real sessions, it may be changed or
    // taken back): a package's standby {"on": "climax", "request", "play_serial", "count", "expires_seconds"?} - Muse
    // writes before it happens the short reaction to her next climax; the app holds it apart from the queue (one at a time:
    // a new one replaces the one held) and lets it go at the moment her gauge's climax comes (Gauge.Climaxed) if it still
    // fits - the launch and character (tagged), the play (its serial, as it was sent), the climax's count, its time - else
    // drops it (package_dropped: replaced / expired / play_changed / other_character / in_match). Nothing waits for it: the
    // climax, the gauge, her face and motion go as ever. A package being shown then is not cut (plan C is not adopted - a
    // monologue neither): the reaction goes first in the queue, right after it (in a play, or once its last block is out: in
    // place of that last line), while it still fits - checked again right before it shows (Stale: its time, the play it was
    // for, a match; the owner's decision of 2026-11-09, GPT's post-review of #105). The climax event names the request let go
    // (standby): gone into the queue - shown or not is told by package_shown / package_dropped. Its blocks are dialogue only - words, an emotion, a voice; anything else refuses the
    // whole package (block_refused field standby, not_allowed), and so does a package field other than its own few. Not in a
    // match or a replay.
    public class Standby
    {
        readonly MuseStudioPlugin h;
        Dictionary<string, object> held;
        string heldRequest = "", heldCharacter = "";
        int heldSerial, heldCount;
        float heldUntil;
        readonly HashSet<string> requests = new HashSet<string>();     // (each request taken once in a launch; a refused one may come again)
        public int FiredForCheck, DroppedForCheck;

        public const string Key = "standby";
        public const string UntilKey = "_standby_until";        // (let go: its time, for the queue)
        public const string SerialKey = "_standby_serial";      // (let go: the play it was for)

        static readonly HashSet<string> PackageFields = new HashSet<string> { "session", "turn", "situation", "blocks", "id", "launch",
            "character", "contract", Key };
        static readonly HashSet<string> BlockFields = new HashSet<string> { "type", "text", "emotion", "voice" };

        public Standby(MuseStudioPlugin host) { h = host; }

        static float Now { get { return Time.realtimeSinceStartup; } }

        public bool Holding { get { return held != null; } }

        public static bool Of(Dictionary<string, object> pkg) { return pkg != null && pkg.ContainsKey(Key) && pkg[Key] != null; }

        // a standby package from the bridge: held (true), or refused / dropped (nothing of it shows)
        public void Take(Dictionary<string, object> pkg)
        {
            if (!h.Launch.Accept(pkg)) return;                  // (another launch's or character's: dropped as ever)
            var sb = pkg[Key] as Dictionary<string, object>;
            string request = sb != null ? Json.Str(sb, "request", "") : "";
            string why = "", detail = "";
            if (sb == null) why = "not_an_object";
            else if (Json.Str(sb, "on", "") != "climax") { why = "unknown_on"; detail = Json.Str(sb, "on", ""); }
            else if (request.Length == 0 || request.Length > 200) why = "no_request";
            else if (requests.Contains(request)) why = "duplicate_request";
            else if (Json.Str(pkg, "launch", null) == null || Json.Str(pkg, "character", null) == null) why = "untargeted";
            else if (h.Match.Active || h.Match.Loading || h.Match.Replaying || h.Replay.Playing) why = "in_match";
            else if (!Json.Has(sb, "play_serial")) why = "no_basis";
            else if ((int)Json.Num(sb, "play_serial") != h.Play.Serial) { why = "stale_basis"; detail = h.Play.Serial.ToString(); }
            else if (!Json.Has(sb, "count") || (int)Json.Num(sb, "count") <= h.Gauge.Climaxes) { why = "stale_count"; detail = h.Gauge.Climaxes.ToString(); }
            else if (Json.Has(sb, "expires_seconds") && (Json.Num(sb, "expires_seconds") < 5 || Json.Num(sb, "expires_seconds") > 300)) why = "out_of_range";
            if (why.Length == 0) why = NotAllowed(pkg, out detail);
            if (why.Length > 0)
            {
                h.Bridge.Event("block_refused", new Dictionary<string, object> { { "field", Key }, { "value", request.Length > 0 ? (object)request : null },
                    { "reason", why }, { "detail", detail.Length > 0 ? (object)detail : null }, { "available", new List<object>() } });
                h.Bridge.Ack(Json.Str(pkg, "_pid"), "dropped", "standby_" + why);
                return;
            }
            requests.Add(request);
            if (held != null) Drop("replaced");
            held = pkg;
            heldRequest = request;
            heldCharacter = h.CharacterId();
            heldSerial = h.Play.Serial;
            heldCount = (int)Json.Num(sb, "count");
            heldUntil = Now + (float)Json.Num(sb, "expires_seconds", 120);
        }

        // what a standby's package and blocks may hold: dialogue only (its words, an emotion, a voice)
        static string NotAllowed(Dictionary<string, object> pkg, out string detail)
        {
            detail = "";
            foreach (var k in pkg.Keys) if (!k.StartsWith("_") && !PackageFields.Contains(k)) { detail = k; return "not_allowed"; }
            var blocks = Json.Arr(pkg, "blocks");
            if (blocks == null || blocks.Count == 0 || blocks.Count > 3) { detail = "blocks"; return "not_allowed"; }
            foreach (var o in blocks)
            {
                var b = o as Dictionary<string, object>;
                if (b == null || Json.Str(b, "type", "dialogue") != "dialogue" || Json.Str(b, "text", "").Length == 0) { detail = "type"; return "not_allowed"; }
                foreach (var k in b.Keys) if (!k.StartsWith("_") && !BlockFields.Contains(k)) { detail = k; return "not_allowed"; }
            }
            return "";
        }

        // her gauge's climax (count: which one): the held reaction let go if it fits - its request (null: none let go)
        public string Fire(int count)
        {
            if (held == null) return null;
            string why = Why(false);
            if (why.Length == 0 && count > heldCount) why = "passed";
            if (why.Length > 0) { Drop(why); return null; }
            if (count != heldCount) return null;                  // (an earlier climax: it waits for its own)
            var pkg = held;
            string request = heldRequest;
            held = null;
            pkg[UntilKey] = (double)heldUntil;
            pkg[SerialKey] = (double)heldSerial;
            FiredForCheck++;
            h.Blocks.First(pkg);
            return request;
        }

        // why the held one goes (empty: it still fits); passed: its climax went by (it is let go only at that moment)
        string Why(bool passed)
        {
            if (Now > heldUntil) return "expired";
            if (passed && h.Gauge.Climaxes >= heldCount) return "passed";
            if (h.Play.Serial != heldSerial) return "play_changed";
            if (h.CharacterId() != heldCharacter) return "other_character";
            if (h.Match.Active || h.Match.Loading || h.Match.Replaying || h.Replay.Playing) return "in_match";
            return "";
        }

        // a reaction let go and waiting in the queue, right before it shows: why it goes unshown now (empty: it still fits) -
        // its time, the play it was for, a match or a replay (its launch and character: Launch.Accept, as for every package).
        // Never passed: its climax came, which is what let it go (GPT's post-review of #105)
        public string Stale(Dictionary<string, object> pkg)
        {
            if (Now > (float)Json.Num(pkg, UntilKey, 0)) return "expired";
            if ((int)Json.Num(pkg, SerialKey, -1) != h.Play.Serial) return "play_changed";
            if (h.Match.Active || h.Match.Loading || h.Match.Replaying || h.Replay.Playing) return "in_match";
            return "";
        }

        public void Tick()
        {
            string why = held != null ? Why(true) : "";
            if (why.Length > 0) Drop(why);
        }

        void Drop(string reason)
        {
            var pkg = held;
            held = null;
            if (pkg == null) return;
            Dropped(pkg, reason);
        }

        // a standby package that will not show: package_dropped (with its request), and the bridge's delivery dropped
        public void Dropped(Dictionary<string, object> pkg, string reason)
        {
            DroppedForCheck++;
            var sb = pkg[Key] as Dictionary<string, object>;
            h.Bridge.Ack(Json.Str(pkg, "_pid"), "dropped", reason);
            h.Bridge.Event("package_dropped", new Dictionary<string, object> { { "session", Json.Str(pkg, "session") }, { "turn", (int)Json.Num(pkg, "turn") },
                { "beat", null }, { "reason", reason }, { "standby", Json.Str(sb, "request") }, { "launch", Json.Str(pkg, "launch") },
                { "character", Json.Str(pkg, "character") }, { "choices", false }, { "by", "app" } });
        }

        // the state's standby: the one held (null: none)
        public Dictionary<string, object> Info()
        {
            if (held == null) return null;
            return new Dictionary<string, object> { { "request", heldRequest }, { "on", "climax" }, { "count", heldCount }, { "play_serial", heldSerial },
                { "expires_in", Math.Max(0, Math.Round(heldUntil - Now)) } };
        }
    }
}
