using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MuseStudio
{
    // Smooth motion changes (the Godot app's scene_controller / play_motion blends): Studio's LoadAnime ends in
    // ChaControl.AnimPlay (Animator.Play: a cut). When MuseStudio asked for a blend just before (Next), that play becomes a
    // crossfade instead - a new scene key 0.5 s, another intensity of the same scene 0.4 s from the same phase, a play's
    // state 0.25 s. Everything else Studio does (the controller, the H parameters, the sway) stays its own.
    public static class Blend
    {
        class Ask { public float Seconds; public bool Phase; }
        static readonly Dictionary<ChaControl, Ask> asks = new Dictionary<ChaControl, Ask>();
        public static int Faded;             // (checks)

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ChaControl), "AnimPlay", new[] { typeof(string) }), new HarmonyMethod(typeof(Blend), "Prefix"));
        }

        // the next play of this character's body crossfades over seconds (phase: from the playing state's phase)
        public static void Next(ChaControl cha, float seconds, bool phase = false)
        {
            if (cha != null && seconds > 0f) asks[cha] = new Ask { Seconds = seconds, Phase = phase };
        }

        static bool Prefix(ChaControl __instance, string stateName)
        {
            Ask a;
            if (__instance == null || !asks.TryGetValue(__instance, out a)) return true;
            asks.Remove(__instance);
            var an = __instance.animBody;
            if (an == null || an.runtimeAnimatorController == null) return true;
            var cur = an.GetCurrentAnimatorStateInfo(0);
            float len = Mathf.Max(cur.length, 0.05f);
            float offset = a.Phase ? Mathf.Repeat(cur.normalizedTime, 1f) : 0f;
            an.CrossFade(stateName, a.Seconds / len, 0, offset);
            Faded++;
            return false;
        }

        // the state playing, or the one it is fading into
        public static AnimatorStateInfo Now(Animator an)
        {
            return an.IsInTransition(0) ? an.GetNextAnimatorStateInfo(0) : an.GetCurrentAnimatorStateInfo(0);
        }
    }
}
