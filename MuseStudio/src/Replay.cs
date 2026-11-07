using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MuseStudio
{
    // A match's record and its playback (the user's request of 2026-11-04; DECISIONS "対戦リプレイ").
    //
    // The record: a live match's packages as they were presented here, in order (the speaker's, as Muse handed them over: her
    // own and the opponent's she checked), with the match's context - both creations (hers made at the start, the opponent's
    // as it came), both names, the limits and the format - and the system's marks: the phase, the tally, the turns and the
    // result as they stood after a package began (block -1) or after one of its blocks (a declaration, a phase). Saved by
    // itself when the match has a result and when it ends (the setting 対戦の記録を残す), one JSON file a match, in the
    // MuseStudio folder's replays (beside game: kept when the game folder is removed). The guest's creation is in it too:
    // the user's to keep or pass on, as a card.
    //
    // The playback: the stage goes to the match's room and the two bodies are built from the recorded creations (all or
    // nothing, as live: hers too - the character on the stage changes for it and comes back after), and the packages go
    // through the live presentation again (the block player, the plays, a declaration's climax shown on the declaring body),
    // the marks put in place of the counting (nothing is counted or judged again). Muse has no part in it: no event goes out
    // meanwhile, Muse's packages wait at the bridge (played after), the state stays as it was with replay.playing. Next /
    // auto / speed; the broadcast view works as live. What it shows is this side's drawing, not the other side's screen.
    public class Replay
    {
        readonly MuseStudioPlugin h;
        public Replay(MuseStudioPlugin host) { h = host; }

        static float Now { get { return Time.realtimeSinceStartup; } }

        public static string Dir
        {
            get
            {
                string check;
                if (MuseStudioPlugin.Args.TryGetValue("check", out check))
                    return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(check)), "replays");
                return Path.Combine(Path.GetDirectoryName(BepInEx.Paths.GameRootPath.TrimEnd('\\', '/')), "replays");
            }
        }

        // ---- the record of a live match ----
        Dictionary<string, object> head;
        List<object> entries;
        string file;
        float t0;
        int recNo = -1;                 // the package being presented, its number in the record
        public string LastSaved = "";   // (checks) the last file written

        public bool Recording { get { return head != null; } }

        public void Begin(Match m, Dictionary<string, object> self)
        {
            head = null;
            if (!h.MatchRecord.Value || m.Replaying) return;
            head = new Dictionary<string, object> { { "kind", "metamuse_match_replay" }, { "version", 1 }, { "started", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") },
                { "room", m.Room }, { "side", m.Side }, { "format", m.Format }, { "limits", Json.Obj(m.Info(), "limits") },
                { "self", new Dictionary<string, object> { { "name", h.Stage.Name }, { "creation", Json.Str(self, "creation", null) } } },
                { "opponent", new Dictionary<string, object> { { "name", m.OpponentName }, { "creation", m.OpponentCreation } } } };
            entries = new List<object>();
            recNo = -1;
            t0 = Now;
            Directory.CreateDirectory(Dir);
            file = Path.Combine(Dir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Match.SafeName(m.OpponentName) + ".json");
        }

        // a package presented in the live match (BlockPlayer.Begin, before the match counts it): as Muse handed it over, its
        // launch / character tags and its match field left out (they belong to that moment)
        public void Package(Dictionary<string, object> pkg, string speaker)
        {
            if (head == null) return;
            recNo++;
            var copy = Json.Parse(Json.Write(pkg)) as Dictionary<string, object> ?? new Dictionary<string, object>();
            foreach (var k in new[] { "match", "launch", "character", "_launch_at", "_character_at" }) copy.Remove(k);     // (the bridge's stamps too)
            entries.Add(new Dictionary<string, object> { { "k", "package" }, { "t", Math.Round(Now - t0, 2) }, { "speaker", speaker }, { "package", copy } });
        }

        // the system's numbers after the package began (block -1) or after one of its blocks
        public void Mark(Match m, int block)
        {
            if (head == null) return;
            entries.Add(new Dictionary<string, object> { { "k", "mark" }, { "n", recNo }, { "i", block }, { "phase", m.Phase },
                { "tally", new Dictionary<string, object> { { "host", m.Tally["host"] }, { "guest", m.Tally["guest"] } } },
                { "turns", new Dictionary<string, object> { { "host", m.Turns["host"] }, { "guest", m.Turns["guest"] } } },
                { "phase_turns", m.PhaseTurns }, { "chars", m.Letters }, { "result", m.Result } });
        }

        public void Save(Match m, string ended)
        {
            if (head == null) return;
            head["result"] = m.Result;
            head["ended"] = ended;
            head["duration"] = Math.Round(Now - t0, 1);
            int n = 0;
            foreach (var e in entries) if (Json.Str(e, "k") == "package") n++;
            head["packages"] = n;
            head["saved"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            var doc = new Dictionary<string, object>(head);
            doc["entries"] = entries;
            try
            {
                File.WriteAllText(file, Json.Write(doc), new System.Text.UTF8Encoding(false));
                LastSaved = file;
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: the replay was not saved: " + e.Message); }
        }

        public void End(Match m, string reason)
        {
            Save(m, reason);
            head = null;
            entries = null;
        }

        // ---- the list (the panel's リプレイ) ----
        public class Item { public string Path, Started, Opponent, Self, Format, Result; public int Packages; }

        public List<Item> List()
        {
            var o = new List<Item>();
            if (!Directory.Exists(Dir)) return o;
            var files = new List<string>(Directory.GetFiles(Dir, "*.json"));
            files.Sort((a, b) => string.CompareOrdinal(b, a));
            foreach (var f in files)
            {
                try
                {
                    var d = Json.Parse(File.ReadAllText(f, System.Text.Encoding.UTF8)) as Dictionary<string, object>;
                    if (Json.Str(d, "kind") != "metamuse_match_replay") continue;
                    var res = Json.Obj(d, "result");
                    string side = Json.Str(d, "side", "host");
                    string self = Json.Str(Json.Obj(d, "self"), "name", ""), opp = Json.Str(Json.Obj(d, "opponent"), "name", "");
                    string r = res == null ? "結果なし" : (Json.Has(res, "draw") && res["draw"] is bool && (bool)res["draw"]) ? "引き分け"
                        : (Json.Str(res, "winner") == side ? self : opp) + "の勝ち";
                    var t = Json.Obj(res, "tally");
                    if (t != null) r += string.Format("（{0} - {1}）", (int)Json.Num(t, side == "host" ? "host" : "guest"), (int)Json.Num(t, side == "host" ? "guest" : "host"));
                    o.Add(new Item { Path = f, Started = Json.Str(d, "started", ""), Self = self, Opponent = opp, Format = Json.Str(d, "format", "sex"),
                        Result = r, Packages = (int)Json.Num(d, "packages") });
                }
                catch (Exception) { }
            }
            return o;
        }

        public bool Delete(string path)
        {
            try
            {
                if (Path.GetFullPath(Path.GetDirectoryName(path)).TrimEnd('\\') != Path.GetFullPath(Dir).TrimEnd('\\')) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception) { return false; }
        }

        // ---- the playback ----
        public bool Playing;            // (Match.Replaying)
        public bool Finished;           // all of it shown (the result stays till the user ends it)
        public bool AutoPlay = true;
        public float Speed = 1f;
        public int Count, Done;
        public string Error = "", Title = "";
        public Dictionary<string, object> Frozen;     // the state as it was (sent meanwhile, with replay.playing)
        public static readonly float[] Speeds = { 1f, 2f, 4f, 0.5f };
        Dictionary<string, object> doc;
        List<object> list;
        int playNo = -1;
        bool autoWas, holdWas, stop;

        public string CanPlay()
        {
            if (Playing) return "再生中です";
            if (h.Match.Active || h.Match.Loading) return "対戦中は再生できません";
            if (h.Outing.Out) return "彼女が出かけています";
            if (h.Chars.Creating) return "キャラメイク中です";
            if (h.Blocks.Busy || h.Blocks.Pending > 0) return "台詞の途中は再生できません";
            return "";
        }

        public string Play(string path)
        {
            string why = CanPlay();
            if (why.Length > 0) return Error = why;
            try { doc = Json.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8)) as Dictionary<string, object>; }
            catch (Exception e) { return Error = "読めません：" + e.Message; }
            if (Json.Str(doc, "kind") != "metamuse_match_replay") return Error = "リプレイのファイルではありません";
            list = Json.Arr(doc, "entries") ?? new List<object>();
            Count = 0;
            foreach (var e in list) if (Json.Str(e, "k") == "package") Count++;
            Done = 0;
            Error = "";
            Title = Path.GetFileNameWithoutExtension(path);
            h.StartCoroutine(Run());
            return "";
        }

        public void Stop() { if (Playing) stop = true; }

        IEnumerator Run()
        {
            Playing = true;
            Finished = false;
            stop = false;
            playNo = -1;
            // Muse out of it: the state as it was, no event, her packages kept at the bridge till the end
            Frozen = h.State();
            Frozen["replay"] = Info();
            h.Bridge.Mute = true;
            holdWas = h.Bridge.Hold;
            h.Bridge.Hold = true;
            autoWas = h.Blocks.Auto;
            h.Blocks.SetAuto(AutoPlay);
            h.Blocks.Pace = Speed;
            yield return h.Match.StartReplay(doc);
            int at = 0;
            while (!stop && at < list.Count)
            {
                var e = list[at] as Dictionary<string, object>;
                at++;
                if (Json.Str(e, "k") != "package") continue;
                var pkg = Json.Parse(Json.Write(Json.Obj(e, "package"))) as Dictionary<string, object>;
                if (pkg == null) continue;
                pkg["speaker"] = Json.Str(e, "speaker", "self");
                foreach (var k in new[] { "launch", "character", "_launch_at", "_character_at" }) pkg.Remove(k);   // (a record made before they were left out: they were that moment's)
                h.Blocks.Enqueue(pkg);
                while (!stop && (h.Blocks.Pending > 0 || h.Blocks.Busy)) yield return null;      // (taken and played out)
                Done++;
                Frozen["replay"] = Info();
            }
            Finished = true;
            Frozen["replay"] = Info();
            while (!stop) yield return null;
            // back as it was
            h.Blocks.Clear();
            yield return h.Match.EndReplay();
            h.Blocks.SetAuto(autoWas);
            h.Blocks.Pace = 1f;
            h.Bridge.Hold = holdWas;
            h.Bridge.Mute = false;
            Playing = false;
            Finished = false;
            Frozen = null;
            doc = null;
            list = null;
            h.Urges.Start("replay_ended");       // (what was told meanwhile went nowhere: the urges holding told again)
            h.SendCatalog();
        }

        // the marks put in place of the counting (Match in a replay): a package begun / one of its blocks
        public void AtPackage(Match m) { playNo++; Apply(m, playNo, -1); }

        public void AtBlock(Match m, int block) { Apply(m, playNo, block); }

        void Apply(Match m, int n, int block)
        {
            if (list == null) return;
            foreach (var o in list)
            {
                var e = o as Dictionary<string, object>;
                if (Json.Str(e, "k") != "mark" || (int)Json.Num(e, "n", -2) != n || (int)Json.Num(e, "i", -2) != block) continue;
                m.Phase = Json.Str(e, "phase", m.Phase);
                var t = Json.Obj(e, "tally");
                if (t != null) { m.Tally["host"] = (int)Json.Num(t, "host"); m.Tally["guest"] = (int)Json.Num(t, "guest"); }
                var tu = Json.Obj(e, "turns");
                if (tu != null) { m.Turns["host"] = (int)Json.Num(tu, "host"); m.Turns["guest"] = (int)Json.Num(tu, "guest"); }
                m.PhaseTurns = (int)Json.Num(e, "phase_turns", m.PhaseTurns);
                m.Letters = (int)Json.Num(e, "chars", m.Letters);
                m.Result = Json.Obj(e, "result");
            }
        }

        public Dictionary<string, object> Info()
        {
            if (!Playing) return null;
            return new Dictionary<string, object> { { "playing", true }, { "finished", Finished }, { "packages", Count }, { "shown", Done } };
        }
    }
}
