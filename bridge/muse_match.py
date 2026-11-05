"""Muse-to-Muse match transport (DECISIONS.md "Muse 同士の通信対戦：基盤の設計"; from_muse/request-p2p-match.md).

Rooms with one-off keys made here - X25519 for the key exchange, Ed25519 for signing; the private keys never leave this
process and are never written down - an end-to-end encrypted store-and-forward relay (ntfy's HTTP API: publish to and
fetch from a topic nobody can guess), and the peer's messages into the bridge's event feed for the local Muse, who checks
them and hands what she accepts to the local app. The apps never reach each other; the bridge stays unreachable from the
peer's side (it only polls the relay).

  POST /api/match/rooms               {"role": "host"|"guest", "relay"?: {"url"}, "room"?: id (the guest: the host's),
                                       "peer"?: {"dh", "sign"} (the guest: the host's public keys)}
                                       -> {"room", "role", "keys": {"dh", "sign"}, "protocol", "relay", "state"}
  POST /api/match/rooms/<id>/peer     {"peer": {"dh", "sign"}} (the host, from the guest's reply) -> the room
  POST /api/match/rooms/<id>/ping     a test post: the peer gets match_peer_seen (the first time) and match_ping
  POST /api/match/rooms/<id>/send     {"package": {...}, "turn"?: n, "state"?: {...}} -> {"seq"}
  POST /api/match/rooms/<id>/close    {"reason"?} a bye to the peer; the room is closed (its keys dropped)
  GET  /api/match                     the rooms (no keys): role, state, seq, heard, silent, closed_by
  GET  /api/match/rooms/<id>          one of them
What crosses (2026-11-04): the package as the app presents it - a dialogue block's text without its （） spans - and, as
package.clothes_now, this side's character's clothes as they are (the app's state's clothes): the other side shows and
recovers her look from it.
Events: match_peer_seen, match_ping, match_package {room, from, seq, turn, state, package, missing}, match_peer_silent,
match_peer_back, match_closed {room, by: "peer"|"self", reason}, match_rejected {room, reason}, match_conditions {room,
agreed, mismatch: [keys], ours, theirs} (the two apps' applied conditions compared - each bridge sends its app's state's
match.limits to the other - told when it changes: the first comparison, a mismatch, agreed again).

The wire: per message (one relay post) {"v", "f": "h"|"g", "q": seq, "i": part, "n": parts, "d": data, "s": signature (part
0)}; the data, joined, is base64(nonce + ChaCha20-Poly1305(zlib(payload JSON))) with the associated data "room|f|q", and
the signature is Ed25519 over "room|f|q|data". Payload: {"type": "package"|"ping"|"heartbeat"|"conditions"|"bye", "sent",
...} (a heartbeat and "conditions" carry "conditions": the app's applied limits).
Optional: needs the 'cryptography' package; without it AVAILABLE is False (the bridge answers /api/match with 503).
Run as a script it is a small local relay of the same API (for tests, or a LAN): python muse_match.py --relay-port 18793
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import re
import secrets
import sys
import threading
import time
import urllib.parse
import urllib.request
import zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

try:
    from cryptography.exceptions import InvalidSignature, InvalidTag
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey
    from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
    from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
    from cryptography.hazmat.primitives.kdf.hkdf import HKDF
    AVAILABLE = True
except ImportError:            # the bridge runs without it; only the match is off
    AVAILABLE = False

PROTOCOL = 1
CHUNK = 2800                   # characters of data per relay post (the public ntfy takes 4 KB a message)
MAX_PARTS = 400                # (about 1 MB) - more is refused
# What a peer or a relay can make this bridge hold or work on is bounded before anything is checked or passed on (the
# security review of 2026-11-05, SEC-02..04): a signed message may still be hostile.
MAX_SEQ_JUMP = 256             # a message this far past the last one heard is refused (its gap not listed) - SEC-02
SEEN_WINDOW = 1024             # the seqs remembered; one older than the window is refused as a replay
MAX_PART_CHARS = 4096          # one post's data (CHUNK is 2800) - SEC-04
MAX_ASSEMBLING = 8             # messages held while their parts come in; past it the oldest is dropped
MAX_HELD_CHARS = MAX_PARTS * CHUNK      # all the parts a room holds before their signature is checked
MAX_PAYLOAD_BYTES = 2 * 1024 * 1024     # a message's payload unpacked (zlib), cut off while unpacking - SEC-03
MAX_FETCH_BYTES = 8 * 1024 * 1024       # one answer of the relay read - SEC-04 (past it: its whole lines, the rest next time)
SIG_CHARS = 86                          # an Ed25519 signature (64 bytes) in b64e
ROOM_ID = re.compile(r"^[A-Za-z0-9_-]{8,64}$")
DEFAULTS = {"relay_url": "https://ntfy.sh", "relay_allow": [], "poll_seconds": 2.0, "heartbeat_seconds": 30.0, "silent_seconds": 90.0,
            "part_expiry_seconds": 300.0}


def b64e(b: bytes) -> str:
    return base64.urlsafe_b64encode(b).decode("ascii").rstrip("=")


def b64d(s: str) -> bytes:
    return base64.urlsafe_b64decode(s + "=" * (-len(s) % 4))


def _sig_ok(s) -> bool:
    # an Ed25519 signature as b64e writes it: 86 characters of the URL-safe alphabet, 64 bytes
    if not isinstance(s, str) or len(s) != SIG_CHARS or not re.fullmatch(r"[-_A-Za-z0-9]+", s):
        return False
    try:
        return len(b64d(s)) == 64
    except ValueError:
        return False


def _held(m: dict) -> int:
    # a message's parts held before its signature is checked: its data and its signature
    return sum(len(x) for x in m["d"].values()) + len(m["s"] or "")


def _now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%S%z")


def _raw(pub) -> bytes:
    return pub.public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)


# ---- which relays this bridge talks to (the security review of 2026-11-05, SEC-05): its own default and those its own
# settings list (match.relay_allow) - never one an invitation names by itself; a URL read as a structure (no userinfo, query
# or fragment) and no redirect followed ----

class NotAllowed(Exception):
    pass


def relay_base(url) -> str:
    """the relay's normalised base URL (scheme://host[:port][/path]); ValueError for one that is not a plain http(s) base"""
    if not isinstance(url, str) or any(c.isspace() for c in url):
        raise ValueError("relay.url must be an http(s) URL")
    u = urllib.parse.urlsplit(url)
    if u.scheme not in ("http", "https") or not u.hostname:
        raise ValueError("relay.url must be an http(s) URL")
    if u.username is not None or u.password is not None or "?" in url or "#" in url or "@" in u.netloc:
        raise ValueError("relay.url may not carry a user, a query or a fragment")      # (an empty ? or # too)
    try:
        port = u.port
    except ValueError:
        raise ValueError("relay.url has a bad port") from None
    if port == 0:
        raise ValueError("relay.url has a bad port")
    host = u.hostname.lower()
    if ":" in host:
        host = f"[{host}]"
    return f"{u.scheme}://{host}{f':{port}' if port else ''}{u.path.rstrip('/')}"


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None                     # (a 3xx is an error: the relay's answer is not followed elsewhere)


