"""Muse's own listener (PR #31 and GPT's design review): Muse's requests told apart by the machine they come from.

Muse's vault puts a stand-in (a placeholder) in its requests' Authorization and swaps in the real key only on its usual way
out - not on the tailnet's way to this PC, so the bridge's token check locked Muse out. This listener takes Muse's requests
on a port of its own on this PC's tailnet address (never loopback: a program on this PC cannot reach it as 127.0.0.1) and:

  1. looks up the real TCP sender's address with Tailscale (`tailscale whois`): the machine must be the one
     bridge_config.json "muse_listener.machine" names - its StableID, which the owner checked in Tailscale's admin console.
     No header is trusted for this (X-Forwarded-For and the like are not read). Unknown, failed or stale -> refused.
  2. (an aid, not the proof) Muse's usual Authorization: "placeholder" "learn" (the default) keeps the SHA-256 of the first
     one the pinned machine sends (cache/muse_listener.json, this PC only) and asks for the same from then on - learnt only
     when that file does not exist (the first time, or after the owner's bridge.py --forget-muse-placeholder); a file that
     cannot be read or holds no digest refuses every request, it is never learnt over. A 64-hex value is that digest given;
     "off" does not look; anything else refuses every request. The value itself is never written or logged.
  3. passes only Muse's own API, listed here by method and path (not the bridge's roles: they also serve shared ways), and
     no browser's request (Origin / Sec-Fetch-Site) and no Host but the bridge's names.

Then the request goes to this PC's bridge (127.0.0.1, its own port) with the real Muse token put in here - the token never
leaves this PC - and the bridge's own checks (role, rate, sizes) apply as for any request.

It opens only with the bridge's auth required, and only on an IPv4 address Tailscale says is this PC's (in its range;
never every interface; loopback for the tests only). One request a connection: a connection that has not sent its request
(headers and body) within read_seconds is cut, at most max_connections are served at once (the rest closed at once), and
at most two `tailscale whois` run at a time. Standard library only.
"""
from __future__ import annotations

import hashlib
import hmac
import http.client
import ipaddress
import json
import os
import re
import socket
import subprocess
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOM = r"[A-Za-z0-9_-]{8,64}"
ROUTES = [            # (method, path pattern): Muse's API (MUSE_GUIDE 7), nothing of the app's, no test way
    ("GET", r"/health"),
    ("GET", r"/api/events"),
    ("POST", r"/api/events/ack"),
    ("POST", r"/api/packages"),
    ("GET", r"/api/packages/status"),
    ("GET", r"/api/state"),
    ("GET", r"/api/catalog"),
    ("GET", r"/api/characters"),
    ("GET", r"/api/voices"),
    ("GET", r"/api/choices"),
    ("GET", r"/api/labels/coordinates"),          # (PR #46 §1: the labels, Muse's to write)
    ("POST", r"/api/labels/coordinates"),
    ("GET", r"/api/labels/rooms"),
    ("POST", r"/api/labels/rooms"),
    ("GET", r"/api/match"),
    ("POST", r"/api/match/rooms"),
    ("GET", rf"/api/match/rooms/{ROOM}"),
    ("POST", rf"/api/match/rooms/{ROOM}/(?:peer|ping|send|close)"),
]
_ROUTES = [(m, re.compile(p + r"\Z")) for m, p in ROUTES]
DEFAULT_TAILSCALE = [r"C:\Program Files\Tailscale\tailscale.exe"] if os.name == "nt" else ["tailscale"]
PASS_BACK = ("Content-Type", "Retry-After", "WWW-Authenticate", "Allow", "Cache-Control")
FAILED_WHOIS_SECONDS = 5.0      # a failed lookup is not asked again for this long (a flood does not start a process each)
WHOIS_AT_ONCE = 2               # `tailscale whois` processes at a time; a lookup that waits longer than 5 s is refused
READ_SECONDS = 20.0             # a connection's request (headers and body) must have come within this; else it is cut
MAX_CONNECTIONS = 16            # connections served at once; past it a new one is closed at once
SLOW_SECONDS = 5.0              # the bridge's answer slower than this is written in the log
TAILNET_RANGE = ipaddress.ip_network("100.64.0.0/10")    # (IPv4 only: the listener listens on this PC's tailnet IPv4)
LIMITS = {"port": (int, 1, 65535, 18767), "whois_ttl": (float, 0.0, 3600.0, 60.0),       # (setting: type, min, max, default)
          "read_seconds": (float, 1.0, 120.0, READ_SECONDS), "max_connections": (int, 1, 256, MAX_CONNECTIONS)}
