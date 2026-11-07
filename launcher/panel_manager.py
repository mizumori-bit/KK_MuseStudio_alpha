"""MuseStudio launcher + panel manager (begun as MetaMuse Stage's, the Godot version's; game_mode native / web are its).

Start sequence (6.1):
  1. Irodori-TTS-Server: GET /health; if it does not answer and its folder is configured, start it minimized
     (uv run --no-sync python -m irodori_openai_tts ...) and poll /health up to wait_seconds (then go on without voice).
     Skipped while the voice is off (the bridge's bridge_config.json irodori.enabled false).
  2. Bridge: GET /health; start bridge.py minimized when it does not answer.
  3. Game window: MuseStudio (game_mode "musestudio": MuseStudio/game/CharaStudio.exe, the user's decision 2026-11-03), the
     Godot window of the project (game_mode "native"), or the browser in --app mode on the bridge's page (web build); at
     (0,0) and maximized.
  4. Muse panel: the browser in --app mode on muse_url with the panel profile, the bottom-left rectangle.
  5. The panel window gets TOPMOST and the base opacity.
Resident (6.2): every hover_poll_ms the cursor is checked against the panel (inside: opaque; outside: the base opacity);
a small control bar follows the panel (opacity slider 64-255, click-through toggle, show / hide, quit). Quit (the bar or
the hotkey, default Ctrl+Alt+Q) restores the panel's attributes and ends only this manager unless stop_on_exit says more.
Hover focus (hover_focus, on by default - the owner's decision of 2026-11-06; the bar's ホバーでフォーカス turns it off / on and keeps it in panel_config.json): the cursor
resting on the panel gives it the keyboard, leaving it gives the keyboard back to the window that had it (HoverFocus).
Running it again while it runs changes nothing (a named mutex); components already up are reused, not started twice.

Windows only. Python standard library (ctypes for Win32, tkinter for the bar). AutoHotkey v2 was the spec's suggestion;
it is not installed on this PC, so the same behaviour is written here (DECISIONS.md).
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import tkinter as tk
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
# the launcher's icon (the user's choice of 2026-11-05, GPT's PR #8; the original in icons/musestudio-launcher-master.png, this
# its margin cut and its small sizes made crisper): the shortcut (make_shortcut.py), the console and the Tk windows
ICON = HERE / "icons" / "musestudio-launcher-app.ico"
user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

GWL_EXSTYLE = -20
WS_EX_LAYERED = 0x00080000
WS_EX_TRANSPARENT = 0x00000020
LWA_ALPHA = 0x2
HWND_TOPMOST = wt.HWND(-1)
HWND_NOTOPMOST = wt.HWND(-2)
SWP_NOMOVE, SWP_NOSIZE, SWP_NOACTIVATE, SWP_SHOWWINDOW, SWP_FRAMECHANGED = 0x2, 0x1, 0x10, 0x40, 0x20
SW_HIDE, SW_MAXIMIZE, SW_SHOWNOACTIVATE, SW_RESTORE, SW_SHOWMINNOACTIVE = 0, 3, 4, 9, 7
WM_HOTKEY, WM_QUIT, WM_CLOSE = 0x0312, 0x0012, 0x0010
MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_NOREPEAT = 0x1, 0x2, 0x4, 0x4000
SPI_GETWORKAREA = 0x0030
ERROR_ALREADY_EXISTS = 183

LONG_PTR = ctypes.c_ssize_t
user32.GetWindowLongPtrW.restype = LONG_PTR
user32.GetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int]
user32.SetWindowLongPtrW.restype = LONG_PTR
user32.SetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int, LONG_PTR]
user32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.UINT]
user32.SetLayeredWindowAttributes.argtypes = [wt.HWND, wt.COLORREF, ctypes.c_ubyte, wt.DWORD]
user32.ShowWindow.argtypes = [wt.HWND, ctypes.c_int]
user32.IsWindow.argtypes = [wt.HWND]
user32.LoadImageW.restype = wt.HANDLE
user32.LoadImageW.argtypes = [wt.HINSTANCE, wt.LPCWSTR, wt.UINT, ctypes.c_int, ctypes.c_int, wt.UINT]
user32.SendMessageW.restype = LONG_PTR
user32.SendMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
kernel32.GetConsoleWindow.restype = wt.HWND
user32.IsWindowVisible.argtypes = [wt.HWND]
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
EnumWindowsProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
user32.GetForegroundWindow.restype = wt.HWND
user32.GetAncestor.restype = wt.HWND
user32.GetAncestor.argtypes = [wt.HWND, wt.UINT]
user32.AttachThreadInput.argtypes = [wt.DWORD, wt.DWORD, wt.BOOL]
user32.BringWindowToTop.argtypes = [wt.HWND]
user32.SetForegroundWindow.argtypes = [wt.HWND]
user32.IsIconic.argtypes = [wt.HWND]
user32.GetAsyncKeyState.restype = ctypes.c_short
user32.GetAsyncKeyState.argtypes = [ctypes.c_int]
GA_ROOTOWNER = 3
VK_LBUTTON, VK_RBUTTON, VK_MBUTTON = 0x01, 0x02, 0x04


def log(msg: str) -> None:
    print(time.strftime("[%H:%M:%S] ") + msg, flush=True)


# ---- Win32 helpers ---------------------------------------------------------------------------------------------------

def set_dpi_aware() -> None:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except (AttributeError, OSError):
        try:
            user32.SetProcessDPIAware()
        except (AttributeError, OSError):
            pass


def top_windows() -> list[tuple[int, int, str, str]]:
    """(hwnd, pid, title, class) of the visible top-level windows."""
    out = []

    def cb(hwnd, _):
        if user32.IsWindowVisible(hwnd):
            pid = wt.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            buf = ctypes.create_unicode_buffer(512)
            user32.GetWindowTextW(hwnd, buf, 512)
            cls = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(hwnd, cls, 256)
            out.append((int(hwnd), int(pid.value), buf.value, cls.value))
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return out


def window_rect(hwnd: int) -> tuple[int, int, int, int]:
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def work_area() -> tuple[int, int, int, int]:
    r = wt.RECT()
    user32.SystemParametersInfoW(SPI_GETWORKAREA, 0, ctypes.byref(r), 0)
    return r.left, r.top, r.right, r.bottom


def screen_size() -> tuple[int, int]:
    return user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)


def exstyle(hwnd: int) -> int:
    return int(user32.GetWindowLongPtrW(hwnd, GWL_EXSTYLE))


def set_exstyle(hwnd: int, value: int) -> None:
    user32.SetWindowLongPtrW(hwnd, GWL_EXSTYLE, value)
    user32.SetWindowPos(hwnd, None, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | 0x4)


def set_alpha(hwnd: int, alpha: int) -> None:
    st = exstyle(hwnd)
    if not st & WS_EX_LAYERED:
        set_exstyle(hwnd, st | WS_EX_LAYERED)
    user32.SetLayeredWindowAttributes(hwnd, 0, max(0, min(255, int(alpha))), LWA_ALPHA)


def set_click_through(hwnd: int, on: bool) -> None:
    st = exstyle(hwnd) | WS_EX_LAYERED
    st = (st | WS_EX_TRANSPARENT) if on else (st & ~WS_EX_TRANSPARENT)
    set_exstyle(hwnd, st)


def set_topmost(hwnd: int, on: bool) -> None:
    user32.SetWindowPos(hwnd, HWND_TOPMOST if on else HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)


def cursor_pos() -> tuple[int, int]:
    p = wt.POINT()
    user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def foreground() -> int:
    return int(user32.GetForegroundWindow() or 0)


def root_of(hwnd: int) -> int:
    """the top-level window a window (a popup, a dialog of it) belongs to"""
    return int(user32.GetAncestor(hwnd, GA_ROOTOWNER) or hwnd or 0) if hwnd else 0


def force_foreground(hwnd: int) -> bool:
    """Another process's window to the front with the keyboard. Windows refuses a background process's plain
    SetForegroundWindow (tried 2026-11-06 on Windows 11: a browser --app window came forward 1 time in 5); with this thread's
    input attached to the foreground window's thread and the target's for the call, 5 in 5 both ways."""
    if not hwnd or not user32.IsWindow(hwnd):
        return False
    if root_of(foreground()) == hwnd:
        return True
    me = kernel32.GetCurrentThreadId()
    threads = {user32.GetWindowThreadProcessId(foreground(), None), user32.GetWindowThreadProcessId(hwnd, None)}
    attached = [t for t in threads if t and t != me and user32.AttachThreadInput(me, t, True)]
    try:
        user32.BringWindowToTop(hwnd)
        user32.SetForegroundWindow(hwnd)
    finally:
        for t in attached:
            user32.AttachThreadInput(me, t, False)
    return root_of(foreground()) == hwnd


def mouse_buttons_down() -> bool:
    return any(user32.GetAsyncKeyState(vk) & 0x8000 for vk in (VK_LBUTTON, VK_RBUTTON, VK_MBUTTON))


class HoverFocus:
    """The panel takes the keyboard while the cursor rests on it and gives it back when the cursor leaves (PR #19, the owner's
    request; GPT's review: the user's own switch wins, never back to a window that is gone; on by default, the owner's decision).
    A wheel turned within enter_s of coming onto the panel still reaches the game (accepted: a guard in the game was weighed
    and left out - the owner's decision of 2026-11-06).

    - In: the cursor on the panel (or its bar) for enter_s, no mouse button held (a drag of the game's camera across it
      switches nothing), the panel shown and not click-through, at least cooldown_s after the last switch. The window that
      had the keyboard is remembered - not the panel's own windows. A refused switch is not tried again until the cursor
      leaves (the panel stays usable with a click).
    - Out: the cursor off both for leave_s, no button held (a drag out of the panel: when it is let go). Back only after this
      class's own switch, while the panel still has the keyboard and the remembered window is still there (shown, not
      minimized); otherwise nothing.
    - The user's switch (Alt+Tab, a click elsewhere, the task bar) while on the panel: forgotten - nothing to go back to.
      And while the cursor stays on the panel after the user's switch (from the panel, or between other windows), the panel
      does not take the keyboard again: only once the cursor has left it and come back (GPT's post-review of #21/#22: the
      next tick counted a new hover and took the keyboard back after enter_s).
    """

    def __init__(self, enter_s: float = 0.15, leave_s: float = 0.3, cooldown_s: float = 0.4):
        self.enter_s, self.leave_s, self.cooldown_s = enter_s, leave_s, cooldown_s
        self.reset()
        self.last_switch = -1e9
        self.held = False                    # (the user switched while the cursor was on the panel: until it leaves)
        self.last_fg = 0                     # (the foreground window's root at the last tick: a change is a switch)
        self.events: list[str] = []          # (tests) what each tick did, when it did something

    def reset(self) -> None:
        self.over_since = None
        self.out_since = None
        self.prev = 0
        self.switched = False
        self.refused = False

    def _note(self, what: str) -> str:
        self.events.append(what)
        if what in ("focused", "restored", "refused", "restore_refused", "user_switched", "window_gone", "held"):
            log(f"hover focus: {what}")
        return what

    def tick(self, panel: int, over: bool, allowed: bool, buttons: bool, now: float, ours=()) -> str:
        fg = foreground()
        fg_root = root_of(fg)
        on_panel = fg_root == panel
        changed = bool(fg_root) and bool(self.last_fg) and fg_root != self.last_fg
        if fg_root:
            self.last_fg = fg_root
        if self.switched and fg and not on_panel:
            self.reset()                      # (the user went elsewhere: theirs wins)
            self.held = bool(over)            # (and while the cursor stays: not taken back)
            return self._note("user_switched")
        if not allowed:
            self.reset()
            self.held = False
            return "off"
        if over:
            self.out_since = None
            if not self.held and changed and not on_panel and not self.switched:
                self.held = True              # (a switch the user made while the cursor rests here: from the panel, or between
                self.over_since = None        #  other windows - it is not taken from them until the cursor leaves)
                return self._note("held")
            if self.held:
                return "held"
            if self.switched or self.refused or on_panel:
                return "inside"
            if buttons:
                self.over_since = None
                return "buttons"
            if self.over_since is None:
                self.over_since = now
            if now - self.over_since < self.enter_s or now - self.last_switch < self.cooldown_s:
                return "waiting"
            mine = {int(h) for h in ours if h} | {panel}
            prev = fg_root if fg_root and fg_root not in mine and user32.IsWindow(fg_root) else 0
            self.over_since = None
            if force_foreground(panel):
                self.switched, self.prev, self.last_switch = True, prev, now
                return self._note("focused")
            self.refused = True
            return self._note("refused")
        self.over_since = None
        self.refused = False
        self.held = False                     # (the cursor left: the next stay may take the keyboard again)
        if not self.switched:
            return "outside"
        if buttons:
            self.out_since = None
            return "buttons"
        if self.out_since is None:
            self.out_since = now
        if now - self.out_since < self.leave_s or now - self.last_switch < self.cooldown_s:
            return "waiting"
        prev = self.prev
        self.reset()
        if not prev or not user32.IsWindow(prev) or not user32.IsWindowVisible(prev) or user32.IsIconic(prev):
            return self._note("window_gone")
        self.last_switch = now
        return self._note("restored" if force_foreground(prev) else "restore_refused")


def process_command_lines(names: tuple[str, ...]) -> list[tuple[int, str]]:
    """(pid, command line) of running processes with these image names (PowerShell CIM; startup only)."""
    flt = " or ".join(f"Name='{n}'" for n in names)
    cmd = ["powershell", "-NoProfile", "-NonInteractive", "-Command",
           f"Get-CimInstance Win32_Process -Filter \"{flt}\" | ForEach-Object {{ \"$($_.ProcessId)`t$($_.CommandLine)\" }}"]
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20,
                             creationflags=0x08000000).stdout
    except (OSError, subprocess.SubprocessError):
        return []
    rows = []
    for line in out.splitlines():
        if "\t" in line:
            pid, cl = line.split("\t", 1)
            if pid.strip().isdigit():
                rows.append((int(pid), cl))
    return rows


# ---- components ------------------------------------------------------------------------------------------------------

def http_ok(url: str, timeout: float = 2.0) -> bool:
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            return 200 <= r.status < 300
    except Exception:  # noqa: BLE001 - any failure means "not up"
        return False


def start_minimized(cmd: list[str], cwd: str | None = None, env: dict | None = None) -> subprocess.Popen:
    si = subprocess.STARTUPINFO()
    si.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    si.wShowWindow = SW_SHOWMINNOACTIVE
    full = None
    if env:
        full = dict(os.environ)
        full.update({str(k): str(v) for k, v in env.items()})
    return subprocess.Popen(cmd, cwd=cwd, env=full, startupinfo=si, creationflags=subprocess.CREATE_NEW_CONSOLE)


def find_browser(cfg: dict) -> str:
    if cfg.get("browser"):
        return str(cfg["browser"])
    for p in [r"C:\Program Files\Google\Chrome\Application\chrome.exe", r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
              os.path.expandvars(r"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"),
              r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", r"C:\Program Files\Microsoft\Edge\Application\msedge.exe"]:
        if os.path.isfile(p):
            return p
    return shutil.which("chrome") or shutil.which("msedge") or ""


class Manager:
    def __init__(self, cfg: dict):
        self.cfg = cfg
        self.started: dict[str, subprocess.Popen] = {}
        self.browser = find_browser(cfg)
        self.profiles = (HERE / cfg.get("profiles_dir", "profiles")).resolve()
        self.panel_hwnd = 0
        self.game_hwnd = 0
        self.base_alpha = int(cfg.get("base_opacity", 200))
        self.click_through = False
        self.panel_hidden = False
        self.current_alpha = -1
        self.bar_hwnd = 0
        self.conf_path: Path | None = None
        self.focus = HoverFocus(cfg.get("hover_focus_enter_ms", 150) / 1000.0, cfg.get("hover_focus_leave_ms", 300) / 1000.0,
                                cfg.get("hover_focus_cooldown_ms", 400) / 1000.0)

    # 6.1-1
    def ensure_irodori(self) -> bool:
        if not self.voice_on():
            log("Irodori: the voice is off (bridge_config.json irodori.enabled) -> not started")
            return False
        ic = self.cfg.get("irodori", {})
        url = ic.get("health_url", "http://127.0.0.1:8088/health")
        if http_ok(url):
            log("Irodori: already running")
            return True
        d = str(ic.get("dir", ""))
        if not d or not os.path.isdir(d):
            log("Irodori: not running and its folder is not configured (irodori.dir) -> no voice")
            return False
        log("Irodori: starting (minimized): " + " ".join(ic.get("command", [])))
        try:
            self.started["irodori"] = start_minimized(list(ic.get("command", [])), cwd=d, env=ic.get("env") or None)
        except OSError as e:
            log(f"Irodori: could not start ({e}) -> no voice")
            return False
        deadline = time.time() + float(ic.get("wait_seconds", 120))
        while time.time() < deadline:
            if http_ok(url):
                log("Irodori: /health answered")
                return True
            time.sleep(1.0)
        log("Irodori: no /health within the wait -> going on without voice")
        return False

    def voice_on(self) -> bool:
        # The one voice switch is the bridge's (bridge_config.json next to its script: irodori.enabled).
        script = (HERE / self.cfg.get("bridge", {}).get("script", "../bridge/bridge.py")).resolve()
        try:
            bc = json.loads((script.parent / "bridge_config.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return True
        return bc.get("irodori", {}).get("enabled", True) is not False

    # 6.1-2
    def ensure_bridge(self) -> bool:
        bc = self.cfg.get("bridge", {})
        url = bc.get("health_url", "http://127.0.0.1:18765/health")
        if http_ok(url):
            log("Bridge: already running")
            return True
        py = sys.executable
        if py.lower().endswith("pythonw.exe"):
            py = py[:-len("pythonw.exe")] + "python.exe"
        script = (HERE / bc.get("script", "../bridge/bridge.py")).resolve()
        log(f"Bridge: starting (minimized): {script}")
        self.started["bridge"] = start_minimized([py, "-B", str(script)], cwd=str(script.parent))
        deadline = time.time() + float(bc.get("wait_seconds", 15))
        while time.time() < deadline:
            if http_ok(url):
                log("Bridge: /health answered")
                return True
            time.sleep(0.3)
        log("Bridge: did not answer in time (the game keeps polling and resumes when it does)")
        return False

    def _profile_windows(self, profile: Path) -> list[int]:
        # Windows of a browser started with this --user-data-dir (the browser process, not its --type= children).
        prof = str(profile).lower()
        pids = {pid for pid, cl in process_command_lines(("chrome.exe", "msedge.exe"))
                if prof in cl.lower() and "--type=" not in cl}
        return [h for h, pid, title, cls in top_windows() if pid in pids and cls.startswith("Chrome_WidgetWin") and title]

    def _wait_window(self, pick, seconds: float) -> int:
        deadline = time.time() + seconds
        while time.time() < deadline:
            h = pick()
            if h:
                return h
            time.sleep(0.3)
        return 0

    def _game_mode(self) -> str:
        mode = str(self.cfg.get("game_mode", "auto"))
        if mode == "auto":
            web_index = (HERE / "../web/index.html").resolve()
            mode = "web" if web_index.is_file() else "native"
        return mode

    def _find_game(self) -> int:
        studio = self._game_mode() == "musestudio"
        title = "MuseStudio" if studio else str(self.cfg.get("game_title", "MetaMuse Stage"))
        started = self.started.get("game")
        for h, pid, t, cls in top_windows():
            if t == title or t.startswith(title + " ") or t.startswith(title + " -"):
                return h
            # MuseStudio names its window once Studio is ready; the one we started is found by its process before that
            if studio and started is not None and pid == started.pid and cls == "UnityWndClass":
                return h
        return 0

    # 6.1-3
    def ensure_game(self) -> int:
        h = self._find_game()
        if h:
            log("Game window: already open")
        else:
            mode = self._game_mode()
            sw, shh = screen_size()
            if mode == "web":
                if not self.browser:
                    log("Game window: no Chrome / Edge found")
                    return 0
                prof = self.profiles / "game"
                log(f"Game window: browser --app {self.cfg.get('game_url')}")
                self.started["game"] = subprocess.Popen([self.browser, f"--app={self.cfg.get('game_url', 'http://localhost:18765/')}",
                                                         f"--user-data-dir={prof}", "--no-first-run", "--no-default-browser-check",
                                                         # Voice without a click: the user types in Muse, not in this window.
                                                         "--autoplay-policy=no-user-gesture-required",
                                                         "--window-position=0,0", f"--window-size={sw},{shh}"])
            elif mode == "musestudio":
                exe = (HERE / self.cfg.get("musestudio_exe", "../MuseStudio/game/CharaStudio.exe")).resolve()
                if not exe.is_file():
                    log(f"Game window: MuseStudio not found ({exe}; MuseStudio/tools/setup.py and build.py)")
                    return 0
                args = [str(exe)] + [str(a) for a in self.cfg.get("musestudio_args", [])]
                if "--selftest" in sys.argv:
                    args.append("--muse-bridge=http://127.0.0.1:1")   # the self-test never talks to the bridge Muse watches
                log(f"Game window: MuseStudio ({exe})")
                self.started["game"] = subprocess.Popen(args, cwd=str(exe.parent))   # Studio reads UserData/ from its folder
            else:
                exe = str(self.cfg.get("godot_exe", ""))
                proj = (HERE / self.cfg.get("godot_project", "../godot")).resolve()
                if not os.path.isfile(exe):
                    log(f"Game window: Godot not found ({exe})")
                    return 0
                log(f"Game window: native Godot ({proj})")
                self.started["game"] = subprocess.Popen([exe, "--path", str(proj), "--position", "0,0"])
            h = self._wait_window(self._find_game, 120.0 if mode == "musestudio" else 40.0)
            if not h:
                log("Game window: did not appear")
                return 0
        user32.ShowWindow(h, SW_MAXIMIZE)
        self.game_hwnd = h
        return h

    def _panel_rect(self) -> tuple[int, int, int, int]:
        return panel_rect(self.cfg, work_area(), screen_size())

    # 6.1-4 / 5
    def ensure_panel(self) -> int:
        prof = self.profiles / "panel"
        url = str(self.cfg.get("muse_url") or "")
        if not url:
            url = self.cfg.get("game_url", "http://localhost:18765/").rstrip("/") + "/static/panel_placeholder.html"
            log("Muse panel: muse_url is not set -> placeholder page")
        existing = self._profile_windows(prof)
        x, y, w, hgt = self._panel_rect()
        if existing:
            h = existing[0]
            log("Muse panel: already open")
        else:
            if not self.browser:
                log("Muse panel: no Chrome / Edge found")
                return 0
            log(f"Muse panel: browser --app {url}")
            self.started["panel"] = subprocess.Popen([self.browser, f"--app={url}", f"--user-data-dir={prof}", "--no-first-run",
                                                      "--no-default-browser-check", f"--window-position={x},{y}", f"--window-size={w},{hgt}"])
            pid = self.started["panel"].pid

            def pick():
                for hh, ppid, title, cls in top_windows():
                    if ppid == pid and cls.startswith("Chrome_WidgetWin") and title:
                        return hh
                return 0

            h = self._wait_window(pick, 25.0) or (self._profile_windows(prof) or [0])[0]
            if not h:
                log("Muse panel: window did not appear")
                return 0
        # The browser may restore an earlier size: the rectangle is enforced, then TOPMOST and the base opacity.
        user32.ShowWindow(h, SW_RESTORE)
        user32.SetWindowPos(h, HWND_TOPMOST, x, y, w, hgt, SWP_NOACTIVATE | SWP_SHOWWINDOW)
        set_alpha(h, self.base_alpha)
        self.current_alpha = self.base_alpha
        self.panel_hwnd = h
        log(f"Muse panel: TOPMOST, opacity {self.base_alpha}/255 at ({x},{y}) {w}x{hgt}")
        return h

    # 6.2
    def hover_tick(self, cursor=None) -> None:
        h = self.panel_hwnd
        if not h or not user32.IsWindow(h) or self.panel_hidden:
            return
        l, t, r, b = window_rect(h)
        cx, cy = cursor if cursor is not None else cursor_pos()
        over = l <= cx < r and t <= cy < b
        target = 255 if over and self.hover_opaque else self.base_alpha
        if target != self.current_alpha:
            set_alpha(h, target)
            self.current_alpha = target
        if self.hover_focus:
            # (the bar under the panel counts as the panel: going down to its buttons keeps the keyboard where it is)
            bl, bt, br, bb = window_rect(self.bar_hwnd) if self.bar_hwnd and user32.IsWindow(self.bar_hwnd) else (0, 0, 0, 0)
            over_any = over or (bl <= cx < br and bt <= cy < bb)
            self.focus.tick(h, over_any, not self.click_through, mouse_buttons_down(), time.monotonic(), (self.bar_hwnd,))

    @property
    def hover_opaque(self) -> bool:
        return bool(self.cfg.get("hover_opaque", False))

    def set_hover_opaque(self, on: bool) -> None:
        self.cfg["hover_opaque"] = bool(on)
        self.hover_tick()

    @property
    def hover_focus(self) -> bool:
        return bool(self.cfg.get("hover_focus", True))

    def set_hover_focus(self, on: bool) -> None:
        self.cfg["hover_focus"] = bool(on)
        self.focus.reset()
        self.save_setting("hover_focus", bool(on))

    def save_setting(self, key: str, value) -> None:
        """one setting written back to this machine's panel_config.json (never the example)"""
        p = self.conf_path
        if not p or p.name != "panel_config.json" or not p.is_file():
            return
        try:
            doc = json.loads(p.read_text(encoding="utf-8"))
            doc[key] = value
            p.write_text(json.dumps(doc, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        except (OSError, ValueError) as e:
            log(f"setting {key}: not saved ({e})")

    # the bar's 全体を終了 (the trial run of 2026-11-04: its 終了 looked like the whole's end): the game and the bridge too (quit_all;
    # the panel's attributes back, the panel itself and Irodori stay unless the setting says)
    def quit_all(self) -> None:
        stop = dict(self.cfg.get("stop_on_exit", {}))
        stop.update(self.cfg.get("quit_all", {"game": True, "bridge": True}))
        self.cfg["stop_on_exit"] = stop
        if stop.get("game"):
            # (MuseStudio skips Studio's 終了しますか？ when this marker is there: the plugin's QuitNow)
            exe = (HERE / self.cfg.get("musestudio_exe", "../MuseStudio/game/CharaStudio.exe")).resolve()
            marker = exe.parent / "UserData" / "MuseStudio" / "quit.now"
            try:
                marker.parent.mkdir(parents=True, exist_ok=True)
                marker.write_text("quit", encoding="utf-8")
            except OSError as e:
                log(f"quit marker: {e}")
        self.restore_and_stop()
        if stop.get("bridge") and "bridge" not in self.started:
            # (a bridge this launcher found running, not started: its process by its command line)
            script = str((HERE / self.cfg.get("bridge", {}).get("script", "../bridge/bridge.py")).resolve()).lower()
            for pid, cl in process_command_lines(("python.exe", "pythonw.exe")):
                low = cl.lower().replace(chr(92), "/")
                if script.replace(chr(92), "/") in low or ("bridge/bridge.py" in low and "--config" not in low):
                    subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True, creationflags=0x08000000)
                    log(f"bridge (pid {pid}): stopped")

    def set_base(self, value: int) -> None:
        self.base_alpha = max(64, min(255, int(value)))
        self.current_alpha = -1

    def toggle_click_through(self, on: bool) -> None:
        self.click_through = on
        self.focus.reset()
        if self.panel_hwnd and user32.IsWindow(self.panel_hwnd):
            set_click_through(self.panel_hwnd, on)
            set_topmost(self.panel_hwnd, True)

    def toggle_hidden(self) -> bool:
        if not self.panel_hwnd or not user32.IsWindow(self.panel_hwnd):
            return self.panel_hidden
        self.panel_hidden = not self.panel_hidden
        self.focus.reset()
        if self.panel_hidden:
            user32.ShowWindow(self.panel_hwnd, SW_HIDE)
        else:
            user32.ShowWindow(self.panel_hwnd, SW_SHOWNOACTIVATE)
            set_topmost(self.panel_hwnd, True)
            self.current_alpha = -1
        return self.panel_hidden

    def restore_and_stop(self) -> None:
        h = self.panel_hwnd
        if h and user32.IsWindow(h):
            if self.panel_hidden:
                user32.ShowWindow(h, SW_SHOWNOACTIVATE)
            set_click_through(h, False)
            user32.SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA)
            set_exstyle(h, exstyle(h) & ~WS_EX_LAYERED)
            set_topmost(h, False)
            log("Muse panel: attributes restored")
        stop = self.cfg.get("stop_on_exit", {})
        for name in ("panel", "game"):
            hh = self.panel_hwnd if name == "panel" else self.game_hwnd
            if stop.get(name) and hh and user32.IsWindow(hh):
                user32.PostMessageW(hh, WM_CLOSE, 0, 0)
                log(f"{name}: closed")
        for name in ("bridge", "irodori"):
            if stop.get(name) and name in self.started:
                self.started[name].terminate()
                log(f"{name}: stopped")


def panel_rect(cfg: dict, work: tuple[int, int, int, int], screen: tuple[int, int]) -> tuple[int, int, int, int]:
    """The panel's rectangle (x, y, w, h): panel.side "right" (default, 2026-11-04: by the mouse hand) / "left" (or the old
    panel.left: a left offset), panel.margin from that edge, its bottom at the work area's bottom (bottom_edge "workarea")
    or the screen's - with the bar under it (controls_attach "bottom_outside") the panel ends the bar's height above."""
    p = cfg.get("panel", {})
    w, hgt, margin = int(p.get("width", 400)), int(p.get("height", 560)), int(p.get("margin", 0))
    side = p.get("side", "left" if "left" in p else "right")
    if side == "right":
        x = work[2] - w - margin
    else:
        x = work[0] + int(p.get("left", margin))
    bottom = work[3] if p.get("bottom_edge", "workarea") == "workarea" else screen[1]
    if cfg.get("controls_attach", "bottom_outside") == "bottom_outside":
        bottom -= BAR_HEIGHT
    return x, bottom - hgt, w, hgt


def bar_rect(attach: str, panel: tuple[int, int, int, int]) -> tuple[int, int, int, int]:
    """The bar's rectangle (x, y, w, h) by the panel's window rectangle (l, t, r, b): under it (bottom_outside, default:
    the panel can be stretched upwards freely), over it (top_outside) or inside its bottom (bottom_inside)."""
    l, t, r, b = panel
    w = max(260, r - l)
    if attach == "bottom_inside":
        return l, b - BAR_HEIGHT, w, BAR_HEIGHT
    if attach == "top_outside":
        return l, max(0, t - BAR_HEIGHT), w, BAR_HEIGHT
    return l, b, w, BAR_HEIGHT


BAR_HEIGHT = 34


# ---- the control bar -------------------------------------------------------------------------------------------------

class ControlBar:
    HEIGHT = BAR_HEIGHT

    def __init__(self, mgr: Manager, attach: str):
        self.mgr = mgr
        self.attach = attach
        self.root = tk.Tk()
        self.root.title("MetaMuse panel controls")
        if ICON.is_file():
            try:
                self.root.iconbitmap(default=str(ICON))          # (every window of the launcher's own)
            except tk.TclError:
                pass
        self.root.overrideredirect(True)
        self.root.attributes("-topmost", True)
        bg, fg = "#15101a", "#f5eff6"          # (the game's colours: Theme)
        self.root.configure(bg=bg)
        f = tk.Frame(self.root, bg=bg)
        f.pack(fill="both", expand=True, padx=6, pady=2)
        tk.Label(f, text="不透明度", bg=bg, fg=fg, font=("Yu Gothic UI", 9)).pack(side="left")
        self.scale = tk.Scale(f, from_=64, to=255, orient="horizontal", showvalue=False, length=110, bg=bg, fg=fg,
                              troughcolor="#3c2942", highlightthickness=0, bd=0, sliderlength=14, command=self._scale)
        self.scale.set(mgr.base_alpha)
        self.scale.pack(side="left", padx=(4, 8))
        self.ct = tk.BooleanVar(value=False)
        tk.Checkbutton(f, text="クリック透過", variable=self.ct, command=self._ct, bg=bg, fg=fg, selectcolor="#6c2951",
                       activebackground=bg, activeforeground=fg, font=("Yu Gothic UI", 9), bd=0, highlightthickness=0).pack(side="left")
        self.vis = tk.Button(f, text="隠す", command=self._vis, bg="#2a1e2f", fg=fg, relief="flat", activebackground="#3c2942", activeforeground=fg, font=("Yu Gothic UI", 9), padx=6)
        self.vis.pack(side="left", padx=(6, 0))
        tk.Button(f, text="全体を終了", command=self.quit_all, bg="#6c2951", fg=fg, relief="flat", activebackground="#8a3566", activeforeground=fg, font=("Yu Gothic UI", 9), padx=6).pack(side="right")
        self.ho = tk.BooleanVar(value=mgr.hover_opaque)
        tk.Checkbutton(f, text="ホバーで不透明", variable=self.ho, command=lambda: self.mgr.set_hover_opaque(self.ho.get()), bg=bg, fg=fg, selectcolor="#6c2951",
                       activebackground=bg, activeforeground=fg, font=("Yu Gothic UI", 9), bd=0, highlightthickness=0).pack(side="left", padx=(6, 0))
        self.hf = tk.BooleanVar(value=mgr.hover_focus)
        tk.Checkbutton(f, text="ホバーでフォーカス", variable=self.hf, command=lambda: self.mgr.set_hover_focus(self.hf.get()), bg=bg, fg=fg, selectcolor="#6c2951",
                       activebackground=bg, activeforeground=fg, font=("Yu Gothic UI", 9), bd=0, highlightthickness=0).pack(side="left", padx=(6, 0))
        self.closing = False
        self.root.after(int(mgr.cfg.get("hover_poll_ms", 100)), self._tick)

    def _scale(self, v):
        self.mgr.set_base(int(float(v)))

    def _ct(self):
        self.mgr.toggle_click_through(self.ct.get())

    def _vis(self):
        hidden = self.mgr.toggle_hidden()
        self.vis.configure(text="表示" if hidden else "隠す")

    def _follow(self):
        if not self.mgr.bar_hwnd:
            try:
                self.mgr.bar_hwnd = int(self.root.frame(), 16)
            except (tk.TclError, ValueError):
                pass
        h = self.mgr.panel_hwnd
        if not h or not user32.IsWindow(h):
            return
        x, y, w, hh = bar_rect(self.attach, window_rect(h))
        self.root.geometry(f"{w}x{hh}+{x}+{y}")

    def _tick(self):
        if self.closing:
            return
        try:
            self.mgr.hover_tick()
            if not self.mgr.panel_hidden:
                self._follow()
        except Exception as e:  # noqa: BLE001 - keep the resident loop alive
            log(f"tick error: {e}")
        self.root.after(int(self.mgr.cfg.get("hover_poll_ms", 100)), self._tick)

    def quit_all(self):
        # (the game and the bridge too: asked once - the whole goes)
        from tkinter import messagebox
        if self.closing or not messagebox.askokcancel("全体を終了", "ゲーム（MuseStudio）とブリッジも終了します。よろしいですか？", parent=self.root):
            return
        self.closing = True
        self.mgr.quit_all()
        self.root.destroy()

    def quit(self):
        if self.closing:
            return
        self.closing = True
        self.mgr.restore_and_stop()
        self.root.destroy()

    def run(self):
        self._follow()
        self.root.mainloop()


def hotkey_thread(spec: str, on_hotkey) -> int:
    """RegisterHotKey on its own message loop; returns the thread id (to post WM_QUIT)."""
    mods, vk = 0, 0
    for part in spec.lower().split("+"):
        part = part.strip()
        if part == "ctrl":
            mods |= MOD_CONTROL
        elif part == "alt":
            mods |= MOD_ALT
        elif part == "shift":
            mods |= MOD_SHIFT
        elif len(part) == 1:
            vk = ord(part.upper())
    ident = {}

    def run():
        ident["tid"] = kernel32.GetCurrentThreadId()
        if not user32.RegisterHotKey(None, 1, mods | MOD_NOREPEAT, vk):
            log(f"hotkey {spec}: could not register (in use?)")
            return
        msg = wt.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            if msg.message == WM_HOTKEY:
                on_hotkey()
        user32.UnregisterHotKey(None, 1)

    t = threading.Thread(target=run, daemon=True)
    t.start()
    for _ in range(50):
        if "tid" in ident:
            break
        time.sleep(0.01)
    return ident.get("tid", 0)


def selftest(mgr: Manager, bar: ControlBar) -> None:
    """--selftest: the resident behaviour checked on the real panel window (no mouse movement: the cursor is passed in)."""
    h = mgr.panel_hwnd
    results = []

    def alpha() -> int:
        a = ctypes.c_ubyte()
        flags = wt.DWORD()
        user32.GetLayeredWindowAttributes(h, None, ctypes.byref(a), ctypes.byref(flags))
        return a.value

    def check(name, ok, detail=""):
        results.append(ok)
        log(("SELFTEST PASS " if ok else "SELFTEST FAIL ") + name + (f" ({detail})" if detail else ""))

    l, t, r, b = window_rect(h)
    x, y, w, hh = mgr._panel_rect()
    check("panel rectangle", (l, t, r - l, b - t) == (x, y, w, hh), f"{(l, t, r - l, b - t)} vs {(x, y, w, hh)}")
    check("panel TOPMOST", bool(exstyle(h) & 0x8))
    check("base opacity applied", alpha() == mgr.base_alpha, str(alpha()))
    mgr.hover_tick(cursor=((l + r) // 2, (t + b) // 2))
    check("hover inside, the setting off (default) -> stays see-through", alpha() == mgr.base_alpha, str(alpha()))
    mgr.cfg["hover_opaque"] = True
    mgr.hover_tick(cursor=((l + r) // 2, (t + b) // 2))
    check("hover inside, the setting on -> opaque", alpha() == 255, str(alpha()))
    mgr.hover_tick(cursor=(r + 300, t - 300))
    check("hover outside -> base", alpha() == mgr.base_alpha, str(alpha()))
    bar.scale.set(120)
    bar.root.update()
    mgr.hover_tick(cursor=(r + 300, t - 300))
    check("slider sets the base opacity", alpha() == 120, str(alpha()))
    bar.ct.set(True)
    bar._ct()
    check("click-through on", bool(exstyle(h) & WS_EX_TRANSPARENT))
    mgr.hover_tick(cursor=((l + r) // 2, (t + b) // 2))
    check("hover still works while click-through", alpha() == 255, str(alpha()))
    bar.ct.set(False)
    bar._ct()
    check("click-through off", not exstyle(h) & WS_EX_TRANSPARENT)
    bar._vis()
    check("hide", not user32.IsWindowVisible(h))
    bar._vis()
    check("show", bool(user32.IsWindowVisible(h)) and bool(exstyle(h) & 0x8))
    bl, bt, br, bb = window_rect(bar.root.winfo_id() and int(bar.root.frame(), 16))
    want = bar_rect(bar.attach, (l, t, r, b))
    check("control bar follows the panel", abs(bl - want[0]) <= 2 and abs(bt - want[1]) <= 2, f"bar {(bl, bt, br, bb)} want {want}")
    if mgr.game_hwnd:
        check("game window maximized", bool(user32.IsZoomed(mgr.game_hwnd)))
    bar.quit()
    check("restore: not TOPMOST", not exstyle(h) & 0x8)
    check("restore: not layered / click-through", not exstyle(h) & (WS_EX_LAYERED | WS_EX_TRANSPARENT))
    log(f"SELFTEST SUMMARY pass={sum(results)} fail={len(results) - sum(results)}")


def set_console_icon() -> None:
    """the console window's icon (start.cmd's; where Windows Terminal hosts the console instead, it keeps its own)"""
    hwnd = kernel32.GetConsoleWindow()
    if not hwnd or not ICON.is_file():
        return
    for which, size in ((1, 32), (0, 16)):          # WM_SETICON: ICON_BIG / ICON_SMALL
        h = user32.LoadImageW(None, str(ICON), 1, size, size, 0x10)          # IMAGE_ICON, LR_LOADFROMFILE
        if h:
            user32.SendMessageW(hwnd, 0x80, which, h)


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    set_dpi_aware()
    set_console_icon()
    mutex = kernel32.CreateMutexW(None, False, "Local\\MetaMuseStagePanelManager")
    if ctypes.get_last_error() == ERROR_ALREADY_EXISTS:
        log("The panel manager is already running: nothing to do.")
        return 0
    conf = HERE / "panel_config.json"
    if not conf.is_file() and (HERE / "panel_config.example.json").is_file():
        conf = HERE / "panel_config.example.json"       # (a fresh clone: the example until this machine's own is made)
        log("No panel_config.json: using panel_config.example.json")
    cfg = json.loads(conf.read_text(encoding="utf-8"))
    mgr = Manager(cfg)
    mgr.conf_path = conf
    mgr.ensure_irodori()
    mgr.ensure_bridge()
    mgr.ensure_game()
    if not mgr.ensure_panel():
        log("No panel window: exiting")
        return 1
    bar = ControlBar(mgr, str(cfg.get("controls_attach", "bottom_outside")))
    tid = hotkey_thread(str(cfg.get("hotkey_quit", "ctrl+alt+q")), lambda: bar.root.after(0, bar.quit))
    log(f"Resident: hover {cfg.get('hover_poll_ms', 100)} ms; quit with the bar or {cfg.get('hotkey_quit', 'ctrl+alt+q')}")
    if "--selftest" in sys.argv:
        bar.root.after(1500, lambda: selftest(mgr, bar))
    try:
        bar.run()
    finally:
        if tid:
            user32.PostThreadMessageW(tid, WM_QUIT, 0, 0)
        kernel32.CloseHandle(mutex)
    log("Panel manager ended")
    return 0


if __name__ == "__main__":
    sys.exit(main())
