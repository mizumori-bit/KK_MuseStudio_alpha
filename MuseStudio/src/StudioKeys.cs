using HarmonyLib;
using UnityEngine;

namespace MuseStudio
{
    // Studio's own shortcut keys (Studio.ShortcutKeyCtrl: Z undo / Shift+Z redo, C moves the selection to the camera's
    // target, Delete deletes it, Ctrl+S / Ctrl+D save / duplicate the scene, W E R Q J K the guide, F the focus, the number
    // keys its cameras, H the next camera, F2 its key help) are off on Muse's stage (Studio's UI hidden): they act on the
    // scene under the stage - also while one types in MuseStudio's own fields, which Studio does not know of - and the
    // original H's Z / X / C are MuseStudio's (your body: Play.YourBodyKeys). Kept: Esc (the end, with Studio's question) and
    // F11 (Studio's screenshot). With Studio's UI shown (F2) the keys are all Studio's again.
    //
    // HideAllUI (keelhauled.hideallui, when it is there) is kept off the stage too: it turns Studio's canvases on and off with
    // Space by a flag of its own, flipped each press - and Space is MuseStudio's key to go on. Its first press set off the
    // canvases MuseStudio had already set off; its second set them all on, Studio's UI over the stage (the user's finding of
    // 2026-11-07, in a match). On the stage its toggle does nothing (its flag left as it was); with Studio's UI shown (F2) it
    // works as ever.
    public static class StudioKeys
    {
        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Studio.ShortcutKeyCtrl), "Update"), new HarmonyMethod(typeof(StudioKeys), "Prefix"));
            var hideAll = AccessTools.TypeByName("HideAllUI.HideStudioUI");
            var toggle = hideAll != null ? AccessTools.Method(hideAll, "ToggleUI") : null;
            if (toggle != null) harmony.Patch(toggle, new HarmonyMethod(typeof(StudioKeys), "HideAllUiPrefix"));
            HideAllUiKept = toggle != null;
        }

        public static bool HideAllUiKept;     // (checks) HideAllUI there and kept off the stage
        public static int HideAllUiBlocked;   // (checks) its toggles that did nothing on the stage

        static bool HideAllUiPrefix()
        {
            if (!OnStage) return true;
            HideAllUiBlocked++;
            return false;
        }

        public static bool OnStage
        {
            get { var p = MuseStudioPlugin.Instance; return p != null && p.Stage != null && p.Stage.Ready && p.Stage.StudioUiHidden; }
        }

        public static int Blocked;           // (checks) frames with a key down that Studio did not get

        static bool Prefix(Studio.ShortcutKeyCtrl __instance)
        {
            if (!OnStage) return true;
            if (Input.anyKeyDown) Blocked++;
            if (Ui.Typing || !Singleton<Manager.Scene>.IsInstance() || Singleton<Manager.Scene>.Instance.AddSceneName != string.Empty) return false;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (Singleton<GameCursor>.IsInstance()) Singleton<GameCursor>.Instance.SetCursorLock(false);
                Singleton<Manager.Scene>.Instance.GameEnd(true);
            }
            else if (Input.GetKeyDown(KeyCode.F11))
            {
                var shot = Traverse.Create(__instance).Field("gameScreenShot").GetValue();
                var cap = shot != null ? shot.GetType().GetMethod("Capture", new[] { typeof(string) }) : null;
                if (cap != null) cap.Invoke(shot, new object[] { string.Empty });
            }
            return false;
        }
    }
}
