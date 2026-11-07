using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx;
using UnityEngine;

namespace MuseStudio
{
    // The characters (MUSE_GUIDE.md 3.5): the one on the stage is a card; the menu switches to another card (Studio's own
    // ChangeChara: she stays where she is) - character_changed. The creation is the original's character maker
    // (game/Koikatu.exe -femalemaker, the Autostart plugin; the user's body: -malemaker / -femalemaker): while it is open the
    // state's mode is "creation" and Muse's packages wait; the card the user saved there (game/UserData/chara/female, a new
    // file or one written since the maker opened) becomes the character - creation_completed with the summary and what
    // changed from the card it opened with; closed without saving: nothing. The futanari attribute is the card's own (the
    // uncensor's "show the penis" on a female body, as the original's maker sets it) or stage.json's.
    public class Chars
    {
        readonly MuseStudioPlugin h;
        public string CardPath = "";
        public bool Creating, ForUser;
        Process maker;
        DateTime openedAt;
        ChaFileControl baseline;
        bool baseFuta;
        readonly HashSet<string> before = new HashSet<string>();
        public string LastError = "";
        readonly Dictionary<string, KeyValuePair<DateTime, string>> names = new Dictionary<string, KeyValuePair<DateTime, string>>();

        public Chars(MuseStudioPlugin host) { h = host; }

        public static string Dir(bool male) { return Path.Combine(Paths.GameRootPath, Path.Combine("UserData", Path.Combine("chara", male ? "male" : "female"))); }

        // the project's card folder (キャラカード, read) next to MuseStudio
        static string ProjectCards { get { return Path.GetFullPath(Path.Combine(MuseStudioPlugin.DataDir, Path.Combine("..", Path.Combine("..", "キャラカード")))); } }

