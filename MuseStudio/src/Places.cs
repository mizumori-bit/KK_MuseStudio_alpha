using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Studio;
using UnityEngine;

namespace MuseStudio
{
    // Every room registered at start-up (PR #46 §4.1, handed over in PR #47; MUSE_GUIDE 3.4): Studio's maps, original and mod,
    // by name only (Studio's list - quick). Their ids (D3) do not move with the mods: an original map's own number
    // ("orig:<no>"); a mod map's zipmod GUID and its number in the mod ("mod:<GUID>:<slot>", the sideloader's resolution
    // table - the number Studio gives a mod map is given again each run). The curated locations (locations.json) keep their
    // keys and their own data; every other map gets a row of its own in the stage's locations (its key: its id), so the stage,
    // the spots, the spot editor and the camera memory take it as they take a curated one.
    //
    // Its features (the H spots' kinds: floor, chair, wall ...) are taken when the room is first loaded and remembered
    // (UserData/MuseStudio/rooms.json): the game's own map whose scene is the room's (map/list/mapinfo) names the H points'
    // prefabs (h/common/HPoint_<no>). A room with none is a room of "here" only. A room found for the first time is told
    // (rooms_added), its features when they are taken (room_features). Registered is not loaded: a room comes in when one goes
    // there (Rooms.LoadLazy) - the kept rooms (the setting Rooms) are in from the start.
    public class Places
    {
        public class Entry
        {
            public string Id = "", Key = "", Name = "", Hash = "";
            public int MapNo;
            public bool Curated;
            public Dictionary<string, object> Features;       // null: not taken yet
        }

        readonly MuseStudioPlugin h;
        readonly string path;
        readonly List<Entry> all = new List<Entry>();
        readonly Dictionary<string, Entry> byKey = new Dictionary<string, Entry>(), byId = new Dictionary<string, Entry>();
        Dictionary<string, object> cache = new Dictionary<string, object>();
        Dictionary<string, int> gameByScene;                  // (read once, when first needed)
        public float RegisterSeconds;                         // (checks)
        public int AddedAtStart;                              // (checks) rooms told in rooms_added at start-up
        public readonly List<string> FeaturesTold = new List<string>();     // (checks)

        public Places(MuseStudioPlugin host, string path)
        {
            h = host;
            this.path = path;
            try
            {
                if (File.Exists(path)) cache = Json.Obj(Json.Parse(File.ReadAllText(path, Encoding.UTF8)), "rooms") ?? new Dictionary<string, object>();
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: rooms " + path + ": " + e.Message); }
        }

        public List<Entry> All { get { return new List<Entry>(all); } }
        public int Count { get { return all.Count; } }

        public Entry ByKey(string key) { Entry e; return key != null && byKey.TryGetValue(key, out e) ? e : null; }

        // a location named by its key or its id -> its key (null: no such room)
        public string KeyOf(string keyOrId)
        {
            if (string.IsNullOrEmpty(keyOrId)) return null;
            Entry e;
            if (byKey.TryGetValue(keyOrId, out e) || byId.TryGetValue(keyOrId, out e)) return e.Key;
            return null;
        }

        public string IdOf(string key) { var e = ByKey(key); return e != null ? e.Id : key; }

        // ---- at start-up: Studio's map list, the sideloader's ids, the curated rows ----
        public void Register(Dictionary<string, object> locations)
        {
            float t0 = Time.realtimeSinceStartup;
            var maps = Singleton<Info>.Instance.dicMapLoadInfo;
            var rows = Json.Obj(locations, "locations");
            // the mod maps' GUID and number in the mod (this run's number -> them)
            var mod = new Dictionary<int, KeyValuePair<string, int>>();
            try
            {
                Type uar = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (uar == null) uar = a.GetType("Sideloader.AutoResolver.UniversalAutoResolver");
                var list = uar != null ? StaticMember(uar, "LoadedStudioResolutionInfo") as System.Collections.IEnumerable : null;
                if (list != null)
                    foreach (var info in list)
                    {
                        int local = Convert.ToInt32(Member(info, "LocalSlot") ?? -1);
                        if (local < 100000000 || !maps.ContainsKey(local)) continue;        // (a resolved mod number: one per item, all kinds)
                        mod[local] = new KeyValuePair<string, int>(Convert.ToString(Member(info, "GUID")), Convert.ToInt32(Member(info, "Slot") ?? -1));
                    }
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: rooms: the sideloader's table not read: " + e.Message); }
            // the curated rows: their map (by number, a mod map by its name) -> their key
            var curated = new Dictionary<int, string>();
            if (rows != null)
                foreach (var kv in rows)
                {
                    var row = kv.Value as Dictionary<string, object>;
                    int no = (int)Json.Num(row, "map", -1);
                    Info.MapLoadInfo mi;
                    if (row != null && Rooms.Find(row, ref no, out mi) && !curated.ContainsKey(no)) curated[no] = kv.Key;
                }
            foreach (var kv in maps)
            {
                int no = kv.Key;
                string id;
                KeyValuePair<string, int> gs;
                if (no < 100000000) id = "orig:" + no;
                else if (mod.TryGetValue(no, out gs) && !string.IsNullOrEmpty(gs.Key)) id = "mod:" + gs.Key + ":" + gs.Value;
                else id = "mod:?:" + kv.Value.name;                  // (no sideloader entry: by its name - not stable over a rename)
                if (byId.ContainsKey(id)) continue;
                var e = new Entry { Id = id, MapNo = no, Name = kv.Value.name, Hash = Digest(kv.Value.bundlePath + "|" + kv.Value.fileName) };
                string ck;
                if (curated.TryGetValue(no, out ck))
                {
                    e.Key = ck;
                    e.Curated = true;
                    e.Name = Json.Str(Json.Obj(rows, ck), "name", e.Name);
                    Json.Obj(rows, ck)["id"] = id;
                }
                else
                {
                    e.Key = id;
                    // (a row of its own: the stage takes it as a curated one - its H points and stand when it is first loaded)
                    if (rows != null) rows[id] = new Dictionary<string, object> { { "name", e.Name }, { "map", no }, { "map_name", kv.Value.name }, { "id", id }, { "auto", true } };
                }
                var c = Json.Obj(cache, id);
                if (c != null && Json.Str(c, "hash", "") == e.Hash) e.Features = Json.Obj(c, "features");
                if (e.Features != null) ApplyFeatures(e, Json.Obj(rows, e.Key));
                all.Add(e);
                byId[id] = e;
                byKey[e.Key] = e;
            }
            // (a curated location whose map is not here: not registered - not a place one can go)
            RegisterSeconds = Time.realtimeSinceStartup - t0;
            // the rooms found for the first time: told (rooms_added), and kept in the file
            var added = new List<object>();
            foreach (var e in all)
                if (Json.Obj(cache, e.Id) == null) added.Add(new Dictionary<string, object> { { "id", e.Id }, { "key", e.Key }, { "name", e.Name } });
            AddedAtStart = added.Count;
            if (added.Count > 0)
            {
                h.Bridge.Event("rooms_added", new Dictionary<string, object> { { "rooms", added }, { "total", all.Count } });
                Save();
            }
            MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: rooms registered: {0} ({1} mod ids, {2} curated, {3} new) in {4:F2} s",
                all.Count, mod.Count, curated.Count, added.Count, RegisterSeconds));
        }

