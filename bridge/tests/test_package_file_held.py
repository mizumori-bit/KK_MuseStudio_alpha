"""GPT's post-review of #26, point 1: the package queue's file not readable when the bridge starts (another program holds
it) must not be written over - nothing is taken (503) until it reads, and then the queue goes on as it was. A garbled file
is copied aside before the queue starts empty.
"""
from __future__ import annotations

import json
import os
import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import Bridge  # noqa: E402

OLD = {"session": "s-old", "turn": 1, "blocks": [{"type": "dialogue", "text": "前のパッケージ"}], "_pid": "p-old", "_received_ms": 0}


def queue_doc() -> dict:
    now = time.time()
    return {"version": 1, "launch": None, "queue": [OLD],
            "log": [{"id": "p-old", "session": "s-old", "turn": 1, "stage": "queued", "queued": "x", "queued_ts": now, "updated_ts": now}]}


def package(turn: int) -> dict:
    return {"session": "s-held", "turn": turn, "blocks": [{"type": "dialogue", "text": f"新しい {turn}"}]}


@unittest.skipUnless(os.name == "nt", "the file is held with a Windows byte-range lock")
class HeldAtTheStart(unittest.TestCase):
    def test_kept_refused_then_resumed(self):
        import msvcrt
        d = Path(tempfile.mkdtemp(prefix="pkg_held_"))
        f = d / "packages.json"
        raw = json.dumps(queue_doc(), ensure_ascii=False).encode("utf-8")
        f.write_bytes(raw)
        fh = open(f, "r+b")
        msvcrt.locking(fh.fileno(), msvcrt.LK_NBLCK, len(raw))       # (another program holds it: reads fail)
        b = Bridge(18940, extra={"packages_file": str(f)})
        try:
            code, ans = b.call("POST", "/api/packages", package(1))
            self.assertEqual(code, 503, ans)
            self.assertFalse(ans["accepted"])
            code, health = b.call("GET", "/health", role="muse")
            self.assertTrue(health.get("packages_unavailable"))
            self.assertEqual(b.call("GET", "/api/packages/next?ack=1", role="app")[0], 204)
            self.assertEqual(b.call("POST", "/api/launch", {"launch": "L-held", "phase": "app_starting"}, role="app")[0], 503)
            self.assertEqual(b.call("POST", "/api/packages/ack", {"ids": ["p-old"], "stage": "received"}, role="app")[0], 503)
            self.assertEqual(b.call("GET", "/api/packages/status?id=p-old", role="muse")[0], 503)
            msvcrt.locking(fh.fileno(), msvcrt.LK_UNLCK, len(raw))
            fh.close()
            fh = None
            self.assertEqual(f.read_bytes(), raw, "the file was not written over while it could not be read")
            # (it reads now: the queue as it was, and new packages taken)
            code, ans = b.call("POST", "/api/packages", package(2))
            self.assertEqual(code, 200, ans)
            code, st = b.call("GET", "/api/packages/status?id=p-old", role="muse")
            self.assertEqual((code, st["stage"]), (200, "queued"))
            code, first = b.call("GET", "/api/packages/next?ack=1", role="app")
            self.assertEqual((code, first["_pid"]), (200, "p-old"), "the old package first")
            code, health = b.call("GET", "/health", role="muse")
            self.assertIsNone(health.get("packages_unavailable"))
        finally:
            if fh is not None:
                try:
                    msvcrt.locking(fh.fileno(), msvcrt.LK_UNLCK, len(raw))
                except OSError:
                    pass
                fh.close()
            b.close()


