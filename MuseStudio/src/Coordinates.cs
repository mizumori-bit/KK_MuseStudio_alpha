using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MuseStudio
{
    // Coordinate cards (PR #46 §3, handed over in PR #47; MUSE_GUIDE 3.5): the original's outfit cards, read only from
    // MuseStudio's own folder (game/UserData/coordinate - never the original install's: the owner's decision, for a later
    // stage of its own). The list is the "引当" side - what there is: each card's id (its path under the folder, "/"
    // between the parts), its name (the card's own, else the file's) and its contents' hash (the labels' check: a card
    // replaced under the same name). Muse reads it in the catalog (coordinates); the labels - what each is for - are the
    // bridge's (labels API), never deciding what can be worn.
    //
    // Wearing one (the user's menu, Muse's block coordinate, Muse's outing change with coordinate) is one way: checked
    // right then - she is on the stage, no play, no match, not out (but for her own change), no creation open; the card
    // there and readable - then Studio's own outfit loading (OCIChar.LoadClothesFile: the mods' data with it, as when one
    // loads an outfit in Studio). An empty slot is the outfit as made (nothing refused for it). A mod part this PC does not
    // have: the sideloader puts the original's in its place, as for a character card - worn, and the parts told
    // (replaced). Not kept over a character switch or a restart: the card's own outfit comes back (the owner's decision);
    // the character card itself is never written.
    public class Coordinates
    {
        readonly MuseStudioPlugin h;
        public readonly string Folder;
        readonly Dictionary<string, Entry> known = new Dictionary<string, Entry>();     // id -> entry (kept while the file is the same)
        DateTime scannedFolder = DateTime.MinValue;
        float nextScan;

        public class Entry
        {
            public string Id = "", Path = "", Name = "", Hash = "";
            public long Size;
            public DateTime Written;
        }

        public Coordinates(MuseStudioPlugin host, string folder)
        {
            h = host;
            Folder = folder;
        }

        public List<Entry> List { get { var o = new List<Entry>(known.Values); o.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal)); return o; } }

        public Entry Find(string id) { Entry e; return id != null && known.TryGetValue(id, out e) ? e : null; }

        // the folder read again (new, gone, changed cards); true when the list changed
        public bool Scan()
        {
            bool changed = false;
            var seen = new HashSet<string>();
            try
            {
                if (Directory.Exists(Folder))
                    foreach (var path in Directory.GetFiles(Folder, "*.png", SearchOption.AllDirectories))
                    {
                        string id = path.Substring(Folder.Length).TrimStart('\\', '/').Replace('\\', '/');
                        seen.Add(id);
                        var fi = new FileInfo(path);
                        Entry e;
                        if (known.TryGetValue(id, out e) && e.Size == fi.Length && e.Written == fi.LastWriteTimeUtc) continue;
                        e = Read(id, path, fi);
                        if (e == null) { if (known.Remove(id)) changed = true; continue; }
                        known[id] = e;
                        changed = true;
                    }
            }
            catch (Exception ex) { MuseStudioPlugin.Log.LogWarning("MuseStudio: coordinates: " + ex.Message); }
            foreach (var id in new List<string>(known.Keys))
                if (!seen.Contains(id)) { known.Remove(id); changed = true; }
            return changed;
        }

        static Entry Read(string id, string path, FileInfo fi)
        {
            try
            {
                var c = new ChaFileCoordinate();
                if (!c.LoadFile(path)) return null;              // (not an outfit card: not listed)
                return new Entry { Id = id, Path = path, Name = NameOf(c, path), Hash = Hash(path), Size = fi.Length, Written = fi.LastWriteTimeUtc };
            }
            catch (Exception) { return null; }
        }

        // the card's own name, else its file's
        static string NameOf(ChaFileCoordinate c, string path)
        {
            string name = (c.coordinateName ?? "").Trim();
            return name.Length > 0 ? name : System.IO.Path.GetFileNameWithoutExtension(path);
        }

        public static string Hash(string path)
        {
            using (var sha = new System.Security.Cryptography.SHA256Managed())
            using (var f = File.OpenRead(path))
            {
                var b = sha.ComputeHash(f);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 12; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // the folder looked at now and then: every 30 s whether its time changed, every 5 min all of it (cards in subfolders do
        // not change the folder's own time); a changed list goes to Muse in the catalog
        float nextDeep;

        public void Tick()
        {
            if (Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + 30f;
            DateTime w;
            try { w = Directory.Exists(Folder) ? Directory.GetLastWriteTimeUtc(Folder) : DateTime.MinValue; }
            catch (Exception) { return; }
            if (w == scannedFolder && Time.realtimeSinceStartup < nextDeep) return;
            scannedFolder = w;
            nextDeep = Time.realtimeSinceStartup + 300f;
            if (Scan()) h.SendCatalog();
        }

        public void Rescan() { nextScan = 0f; nextDeep = 0f; }

        // the catalog's coordinates (MUSE_GUIDE 3.5)
        public List<object> Catalog()
        {
            var o = new List<object>();
            foreach (var e in List) o.Add(new Dictionary<string, object> { { "id", e.Id }, { "name", e.Name }, { "hash", e.Hash } });
            return o;
        }

        // why it cannot be worn now ("" it can): she on the stage, no play, no match, not out (her own change aside), no creation
        public string Blocked(bool ownChange)
        {
            if (h.Stage == null || h.Stage.Heroine == null || !h.Stage.Ready) return "not_ready";
            if (h.Chars.Creating) return "creation";
            if (h.Match.Active || h.Match.Loading) return "in_match";
            if (h.Play.Active) return "in_play";
            if (h.Outing.Out && !ownChange) return "she_is_out";
            return "";
        }

        public int Worn;                    // (checks)
        public List<string> LastReplaced = new List<string>();

        // wear the card: "" done, else the reason it was not (nothing changed then)
        public string Wear(string id, string by, bool ownChange = false)
        {
            string why = Blocked(ownChange);
            if (why.Length > 0) return why;
            var e = Find(id);
            if (e == null || !File.Exists(e.Path)) { Rescan(); return "no_coordinate"; }
            var probe = new ChaFileCoordinate();
            bool readable;
            try { readable = probe.LoadFile(e.Path); } catch (Exception) { readable = false; }
            if (!readable) return "unreadable";                   // (read failed: not worn - nothing changed)
            // the card as it is now (GPT's post-review of #52): one put under the same name since the list was made - its own
            // name and hash in the list (sent again: the labels' check sees the changed contents) and in what is told
            string hashNow;
            try { hashNow = Hash(e.Path); } catch (Exception) { return "unreadable"; }
            string nameNow = NameOf(probe, e.Path);
            if (hashNow != e.Hash || nameNow != e.Name)
            {
                var fi = new FileInfo(e.Path);
                e = new Entry { Id = e.Id, Path = e.Path, Name = nameNow, Hash = hashNow, Size = fi.Length, Written = fi.LastWriteTimeUtc };
                known[e.Id] = e;
                h.SendCatalog();
            }
            var replaced = Missing(probe);
            try { h.Stage.Heroine.LoadClothesFile(e.Path); }
            catch (Exception ex)
            {
                MuseStudioPlugin.Log.LogWarning("MuseStudio: coordinates: " + id + ": " + ex.Message);
                return "unreadable";
            }
            var cha = h.Stage.Cha;
            for (int k = 0; k < cha.fileStatus.clothesState.Length; k++) cha.SetClothesState(k, 0, false);       // (dressed in it)
            Worn++;
            LastReplaced = replaced;
            h.OutfitTold("coordinate", by, new Dictionary<string, object> { { "id", e.Id }, { "name", e.Name }, { "hash", e.Hash } }, replaced);
            return "";
        }

        // the mod parts on the card this PC does not have (the sideloader's list on the card against the mods loaded): put
        // in the original's place when it is worn - told as replaced, never a reason to refuse
        static List<string> Missing(ChaFileCoordinate f)
        {
            var miss = new List<string>();
            try
            {
                Type es = null, ri = null, uar = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (es == null) es = asm.GetType("ExtensibleSaveFormat.ExtendedSave");
                    if (ri == null) ri = asm.GetType("Sideloader.AutoResolver.ResolveInfo");
                    if (uar == null) uar = asm.GetType("Sideloader.AutoResolver.UniversalAutoResolver");
                }
                if (es == null || ri == null || uar == null) return miss;
                var get = es.GetMethod("GetExtendedDataById", new[] { typeof(ChaFileCoordinate), typeof(string) });
                if (get == null) return miss;
                var pd = get.Invoke(null, new object[] { f, "com.bepis.sideloader.universalautoresolver" });
                if (pd == null) return miss;
                var data = pd.GetType().GetField("data").GetValue(pd) as Dictionary<string, object>;
                object infos;
                if (data == null || !data.TryGetValue("info", out infos) || !(infos is IEnumerable)) return miss;
                var loaded = new HashSet<string>();
                var lr = Member(null, uar, "LoadedResolutionInfo") as IEnumerable;
                if (lr != null) foreach (var x in lr) loaded.Add(Key(x));
                var de = ri.GetMethod("Deserialize", new[] { typeof(byte[]) });
                foreach (var b in (IEnumerable)infos)
                {
                    var info = b is byte[] && de != null ? de.Invoke(null, new object[] { (byte[])b }) : null;
                    if (info == null) continue;
                    if (!loaded.Contains(Key(info))) miss.Add(Convert.ToString(Member(info, ri, "GUID")) + " " + Key(info));
                }
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: coordinates: the card's mods not checked: " + e.Message); }
            return miss;
        }

        static string Key(object info)
        {
            var t = info.GetType();
            return (Convert.ToString(Member(info, t, "GUID")) ?? "").ToLowerInvariant() + "|" + Convert.ToString(Member(info, t, "CategoryNo")) + "|" + Convert.ToString(Member(info, t, "Slot"));
        }

        static object Member(object o, Type t, string name)
        {
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
            var p = t.GetProperty(name, F);
            if (p != null) return p.GetValue(o, null);
            var fl = t.GetField(name, F);
            return fl != null ? fl.GetValue(o) : null;
        }
    }
}