        // ---- a room loaded: its features taken the first time (and its row's H points and stand) ----
        public void Loaded(string key)
        {
            var e = ByKey(key);
            if (e == null) return;
            var row = Json.Obj(Json.Obj(h.Stage.Locations, "locations"), key);
            if (e.Features != null) { ApplyFeatures(e, row); return; }
            Take(e, row, true);
        }

        // every room's features taken now, without loading the rooms (their H points' prefabs): only when the user asks (the
        // settings' button) - some minutes with many rooms (PR #46 §4.1)
        public bool Scanning;
        public int ScanLeft;

        public System.Collections.IEnumerator ScanAll()
        {
            if (Scanning) yield break;
            Scanning = true;
            var todo = all.FindAll(x => x.Features == null);
            ScanLeft = todo.Count;
            float t0 = Time.realtimeSinceStartup;
            foreach (var e in todo)
            {
                Take(e, Json.Obj(Json.Obj(h.Stage.Locations, "locations"), e.Key), false);
                ScanLeft--;
                yield return null;
            }
            Save();
            Scanning = false;
            MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: rooms' features taken: {0} in {1:F1} s", todo.Count, Time.realtimeSinceStartup - t0));
        }

        void Take(Entry e, Dictionary<string, object> row, bool save)
        {
            int game = e.Curated ? (Json.Has(row, "h_map") ? (int)Json.Num(row, "h_map") : -1) : GameMapOf(e.MapNo);
            var points = game >= 0 ? HData.Points(game) : new List<Spot>();
            var places = new List<object>();
            var doc = Json.Obj(h.Play.Doc, "places");
            if (doc != null)
                foreach (var kv in doc)
                {
                    var toggles = Json.Arr(kv.Value as Dictionary<string, object>, "toggles") ?? new List<object>();
                    if (points.Exists(sp => toggles.Exists(t => sp.Categories.Contains(Convert.ToInt32(t))))) places.Add(kv.Key);
                }
            e.Features = new Dictionary<string, object> { { "game_map", game }, { "places", places }, { "spots", points.Count }, { "here_only", points.Count == 0 } };
            // (a row of its own: where she stands - an H point to stand or lie at, else the first one, else the room's origin)
            if (!e.Curated && points.Count > 0)
            {
                var stand = points.Find(sp => sp.Categories.Contains(1)) ?? points.Find(sp => sp.Categories.Contains(0)) ?? points[0];
                e.Features["stand"] = new List<object> { Math.Round(stand.Pos.x, 4), Math.Round(stand.Pos.y, 4), Math.Round(stand.Pos.z, 4), Math.Round(stand.Rot.eulerAngles.y, 2) };
            }
            ApplyFeatures(e, row);
            if (save) Save();
            FeaturesTold.Add(e.Id);
            h.Bridge.Event("room_features", new Dictionary<string, object> { { "room", h.Stage.LocationInfo(e.Key) }, { "places", places },
                { "spots", points.Count }, { "here_only", points.Count == 0 } });
            MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: room {0} features: game map {1}, {2} H points, places {3}", e.Id, game, points.Count, Json.Write(places)));
        }

