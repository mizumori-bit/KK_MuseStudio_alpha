"""The labels (PR #46 §1, handed over in PR #47; MUSE_GUIDE 7.2): what each coordinate card or room is for, written by Muse
(and the user), kept apart from what there is. What there is - the "引当" side - is the app's catalog (its coordinates and,
later, rooms: id and the contents' hash); a label only describes, it never decides what can be worn or used.

Kept in this PC's own file (bridge/cache/labels.json: the cards and the mod rooms are this PC's, never the repository).
Each label: {"text", "tags", "by": "muse" / "user", "rev", "hash", "status", "updated"}.
  status "ok"     - on what there is now
         "check"  - its card's contents changed since it was written (the same name, another card): kept, to look at again
         "kept"   - what it was on is not there any more (removed, renamed): kept, not used; it comes back by itself when a
                    card with the same contents appears without a label of its own (a rename)
A label the user wrote ("by": "user") is not written over by Muse (the user's own wins). Writes name the rev they read
(a label changed meanwhile: 409 conflict). Standard library only.
"""
from __future__ import annotations

import json
import os
import shutil
import sys
import time
from pathlib import Path

KINDS = ("coordinates", "rooms")
MAX_LABELS = 2000            # per kind
MAX_TEXT = 500               # characters
MAX_TAGS, MAX_TAG = 10, 40
UNREAD = "the labels' file could not be read (another program may hold it): no label is written until it can be"
GARBLED = "the labels' file is not one and could not be copied aside: no label is written until a person moves it away"


def _now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%S")