PLACEHOLDER_FILE = "muse_listener.json"     # (next to the tokens: cache/)


def route_allowed(method: str, raw_path: str) -> bool:
    path = urllib.parse.unquote(raw_path.split("?", 1)[0])          # (as the bridge reads it)
    return any(method == m and rx.match(path) for m, rx in _ROUTES)


class Whois:
    """the machine (StableID) behind a tailnet address, from this PC's Tailscale; kept for ttl seconds"""

    def __init__(self, cli: list, ttl: float):
        self.cli, self.ttl = list(cli), float(ttl)
        self.seen: dict = {}
        self.lock = threading.Lock()
        self.running = threading.BoundedSemaphore(WHOIS_AT_ONCE)

    def machine(self, ip: str) -> str | None:
        now = time.monotonic()
        with self.lock:
            hit = self.seen.get(ip)
            if hit is not None and now < hit[0]:
                return hit[1]
        if not self.running.acquire(timeout=5.0):
            return None                                   # (too many lookups at once: refused, not remembered)
        try:
            out = subprocess.run(self.cli + ["whois", "--json", ip], capture_output=True, text=True, encoding="utf-8",
                                 timeout=5, check=True).stdout
            node = json.loads(out).get("Node") or {}
            sid = str(node.get("StableID") or "") or None
        except (OSError, subprocess.SubprocessError, ValueError, AttributeError):
            sid = None
        finally:
            self.running.release()
        with self.lock:
            self.seen[ip] = (now + (self.ttl if sid else min(self.ttl, FAILED_WHOIS_SECONDS)), sid)
        return sid


def tailnet_addresses(cli: list) -> list:
    """this PC's own tailnet IPv4 addresses as Tailscale lists them; [] when it cannot say"""
    try:
        out = subprocess.run(cli + ["ip"], capture_output=True, text=True, encoding="utf-8", timeout=5, check=True).stdout
    except (OSError, subprocess.SubprocessError):
        return []
    found = []
    for line in out.splitlines():
        try:
            found.append(ipaddress.ip_address(line.strip()))
        except ValueError:
            continue
    return [str(a) for a in found if a.version == 4]


def listen_address(lc: dict, cli: list, test: bool) -> tuple:
    """(the address to listen on, None) or (None, why not): this PC's own tailnet IPv4 address only - one Tailscale lists,
    in its range; "address" may only name one of them. IPv4 only (the server's socket is IPv4). Loopback (127.0.0.1) only
    in the tests; never every interface"""
    given = str(lc.get("address") or "").strip()
    if given:
        try:
            a = ipaddress.ip_address(given)
        except ValueError:
            return None, f"address {given!r} is not an address"
        if a.is_unspecified:
            return None, "address may not be every interface"
        if a.version != 4:
            return None, "address must be an IPv4 address (the listener is IPv4 only)"
        if a.is_loopback:
            return (given, None) if test else (None, "a loopback address is for the tests only")
    if test:
        # (a test bridge - the checks' (run_checks.py) and the tests' - never takes Muse's port on the tailnet: Muse's
        # requests are the live bridge's. 2026-11-07: a check's bridge, with this PC's settings, opened it while the live
        # bridge was down, and a request from Muse's machine reached it)
        return None, "a test bridge (--test) listens on loopback only, never on the tailnet"
    own = tailnet_addresses(cli)
    if not own:
        return None, "this PC's tailnet address is unknown (Tailscale not running?)"
    address = given or own[0]
    if address not in own:
        return None, "address is not one of this PC's tailnet addresses (tailscale ip)"
    if ipaddress.ip_address(address) not in TAILNET_RANGE:
        return None, "the address is not in Tailscale's range"
    return address, None


