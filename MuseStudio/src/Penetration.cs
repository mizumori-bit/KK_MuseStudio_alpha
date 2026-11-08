using System;
using System.Reflection;
using UnityEngine;

namespace MuseStudio
{
    // The penis along her (the trial run of 2026-11-03: the mod penis went through her body): the Studio BetterPenetration
    // plugin (KK_Studio_BetterPenetration, a mod installed apart from the game) bends a body's penis onto the nearest vagina / anus / mouth of
    // another character when its controller's auto target is set - Studio's UI sets it per character, MuseStudio per play:
    // an insertion the vagina (the anus for an anal one), a mouth service the mouth, else off. No plugin: nothing.
    //   An insertion is aimed per state as the original aims (PlayCtl.AimByState, Aim): its controller checks its auto target
    //   only once a second and keeps the last target when set off (the penis bent on to it) - Aim takes a target at once, and
    //   off takes the target away and stops the controller, the penis left to the motion and the original's aim (DanLook).
    public static class Penetration
    {
        public const int Off = 0, Vaginal = 1, Anal = 2, Oral = 3;

        static ChaControl lastCha;
        static Component lastController;

        static Component Controller(ChaControl cha)
        {
            if (cha == null) return null;
            if (cha == lastCha && lastController != null) return lastController;      // (an insertion asks every frame)
            foreach (var c in cha.GetComponents<MonoBehaviour>())
                if (c != null && c.GetType().Name == "BetterPenetrationController") { lastCha = cha; lastController = c; return c; }
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

        // (the plugin's node constraints, the penis's target hangs on: KK_Studio_BetterPenetration.nodeConstraintPlugin)
        static object nodePlugin;
        static object NodePlugin()
        {
            if (nodePlugin != null) return nodePlugin;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("KK_Studio_BetterPenetration.KK_Studio_BetterPenetration");
                if (t == null) continue;
                var f = t.GetField("nodeConstraintPlugin", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) nodePlugin = f.GetValue(null);
                break;
            }
            return nodePlugin;
        }

        static void Call(Component c, string method)
        {
            var plugin = NodePlugin();
            var m = c.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m != null && plugin != null) m.Invoke(c, new[] { plugin });
        }

        // a target taken at once (not at the controller's next check), or off: the target taken away and the controller
        // stopped; false: no plugin, or its controller not ready (try again)
        public static bool Aim(ChaControl cha, int target)
        {
            var c = Controller(cha);
            if (c == null) return false;
            try
            {
                if (target != Off)
                {
                    if (!Target(cha, target)) return false;          // (on, its controller running)
                    Call(c, "CheckAutoTarget");
                    return true;
                }
                if (!Target(cha, Off)) return false;
                Call(c, "RemoveDanConstraints");
                var en = c.GetType().GetProperty("Enabled");
                if (en != null && en.CanWrite) en.SetValue(c, false, null);
                return !((MonoBehaviour)c).enabled;
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: BetterPenetration: " + e.Message); return false; }
        }

        // after a play: no target, the controller running as before the play
        public static void Let(ChaControl cha, bool enabled)
        {
            var c = Controller(cha);
            if (c == null) return;
            try
            {
                Target(cha, Off);
                Call(c, "RemoveDanConstraints");
                var en = c.GetType().GetProperty("Enabled");
                if (en != null && en.CanWrite) en.SetValue(c, enabled, null);
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: BetterPenetration: " + e.Message); }
        }

        public static bool Running(ChaControl cha) { var c = Controller(cha) as MonoBehaviour; return c != null && c.enabled; }

        public static int Get(ChaControl cha)
        {
            var c = Controller(cha);
            var at = c != null ? c.GetType().GetProperty("DanAutoTarget") : null;
            try { return at != null ? (int)at.GetValue(c, null) : -1; } catch (Exception) { return -1; }
        }
    }
}
