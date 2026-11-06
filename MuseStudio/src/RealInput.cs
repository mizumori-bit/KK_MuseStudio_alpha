using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace MuseStudio
{
    // (checks) The user's own input, for real: Windows' mouse and keyboard on the game window (the user's wish of 2026-11-04:
    // the match's checks also click the UI as one uses it). A click lands only when the game window is in front and the point
    // is on it (WindowFromPoint) - else nothing is pressed and the check fails, so no other window gets a click.
    public static class RealInput
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        const uint LEFTDOWN = 0x02, LEFTUP = 0x04, KEYUP = 0x02, MOVE = 0x01, WHEEL = 0x0800;
        static readonly uint[] Downs = { 0x02, 0x08, 0x20 }, Ups = { 0x04, 0x10, 0x40 };      // left / right / middle

        public static string LastError = "";
        public static int Clicks;

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

        // the game window in front; false (LastError) when it cannot be
        static bool Front(IntPtr w)
        {
            if (w == IntPtr.Zero) { LastError = "no game window"; return false; }
            if (GetForegroundWindow() != w) SetForegroundWindow(w);
            if (GetForegroundWindow() != w)
            {
                // Windows lets only the window in front give the front away: for a moment the game's input is joined to that
                // window's thread (no key or click goes to it), then the game window comes forward
                var fg = GetForegroundWindow();
                uint pid, fgThread = GetWindowThreadProcessId(fg, out pid), wThread = GetWindowThreadProcessId(w, out pid);
                bool joined = fgThread != 0 && fgThread != wThread && AttachThreadInput(fgThread, wThread, true);
                ShowWindow(w, 5);
                BringWindowToTop(w);
                SetForegroundWindow(w);
                if (joined) AttachThreadInput(fgThread, wThread, false);
            }
            if (GetForegroundWindow() != w) { LastError = "the game window is not in front"; return false; }
            return true;
        }

        // a left click at a point of the GUI (IMGUI's pixels: from the top left of the game's screen)
        public static IEnumerator Click(Vector2 gui)
        {
            LastError = "";
            var w = Window();
            var p = new POINT();
            // (another window in front for a moment - another program's: tried again a few times)
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) yield return new WaitForSecondsRealtime(0.5f);
                LastError = "";
                if (!Front(w)) continue;
                yield return null;
                p = new POINT { X = Mathf.RoundToInt(gui.x), Y = Mathf.RoundToInt(gui.y) };
                ClientToScreen(w, ref p);
                var at = WindowFromPoint(p);
                if (at != w && GetAncestor(at, 2) != w) { LastError = "the point is not on the game window"; continue; }
                break;
            }
            if (LastError.Length > 0) yield break;
            SetCursorPos(p.X, p.Y);
            yield return null;
            yield return null;
            mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            yield return new WaitForSecondsRealtime(0.08f);
            mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
            Clicks++;
            yield return null;
            yield return null;
        }

        public static IEnumerator Click(Rect r) { return Click(r.center); }

        // a drag with a button (0 left, 1 right, 2 middle) from a point of the GUI by (dx, dy) screen pixels over some frames
        public static IEnumerator Drag(int button, Vector2 gui, Vector2 by, int steps = 12)
        {
            LastError = "";
            var w = Window();
            var p = new POINT();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) yield return new WaitForSecondsRealtime(0.5f);
                LastError = "";
                if (!Front(w)) continue;
                yield return null;
                p = new POINT { X = Mathf.RoundToInt(gui.x), Y = Mathf.RoundToInt(gui.y) };
                ClientToScreen(w, ref p);
                var at = WindowFromPoint(p);
                if (at != w && GetAncestor(at, 2) != w) { LastError = "the point is not on the game window"; continue; }
                break;
            }
            if (LastError.Length > 0) yield break;
            SetCursorPos(p.X, p.Y);
            yield return null;
            yield return null;
            mouse_event(Downs[button], 0, 0, 0, UIntPtr.Zero);
            yield return null;
            yield return null;
            int sx = 0, sy = 0;
            for (int i = 1; i <= steps; i++)
            {
                int tx = Mathf.RoundToInt(by.x * i / steps), ty = Mathf.RoundToInt(by.y * i / steps);
                mouse_event(MOVE, (uint)(tx - sx), (uint)(ty - sy), 0, UIntPtr.Zero);
                sx = tx;
                sy = ty;
                yield return null;
            }
            yield return null;
            mouse_event(Ups[button], 0, 0, 0, UIntPtr.Zero);
            yield return null;
            yield return null;
        }

        // a left press held at a point of the GUI for some seconds, the hand moving a little (jitter: the most pixels a frame,
        // back and forth - a real hand's press is never quite still; 0: still), each frame told how long it has been held;
        // then let go
        public static IEnumerator Press(Vector2 gui, float seconds, int jitter, Action<float> each = null)
        {
            LastError = "";
            var w = Window();
            var p = new POINT();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) yield return new WaitForSecondsRealtime(0.5f);
                LastError = "";
                if (!Front(w)) continue;
                yield return null;
                p = new POINT { X = Mathf.RoundToInt(gui.x), Y = Mathf.RoundToInt(gui.y) };
                ClientToScreen(w, ref p);
                var at = WindowFromPoint(p);
                if (at != w && GetAncestor(at, 2) != w) { LastError = "the point is not on the game window"; continue; }
                break;
            }
            if (LastError.Length > 0) yield break;
            SetCursorPos(p.X, p.Y);
            yield return null;
            yield return null;
            mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            float t0 = Time.realtimeSinceStartup;
            int n = 0, off = 0;
            while (Time.realtimeSinceStartup - t0 < seconds)
            {
                yield return null;
                if (jitter > 0)
                {
                    int step = (n++ % 2 == 0 ? 1 : -1) * (1 + n % jitter);     // (back and forth, never far from where it pressed)
                    if (Mathf.Abs(off + step) > 4 * jitter) step = -step;
                    off += step;
                    mouse_event(MOVE, (uint)step, 0, 0, UIntPtr.Zero);
                }
                if (each != null) each(Time.realtimeSinceStartup - t0);
            }
            mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
            Clicks++;
            yield return null;
            yield return null;
        }

        // the wheel turned at a point of the GUI (notches: + away from the user)
        public static IEnumerator Wheel(Vector2 gui, int notches)
        {
            LastError = "";
            var w = Window();
            if (!Front(w)) yield break;
            yield return null;
            var p = new POINT { X = Mathf.RoundToInt(gui.x), Y = Mathf.RoundToInt(gui.y) };
            ClientToScreen(w, ref p);
            SetCursorPos(p.X, p.Y);
            yield return null;
            yield return null;
            mouse_event(WHEEL, 0, 0, (uint)(120 * notches), UIntPtr.Zero);
            yield return null;
            yield return null;
            yield return null;
        }

        // a key held down for a while (the camera's keys move while held)
        public static IEnumerator Hold(byte vk, float seconds)
        {
            LastError = "";
            if (!Front(Window())) yield break;
            yield return null;
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            yield return new WaitForSecondsRealtime(seconds);
            keybd_event(vk, 0, KEYUP, UIntPtr.Zero);
            yield return null;
            yield return null;
        }

        // a key pressed and let go (a virtual key: 0x20 space, 0x0D enter)
        public static IEnumerator Key(byte vk)
        {
            LastError = "";
            bool front = false;
            for (int attempt = 0; attempt < 5 && !front; attempt++)
            {
                if (attempt > 0) yield return new WaitForSecondsRealtime(0.5f);
                front = Front(Window());
            }
            if (!front) yield break;
            yield return null;
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            yield return new WaitForSecondsRealtime(0.06f);
            keybd_event(vk, 0, KEYUP, UIntPtr.Zero);
            yield return null;
            yield return null;
        }
    }
}