        static void ApplyFeatures(Entry e, Dictionary<string, object> row)
        {
            if (e.Curated || row == null || e.Features == null) return;
            int game = (int)Json.Num(e.Features, "game_map", -1);
            if (game >= 0) row["h_map"] = game; else row.Remove("h_map");
            var stand = Json.Arr(e.Features, "stand");
            if (stand != null) row["stand"] = stand;
        }

        // the game's map (its H points' number) whose scene is this Studio map's: map/list/mapinfo, read once
        int GameMapOf(int studioNo)
        {
            Info.MapLoadInfo mi;
            if (!Singleton<Info>.Instance.dicMapLoadInfo.TryGetValue(studioNo, out mi)) return -1;
            if (gameByScene == null)
            {
                gameByScene = new Dictionary<string, int>();
                float t0 = Time.realtimeSinceStartup;
                try
                {
                    foreach (var bundle in CommonLib.GetAssetBundleNameListFromPath("map/list/mapinfo/", true))
                    {
                        UnityEngine.Object[] assets;
                        try { assets = AssetBundleManager.LoadAllAsset(bundle, typeof(MapInfo), null).GetAllAssets<UnityEngine.Object>(); }
                        catch (Exception) { continue; }
                        foreach (var asset in assets ?? new UnityEngine.Object[0])
                        {
                            var info = asset as MapInfo;
                            if (info == null || info.param == null) continue;
                            foreach (var p in info.param)
                            {
                                string k = (p.AssetBundleName + "|" + p.AssetName).ToLowerInvariant();
                                if (!gameByScene.ContainsKey(k)) gameByScene[k] = p.No;
                            }
                        }
                    }
                }
                catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: rooms: the game's map list not read: " + e.Message); }
                MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: the game's map list: {0} scenes in {1:F2} s", gameByScene.Count, Time.realtimeSinceStartup - t0));
            }
            int g;
            return gameByScene.TryGetValue((mi.bundlePath + "|" + mi.fileName).ToLowerInvariant(), out g) ? g : -1;
        }

        // ---- the catalog's rooms (MUSE_GUIDE 3.4, 7.5): every room; usual - the kept ones (いつもの部屋) ----
        public List<object> Catalog()
        {
            var o = new List<object>();
            var rooms = h.Rooms;
            foreach (var key in h.Stage.AllLocationKeys())       // (the kept rooms first: the stage's order)
            {
                var e = ByKey(key);
                if (e == null) continue;
                o.Add(new Dictionary<string, object> { { "id", e.Id }, { "key", e.Key }, { "name", e.Name }, { "hash", e.Hash },
                    { "usual", rooms.IsKept(e.Key) }, { "loaded", rooms.Has(e.Key) },
                    { "features", e.Features == null ? null : new Dictionary<string, object> { { "places", Json.Arr(e.Features, "places") ?? new List<object>() },
                        { "spots", (int)Json.Num(e.Features, "spots") }, { "here_only", Json.Num(e.Features, "spots") <= 0 } } } });
            }
            return o;
        }

        void Save()
        {
            foreach (var e in all)
                cache[e.Id] = new Dictionary<string, object> { { "name", e.Name }, { "hash", e.Hash }, { "features", e.Features } };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Json.Write(new Dictionary<string, object> { { "rooms", cache } }), new UTF8Encoding(false));
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: rooms " + path + ": " + e.Message); }
        }

        static string Digest(string s)
        {
            using (var sha = new System.Security.Cryptography.SHA256Managed())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
                var sb = new StringBuilder();
                for (int i = 0; i < 12; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
        }

        static object StaticMember(Type t, string name)
        {
            const System.Reflection.BindingFlags S = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var p = t.GetProperty(name, S);
            if (p != null) return p.GetValue(null, null);
            var f = t.GetField(name, S);
            return f != null ? f.GetValue(null) : null;
        }

        static object Member(object o, string name)
        {
            const System.Reflection.BindingFlags I = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var t = o.GetType();
            var p = t.GetProperty(name, I);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            var f = t.GetField(name, I);
            return f != null ? f.GetValue(o) : null;
        }
    }
}
