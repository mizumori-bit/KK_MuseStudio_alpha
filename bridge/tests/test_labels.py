"""The labels (PR #46 §1, PR #47; bridge/labels.py): Muse writes what each coordinate card is for; the app's catalog is what
there is. Unknown ids refused, the rev guards a label changed meanwhile, the user's own label is kept, a changed card is
marked to look at again, a gone one kept aside and back on a card with the same contents (a rename)."""
from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import ROOT, Bridge  # noqa: E402


def catalog(coords: list) -> dict:
    return {"version": 1, "plays": [], "coordinates": [{"id": i, "name": i, "hash": h} for i, h in coords]}


class Labels(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.b = Bridge(18950)

    @classmethod
    def tearDownClass(cls):
        cls.b.close()

    def post_catalog(self, coords):
        self.assertEqual(self.b.call("POST", "/api/catalog", catalog(coords), role="app")[0], 200)

    def test_a_the_flow(self):
        b = self.b
        code, ans = b.call("POST", "/api/labels/coordinates", {"id": "a.png", "text": "x"}, role="muse")
        self.assertEqual((code, ans["reason"]), (409, "not_listed"), "before the app listed anything")
        self.post_catalog([("a.png", "h1"), ("sub/b.png", "h2")])
        code, view = b.call("GET", "/api/labels/coordinates", role="muse")
        self.assertEqual(code, 200)
        self.assertEqual(sorted(view["unlabeled"]), ["a.png", "sub/b.png"])
        code, ans = b.call("POST", "/api/labels/coordinates", {"id": "a.png", "text": "デート向けの私服", "tags": ["デート", "私服"]}, role="muse")
        self.assertEqual((code, ans["rev"]), (200, 1))
        code, ans = b.call("POST", "/api/labels/coordinates", {"id": "nope.png", "text": "x"}, role="muse")
        self.assertEqual((code, ans["reason"]), (404, "unknown_id"))
        code, ans = b.call("POST", "/api/labels/coordinates", {"id": "a.png", "text": "読まずに上書き"}, role="muse")
        self.assertEqual((code, ans["reason"], ans["rev"]), (409, "conflict", 1))
        code, ans = b.call("POST", "/api/labels/coordinates", {"id": "a.png", "text": "部屋着にも", "rev": 1}, role="muse")
        self.assertEqual((code, ans["rev"]), (200, 2))
        # the card's contents changed (the same name): to look at again
        self.post_catalog([("a.png", "h1x"), ("sub/b.png", "h2")])
        view = b.call("GET", "/api/labels/coordinates", role="muse")[1]
        self.assertEqual(view["labels"]["a.png"]["status"], "check")
        code, ans = b.call("POST", "/api/labels/coordinates", {"id": "a.png", "text": "見直した", "rev": 2}, role="muse")
        self.assertEqual(code, 200)
        self.assertEqual(b.call("GET", "/api/labels/coordinates", role="muse")[1]["labels"]["a.png"]["status"], "ok")
        # gone (renamed): kept aside, not on anything; back by itself on the card with its contents
        self.post_catalog([("sub/b.png", "h2")])
        view = b.call("GET", "/api/labels/coordinates", role="muse")[1]
        self.assertEqual(view["labels"]["a.png"]["status"], "kept")
        self.post_catalog([("renamed.png", "h1x"), ("sub/b.png", "h2")])
        view = b.call("GET", "/api/labels/coordinates", role="muse")[1]
        self.assertNotIn("a.png", view["labels"])
        self.assertEqual((view["labels"]["renamed.png"]["text"], view["labels"]["renamed.png"]["status"]), ("見直した", "ok"))
        # kept in the bridge's file, never anything but ids and words
        doc = json.loads((b.tmp / "labels.json").read_text(encoding="utf-8"))
        self.assertIn("renamed.png", doc["coordinates"])
        self.assertNotIn(str(b.tmp), json.dumps(doc, ensure_ascii=False))

    def test_b_bad_bodies_and_roles(self):
        b = self.b
        self.post_catalog([("a.png", "h1")])
        for body in ({"id": "a.png"}, {"id": "a.png", "text": ""}, {"id": "a.png", "text": "x" * 501}, {"id": "a.png", "text": "x", "tags": "t"},
                     {"id": "a.png", "text": "x", "tags": ["t"] * 11}, {"id": "a.png", "text": "x", "rev": "1"}, ["a.png"]):
            with self.subTest(body=body):
                self.assertEqual(b.call("POST", "/api/labels/coordinates", body, role="muse")[0], 400)
        self.assertEqual(b.call("POST", "/api/labels/coordinates", {"id": "a.png", "text": "x"}, role="app")[0], 403)
        self.assertEqual(b.call("GET", "/api/labels/coordinates", role=None)[0], 401)
        code, ans = b.call("POST", "/api/labels/rooms", {"id": "myroom", "text": "x"}, role="muse")
        self.assertEqual((code, ans["reason"]), (409, "not_listed"), "rooms are not listed by the app yet")

    def test_c_the_users_own_label_is_kept(self):
        d = Path(tempfile.mkdtemp(prefix="labels_user_"))
        f = d / "labels.json"
        f.write_text(json.dumps({"coordinates": {"u.png": {"text": "ユーザーの説明", "tags": [], "by": "user", "rev": 3, "hash": "hu", "status": "ok"}},
                                 "rooms": {}}, ensure_ascii=False), encoding="utf-8")
        b = Bridge(18952, extra={"labels_file": str(f)})
        try:
            self.assertEqual(b.call("POST", "/api/catalog", catalog([("u.png", "hu")]), role="app")[0], 200)
            code, ans = b.call("POST", "/api/labels/coordinates", {"id": "u.png", "text": "Muse の説明", "rev": 3}, role="muse")
            self.assertEqual((code, ans["reason"]), (409, "user_label"))
            self.assertEqual(json.loads(f.read_text(encoding="utf-8"))["coordinates"]["u.png"]["text"], "ユーザーの説明")
        finally:
            b.close()

    def test_d_muses_listener_passes_the_labels(self):
        sys.path.insert(0, str(ROOT / "bridge"))
        import muse_listener
        for m, path in (("GET", "/api/labels/coordinates"), ("POST", "/api/labels/coordinates"), ("GET", "/api/labels/rooms"), ("POST", "/api/labels/rooms")):
            with self.subTest(path=path, m=m):
                self.assertTrue(muse_listener.route_allowed(m, path))


if __name__ == "__main__":
    unittest.main()
