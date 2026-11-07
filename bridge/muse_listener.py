"""Muse's own listener (PR #31 and GPT's design review): Muse's requests told apart by the machine they come from.

Muse's vault puts a stand-in (a placeholder) in its requests' Authorization and swaps in the real key only on its usual way
out - not on the tailnet's way to this PC, so the bridge's token check locked Muse out. This listener takes Muse's requests
on a port of its own on this PC's tailnet address (never loopback: a program on this PC cannot reach it as 127.0.0.1) and:

  1. looks up the real TCP sender's address with Tailscale (`tailscale whois`): the machine must be the one
     bridge_config.json "muse_listener.machine" names - its StableID, which the owner checked in Tailscale's admin console.
     No header is trusted for this (X-Forwarded-For and the like are not read). Unknown, failed or stale -> refused.
  2. (an aid, not the proof) Muse's usual Authorization: "placeholder" "learn" keeps the SHA-256 of the first one the pinned
     machine sends (cache/muse_listener.json, this PC only) and asks for the same from then on; a 64-hex value is that
     digest given; "off" does not look. The value itself is never written or logged.
  3. passes only Muse's own API, listed here by method and path (not the bridge's roles: they also serve shared ways), and
     no browser's request (Origin / Sec-Fetch-Site) and no Host but the bridge's names.

Then the request goes to this PC's bridge (127.0.0.1, its own port) with the real Muse token put in here - the token never
leaves this PC - and the bridge's own checks (role, rate, sizes) apply as for any request. Standard library only.
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
    ("GET", r"/api/match"),
    ("POST", r"/api/match/rooms"),
    ("GET", rf"/api/match/rooms/{ROOM}"),
    ("POST", rf"/api/match/rooms/{ROOM}/(?:peer|ping|send|close)"),
]
_ROUTES = [(m, re.compile(p + r"\Z")) for m, p in ROUTES]
DEFAULT_TAILSCALE = [r"C:\Program Files\Tailscale\tailscale.exe"] if os.name == "nt" else ["tailscale"]
PASS_BACK = ("Content-Type", "Retry-After", "WWW-Authenticate", "Allow", "Cache-Control")
FAILED_WHOIS_SECONDS = 5.0      # a failed lookup is not asked again for this long (a flood does not start a process each)


def route_allowed(method: str, raw_path: str) -> bool:
    path = urllib.parse.unquote(raw_path.split("?", 1)[0])          # (as the bridge reads it)
    return any(method == m and rx.match(path) for m, rx in _ROUTES)


class Whois:
    """the machine (StableID) behind a tailnet address, from this PC's Tailscale; kept for ttl seconds"""

    def __init__(self, cli: list, ttl: float):
        self.cli, self.ttl = list(cli), float(ttl)
        self.seen: dict = {}
        self.lock = threading.Lock()

    def machine(self, ip: str) -> str | None:
        now = time.monotonic()
        with self.lock:
            hit = self.seen.get(ip)
            if hit is not None and now < hit[0]:
                return hit[1]
        sid = None
        try:
            out = subprocess.run(self.cli + ["whois", "--json", ip], capture_output=True, text=True, encoding="utf-8",
                                 timeout=5, check=True).stdout
            node = json.loads(out).get("Node") or {}
            sid = str(node.get("StableID") or "") or None
        except (OSError, subprocess.SubprocessError, ValueError, AttributeError):
            sid = None
        with self.lock:
            self.seen[ip] = (now + (self.ttl if sid else min(self.ttl, FAILED_WHOIS_SECONDS)), sid)
        return sid


