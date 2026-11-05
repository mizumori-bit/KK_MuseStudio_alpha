"""The match transport's bounds against a hostile peer or relay (the security review of 2026-11-05, SEC-02..04): the cases
the review measured, now refused before anything is passed on. No game, no network (a local relay on a spare port).

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import base64
import json
import os
import sys
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import muse_match as mm  # noqa: E402

EXPIRY = 300.0


@unittest.skipUnless(mm.AVAILABLE, "the match needs the cryptography package")
class MatchLimits(unittest.TestCase):
    def setUp(self):
        self.host = mm.Room("roomtest01", "host", "http://127.0.0.1:1")
        self.guest = mm.Room("roomtest01", "guest", "http://127.0.0.1:1")
        self.host.set_peer(self.guest.keys())
        self.guest.set_peer(self.host.keys())

    def deliver(self, payload: dict):
        """the host's message -> the guest: the last open()'s result"""
        _q, posts = self.host.seal(payload)
        out = None
        for p in posts:
            out = self.guest.open(p, EXPIRY)
        return out

    def test_a_bad_signature_is_refused_before_it_is_held(self):     # GPT's review of PR #16: `s` held unchecked
        _q, posts = self.host.seal({"type": "ping"})
        env = json.loads(posts[0])
        for bad in ("A" * 200000, {"x": 1}, "A" * 85, "!" * 86, 42, ["A" * 86]):
            reason, payload, _q2, _m = self.guest.open(json.dumps(dict(env, s=bad)), EXPIRY)
            self.assertEqual((reason, payload), ("protocol", None), repr(bad)[:40])
            self.assertEqual(self.guest.parts, {})
        reason, payload, _q3, _m = self.guest.open(posts[0], EXPIRY)    # (the next post of the same fetch: taken as usual)
        self.assertEqual((reason, payload["type"]), ("", "ping"))

    def test_the_signature_counts_in_what_is_held(self):           # GPT's review of PR #16: only `d` was counted
        _q, posts = self.host.seal({"type": "ping", "pad": base64.b64encode(os.urandom(6000)).decode("ascii")})
        self.assertGreater(len(posts), 1)
        first = json.loads(posts[0])
        keep = mm.MAX_HELD_CHARS
        mm.MAX_HELD_CHARS = len(first["d"]) + 10                  # (the data alone would fit; with its signature it does not)
        try:
            reason, payload, _q2, _m = self.guest.open(posts[0], EXPIRY)
        finally:
            mm.MAX_HELD_CHARS = keep
        self.assertEqual((reason, payload, self.guest.parts), ("protocol", None, {}))

    def test_normal_message_and_small_gap(self):
        reason, payload, q, missing = self.deliver({"type": "ping"})
        self.assertEqual((reason, payload["type"], q, missing), ("", "ping", 1, []))
        self.host.seq = 4                                    # (2..4 lost on the way)
        reason, payload, q, missing = self.deliver({"type": "ping"})
        self.assertEqual((reason, q, missing), ("", 5, [2, 3, 4]))

    def test_seq_jump_refused_not_listed(self):              # SEC-02: q 50,000 made a 49,999-item list
        self.host.seq = 49999
        reason, payload, q, missing = self.deliver({"type": "ping"})
        self.assertEqual((reason, payload, missing), ("protocol", None, []))
        self.assertEqual(self.guest.max_seen, 0)

    def test_old_seq_beyond_window_is_a_replay(self):
        self.host.seq = 200
        self.assertEqual(self.deliver({"type": "ping"})[0], "")
        for k in range(mm.SEEN_WINDOW // 200 + 1):            # (walk the seq forward within the jump limit)
            self.host.seq += 200
            self.assertEqual(self.deliver({"type": "ping"})[0], "")
        self.host.seq = 1
        self.assertEqual(self.deliver({"type": "ping"})[0], "replay")
        self.assertLessEqual(len(self.guest.seen), 2 * mm.SEEN_WINDOW)

    def test_payload_unpacking_past_the_bound_refused(self):  # SEC-03 A: 5.7 KB became 4 MB
        reason, payload, _q, _m = self.deliver({"type": "package", "x": "a" * (mm.MAX_PAYLOAD_BYTES + 1)})
        self.assertEqual((reason, payload), ("protocol", None))
        reason, payload, _q, _m = self.deliver({"type": "package", "x": "a" * 100000})
        self.assertEqual(reason, "")

    def test_unsigned_fragments_bounded(self):               # SEC-04: five 200,000-character parts were held
        big = json.dumps({"v": mm.PROTOCOL, "f": "h", "q": 1, "i": 1, "n": 2, "d": "x" * 200000})
        self.assertEqual(self.guest.open(big, EXPIRY)[0], "protocol")
        self.assertEqual(self.guest.parts, {})
        for q in range(1, 60):                                # many messages, never completed, never signed
            for i in range(1, 40):
                env = json.dumps({"v": mm.PROTOCOL, "f": "h", "q": q, "i": i, "n": mm.MAX_PARTS, "d": "x" * mm.MAX_PART_CHARS})
                self.guest.open(env, EXPIRY)
        held = sum(len(x) for m in self.guest.parts.values() for x in m["d"].values())
        self.assertLessEqual(len(self.guest.parts), mm.MAX_ASSEMBLING)
        self.assertLessEqual(held, mm.MAX_HELD_CHARS)

    def test_parts_expire_without_new_posts(self):
        env = json.dumps({"v": mm.PROTOCOL, "f": "h", "q": 3, "i": 1, "n": 2, "d": "x" * 100})
        self.guest.open(env, EXPIRY)
        self.guest.parts[3]["at"] -= EXPIRY + 1
        self.guest.prune(EXPIRY)
        self.assertEqual(self.guest.parts, {})


@unittest.skipUnless(mm.AVAILABLE, "the match needs the cryptography package")
class RelayRead(unittest.TestCase):
    def test_relay_answer_bounded(self):                     # SEC-04: the relay's answer was read whole
        relay = mm.LocalRelay(18789).start()
        try:
            r = mm.NtfyRelay("http://127.0.0.1:18789", timeout=5)
            for n in range(3):
                r.publish("mmlimittest", str(n) * 2000)
            self.assertEqual(len(r.fetch("mmlimittest", "all")), 3)
            keep = mm.MAX_FETCH_BYTES
            mm.MAX_FETCH_BYTES = 3000
            try:
                # past the cap: the whole messages read so far, and `since` moves on (GPT's review of PR #16: the same
                # `since` failed again and again while the relay held more than one answer)
                since, got = "all", []
                for _ in range(5):
                    part = r.fetch("mmlimittest", since)
                    if not part:
                        break
                    self.assertLessEqual(len(part), 1)
                    got += [body[0] for _mid, body in part]
                    since = part[-1][0]
                self.assertEqual(got, ["0", "1", "2"])
                mm.MAX_FETCH_BYTES = 100                         # (not one whole message within the cap: refused)
                with self.assertRaises(ValueError):
                    r.fetch("mmlimittest", "all")
            finally:
                mm.MAX_FETCH_BYTES = keep
        finally:
            relay.shutdown()
            relay.server_close()


class CreationSize(unittest.TestCase):
    def test_creation_too_large_refused(self):              # SEC-03 B: the bridge checked only that it was a string
        import bridge
        pkg = {"session": "t", "turn": 1, "blocks": [{"type": "dialogue", "text": "x"}],
               "match": {"start": {"room": "roomtest01", "side": "guest", "opponent": {"name": "a", "creation": "A" * (bridge.MAX_CREATION_CHARS + 1)},
                                   "limits": {"arousal_start": 0.3, "format": "sex"}}}}
        self.assertTrue(any("creation passes" in e for e in bridge.validate_package(pkg)))
        pkg["match"]["start"]["opponent"]["creation"] = "A" * 1000
        self.assertFalse(any("creation" in e for e in bridge.validate_package(pkg)))


if __name__ == "__main__":
    unittest.main()