def list_machines(cli: list) -> int:
    """this PC's tailnet as Tailscale lists it, for the owner to compare with the admin console (bridge.py --list-machines)"""
    try:
        st = json.loads(subprocess.run(cli + ["status", "--json"], capture_output=True, text=True, encoding="utf-8",
                                       timeout=10, check=True).stdout)
    except (OSError, subprocess.SubprocessError, ValueError) as e:
        print(f"tailscale status failed: {e}")
        return 1
    rows = [("this PC", st.get("Self") or {})] + [("", p) for p in (st.get("Peer") or {}).values()]
    print("StableID (muse_listener.machine)   name   tailnet IPv4   tags   online   (compare with Tailscale's admin console, Machines)")
    for mark, n in rows:
        ip4 = next((a for a in n.get("TailscaleIPs") or [] if "." in a), "?")
        print(f"{n.get('ID', '?')}   {n.get('HostName', '?')}{' (' + mark + ')' if mark else ''}   {ip4}   "
              f"{','.join(n.get('Tags') or []) or '-'}   {n.get('Online')}")
    return 0


class Placeholder:
    """the aid: the digest of Muse's usual Authorization ("learn" / "off" / a 64-hex digest; anything else refuses)"""

    def __init__(self, mode, file: Path):
        raw = "learn" if mode is None else str(mode).strip().lower()
        self.file, self.lock, self.told = file, threading.Lock(), False
        if raw in ("off", "learn") or re.fullmatch(r"[0-9a-f]{64}", raw):
            self.mode = raw
        else:
            self.mode = "invalid"
            sys.stderr.write("[bridge] muse listener: placeholder is not learn / off / a 64-hex digest - every request is "
                             "refused until it is fixed\n")

    def _learned(self) -> tuple:
        """("none", None): no file - the first time, or after the owner's reset; ("ok", digest); ("bad", None): there but
        unreadable or without a digest - never learnt over"""
        try:
            text = self.file.read_text(encoding="utf-8")
        except FileNotFoundError:
            return "none", None
        except (OSError, ValueError):                     # (unreadable, or not UTF-8 - a UnicodeDecodeError)
            return "bad", None
        try:
            v = json.loads(text).get("placeholder_sha256")
        except (ValueError, AttributeError):
            return "bad", None
        return ("ok", v) if isinstance(v, str) and re.fullmatch(r"[0-9a-f]{64}", v) else ("bad", None)

    def check(self, auth: str) -> bool:
        if self.mode == "invalid":
            return False
        if self.mode == "off":
            return True
        scheme, _, value = auth.partition(" ")
        value = value.strip()
        if scheme != "Bearer" or not value:
            return False
        digest = hashlib.sha256(value.encode("utf-8")).hexdigest()
        if self.mode != "learn":
            return hmac.compare_digest(digest, self.mode)
        with self.lock:
            kind, known = self._learned()
            if kind == "ok":
                self.told = False
                return hmac.compare_digest(digest, known)
            if kind == "bad":
                if not self.told:
                    self.told = True
                    sys.stderr.write(f"[bridge] muse listener: the learned digest ({self.file.name}) cannot be read - every "
                                     "request is refused (fix the file, or bridge.py --forget-muse-placeholder to learn again)\n")
                return False
            try:
                self.file.parent.mkdir(parents=True, exist_ok=True)
                with open(self.file, "x", encoding="utf-8") as f:       # (only when there is none: never over one)
                    f.write(json.dumps({"placeholder_sha256": digest}))
            except OSError:
                sys.stderr.write("[bridge] muse listener: the digest could not be kept - refused\n")
                return False
            sys.stderr.write("[bridge] muse listener: learned the digest of Muse's Authorization (the value is not kept)\n")
            return True


def forget_placeholder(tokens_file: Path) -> int:
    """the owner's reset (bridge.py --forget-muse-placeholder): the next Authorization from Muse's machine is learnt"""
    f = tokens_file.parent / PLACEHOLDER_FILE
    try:
        f.unlink()
        print(f"removed {f} - the next request from Muse's machine is learnt")
    except FileNotFoundError:
        print(f"nothing to remove ({f} does not exist) - the next request from Muse's machine is learnt")
    except OSError as e:
        print(f"could not remove {f}: {e}")
        return 1
    return 0


class ListenerServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = os.name != "nt"

    def __init__(self, address, handler, max_connections: int = MAX_CONNECTIONS):
        self.slots = threading.BoundedSemaphore(max(1, int(max_connections)))
        super().__init__(address, handler)

    def process_request(self, request, client_address):
        if not self.slots.acquire(blocking=False):
            self.shutdown_request(request)             # (busy: closed at once, no thread for it)
            return
        try:
            super().process_request(request, client_address)
        except Exception:
            self.slots.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.slots.release()

    def server_bind(self) -> None:
        if os.name == "nt" and hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


