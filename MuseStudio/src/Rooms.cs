using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Studio;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MuseStudio
{
    // The rooms kept in the scene from the start (DECISIONS "Muse 側の資料との読み合わせ"; the user's decision 2026-11-03): the
    // locations of the setting "Rooms" (default: the original's own room, the girl's room, the bathroom, the home toilet,
    // the bedroom) are loaded once at start-up - each map's scene added beside Studio's (AssetBundleManager.LoadLevel,
    // additive), its roots moved Spacing metres apart along x - and only the room the camera is in is active (the others
    // keep their assets loaded but draw, light and run nothing). Moving between them is instant; the room's scene becomes
    // the active one (its ambient light, its sky; Studio's Map then takes its root as the map).
    //
    // The other rooms (PR #46 §4.1, handed over in PR #47; every map registered at start-up - Places) come in the same way when
    // one goes there (LoadLazy: some 0.2-1 s, 6-370 MB each, measured 2026-11-07) and go again when they are done with: past
    // the setting's number of extra rooms kept loaded (default 6), the one used longest ago that is not in use - not the room
    // shown, not hers, not her outing's (Trim) - its scene unloaded and its assets let go (the memory came back in full).
    public class Rooms
    {
        public class Room
        {
            public string Key = "";
            public int MapNo;
            public Scene Scene;
            public readonly List<GameObject> Roots = new List<GameObject>();
            public Vector3 Offset;
            public int Slot;
            public bool Lazy;                 // (not one of the setting's: loaded when one went there)
            public float Used;                // (when it was last shown or in use)
        }

        public const float Spacing = 400f;
        readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();
        readonly HashSet<int> slots = new HashSet<int>();
        readonly HashSet<string> loading = new HashSet<string>();
        public string Shown = "";
        public float LoadSeconds;            // (checks) the start-up loading
        public long MemBefore, MemAfter;     // (checks) the process's private memory before and after it, MB
        public readonly List<string> Failed = new List<string>();
        public int Unloaded;                 // (checks) lazy rooms let go
        public float LastLazySeconds;        // (checks) the last lazy room's loading

        public bool Has(string key) { return key != null && rooms.ContainsKey(key); }

        public bool HasMap(int no) { foreach (var r in rooms.Values) if (r.MapNo == no) return true; return false; }

        public bool IsKept(string key) { Room r; return key != null && rooms.TryGetValue(key, out r) && !r.Lazy; }

        public bool Loading(string key) { return key != null && loading.Contains(key); }

        public IEnumerable<string> Keys { get { foreach (var r in rooms.Values) if (!r.Lazy) yield return r.Key; } }

        public IEnumerable<string> LazyKeys { get { foreach (var r in rooms.Values) if (r.Lazy) yield return r.Key; } }

        public Vector3 OffsetOf(string key)
        {
            Room r;
            return key != null && rooms.TryGetValue(key, out r) ? r.Offset : Vector3.zero;
        }

        public void Touch(string key) { Room r; if (key != null && rooms.TryGetValue(key, out r)) r.Used = Time.realtimeSinceStartup; }

        public IEnumerator Load(IEnumerable<string> keys, Dictionary<string, object> locations)
        {
            float t0 = Time.realtimeSinceStartup;
            long ws;
            MemBefore = MemoryMB(out ws);
            foreach (var key in keys)
            {
                var row = Json.Obj(Json.Obj(locations, "locations"), key);
                if (row == null) { Failed.Add(key + " (no map)"); continue; }
                yield return LoadOne(key, row, false);
            }
            LoadSeconds = Time.realtimeSinceStartup - t0;
            MemAfter = MemoryMB(out ws);
        }

        // a room not loaded yet, now (another loading of the same room: waited for)
        public IEnumerator LoadLazy(string key, Dictionary<string, object> row)
        {
            if (Has(key)) yield break;
            if (loading.Contains(key)) { while (loading.Contains(key)) yield return null; yield break; }
            float t0 = Time.realtimeSinceStartup;
            yield return LoadOne(key, row, true);
            LastLazySeconds = Time.realtimeSinceStartup - t0;
        }

        // one room's scene added beside Studio's (AssetBundleManager.LoadLevel, additive), its roots moved to its slot's place
        // and kept off until it is shown; then its features taken (Places.Loaded)
        IEnumerator LoadOne(string key, Dictionary<string, object> row, bool lazy)
        {
            int no = (int)Json.Num(row, "map", -1);
            Info.MapLoadInfo info;
            if (!Find(row, ref no, out info)) { Failed.Add(key + " (no map)"); yield break; }
            loading.Add(key);
            var before = new HashSet<int>();
            for (int s = 0; s < SceneManager.sceneCount; s++) before.Add(SceneManager.GetSceneAt(s).GetHashCode());
            bool started = true;
            try { AssetBundleManager.LoadLevel(info.bundlePath, info.fileName, true, info.manifest); }
            catch (Exception e) { Failed.Add(key + " (" + e.Message + ")"); started = false; }
            // the scene that came in (by its handle, GetHashCode: names may repeat), loaded
            Scene sc = new Scene();
            float w0 = Time.realtimeSinceStartup;
            while (started && Time.realtimeSinceStartup - w0 < 60f)
            {
                for (int s = 0; s < SceneManager.sceneCount; s++)
                {
                    var c = SceneManager.GetSceneAt(s);
                    if (!before.Contains(c.GetHashCode()) && c.isLoaded) sc = c;
                }
                if (sc.IsValid()) break;
                yield return null;
            }
            if (!sc.IsValid())
            {
                if (started) Failed.Add(key + " (did not load)");
                loading.Remove(key);
                yield break;
            }
            int slot = 1;
            while (slots.Contains(slot)) slot++;
            slots.Add(slot);
            var room = new Room { Key = key, MapNo = no, Scene = sc, Offset = new Vector3(Spacing * slot, 0f, 0f), Slot = slot, Lazy = lazy, Used = Time.realtimeSinceStartup };
            foreach (var go in sc.GetRootGameObjects())
            {
                go.transform.position += room.Offset;
                go.SetActive(false);
                room.Roots.Add(go);
            }
            rooms[key] = room;
            loading.Remove(key);
            MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: room {0} (map {1}, {2} roots) at x {3}{4}", key, no, room.Roots.Count, room.Offset.x, lazy ? " (lazy)" : ""));
            if (MuseStudioPlugin.Instance.Places != null) MuseStudioPlugin.Instance.Places.Loaded(key);
            yield return null;
        }

        // past the number of extra rooms kept loaded: the ones used longest ago let go - never one in use (keep: the room shown,
        // hers, her outing's)
        public void Trim(int limit, ICollection<string> keep)
        {
            var lazy = new List<Room>();
            foreach (var r in rooms.Values) if (r.Lazy && !keep.Contains(r.Key) && r.Key != Shown) lazy.Add(r);
            int inUse = 0;
            foreach (var r in rooms.Values) if (r.Lazy && (keep.Contains(r.Key) || r.Key == Shown)) inUse++;
            lazy.Sort((a, c) => a.Used.CompareTo(c.Used));
            int over = inUse + lazy.Count - Mathf.Max(0, limit);
            for (int i = 0; i < over && i < lazy.Count; i++) MuseStudioPlugin.Instance.StartCoroutine(Unload(lazy[i]));
        }

        IEnumerator Unload(Room r)
        {
            rooms.Remove(r.Key);
            slots.Remove(r.Slot);
            foreach (var go in r.Roots) if (go != null) go.SetActive(false);
            if (r.Scene.IsValid() && r.Scene.isLoaded)
            {
                var op = SceneManager.UnloadSceneAsync(r.Scene);
                while (op != null && !op.isDone) yield return null;
            }
            var un = Resources.UnloadUnusedAssets();
            while (un != null && !un.isDone) yield return null;
            Unloaded++;
            MuseStudioPlugin.Log.LogInfo("MuseStudio: room " + r.Key + " let go (lazy)");
        }

        // the process's private memory and working set, MB (Mono's Process gives 0 here)
        [StructLayout(LayoutKind.Sequential)]
        struct MemCounters
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool K32GetProcessMemoryInfo(IntPtr process, out MemCounters counters, uint cb);

        public static long MemoryMB(out long workingSet)
        {
            workingSet = 0;
            try
            {
                MemCounters c;
                if (!K32GetProcessMemoryInfo(GetCurrentProcess(), out c, (uint)Marshal.SizeOf(typeof(MemCounters)))) return 0;
                workingSet = (long)c.WorkingSetSize.ToUInt64() / (1024 * 1024);
                return (long)c.PrivateUsage.ToUInt64() / (1024 * 1024);
            }
            catch { return 0; }
        }

        // a location's map: by its number, else (a mod map, whose number the sideloader gives per run) by its name
        public static bool Find(Dictionary<string, object> row, ref int no, out Info.MapLoadInfo info)
        {
            var maps = Singleton<Info>.Instance.dicMapLoadInfo;
            string name = Json.Str(row, "map_name", "");
            if (maps.TryGetValue(no, out info) && (name.Length == 0 || info.name == name)) return true;
            foreach (var kv in maps)
                if (name.Length > 0 && kv.Value.name == name) { no = kv.Key; info = kv.Value; return true; }
            info = null;
            return false;
        }

        // the camera's room: that one active (its scene the active one), the others off; null: all off
        // a transform of the room shown (its walls and furniture: the kept rooms are scenes of their own, not under Studio's map)
        public bool InShown(Transform t)
        {
            Room r;
            if (t == null || !rooms.TryGetValue(Shown, out r)) return false;
            foreach (var go in r.Roots) if (go != null && t.IsChildOf(go.transform)) return true;
            return false;
        }

        public void Show(string key)
        {
            foreach (var r in rooms.Values)
            {
                bool on = r.Key == key;
                foreach (var go in r.Roots) if (go != null && go.activeSelf != on) go.SetActive(on);
                if (on && r.Scene.IsValid() && r.Scene.isLoaded) SceneManager.SetActiveScene(r.Scene);
            }
            Shown = key ?? "";
        }
    }
}