_OPENER = urllib.request.build_opener(_NoRedirect)


# ---- the relay: publish to / fetch from a topic (ntfy's HTTP API) ----

class NtfyRelay:
    def __init__(self, url: str, timeout: float = 10.0):
        self.url = url.rstrip("/")
        self.timeout = timeout

    def publish(self, topic: str, body: str) -> None:
        req = urllib.request.Request(f"{self.url}/{topic}", data=body.encode("utf-8"), method="POST",
                                     headers={"Content-Type": "text/plain; charset=utf-8"})
        with _OPENER.open(req, timeout=self.timeout) as r:
            r.read(65536)

    def fetch(self, topic: str, since: str) -> list[tuple[str, str]]:
        # the messages after `since` (a message id, or "all"), oldest first: [(id, body)]
        url = f"{self.url}/{topic}/json?poll=1&since={urllib.parse.quote(since)}"
        with _OPENER.open(url, timeout=self.timeout) as r:
            raw = r.read(MAX_FETCH_BYTES + 1)
        if len(raw) > MAX_FETCH_BYTES:
            # (more waiting than one answer may hold - GPT's review of PR #16: the whole lines read so far, `since` moves to
            # their last and the rest comes next time; a relay message is a few KB, so this is never none)
            cut = raw.rfind(b"\n", 0, MAX_FETCH_BYTES)
            if cut < 0:
                raise ValueError(f"the relay's answer has no whole message in {MAX_FETCH_BYTES} bytes")
            raw = raw[:cut + 1]
        text = raw.decode("utf-8", "replace")
        out = []
        for line in text.splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                m = json.loads(line)
            except ValueError:
                continue
            if not isinstance(m, dict):
                continue                          # (an array or a bare value: not a message - the lines after it still read)
            mid = m.get("id")
            if isinstance(mid, bool) or not isinstance(mid, (str, int)) or str(mid) == "":
                continue
            if m.get("event", "message") == "message" and isinstance(m.get("message"), str):
                out.append((str(mid), m["message"]))
        return out


