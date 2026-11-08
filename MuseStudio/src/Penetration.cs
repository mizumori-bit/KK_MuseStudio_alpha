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
    //   Each step tells whether it was done (GPT's review of #100): a member missing, a call failed, the controller not ready
    //   or not running - false, and the play tries again.
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

        // the auto target (Off / Vaginal / Anal / Oral); false: no plugin, or its controller not ready (its setters then do
        // nothing; its getters: off, not running) - a target on also its controller running
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
                if ((int)at.GetValue(c, null) != target) return false;
                return target == Off || en == null || (bool)en.GetValue(c, null);
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

        static bool warned;
        static void Missing(string what)
        {
            if (!warned) MuseStudioPlugin.Log.LogWarning("MuseStudio: BetterPenetration: no " + what + " (the insertion's penis aim waits)");
            warned = true;
        }

        // one of the controller's methods with the plugin's node constraints; false: either missing (or the call failed)
        static bool Call(Component c, string method)
        {
            var plugin = NodePlugin();
            if (plugin == null) { Missing("nodeConstraintPlugin"); return false; }
            var m = c.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) { Missing(method); return false; }
            m.Invoke(c, new[] { plugin });
            return true;
        }

        // a target taken at once (not at the controller's next check), the controller ready and running; or off: the target
        // taken away and the controller stopped. false: no plugin, a member missing, a call failed, or the controller not
        // ready (try again)
        public static bool Aim(ChaControl cha, int target)
        {
            var c = Controller(cha);
            if (c == null) return false;
            try
            {
                var en = c.GetType().GetProperty("Enabled");
                if (en == null || !en.CanWrite) { Missing("Enabled"); return false; }
                if (target != Off)
                {
                    if (!Target(cha, target)) return false;          // (the target, its controller ready and running)
                    return Call(c, "CheckAutoTarget");
                }
                if (!Target(cha, Off)) return false;
                if (!Call(c, "RemoveDanConstraints")) return false;
                en.SetValue(c, false, null);
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

        // the controller aiming: ready and running (its own Enabled: its agent and targets there, the component on)
        public static bool Aiming(ChaControl cha)
        {
            var c = Controller(cha);
            var en = c != null ? c.GetType().GetProperty("Enabled") : null;
            try { return en != null && (bool)en.GetValue(c, null); } catch (Exception) { return false; }
        }

        public static int Get(ChaControl cha)
        {
            var c = Controller(cha);
            var at = c != null ? c.GetType().GetProperty("DanAutoTarget") : null;
            try { return at != null ? (int)at.GetValue(c, null) : -1; } catch (Exception) { return -1; }
        }
    }
}