@unittest.skipUnless(os.name == "nt", "the file is held with a Windows byte-range lock")
class StampsAfterTheFileReads(unittest.TestCase):
    """GPT's re-review of #39, point 1: the first package taken after the file reads again carries the launch and the
    character the file brought back - a switch before the app takes it drops it (other_character)"""

    def test_switch_right_after(self):
        import msvcrt
        d = Path(tempfile.mkdtemp(prefix="pkg_stamp_"))
        f = d / "packages.json"
        doc = {"version": 1, "launch": {"id": "L-saved", "phase": "character_selected", "started": "x", "updated": "x",
                                        "character": {"id": "card:A.png", "name": "A", "persona": None}}, "queue": [], "log": []}
        raw = json.dumps(doc).encode("utf-8")
        f.write_bytes(raw)
        fh = open(f, "r+b")
        msvcrt.locking(fh.fileno(), msvcrt.LK_NBLCK, len(raw))
        b = Bridge(18946, extra={"packages_file": str(f)})
        try:
            msvcrt.locking(fh.fileno(), msvcrt.LK_UNLCK, len(raw))
            fh.close()
            fh = None
            code, ans = b.call("POST", "/api/packages", package(1))           # (the first use after it reads: untagged)
            self.assertEqual(code, 200, ans)
            code, st = b.call("GET", "/api/packages/status?id=" + ans["id"], role="muse")
            self.assertEqual((code, st["launch"], st["character"]), (200, "L-saved", "card:A.png"))
            self.assertEqual(b.call("POST", "/api/launch", {"launch": "L-saved", "phase": "character_selected",
                                                            "character": {"id": "card:B.png", "name": "B"}}, role="app")[0], 200)
            self.assertEqual(b.call("GET", "/api/packages/next?ack=1", role="app")[0], 204, "not given to B")
            code, st = b.call("GET", "/api/packages/status?id=" + ans["id"], role="muse")
            self.assertEqual((st["stage"], st["reason"]), ("dropped", "other_character"))
        finally:
            if fh is not None:
                try:
                    msvcrt.locking(fh.fileno(), msvcrt.LK_UNLCK, len(raw))
                except OSError:
                    pass
                fh.close()
            b.close()


class NoPathInTheAnswers(unittest.TestCase):
    """GPT's re-review of #39, point 2: a file that cannot be read is told without this PC's path (the log has it)"""

    def test_a_folder_where_the_file_should_be(self):
        d = Path(tempfile.mkdtemp(prefix="pkg_nopath_"))
        f = d / "packages.json"
        f.mkdir()                                          # (there, but not a file: it cannot be read)
        b = Bridge(18948, extra={"packages_file": str(f)})
        try:
            answers = [b.call("POST", "/api/packages", package(1)), b.call("GET", "/health", role="muse"),
                       b.call("GET", "/api/packages/status", role="muse"),
                       b.call("POST", "/api/packages/ack", {"ids": ["x"], "stage": "received"}, role="app"),
                       b.call("POST", "/api/launch", {"launch": "L", "phase": "app_starting"}, role="app")]
            self.assertEqual(answers[0][0], 503)
            self.assertTrue(answers[1][1].get("packages_unavailable"))
            for code, body in answers:
                text = json.dumps(body, ensure_ascii=False)
                with self.subTest(code=code, text=text[:80]):
                    self.assertNotIn(str(d), text)
                    self.assertNotIn(d.name, text)
                    self.assertNotIn(":\\\\", text)
        finally:
            b.close()


class GarbledAtTheStart(unittest.TestCase):
    def test_copied_aside_then_empty(self):
        d = Path(tempfile.mkdtemp(prefix="pkg_garbled_"))
        f = d / "packages.json"
        f.write_bytes(b"not a queue {")
        b = Bridge(18942, extra={"packages_file": str(f)})
        try:
            self.assertEqual(b.call("POST", "/api/packages", package(1))[0], 200)
            asides = list(d.glob("packages.json.unreadable-*"))
            self.assertEqual(len(asides), 1)
            self.assertEqual(asides[0].read_bytes(), b"not a queue {")
        finally:
            b.close()

    def test_not_utf8(self):
        d = Path(tempfile.mkdtemp(prefix="pkg_bytes_"))
        f = d / "packages.json"
        f.write_bytes(bytes([0xFF, 0xFE, 0x80]))
        b = Bridge(18944, extra={"packages_file": str(f)})
        try:
            self.assertEqual(b.call("POST", "/api/packages", package(1))[0], 200)
            self.assertEqual(len(list(d.glob("packages.json.unreadable-*"))), 1)
        finally:
            b.close()


if __name__ == "__main__":
    unittest.main()
