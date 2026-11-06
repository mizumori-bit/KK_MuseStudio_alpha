"""GPT's re-reviews of #26 / #27 / #28 (2026-11-05): a new launch frees the queue's bytes too; a relay line that is not a
message object is passed over; an oversized old feed file is kept aside, not read; the tokens' file read again when it
changes; one Host of a name's shape; HEAD does not take a package; refusals are counted against the rates. No game, no
outside network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import http.client
import io
import json
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import bridge  # noqa: E402
import muse_match as mm  # noqa: E402
from _helpers import ROOT, Bridge, package  # noqa: E402


def raw(b: Bridge, method: str, path: str, hosts: list, token: str | None = None):
    c = http.client.HTTPConnection("127.0.0.1", b.port, timeout=10)
    c.putrequest(method, path, skip_host=True)
    for h in hosts:
        c.putheader("Host", h)
    if token:
        c.putheader("Authorization", "Bearer " + token)
    c.endheaders()
    r = c.getresponse()
    r.read()
    c.close()
    return r.status


class QueueAndHttp(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.b = Bridge(18801)

    @classmethod
    def tearDownClass(cls):
        cls.b.close()

    def test_a_new_launch_frees_the_queue_bytes(self):          # #26 P1: the empty queue still looked full
        bigs = (json.dumps(package("あ" * (512 * 1024), turn), ensure_ascii=False).encode("utf-8") for turn in range(1, 100))
        big = next(bigs)
        n = 0
        while self.b.call("POST", "/api/packages", raw=big)[0] == 200:      # (a new turn each time: the same content is one package)
            n += 1
            self.assertLess(n, 20)
            big = next(bigs)
        self.assertEqual(self.b.call("POST", "/api/packages", raw=big)[0], 429)
        self.assertEqual(self.b.call("POST", "/api/launch", {"launch": "fresh-1", "phase": "app_starting"}, role="app")[0], 200)
        self.assertEqual(self.b.call("GET", "/api/packages/next", role="app")[0], 204)
        self.assertEqual(self.b.call("POST", "/api/packages", package("small"))[0], 200)       # (room again)
        code, pkg = self.b.call("GET", "/api/packages/next", role="app")
        self.assertEqual((code, pkg["blocks"][0]["text"]), (200, "small"))
        self.assertEqual(self.b.call("POST", "/api/packages", raw=next(bigs))[0], 200)
        while self.b.call("GET", "/api/packages/next", role="app")[0] == 200:
            pass

    def test_head_does_not_take_a_package(self):                 # #27 P3
        self.assertEqual(self.b.call("POST", "/api/packages", package("kept"))[0], 200)
        self.assertEqual(raw(self.b, "HEAD", "/api/packages/next", [f"127.0.0.1:{self.b.port}"], self.b.token("app")), 405)
        self.assertEqual(self.b.call("GET", "/api/packages")[1]["queue"], 1)
        code, pkg = self.b.call("GET", "/api/packages/next", role="app")
        self.assertEqual((code, pkg["blocks"][0]["text"]), (200, "kept"))

    def test_one_host_of_a_names_shape(self):                    # #27 P3
        tok = self.b.token("muse")
        here = f"127.0.0.1:{self.b.port}"
        self.assertEqual(raw(self.b, "GET", "/api/state", [here], tok), 200)
        self.assertEqual(raw(self.b, "GET", "/api/state", [here, "evil.example"], tok), 403)    # (two Host headers)
        self.assertEqual(raw(self.b, "GET", "/api/state", [here, here], tok), 403)
        self.assertEqual(raw(self.b, "GET", "/api/state", ["[::1]junk"], tok), 403)
        self.assertEqual(raw(self.b, "GET", "/api/state", ["[::1]:80x"], tok), 403)
        self.assertEqual(raw(self.b, "GET", "/api/state", ["127.0.0.1 :1"], tok), 403)
        self.assertEqual(raw(self.b, "GET", "/api/state", [f"[::1]:{self.b.port}"], tok), 200)
        self.assertEqual(raw(self.b, "GET", "/api/state", ["LOCALHOST."], tok), 200)


class Tokens(unittest.TestCase):                                 # #27 P2: the shown token and the one in use
    def test_the_file_is_read_again_when_it_changes(self):
        b = Bridge(18802)
        self.addCleanup(b.close)
        old = b.token("muse")
        tokens = b.tmp / "cache" / "tokens.json"
        tokens.unlink()
        out = subprocess.run([sys.executable, "-B", str(ROOT / "bridge/bridge.py"), "--config", str(b.tmp / "config.json"), "--print-token", "muse"],
                             capture_output=True, text=True, timeout=30)
        new = out.stdout.strip()
        self.assertNotEqual(new, old)
        self.assertEqual(b.call("GET", "/api/state", headers={"Authorization": "Bearer " + new}, role=None)[0], 200)
        self.assertEqual(b.call("GET", "/api/state", headers={"Authorization": "Bearer " + old}, role=None)[0], 401)
        self.assertEqual(b.call("POST", "/api/state", {"mode": "talk"}, role="app")[0], 200)     # (the app's, read from the file again)

    def test_a_deleted_file_is_written_again_with_the_tokens_in_use(self):
        b = Bridge(18803)
        self.addCleanup(b.close)
        old = b.token("muse")
        (b.tmp / "cache" / "tokens.json").unlink()
        self.assertEqual(b.call("GET", "/api/state", headers={"Authorization": "Bearer " + old}, role=None)[0], 200)
        out = subprocess.run([sys.executable, "-B", str(ROOT / "bridge/bridge.py"), "--config", str(b.tmp / "config.json"), "--print-token", "muse"],
                             capture_output=True, text=True, timeout=30)
        self.assertEqual(out.stdout.strip(), old)


class RatesOnRefusals(unittest.TestCase):                        # #28 P2: refusals were not counted
    def test_refusals_are_counted(self):
        b = Bridge(18804, extra={"rate_limits": {"anon": [0.5, 3], "muse": [0.5, 2], "app": [100000, 100000]}})   # (the helper's /health at the start: one of anon's)
        self.addCleanup(b.close)
        bad = [raw(b, "GET", "/api/state", ["evil.example"]) for _ in range(3)]
        self.assertEqual(bad, [403, 403, 429])
        role = [b.call("POST", "/api/state", {"mode": "talk"}, role="muse")[0] for _ in range(3)]
        self.assertEqual(role, [403, 403, 429])
        self.assertEqual(b.call("OPTIONS", "/api/state", role="app")[0], 204)


class RelayLines(unittest.TestCase):                             # #26 P2: an array line stopped the rest
    def test_a_line_that_is_not_a_message_object_is_passed_over(self):
        body = b'[]\n"text"\n{"id": true, "event": "message", "message": "bad id"}\n{"id": "m1", "event": "message", "message": "ok"}\n'

        class Answer(io.BytesIO):
            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

        class Opener:
            def open(self, url, timeout=None):
                return Answer(body)

        keep = mm._OPENER
        mm._OPENER = Opener()
        try:
            got = mm.NtfyRelay("http://127.0.0.1:1").fetch("t", "all")
        finally:
            mm._OPENER = keep
        self.assertEqual(got, [("m1", "ok")])


class OversizeFeedFile(unittest.TestCase):                       # #26 P3: read whole before the limits
    def test_kept_aside_not_read(self):
        tmp = Path(tempfile.mkdtemp(prefix="bridge_oversize_"))
        self.addCleanup(shutil.rmtree, tmp, True)
        f = tmp / "events.json"
        f.write_text(json.dumps({"next_id": 50, "events": [{"id": i, "type": "t", "pad": "x" * 200} for i in range(1, 40)]}), encoding="utf-8")
        size = f.stat().st_size
        keep = bridge.MAX_EVENTS_FILE_BYTES
        bridge.MAX_EVENTS_FILE_BYTES = 1000
        try:
            st = bridge.State({"_audio_dir": tmp / "audio", "_events_file": f, "irodori": {"enabled": False}})
            if st.match is not None:
                st.match.stop.set()
        finally:
            bridge.MAX_EVENTS_FILE_BYTES = keep
        aside = list(tmp.glob("events.json.oversize-*"))
        self.assertEqual(len(aside), 1)
        self.assertEqual(aside[0].stat().st_size, size)              # (kept as it was)
        self.assertEqual(st.peek_events(0, 100), [])
        self.assertGreater(st.next_event_id, int(time.time() * 1000) - 60000)
        eid = st.add_event({"type": "after"})
        self.assertGreater(eid, 50)


class OversizeAsideFails(unittest.TestCase):                     # #32 P1: a failed move let the first event replace the old file
    def _state(self, tmp, f):
        keep = bridge.MAX_EVENTS_FILE_BYTES
        bridge.MAX_EVENTS_FILE_BYTES = 1000
        try:
            st = bridge.State({"_audio_dir": tmp / "audio", "_events_file": f, "irodori": {"enabled": False}})
        finally:
            bridge.MAX_EVENTS_FILE_BYTES = keep
        if st.match is not None:
            st.match.stop.set()
        return st

    def _old_file(self):
        tmp = Path(tempfile.mkdtemp(prefix="bridge_aside_"))
        self.addCleanup(shutil.rmtree, tmp, True)
        f = tmp / "events.json"
        f.write_text(json.dumps({"next_id": 50, "events": [{"id": i, "type": "t", "pad": "x" * 200} for i in range(1, 40)]}), encoding="utf-8")
        return tmp, f, f.read_bytes()

    def _no_move(self):
        real = bridge.os.replace

        def replace(src, dst):
            if "oversize" in str(dst):
                raise PermissionError("locked (test)")
            return real(src, dst)
        bridge.os.replace = replace
        self.addCleanup(setattr, bridge.os, "replace", real)

    def test_copied_aside_when_it_cannot_be_moved(self):
        tmp, f, before = self._old_file()
        self._no_move()
        st = self._state(tmp, f)
        aside = list(tmp.glob("events.json.oversize-*"))
        self.assertEqual(len(aside), 1)
        self.assertEqual(aside[0].read_bytes(), before)               # (a copy, whole)
        self.assertEqual(st.peek_events(0, 100), [])
        self.assertGreater(st.add_app_event({"type": "after"}, "c1")[0], 50)

    def test_left_as_it_is_and_no_event_taken_when_it_cannot_be_kept(self):
        tmp, f, before = self._old_file()
        self._no_move()
        real_copy = bridge.shutil.copyfile

        def copyfile(src, dst, *a, **k):
            raise PermissionError("locked (test)")
        bridge.shutil.copyfile = copyfile
        self.addCleanup(setattr, bridge.shutil, "copyfile", real_copy)
        st = self._state(tmp, f)
        self.assertTrue(st.events_frozen)
        with self.assertRaises(OSError):
            st.add_app_event({"type": "after"}, "c1")
        self.assertEqual(st.add_event({"type": "bridge_own"}), 0)
        self.assertEqual(f.read_bytes(), before)                      # (the old file untouched)
        self.assertEqual(list(tmp.glob("events.json.oversize-*")), [])


class NewTokens(unittest.TestCase):                              # #32 P2: an app's poll between deleting and printing
    def test_new_tokens_while_running(self):
        b = Bridge(18805)
        self.addCleanup(b.close)
        old_muse, old_app = b.token("muse"), b.token("app")
        self.assertEqual(b.call("GET", "/api/packages/next", role="app")[0], 204)          # (the app polling, as ever)
        out = subprocess.run([sys.executable, "-B", str(ROOT / "bridge/bridge.py"), "--config", str(b.tmp / "config.json"), "--new-tokens"],
                             capture_output=True, text=True, timeout=30)
        self.assertEqual(out.returncode, 0)
        self.assertEqual(b.call("GET", "/api/packages/next", role="app")[0], 204)          # (the new app token, read from the file)
        new_muse = subprocess.run([sys.executable, "-B", str(ROOT / "bridge/bridge.py"), "--config", str(b.tmp / "config.json"), "--print-token", "muse"],
                                  capture_output=True, text=True, timeout=30).stdout.strip()
        self.assertNotIn(new_muse, (old_muse, old_app))
        self.assertEqual(b.call("GET", "/api/state", headers={"Authorization": "Bearer " + new_muse}, role=None)[0], 200)
        self.assertEqual(b.call("GET", "/api/state", headers={"Authorization": "Bearer " + old_muse}, role=None)[0], 401)
        self.assertEqual(b.call("GET", "/api/packages/next", headers={"Authorization": "Bearer " + old_app}, role=None)[0], 401)
        self.assertEqual(b.call("POST", "/api/state", {"mode": "talk"}, role="app")[0], 200)


class HeadOnTheTakingWays(unittest.TestCase):                    # #32 P3: %6Eext and the old choice queue
    def test_head_takes_nothing(self):
        b = Bridge(18806)
        self.addCleanup(b.close)
        here = f"127.0.0.1:{b.port}"
        self.assertEqual(b.call("POST", "/api/packages", package("kept"))[0], 200)
        self.assertEqual(raw(b, "HEAD", "/api/packages/%6Eext", [here], b.token("app")), 405)
        self.assertEqual(b.call("GET", "/api/packages")[1]["queue"], 1)
        self.assertEqual(b.call("POST", "/api/choices", {"session": "t", "turn": 1, "kind": "stance", "value": "a"}, role="app")[0], 200)
        self.assertEqual(raw(b, "HEAD", "/api/choices", [here], b.token("muse")), 405)
        self.assertEqual(raw(b, "HEAD", "/api/%63hoices", [here], b.token("muse")), 405)
        code, doc = b.call("GET", "/api/choices")
        self.assertEqual((code, len(doc["choices"])), (200, 1))


class FeedFileNotReadable(unittest.TestCase):                    # #34 P1: an unreadable normal file was replaced by the first event
    def _file(self):
        tmp = Path(tempfile.mkdtemp(prefix="bridge_unread_"))
        self.addCleanup(shutil.rmtree, tmp, True)
        f = tmp / "events.json"
        f.write_text(json.dumps({"next_id": 8, "events": [{"id": i, "type": "old", "n": i} for i in range(1, 8)], "event_ids": [["c3", 3]]}),
                     encoding="utf-8")
        return tmp, f, f.read_bytes()

    def _state(self, tmp, f):
        st = bridge.State({"_audio_dir": tmp / "audio", "_events_file": f, "irodori": {"enabled": False}})
        if st.match is not None:
            st.match.stop.set()
        return st

    def test_no_event_until_it_reads_then_the_feed_as_it_was(self):
        tmp, f, before = self._file()
        real = Path.read_text
        locked = {"on": True}

        def read_text(self_, *a, **k):
            if locked["on"] and self_.name == "events.json":
                raise PermissionError("held by another program (test)")
            return real(self_, *a, **k)
        Path.read_text = read_text
        self.addCleanup(setattr, Path, "read_text", real)
        st = self._state(tmp, f)
        self.assertTrue(st.events_frozen)
        with self.assertRaises(OSError):
            st.add_app_event({"type": "new"}, "c9")
        with self.assertRaises(OSError):
            st.ack_events(set(), 1)
        self.assertEqual(st.add_event({"type": "bridge_own"}), 0)
        self.assertEqual(f.read_bytes(), before)                      # (the file untouched)
        locked["on"] = False                                          # (the other program lets go)
        eid, again = st.add_app_event({"type": "new"}, "c9")
        self.assertEqual((eid, again), (8, False))
        feed = st.peek_events(0, 100)
        self.assertEqual([e["id"] for e in feed], list(range(1, 9)))
        self.assertEqual(st.add_app_event({"type": "old"}, "c3"), (3, True))          # (the app's ids came back too)

    def test_a_garbled_file_is_copied_aside_first(self):
        tmp = Path(tempfile.mkdtemp(prefix="bridge_garbled_"))
        self.addCleanup(shutil.rmtree, tmp, True)
        f = tmp / "events.json"
        f.write_text('{"next_id": 5, "events": [', encoding="utf-8")
        st = self._state(tmp, f)
        aside = list(tmp.glob("events.json.unreadable-*"))
        self.assertEqual(len(aside), 1)
        self.assertEqual(aside[0].read_text(encoding="utf-8"), '{"next_id": 5, "events": [')
        self.assertFalse(st.events_frozen)
        self.assertGreater(st.add_event({"type": "after"}), int(time.time() * 1000) - 60000)

    def test_a_missing_file_is_a_first_start(self):
        tmp = Path(tempfile.mkdtemp(prefix="bridge_first_"))
        self.addCleanup(shutil.rmtree, tmp, True)
        st = self._state(tmp, tmp / "events.json")
        self.assertFalse(st.events_frozen)
        self.assertEqual(st.add_event({"type": "first"}), 1)


class TokensReplacedBetweenLooks(unittest.TestCase):              # #34 P2: a new file recorded, its tokens never read
    def test_the_stamp_kept_is_the_one_read(self):
        tmp = Path(tempfile.mkdtemp(prefix="bridge_tokrace_"))
        self.addCleanup(shutil.rmtree, tmp, True)
        st = bridge.State({"_audio_dir": tmp / "audio", "_events_file": tmp / "events.json", "irodori": {"enabled": False}})
        if st.match is not None:
            st.match.stop.set()
        old = dict(st.tokens_now())
        stale = st._tokens_stamp()
        real = st._tokens_stamp
        st._tokens_stamp = lambda: stale                              # (this look taken before the new file came)
        bridge.save_tokens(st.tokens_file, {r: "n" * 40 + r for r in bridge.ROLES})
        self.assertEqual(st.tokens_now(), old)                        # (this request: as it looked)
        st._tokens_stamp = real
        now = st.tokens_now()                                         # (the next: the new file read)
        self.assertEqual(now["muse"], "n" * 40 + "muse")
        self.assertEqual(now["app"], "n" * 40 + "app")


class TokensReplacedAtStart(unittest.TestCase):                  # #35 P2: new tokens made between the start's read and its look
    def test_the_first_request_reads_the_file(self):
        tmp = Path(tempfile.mkdtemp(prefix="bridge_tokstart_"))
        self.addCleanup(shutil.rmtree, tmp, True)
        tokens = tmp / "tokens.json"
        bridge.save_tokens(tokens, {r: "o" * 40 + r for r in bridge.ROLES})
        real = bridge.load_tokens

        def load_then_replaced(path):
            got = real(path)
            bridge.save_tokens(path, {r: "n" * 40 + r for r in bridge.ROLES})      # (--new-tokens, right after the start read them)
            return got
        bridge.load_tokens = load_then_replaced
        try:
            st = bridge.State({"_audio_dir": tmp / "audio", "_events_file": tmp / "events.json", "_tokens_file": tokens, "irodori": {"enabled": False}})
        finally:
            bridge.load_tokens = real
        if st.match is not None:
            st.match.stop.set()
        self.assertEqual(st.tokens["muse"], "o" * 40 + "muse")      # (what the start read)
        now = st.tokens_now()                                          # (the first request: the file as it is)
        self.assertEqual((now["muse"], now["app"]), ("n" * 40 + "muse", "n" * 40 + "app"))


if __name__ == "__main__":
    unittest.main()
