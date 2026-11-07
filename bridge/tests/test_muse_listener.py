"""Muse's own listener (bridge/muse_listener.py; PR #31 and GPT's design review): requests are let through by the machine
they come from (the real TCP sender looked up with Tailscale - here a stand-in, fake_tailscale.py), plus the aid of Muse's
usual Authorization, for Muse's own API only; then the bridge gets them with the real Muse token. The tests listen on
127.0.0.1 (allowed with --test only): the stand-in maps 127.0.0.1 to a machine.
"""
from __future__ import annotations

import json
import socket
import sys
import tempfile
import time
import unittest
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import ROOT, Bridge  # noqa: E402

MUSE = "nMUSE1234CNTRL"
OTHER = "nOTHER5678CNTRL"
FAKE = str(Path(__file__).resolve().parent / "fake_tailscale.py")
PLACEHOLDER = "Bearer stand-in-for-the-key-0123456789"


def listener_cfg(table: Path, port: int, **over) -> dict:
    lc = {"enabled": True, "address": "127.0.0.1", "port": port, "machine": MUSE, "tailscale": [sys.executable, "-B", FAKE, str(table)],
          "placeholder": "learn", "whois_ttl": 0}
    lc.update(over)
    return {"allowed_hosts": ["mypc.tailnet.example"], "muse_listener": lc}


def table_file(entries: dict) -> Path:
    p = Path(tempfile.mkdtemp(prefix="fake_ts_")) / "map.json"
    p.write_text(json.dumps(entries), encoding="utf-8")
    return p


def call(port: int, method: str, path: str, body=None, auth: str | None = PLACEHOLDER, host: str = "mypc.tailnet.example",
         headers: dict | None = None):
    h = {"Host": f"{host}:{port}", "Content-Type": "application/json"}
    if auth is not None:
        h["Authorization"] = auth
    h.update(headers or {})
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=data, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            text = r.read()
            return r.status, json.loads(text) if text else None
    except urllib.error.HTTPError as e:
        text = e.read()
        try:
            return e.code, json.loads(text) if text else None
        except ValueError:
            return e.code, None


def is_open(port: int) -> bool:
    s = socket.socket()
    s.settimeout(1)
    try:
        return s.connect_ex(("127.0.0.1", port)) == 0
    finally:
        s.close()


