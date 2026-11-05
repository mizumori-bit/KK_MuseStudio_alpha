"""The bridge's tokens and Host check (the security review of 2026-11-05, SEC-01 and SEC-06): without a token nothing is read,
written, taken or acknowledged; a token of the other role is refused; every request's Host is checked, browser or not.
No game, no outside network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import http.client
import json
import subprocess
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import bridge  # noqa: E402
from _helpers import ROOT, Bridge, package  # noqa: E402

LAUNCH = {"launch": "t-1", "phase": "starting"}


class Auth(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.b = Bridge(18796, extra={"allowed_hosts": ["mypc.tailnet.example"]})

    @classmethod
    def tearDownClass(cls):
        cls.b.close()

    def raw(self, method: str, path: str, host: str | None, token: str | None = None):
        c = http.client.HTTPConnection("127.0.0.1", self.b.port, timeout=10)
        c.putrequest(method, path, skip_host=True)
        if host is not None:
            c.putheader("Host", host)
        if token:
            c.putheader("Authorization", "Bearer " + token)
        c.endheaders()
        r = c.getresponse()
        body = r.read()
        c.close()
        return r.status, body

    def test_nothing_without_a_token(self):
        cases = [("GET", "/api/state", None), ("GET", "/api/events", None), ("POST", "/api/events/ack", {"upto": 0}),
                 ("POST", "/api/packages", package()), ("GET", "/api/packages/next", None), ("POST", "/api/events", {"type": "x"}),
                 ("POST", "/api/state", {}), ("GET", "/api/characters", None), ("GET", "/api/catalog", None),
                 ("POST", "/api/launch", LAUNCH), ("GET", "/api/match", None), ("POST", "/api/match/rooms", {}),
                 ("GET", "/api/choices", None), ("GET", "/kkdata/manifest.json", None), ("GET", "/static/no_web_build.html", None)]
        for method, path, body in cases:
            self.assertEqual(self.b.call(method, path, body, role=None)[0], 401, f"{method} {path}")
            self.assertEqual(self.b.call(method, path, body, role=None, headers={"Authorization": "Bearer not-a-token-of-this-bridge-at-all"})[0],
                             401, f"{method} {path} (a wrong token)")

    def test_the_other_roles_token_is_refused(self):
        for method, path, body in [("POST", "/api/events", {"type": "x"}), ("POST", "/api/state", {}), ("GET", "/api/packages/next", None),
                                   ("POST", "/api/launch", LAUNCH), ("POST", "/api/catalog", {})]:
            self.assertEqual(self.b.call(method, path, body, role="muse")[0], 403, f"muse: {method} {path}")
        for method, path, body in [("GET", "/api/state", None), ("GET", "/api/events", None), ("POST", "/api/events/ack", {"upto": 0}),
                                   ("POST", "/api/packages", package()), ("GET", "/api/characters", None), ("GET", "/api/catalog", None),
                                   ("POST", "/api/match/rooms", {}), ("GET", "/api/match", None), ("GET", "/api/choices", None)]:
            self.assertEqual(self.b.call(method, path, body, role="app")[0], 403, f"app: {method} {path}")

    def test_each_role_does_its_own(self):
        self.assertEqual(self.b.call("POST", "/api/packages", package("t"), role="muse")[0], 200)
        self.assertEqual(self.b.call("GET", "/api/packages/next", role=None)[0], 401)             # (not taken without the token)
        self.assertEqual(self.b.call("GET", "/api/packages", role="muse")[1]["queue"], 1)
        code, pkg = self.b.call("GET", "/api/packages/next", role="app")
        self.assertEqual((code, pkg["blocks"][0]["text"]), (200, "t"))
        code, doc = self.b.call("POST", "/api/events", {"type": "auth_check"}, role="app")
        self.assertEqual(code, 200)
        code, feed = self.b.call("GET", "/api/events", role="muse")
        self.assertIn(doc["id"], [e["id"] for e in feed["events"]])
        self.assertEqual(self.b.call("POST", "/api/events/ack", {"upto": doc["id"]}, role=None)[0], 401)   # (not acknowledged without it)
        self.assertEqual(self.b.call("POST", "/api/events/ack", {"upto": doc["id"]}, role="muse")[0], 200)
        self.assertEqual(self.b.call("POST", "/api/state", {"mode": "talk"}, role="app")[0], 200)
        self.assertEqual(self.b.call("GET", "/api/state", role="muse")[0], 200)

    def test_health_says_only_alive_without_a_token(self):
        code, doc = self.b.call("GET", "/health", role=None)
        self.assertEqual((code, doc), (200, {"ok": True, "auth": "required"}))
        for role in ("app", "muse"):
            code, doc = self.b.call("GET", "/health", role=role)
            self.assertEqual(code, 200)
            self.assertIn("launch", doc)

    def test_the_host_is_checked_browser_or_not(self):           # SEC-06: no browser headers, a foreign Host
        tok = self.b.token("muse")
        self.assertEqual(self.raw("GET", "/api/state", "evil.example", tok)[0], 403)
        self.assertEqual(self.raw("GET", "/api/state", f"evil.example:{self.b.port}", tok)[0], 403)
        self.assertEqual(self.raw("GET", "/health", None)[0], 403)                                  # (no Host at all)
        self.assertEqual(self.raw("GET", "/api/state", f"localhost:{self.b.port}", tok)[0], 200)
        self.assertEqual(self.raw("GET", "/api/state", "mypc.tailnet.example", tok)[0], 200)        # (allowed_hosts)
        self.assertEqual(self.raw("GET", "/api/state", "MyPC.tailnet.example:443", tok)[0], 200)
        self.assertEqual(self.raw("GET", "/api/state", "mypc.tailnet.example.evil.example", tok)[0], 403)

    def test_the_tokens_are_kept_and_printed(self):
        doc = json.loads((self.b.tmp / "cache" / "tokens.json").read_text(encoding="utf-8"))
        self.assertEqual(set(doc), {"muse", "app"})
        self.assertNotEqual(doc["muse"], doc["app"])
        self.assertTrue(all(len(v) >= 32 for v in doc.values()))
        out = subprocess.run([sys.executable, "-B", str(ROOT / "bridge/bridge.py"), "--config", str(self.b.tmp / "config.json"),
                              "--print-token", "muse"], capture_output=True, text=True, timeout=30)
        self.assertEqual(out.stdout.strip(), doc["muse"])


class AuthOff(unittest.TestCase):
    def test_off_is_as_before_but_the_host_still_checked(self):
        b = Bridge(18797, extra={"auth": "off"})
        self.addCleanup(b.close)
        self.assertEqual(b.call("GET", "/api/state", role=None)[0], 200)
        self.assertIn("launch", b.call("GET", "/health", role=None)[1])
        c = http.client.HTTPConnection("127.0.0.1", b.port, timeout=10)
        c.putrequest("GET", "/api/state", skip_host=True)
        c.putheader("Host", "evil.example")
        c.endheaders()
        self.assertEqual(c.getresponse().status, 403)
        c.close()


class Roles(unittest.TestCase):
    def test_every_route_has_its_roles(self):
        self.assertEqual(bridge.roles_for("GET", "/api/match/rooms/abc"), {"muse", "app"})
        self.assertEqual(bridge.roles_for("POST", "/api/match/rooms/abc/send"), {"muse"})
        self.assertEqual(bridge.roles_for("POST", "/api/match/rooms/abc/close"), {"muse", "app"})     # (the user leaving)
        self.assertEqual(bridge.roles_for("POST", "/api/match/rooms"), {"muse"})
        self.assertEqual(bridge.roles_for("HEAD", "/api/state"), {"muse"})
        self.assertEqual(bridge.roles_for("GET", "/api/packages/next"), {"app"})


if __name__ == "__main__":
    unittest.main()
