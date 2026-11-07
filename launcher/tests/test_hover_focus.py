"""The launcher's hover focus (panel_manager.HoverFocus; PR #19, GPT's post-review of #21/#22) with the window calls stood in
for: the foreground window is a number the test sets, a switch to the panel succeeds. Windows only (the module loads user32).
  python -B -m unittest discover -s launcher/tests
"""
from __future__ import annotations

import os
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

GAME, PANEL, BAR, OTHER = 100, 200, 201, 300


@unittest.skipUnless(os.name == "nt", "the launcher is for Windows")
class HoverFocusTicks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import panel_manager
        cls.pm = panel_manager

    def setUp(self):
        pm = self.pm
        self.fg = GAME
        self.saved = (pm.foreground, pm.root_of, pm.force_foreground, pm.user32)
        test = self

        class User32:
            @staticmethod
            def IsWindow(h):
                return 1 if h else 0

            @staticmethod
            def IsWindowVisible(h):
                return 1

            @staticmethod
            def IsIconic(h):
                return 0

        def force(h):
            test.fg = h
            return True

        pm.foreground = lambda: self.fg
        pm.root_of = lambda h: h
        pm.force_foreground = force
        pm.user32 = User32
        self.f = pm.HoverFocus(enter_s=0.15, leave_s=0.3, cooldown_s=0.4)
        self.now = 10.0

    def tearDown(self):
        self.pm.foreground, self.pm.root_of, self.pm.force_foreground, self.pm.user32 = self.saved

    def ticks(self, over: bool, seconds: float, buttons: bool = False) -> list:
        out, end = [], self.now + seconds
        while self.now < end:
            out.append(self.f.tick(PANEL, over, True, buttons, self.now, (BAR,)))
            self.now += 0.05
        return out

    def test_hover_takes_and_gives_back(self):
        self.assertIn("focused", self.ticks(True, 0.4))
        self.assertEqual(self.fg, PANEL)
        self.assertIn("restored", self.ticks(False, 0.8))
        self.assertEqual(self.fg, GAME)

    def test_alt_tab_from_the_panel_is_kept_while_the_cursor_stays(self):
        self.ticks(True, 0.4)
        self.assertEqual(self.fg, PANEL)
        self.fg = OTHER                                   # (Alt+Tab: the user's own switch, the cursor still on the panel)
        out = self.ticks(True, 3.0)
        self.assertIn("user_switched", out)
        self.assertNotIn("focused", out)
        self.assertEqual(self.fg, OTHER, "the window the user chose stays in front")
        self.ticks(False, 0.2)                            # (the cursor leaves ...)
        self.assertEqual(self.fg, OTHER, "nothing is given back after the user's switch")
        self.assertIn("focused", self.ticks(True, 0.6))   # (... and comes back: the panel may take it again)
        self.assertEqual(self.fg, PANEL)

    def test_a_click_on_the_panel_then_alt_tab(self):
        self.fg = PANEL                                   # (the user clicked the panel: not this class's switch)
        self.ticks(True, 0.3)
        self.fg = OTHER
        out = self.ticks(True, 3.0)
        self.assertIn("held", out)
        self.assertNotIn("focused", out)
        self.assertEqual(self.fg, OTHER)

    def test_a_switch_between_other_windows_while_waiting(self):
        self.f.tick(PANEL, True, True, False, self.now, (BAR,))   # (the cursor just came onto the panel)
        self.now += 0.05
        self.fg = OTHER                                   # (the user switches before the panel took it)
        out = self.ticks(True, 3.0)
        self.assertNotIn("focused", out)
        self.assertEqual(self.fg, OTHER)

    def test_a_held_button_still_switches_nothing(self):
        out = self.ticks(True, 1.0, buttons=True)
        self.assertNotIn("focused", out)
        self.assertEqual(self.fg, GAME)

    def test_off_resets(self):
        self.ticks(True, 0.4)
        self.fg = OTHER
        self.ticks(True, 0.2)
        self.f.tick(PANEL, True, False, False, self.now, (BAR,))  # (click-through or hidden)
        self.now += 0.05
        self.assertFalse(self.f.held)


if __name__ == "__main__":
    unittest.main()