# ---- the package that crosses (from_muse/request-match-clothes-and-parse.md 2): as the app presents it - a dialogue block's
# text without its （） (her inner thoughts: the author's layer, the user's own in the chat's log), the structure as it is ----
INNER = re.compile(r"（[^（）]*）")


def presented(pkg: dict) -> dict:
    out = json.loads(json.dumps(pkg, ensure_ascii=False))
    for b in out.get("blocks") or []:
        if isinstance(b, dict) and b.get("type", "dialogue") == "dialogue" and isinstance(b.get("text"), str):
            b["text"] = INNER.sub("", b["text"]).strip(" \u3000\n\r\t")
    return out


# ---- a room ----

class Room:
    def __init__(self, rid: str, role: str, relay_url: str):
        self.id, self.role, self.relay_url = rid, role, relay_url
        self.relay = NtfyRelay(relay_url)
        self.dh = X25519PrivateKey.generate()
        self.sign = Ed25519PrivateKey.generate()
        self.peer_sign = None
        self.k_send = self.k_recv = None
        self.t_send = self.t_recv = ""
        self.seq = 0
        self.seen: set[int] = set()
        self.max_seen = 0
        self.parts: dict[int, dict] = {}
        self.since = "all"
        self.heard = 0.0               # time of the peer's last message (0: never)
        self.sent_at = 0.0
        self.silent = False
        self.state = "waiting_peer"    # waiting_peer -> ready (keys) -> connected (heard the peer) -> closed
        self.closed_by = None
        self.rejected = 0
        self.cond_sent = None          # our app's conditions as last sent / the peer's as last heard / the comparison
        self.peer_cond = None
        self.cond_report = None
        self.cond_view = None
        self.created = time.time()
        self.lock = threading.Lock()

    @property
    def letter(self) -> str:
        return "h" if self.role == "host" else "g"

    def keys(self) -> dict:
        return {"dh": b64e(_raw(self.dh.public_key())), "sign": b64e(_raw(self.sign.public_key()))}

    def set_peer(self, peer: dict) -> None:
        pdh = X25519PublicKey.from_public_bytes(b64d(peer["dh"]))
        psig = Ed25519PublicKey.from_public_bytes(b64d(peer["sign"]))
        mine, theirs = _raw(self.dh.public_key()), _raw(pdh)
        host_pub, guest_pub = (mine, theirs) if self.role == "host" else (theirs, mine)
        okm = HKDF(algorithm=hashes.SHA256(), length=128, salt=self.id.encode("ascii"),
                   info=b"metamuse-match/1" + host_pub + guest_pub).derive(self.dh.exchange(pdh))
        k_hg, k_gh, t_hg, t_gh = okm[:32], okm[32:64], okm[64:96], okm[96:]

        def topic(t: bytes) -> str:
            return "mm" + base64.b32encode(t[:20]).decode("ascii").lower().rstrip("=")
        if self.role == "host":
            self.k_send, self.k_recv, self.t_send, self.t_recv = k_hg, k_gh, topic(t_hg), topic(t_gh)
        else:
            self.k_send, self.k_recv, self.t_send, self.t_recv = k_gh, k_hg, topic(t_gh), topic(t_hg)
        self.peer_sign = psig
        if self.state == "waiting_peer":
            self.state = "ready"

    def seal(self, payload: dict) -> tuple[int, list[str]]:
        self.seq += 1
        q, f = self.seq, self.letter
        plain = zlib.compress(json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8"), 9)
        nonce = os.urandom(12)
        ad = f"{self.id}|{f}|{q}".encode("ascii")
        data = b64e(nonce + ChaCha20Poly1305(self.k_send).encrypt(nonce, plain, ad))
        sig = b64e(self.sign.sign(ad + b"|" + data.encode("ascii")))
        chunks = [data[i:i + CHUNK] for i in range(0, len(data), CHUNK)] or [""]
        posts = []
        for i, c in enumerate(chunks):
            env = {"v": PROTOCOL, "f": f, "q": q, "i": i, "n": len(chunks), "d": c}
            if i == 0:
                env["s"] = sig
            posts.append(json.dumps(env, separators=(",", ":")))
        return q, posts

    def open(self, body: str, expiry: float) -> tuple[str, dict | None, int, list[int]]:
        # one relay post -> ("", payload, seq, missing) when a message is complete; ("", None, ..) while parts are still
        # missing; (reason, None, ..) when refused (reason: protocol / signature / decrypt / replay)
        try:
            env = json.loads(body)
            v, f, q, i, n, d = env["v"], env["f"], int(env["q"]), int(env["i"]), int(env["n"]), env["d"]
        except (ValueError, KeyError, TypeError):
            return "protocol", None, 0, []
        if (v != PROTOCOL or f != ("g" if self.role == "host" else "h") or not 0 <= i < n <= MAX_PARTS or not isinstance(d, str)
                or len(d) > MAX_PART_CHARS or q < 1):
            return "protocol", None, q, []
        if q in self.seen or q <= self.max_seen - SEEN_WINDOW:
            return "replay", None, q, []
        if q > self.max_seen + MAX_SEQ_JUMP:
            return "protocol", None, q, []
        # the first part's signature: a signature's string or none, before it is held (GPT's review of PR #16)
        s = env.get("s") if i == 0 else None
        if s is not None and not _sig_ok(s):
            return "protocol", None, q, []
        self.prune(expiry)
        if q not in self.parts and len(self.parts) >= MAX_ASSEMBLING:
            del self.parts[min(self.parts, key=lambda k: self.parts[k]["at"])]
        p = self.parts.setdefault(q, {"n": n, "d": {}, "s": None, "at": time.time()})
        if p["n"] != n:
            return "protocol", None, q, []
        # (all that is held, before any signature - the signatures too: the oldest messages go first; one alone too big is
        # refused)
        held = (sum(_held(m) for m in self.parts.values()) + len(d) - len(p["d"].get(i, ""))
                + (len(s) - len(p["s"] or "") if s is not None else 0))
        while held > MAX_HELD_CHARS and len(self.parts) > 1:
            old = min((k for k in self.parts if k != q), key=lambda k: self.parts[k]["at"])
            held -= _held(self.parts[old])
            del self.parts[old]
        if held > MAX_HELD_CHARS:
            del self.parts[q]
            return "protocol", None, q, []
        p["d"][i] = d
        if s is not None:
            p["s"] = s
        if len(p["d"]) < n or not p["s"]:
            return "", None, q, []
        del self.parts[q]
        data = "".join(p["d"][k] for k in range(n))
        ad = f"{self.id}|{f}|{q}".encode("ascii")
        try:
            self.peer_sign.verify(b64d(p["s"]), ad + b"|" + data.encode("ascii"))
        except (InvalidSignature, ValueError):
            return "signature", None, q, []
        try:
            raw = b64d(data)
            plain = ChaCha20Poly1305(self.k_recv).decrypt(raw[:12], raw[12:], ad)
        except (InvalidTag, ValueError):
            return "decrypt", None, q, []
        try:
            z = zlib.decompressobj()
            text = z.decompress(plain, MAX_PAYLOAD_BYTES)
            if z.unconsumed_tail or not z.eof:
                return "protocol", None, q, []          # (unpacks past MAX_PAYLOAD_BYTES: stopped there)
            payload = json.loads(text.decode("utf-8"))
        except (ValueError, zlib.error, RecursionError):
            return "decrypt", None, q, []
        if not isinstance(payload, dict):
            return "protocol", None, q, []
        missing = [k for k in range(self.max_seen + 1, q) if k not in self.seen]       # (at most MAX_SEQ_JUMP)
        self.seen.add(q)
        self.max_seen = max(self.max_seen, q)
        if len(self.seen) > 2 * SEEN_WINDOW:
            self.seen = {k for k in self.seen if k > self.max_seen - SEEN_WINDOW}
        return "", payload, q, missing

    def prune(self, expiry: float) -> None:
        # parts of messages never completed, past their time (called on each post and each poll: also when nothing comes)
        now = time.time()
        for k in [k for k, p in self.parts.items() if now - p["at"] > expiry]:
            del self.parts[k]

    def view(self) -> dict:
        return {"room": self.id, "role": self.role, "state": self.state, "relay": self.relay_url, "seq": self.seq,
                "heard": round(time.time() - self.heard, 1) if self.heard else None, "silent": self.silent,
                "closed_by": self.closed_by, "rejected": self.rejected, "protocol": PROTOCOL, "conditions": self.cond_view}


# ---- the rooms of this bridge ----

class Match:
    def __init__(self, add_event, cfg: dict | None = None, conditions=None, clothes=None):
        self.add_event = add_event
        self.clothes = clothes or (lambda: None)      # () -> this side's character's clothes now (the app's state's clothes)
        self.conditions = conditions or (lambda rid: None)      # room id -> this side's app's applied conditions (or None)
        self.cfg = dict(DEFAULTS)
        self.cfg.update(cfg or {})
        self.rooms: dict[str, Room] = {}
        self.lock = threading.Lock()
        self.stop = threading.Event()
        threading.Thread(target=self._loop, name="match-relay", daemon=True).start()

    def allowed_relays(self) -> set:
        out = set()
        for u in [self.cfg["relay_url"]] + list(self.cfg.get("relay_allow") or []):
            try:
                out.add(relay_base(u))
            except ValueError:
                sys.stderr.write(f"[match] a relay in the settings is not a plain http(s) base URL: {u!r} (left out)\n")
        return out

    def _room(self, rid: str) -> Room:
        r = self.rooms.get(rid)
        if r is None:
            raise KeyError(rid)
        return r

    @staticmethod
    def _peer(peer) -> dict:
        if not isinstance(peer, dict) or not isinstance(peer.get("dh"), str) or not isinstance(peer.get("sign"), str):
            raise ValueError("peer must be {dh, sign} (the other side's public keys)")
        if len(b64d(peer["dh"])) != 32 or len(b64d(peer["sign"])) != 32:
            raise ValueError("peer keys must be 32 bytes each (base64url)")
        return peer

    def create(self, body: dict) -> dict:
        role = body.get("role")
        if role not in ("host", "guest"):
            raise ValueError("role must be 'host' or 'guest'")
        relay = body.get("relay") or {}
        url = relay.get("url", self.cfg["relay_url"]) if isinstance(relay, dict) else None
        url = relay_base(url)
        if url not in self.allowed_relays():
            raise NotAllowed(f"the relay {url} is not one this bridge talks to (its own settings: match.relay_url / match.relay_allow)")
        rid = body.get("room")
        if role == "host":
            rid = rid if isinstance(rid, str) and ROOM_ID.match(rid) else secrets.token_urlsafe(18)
        else:
            if not isinstance(rid, str) or not ROOM_ID.match(rid):
                raise ValueError("room must be the host's room id")
            peer = self._peer(body.get("peer"))
        with self.lock:
            old = self.rooms.get(rid)
            if old is not None and old.state != "closed":
                raise ValueError("room exists here already")
            r = Room(rid, role, url)
            if role == "guest":
                r.set_peer(peer)
            self.rooms[rid] = r
        out = r.view()
        out["keys"] = r.keys()
        return out

    def set_peer(self, rid: str, body: dict) -> dict:
        r = self._room(rid)
        peer = self._peer(body.get("peer"))
        with r.lock:
            if r.role != "host" or r.state == "closed":
                raise ValueError("only an open host room takes the guest's keys")
            r.set_peer(peer)
        return r.view()

    def _post(self, r: Room, payload: dict) -> int:
        if r.k_send is None or r.state == "closed":
            raise ValueError("the room has no peer yet" if r.k_send is None else "the room is closed")
        payload = dict(payload)
        payload.setdefault("sent", _now())
        with r.lock:
            q, posts = r.seal(payload)
        for body in posts:
            r.relay.publish(r.t_send, body)
        r.sent_at = time.time()
        return q

    def ping(self, rid: str) -> dict:
        return {"seq": self._post(self._room(rid), {"type": "ping"})}

    def send(self, rid: str, body: dict) -> dict:
        if not isinstance(body.get("package"), dict):
            raise ValueError("package must be an object")
        turn = body.get("turn")
        if turn is not None and (isinstance(turn, bool) or not isinstance(turn, int)):
            raise ValueError("turn must be an integer or null")
        if body.get("state") is not None and not isinstance(body["state"], dict):
            raise ValueError("state must be an object or null")
        pkg = presented(body["package"])
        try:
            c = self.clothes()
        except Exception:        # (the bridge's state unreadable for now)
            c = None
        if isinstance(c, dict):
            pkg["clothes_now"] = c
        return {"seq": self._post(self._room(rid), {"type": "package", "turn": turn, "state": body.get("state"), "package": pkg})}

    def close(self, rid: str, reason: str = "") -> dict:
        r = self._room(rid)
        if r.state != "closed" and r.k_send is not None:
            try:
                self._post(r, {"type": "bye", "reason": reason})
            except Exception as e:        # the relay down: closed here anyway
                sys.stderr.write(f"[match] bye not sent: {e}\n")
        self._closed(r, "self", reason)
        return r.view()

    def _closed(self, r: Room, by: str, reason: str) -> None:
        with r.lock:
            if r.state == "closed":
                return
            r.state, r.closed_by = "closed", by
            r.k_send = r.k_recv = None         # (the keys go with the room)
        self.add_event({"type": "match_closed", "room": r.id, "by": by, "reason": reason})

    def view(self, rid: str | None = None):
        if rid is not None:
            return self._room(rid).view()
        return {"available": True, "rooms": [r.view() for r in self.rooms.values()]}

    # ---- the relay, polled ----
    def _loop(self) -> None:
        while not self.stop.wait(float(self.cfg["poll_seconds"])):
            for r in list(self.rooms.values()):
                if r.state in ("waiting_peer", "closed") or r.k_recv is None:
                    continue
                try:
                    self._poll(r)
                except Exception as e:          # the relay unreachable for now: tried again
                    sys.stderr.write(f"[match] {r.id}: {e}\n")

    def _poll(self, r: Room) -> None:
        now = time.time()
        with r.lock:
            r.prune(float(self.cfg["part_expiry_seconds"]))
        for mid, body in r.relay.fetch(r.t_recv, r.since):
            r.since = mid
            with r.lock:
                if r.k_recv is None:
                    return
                reason, payload, q, missing = r.open(body, float(self.cfg["part_expiry_seconds"]))
            if reason:
                if reason != "replay":
                    r.rejected += 1
                    self.add_event({"type": "match_rejected", "room": r.id, "reason": reason, "seq": q})
                continue
            if payload is None:
                continue
            self._heard(r)
            self._deliver(r, payload, q, missing)
            if r.state == "closed":
                return
        if r.state == "connected" and r.heard and not r.silent and now - r.heard > float(self.cfg["silent_seconds"]):
            r.silent = True
            self.add_event({"type": "match_peer_silent", "room": r.id, "seconds": round(now - r.heard)})
        ours = self._ours(r)
        if r.state in ("ready", "connected") and ours is not None and ours != r.cond_sent:
            try:
                self._post(r, {"type": "conditions", "conditions": ours})
                r.cond_sent = ours
            except Exception as e:
                sys.stderr.write(f"[match] conditions: {e}\n")
        elif r.state in ("ready", "connected") and now - r.sent_at > float(self.cfg["heartbeat_seconds"]):
            try:
                self._post(r, {"type": "heartbeat", "conditions": ours} if ours is not None else {"type": "heartbeat"})
            except Exception as e:
                sys.stderr.write(f"[match] heartbeat: {e}\n")
        self._compare(r)

    # ---- the two apps' applied conditions (the format, the arousal to start from, the limits) compared ----
    def _ours(self, r: Room) -> dict | None:
        try:
            c = self.conditions(r.id)
        except Exception:        # (the bridge's state unreadable for now)
            return None
        return c if isinstance(c, dict) else None

    @staticmethod
    def _same(a, b) -> bool:
        if isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool) and not isinstance(b, bool):
            return abs(float(a) - float(b)) < 1e-6
        return a == b

    def _compare(self, r: Room) -> None:
        ours, theirs = self._ours(r), r.peer_cond
        if ours is None or not isinstance(theirs, dict):
            return
        diff = sorted(k for k in set(ours) | set(theirs) if not self._same(ours.get(k), theirs.get(k)))
        report = (tuple(diff), json.dumps(ours, sort_keys=True), json.dumps(theirs, sort_keys=True))
        if report == r.cond_report:
            return
        r.cond_report = report
        r.cond_view = {"agreed": not diff, "mismatch": diff, "ours": ours, "theirs": theirs}
        self.add_event({"type": "match_conditions", "room": r.id, "agreed": not diff, "mismatch": diff, "ours": ours, "theirs": theirs})

    def _heard(self, r: Room) -> None:
        first = r.state == "ready"
        r.heard = time.time()
        if first:
            r.state = "connected"
            self.add_event({"type": "match_peer_seen", "room": r.id, "peer": "guest" if r.role == "host" else "host"})
        if r.silent:
            r.silent = False
            self.add_event({"type": "match_peer_back", "room": r.id})

    def _deliver(self, r: Room, payload: dict, q: int, missing: list[int]) -> None:
        peer = "guest" if r.role == "host" else "host"
        kind = payload.get("type")
        if kind == "ping":
            self.add_event({"type": "match_ping", "room": r.id, "from": peer, "seq": q})
        elif kind == "package":
            self.add_event({"type": "match_package", "room": r.id, "from": peer, "seq": q, "turn": payload.get("turn"),
                            "state": payload.get("state"), "package": payload.get("package"), "missing": missing,
                            "sent": payload.get("sent")})
        elif kind == "bye":
            self._closed(r, "peer", str(payload.get("reason") or ""))
        if kind in ("heartbeat", "conditions") and isinstance(payload.get("conditions"), dict):
            r.peer_cond = payload["conditions"]
            self._compare(r)


