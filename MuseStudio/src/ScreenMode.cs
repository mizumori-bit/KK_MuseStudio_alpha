using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace MuseStudio
{
    // The game's window (the user's request of 2026-11-04): full screen by default, a setting to change it (kept by the
    // settings file). Full screen is the whole screen as a borderless window, not Unity's exclusive mode: Muse's panel (the
    // launcher's topmost browser window) lies over the game, and an exclusive screen would drop to the taskbar whenever the
    // panel is clicked. Off: a framed window of 80 % of the screen. The checks keep their window (their clicks are by pixels).
    public static class ScreenMode
    {
        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);

        const int GWL_STYLE = -16;
        const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000, WS_OVERLAPPEDWINDOW = 0x00CF0000;
        const uint SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;

        public static bool Full;      // (checks) the mode applied
        public static bool Applied;

        // Unity keeps the window's size in the registry (HKCU\Software\illusion\Koikatu\CharaStudio, Screenmanager ...) - shared
        // with the original's Studio. That one takes its size from its own setup.xml at start (IllusionFixes' SettingsVerifier),
        // yet the values are put back as they were at our start when we quit: our window's size never reaches the original.
        static readonly System.Collections.Generic.Dictionary<string, object> regBefore = new System.Collections.Generic.Dictionary<string, object>();
        const string RegKey = @"Software\illusion\Koikatu\CharaStudio";

        public static void Remember()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (k == null) return;
                    foreach (var n in k.GetValueNames()) if (n.StartsWith("Screenmanager ")) regBefore[n] = k.GetValue(n);
                }
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: the window's registry values: " + e.Message); }
        }

        public static void Restore()
        {
            if (!Applied || regBefore.Count == 0) return;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegKey, true))
                {
                    if (k == null) return;
                    foreach (var kv in regBefore) k.SetValue(kv.Key, kv.Value, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: the window's registry values back: " + e.Message); }
        }

        static IntPtr Window()
        {
            uint me = (uint)Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != me) return true;
                var sb = new StringBuilder(64);
                GetClassNameW(h, sb, 64);
                if (sb.ToString() == "UnityWndClass") { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static IEnumerator Apply(bool full)
        {
            int sw = Display.main.systemWidth, sh = Display.main.systemHeight;
            int w = full ? sw : Mathf.RoundToInt(sw * 0.8f), hh = full ? sh : Mathf.RoundToInt(sh * 0.8f);
            Screen.SetResolution(w, hh, false);
            yield return null;
            yield return null;
            var win = Window();
            if (win != IntPtr.Zero)
            {
                int style = GetWindowLong(win, GWL_STYLE);
                style = full ? (style & ~WS_OVERLAPPEDWINDOW) | WS_POPUP | WS_VISIBLE : (style & ~WS_POPUP) | WS_OVERLAPPEDWINDOW | WS_VISIBLE;
                SetWindowLong(win, GWL_STYLE, style);
                int x = full ? 0 : (sw - w) / 2, y = full ? 0 : (sh - hh) / 2;
                SetWindowPos(win, IntPtr.Zero, x, y, w, hh, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            }
            Full = full;
            Applied = true;
            MuseStudioPlugin.Log.LogInfo(string.Format("MuseStudio: the window {0} {1}x{2}", full ? "full screen (borderless)" : "framed", w, hh));
            WindowTitle.ApplyIcon();          // (its icon again after the change)
        }
    }
}
