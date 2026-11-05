using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Studio;

namespace MuseStudio
{
    // Studio's lists (maps, animation groups / categories / entries, voices) as JSON: what MuseStudio's data files name by
    // number (--muse-dump=PATH). Read once after Studio is ready.
    static class Dump
    {
        public static void Write(string path)
        {
            var info = Singleton<Info>.Instance;
            var maps = new List<object>();
            foreach (var kv in info.dicMapLoadInfo)
                maps.Add(new Dictionary<string, object> { { "no", kv.Key }, { "name", kv.Value.name }, { "bundle", kv.Value.bundlePath }, { "file", kv.Value.fileName } });
            var anime = new List<object>();
            foreach (var g in info.dicAnimeLoadInfo)
            {
                Info.GroupInfo gi;
                info.dicAGroupCategory.TryGetValue(g.Key, out gi);
                foreach (var c in g.Value)
                {
                    string cname = null;
                    if (gi != null) gi.dicCategory.TryGetValue(c.Key, out cname);
                    foreach (var n in c.Value)
                    {
                        var a = n.Value;
                        var h = a as Info.HAnimeLoadInfo;
                        anime.Add(new Dictionary<string, object> {
                            { "group", g.Key }, { "group_name", gi != null ? gi.name : null }, { "category", c.Key }, { "category_name", cname },
                            { "no", n.Key }, { "name", a.name }, { "bundle", a.bundlePath }, { "file", a.fileName }, { "clip", a.clip },
                            { "h", h != null }, { "motion", h != null && h.isMotion } });
                    }
                }
            }
            var voice = new List<object>();
            foreach (var g in info.dicVoiceLoadInfo)
            {
                Info.GroupInfo gi;
                info.dicVoiceGroupCategory.TryGetValue(g.Key, out gi);
                foreach (var c in g.Value)
                {
                    string cname = null;
                    if (gi != null) gi.dicCategory.TryGetValue(c.Key, out cname);
                    voice.Add(new Dictionary<string, object> { { "group", g.Key }, { "group_name", gi != null ? gi.name : null }, { "category", c.Key },
                        { "category_name", cname }, { "count", c.Value.Count } });
                }
            }
            var doc = new Dictionary<string, object> { { "maps", maps }, { "anime", anime }, { "voice", voice } };
            File.WriteAllText(path, Json.Write(doc), new UTF8Encoding(false));
            MuseStudioPlugin.Log.LogMessage(string.Format("MuseStudio: dumped {0} maps, {1} animations -> {2}", maps.Count, anime.Count, path));
        }
    }
}