# ---- a small local relay of the same API (tests; a LAN) ----

class LocalRelay(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, port: int, host: str = "127.0.0.1"):
        self.topics: dict[str, list[tuple[str, str]]] = {}
        self.next_id = 1
        self.store_lock = threading.Lock()
        self.posts = 0
        super().__init__((host, port), _RelayHandler)

    def start(self) -> "LocalRelay":
        threading.Thread(target=self.serve_forever, name="local-relay", daemon=True).start()
        return self


class _RelayHandler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def _topic(self) -> str:
        parts = urllib.parse.urlsplit(self.path).path.strip("/").split("/")
        return parts[0] if parts and re.match(r"^[-_A-Za-z0-9]{1,64}$", parts[0]) else ""

    def do_POST(self):
        t = self._topic()
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n).decode("utf-8", "replace") if 0 < n <= 4096 else None
        if not t or body is None:
            self.send_response(400)
            self.end_headers()
            return
        srv = self.server
        with srv.store_lock:
            mid = f"m{srv.next_id}"
            srv.next_id += 1
            srv.topics.setdefault(t, []).append((mid, body))
            srv.posts += 1
        out = json.dumps({"id": mid, "topic": t, "event": "message"}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(out)))
        self.end_headers()
        self.wfile.write(out)

    def do_GET(self):
        t = self._topic()
        q = urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query)
        since = (q.get("since") or ["all"])[0]
        with self.server.store_lock:
            msgs = list(self.server.topics.get(t, []))
        if since != "all":
            ids = [m[0] for m in msgs]
            msgs = msgs[ids.index(since) + 1:] if since in ids else msgs
        out = "".join(json.dumps({"id": i, "event": "message", "topic": t, "message": b}) + "\n" for i, b in msgs).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/x-ndjson")
        self.send_header("Content-Length", str(len(out)))
        self.end_headers()
        self.wfile.write(out)


def main() -> int:
    ap = argparse.ArgumentParser(description="a local relay of ntfy's API for MetaMuse matches (tests, a LAN)")
    ap.add_argument("--relay-port", type=int, default=18793)
    ap.add_argument("--host", default="127.0.0.1")
    a = ap.parse_args()
    srv = LocalRelay(a.relay_port, a.host)
    print(f"local relay on http://{a.host}:{a.relay_port}")
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