        // [path, name] of the cards: the maker's saves first, then the project's card folder
        public List<KeyValuePair<string, string>> List()
        {
            var o = new List<KeyValuePair<string, string>>();
            foreach (var dir in new[] { Dir(false), ProjectCards })
            {
                if (!Directory.Exists(dir)) continue;
                var files = Directory.GetFiles(dir, "*.png", dir == ProjectCards ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                Array.Sort(files, (x, y) => File.GetLastWriteTime(y).CompareTo(File.GetLastWriteTime(x)));
                foreach (var f in files)
                {
                    string n = NameOf(f);
                    if (n != null) o.Add(new KeyValuePair<string, string>(f, n));
                }
            }
            return o;
        }

        // [path, name] of the bodies for "your body" (the Godot app's: the default and the bodies made): a male body the
        // maker's male cards, a female one the female cards (the creation's "あなたの体" saves there)
        public List<KeyValuePair<string, string>> Bodies(bool male)
        {
            if (!male) return List();
            var o = new List<KeyValuePair<string, string>>();
            string dir = Dir(true);
            if (!Directory.Exists(dir)) return o;
            var files = Directory.GetFiles(dir, "*.png", SearchOption.TopDirectoryOnly);
            Array.Sort(files, (x, y) => File.GetLastWriteTime(y).CompareTo(File.GetLastWriteTime(x)));
            foreach (var f in files)
            {
                string n = NameOf(f, 0);
                if (n != null) o.Add(new KeyValuePair<string, string>(f, n.Length > 0 ? n : Path.GetFileNameWithoutExtension(f)));
            }
            return o;
        }

        string NameOf(string path, int sex = 1)
        {
            var t = File.GetLastWriteTime(path);
            KeyValuePair<DateTime, string> c;
            if (names.TryGetValue(path, out c) && c.Key == t) return c.Value;
            string n = null;
            try
            {
                var f = new ChaFileControl();
                if (f.LoadCharaFile(path, (byte)sex, true, true)) n = f.parameter.fullname.Trim();     // the cards of that sex only
            }
            catch (Exception) { }
            names[path] = new KeyValuePair<DateTime, string>(t, n);
            return n;
        }

        public static ChaFileControl Load(string path)
        {
            var f = new ChaFileControl();
            return f.LoadCharaFile(path, 255, true, true) ? f : null;
        }

        // ---- the futanari: the uncensor's "show the penis" on the card (KKAPI's extended data), or stage.json ----

        public static bool CardFutanari(ChaFile f)
        {
            if (f == null || f.parameter.sex == 0) return false;
            try
            {
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    if ((t = asm.GetType("ExtensibleSaveFormat.ExtendedSave")) != null) break;
                var m = t != null ? t.GetMethod("GetExtendedDataById", new[] { typeof(ChaFile), typeof(string) }) : null;
                var pd = m != null ? m.Invoke(null, new object[] { f, "com.deathweasel.bepinex.uncensorselector" }) : null;
                if (pd == null) return false;
                var data = pd.GetType().GetField("data").GetValue(pd) as Dictionary<string, object>;
                object v;
                return data != null && data.TryGetValue("DisplayPenis", out v) && v is bool && (bool)v;
            }
            catch (Exception) { return false; }
        }

        public bool Futanari
        {
            get
            {
                object v;
                var a = Json.Obj(Json.Obj(h.Stage.Cfg, "character"), "attributes");
                if (a != null && a.TryGetValue("futanari", out v) && v is bool && (bool)v) return true;
                return h.Stage.Cha != null && CardFutanari(h.Stage.Cha.chaFile);
            }
        }

        // the state's / events' character
        public Dictionary<string, object> Info()
        {
            var cha = h.Stage.Cha;
            int pers = cha != null ? cha.chaFile.parameter.personality : 0;
            // updated / uid: what the bridge's /api/characters matches its cards with (the card's unique id - Koikatsu cards
            // have none - else the name and the card file's updated time)
            string updated = CardPath.Length > 0 && File.Exists(CardPath) ? File.GetLastWriteTime(CardPath).ToString("yyyy-MM-ddTHH:mm:ss") : null;
            return new Dictionary<string, object> { { "id", h.CharacterId() }, { "name", h.Stage.Name }, { "saved", true }, { "updated", updated }, { "uid", null },
                { "personality", new Dictionary<string, object> { { "no", pers }, { "name", CharaSummary.PersonalityName(pers) } } },
                { "person", h.Personas.PersonInfo(h.CharacterId()) }, { "persona", h.Personas.Of(h.CharacterId()) },
                { "persona_source", h.Personas.PersonaSource(h.CharacterId()) },
                { "voice", null }, { "attributes", new Dictionary<string, object> { { "futanari", Futanari } } } };
        }

        // ---- switching ----

        public float SwitchedAt = -10f;           // (outfit_changed: not for a switch)

        public string Switch(string path, string source, bool tell = true)
        {
            if (!File.Exists(path)) return "no card " + path;
            if (h.Outing.Out) return "she is out (" + h.Outing.Kind + ")";
            if (h.Match.Active || h.Match.Loading) return "in a match";
            var prev = Info();
            if (h.Play.Active) h.Play.End("character");
            h.Blocks.DropPendingFor("card:" + Path.GetFileName(path));           // (what waits was for the one before - PR #24)
            h.Stage.ReleaseMuse("character", false);                             // (her face held was the one before's - PR #46 §2)
            h.Stage.StopWalk(false);
            h.Presence.HerAt = "";                 // (the new character is with him)
            h.Stage.Heroine.ChangeChara(path);
            h.Stage.TakeCardFace();                // (the new one's own face: her ordinary face from now)
            CardPath = path;
            h.CharacterCard.Value = path;          // the last character
            h.Stage.Name = h.Stage.Cha.chaFile.parameter.fullname.Trim();
            h.Stage.Heroine.treeNodeObject.textName = h.Stage.Name;
            h.Stage.Rebuilt();
            h.Stage.ApplyCamera(Json.Str(Data.Load("cameras.json"), "default", "medium"), false);
            h.Touch.Rebind();
            h.Mood.LoadFor(h.CharacterId());
            h.Presence.ResetBelief();              // a new character thinks nothing of him yet
            Clothes.Forget();
            h.Urges.Start("character_changed");    // her urges (her moods)
            SwitchedAt = Time.realtimeSinceStartup;
            if (tell) h.Bridge.Event("character_changed", new Dictionary<string, object> { { "character", Info() }, { "previous", prev }, { "source", source } });
            if (h.Launch.Phase == "character_selected") h.Launch.Tell("character_selected");     // the bridge's character (its pool) follows
            h.SendCatalog();
            return "";
        }

        // ---- the creation: the original's maker ----

        public string Open(bool forUser)
        {
            if (Creating) return "already open";
            string exe = Path.Combine(Paths.GameRootPath, "Koikatu.exe");
            if (!File.Exists(exe)) return "no Koikatu.exe in the launch folder (tools/setup.py)";
            if (h.Play.Active) h.Play.End("creation");
            bool male = forUser && MuseStudioPlugin.Instance.YourBody.Value != "female";
            string dir = Dir(male);
            Directory.CreateDirectory(dir);
            // the card it opens with (the maker's load list has it): the character's, or the user's body's
            string src = forUser ? MuseStudioPlugin.Instance.YourBodyCard.Value : CardPath;
            baseline = null;
            if (!string.IsNullOrEmpty(src) && File.Exists(src))
            {
                string inDir = Path.GetFullPath(Path.GetDirectoryName(src)).TrimEnd('\\') == Path.GetFullPath(dir).TrimEnd('\\') ? src : Path.Combine(dir, Path.GetFileName(src));
                if (inDir != src && !File.Exists(inDir)) File.Copy(src, inDir);
                baseline = Load(inDir);
            }
            baseFuta = baseline != null && CardFutanari(baseline);
            before.Clear();
            foreach (var f in Directory.GetFiles(dir, "*.png")) before.Add(f);
            try
            {
                maker = Process.Start(new ProcessStartInfo(exe, male ? "-malemaker" : "-femalemaker") { WorkingDirectory = Paths.GameRootPath, UseShellExecute = false });
            }
            catch (Exception e) { return LastError = "maker: " + e.Message; }
            openedAt = DateTime.Now;
            Creating = true;
            ForUser = forUser;
            MuseStudioPlugin.Log.LogMessage("MuseStudio: the original's maker opened (" + (forUser ? "the user's body" : "the character") + ")");
            return "";
        }

        // (checks) the maker "closed" with this card saved, without running it
        public void CloseForCheck(string saved, bool forUser)
        {
            Creating = true;
            ForUser = forUser;
            baseline = Load(CardPath);
            baseFuta = baseline != null && CardFutanari(baseline);
            before.Clear();
            Closed(saved);
        }

        public void Tick()
        {
            if (!Creating || maker == null) return;
            bool exited;
            try { exited = maker.HasExited; } catch (Exception) { exited = true; }
            if (!exited) return;
            maker = null;
            // the card saved while the maker was open (a new file, or one written since)
            string dir = Dir(ForUser && MuseStudioPlugin.Instance.YourBody.Value != "female");
            string saved = null;
            DateTime best = openedAt.AddSeconds(-1);
            foreach (var f in Directory.GetFiles(dir, "*.png"))
            {
                var t = File.GetLastWriteTime(f);
                if (t > best) { best = t; saved = f; }
            }
            Closed(saved);
        }

        void Closed(string saved)
        {
            Creating = false;
            if (saved == null) { MuseStudioPlugin.Log.LogMessage("MuseStudio: the maker closed without a saved card"); return; }
            bool isNew = !before.Contains(saved);
            if (ForUser)
            {
                // the user's body: the next play's partner (male / female by the card)
                var f = Load(saved);
                MuseStudioPlugin.Instance.YourBodyCard.Value = saved;
                if (f != null) MuseStudioPlugin.Instance.YourBody.Value = f.parameter.sex == 0 ? "male" : "female";
                h.SendCatalog();
                return;
            }
            Switch(saved, "creation", false);
            var card = h.Stage.Cha.chaFile;
            h.Bridge.Event("creation_completed", new Dictionary<string, object> { { "character", Info() }, { "new", isNew },
                { "summary", CharaSummary.Build(card as ChaFileControl ?? Load(saved), Futanari) },
                { "changed", baseline != null ? CharaSummary.Changes(baseline, Load(saved), baseFuta, Futanari) : new List<object>() } });
        }
    }
}