class MuseListener(unittest.TestCase):
    LP = 18877

    @classmethod
    def setUpClass(cls):
        cls.table = table_file({"127.0.0.1": MUSE})
        cls.b = Bridge(18876, extra=listener_cfg(cls.table, cls.LP), test=True)

    @classmethod
    def tearDownClass(cls):
        cls.b.close()

    def setUp(self):
        self.table.write_text(json.dumps({"127.0.0.1": MUSE}), encoding="utf-8")

    def test_a_muse_reads_and_sends_through_it(self):
        code, health = call(self.LP, "GET", "/health")
        self.assertEqual(code, 200)
        self.assertIn("launch", health, "the full /health (the Muse token was put in)")
        self.assertEqual(call(self.LP, "GET", "/api/state")[0], 200)
        code, ans = call(self.LP, "POST", "/api/packages", {"session": "s-listener", "turn": 1, "blocks": [{"type": "dialogue", "text": "やあ"}]})
        self.assertEqual(code, 200, ans)
        self.assertTrue(ans["accepted"])
        self.assertEqual(call(self.LP, "GET", "/api/packages/status?session=s-listener&turn=1")[0], 200)
        self.assertEqual(call(self.LP, "GET", "/api/events?after=0")[0], 200)

    def test_b_the_learned_placeholder_is_asked_for(self):
        call(self.LP, "GET", "/health")                                  # (learned by now, whichever test ran first)
        code, ans = call(self.LP, "GET", "/api/state", auth="Bearer another-stand-in")
        self.assertEqual((code, ans["reason"]), (401, "placeholder"))
        code, ans = call(self.LP, "GET", "/api/state", auth=None)
        self.assertEqual((code, ans["reason"]), (401, "placeholder"))
        learned = (self.b.tmp / "cache" / "muse_listener.json").read_text(encoding="utf-8")
        self.assertNotIn("stand-in", learned, "only the digest is kept")

    def test_c_another_machine_is_refused_whatever_its_headers(self):
        self.table.write_text(json.dumps({"127.0.0.1": OTHER}), encoding="utf-8")
        code, ans = call(self.LP, "GET", "/api/state", headers={"X-Forwarded-For": "100.64.0.9", "X-Real-IP": "100.64.0.9",
                                                                "Tailscale-User-Login": "someone@example.invalid"})
        self.assertEqual((code, ans["reason"]), (403, "not_muse_machine"))

    def test_d_an_unknown_sender_is_refused(self):
        self.table.write_text(json.dumps({"127.0.0.1": "fail"}), encoding="utf-8")
        code, ans = call(self.LP, "GET", "/health")
        self.assertEqual((code, ans["reason"]), (403, "unverified"))

    def test_e_the_apps_ways_and_the_test_way_are_not_passed(self):
        for method, path, body in (("GET", "/api/packages/next", None), ("POST", "/api/packages/ack", {"id": "x", "stage": "received"}),
                                   ("POST", "/api/launch", {"launch": "x", "phase": "app_starting"}), ("POST", "/api/events", {"type": "x"}),
                                   ("POST", "/api/state", {}), ("POST", "/api/test/faults", {}), ("HEAD", "/health", None),
                                   ("GET", "/api/match/rooms/../../api/packages/next", None), ("GET", "/", None)):
            with self.subTest(method=method, path=path):
                code, ans = call(self.LP, method, path, body)
                self.assertEqual(code, 403)
                if ans is not None:
                    self.assertEqual(ans["reason"], "not_allowed")

    def test_f_a_browser_and_a_foreign_host_are_refused(self):
        code, ans = call(self.LP, "GET", "/api/state", headers={"Origin": "https://example.invalid"})
        self.assertEqual((code, ans["reason"]), (403, "browser"))
        code, ans = call(self.LP, "GET", "/api/state", host="evil.example")
        self.assertEqual((code, ans["reason"]), (403, "host"))

    def test_g_the_bridge_itself_still_asks_for_its_token(self):
        self.assertEqual(self.b.call("GET", "/api/state", role=None)[0], 401)
        self.assertEqual(self.b.call("GET", "/api/state", role="muse")[0], 200)

    def test_h_new_tokens_are_taken_without_muse_doing_anything(self):
        sys.path.insert(0, str(ROOT / "bridge"))
        import bridge as _bridge
        _bridge.save_tokens(self.b.tmp / "cache" / "tokens.json", {"muse": "m" * 43, "app": "a" * 43})
        self.assertEqual(call(self.LP, "GET", "/api/state")[0], 200)


class NotOpened(unittest.TestCase):
    def test_no_machine_set_no_port(self):
        b = Bridge(18878, extra=listener_cfg(table_file({"127.0.0.1": MUSE}), 18879, machine=""), test=True)
        try:
            self.assertFalse(is_open(18879))
            self.assertEqual(b.call("GET", "/api/state", role=None)[0], 401)      # (auth stays required)
        finally:
            b.close()

    def test_loopback_outside_the_tests_no_port(self):
        b = Bridge(18880, extra=listener_cfg(table_file({"127.0.0.1": MUSE}), 18881), test=False)
        try:
            self.assertFalse(is_open(18881))
        finally:
            b.close()

    def test_not_enabled_no_port(self):
        b = Bridge(18882, extra=listener_cfg(table_file({"127.0.0.1": MUSE}), 18883, enabled=False), test=True)
        try:
            self.assertFalse(is_open(18883))
        finally:
            b.close()


class DigestGiven(unittest.TestCase):
    def test_a_given_digest(self):
        import hashlib
        digest = hashlib.sha256(PLACEHOLDER.split(" ", 1)[1].encode("utf-8")).hexdigest()
        b = Bridge(18884, extra=listener_cfg(table_file({"127.0.0.1": MUSE}), 18885, placeholder=digest), test=True)
        try:
            self.assertEqual(call(18885, "GET", "/api/state")[0], 200)
            self.assertEqual(call(18885, "GET", "/api/state", auth="Bearer other")[0], 401)
        finally:
            b.close()

    def test_off_looks_at_the_machine_only(self):
        b = Bridge(18886, extra=listener_cfg(table_file({"127.0.0.1": MUSE}), 18887, placeholder="off"), test=True)
        try:
            self.assertEqual(call(18887, "GET", "/api/state", auth=None)[0], 200)
        finally:
            b.close()