def tailnet_ipv4(cli: list) -> str | None:
    try:
        out = subprocess.run(cli + ["ip", "-4"], capture_output=True, text=True, encoding="utf-8", timeout=5, check=True).stdout
        ip = out.strip().splitlines()[0].strip()
        ipaddress.IPv4Address(ip)
        return ip
    except (OSError, subprocess.SubprocessError, ValueError, IndexError):
        return None


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
    """the aid: the digest of Muse's usual Authorization ("off" / "learn" / a 64-hex digest)"""

    def __init__(self, mode: str, file: Path):
        mode = str(mode or "off").strip().lower()
        self.file, self.lock = file, threading.Lock()
        self.mode = mode if mode in ("off", "learn") or re.fullmatch(r"[0-9a-f]{64}", mode) else "off"
        if self.mode != str(mode or "off").strip().lower():
            sys.stderr.write("[bridge] muse listener: placeholder is not off / learn / a 64-hex digest - not looked at\n")

    def _learned(self) -> str | None:
        try:
            v = json.loads(self.file.read_text(encoding="utf-8")).get("placeholder_sha256")
            return v if isinstance(v, str) and re.fullmatch(r"[0-9a-f]{64}", v) else None
        except (OSError, ValueError, AttributeError):
            return None

    def check(self, auth: str) -> bool:
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
            known = self._learned()
            if known is None:
                self.file.parent.mkdir(parents=True, exist_ok=True)
                tmp = self.file.with_suffix(".part")
                tmp.write_text(json.dumps({"placeholder_sha256": digest}), encoding="utf-8")
                os.replace(tmp, self.file)
                sys.stderr.write("[bridge] muse listener: learned the digest of Muse's Authorization (the value is not kept)\n")
                return True
            return hmac.compare_digest(digest, known)


class ListenerServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = os.name != "nt"

    def server_bind(self) -> None:
        if os.name == "nt" and hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


def start(cfg: dict, state, bridge_port: int, host_allowed, max_body: int):
    """open the listener when bridge_config.json "muse_listener" asks for it and is complete; None otherwise (the bridge
    goes on with its token check alone - auth stays required)"""
    lc = cfg.get("muse_listener") or {}
    if not lc.get("enabled"):
        return None
    machine = str(lc.get("machine") or "").strip()
    if not machine:
        sys.stderr.write("[bridge] muse listener: no machine (StableID) set - not opened (bridge.py --list-machines)\n")
        return None
    cli = lc.get("tailscale") or DEFAULT_TAILSCALE
    cli = [str(x) for x in (cli if isinstance(cli, list) else [cli])]
    address = str(lc.get("address") or "").strip() or tailnet_ipv4(cli)
    if not address:
        sys.stderr.write("[bridge] muse listener: this PC's tailnet address is unknown (Tailscale not running?) - not opened\n")
        return None
    try:
        loop = ipaddress.ip_address(address).is_loopback
    except ValueError:
        loop = True
    if loop and not cfg.get("_test"):
        sys.stderr.write("[bridge] muse listener: a loopback address is for the tests only - not opened\n")
        return None
    whois = Whois(cli, float(lc.get("whois_ttl", 60)))
    aid = Placeholder(lc.get("placeholder", "off"), cfg["_tokens_file"].parent / "muse_listener.json")

    class Handler(BaseHTTPRequestHandler):
        server_version = "MuseStudioListener/1.0"
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):
            pass                                          # (refusals are logged below; the bridge logs what it serves)

        def _refuse(self, code: int, reason: str, message: str):
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
            h = self.headers
            if h.get("Origin") is not None or h.get("Sec-Fetch-Site") is not None:
                return self._refuse(403, "browser", "a web page may not use Muse's listener")
            if not host_allowed(h.get_all("Host"), cfg):
                return self._refuse(403, "host", "this Host is not one of the bridge's names (bridge_config.json allowed_hosts)")
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
            body = self.rfile.read(n) if n > 0 else None
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
            self.send_response(r.status)
            for k in PASS_BACK:
                v = r.getheader(k)
                if v:
                    self.send_header(k, v)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        do_GET = do_POST = do_HEAD = do_PUT = do_DELETE = do_PATCH = do_OPTIONS = _any

    port = int(lc.get("port", 18767))
    try:
        srv = ListenerServer((address, port), Handler)
    except OSError as e:
        sys.stderr.write(f"[bridge] muse listener: cannot listen on the tailnet address's port {port} ({e}) - not opened\n")
        return None
    threading.Thread(target=srv.serve_forever, daemon=True, name="muse-listener").start()
    sys.stderr.write(f"[bridge] muse listener: on this PC's tailnet address, port {port} (Muse's machine only; "
                     f"placeholder {aid.mode if aid.mode in ('off', 'learn') else 'digest'})\n")
    return srv
