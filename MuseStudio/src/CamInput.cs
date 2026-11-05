using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace MuseStudio
{
    // The camera's controls, the user's to set (the user's request of 2026-11-04; DECISIONS "カメラ操作のカスタマイズ"):
    // Studio's own camera (Studio.CameraControl: the left drag turns, the right drag moves up / down and nearer / farther, the
    // middle drag moves across, both buttons move flat, the wheel zooms; the keys: the arrows, Page Up / Down, Home / End,
    // the number pad, . \ = ] / ; and A) taken over on Muse's stage by the same moves with the settings' choices: what each
    // button's drag does (turn / across / up-down and near-far / zoom / nothing), the sensitivities (turning, moving, the
    // wheel), turning and the wheel reversed, and each key (BepInEx's key settings: any key, with modifiers). The defaults
    // are Studio's own, so nothing changes until the user changes it. With Studio's UI shown (F2) Studio's own controls
    // are back.
    // At the partner's eyes in a play (目線へ) the eyes stay: a turning drag and the turn keys turn the head (PlayCtl.PovTurn,
    // within limits), the turn's reset key looks straight at her again; moving, zooming and the wheel do nothing there.
    public static class CamInput
    {
        public static readonly string[] Drags = { "rotate", "pan", "dolly", "zoom", "none" };

        static AccessTools.FieldRef<Studio.CameraControl, Studio.CameraControl.CameraData> cameraData, cameraReset;
        static AccessTools.FieldRef<Studio.CameraControl, bool> dragging;
        static AccessTools.FieldRef<Studio.CameraControl, float> rateAddSpeed;

        public static void Patch(Harmony harmony)
        {
            cameraData = AccessTools.FieldRefAccess<Studio.CameraControl, Studio.CameraControl.CameraData>("cameraData");
            cameraReset = AccessTools.FieldRefAccess<Studio.CameraControl, Studio.CameraControl.CameraData>("cameraReset");
            dragging = AccessTools.FieldRefAccess<Studio.CameraControl, bool>("dragging");
            rateAddSpeed = AccessTools.FieldRefAccess<Studio.CameraControl, float>("rateAddSpeed");
            var t = typeof(Studio.CameraControl);
            harmony.Patch(AccessTools.Method(t, "InputMouseProc"), new HarmonyMethod(typeof(CamInput), "Mouse"));
            harmony.Patch(AccessTools.Method(t, "InputMouseWheelZoomProc"), new HarmonyMethod(typeof(CamInput), "Wheel"));
            harmony.Patch(AccessTools.Method(t, "InputKeyProc"), new HarmonyMethod(typeof(CamInput), "Keys"));
        }

        public static string LastDrag = "";       // (checks) what the last drag did
        public static float UserMovedAt = -1f;    // when the user's own drag / wheel / keys last moved the free camera (CameraMemory)
        public static string UserMovedIn = "";    // the room it was in then

        static void UserMoved()
        {
            UserMovedAt = Time.realtimeSinceStartup;
            var h = MuseStudioPlugin.Instance;
            UserMovedIn = h != null && h.Stage != null ? h.Stage.Location ?? "" : "";
        }
        public static int WheelSeen;              // (checks) frames the wheel turned
        public static float WheelForCheck;        // (checks) a turn of the wheel given once (when the real one does not reach Unity)

        static string DragOf(ConfigEntry<string> e) { string v = e.Value; return System.Array.IndexOf(Drags, v) >= 0 ? v : "none"; }

        static bool Pov { get { var pl = MuseStudioPlugin.Instance.Play; return pl != null && pl.Active && pl.View == "pov"; } }

        static void Move(Studio.CameraControl cc, Studio.CameraControl.CameraData cd, Vector3 local)
        {
            if (cc.transBase != null) cd.pos += cc.transBase.InverseTransformDirection(cc.transform.TransformDirection(local));
            else cd.pos += cc.transform.TransformDirection(local);
        }

        static void Turn(Studio.CameraControl.CameraData cd, Vector3 by)
        {
            cd.rotate.y = (cd.rotate.y + by.y) % 360f;
            cd.rotate.x = (cd.rotate.x + by.x) % 360f;
            cd.rotate.z = (cd.rotate.z + by.z) % 360f;
        }

        static bool Mouse(Studio.CameraControl __instance, ref bool __result)
        {
            if (!StudioKeys.OnStage) return true;
            var h = MuseStudioPlugin.Instance;
            var cd = cameraData(__instance);
            float ax = Input.GetAxis("Mouse X"), ay = Input.GetAxis("Mouse Y");
            ref bool drag = ref dragging(__instance);
            if ((!EventSystem.current || !EventSystem.current.IsPointerOverGameObject()) && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2)))
                drag = true;
            else if (!Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2))
                drag = false;
            __result = false;
            if (!drag) return false;
            bool l = Input.GetMouseButton(0), r = Input.GetMouseButton(1), m = Input.GetMouseButton(2);
            string act = l && r ? (h.CamBothMove.Value ? "flat" : "none") : l ? DragOf(h.CamLeftDrag) : r ? DragOf(h.CamRightDrag) : m ? DragOf(h.CamMiddleDrag) : "none";
            float rate = rateAddSpeed(__instance), mv = __instance.moveSpeed * rate * h.CamMoveSpeed.Value, rot = rate * h.CamTurnSpeed.Value;
            float sx = h.CamInvertX.Value ? -1f : 1f, sy = h.CamInvertY.Value ? -1f : 1f;
            if (Pov)
            {
                if (act == "rotate" && (ax != 0f || ay != 0f))
                {
                    h.Play.PovTurn(ax * __instance.xRotSpeed * rot * sx, -ay * __instance.yRotSpeed * rot * sy);
                    LastDrag = "pov_turn";
                }
                return false;
            }
            switch (act)
            {
                case "rotate":       // (Studio's left drag)
                    Turn(cd, new Vector3(-ay * __instance.yRotSpeed * rot * sy, ax * __instance.xRotSpeed * rot * sx, 0f));
                    break;
                case "dolly":        // (Studio's right drag: up / down, nearer / farther)
                    cd.pos.y += ay * mv;
                    cd.distance.z = Mathf.Min(0f, cd.distance.z - ax * mv);
                    break;
                case "pan":          // (Studio's middle drag: across the view)
                    Move(__instance, cd, new Vector3(ax * mv, ay * mv, 0f));
                    break;
                case "zoom":         // (a drag up / down: nearer / farther)
                    cd.distance.z = Mathf.Min(0f, cd.distance.z + ay * mv * 2f);
                    break;
                case "flat":         // (Studio's both buttons: flat, across and forward)
                    Move(__instance, cd, new Vector3(ax * mv, 0f, ay * mv));
                    break;
                default:
                    return false;
            }
            if (ax != 0f || ay != 0f) { LastDrag = act; UserMoved(); }
            __result = true;
            return false;
        }

        static bool Wheel(Studio.CameraControl __instance, ref bool __result)
        {
            if (!StudioKeys.OnStage) return true;
            var h = MuseStudioPlugin.Instance;
            float axis = Input.GetAxis("Mouse ScrollWheel") + WheelForCheck;
            WheelForCheck = 0f;
            if (axis != 0f) WheelSeen++;
            float num = axis * __instance.zoomSpeed * h.CamZoomSpeed.Value * (h.CamInvertWheel.Value ? -1f : 1f);
            __result = false;
            if (num == 0f || Pov) return false;
            var cd = cameraData(__instance);
            cd.distance.z = Mathf.Min(0f, cd.distance.z + num);
            UserMoved();
            __result = true;
            return false;
        }

        static bool Keys(Studio.CameraControl __instance, ref bool __result)
        {
            if (!StudioKeys.OnStage) return true;
            __result = false;
            if (Ui.Typing) return false;
            var h = MuseStudioPlugin.Instance;
            var k = h.CamKeys;
            var cd = cameraData(__instance);
            if (Pov)
            {
                float pn = 10f * Time.deltaTime * h.CamTurnSpeed.Value, psx = h.CamInvertX.Value ? -1f : 1f, psy = h.CamInvertY.Value ? -1f : 1f;
                if (k["turn_reset"].Value.IsDown() || k["reset"].Value.IsDown()) { h.Play.PovYaw = h.Play.PovPitch = 0f; }
                float py = 0f, pp = 0f;
                if (k["turn_left"].Value.IsPressed()) py -= pn * __instance.xRotSpeed * psx;
                else if (k["turn_right"].Value.IsPressed()) py += pn * __instance.xRotSpeed * psx;
                if (k["turn_up"].Value.IsPressed()) pp -= pn * __instance.yRotSpeed * psy;
                else if (k["turn_down"].Value.IsPressed()) pp += pn * __instance.yRotSpeed * psy;
                if (py != 0f || pp != 0f) h.Play.PovTurn(py, pp);
                return false;
            }
            bool moved = false;
            if (k["reset"].Value.IsDown()) { __instance.Reset(0); moved = true; }
            else if (k["turn_reset"].Value.IsDown()) { var rs = cameraReset(__instance); cd.rotate.x = rs.rotate.x; cd.rotate.y = rs.rotate.y; moved = true; }
            else if (k["roll_reset"].Value.IsDown()) { cd.rotate.z = 0f; moved = true; }
            else if (k["fov_reset"].Value.IsDown()) { __instance.fieldOfView = cameraReset(__instance).parse; moved = true; }
            float d = Time.deltaTime * h.CamMoveSpeed.Value;
            if (k["zoom_in"].Value.IsPressed()) { moved = true; cd.distance.z = Mathf.Min(0f, cd.distance.z + d); }
            else if (k["zoom_out"].Value.IsPressed()) { moved = true; cd.distance.z -= d; }
            if (k["move_right"].Value.IsPressed()) { moved = true; Move(__instance, cd, new Vector3(d, 0f, 0f)); }
            else if (k["move_left"].Value.IsPressed()) { moved = true; Move(__instance, cd, new Vector3(-d, 0f, 0f)); }
            if (k["move_forward"].Value.IsPressed()) { moved = true; Move(__instance, cd, new Vector3(0f, 0f, d)); }
            else if (k["move_back"].Value.IsPressed()) { moved = true; Move(__instance, cd, new Vector3(0f, 0f, -d)); }
            if (k["move_up"].Value.IsPressed()) { moved = true; cd.pos.y += d; }
            else if (k["move_down"].Value.IsPressed()) { moved = true; cd.pos.y -= d; }
            float num = 10f * Time.deltaTime * h.CamTurnSpeed.Value;
            float sx = h.CamInvertX.Value ? -1f : 1f, sy = h.CamInvertY.Value ? -1f : 1f;
            var turn = Vector3.zero;
            if (k["roll_left"].Value.IsPressed()) { moved = true; turn.z += num; }
            else if (k["roll_right"].Value.IsPressed()) { moved = true; turn.z -= num; }
            if (k["turn_down"].Value.IsPressed()) { moved = true; turn.x -= num * __instance.yRotSpeed * sy; }
            else if (k["turn_up"].Value.IsPressed()) { moved = true; turn.x += num * __instance.yRotSpeed * sy; }
            if (k["turn_left"].Value.IsPressed()) { moved = true; turn.y += num * __instance.xRotSpeed * sx; }
            else if (k["turn_right"].Value.IsPressed()) { moved = true; turn.y -= num * __instance.xRotSpeed * sx; }
            if (moved) Turn(cd, turn);
            float dt = Time.deltaTime;
            if (k["fov_in"].Value.IsPressed()) { moved = true; __instance.fieldOfView = Mathf.Max(cd.parse - dt * 15f, 1f); }
            else if (k["fov_out"].Value.IsPressed()) { moved = true; __instance.fieldOfView = Mathf.Min(cd.parse + dt * 15f, __instance.limitFov); }
            if (moved) UserMoved();
            __result = moved;
            return false;
        }

        // the keys' settings: name -> (the setting's name, Studio's key)
        public static readonly string[][] KeyRows = {
            new[] { "turn_left", "回転：左", "Keypad4" }, new[] { "turn_right", "回転：右", "Keypad6" }, new[] { "turn_up", "回転：上", "Keypad8" },
            new[] { "turn_down", "回転：下", "Keypad2" }, new[] { "move_left", "移動：左", "LeftArrow" }, new[] { "move_right", "移動：右", "RightArrow" },
            new[] { "move_forward", "移動：前", "UpArrow" }, new[] { "move_back", "移動：後ろ", "DownArrow" }, new[] { "move_up", "移動：上", "PageUp" },
            new[] { "move_down", "移動：下", "PageDown" }, new[] { "zoom_in", "近づく", "Home" }, new[] { "zoom_out", "離れる", "End" },
            new[] { "roll_left", "傾き：左", "Period" }, new[] { "roll_right", "傾き：右", "Backslash" }, new[] { "fov_in", "画角：狭く", "Equals" },
            new[] { "fov_out", "画角：広く", "RightBracket" }, new[] { "turn_reset", "回転を戻す", "Keypad5" }, new[] { "roll_reset", "傾きを戻す", "Slash" },
            new[] { "fov_reset", "画角を戻す", "Semicolon" }, new[] { "reset", "カメラを初期位置へ", "A" } };
    }
}