class StartConditions(unittest.TestCase):
    """GPT's post-review 1: only this PC's own tailnet address (in Tailscale's ranges), never every interface, loopback
    only in the tests, and only with the bridge's auth required"""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT / "bridge"))
        import muse_listener
        cls.ml = muse_listener

    def addr(self, given: str, own: str, test: bool):
        cli = [sys.executable, "-B", FAKE, str(table_file({"_self": own}))]
        return self.ml.listen_address({"address": given} if given else {}, cli, test)

    def test_this_pcs_tailnet_address(self):
        self.assertEqual(self.addr("", "100.101.1.1", False), ("100.101.1.1", None))
        self.assertEqual(self.addr("100.101.1.1", "100.101.1.1", False), ("100.101.1.1", None))

    def test_refused_addresses(self):
        for given, own, test in (("0.0.0.0", "100.101.1.1", False), ("::", "100.101.1.1", True), ("0.0.0.0", "100.101.1.1", True),
                                 ("127.0.0.1", "100.101.1.1", False), ("100.100.100.100", "100.101.1.1", False),
                                 ("192.168.1.5", "192.168.1.5", False), ("", "192.168.1.5", False), ("", "", False),
                                 ("junk", "100.101.1.1", False), ("fd7a:115c:a1e0::1", "fd7a:115c:a1e0::1", False),
                                 ("", "fd7a:115c:a1e0::1", False)):
            with self.subTest(given=given, own=own, test=test):
                address, why = self.addr(given, own, test)
                self.assertIsNone(address)
                self.assertTrue(why)

    def test_loopback_in_the_tests(self):
        self.assertEqual(self.addr("127.0.0.1", "", True), ("127.0.0.1", None))

    def test_not_with_auth_off(self):
        class State:
            auth = False
        lc = listener_cfg(table_file({"127.0.0.1": MUSE}), 18890)["muse_listener"]
        cfg = {"muse_listener": lc, "_test": True, "_tokens_file": Path(tempfile.mkdtemp()) / "tokens.json"}
        self.assertIsNone(self.ml.start(cfg, State(), 18765, lambda h, c: True, 1024))
        self.assertFalse(is_open(18890))

    def test_not_with_auth_off_in_a_bridge(self):
        extra = listener_cfg(table_file({"127.0.0.1": MUSE}), 18891)
        extra["auth"] = "off"
        b = Bridge(18892, extra=extra, test=True)
        try:
            self.assertFalse(is_open(18891))
        finally:
            b.close()


class BadSettingsKeepTheBridge(unittest.TestCase):
    """#37's post-review 1: a wrong listener setting closes the listener only - the bridge runs on, asking for its token"""

    def test_wrong_numbers(self):
        for i, over in enumerate(({"read_seconds": "abc"}, {"max_connections": 0}, {"whois_ttl": -1}, {"port": "x"},
                                  {"read_seconds": "nan"}, {"max_connections": True}, {"max_connections": 2.5},
                                  {"read_seconds": 1e9}, {"port": 70000})):
            with self.subTest(over=over):
                lp = 18900 + 2 * i
                extra = listener_cfg(table_file({"127.0.0.1": MUSE}), lp)
                extra["muse_listener"].update(over)
                b = Bridge(lp + 1, extra=extra, test=True)          # (it starts: /health answers - the bridge did not stop)
                try:
                    self.assertFalse(is_open(lp))
                    self.assertEqual(b.call("GET", "/api/state", role=None)[0], 401)
                    self.assertEqual(b.call("GET", "/api/state", role="muse")[0], 200)
                finally:
                    b.close()

    def test_not_an_object(self):
        extra = listener_cfg(table_file({"127.0.0.1": MUSE}), 18930)
        extra["muse_listener"] = "yes"
        b = Bridge(18931, extra=extra, test=True)
        try:
            self.assertFalse(is_open(18930))
            self.assertEqual(b.call("GET", "/api/state", role=None)[0], 401)
        finally:
            b.close()