class LabelStore:
    def __init__(self, path: Path):
        self.path = Path(path)
        self.doc: dict = {k: {} for k in KINDS}
        self.frozen = ""            # (why nothing is written now; "" - it may be)
        self.unread = False
        self._load()

    # ---- the file ----
    def _load(self) -> None:
        try:
            text = self.path.read_text(encoding="utf-8")
        except FileNotFoundError:
            return
        except ValueError as e:
            self._garbled(e)
            return
        except OSError as e:
            self.unread, self.frozen = True, UNREAD
            sys.stderr.write(f"[bridge] the labels' file {self.path} could not be read ({e}): kept as it is, no label written until it can be\n")
            return
        try:
            doc = json.loads(text)
            self.doc = {k: {i: r for i, r in (doc.get(k) or {}).items() if isinstance(i, str) and isinstance(r, dict)} for k in KINDS}
        except (ValueError, AttributeError, TypeError) as e:
            self._garbled(e)

    def _garbled(self, err) -> None:
        aside = self.path.with_name(self.path.name + ".unreadable-" + time.strftime("%Y%m%d-%H%M%S"))
        try:
            shutil.copyfile(self.path, aside)
        except OSError as e:
            self.frozen = GARBLED
            sys.stderr.write(f"[bridge] the labels' file {self.path} is not one ({err}) and could not be copied aside ({e}): kept, no label written\n")
            return
        sys.stderr.write(f"[bridge] the labels' file was not one ({err}): copied aside as {aside.name}, the labels start empty\n")

    def ready(self) -> str:
        """"" when the labels may be written; else why not (a file not readable at the start is read again here)"""
        if self.unread:
            try:
                text = self.path.read_text(encoding="utf-8")
                doc = json.loads(text)
                self.doc = {k: {i: r for i, r in (doc.get(k) or {}).items() if isinstance(i, str) and isinstance(r, dict)} for k in KINDS}
                self.unread, self.frozen = False, ""
            except FileNotFoundError:
                self.unread, self.frozen = False, ""
            except OSError:
                return self.frozen
            except (ValueError, AttributeError, TypeError) as e:
                self.unread, self.frozen = False, ""
                self._garbled(e)
        return self.frozen

    def _save(self) -> None:
        if self.frozen:
            raise OSError(self.frozen)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(".part")
        tmp.write_text(json.dumps(self.doc, ensure_ascii=False, indent=1), encoding="utf-8")
        os.replace(tmp, self.path)

    # ---- what there is (the app's catalog) ----
    @staticmethod
    def items(catalog: dict | None, kind: str) -> dict:
        """id -> hash of what the catalog lists for this kind"""
        out = {}
        for it in (catalog or {}).get(kind) or []:
            if isinstance(it, dict) and isinstance(it.get("id"), str):
                out[it["id"]] = it.get("hash") if isinstance(it.get("hash"), str) else None
        return out

    def reconcile(self, catalog: dict | None) -> bool:
        """the labels set against what there is now: changed contents -> check, gone -> kept, a kept one back on a card with
        its contents and no label of its own (a rename) -> ok there. True when something changed (saved then)"""
        if self.ready():
            return False
        changed = False
        for kind in KINDS:
            have = self.items(catalog, kind)
            if not have and kind not in (catalog or {}):
                continue                        # (the app does not list this kind yet: left as it is)
            labels = self.doc[kind]
            for lid, rec in labels.items():
                if lid in have:
                    if rec.get("status") == "kept":
                        rec["status"], changed = "ok", True
                    if rec.get("hash") and have[lid] and have[lid] != rec["hash"] and rec.get("status") != "check":
                        rec["status"], changed = "check", True
                elif rec.get("status") != "kept":
                    rec["status"], changed = "kept", True
            # a rename: a kept label whose contents are back under another id with no label
            by_hash = {}
            for lid, rec in labels.items():
                if rec.get("status") == "kept" and rec.get("hash"):
                    by_hash.setdefault(rec["hash"], lid)
            for iid, h in have.items():
                if iid not in labels and h and h in by_hash:
                    old = by_hash.pop(h)
                    rec = labels.pop(old)
                    rec["status"], rec["updated"] = "ok", _now()
                    labels[iid] = rec
                    changed = True
                    sys.stderr.write(f"[bridge] label of {kind} {old} back on {iid} (the same contents)\n")
        if changed:
            try:
                self._save()
            except OSError as e:
                sys.stderr.write(f"[bridge] the labels not saved: {e}\n")
        return changed

    # ---- the API ----
    def view(self, kind: str, catalog: dict | None) -> dict:
        have = self.items(catalog, kind)
        labels = {lid: {k: rec.get(k) for k in ("text", "tags", "by", "rev", "status", "updated")} for lid, rec in self.doc[kind].items()}
        unlabeled = [i for i in have if i not in self.doc[kind]]
        return {"labels": labels, "unlabeled": unlabeled, "listed": catalog is not None and kind in catalog}

    def write(self, kind: str, body, catalog: dict | None, by: str = "muse") -> tuple[int, dict]:
        why = self.ready()
        if why:
            return 503, {"ok": False, "error": why}
        if not isinstance(body, dict) or not isinstance(body.get("id"), str):
            return 400, {"ok": False, "error": "a label takes {\"id\", \"text\", \"tags\"?, \"rev\"?}"}
        text, tags, rev = body.get("text"), body.get("tags", []), body.get("rev")
        if not isinstance(text, str) or not 1 <= len(text) <= MAX_TEXT:
            return 400, {"ok": False, "error": f"text must be a string of 1-{MAX_TEXT} characters"}
        if not isinstance(tags, list) or len(tags) > MAX_TAGS or not all(isinstance(t, str) and 1 <= len(t) <= MAX_TAG for t in tags):
            return 400, {"ok": False, "error": f"tags must be a list of at most {MAX_TAGS} strings of 1-{MAX_TAG} characters"}
        if rev is not None and (isinstance(rev, bool) or not isinstance(rev, int)):
            return 400, {"ok": False, "error": "rev must be an integer or left out"}
        if catalog is None or kind not in catalog:
            return 409, {"ok": False, "reason": "not_listed", "error": f"the app has not listed its {kind} yet"}
        have = self.items(catalog, kind)
        lid = body["id"]
        if lid not in have:
            return 404, {"ok": False, "reason": "unknown_id", "error": f"no such {kind[:-1]} in the app's list"}
        labels = self.doc[kind]
        cur = labels.get(lid)
        if cur is not None and cur.get("by") == "user" and by != "user":
            return 409, {"ok": False, "reason": "user_label", "error": "the user wrote this label: theirs is kept", "rev": cur.get("rev")}
        if cur is not None and rev != cur.get("rev"):
            return 409, {"ok": False, "reason": "conflict", "error": "the label changed since it was read: read it again", "rev": cur.get("rev")}
        if cur is None and len(labels) >= MAX_LABELS:
            return 409, {"ok": False, "reason": "full", "error": f"at most {MAX_LABELS} labels"}
        rec = {"text": text, "tags": list(tags), "by": by, "rev": (cur.get("rev", 0) if cur else 0) + 1, "hash": have[lid],
               "status": "ok", "updated": _now()}
        labels[lid] = rec
        try:
            self._save()
        except OSError as e:
            if cur is None:
                del labels[lid]
            else:
                labels[lid] = cur
            sys.stderr.write(f"[bridge] the label not saved: {e}\n")
            return 503, {"ok": False, "error": self.frozen or "the label could not be saved (the bridge's file could not be written)"}
        return 200, {"ok": True, "id": lid, "rev": rec["rev"]}
