"""Which relays the bridge talks to (the security review of 2026-11-05, SEC-05): only its own default and the ones its own
settings list, a URL read as a structure, no redirect followed. No game, no outside network (servers on spare local ports).

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import sys
import threading
import unittest
import urllib.error
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import muse_match as mm  # noqa: E402


class RelayUrl(unittest.TestCase):
    def test_plain_bases(self):
        self.assertEqual(mm.relay_base("https://ntfy.sh"), "https://ntfy.sh")
        self.assertEqual(mm.relay_base("https://NTFY.sh/"), "https://ntfy.sh")
        self.assertEqual(mm.relay_base("http://192.168.1.20:18793/relay/"), "http://192.168.1.20:18793/relay")

    def test_refused_shapes(self):
        for bad in ("ftp://ntfy.sh", "https://user:pw@ntfy.sh", "https://ntfy.sh@127.0.0.1", "https://ntfy.sh/?x=1",
                    "https://ntfy.sh/#f", "http://127.0.0.1:18765/api/packages/next?drop=", "javascript:alert(1)",
                    "https://ntfy.sh:99999", "https:// ntfy.sh", 42, None,
                    "https://ntfy.sh?", "https://ntfy.sh#", "https://ntfy.sh:0"):        # (GPT's review of PR #18)
            with self.assertRaises(ValueError, msg=repr(bad)):
                mm.relay_base(bad)


@unittest.skipUnless(mm.AVAILABLE, "the match needs the cryptography package")
class RelayAllow(unittest.TestCase):
    def make(self, **cfg):
        m = mm.Match(lambda e: None, cfg)
        self.addCleanup(m.stop.set)
        return m

    def test_default_relay_allowed(self):
        m = self.make()
        self.assertEqual(m.create({"role": "host"})["relay"], "https://ntfy.sh")
        self.assertEqual(m.create({"role": "host", "relay": {"url": "https://ntfy.sh/"}})["relay"], "https://ntfy.sh")

    def test_an_invitations_relay_not_used_by_itself(self):
        m = self.make()
        with self.assertRaises(mm.NotAllowed):
            m.create({"role": "host", "relay": {"url": "http://127.0.0.1:18765"}})
        with self.assertRaises(ValueError):           # (the review's case: the relay's path swallowed by a query)
            m.create({"role": "host", "relay": {"url": "http://127.0.0.1:18765/api/packages/next?x="}})

    def test_a_listed_relay_allowed(self):
        m = self.make(relay_allow=["http://192.168.1.20:18793/"])
        self.assertEqual(m.create({"role": "host", "relay": {"url": "http://192.168.1.20:18793"}})["relay"], "http://192.168.1.20:18793")


class _Redirect(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_GET(self):
        self.send_response(302)
        self.send_header("Location", "http://127.0.0.1:1/elsewhere")
        self.send_header("Content-Length", "0")
        self.end_headers()


class NoRedirect(unittest.TestCase):
    def test_a_redirect_is_not_followed(self):
        srv = ThreadingHTTPServer(("127.0.0.1", 18788), _Redirect)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        try:
            with self.assertRaises(urllib.error.HTTPError) as c:
                mm.NtfyRelay("http://127.0.0.1:18788", timeout=5).fetch("mmtopic", "all")
            self.assertEqual(c.exception.code, 302)
        finally:
            srv.shutdown()
            srv.server_close()


if __name__ == "__main__":
    unittest.main()