class PlaceholderFailsClosed(unittest.TestCase):
    """GPT's post-review 2: a wrong setting refuses; the digest is learnt only when there is none, never over a file that
    cannot be read"""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT / "bridge"))
        import muse_listener
        cls.ml = muse_listener

    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="ph_"))
        self.file = self.dir / "muse_listener.json"

    def test_a_wrong_setting_refuses_everything(self):
        for mode in ("lern", "ON", "true", "abc123", 1):
            with self.subTest(mode=mode):
                ph = self.ml.Placeholder(mode, self.file)
                self.assertFalse(ph.check(PLACEHOLDER))
                self.assertFalse(self.file.exists())

    def test_no_setting_is_learn(self):
        ph = self.ml.Placeholder(None, self.file)
        self.assertTrue(ph.check(PLACEHOLDER))
        self.assertTrue(ph.check(PLACEHOLDER))
        self.assertFalse(ph.check("Bearer other"))
        self.assertNotIn("stand-in", self.file.read_text(encoding="utf-8"))

    def test_an_unreadable_file_is_not_learnt_over(self):
        for content in ("not json", "{}", '{"placeholder_sha256": "short"}', "[1, 2]"):
            with self.subTest(content=content):
                self.file.write_text(content, encoding="utf-8")
                ph = self.ml.Placeholder("learn", self.file)
                self.assertFalse(ph.check(PLACEHOLDER))
                self.assertEqual(self.file.read_text(encoding="utf-8"), content, "the file is left as it was")

    def test_a_file_not_in_utf8_is_refused_and_kept(self):
        raw = bytes([0xFF, 0xFE]) + b'{"placeholder_sha256": ' + bytes([0x80]) + b"}"
        self.file.write_bytes(raw)
        ph = self.ml.Placeholder("learn", self.file)
        self.assertFalse(ph.check(PLACEHOLDER))
        self.assertEqual(self.file.read_bytes(), raw)

    def test_a_file_that_cannot_be_read_is_not_learnt_over(self):
        self.file.mkdir()                                  # (there, but not readable as a file)
        ph = self.ml.Placeholder("learn", self.file)
        self.assertFalse(ph.check(PLACEHOLDER))
        self.assertTrue(self.file.is_dir())

    def test_the_owners_reset_learns_again(self):
        ph = self.ml.Placeholder("learn", self.file)
        self.assertTrue(ph.check(PLACEHOLDER))
        self.assertFalse(ph.check("Bearer a-new-stand-in"))
        self.assertEqual(self.ml.forget_placeholder(self.dir / "tokens.json"), 0)
        self.assertTrue(ph.check("Bearer a-new-stand-in"))

    def test_a_wrong_setting_in_a_bridge(self):
        b = Bridge(18893, extra=listener_cfg(table_file({"127.0.0.1": MUSE}), 18894, placeholder="lern"), test=True)
        try:
            code, ans = call(18894, "GET", "/api/state")
            self.assertEqual((code, ans["reason"]), (401, "placeholder"))
        finally:
            b.close()


class ConnectionLimits(unittest.TestCase):
    """GPT's post-review 3: a request that does not come in time is cut; past max_connections a connection is closed at once"""
    LP = 18896

    @classmethod
    def setUpClass(cls):
        cls.b = Bridge(18895, extra=listener_cfg(table_file({"127.0.0.1": MUSE}), cls.LP, placeholder="off", read_seconds=2,
                                                 max_connections=2), test=True)

    @classmethod
    def tearDownClass(cls):
        cls.b.close()

    @staticmethod
    def closed_within(s: socket.socket, seconds: float) -> bool:
        s.settimeout(seconds)
        try:
            return s.recv(1024) == b""
        except (ConnectionResetError, ConnectionAbortedError):
            return True
        except socket.timeout:
            return False

    def test_a_stalled_request_is_cut(self):
        s = socket.create_connection(("127.0.0.1", self.LP))
        try:
            s.sendall(b"GET /health HTTP/1.1\r\nHost: mypc.tailnet.example\r\n")      # (the headers never end)
            t0 = time.monotonic()
            self.assertTrue(self.closed_within(s, 6))
            self.assertLess(time.monotonic() - t0, 5)
        finally:
            s.close()

    def test_a_stalled_body_is_cut_and_not_passed(self):
        s = socket.create_connection(("127.0.0.1", self.LP))
        try:
            s.sendall(b"POST /api/packages HTTP/1.1\r\nHost: mypc.tailnet.example\r\nContent-Type: application/json\r\n"
                      b"Content-Length: 500\r\n\r\n{\"session\": ")
            self.assertTrue(self.closed_within(s, 6))
        finally:
            s.close()
        self.assertEqual(self.b.call("GET", "/api/packages/status?session=stalled&turn=1", role="muse")[0], 404)

    def test_past_the_number_a_connection_is_closed_at_once(self):
        held = [socket.create_connection(("127.0.0.1", self.LP)) for _ in range(2)]
        try:
            time.sleep(0.3)
            extra = socket.create_connection(("127.0.0.1", self.LP))
            try:
                self.assertTrue(self.closed_within(extra, 1.0), "the third connection is closed at once")
            finally:
                extra.close()
        finally:
            for s in held:
                s.close()
        time.sleep(2.5)                                   # (the held ones cut or closed: their slots free again)
        self.assertEqual(call(self.LP, "GET", "/health", auth=None)[0], 200)


if __name__ == "__main__":
    unittest.main()
