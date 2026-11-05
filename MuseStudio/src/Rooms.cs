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
    // the active one (its ambient light, its sky; Studio's Map then takes its root as the map). A location not kept this
    // way loads as before (Studio's own map, at the origin).
    public class Rooms
    {
        public class Room
        {
            public string Key = "";
            public int MapNo;
            public Scene Scene;
            public readonly List<GameObject> Roots = new List<GameObject>();
            public Vector3 Offset;
        }

        public const float Spacing = 400f;
        readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();
        public string Shown = "";
        public float LoadSeconds;            // (checks) the start-up loading
        public long MemBefore, MemAfter;     // (checks) the process's private memory before and after it, MB
        public readonly List<string> Failed = new List<string>();

        public bool Has(string key) { return key != null && rooms.ContainsKey(key); }

        public IEnumerable<string> Keys { get { return rooms.Keys; } }

        public Vector3 OffsetOf(string key)
        {
            Room r;
            return key != null && rooms.TryGetValue(key, out r) ? r.Offset : Vector3.zero;
        }

        public IEnumerator Load(IEnumerable<string> keys, Dictionary<string, object> locations)
        {
            float t0 = Time.realtimeSinceStartup;
            long ws;
            MemBefore = MemoryMB(out ws);
            int i = 1;
            foreach (var key in keys)
            {
                var row = Json.Obj(Json.Obj(locations, "locations"), key);
                int no = (int)Json.Num(row, "map", -1);
                Info.MapLoadInfo info;
                if (row == null || !Find(row, ref no, out info)) { Failed.Add(key + " (no map)"); continue; }
                var before = new HashSet<int>();
                for (int s = 0; s < SceneManager.sceneCount; s++) before.Add(SceneManager.GetSceneAt(s).GetHashCode());
                try { AssetBundleManager.LoadLevel(info.bundlePath, info.fileName, true, info.manifest); }
                catch (Exception e) { Failed.Add(key + " (" + e.Message + ")"); continue; }
                // the scene that came in (by its handle, GetHashCode: names may repeat), loaded
                Scene sc = new Scene();
                float w0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - w0 < 60f)
                {
                    for (int s = 0; s < SceneManager.sceneCount; s++)
                    {
                        var c = SceneManager.GetSceneAt(s);
                        if (!before.Contains(c.GetHashCode()) && c.isLoaded) sc = c;
                    }
                    if (sc.IsValid()) break;
                    yield return null;
                }
                if (!sc.IsValid()) { Failed.Add(key + " (did not load)"); continue; }
                var room = new Room { Key = key, MapNo = no, Scene = sc, Offset = new Vector3(Spacing * i, 0f, 0f) };
                foreach (var go in sc.GetRootGameObjects())
                {
                    go.transform.position += room.Offset;
                    go.SetActive(false);
                    room.Roots.Add(go);
                }
                rooms[key] = room;
                i++;
                MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: room {0} (map {1}, {2} roots) at x {3}", key, no, room.Roots.Count, room.Offset.x));
                yield return null;
            }
            LoadSeconds = Time.realtimeSinceStartup - t0;
            MemAfter = MemoryMB(out ws);
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
