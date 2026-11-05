using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace MuseStudio
{
    // The original's own tables that MuseStudio's local data files are made from (tools/derive_data.py), read in the game from
    // the user's install (--muse-dump: game/original_lists.json beside Studio's lists). The repository holds none of them: a
    // fresh clone makes its data from its own original (the review of 2026-11-05: the data was made from the Godot app's
    // files and the KoikatsuOfGodot port, neither in the repository). Read with the game's own loaders where it has them (the
    // talk sheet: ActionGame.Communication.Info.LoadData), else the lists as the game names them (ExcelData rows); the parts
    // whose layout is the game's own (the ambience prefabs, the footstep events) are written field by field (Fields), for the
    // derive tool to read. Only numbers and names the data files use; the talk sheet's lines are not written.
    static class Originals
    {
        public static IEnumerator Write(string path)
        {
            var doc = new Dictionary<string, object>();
            var errors = new List<object>();
            Section(doc, errors, "personalities", Personalities);
            Section(doc, errors, "poses", Poses);
            Section(doc, errors, "touch", Touch);
            Section(doc, errors, "expressions", Expressions);
            Section(doc, errors, "mapinfo", MapInfo);
            Section(doc, errors, "env_clips", EnvClips);
            Section(doc, errors, "env_areas", EnvAreas);
            Section(doc, errors, "footstep_clips", FootstepClips);
            var events = new List<object>();
            yield return FootstepEvents(events, errors);
            doc["footstep_events"] = events;
            var clips = new List<object>();
            yield return PoseClips(doc, clips, errors);
            doc["pose_clips"] = clips;
            doc["errors"] = errors;
            File.WriteAllText(path, Json.Write(doc), new UTF8Encoding(false));
            MuseStudioPlugin.Log.LogMessage(string.Format("MuseStudio: dumped the original's tables -> {0} ({1} errors)", path, errors.Count));
        }

        static void Section(Dictionary<string, object> doc, List<object> errors, string name, Func<object> read)
        {
            try { doc[name] = read(); }
            catch (Exception e)
            {
                errors.Add(name + ": " + e);
                MuseStudioPlugin.Log.LogWarning("MuseStudio: originals " + name + ": " + e.Message);
            }
        }

        // ---- the lists as the game names them ----

        // the bundles under a folder in name order (the game's own order: a later bundle replaces an earlier one's rows)
        static List<string> Bundles(string folder)
        {
            var o = new List<string>(CommonLib.GetAssetBundleNameListFromPath(folder, true));
            o.Sort(StringComparer.Ordinal);
            return o;
        }

        static List<string> Assets(string bundle)
        {
            var o = new List<string>();
            foreach (var n in AssetBundleCheck.GetAllAssetName(bundle, false, null, false)) o.Add(Path.GetFileNameWithoutExtension(n));
            return o;
        }

        static ExcelData Sheet(string bundle, string asset)
        {
            var a = AssetBundleManager.LoadAsset(bundle, asset, typeof(ExcelData), null);
            return a != null ? a.GetAsset<ExcelData>() : null;
        }

        static List<object> Rows(ExcelData ex)
        {
            var o = new List<object>();
            if (ex == null) return o;
            foreach (var p in ex.list) o.Add(new List<object>(p.list.ToArray()));
            return o;
        }

        // every sheet of a folder's bundles whose asset name matches: [bundle, asset, rows]
        static List<object> Sheets(string folder, string pattern)
        {
            var o = new List<object>();
            foreach (var b in Bundles(folder))
            {
                foreach (var an in Assets(b))
                    if (System.Text.RegularExpressions.Regex.IsMatch(an, pattern))
                        o.Add(new List<object> { b, an, Rows(Sheet(b, an)) });
                AssetBundleManager.UnloadAssetBundle(b, false, null, false);
            }
            return o;
        }

        // ---- the talk ----

        static object Personalities()
        {
            var o = new Dictionary<string, object>();
            for (int no = 0; no < 100; no++)
            {
                string nm = CharaSummary.PersonalityName(no);
                if (nm.Length > 0) o[no.ToString()] = nm;
            }
            return o;
        }

        // the talk's pose tables (communication/info_*: pose_NN - name, the motion's bundle / asset / state, its IK) and how
        // often each personality's lines use each pose (the cell after the line's voice / text; counts only, no line kept)
        static object Poses()
        {
            var tables = new List<object>();
            var names = new HashSet<string>();
            var talk = new Dictionary<string, ExcelData>();
            foreach (var b in Bundles("communication/"))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(b), "^info_[0-9]*$")) continue;
                foreach (var an in Assets(b))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(an, "^pose_([0-9]*)$");
                    var c = System.Text.RegularExpressions.Regex.Match(an, "^communication_([0-9]*)$");
                    if (!m.Success && !c.Success) continue;
                    var ex = Sheet(b, an);
                    if (ex == null) continue;
                    if (m.Success)
                    {
                        tables.Add(new List<object> { int.Parse(m.Groups[1].Value), b, Rows(ex) });
                        foreach (var p in ex.list) if (p.list.Count > 0) names.Add(p.list[0]);
                    }
                    else
                    {
                        // (a personality's sheet in two bundles: the later version has more rows - that one)
                        ExcelData had;
                        if (!talk.TryGetValue(c.Groups[1].Value, out had) || had.list.Count < ex.list.Count) talk[c.Groups[1].Value] = ex;
                    }
                }
                AssetBundleManager.UnloadAssetBundle(b, false, null, false);
            }
            var use = new List<object>();
            foreach (var kv in talk)
            {
                var count = new Dictionary<string, int>();
                foreach (var p in kv.Value.list)
                    for (int k = 3; k < p.list.Count; k++)
                        if (names.Contains(p.list[k]) && p.list[k - 3] != null && p.list[k - 3].StartsWith("sound/"))
                        {
                            int n;
                            count.TryGetValue(p.list[k], out n);
                            count[p.list[k]] = n + 1;
                        }
                foreach (var c in count) use.Add(new List<object> { int.Parse(kv.Key), c.Key, c.Value });
            }
            return new Dictionary<string, object> { { "tables", tables }, { "use", use } };
        }

        // the talk's touch reactions as the game reads its sheet (Info.LoadData): personality -> stage -> touch point -> the
        // rows in order, each its sheet row and its talk's pose and expression (no line, no voice)
        static object Touch()
        {
            var o = new Dictionary<string, object>();
            // (Studio has no talk manager: the one the main game keeps - its Awake reads the talk's path lists - for the reading)
            GameObject made = null;
            if (!Singleton<Manager.Communication>.IsInstance())
            {
                made = new GameObject("MuseStudio_communication");
                made.AddComponent<Manager.Communication>();
            }
            var com = Singleton<Manager.Communication>.Instance;
            if (com != null && TalkPaths(com) <= 0)
            {
                // (its Awake left the path lists empty: read them as it would)
                var load = typeof(Manager.Communication).GetMethod("LoadInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                try { if (load != null) load.Invoke(com, null); }
                catch (Exception e) { o["_load_info"] = (e.InnerException ?? e).ToString(); }
            }
            o["_manager"] = com == null ? "none" : "talk paths " + TalkPaths(com) + (made != null ? " (made)" : "");
            // (its sheet path asks the main game's week - weekday or weekend - which Studio has not: the weekday sheet, while reading)
            var harmony = new HarmonyLib.Harmony(MuseStudioPlugin.GUID + ".originals");
            var target = HarmonyLib.AccessTools.Method(typeof(Manager.Communication), "GetTalkFilePath");
            harmony.Patch(target, new HarmonyLib.HarmonyMethod(typeof(Originals), "WeekdaySheet"));
            try { ReadTouch(o); }
            finally
            {
                harmony.Unpatch(target, HarmonyLib.HarmonyPatchType.Prefix, harmony.Id);
                if (made != null) UnityEngine.Object.Destroy(made);
            }
            return o;
        }

        static bool WeekdaySheet(Manager.Communication __instance, int _personality, ref string _bundle, ref string _file, ref bool __result)
        {
            _bundle = _file = string.Empty;
            __result = false;
            var f = typeof(Manager.Communication).GetField("dicTalkInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var d = f != null ? f.GetValue(__instance) as IDictionary : null;
            if (d == null || !d.Contains(_personality)) return false;
            var fi = d[_personality];
            _bundle = Convert.ToString(fi.GetType().GetField("assetBundle").GetValue(fi));
            _file = Convert.ToString(fi.GetType().GetField("file").GetValue(fi));
            __result = true;
            return false;
        }

        // (the manager's talk path list: private)
        static int TalkPaths(Manager.Communication com)
        {
            var f = typeof(Manager.Communication).GetField("dicTalkInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var d = f != null ? f.GetValue(com) as IDictionary : null;
            return d != null ? d.Count : -1;
        }

        static void ReadTouch(Dictionary<string, object> o)
        {
            for (int pers = 0; pers < 100; pers++)
            {
                if (CharaSummary.PersonalityName(pers).Length == 0) continue;
                ActionGame.Communication.Info info;
                try
                {
                    info = new ActionGame.Communication.Info();
                    info.LoadData(pers);
                }
                catch (Exception e)
                {
                    o[pers.ToString()] = "error: " + e;
                    continue;
                }
                var stages = new Dictionary<string, object>();
                if (info.dicTalkInfo == null) continue;
                foreach (var st in info.dicTalkInfo)
                {
                    Dictionary<int, List<ActionGame.Communication.Info.BasicInfo>> parts;
                    if (!st.Value.TryGetValue(ActionGame.Communication.Info.Group.Touch, out parts)) continue;
                    var po = new Dictionary<string, object>();
                    foreach (var part in parts)
                    {
                        var rows = new List<object>();
                        foreach (var bi in part.Value)
                        {
                            var r = new Dictionary<string, object> { { "row", bi.row }, { "type", bi.GetType().Name } };
                            var g = bi as ActionGame.Communication.Info.GenericInfo;
                            var talk = new List<object>();
                            if (g != null && g.talk != null)
                                foreach (var t in g.talk)
                                    if (t != null) talk.Add(new Dictionary<string, object> { { "pose", t.pose ?? "" }, { "facialExpression", t.facialExpression ?? "" } });
                            r["talk"] = talk;
                            rows.Add(r);
                        }
                        po[part.Key.ToString()] = rows;
                    }
                    stages[st.Key.ToString()] = po;
                }
                o[pers.ToString()] = stages;
            }
        }

        // the expression tables (etcetra/list/exp: per personality cNN - name, eyebrow, eyes, mouth, their open max, eyes look,
        // cheek, highlight, tears, blink)
        static object Expressions() { return Sheets("etcetra/list/exp/", "^c[0-9]+$"); }

        // ---- the maps' sounds ----

        static object MapInfo()
        {
            var o = new List<object>();
            foreach (var b in Bundles("map/list/mapinfo/"))
            {
                foreach (var an in Assets(b))
                {
                    var a = AssetBundleManager.LoadAsset(b, an, typeof(global::MapInfo), null);
                    var mi = a != null ? a.GetAsset<global::MapInfo>() : null;
                    if (mi == null || mi.param == null) continue;
                    foreach (var p in mi.param)
                        o.Add(new Dictionary<string, object> { { "bundle", b }, { "no", p.No }, { "name", p.MapName }, { "scene_bundle", p.AssetBundleName }, { "scene", p.AssetName } });
                }
                AssetBundleManager.UnloadAssetBundle(b, false, null, false);
            }
            return o;
        }

        // the ambience's clips (action/list/sound/se/env: the bundle's sheet names under '#clip' the lists of id, name, bundle,
        // asset) - the sheets as they are, each list with its section
        static object EnvClips()
        {
            var o = new List<object>();
            foreach (var b in Bundles("action/list/sound/se/env/"))
            {
                var rows = Rows(Sheet(b, Path.GetFileNameWithoutExtension(b)));
                // (its sections '#clip' and '#playlist': rows [id, name, the list's bundle, its asset])
                var lists = new List<object>();
                string section = "";
                foreach (List<object> r in rows)
                {
                    string c0 = r.Count > 0 ? Convert.ToString(r[0]) : "";
                    if (c0.StartsWith("#")) { section = c0; continue; }
                    if (r.Count >= 4 && Convert.ToString(r[2]).Length > 0 && Convert.ToString(r[3]).Length > 0 && section.Length > 0)
                    {
                        var sub = Sheet(Convert.ToString(r[2]), Convert.ToString(r[3]));
                        if (sub != null) lists.Add(new List<object> { section, r[2], r[3], Rows(sub) });
                    }
                }
                o.Add(new Dictionary<string, object> { { "bundle", b }, { "rows", rows }, { "lists", lists } });
                AssetBundleManager.UnloadAssetBundle(b, false, null, false);
            }
            return o;
        }

        // the maps' ambience prefabs (map/sound: EnvArea_NN, an EnvArea3D whose playlist plays its clips at its transforms)
        static object EnvAreas()
        {
            var o = new List<object>();
            foreach (var b in Bundles("map/sound/"))
            {
                foreach (var an in Assets(b))
                {
                    if (!an.StartsWith("envarea", StringComparison.OrdinalIgnoreCase)) continue;
                    var a = AssetBundleManager.LoadAsset(b, an, typeof(GameObject), null);
                    var go = a != null ? a.GetAsset<GameObject>() : null;
                    if (go == null) continue;
                    var area = go.GetComponentInChildren<ActionGame.EnvArea3D>(true);
                    o.Add(new Dictionary<string, object> { { "bundle", b }, { "name", an }, { "active", go.activeSelf },
                        { "area", area != null ? Fields(area, 4) : null } });
                }
                AssetBundleManager.UnloadAssetBundle(b, false, null, false);
            }
            return o;
        }

        // the footsteps' clips (action/list/sound/se/footstep: the bundle's sheet - id (FootStepSE), name, the list's bundle /
        // asset - and those lists: name, bundle, asset)
        static object FootstepClips()
        {
            var o = new List<object>();
            foreach (var b in Bundles("action/list/sound/se/footstep/"))
            {
                var rows = Rows(Sheet(b, Path.GetFileNameWithoutExtension(b)));
                var lists = new List<object>();
                foreach (List<object> r in rows)
                    if (r.Count >= 4 && Convert.ToString(r[2]).Length > 0 && Convert.ToString(r[3]).Length > 0)
                    {
                        var sub = Sheet(Convert.ToString(r[2]), Convert.ToString(r[3]));
                        if (sub != null) lists.Add(new List<object> { r[2], r[3], Rows(sub) });
                    }
                o.Add(new Dictionary<string, object> { { "bundle", b }, { "rows", rows }, { "lists", lists } });
                AssetBundleManager.UnloadAssetBundle(b, false, null, false);
            }
            return o;
        }

        // the clip each talk pose's state plays (the pose tables name the controller's state; Studio's list names the clip):
        // each state played on its controller - [bundle, asset, state, clip]
        static IEnumerator PoseClips(Dictionary<string, object> doc, List<object> o, List<object> errors)
        {
            var want = new SortedDictionary<string, SortedDictionary<string, bool>>(StringComparer.Ordinal);
            var poses = doc.ContainsKey("poses") ? doc["poses"] as Dictionary<string, object> : null;
            if (poses == null) yield break;
            foreach (List<object> t in (List<object>)poses["tables"])
                foreach (List<object> r in (List<object>)t[2])
                {
                    if (r.Count < 4 || Convert.ToString(r[0]) == "name" || Convert.ToString(r[1]).Length == 0) continue;
                    string key = r[1] + "|" + r[2];
                    if (!want.ContainsKey(key)) want[key] = new SortedDictionary<string, bool>(StringComparer.Ordinal);
                    want[key][Convert.ToString(r[3])] = true;
                }
            foreach (var kv in want)
            {
                GameObject go = null;
                Animator anim = null;
                var sp = kv.Key.Split('|');
                try
                {
                    var a = AssetBundleManager.LoadAsset(sp[0], sp[1], typeof(RuntimeAnimatorController), null);
                    var rac = a != null ? a.GetAsset<RuntimeAnimatorController>() : null;
                    if (rac == null) { errors.Add("pose_clips " + kv.Key + ": no controller"); continue; }
                    go = new GameObject("MuseStudio_poses");
                    anim = go.AddComponent<Animator>();
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    anim.runtimeAnimatorController = rac;
                }
                catch (Exception e) { errors.Add("pose_clips " + kv.Key + ": " + e.Message); if (go != null) UnityEngine.Object.Destroy(go); continue; }
                yield return null;
                foreach (var state in kv.Value.Keys)
                {
                    string clip = "";
                    try
                    {
                        int hash = Animator.StringToHash(state);
                        if (anim.HasState(0, hash))
                        {
                            anim.Play(hash, 0, 0f);
                            anim.Update(0f);
                            var ci = anim.GetCurrentAnimatorClipInfo(0);
                            if (ci != null && ci.Length > 0 && ci[0].clip != null) clip = ci[0].clip.name;
                        }
                    }
                    catch (Exception e) { errors.Add("pose_clips " + kv.Key + " " + state + ": " + e.Message); }
                    o.Add(new List<object> { sp[0], sp[1], state, clip });
                }
                UnityEngine.Object.Destroy(go);
            }
        }

        // the walk's footstep events: the heroine's action controller (action/animator: its FootStepBlendTreeEvent behaviours)
        static IEnumerator FootstepEvents(List<object> o, List<object> errors)
        {
            GameObject go = null;
            var found = new List<KeyValuePair<string, RuntimeAnimatorController>>();
            try
            {
                foreach (var b in Bundles("action/animator/"))
                    foreach (var an in Assets(b))
                    {
                        var a = AssetBundleManager.LoadAsset(b, an, typeof(RuntimeAnimatorController), null);
                        var rac = a != null ? a.GetAsset<RuntimeAnimatorController>() : null;
                        if (rac != null) found.Add(new KeyValuePair<string, RuntimeAnimatorController>(b + "|" + an, rac));
                    }
            }
            catch (Exception e) { errors.Add("footstep_events: " + e); }
            foreach (var kv in found)
            {
                try
                {
                    if (go != null) UnityEngine.Object.Destroy(go);
                    go = new GameObject("MuseStudio_footsteps");
                    var anim = go.AddComponent<Animator>();
                    anim.runtimeAnimatorController = kv.Value;
                }
                catch (Exception e) { errors.Add("footstep_events " + kv.Key + ": " + e.Message); continue; }
                yield return null;
                try
                {
                    var anim = go.GetComponent<Animator>();
                    var list = new List<object>();
                    // (in the controller's own order - Unity 5.6 names no state's behaviours)
                    foreach (var be in anim.GetBehaviours<StateMachineBehaviour>())
                        if (be != null && be.GetType().Name.StartsWith("FootStep"))
                            list.Add(new Dictionary<string, object> { { "class", be.GetType().Name }, { "fields", Fields(be, 3) } });
                    if (list.Count == 0) continue;
                    o.Add(new Dictionary<string, object> { { "controller", kv.Key }, { "behaviours", list } });
                }
                catch (Exception e) { errors.Add("footstep_events " + kv.Key + ": " + e.Message); }
            }
            if (go != null) UnityEngine.Object.Destroy(go);
        }

        // ---- the game's own objects, field by field (their layout is the game's: the derive tool reads them) ----

        static object Fields(object v, int depth)
        {
            if (v == null) return null;
            var t = v.GetType();
            if (v is string || t.IsPrimitive) return v;
            if (t.IsEnum) return v.ToString();
            if (v is Vector3) { var p = (Vector3)v; return new List<object> { p.x, p.y, p.z }; }
            if (v is Vector2) { var p = (Vector2)v; return new List<object> { p.x, p.y }; }
            if (v is Quaternion) { var q = (Quaternion)v; return new List<object> { q.x, q.y, q.z, q.w }; }
            if (v is Transform)
            {
                var tr = (Transform)v;
                return new Dictionary<string, object> { { "name", tr.name }, { "position", Fields(tr.position, 0) }, { "local", Fields(tr.localPosition, 0) } };
            }
            if (v is GameObject) return new Dictionary<string, object> { { "name", ((GameObject)v).name }, { "position", Fields(((GameObject)v).transform.position, 0) } };
            if (v is UnityEngine.Object && !(v is MonoBehaviour) && !(v is StateMachineBehaviour)) return new Dictionary<string, object> { { "object", ((UnityEngine.Object)v).name }, { "type", t.Name } };
            if (depth <= 0) return t.Name;
            if (v is IDictionary)
            {
                var d = new Dictionary<string, object>();
                foreach (DictionaryEntry kv in (IDictionary)v) d[Convert.ToString(kv.Key)] = Fields(kv.Value, depth - 1);
                return d;
            }
            if (v is IEnumerable)
            {
                var l = new List<object>();
                foreach (var x in (IEnumerable)v) l.Add(Fields(x, depth - 1));
                return l;
            }
            var o = new Dictionary<string, object> { { "_type", t.Name } };
            for (var tt = t; tt != null && tt != typeof(MonoBehaviour) && tt != typeof(StateMachineBehaviour) && tt != typeof(object); tt = tt.BaseType)
                foreach (var f in tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.Name.Contains("<") || o.ContainsKey(f.Name)) continue;
                    object fv;
                    try { fv = f.GetValue(v); } catch { continue; }
                    o[f.Name] = Fields(fv, depth - 1);
                }
            return o;
        }
    }
}