def settings(lc: dict) -> tuple:
    """({setting: value}, None) or (None, why not): the numbers checked - finite, of their type, within LIMITS"""
    out = {}
    for key, (kind, lo, hi, default) in LIMITS.items():
        raw = lc.get(key, default)
        if isinstance(raw, bool):
            return None, f"{key} must be a number"
        try:
            v = kind(raw)
        except (TypeError, ValueError, OverflowError):
            return None, f"{key} must be a number"
        if kind is int and isinstance(raw, float) and raw != int(raw):
            return None, f"{key} must be a whole number"
        if not (lo <= v <= hi):                          # (NaN fails this too)
            return None, f"{key} must be from {lo} to {hi}"
        out[key] = v
    return out, None


def start(cfg: dict, state, bridge_port: int, host_allowed, max_body: int):
    """open the listener when bridge_config.json "muse_listener" asks for it and is complete; None otherwise - the bridge
    goes on with its token check alone (auth stays required). Nothing here stops the bridge: a fault is logged and the
    listener alone stays closed"""
    try:
        return _start(cfg, state, bridge_port, host_allowed, max_body)
    except Exception as e:                               # (a setting or a fault not foreseen: the listener only)
        sys.stderr.write(f"[bridge] muse listener: not opened ({type(e).__name__}: {e})\n")
        return None


