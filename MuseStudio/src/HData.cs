using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using H;
using UnityEngine;

namespace MuseStudio
{
    // The original's H data, read with the original's own code inside Studio: the position list (h/list AnimationInfo_00..09,
    // HSceneProc.CreateAllAnimationList on a bare HSceneProc - the parser touches nothing but its list array), the position's
    // camera and local move files (h/list), and the map's H points (h/common HPoint_<map> / HPoint_Add_<map>).
    public static class HData
    {
        static List<HSceneProc.AnimationListInfo>[] lists;

        public static HSceneProc.AnimationListInfo Info(int mode, int id)
        {
            if (lists == null) Load();
            if (lists == null || mode < 0 || mode >= lists.Length || lists[mode] == null) return null;
            foreach (var a in lists[mode]) if (a.id == id) return a;
            return null;
        }

        static void Load()
        {
            try
            {
                var proc = (HSceneProc)FormatterServices.GetUninitializedObject(typeof(HSceneProc));
                var f = typeof(HSceneProc).GetField("lstAnimInfo", BindingFlags.Instance | BindingFlags.NonPublic);
                var arr = new List<HSceneProc.AnimationListInfo>[10];
                f.SetValue(proc, arr);
                typeof(HSceneProc).GetMethod("CreateAllAnimationList", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(proc, null);
                lists = arr;
                int n = 0;
                foreach (var l in arr) if (l != null) n += l.Count;
                MuseStudioPlugin.Log.LogInfo("MuseStudio: the original's H positions: " + n);
            }
            catch (Exception e)
            {
                MuseStudioPlugin.Log.LogError("MuseStudio: H list: " + e);
            }
        }

        static string[,] ListFile(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            string text = GlobalMethod.LoadAllListText("h/list/", file, null);
            if (string.IsNullOrEmpty(text)) return null;
            string[,] a;
            GlobalMethod.GetListString(text, out a);
            return a;
        }

        // CameraData of an H camera file: Pos (3), Dir (3), Rot (3), Fov - one value per row (CameraControl_Ver2.CameraDataLoad)
        public static float[] Camera(string file)
        {
            var a = ListFile(file);
            if (a == null || a.GetLength(0) < 10) return null;
            var v = new float[10];
            for (int i = 0; i < 10; i++) v[i] = float.Parse(a[i, 0], System.Globalization.CultureInfo.InvariantCulture);
            return v;
        }

        // HSceneProc.LoadLocalMoveList: the position's place in the H point's frame (a few positions on special points)
        public static bool LocalMove(string file, out Vector3 pos, out Vector3 rot)
        {
            pos = rot = Vector3.zero;
            var a = ListFile(file);
            if (a == null) return false;
            for (int i = 0; i < a.GetLength(0); i++)
            {
                pos = new Vector3(P(a[i, 0]), P(a[i, 1]), P(a[i, 2]));
                rot = new Vector3(P(a[i, 3]), P(a[i, 4]), P(a[i, 5]));
            }
            return true;
        }

        static float P(string s) { return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }

        // The map's H points: the prefabs' HPointData (not placed in the scene: their transforms are read off the assets, as
        // the original places them unmoved), minus the HPointOmitObject list (the Godot app does the same).
        public static List<Spot> Points(int hmap)
        {
            var o = new List<Spot>();
            foreach (var set in new[] { new[] { "base", "HPoint_" }, new[] { "add", "HPoint_Add_" } })
            {
                List<GameObject> objs;
                try { objs = GlobalMethod.LoadAllFolder<GameObject>("h/common/", set[1] + hmap, null, null); }
                catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: H points " + set[1] + hmap + ": " + e.Message); continue; }
                if (objs == null || objs.Count == 0) continue;
                var root = objs[objs.Count - 1];
                var omit = root.GetComponent<HPointOmitObject>();
                foreach (var hp in root.GetComponentsInChildren<HPointData>(true))
                {
                    if (omit != null && omit.list != null && omit.list.Contains(hp.gameObject)) continue;
                    o.Add(new Spot { Id = set[0] + "/" + hp.name, Object = hp.name, Categories = new List<int>(hp.category ?? new int[0]),
                                     Pos = hp.transform.position, Rot = hp.transform.rotation });
                }
            }
            return o;
        }
    }

    public class Spot
    {
        public string Id = "";
        public string Object = "";
        public string Name = "";
        public List<string> Tags = new List<string>();
        public List<int> Categories = new List<int>();
        public List<string> Aliases = new List<string>();      // the ids of the points merged into it (still taken)
        public Vector3 Pos;
        public Quaternion Rot = Quaternion.identity;
        public bool Here;
    }
}
