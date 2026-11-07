"""The labels (PR #46 §1, PR #47; bridge/labels.py): Muse writes what each coordinate card is for; the app's catalog is what
there is. Unknown ids refused, the rev guards a label changed meanwhile, the user's own label is kept, a changed card is
marked to look at again, a gone one kept aside and back on a card with the same contents (a rename)."""
from __future__ import annotations

import json
import os
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


def edit_outside(f: Path, doc) -> None:
    """an edit by hand while the bridge runs (its time moved on: an editor's save)"""
    f.write_text(doc if isinstance(doc, str) else json.dumps(doc, ensure_ascii=False), encoding="utf-8")
    st = f.stat()
    os.utime(f, ns=(st.st_atime_ns, st.st_mtime_ns + 2_000_000_000))


class EditedWhileRunning(unittest.TestCase):
    """GPT's post-review of #52 (point 1): an edit by hand to labels.json while the bridge runs is taken before anything is
    read or written - the user's own wins over Muse's stale rev, a save of another label or the catalog's reconcile keeps it;
    an edit half done is kept as it is (nothing written until it reads)"""

    def setUp(self):
        self.f = Path(tempfile.mkdtemp(prefix="labels_live_")) / "labels.json"
        self.b = Bridge(18954, extra={"labels_file": str(self.f)})
        self.assertEqual(self.b.call("POST", "/api/catalog", catalog([("a.png", "h1"), ("b.png", "h2")]), role="app")[0], 200)

    def tearDown(self):
        self.b.close()

    def doc(self) -> dict:
        return json.loads(self.f.read_text(encoding="utf-8"))

    def test_the_users_edit_while_running_wins_and_is_kept(self):
        b = self.b
        self.assertEqual(b.call("POST", "/api/labels/coordinates", {"id": "a.png", "text": "Muse の説明"}, role="muse")[0], 200)
        d = self.doc()
        d["coordinates"]["a.png"].update(text="ユーザーの説明", by="user", rev=2)
        edit_outside(self.f, d)
        code, ans = b.call("POST", "/api/labels/coordinates", {"id": "a.png", "text": "古い rev で上書き", "rev": 1}, role="muse")
        self.assertEqual((code, ans["reason"]), (409, "user_label"))
        self.assertEqual(self.doc()["coordinates"]["a.png"]["text"], "ユーザーの説明")
        self.assertEqual(b.call("GET", "/api/labels/coordinates", role="muse")[1]["labels"]["a.png"]["by"], "user")
        # another label saved: the user's edit stays in the file
        self.assertEqual(b.call("POST", "/api/labels/coordinates", {"id": "b.png", "text": "部屋着"}, role="muse")[0], 200)
        d = self.doc()
        self.assertEqual((d["coordinates"]["a.png"]["text"], d["coordinates"]["b.png"]["text"]), ("ユーザーの説明", "部屋着"))
        # edited again, then the catalog's reconcile saves the whole file: the edit stays
        d["coordinates"]["a.png"]["text"] = "ユーザーの説明（直した）"
        edit_outside(self.f, d)
        self.assertEqual(b.call("POST", "/api/catalog", catalog([("a.png", "h1x"), ("b.png", "h2")]), role="app")[0], 200)
        d = self.doc()
        self.assertEqual((d["coordinates"]["a.png"]["text"], d["coordinates"]["a.png"]["status"]), ("ユーザーの説明（直した）", "check"))

    def test_an_edit_half_done_is_kept_and_nothing_written(self):
        b = self.b
        self.assertEqual(b.call("POST", "/api/labels/coordinates", {"id": "a.png", "text": "x"}, role="muse")[0], 200)
        edit_outside(self.f, '{"coordinates": {"a.png": ')
        self.assertEqual(b.call("POST", "/api/labels/coordinates", {"id": "b.png", "text": "y"}, role="muse")[0], 503)
        self.assertEqual(b.call("GET", "/api/labels/coordinates", role="muse")[0], 503)
        self.assertEqual(b.call("POST", "/api/catalog", catalog([("b.png", "h2")]), role="app")[0], 200)
        self.assertEqual(self.f.read_text(encoding="utf-8"), '{"coordinates": {"a.png": ', "the half-done edit kept as it is")
        edit_outside(self.f, {"coordinates": {"a.png": {"text": "直した", "tags": [], "by": "user", "rev": 5, "hash": "h1", "status": "ok"}}, "rooms": {}})
        self.assertEqual(b.call("POST", "/api/labels/coordinates", {"id": "b.png", "text": "y"}, role="muse")[0], 200)
        self.assertEqual(self.doc()["coordinates"]["a.png"]["text"], "直した")

    def test_a_save_does_not_write_over_an_edit_since_it_read(self):
        sys.path.insert(0, str(ROOT / "bridge"))
        import labels
        f = Path(tempfile.mkdtemp(prefix="labels_race_")) / "labels.json"
        f.write_text(json.dumps({"coordinates": {"a.png": {"text": "t", "by": "muse", "rev": 1}}, "rooms": {}}), encoding="utf-8")
        s = labels.LabelStore(f)
        self.assertEqual(s.ready(), "")
        edit_outside(f, {"coordinates": {"a.png": {"text": "その瞬間の直し", "by": "user", "rev": 2}}, "rooms": {}})
        with self.assertRaises(labels.Changed):
            s._save()
        self.assertEqual(json.loads(f.read_text(encoding="utf-8"))["coordinates"]["a.png"]["text"], "その瞬間の直し")


if __name__ == "__main__":
    unittest.main()
