using System.Collections.Generic;
using Studio;

namespace MuseStudio
{
    // Studio's animation list at run time: a mod's motions (AnimationLoader's AL groups) carry numbers the sideloader gives
    // per run, so a play names their states by clip and the number is looked up here (the original's own keep fixed
    // numbers). A group may be named too ([group, category, "group name"]): its id then comes from the name.
    public static class StudioAnim
    {
        static readonly Dictionary<string, int> cache = new Dictionary<string, int>();

        public static int Group(int group, string name)
        {
            var info = Singleton<Info>.Instance;
            Info.GroupInfo gi;
            if (string.IsNullOrEmpty(name) || (info.dicAGroupCategory.TryGetValue(group, out gi) && gi.name == name)) return group;
            foreach (var kv in info.dicAGroupCategory) if (kv.Value.name == name) return kv.Key;
            return group;
        }

        // the state's number: a number as it is, a clip name looked up in the group's category (-1: none)
        public static int No(int group, int category, object state)
        {
            if (state is double) return (int)(double)state;
            if (state is int) return (int)state;
            string clip = state as string;
            if (clip == null) return -1;
            string key = group + "/" + category + "/" + clip;
            int no;
            if (cache.TryGetValue(key, out no)) return no;
            no = -1;
            Dictionary<int, Dictionary<int, Info.AnimeLoadInfo>> cats;
            Dictionary<int, Info.AnimeLoadInfo> nos;
            if (Singleton<Info>.Instance.dicAnimeLoadInfo.TryGetValue(group, out cats) && cats.TryGetValue(category, out nos))
                foreach (var kv in nos) if (kv.Value.clip == clip) { no = kv.Key; break; }
            cache[key] = no;
            return no;
        }
    }
}