def _start(cfg: dict, state, bridge_port: int, host_allowed, max_body: int):
    lc = cfg.get("muse_listener") or {}
    if not isinstance(lc, dict):
        sys.stderr.write("[bridge] muse listener: muse_listener is not an object - not opened\n")
        return None
    if not lc.get("enabled"):
        return None
    machine = str(lc.get("machine") or "").strip()
    if not machine:
        sys.stderr.write("[bridge] muse listener: no machine (StableID) set - not opened (bridge.py --list-machines)\n")
        return None
    if not getattr(state, "auth", False):
        sys.stderr.write("[bridge] muse listener: the bridge's auth is off - not opened (it is for a bridge that asks for its token)\n")
        return None
    conf, why = settings(lc)
    if conf is None:
        sys.stderr.write(f"[bridge] muse listener: {why} - not opened\n")
        return None
    cli = lc.get("tailscale") or DEFAULT_TAILSCALE
    cli = [str(x) for x in (cli if isinstance(cli, list) else [cli])]
    address, why = listen_address(lc, cli, bool(cfg.get("_test")))
    if address is None:
        sys.stderr.write(f"[bridge] muse listener: {why} - not opened\n")
        return None
    whois = Whois(cli, conf["whois_ttl"])
    aid = Placeholder(lc.get("placeholder"), cfg["_tokens_file"].parent / PLACEHOLDER_FILE)
    read_seconds = conf["read_seconds"]

    class Handler(BaseHTTPRequestHandler):
        server_version = "MuseStudioListener/1.0"
        protocol_version = "HTTP/1.1"
        timeout = read_seconds                            # (each read; the whole request has the deadline below)

        def setup(self):
            super().setup()
            self.deadline = threading.Timer(read_seconds, self._cut)
            self.deadline.daemon = True
            self.deadline.start()

        def _cut(self):
            # (the request did not come in time: the connection is closed under the reader - and written in the log: its
            # sender sees its connection end with no answer. 2026-11-07: Muse's GET /api/state ended so, after 20 s, with
            # nothing in the log to tell why)
            cmd = getattr(self, "command", None)
            what = f"{cmd} {str(getattr(self, 'path', '') or '').split('?', 1)[0]}" if cmd else "(its request line had not come)"
            hdrs = getattr(self, "headers", None)
            length = hdrs.get("Content-Length") if hdrs is not None else None
            state.log_refusal("muse_listener_cut", f"[bridge] {time.strftime('%H:%M:%S')} muse listener cut a request not complete in {read_seconds:g} s: "
                                                   f"{what} - while {getattr(self, 'stage', 'reading its headers')}"
                                                   f"{f' (Content-Length {length})' if length else ''}\n")
            try:
                self.connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass

        def finish(self):
            self.deadline.cancel()
            try:
                super().finish()
            except OSError:
                pass

        def log_message(self, fmt, *args):
            pass                                          # (refusals are logged below; the bridge logs what it serves)

        def _refuse(self, code: int, reason: str, message: str):
            self.deadline.cancel()
            path = self.path.split("?", 1)[0]
            if state.take("anon") > 0.0:
                code, reason, message = 429, "rate", "too many refused requests: wait a little"
            state.log_refusal("muse_listener", f"[bridge] {time.strftime('%H:%M:%S')} muse listener refused {reason}: {self.command} {path}\n")
            body = json.dumps({"error": message, "reason": reason}).encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)
            self.close_connection = True

        def _any(self):
            self.close_connection = True                  # (one request a connection)
            h = self.headers
            if h.get("Origin") is not None or h.get("Sec-Fetch-Site") is not None:
                return self._refuse(403, "browser", "a web page may not use Muse's listener")
            if not host_allowed(h.get_all("Host"), cfg):
                return self._refuse(403, "host", "this Host is not one of the bridge's names (bridge_config.json allowed_hosts)")
            self.stage = "looking up the sending machine (tailscale whois)"
            sid = whois.machine(self.client_address[0])
            if sid is None:
                return self._refuse(403, "unverified", "the sending machine could not be looked up")
            if not hmac.compare_digest(sid.encode("utf-8"), machine.encode("utf-8")):
                return self._refuse(403, "not_muse_machine", "only Muse's machine may use this port")
            if not route_allowed(self.command, self.path):
                return self._refuse(403, "not_allowed", f"{self.command} is not one of Muse's ways here")
            if not aid.check(h.get("Authorization") or ""):
                return self._refuse(401, "placeholder", "Muse's usual Authorization is needed")
            if h.get("Transfer-Encoding"):
                return self._refuse(411, "length", "send the body with a Content-Length")
            try:
                n = int(h.get("Content-Length") or 0)
            except ValueError:
                return self._refuse(400, "length", "a bad Content-Length")
            if n > max_body:
                return self._refuse(413, "too_large", f"the body passes {max_body} bytes")
            body = None
            if self.command not in ("GET", "HEAD"):       # (the bridge's ways to read take no body: a length on one is not waited for)
                self.stage = "reading its body"
                body = self.rfile.read(n) if n > 0 else None
                if body is not None and len(body) < n:
                    return                                # (cut before the whole body came: logged by the cut)
            self.deadline.cancel()
            t0 = time.monotonic()
            fwd = {"Host": f"127.0.0.1:{bridge_port}", "Authorization": "Bearer " + state.tokens_now()["muse"]}
            for k in ("Content-Type", "Accept"):
                if h.get(k):
                    fwd[k] = h.get(k)
            try:
                c = http.client.HTTPConnection("127.0.0.1", bridge_port, timeout=60)
                c.request(self.command, self.path, body=body, headers=fwd)
                r = c.getresponse()
                data = r.read()
                c.close()
            except OSError:
                return self._refuse(502, "bridge", "the bridge did not answer")
            took = time.monotonic() - t0
            if took >= SLOW_SECONDS:                      # (a slow answer is written: the sender may have stopped waiting)
                state.log_refusal("muse_listener_slow", f"[bridge] {time.strftime('%H:%M:%S')} muse listener: {self.command} "
                                                        f"{self.path.split('?', 1)[0]} took {took:.1f} s at the bridge ({r.status})\n")
            self.send_response(r.status)
            for k in PASS_BACK:
                v = r.getheader(k)
                if v:
                    self.send_header(k, v)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        do_GET = do_POST = do_HEAD = do_PUT = do_DELETE = do_PATCH = do_OPTIONS = _any

    port = conf["port"]
    try:
        srv = ListenerServer((address, port), Handler, conf["max_connections"])
    except OSError as e:
        sys.stderr.write(f"[bridge] muse listener: cannot listen on the tailnet address's port {port} ({e}) - not opened\n")
        return None
    threading.Thread(target=srv.serve_forever, daemon=True, name="muse-listener").start()
    sys.stderr.write(f"[bridge] muse listener: on this PC's tailnet address, port {port} (Muse's machine only; "
                     f"placeholder {aid.mode if aid.mode in ('off', 'learn') else 'digest'})\n")
    return srv
