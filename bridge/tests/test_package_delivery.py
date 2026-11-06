"""The packages' delivery (PR #24, the audit's point: the events had a saved feed and acks, the packages none). A package is
saved before it is taken (not saved: 503, not taken), sent again it is one package (its id, else the same content), an
app that acks (?ack=1) gets it again until it says received, a bridge restart keeps the queue and the launch it is for,
a new launch drops what is left (before_launch) and what the old app had but never began (app_restarted), an untagged
package is for the character of when it came, and Muse can ask where a package is. No game, no outside network.

  python -B -m unittest discover -s bridge/tests
"""
from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import Bridge, package  # noqa: E402

APP = {"X-MMS-Instance": "win-1", "X-MMS-Started": "1000"}


class Delivery(unittest.TestCase):
    def setUp(self):
        self.b = Bridge(18873, test=True)
        self.turn = 0

    def tearDown(self):
        self.b.close()

    def pkg(self, text="x", **kw):
        self.turn += 1
        p = package(text, self.turn)
        p.update(kw)
        return p

    def post(self, p):
        return self.b.call("POST", "/api/packages", p)

    def nxt(self, ack=True):
        return self.b.call("GET", "/api/packages/next" + ("?ack=1" if ack else ""), role="app", headers=APP)

    def ack(self, ids, stage, reason=None):
        body = {"ids": ids, "stage": stage}
        if reason:
            body["reason"] = reason
        return self.b.call("POST", "/api/packages/ack", body, role="app", headers=APP)

    def status(self, pid):
        return self.b.call("GET", "/api/packages/status?id=" + pid, role="muse")

    def launch(self, lid, phase="character_selected", char="card:a.png"):
        return self.b.call("POST", "/api/launch", {"launch": lid, "phase": phase, "character": {"id": char, "name": "a"}}, role="app", headers=APP)

    def events(self, kind):
        code, doc = self.b.call("GET", "/api/events?limit=500", role="muse")
        return [e for e in (doc or {}).get("events", []) if e.get("type") == kind]

    def saved(self) -> dict:
        return json.loads((self.b.tmp / "packages.json").read_text(encoding="utf-8"))

    def test_saved_before_taken_and_an_id_back(self):
        code, r = self.post(self.pkg("はい。", id="m-1"))
        self.assertEqual((code, r["accepted"], r["id"]), (200, True, "m-1"))
        self.assertEqual([q["_pid"] for q in self.saved()["queue"]], ["m-1"])
        self.assertEqual(self.status("m-1")[1]["stage"], "queued")

    def test_sent_again_is_one_package(self):
        p = self.pkg("一度だけ。", id="m-2")
        self.post(p)
        code, r = self.post(p)
        self.assertEqual((code, r["duplicate"], r["stage"], r["queue"]), (200, True, "queued", 1))
        q = self.pkg("id なしの同じ中身。")
        code, r1 = self.post(q)
        code, r2 = self.post(q)
        self.assertTrue(r2.get("duplicate") and r2["id"] == r1["id"] and r1["id"].startswith("h:"), (r1, r2))
        self.assertEqual(self.b.call("GET", "/api/packages")[1]["queue"], 2)

    def test_given_again_until_received(self):
        self.post(self.pkg("届くまで。", id="m-3"))
        code, a = self.nxt()
        code2, b = self.nxt()                       # (the first answer lost on the way: the same package again)
        self.assertEqual((code, code2, a["_pid"], b["_pid"]), (200, 200, "m-3", "m-3"))
        st = self.status("m-3")[1]
        self.assertEqual((st["stage"], st["deliveries"]), ("delivered", 2))
        self.assertEqual(self.ack(["m-3"], "received")[0], 200)
        self.assertEqual(self.nxt()[0], 204)
        self.assertEqual(self.status("m-3")[1]["stage"], "received")
        self.assertNotIn("届くまで", json.dumps(self.saved(), ensure_ascii=False))          # (no text kept once received)
        self.assertEqual(self.ack(["m-3"], "started")[0], 200)
        code, r = self.post(self.pkg("届くまで。", id="m-3"))
        self.assertEqual((r["duplicate"], r["stage"]), (True, "started"))                    # (begun: sending it again does nothing)
        self.assertEqual(self.ack(["m-3"], "received")[0], 200)                             # (never back)
        self.assertEqual(self.status("m-3")[1]["stage"], "started")

    def test_a_bridge_restart_keeps_the_queue_and_the_launch(self):
        self.launch("L1")
        self.post(self.pkg("再起動のあとで。", id="m-4", launch="L1"))
        self.b.restart()
        code, h = self.b.call("GET", "/health")
        self.assertEqual((h["launch"]["id"], h["launch"]["phase"]), ("L1", "bridge_started"))   # (the phase: the app's next report)
        code, p = self.nxt()
        self.assertEqual((code, p["_pid"]), (200, "m-4"))
        self.assertEqual(self.events("package_dropped"), [])

    def test_a_new_launch_drops_what_is_left_and_what_the_old_app_never_began(self):
        self.launch("L1")
        self.post(self.pkg("受け取ったが出ていない。", id="m-5"))
        self.nxt()
        self.ack(["m-5"], "received")
        self.post(self.pkg("渡したが返事が無い。", id="m-6"))
        self.nxt()
        self.post(self.pkg("まだ渡していない。", id="m-7"))
        self.post(self.pkg("始まった。", id="m-8"))
        self.assertEqual(self.launch("L2", "app_starting")[0], 200)
        why = {e["turn"]: e["reason"] for e in self.events("package_dropped")}
        self.assertEqual(why, {1: "app_restarted", 2: "app_restarted", 3: "before_launch", 4: "before_launch"}, why)
        self.assertEqual([self.status(i)[1]["stage"] for i in ("m-5", "m-6", "m-7")], ["dropped"] * 3)
        self.assertEqual(self.status("m-7")[1]["reason"], "before_launch")
        self.assertEqual(self.nxt()[0], 204)

    def test_an_untagged_package_is_for_the_character_of_when_it_came(self):
        self.launch("L1", char="card:a.png")
        self.post(self.pkg("A さんへ。", id="m-9"))
        self.launch("L1", char="card:b.png")          # (the user switched; the same launch)
        self.post(self.pkg("B さんへ。", id="m-10"))
        code, p = self.nxt()
        self.assertEqual((code, p["_pid"]), (200, "m-10"))
        self.assertEqual(self.status("m-9")[1]["reason"], "other_character")
        self.assertEqual([e["reason"] for e in self.events("package_dropped")], ["other_character"])

    def test_not_saved_is_not_taken(self):
        self.b.call("POST", "/api/test/faults", {"fail_package_save": 1})
        code, r = self.post(self.pkg("保存できない。", id="m-11"))
        self.assertEqual((code, r["accepted"]), (503, False))
        self.assertEqual(self.b.call("GET", "/api/packages")[1]["queue"], 0)
        self.assertEqual(self.status("m-11")[0], 404)
        code, r = self.post(self.pkg("保存できない。", id="m-11"))      # (sent again: taken now)
        self.assertEqual((code, r["accepted"], r.get("duplicate")), (200, True, None))

    def test_a_caller_without_acks_takes_as_before(self):
        self.post(self.pkg("昔の取り方。", id="m-12"))
        code, p = self.nxt(ack=False)
        self.assertEqual((code, p["_pid"]), (200, "m-12"))
        self.assertEqual(self.nxt(ack=False)[0], 204)
        self.assertEqual(self.status("m-12")[1]["stage"], "received")

    def test_shapes_and_who_may(self):
        self.assertEqual(self.ack([], "received")[0], 400)
        self.assertEqual(self.ack(["x"], "shown")[0], 400)
        code, r = self.ack(["nope"], "received")
        self.assertEqual((code, r["unknown"]), (200, ["nope"]))
        self.assertEqual(self.status("nope")[0], 404)
        self.assertEqual(self.b.call("GET", "/api/packages/status?id=x", role="app")[0], 403)
        self.assertEqual(self.b.call("POST", "/api/packages/ack", {"ids": ["x"], "stage": "received"}, role="muse")[0], 403)
        self.assertEqual(self.b.call("GET", "/api/packages/status?id=x", role=None)[0], 401)
        self.assertEqual(self.post(self.pkg("x", id=""))[0], 400)
        self.assertEqual(self.post(self.pkg("x", id="y" * 201))[0], 400)


if __name__ == "__main__":
    unittest.main()
