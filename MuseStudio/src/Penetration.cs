using System;
using UnityEngine;

namespace MuseStudio
{
    // The penis along her (the trial run of 2026-11-03: the mod penis went through her body): the Studio BetterPenetration
    // plugin (KK_Studio_BetterPenetration, a mod installed apart from the game) bends a body's penis onto the nearest vagina / anus / mouth of
    // another character when its controller's auto target is set - Studio's UI sets it per character, MuseStudio per play:
    // an insertion the vagina (the anus for an anal one), a mouth service the mouth, else off. No plugin: nothing.
    public static class Penetration
    {
        public const int Off = 0, Vaginal = 1, Anal = 2, Oral = 3;

        static Component Controller(ChaControl cha)
        {
            if (cha == null) return null;
            foreach (var c in cha.GetComponents<MonoBehaviour>())
                if (c != null && c.GetType().Name == "BetterPenetrationController") return c;
            return null;
        }

        public static bool Available(ChaControl cha) { return Controller(cha) != null; }

        // the auto target (Off / Vaginal / Anal / Oral); false: no plugin, or its controller not ready
        public static bool Target(ChaControl cha, int target)
        {
            var c = Controller(cha);
            if (c == null) return false;
            try
            {
                var t = c.GetType();
                var en = t.GetProperty("Enabled");
                if (target != Off && en != null && en.CanWrite) en.SetValue(c, true, null);
                var at = t.GetProperty("DanAutoTarget");
                if (at == null) return false;
                at.SetValue(c, target, null);
                return (int)at.GetValue(c, null) == target;
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: BetterPenetration: " + e.Message); return false; }
        }

        public static int Get(ChaControl cha)
        {
            var c = Controller(cha);
            var at = c != null ? c.GetType().GetProperty("DanAutoTarget") : null;
            try { return at != null ? (int)at.GetValue(c, null) : -1; } catch (Exception) { return -1; }
        }
    }
}
