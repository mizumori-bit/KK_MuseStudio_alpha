"""Requests per sender (the security review of 2026-11-05, SEC-07 - after SEC-01 told the senders apart): past a sender's
rate 429 with Retry-After, the other senders not held back, room again after a while. No game, no outside network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import sys
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import bridge  # noqa: E402
from _helpers import Bridge  # noqa: E402


class Rate(unittest.TestCase):
    def test_a_sender_past_its_rate(self):
        b = Bridge(18798, extra={"rate_limits": {"muse": [2, 5], "app": [100000, 100000], "anon": [2, 3]}})
        self.addCleanup(b.close)
        codes = [b.call("GET", "/api/state", role="muse")[0] for _ in range(5)]
        self.assertEqual(codes, [200] * 5)                              # (at once: up to its burst)
        code, doc = b.call("GET", "/api/state", role="muse")
        self.assertEqual(code, 429)
        self.assertGreaterEqual(doc["retry_after"], 1)
        self.assertEqual(b.call("POST", "/api/state", {"mode": "talk"}, role="app")[0], 200)     # (the app not held back)
        time.sleep(1.2)
        self.assertEqual(b.call("GET", "/api/state", role="muse")[0], 200)                       # (room again)
        anon = [b.call("GET", "/api/state", role=None)[0] for _ in range(4)]
        self.assertEqual(anon, [401, 401, 401, 429])                    # (without a token too: a flood of 401s is held)

    def test_off_is_one_sender(self):
        b = Bridge(18799, extra={"auth": "off", "rate_limits": {"all": [1, 3]}})      # (the helper's /health at the start takes one)
        self.addCleanup(b.close)
        self.assertEqual([b.call("GET", "/api/state", role=None)[0] for _ in range(3)], [200, 200, 429])

    def test_the_defaults_are_far_above_use(self):
        for sender, (rate, burst) in bridge.RATE_LIMITS.items():
            self.assertGreaterEqual(rate, 20, sender)
            self.assertGreaterEqual(burst, 100, sender)


if __name__ == "__main__":
    unittest.main()
