"""MuseStudio bridge (begun as MetaMuse Stage's, the Godot version's; its routes are kept): the local server between Muse,
Irodori and the app (MuseStudio; the Godot app spoke the same contract, MUSE_GUIDE.md).

  GET  /                      the Godot version's web export (static files of web_root; none -> static/no_web_build.html)
  GET  /health                {"ok": true, "irodori": true|false, "voice": true|false, "queue": packages waiting,
                              "launch": {"id", "phase", "started", "updated", "character"}}
                              (voice false: bridge_config.json irodori.enabled is false - the voice is off, Irodori is not called)
                              (launch: the app's start-up as it reports it - phase bridge_started (no app yet) / app_starting /
                              stage_ready (waiting for the character to be chosen) / character_selected; MUSE_GUIDE.md 7)
  POST /api/launch            the app's start-up phase {"launch": id, "phase", "character"}; a new launch id drops the packages
                              waiting from before it (package_dropped, reason "before_launch")
  POST /api/packages          a turn package -> FIFO queue, saved first (cache/packages.json) -> {"accepted": true, "queue": n,
                              "id"}; malformed -> 400; not saved -> 503 (not taken: send it again). A package sent again (its
                              "id", else the same content) is not queued twice: {"accepted": true, "duplicate": true, "stage"}
  GET  /api/packages/next     the next package; 204 when empty. One tagged with another launch or another character than the
                              app's now is dropped instead (package_dropped) - an untagged one is for the launch and character
                              of when it came. ?hold=1: the app is not ready to present (before the character is chosen) - 204,
                              the queue kept (the response pool). ?ack=1 (MuseStudio): the package stays until the app's
                              POST /api/packages/ack (received) - an answer lost on the way is given again; without it, taken
                              out as before
  POST /api/packages/ack      (the app) {"ids": [...], "stage": "received" / "started" / "finished" / "dropped", "reason",
                              "block", "blocks"} -> the packages' delivery stages; received / dropped take the body out of the
                              queue (the record stays, no text); started with "block" (from 0): that block shown, its
                              operations done (last_block); finished: all shown, or cut short (reason). A new launch makes the
                              started ones not finished interrupted (package_interrupted, last_block)
  GET  /api/packages/status   (Muse) ?id=<id> (or ?session=&turn=) -> a package's delivery stage: queued / delivered /
                              received / started / finished / interrupted / dropped (with the reason, last_block, blocks);
                              without them the last 50
  POST /api/tts               {"text", "voice", "caption", "emoji", "seed"} -> {"audio_url": "/audio/<hash>.wav"} (cached); 502 on failure,
                              503 {"voice": false} while the voice is off
  GET  /audio/<hash>.wav      generated audio
  POST /api/choices           {"session", "turn", "kind", "value"} -> choice queue, and a "choice" event
  GET  /api/choices           the choice queue (emptied; kept for older callers - the event feed is the main path)
  POST /api/events            an app event {"type", ...} -> the event feed (gets "id" and "time")
  GET  /api/events            peek: the unacknowledged events, oldest first (?after=<id>&limit=<n>); nothing is removed
  POST /api/events/ack        {"ids": [...]} and/or {"upto": <id>} -> those events leave the feed
  POST /api/state             the app's current state (replaces the last one)
  GET  /api/state             that state + whether the app is connected, the package queue and the pending events
  GET  /api/characters        the characters and the active one: MuseStudio's cards (characters_source "cards": the card folders
                              read on each request) or the Godot app's saves ("saves": the project's characters/ folder)
  GET  /api/voices            the voices Irodori knows, named by voice_catalog.json (the creation's voice list)
  POST /api/catalog           the app's play catalog (the plays, the spots of the location, what can start now)
  GET  /api/catalog           that catalog ({"catalog": null} before the app sent one)
App windows send X-MMS-Instance / X-MMS-Started: only the newest window that is polling gets packages and may post state /
events (409 for an older one: two windows would take turns with their states and eat each other's packages).
  /api/match/...              the Muse-to-Muse match (muse_match.py: rooms with one-off keys, the encrypted relay, the peer's
                              packages as match_* events); 503 without the 'cryptography' package
  GET  /kkdata/manifest.json  the copied KoikatsuOfGodot data list (web build: data_sync.gd)
  GET  /kkdata/<path>         one copied data file
Python standard library only (http.server.ThreadingHTTPServer) - the match alone needs 'cryptography' (optional). Settings:
bridge_config.json next to this file.
"""
from __future__ import annotations

import argparse
import collections
import hashlib
import hmac
import json
import mimetypes
import os
import re
import secrets
import shutil
import socket
import sys
import threading
import time
import urllib.parse
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

try:
    import muse_match
except Exception:              # (the file missing: no match either)
    muse_match = None

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from irodori_client import IrodoriClient, IrodoriError  # noqa: E402
import kk_card  # noqa: E402
import muse_listener  # noqa: E402
import labels  # noqa: E402

BLOCK_TYPES = {"dialogue", "sound"}
CHOICE_KINDS = {"stance", "state_check", "situation"}
FINISH_VALUES = {"soon", "inside", "outside", "drink", "vomit"}   # play.finish: hold the OLoop, or the place
MATCH_FORMATS = ("sex", "lesbian")     # match.start.limits.format: the host's (a match with the original's male part / two women)
MOOD_VALUES = {"satisfied", "relieved", "leaked", "worked"}       # mood.<key>: her climax satisfied it / she went to the toilet / she wet herself / she worked (cleaning: the wish to shower) (Muse's calls)
LAUNCH_PHASES = ("app_starting", "stage_ready", "character_selected")   # POST /api/launch (before any: "bridge_started")
MAX_BODY = 2 * 1024 * 1024
MAX_CREATION_CHARS = 1536 * 1024   # match.start's opponent creation (base64; a creation is the card's numbers, no picture)
MAX_EVENTS = 1000              # unacknowledged events kept; past this the oldest are dropped (logged)
# Totals a sender gone wrong cannot push past (the security review of 2026-11-05, SEC-07): far above everyday use.
MAX_EVENTS_BYTES = 16 * 1024 * 1024    # the unacknowledged events altogether; past it the oldest are dropped (logged)
MAX_PACKAGES = 200                     # packages waiting for the app; past it 429
MAX_PACKAGE_LOG = 500                  # the packages' delivery records kept (no text: id, session, turn, stage, times)
PACKAGE_LOG_DAYS = 7                   # a finished record older than this goes
PACKAGE_STAGES = ("queued", "delivered", "received", "started", "finished", "interrupted", "dropped")
ACK_STAGES = ("received", "started", "finished", "dropped")
# (#70 adopted, 1: after the start) started's blocks: the app tells each block it has shown ("block": its index, from 0 - its
# operations done) and finished when it has shown them all (or cut it short: reason "superseded" - Muse's next package in,
# or "cleared"); a new launch finds the started ones not finished: interrupted, the last block known shown kept
# (last_block) - the one after it not known either way (shown or not: the app may have gone between showing it and telling)
CONTENT_DUP_SECONDS = 600              # a package without an id: the same content within this is the same package sent again
MAX_PACKAGES_BYTES = 16 * 1024 * 1024  # those packages altogether; past it 429
MAX_EVENT_BYTES = 4 * 1024 * 1024      # one event (an app's is under MAX_BODY; a match's package can be more): past it refused
MAX_EVENTS_FILE_BYTES = 64 * 1024 * 1024    # a saved feed bigger than this (one from before the limits) is not read: kept aside
MAX_CHOICES = 200                      # the old choice queue (GET /api/choices); past it the oldest are dropped
MAX_CHOICES_BYTES = 1024 * 1024        # that queue altogether; past it the oldest are dropped
MAX_CHOICE_CHARS = {"session": 256, "kind": 64, "value": 4096}    # one choice's strings
# what Muse and the app are told when a file of the bridge's cannot be used (GPT's re-review of #39: no path of this PC in an
# answer - the bridge's log has the file and the system's message)
PACKAGES_UNREAD = ("the package queue's file could not be read (another program may hold it): no package is taken until it can be "
                   "(it is read again before each use; the file is kept as it is)")
PACKAGES_GARBLED = "the package queue's file is not one and could not be copied aside: no package is taken until a person moves it away"
EVENTS_UNREAD = "the event feed's file could not be read (another program may hold it): no event is taken until it can be"
EVENTS_GARBLED = "the event feed's file is not a feed and could not be copied aside: no event is taken until a person moves it away"
EVENTS_OVERSIZE = "the event feed's old file is too big and could not be kept aside: no event is taken until a person moves it away"
MAX_EVENT_IDS = 4096           # the app's event ids remembered (a resent event is told apart; see add_app_event)
# requests per sender (the security review's SEC-07, after SEC-01 told the senders apart): [per second, at once] - far above
# use (Muse polls every few seconds; the app reports its state up to some 30 times a second in the checks); past it 429 with
# Retry-After. bridge_config.json
# "rate_limits" may set others. With auth off every request is one sender ("all"); requests without a token are "anon".
RATE_LIMITS = {"muse": [20, 100], "app": [100, 400], "all": [120, 500], "anon": [20, 100]}
APP_SEEN_SECONDS = 5.0         # the app polls /api/packages/next every second: "connected" if it did within this
STATE_FIELDS = ("replay", "character", "mode", "outfit", "location", "scene", "emotion", "face", "camera", "package", "gauge", "play", "mood",
                "beat", "presence", "belief", "urges", "outing", "her_location", "relationship", "clothes", "app_name", "match",
                "settings", "play_serial", "items")      # (the contract's 7.4: test_state_fields.py holds them to it)
SPEAKERS = ("self", "opponent")        # package.speaker (the match: the opponent's package, forwarded by the local Muse)
MATCH_PHASES = ("intro", "negotiation", "sex", "end")
MATCH_SIDES = ("host", "guest")
MIME = {".wasm": "application/wasm", ".pck": "application/octet-stream", ".js": "application/javascript",
        ".html": "text/html; charset=utf-8", ".json": "application/json", ".wav": "audio/wav", ".png": "image/png",
        ".bin": "application/octet-stream", ".dds": "application/octet-stream", ".svg": "image/svg+xml"}


def load_config(path: Path) -> dict:
    cfg = json.loads(path.read_text(encoding="utf-8"))

    def resolve(p: str) -> Path:
        q = Path(p)
        return q if q.is_absolute() else (path.parent / q).resolve()

    cfg["_web_root"] = resolve(cfg.get("web_root", "../web"))
    cfg["_static_root"] = resolve(cfg.get("static_root", "static"))
    cfg["_audio_dir"] = resolve(cfg.get("audio_cache_dir", "cache/audio"))
    cfg["_kkdata_root"] = resolve(cfg.get("kkdata_root", "../kk_data"))
    cfg["_events_file"] = resolve(cfg.get("events_file", "cache/events.json"))
    cfg["_packages_file"] = resolve(cfg["packages_file"]) if cfg.get("packages_file") else cfg["_events_file"].parent / "packages.json"
    cfg["_characters_dir"] = resolve(cfg.get("characters_dir", "../characters"))
    cfg["_card_dirs"] = [resolve(d) for d in cfg.get("card_dirs", [])]
    cfg["_persona_file"] = resolve(cfg["persona_file"]) if cfg.get("persona_file") else None
    cfg["_voice_catalog"] = resolve(cfg.get("voice_catalog", "voice_catalog.json"))
    cfg["_web_data_list"] = resolve(cfg.get("web_data_list", "../tools/kk_web_list.txt"))
    cfg["_tokens_file"] = resolve(cfg.get("tokens_file", "cache/tokens.json"))
    cfg["_labels_file"] = resolve(cfg["labels_file"]) if cfg.get("labels_file") else cfg["_events_file"].parent / "labels.json"
    return cfg


# ---- who may use the bridge (the security review of 2026-11-05, SEC-01 and SEC-06) ------------------------------------
# Two tokens, made at the first start and kept on this PC only (tokens_file, default bridge/cache/tokens.json - never in the
# repository or a log): "muse" for Muse (packages, the feed and its ack, the state and lists, the match's rooms) and "app"
# for MuseStudio and the launcher (events, state, catalog and launch reports, taking packages, watching its match room).
# Sent as "Authorization: Bearer <token>". bridge_config.json "auth": "required" (the default, from the start) or "off" (the
# bridge's own tests only). GET /health answers without a token, but only that the bridge is alive. And every request's Host
# must be this PC's own name (127.0.0.1 / localhost / [::1]) or one "allowed_hosts" lists (the tailnet name Tailscale's
# serve passes on): a foreign name pointed here (DNS rebinding) is refused whether or not the request looks like a browser's.
# Muse's own listener (muse_listener.py, PR #31): where Muse's vault cannot put the real key on the tailnet's way, Muse's
# requests come to a port of their own on this PC's tailnet address, are let in by the sending machine (Tailscale's
# whois of the real TCP sender) and reach this bridge with the real Muse token put in on this PC.

ROLES = ("muse", "app")
_MUSE = {("GET", "/api/events"), ("POST", "/api/events/ack"), ("POST", "/api/packages"), ("GET", "/api/packages/status"), ("GET", "/api/state"),
         ("GET", "/api/labels/coordinates"), ("POST", "/api/labels/coordinates"), ("GET", "/api/labels/rooms"), ("POST", "/api/labels/rooms"),
         ("GET", "/api/catalog"), ("GET", "/api/characters"), ("GET", "/api/voices"), ("GET", "/api/choices"),
         ("POST", "/api/test/faults")}
_APP = {("POST", "/api/events"), ("POST", "/api/state"), ("POST", "/api/catalog"), ("POST", "/api/launch"),
        ("GET", "/api/packages/next"), ("POST", "/api/packages/ack"), ("POST", "/api/choices")}


def roles_for(method: str, path: str) -> set:
    """the roles whose token may make this request (GET /health: any, or none for the short answer)"""
    m = "GET" if method == "HEAD" else method
    if (m, path) in _MUSE:
        return {"muse"}
    if (m, path) in _APP:
        return {"app"}
    if path == "/api/match" or path.startswith("/api/match/"):
        parts = path.strip("/").split("/")
        if m == "GET" and len(parts) == 4 and parts[2] == "rooms":
            return {"muse", "app"}                    # (the app watches its room: closed by the peer)
        if m == "POST" and len(parts) == 5 and parts[2] == "rooms" and parts[4] == "close":
            return {"muse", "app"}                    # (the user leaving the match closes its room - Match.cs)
        return {"muse"}
    return set(ROLES)                                 # (the voice, the files and pages, the queue's count, a path unknown: 404)


def save_tokens(path: Path, toks: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".part")
    tmp.write_text(json.dumps(toks, indent=1), encoding="utf-8")
    os.replace(tmp, path)


def load_tokens(path: Path) -> dict:
    """the two tokens, made (and saved) when missing"""
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        doc = {}
    toks = {r: doc[r] for r in ROLES if isinstance(doc.get(r), str) and len(doc[r]) >= 32}
    if len(toks) < len(ROLES):
        for r in ROLES:
            toks.setdefault(r, secrets.token_urlsafe(32))
        save_tokens(path, toks)
        sys.stderr.write(f"[bridge] made the bridge's tokens: {path}\n")
    return toks


HOST_SHAPE = re.compile(r"^(\[[0-9a-f:.]+\]|[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?\.?)(?::[0-9]{1,5})?$")


def host_allowed(host, cfg: dict) -> bool:
    """a request's Host: this PC's own names, or one bridge_config.json allowed_hosts lists (the port not compared). One
    header only, a name's shape only (GPT's re-review of #27: a second Host, or junk after an IPv6 name, came through)"""
    if isinstance(host, list):
        if len(host) != 1:
            return False
        host = host[0]
    if not host:
        return False
    m = HOST_SHAPE.match(host.strip().lower())
    if not m:
        return False
    allowed = {"127.0.0.1", "localhost", "[::1]"} | {str(x).strip().lower().rstrip(".") for x in cfg.get("allowed_hosts", [])}
    return m.group(1).rstrip(".") in allowed


# ---- turn package validation (spec 3.1) ------------------------------------------------------------------------------

def _opt_dict(v, name: str, errors: list) -> dict | None:
    if v is None:
        return None
    if not isinstance(v, dict):
        errors.append(f"{name} must be an object or null")
        return None
    return v


def _opt_str(d: dict, key: str, name: str, errors: list) -> None:
    if key in d and d[key] is not None and not isinstance(d[key], str):
        errors.append(f"{name}.{key} must be a string or null")


def package_key(pkg: dict) -> str:
    """the package's identity: its own id, else its content (a resend after a lost answer is the same content)"""
    pid = pkg.get("id")
    if isinstance(pid, str) and pid:
        return pid
    plain = {k: v for k, v in pkg.items() if not k.startswith("_")}
    return "h:" + hashlib.sha256(json.dumps(plain, ensure_ascii=False, sort_keys=True).encode("utf-8")).hexdigest()[:32]


def validate_package(pkg) -> list[str]:
    """Structural checks only. Unknown labels / keys / camera names pass: the app falls back (spec 7)."""
    errors: list[str] = []
    if not isinstance(pkg, dict):
        return ["package must be a JSON object"]
    if not isinstance(pkg.get("session"), str):
        errors.append("session must be a string")
    turn = pkg.get("turn")
    if isinstance(turn, bool) or not isinstance(turn, int):
        errors.append("turn must be an integer")
    if "situation" in pkg and pkg["situation"] is not None and not isinstance(pkg["situation"], str):
        errors.append("situation must be a string or null")
    bt = pkg.get("beat")
    if bt is not None and (isinstance(bt, bool) or not isinstance(bt, int) or bt < 0):
        errors.append("beat must be a non-negative integer or null")
    for key in ("launch", "character"):
        if pkg.get(key) is not None and not isinstance(pkg[key], str):
            errors.append(f"{key} must be a string or null")
    pid = pkg.get("id")
    if pid is not None and (not isinstance(pid, str) or not 1 <= len(pid) <= 200):
        errors.append("id must be a string of 1-200 characters or null")
    if pkg.get("monologue") is not None and not isinstance(pkg["monologue"], bool):
        errors.append("monologue must be true / false or null")
    if pkg.get("speaker") is not None and pkg["speaker"] not in SPEAKERS:
        errors.append("speaker must be 'self' / 'opponent' or null")
    mt = pkg.get("match")
    if mt is not None:
        if not isinstance(mt, dict):
            errors.append("match must be an object or null")
        else:
            st = mt.get("start")
            if st is not None:
                if not isinstance(st, dict) or not isinstance(st.get("room"), str) or st.get("side") not in MATCH_SIDES:
                    errors.append("match.start must be {room, side: host / guest, opponent: {name, creation}, limits?}")
                else:
                    op = st.get("opponent")
                    if not isinstance(op, dict) or not isinstance(op.get("name"), str) or (op.get("creation") is not None and not isinstance(op["creation"], str)):
                        errors.append("match.start.opponent must be {name: string, creation: string or null}")
                    elif isinstance(op.get("creation"), str) and len(op["creation"]) > MAX_CREATION_CHARS:
                        errors.append(f"match.start.opponent.creation passes {MAX_CREATION_CHARS} characters")
                    lim = st.get("limits")
                    if lim is not None and not isinstance(lim, dict):
                        errors.append("match.start.limits must be an object or null")
                    lim = lim if isinstance(lim, dict) else {}
                    if lim.get("format") is not None and lim["format"] not in MATCH_FORMATS:
                        errors.append("match.start.limits.format must be 'sex' / 'lesbian' or null")
                    a0 = lim.get("arousal_start")
                    if a0 is not None and (isinstance(a0, bool) or not isinstance(a0, (int, float)) or not 0 <= float(a0) <= 1):
                        errors.append("match.start.limits.arousal_start must be a number 0..1 or null")
                    if st.get("side") == "guest":
                        # (the guest starts from the host's conditions: the host's values, carried in the invitation)
                        for k in ("arousal_start", "format"):
                            if lim.get(k) is None:
                                errors.append(f"match.start.limits.{k} is required for the guest (the host's value from the invitation)")
            if mt.get("end") is not None and not isinstance(mt["end"], str):
                errors.append("match.end must be a string (the reason) or null")
            if mt.get("export_creation") is not None and not isinstance(mt["export_creation"], bool):
                errors.append("match.export_creation must be true / false or null")
    st = pkg.get("settings")
    if st is not None:
        # (Muse's change of the speeds the user asked for - PR #12 adopted; the app judges the names and the ranges)
        if not isinstance(st, dict):
            errors.append("settings must be an object or null")
        else:
            for k, v in st.items():
                if k == "request":
                    if not isinstance(v, str) or not v or len(v) > 200:
                        errors.append("settings.request must be a string of 1-200 characters")
                elif isinstance(v, bool) or not isinstance(v, (int, float)):
                    errors.append(f"settings.{k} must be a number")
    rel = pkg.get("relationship")
    if rel is not None:
        if not isinstance(rel, dict) or "person" not in rel:
            errors.append("relationship must be an object with person (a number, or null for Muse herself), affection and trust")
        else:
            if rel["person"] is not None and (isinstance(rel["person"], bool) or not isinstance(rel["person"], int)):
                errors.append("relationship.person must be an integer or null")
            for k in ("affection", "trust"):
                v = rel.get(k)
                if isinstance(v, bool) or not isinstance(v, (int, float)) or not 0 <= float(v) <= 100:
                    errors.append(f"relationship.{k} must be a number 0-100")
    blocks = pkg.get("blocks")
    if not isinstance(blocks, list) or not blocks:
        errors.append("blocks must be a non-empty array")
        blocks = []
    for i, b in enumerate(blocks):
        n = f"blocks[{i}]"
        if not isinstance(b, dict):
            errors.append(f"{n} must be an object")
            continue
        if b.get("type") not in BLOCK_TYPES:
            errors.append(f"{n}.type must be 'dialogue' or 'sound'")
        if not isinstance(b.get("text"), str):
            errors.append(f"{n}.text must be a string")
        emo = _opt_dict(b.get("emotion"), n + ".emotion", errors)
        if emo is not None:
            if not isinstance(emo.get("label"), str):
                errors.append(f"{n}.emotion.label must be a string")
            _opt_str(emo, "caption", n + ".emotion", errors)
            _opt_str(emo, "emoji", n + ".emotion", errors)
        sc = _opt_dict(b.get("scene"), n + ".scene", errors)
        if sc is not None:
            if not isinstance(sc.get("key"), str):
                errors.append(f"{n}.scene.key must be a string")
            it = sc.get("intensity", 1)
            if it is not None and (isinstance(it, bool) or not isinstance(it, int) or not 1 <= it <= 3):
                errors.append(f"{n}.scene.intensity must be an integer 1-3")
            sp = sc.get("speed", 1.0)
            if sp is not None and (isinstance(sp, bool) or not isinstance(sp, (int, float)) or not 0.5 <= float(sp) <= 2.0):
                errors.append(f"{n}.scene.speed must be a number 0.5-2.0")
        if "camera" in b and b["camera"] is not None and not isinstance(b["camera"], str):
            errors.append(f"{n}.camera must be a string or null")
        vo = _opt_dict(b.get("voice"), n + ".voice", errors)
        if vo is not None:
            _opt_str(vo, "engine", n + ".voice", errors)
            _opt_str(vo, "voice", n + ".voice", errors)
            pr = vo.get("prompt")
            if pr is not None and (isinstance(pr, bool) or not isinstance(pr, int) or pr < 0):
                errors.append(f"{n}.voice.prompt must be a non-negative integer or null")
        if "se" in b and b["se"] is not None and not isinstance(b["se"], str):
            errors.append(f"{n}.se must be a string or null")
        if "location" in b and b["location"] is not None and not isinstance(b["location"], str):
            errors.append(f"{n}.location must be a string or null")
        pl = _opt_dict(b.get("play"), n + ".play", errors)
        if pl is not None:
            pc = pl.get("pace")
            if pc is not None and (isinstance(pc, bool) or not isinstance(pc, int) or not 0 <= pc <= 3):
                errors.append(f"{n}.play.pace must be an integer 0-3 or null")
            ps = pl.get("speed")
            if ps is not None and (isinstance(ps, bool) or not isinstance(ps, (int, float)) or not 0.5 <= float(ps) <= 2.0):
                errors.append(f"{n}.play.speed must be a number 0.5-2.0 or null")
            fi = pl.get("finish")
            if fi is not None and fi not in FINISH_VALUES:
                errors.append(f"{n}.play.finish must be soon / inside / outside / drink / vomit or null")
            for key in ("pull", "insert", "condom"):
                pu = pl.get(key)
                if pu is not None and not isinstance(pu, bool):
                    errors.append(f"{n}.play.{key} must be true / false or null")
            ps0 = pl.get("start")
            if ps0 is not None and not isinstance(ps0, str):
                errors.append(f"{n}.play.start must be a play id (string) or null")
            if pl.get("male_part") is not None and pl["male_part"] not in MATCH_SIDES:
                errors.append(f"{n}.play.male_part must be 'host' / 'guest' or null")
            for key in ("spot", "location"):
                if pl.get(key) is not None and not isinstance(pl[key], str):
                    errors.append(f"{n}.play.{key} must be a string or null")
            # (Muse's start of a normal play - MUSE_GUIDE 3.7: its request and the play_serial it saw; the app checks them as it shows)
            rq = pl.get("request")
            if rq is not None and (not isinstance(rq, str) or not rq or len(rq) > 200):
                errors.append(f"{n}.play.request must be a string of 1-200 characters or null")
            bs = pl.get("basis")
            if bs is not None and (isinstance(bs, bool) or not isinstance(bs, int) or bs < 0):
                errors.append(f"{n}.play.basis must be a non-negative integer (the state's play_serial) or null")
            for key in ("anal", "reverse"):
                if pl.get(key) is not None and not isinstance(pl[key], bool):
                    errors.append(f"{n}.play.{key} must be true / false or null")
            if pl.get("strapon") is not None and not isinstance(pl["strapon"], bool):
                errors.append(f"{n}.play.strapon must be true / false or null")
            if pl.get("end") is not None and not isinstance(pl["end"], bool):
                errors.append(f"{n}.play.end must be true / false or null")
        pl0 = b.get("play")
        if isinstance(pl0, dict) and pl0.get("climax") is not None and pl0["climax"] != "player":
            errors.append(f"{n}.play.climax must be 'player' (the user's declared climax) or null")
        if b.get("pose") is not None and not isinstance(b["pose"], str):
            errors.append(f"{n}.pose must be a string (a talk motion's name) or null")
        if b.get("declare") is not None and b["declare"] != "climax":
            errors.append(f"{n}.declare must be 'climax' or null")
        if b.get("anim") is not None and b["anim"] != "climax":
            errors.append(f"{n}.anim must be 'climax' or null")
        if b.get("match_phase") is not None and b["match_phase"] not in MATCH_PHASES:
            errors.append(f"{n}.match_phase must be intro / negotiation / sex / end or null")
        ac = b.get("action")
        if ac is not None and not isinstance(ac, str) and not (isinstance(ac, dict) and isinstance(ac.get("kind"), str)):
            errors.append(f"{n}.action must be a string, an object with a kind string, or null")
        if b.get("coordinate") is not None and (not isinstance(b["coordinate"], str) or not 1 <= len(b["coordinate"]) <= 300):
            errors.append(f"{n}.coordinate must be a string of 1-300 characters (a coordinate card's id) or null")
        it = b.get("item")
        if it is not None:
            # (MUSE_GUIDE 3.11, contract 1.33: one operation - take / decline an offer, use one, take a suggestion, give him one)
            ops = [k for k in ("accept", "decline", "use", "suggestion", "give") if k in it] if isinstance(it, dict) else []
            if not isinstance(it, dict) or len(ops) != 1 or not isinstance(it.get(ops[0]), str) or not 1 <= len(it[ops[0]]) <= 100:
                errors.append(f"{n}.item must be an object with one of accept / decline / use / suggestion / give (a string of 1-100 characters)")
            elif not isinstance(it.get("request"), str) or not 1 <= len(it["request"]) <= 200:
                errors.append(f"{n}.item.request must be a string of 1-200 characters (a new one for each operation: a resend is done once)")
            elif it.get("on") not in (None, "self", "user") or it.get("from") not in (None, "own", "shelf"):
                errors.append(f"{n}.item.on must be self / user, item.from own / shelf (or left out)")
        cl = _opt_dict(b.get("clothes"), n + ".clothes", errors)
        if cl is not None:
            for k, v in cl.items():
                if v not in ("on", "half", "off"):
                    errors.append(f"{n}.clothes.{k} must be 'on' / 'half' / 'off'")
        bl = _opt_dict(b.get("belief"), n + ".belief", errors)
        if bl is not None:
            for k, v in bl.items():
                if not isinstance(v, bool):
                    errors.append(f"{n}.belief.{k} must be true / false")
        md = _opt_dict(b.get("mood"), n + ".mood", errors)
        if md is not None:
            for k, v in md.items():
                if v not in MOOD_VALUES:
                    errors.append(f"{n}.mood.{k} must be 'satisfied' / 'relieved' / 'leaked' / 'worked'")
    ch = pkg.get("choices")
    if ch is not None:
        if not isinstance(ch, dict):
            errors.append("choices must be an object or null")
        else:
            if ch.get("kind") not in CHOICE_KINDS:
                errors.append("choices.kind must be stance / state_check / situation")
            opts = ch.get("options")
            if not isinstance(opts, list) or not all(isinstance(o, str) for o in opts):
                errors.append("choices.options must be an array of strings")
    return errors


def validate_choice(c) -> list[str]:
    errors = []
    if not isinstance(c, dict):
        return ["choice must be a JSON object"]
    if not isinstance(c.get("session"), str):
        errors.append("session must be a string")
    if isinstance(c.get("turn"), bool) or not isinstance(c.get("turn"), int):
        errors.append("turn must be an integer")
    if not isinstance(c.get("kind"), str):
        errors.append("kind must be a string")
    if not isinstance(c.get("value"), str):
        errors.append("value must be a string")
    for k, n in MAX_CHOICE_CHARS.items():
        if isinstance(c.get(k), str) and len(c[k]) > n:
            errors.append(f"{k} passes {n} characters")
    return errors


def validate_event(e) -> list[str]:
    if not isinstance(e, dict):
        return ["event must be a JSON object"]
    t = e.get("type")
    if not isinstance(t, str) or not t or len(t) > 64 or not all(c.islower() or c.isdigit() or c == "_" for c in t):
        return ["type must be a short lower_snake_case string"]
    return []


def _now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%S")


def _size(obj) -> int:
    """an object's size as it is saved and answered: its JSON's UTF-8 bytes (the totals of SEC-07 - GPT's review of PR #19:
    characters let Japanese text pass the limits by up to three times)"""
    return len(json.dumps(obj, ensure_ascii=False).encode("utf-8"))


def _trim_events(events: list, sizes: dict) -> int:
    """how many of the oldest events go for the feed to keep within MAX_EVENTS and MAX_EVENTS_BYTES (one event is at most
    MAX_EVENT_BYTES, under the total: the newest always stays)"""
    dropped = max(0, len(events) - MAX_EVENTS)
    total = sum(sizes[e["id"]] for e in events[dropped:])
    while total > MAX_EVENTS_BYTES and dropped < len(events) - 1:
        total -= sizes[events[dropped]["id"]]
        dropped += 1
    return dropped


class TooLarge(ValueError):
    pass


def voice_version(catalog_path: Path, voice: str) -> str:
    # A voice made again (tools/voice_design.py --redo) gets a new "updated": its old lines are not reused from the cache.
    try:
        v = json.loads(catalog_path.read_text(encoding="utf-8")).get("voices", {}).get(voice, {})
        return str(v.get("updated", "")) if isinstance(v, dict) else ""
    except (OSError, ValueError):
        return ""


def voices_view(irodori, catalog_path: Path) -> dict:
    # The creation's voice list: Irodori's registered voices (not the built-in "none"), in voice_catalog.json order first,
    # each with the catalog's name / description (tools/voice_design.py writes it). Irodori away: {"irodori": false}; the
    # voice off: {"irodori": false, "voice": false}.
    try:
        cat = json.loads(catalog_path.read_text(encoding="utf-8")).get("voices", {})
    except (OSError, ValueError):
        cat = {}
    if not irodori.enabled:
        return {"irodori": False, "voice": False, "voices": []}
    ids = irodori.list_voices()
    if ids is None:
        return {"irodori": False, "voices": []}
    ids = [i for i in ids if i != "none"]
    order = [i for i in cat if i in ids] + sorted(i for i in ids if i not in cat)
    out = []
    for i in order:
        c = cat.get(i, {}) if isinstance(cat.get(i), dict) else {}
        out.append({"id": i, "name": c.get("name") or i, "description": c.get("description", ""),
                    "status": c.get("status", "registered")})
    return {"irodori": True, "voices": out}


_PERSONALITIES: dict = {}


def personality_name(no) -> str | None:
    # The Maker's personality list (kk_data cache/port/chara/param/lists.json), for saves made before the name was stored.
    if not _PERSONALITIES:
        try:
            doc = json.loads((Path(__file__).resolve().parent.parent / "kk_data/cache/port/chara/param/lists.json").read_text(encoding="utf-8"))
            for g in doc.get("groups", {}).get("personality", []):
                for a in g.get("assets", {}).values():
                    for p in a.get("param", []):
                        if int(p.get("No", -1)) >= 0:
                            _PERSONALITIES[int(p["No"])] = p.get("Personality")
        except (OSError, ValueError):
            _PERSONALITIES[-999] = None
    return _PERSONALITIES.get(int(no)) if isinstance(no, (int, float)) else None


def list_characters(folder: Path) -> dict:
    # The app's saved characters (godot/app/character_store.gd): profile fields only, newest first; and the active one.
    out = []
    active = None
    if folder.is_dir():
        try:
            active = json.loads((folder / "settings.json").read_text(encoding="utf-8")).get("active") or None
        except (OSError, ValueError):
            pass
        for f in folder.glob("*.json"):
            if f.name == "settings.json":
                continue
            try:
                r = json.loads(f.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            if r.get("kind") != "metamuse-character":
                continue
            out.append({"id": r.get("id"), "name": r.get("name"), "personality": r.get("personality"),
                        "personality_name": r.get("personality_name") or personality_name(r.get("personality")),
                        "attributes": r.get("attributes"), "updated": r.get("updated")})
    out.sort(key=lambda r: str(r.get("updated") or ""), reverse=True)
    return {"characters": out, "active": active}


_CARDS: dict = {}       # path -> (mtime, size, read_card result): a card is read again only when its file changed


def load_personas(path: Path | None) -> dict:
    # MuseStudio's who-she-is table (game/UserData/MuseStudio/personas.json, its menu writes it): the registered personas
    # (slots: no, name - the Muse side's ledger numbers), the persons (no, name, default persona, retired - numbered by
    # MuseStudio) and each card's person and own persona ("card:<file>" -> {"person": no / 0, "persona": {"no", "name"} /
    # null = none; absent = its person's}). The numbers are the keys; the names are the table's now.
    try:
        doc = json.loads(Path(path).read_text(encoding="utf-8")) if path else {}
    except (OSError, ValueError):
        doc = {}
    return doc if isinstance(doc, dict) else {}


def _num(v) -> int:
    return v if isinstance(v, int) and not isinstance(v, bool) else 0


def _person(personas: dict, no: int) -> dict | None:
    for pe in personas.get("persons") or []:
        if isinstance(pe, dict) and _num(pe.get("no")) == no and no > 0:
            return pe
    return None


def person_of(personas: dict, card_id: str):
    # the card's person {"no", "name"}, or None: Muse herself (unassigned)
    c = (personas.get("cards") or {}).get(card_id)
    pe = _person(personas, _num(c.get("person")) if isinstance(c, dict) else 0)
    return {"no": pe["no"], "name": pe.get("name")} if pe else None


def persona_of(personas: dict, card_id: str):
    # the card's own persona (or none), else its person's default; {"no", "name"} or None
    c = (personas.get("cards") or {}).get(card_id)
    c = c if isinstance(c, dict) else {}
    stored = None
    if "persona" in c:
        own = c.get("persona")
        no, stored = (_num(own.get("no")), own.get("name")) if isinstance(own, dict) else (0, None)
    elif _num(c.get("no")):                        # (the first form: the card's persona at its top)
        no, stored = _num(c.get("no")), c.get("name")
    else:
        pe = _person(personas, _num(c.get("person")))
        no = _num(pe.get("persona")) if pe else 0
    if no <= 0:
        return None
    for sl in personas.get("slots") or []:
        if isinstance(sl, dict) and sl.get("no") == no:
            return {"no": no, "name": sl.get("name")}
    return {"no": no, "name": stored}


def list_cards(dirs: list, character: dict | None, persona_file: Path | None = None) -> dict:
    # MuseStudio's characters are the card files (the maker's saves, the project's card folder): read on each request, so
    # the list is right whether MuseStudio runs or not. Female cards only, newest first. id as MuseStudio names the
    # character ("card:<file name>"). active: the card that is the state's character - by the card's unique id when both
    # have one, else by name and updated time (the card file's).
    out, seen = [], set()
    personas = load_personas(persona_file)
    for d in dirs:
        if not Path(d).is_dir():
            continue
        for f in sorted(Path(d).rglob("*.png")):
            try:
                st = f.stat()
                c = _CARDS.get(str(f))
                if c is None or c[0] != st.st_mtime or c[1] != st.st_size:
                    c = (st.st_mtime, st.st_size, kk_card.read_card(f))
                    _CARDS[str(f)] = c
            except (OSError, ValueError, IndexError, KeyError, UnicodeDecodeError):
                continue
            card = c[2]
            if not card or card["sex"] != 1 or str(f.resolve()) in seen:
                continue
            seen.add(str(f.resolve()))
            out.append({"id": "card:" + f.name, "name": card["name"], "personality": card["personality"],
                        "personality_name": personality_name(card["personality"]), "attributes": {"futanari": card["futanari"]},
                        "updated": time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(st.st_mtime)), "uid": card["uid"],
                        "person": person_of(personas, "card:" + f.name), "persona": persona_of(personas, "card:" + f.name), "file": str(f)})
    out.sort(key=lambda r: r["updated"], reverse=True)
    active = None
    ch = character if isinstance(character, dict) else {}
    for r in out:
        if r["uid"] and ch.get("uid"):
            hit = r["uid"] == ch.get("uid")
        else:
            hit = bool(ch.get("name")) and r["name"] == ch.get("name") and r["updated"] == ch.get("updated")
        if hit:
            active = r["id"]
            break
    persons = [{"no": pe.get("no"), "name": pe.get("name"), "persona": _num(pe.get("persona")) or None, "retired": bool(pe.get("retired"))}
               for pe in personas.get("persons") or [] if isinstance(pe, dict)]
    return {"characters": out, "active": active, "persons": persons}


# ---- server state ----------------------------------------------------------------------------------------------------

class State:
    def __init__(self, cfg: dict):
        self.cfg = cfg
        self.packages: collections.deque = collections.deque()
        self.package_sizes: collections.deque = collections.deque()     # (each package's _size, beside it)
        self.packages_bytes = 0
        self.choices: list = []
        self.lock = threading.Lock()
        self.irodori = IrodoriClient(cfg.get("irodori", {}))
        self.tts_locks: dict[str, threading.Lock] = {}
        self.audio_dir: Path = cfg["_audio_dir"]
        self.audio_dir.mkdir(parents=True, exist_ok=True)
        self.default_voice = str(cfg.get("default_voice", ""))
        self.kk_manifest_cache: tuple[float, bytes] | None = None
        # The event feed (app -> Muse): unacknowledged events, kept on disk so a bridge restart loses none.
        self.events_file: Path = cfg["_events_file"]
        self.events: list[dict] = []
        self.next_event_id = 1
        # the app's own ids of its events (header X-MMS-Event-Id) -> the feed's id: a resend after a lost answer is the same event
        self.event_ids: "collections.OrderedDict[str, int]" = collections.OrderedDict()
        self.event_sizes: dict[int, int] = {}       # feed id -> its size as saved (MAX_EVENTS_BYTES)
        self.events_frozen = ""                    # (why the feed's file is not written: an oversized old one not kept aside, one not readable)
        self.events_unread = False                 # (the file there but not readable at the start: read again before each use)
        self.packages_frozen = ""                  # (why the package file is not written: not readable at the start, or garbled
        self.packages_unread = False               #  and not copied aside) / (not readable at the start: read again before each use)
        self.duplicates = 0
        self.faults = {"fail_500": 0, "drop_after_save": 0, "slow_state": 0, "fail_package_save": 0}      # (--test only: POST /api/test/faults)
        self.auth = str(cfg.get("auth", "required")) != "off"
        self.rates = {**RATE_LIMITS, **{k: v for k, v in (cfg.get("rate_limits") or {}).items()
                                        if isinstance(v, list) and len(v) == 2 and all(isinstance(x, (int, float)) and x > 0 for x in v)}}
        self.buckets: dict[str, list] = {}         # sender -> [tokens left, when counted]
        self.tokens_file = cfg.get("_tokens_file") or Path(cfg["_events_file"]).parent / "tokens.json"
        self.tokens = load_tokens(self.tokens_file)
        self.tokens_seen = None                     # (not looked at yet: the first request reads the file again - new tokens made
                                                    # between this read and a look at the file are not missed; GPT's re-review of #35)
        self.tokens_lock = threading.Lock()
        self.log_window: dict[str, list] = {}       # refusal code -> [window start, lines, suppressed]
        self.log_lock = threading.Lock()            # (its own: a refusal may be answered while the state's lock is held)
        self._load_events()
        # The packages (Muse -> app, PR #24): the queue, its delivery records and the launch they are for, kept on disk so a
        # bridge restart loses none (its own file next to the feed's; bridge/cache is this PC's, never the repository)
        self.packages_file: Path = cfg.get("_packages_file") or Path(cfg["_events_file"]).parent / "packages.json"
        self.package_log: "collections.OrderedDict[str, dict]" = collections.OrderedDict()
        self._launch_loaded: dict | None = None
        self.app_state: dict | None = None
        self.app_state_time: str | None = None
        self.catalog: dict | None = None          # the app's play catalog (POST /api/catalog)
        lf = Path(cfg.get("_labels_file") or Path(cfg["_events_file"]).parent / "labels.json")
        # (PR #46 §1; the labels written beforehand, §4.3: the repository's for the original's rooms, this PC's for its mod rooms)
        self.labels = labels.LabelStore(lf, [HERE / "room_labels.json", lf.parent / "room_labels.local.json"])
        self.catalog_time: str | None = None
        self.app_seen = 0.0
        self.instances: dict[str, dict] = {}     # app window id -> {"started": ms, "seen": time}
        # the app's start-up (POST /api/launch): the launch id, its phase and the chosen character; bridge_started until then
        self.launch: dict = {"id": None, "phase": "bridge_started", "started": _now(), "updated": _now(), "character": None}
        self._load_packages()
        # the Muse-to-Muse match (muse_match.py): None without the 'cryptography' package
        self.match = (muse_match.Match(self.add_event, cfg.get("match", {}), conditions=self.match_conditions, clothes=self.app_clothes)
                      if muse_match is not None and muse_match.AVAILABLE else None)

    # ---- the packages' queue on disk (PR #24) ----

    def _load_packages(self) -> None:
        # (GPT's post-review of #26: a file not readable at the start - held by another program - is kept exactly as it is:
        # nothing is taken or written until it reads, and it is read again before each use; a garbled one is copied aside
        # first, else kept and nothing taken. Before, a failed read started empty and the next save wrote over the queue)
        f = self.packages_file
        try:
            text = f.read_text(encoding="utf-8")
        except FileNotFoundError:
            return                                      # (the first start)
        except ValueError as e:                         # (not UTF-8)
            self._packages_garbled(e)
            return
        except OSError as e:
            self.packages_unread = True
            self.packages_frozen = PACKAGES_UNREAD
            sys.stderr.write(f"[bridge] the package queue's file {f} could not be read ({e}): no package is taken until it can be "
                             f"(it is read again before each use; the file is kept as it is)\n")
            return
        try:
            doc = json.loads(text)
            queue = [p for p in doc.get("queue", []) if isinstance(p, dict) and isinstance(p.get("_pid"), str)]
            log = [r for r in doc.get("log", []) if isinstance(r, dict) and isinstance(r.get("id"), str)]
            launch = doc.get("launch")
        except (ValueError, AttributeError, TypeError) as e:
            self._packages_garbled(e)
            return
        self._take_packages(queue, log, launch)

    def _take_packages(self, queue: list, log: list, launch) -> None:
        for pkg in queue:
            self.packages.append(pkg)
            sz = _size(pkg)
            self.package_sizes.append(sz)
            self.packages_bytes += sz
        for r in log:
            self.package_log[r["id"]] = r
        if isinstance(launch, dict) and launch.get("id"):
            # (the same launch goes on after a bridge restart: its packages are delivered, not dropped as old. Its phase waits
            # for the app's next report - the app reports again when /health shows another launch or phase)
            self.launch = {k: launch.get(k) for k in ("id", "phase", "started", "updated", "character")}
            self.launch["phase"] = "bridge_started"
            self.launch["updated"] = _now()
        sys.stderr.write(f"[bridge] package queue restored: {len(self.packages)} waiting, {len(self.package_log)} records\n")

    def _packages_garbled(self, err) -> None:
        # a file that reads but is not the queue's: copied aside as it is, then an empty queue; no copy: kept, nothing taken
        f = self.packages_file
        aside = f.with_name(f.name + ".unreadable-" + time.strftime("%Y%m%d-%H%M%S"))
        try:
            shutil.copyfile(f, aside)
        except OSError as e:
            self.packages_frozen = PACKAGES_GARBLED
            sys.stderr.write(f"[bridge] the package queue's file {f} is not one ({err}) and could not be copied aside ({e}): kept as it is, "
                             f"and no package is taken - move it away, then start the bridge again\n")
            return
        sys.stderr.write(f"[bridge] the package queue's file was not one ({err}): copied aside as {aside.name}, the queue starts empty\n")

    def packages_ready_locked(self) -> str:
        """(with the lock held) "" when the package store may be used; else why not. A file not readable at the start is read
        again here - the queue as it was, from then on"""
        if self.packages_unread:
            try:
                text = self.packages_file.read_text(encoding="utf-8")
                doc = json.loads(text)
                parts = ([p for p in doc.get("queue", []) if isinstance(p, dict) and isinstance(p.get("_pid"), str)],
                         [r for r in doc.get("log", []) if isinstance(r, dict) and isinstance(r.get("id"), str)], doc.get("launch"))
            except FileNotFoundError:
                parts = None                            # (gone meanwhile: nothing left to keep - an empty queue)
            except OSError:
                return self.packages_frozen             # (still not readable)
            except (ValueError, AttributeError, TypeError) as e:
                self.packages_unread, self.packages_frozen = False, ""
                self._packages_garbled(e)
                return self.packages_frozen
            self.packages_unread, self.packages_frozen = False, ""
            if parts is not None:
                self._take_packages(*parts)
            sys.stderr.write("[bridge] the package queue's file reads now\n")
        return self.packages_frozen

    def _save_packages(self) -> None:
        # Called with the lock held; raises when the file cannot be written (the caller then takes nothing) - and while the
        # file could not be read: never written over (GPT's post-review of #26)
        if self.packages_frozen:
            raise OSError(self.packages_frozen)
        if self.faults.get("fail_package_save", 0) > 0:
            self.faults["fail_package_save"] -= 1
            raise OSError("(test) the package queue's file not written")
        self._prune_log()
        self.packages_file.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.packages_file.with_suffix(".part")
        doc = {"version": 1, "launch": self.launch, "queue": list(self.packages), "log": list(self.package_log.values())}
        tmp.write_text(json.dumps(doc, ensure_ascii=False), encoding="utf-8")
        os.replace(tmp, self.packages_file)

    def _prune_log(self) -> None:
        waiting = {p.get("_pid") for p in self.packages}
        cutoff = time.time() - PACKAGE_LOG_DAYS * 86400
        for k in [k for k, r in self.package_log.items() if k not in waiting and r.get("updated_ts", 0) < cutoff]:
            del self.package_log[k]
        while len(self.package_log) > MAX_PACKAGE_LOG:
            old = next((k for k in self.package_log if k not in waiting), None)
            if old is None:
                break
            del self.package_log[old]

    def _mark(self, pid: str, stage: str, reason: str | None = None) -> dict | None:
        # a package's record to a later stage (never back; dropped is the end)
        r = self.package_log.get(pid)
        if r is None:
            return None
        cur = r.get("stage", "queued")
        if cur == "dropped" or (stage != "dropped" and PACKAGE_STAGES.index(stage) <= PACKAGE_STAGES.index(cur)):
            return r
        r["stage"] = stage
        r[stage] = _now()
        r["updated_ts"] = time.time()
        if reason:
            r["reason"] = reason
        return r

    def _take_out(self, pid: str) -> bool:
        # the package's body out of the queue (its record stays)
        for i, p in enumerate(self.packages):
            if p.get("_pid") == pid:
                del self.packages[i]
                self.packages_bytes -= self.package_sizes[i]
                del self.package_sizes[i]
                return True
        return False

    def app_clothes(self) -> dict | None:
        # this side's character's clothes as the app last told them (its state's clothes), for the match's packages
        with self.lock:
            c = (self.app_state or {}).get("clothes")
        return dict(c) if isinstance(c, dict) else None

    def match_conditions(self, rid: str) -> dict | None:
        # the conditions this side's app applied to the match in room rid (its state's match.limits), for the peer's bridge
        with self.lock:
            m = (self.app_state or {}).get("match")
        if not isinstance(m, dict) or m.get("room") != rid or not isinstance(m.get("limits"), dict):
            return None
        return dict(m["limits"])

    def _tokens_stamp(self):
        try:
            st = self.tokens_file.stat()
            return (st.st_mtime_ns, st.st_size)
        except OSError:
            return None

    def tokens_now(self) -> dict:
        """the tokens as their file says: read again when it changes (--print-token made new ones after the file was
        deleted: the shown token is the one in use - GPT's re-review of #27); the file gone, written again with the ones in
        use (they stay the truth until a new file comes)"""
        # (the stamp kept is the one taken before the read that was used: a file replaced after it differs next time and is
        # read then - GPT's re-review of #34: a second stat after the read recorded a new file whose tokens were never read)
        with self.tokens_lock:
            stamp = self._tokens_stamp()
            if stamp is None:
                save_tokens(self.tokens_file, self.tokens)
                self.tokens_seen = None             # (read back next time: whoever wrote last, the file is the truth)
                sys.stderr.write(f"[bridge] the tokens' file was gone: written again with the tokens in use ({self.tokens_file})\n")
            elif stamp != self.tokens_seen:
                self.tokens = load_tokens(self.tokens_file)
                self.tokens_seen = stamp
                sys.stderr.write("[bridge] the tokens' file changed: its tokens are the ones in use now\n")
            return self.tokens

    def log_refusal(self, code: str, line: str) -> None:
        """a refusal written to the log: at most 20 lines a code in 10 s, the rest counted (a flood does not fill it)"""
        now = time.monotonic()
        with self.log_lock:
            w = self.log_window.get(code)
            if w is None or now - w[0] >= 10.0:
                if w is not None and w[2] > 0:
                    sys.stderr.write(f"[bridge] ({w[2]} more {code} refusals not written in 10 s)\n")
                w = [now, 0, 0]
                self.log_window[code] = w
            if w[1] < 20:
                w[1] += 1
                sys.stderr.write(line)
            else:
                w[2] += 1

    def take(self, sender: str) -> float:
        """one request of this sender: 0 when it may go now, else the seconds until it may (a token bucket per sender)"""
        rate, burst = self.rates.get(sender, self.rates["all"])
        now = time.monotonic()
        with self.lock:
            left, at = self.buckets.get(sender, [float(burst), now])
            left = min(float(burst), left + (now - at) * rate)
            if left >= 1.0:
                self.buckets[sender] = [left - 1.0, now]
                return 0.0
            self.buckets[sender] = [left, now]
            return (1.0 - left) / rate

    def note_instance(self, iid: str, started: int) -> bool:
        # True when this window is the newest one seen in the last APP_SEEN_SECONDS (the one the bridge serves).
        with self.lock:
            now = time.time()
            self.instances[iid] = {"started": started, "seen": now}
            self.instances = {k: v for k, v in self.instances.items() if now - v["seen"] < APP_SEEN_SECONDS}
            newest = max(self.instances.items(), key=lambda kv: (kv[1]["started"], kv[0]))[0]
            return newest == iid

    # ---- event feed ----
    def _load_events(self) -> None:
        # a file past MAX_EVENTS_FILE_BYTES (only one saved before the limits can be) is not read into memory: kept aside as it
        # is (its events not lost - a person can look), the feed starting afresh with ids after any it held (time in ms: Muse's
        # `after` cursor still sees the new ones) - GPT's re-review of #26
        try:
            size = self.events_file.stat().st_size
        except OSError:
            size = 0
        if size > MAX_EVENTS_FILE_BYTES:
            # moved aside, else copied aside; neither: the file stays exactly as it is and the feed takes no events (503) until a
            # person moves it and restarts the bridge - never written over without a copy (GPT's re-review of #32: a failed move
            # left an empty feed whose first event replaced the old file)
            aside = self.events_file.with_name(self.events_file.name + ".oversize-" + time.strftime("%Y%m%d-%H%M%S"))
            kept = ""
            try:
                os.replace(self.events_file, aside)
                kept = "moved"
            except OSError:
                try:
                    shutil.copyfile(self.events_file, aside)
                    kept = "copied"
                except OSError as e:
                    self.events_frozen = EVENTS_OVERSIZE
                    sys.stderr.write(f"[bridge] the event feed's file {self.events_file} ({size} bytes, past {MAX_EVENTS_FILE_BYTES}) could not be "
                                     f"kept aside ({e}): left as it is, and no event is taken - move it away, then start the bridge again\n")
                    return
            self.next_event_id = int(time.time() * 1000)
            sys.stderr.write(f"[bridge] the event feed's file was {size} bytes (past {MAX_EVENTS_FILE_BYTES}): {kept} aside as {aside.name}, "
                             f"the feed starts afresh from id {self.next_event_id}\n")
            try:
                self._save_events()
            except OSError as e:
                sys.stderr.write(f"[bridge] the new event feed not saved yet: {e}\n")
            return
        # a normal file: missing (a first start - an empty feed), not readable now (held by another program: no event is taken -
        # 503 - and it is read again before each use; GPT's re-review of #34: the first event replaced it), garbled (copied aside,
        # then afresh; no copy: kept as it is, no event taken)
        try:
            doc = json.loads(self.events_file.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return
        except ValueError as e:
            self._garbled(e)
            return
        except OSError as e:
            self.events_unread = True
            self.events_frozen = EVENTS_UNREAD
            sys.stderr.write(f"[bridge] the event feed's file {self.events_file} could not be read ({e}): no event is taken until it can be "
                             f"(it is read again before each use)\n")
            return
        self._take_feed(doc)

    def _garbled(self, err) -> None:
        # a feed file that reads but is not one: copied aside as it is, the feed afresh (ids after any it held); no copy: no event
        aside = self.events_file.with_name(self.events_file.name + ".unreadable-" + time.strftime("%Y%m%d-%H%M%S"))
        try:
            shutil.copyfile(self.events_file, aside)
        except OSError as e:
            self.events_frozen = EVENTS_GARBLED
            sys.stderr.write(f"[bridge] the event feed's file {self.events_file} is not a feed ({err}) and could not be copied aside ({e}): left "
                             f"as it is, and no event is taken - move it away, then start the bridge again\n")
            return
        self.events, self.event_sizes = [], {}
        self.next_event_id = int(time.time() * 1000)
        sys.stderr.write(f"[bridge] the event feed's file was not a feed ({err}): copied aside as {aside.name}, the feed starts afresh from id "
                         f"{self.next_event_id}\n")

    def _thaw_locked(self) -> None:
        # (with the lock held) a feed file not readable at the start: read again now - the feed as it was, writing again
        if not self.events_unread:
            return
        try:
            doc = json.loads(self.events_file.read_text(encoding="utf-8"))
        except FileNotFoundError:
            doc = None                              # (gone meanwhile: nothing left to keep - an empty feed)
        except ValueError as e:
            self.events_unread, self.events_frozen = False, ""
            self._garbled(e)
            return
        except OSError:
            return                                  # (still not readable: still no event)
        self.events_unread, self.events_frozen = False, ""
        if doc is not None:
            self._take_feed(doc)
        sys.stderr.write(f"[bridge] the event feed's file reads now: {len(self.events)} event(s), next id {self.next_event_id}\n")

    def _take_feed(self, doc) -> None:
        try:
            self.events = [e for e in doc.get("events", []) if isinstance(e, dict) and isinstance(e.get("id"), int)]
            self.next_event_id = max([int(doc.get("next_id", 1))] + [e["id"] + 1 for e in self.events])
            # the app's ids, acknowledged events' too (kept apart from the feed: a resend after an ack and a restart is still
            # the same event - GPT's review of PR #14)
            for e in self.events:                         # (the first version kept them in the events)
                if isinstance(e.get("_cid"), str):
                    self.event_ids[e.pop("_cid")] = e["id"]
            for pair in doc.get("event_ids", []):
                if isinstance(pair, list) and len(pair) == 2 and isinstance(pair[0], str) and isinstance(pair[1], int):
                    self.event_ids[pair[0]] = pair[1]
            while len(self.event_ids) > MAX_EVENT_IDS:
                self.event_ids.popitem(last=False)
            self.event_sizes = {e["id"]: _size(e) for e in self.events}
        except (AttributeError, TypeError, ValueError) as e:     # (the JSON not a feed's shape: as a garbled file)
            self.events, self.event_sizes, self.event_ids = [], {}, collections.OrderedDict()
            self._garbled(e)
            return
        # the limits from the start too (a file saved before them, or with larger ones - GPT's review of PR #19): one event too
        # big goes, then the oldest past the totals; the next id and the app's ids stay as they are
        big = [e["id"] for e in self.events if self.event_sizes[e["id"]] > MAX_EVENT_BYTES]
        keep = [e for e in self.events if self.event_sizes[e["id"]] <= MAX_EVENT_BYTES]
        dropped = _trim_events(keep, self.event_sizes)
        if big or dropped:
            self.events = keep[dropped:]
            self.event_sizes = {e["id"]: self.event_sizes[e["id"]] for e in self.events}
            sys.stderr.write(f"[bridge] event feed at start: dropped {len(big)} too big and {dropped} past the limits\n")
            try:
                self._save_events()
            except OSError as e:
                sys.stderr.write(f"[bridge] event feed not saved: {e}\n")

    def _save_events(self, events: list | None = None, next_id: int | None = None, ids=None) -> None:
        # Called with the lock held. The given feed (default: the one shown), its next id and the app's ids; raises when the
        # file cannot be written (the caller then shows nothing new).
        if self.events_frozen:
            raise OSError(self.events_frozen)
        self.events_file.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.events_file.with_suffix(".part")
        doc = {"next_id": self.next_event_id if next_id is None else next_id, "events": self.events if events is None else events,
               "event_ids": [[k, v] for k, v in (self.event_ids if ids is None else ids).items()]}
        tmp.write_text(json.dumps(doc, ensure_ascii=False), encoding="utf-8")
        os.replace(tmp, self.events_file)

    def add_event(self, ev: dict) -> int:
        # the bridge's own events (a choice, the match's): one too big is told as such instead of being lost unsaid
        with self.lock:
            self._thaw_locked()
            try:
                try:
                    return self._add_event_locked(ev)
                except TooLarge as e:
                    sys.stderr.write(f"[bridge] {e}\n")
                    return self._add_event_locked({"type": "event_too_large", "of": str(ev.get("type", ""))[:64], "bytes": _size(ev)})
            except OSError as e:                    # (the feed's file not writable: this event is not shown - logged)
                sys.stderr.write(f"[bridge] event {ev.get('type')} not saved: {e}\n")
                return 0

    def _add_event_locked(self, ev: dict, cid: str | None = None) -> int:
        # Called with the lock held. Saved first, then shown: a failed save leaves the feed, its next id and the app's ids as
        # they were (GPT's review of PR #14 - the event shown but its app id not, then added again on the resend).
        ev = {k: v for k, v in ev.items() if k not in ("id", "time")}
        ev = {"id": self.next_event_id, "time": _now(), **ev}
        size = _size(ev)
        if size > MAX_EVENT_BYTES:
            raise TooLarge(f"an event of {size} bytes passes {MAX_EVENT_BYTES}")
        events = self.events + [ev]
        sizes = dict(self.event_sizes)
        sizes[ev["id"]] = size
        dropped = _trim_events(events, sizes)
        if dropped > 0:
            for e in events[:dropped]:
                sizes.pop(e["id"], None)
            events = events[dropped:]
        ids = self.event_ids
        if cid:
            ids = collections.OrderedDict(self.event_ids)
            ids[cid] = ev["id"]
            while len(ids) > MAX_EVENT_IDS:
                ids.popitem(last=False)
        self._save_events(events, self.next_event_id + 1, ids)
        self.events, self.next_event_id, self.event_ids, self.event_sizes = events, self.next_event_id + 1, ids, sizes
        if dropped > 0:
            sys.stderr.write(f"[bridge] event feed full: dropped the {dropped} oldest unacknowledged event(s)\n")
        return ev["id"]

    def add_app_event(self, ev: dict, cid: str | None) -> tuple[int, bool]:
        """An app event with the app's own id: one already taken (its answer lost, so sent again) is not added twice -
        (the feed's id, whether it was a resend). The review of 2026-11-05, point 3."""
        with self.lock:
            self._thaw_locked()
            if cid and cid in self.event_ids:
                self.duplicates += 1
                return self.event_ids[cid], True
            return self._add_event_locked(ev, cid), False

    def peek_events(self, after: int, limit: int) -> list[dict]:
        with self.lock:
            self._thaw_locked()
            # (the app's id is the bridge's own bookkeeping: not in the feed)
            return [{k: v for k, v in e.items() if k != "_cid"} for e in self.events if e["id"] > after][:limit]

    def ack_events(self, ids: set[int], upto: int | None) -> int:
        with self.lock:
            self._thaw_locked()
            if self.events_frozen:
                raise OSError(self.events_frozen)
            keep = [e for e in self.events if not (e["id"] in ids or (upto is not None and e["id"] <= upto))]
            n = len(self.events) - len(keep)
            if n:
                self._save_events(keep)
                self.events = keep
                self.event_sizes = {e["id"]: self.event_sizes.get(e["id"], 0) for e in keep}
            return n

    def state_view(self) -> dict:
        with self.lock:
            out: dict = {k: None for k in STATE_FIELDS}
            out.update(self.app_state or {})
            now = time.time()
            out["app"] = {"connected": now - self.app_seen < APP_SEEN_SECONDS,
                          "windows": sum(1 for v in self.instances.values() if now - v["seen"] < APP_SEEN_SECONDS),
                          "last_seen": time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(self.app_seen)) if self.app_seen else None,
                          "reported": self.app_state_time}
            out["queue"] = len(self.packages)
            out["events_pending"] = len(self.events)
            return out

    def set_launch(self, lid: str, phase: str, character) -> list[tuple[dict, str]] | None:
        # A new launch id: the packages waiting now belong to no live launch (an older one's, or sent before it) - dropped
        # (before_launch); the ones the old app had taken but never began (delivered / received) went with it (app_restarted).
        # The same id again (a phase, the character chosen, a bridge restarted under a running app): nothing dropped.
        # None: the package file cannot be used yet - nothing changed (the app reports again)
        with self.lock:
            if self.packages_ready_locked():
                return None
            dropped: list[tuple[dict, str]] = []
            if lid != self.launch.get("id"):
                for pkg in self.packages:
                    r = self.package_log.get(pkg.get("_pid"))
                    gone = "app_restarted" if r is not None and r.get("stage") == "delivered" else "before_launch"
                    self._mark(pkg.get("_pid"), "dropped", gone)
                    dropped.append((pkg, gone))
                for r in self.package_log.values():
                    if r.get("stage") == "received":
                        self._mark(r["id"], "dropped", "app_restarted")
                        dropped.append(({k: r.get(k) for k in ("session", "turn", "beat", "launch", "character")}, "app_restarted"))
                    elif r.get("stage") == "started" and r.get("launch") == self.launch.get("id") and "blocks" in r:
                        # (#70 adopted, 1) begun and not finished: the old app went in the middle of it (the old launch's own;
                        # one with no block count was begun by an app before this - not known to have been cut, left as it is)
                        self._mark(r["id"], "interrupted")
                        dropped.append(({k: r.get(k) for k in ("id", "session", "turn", "beat", "launch", "character", "last_block", "blocks")},
                                        "interrupted"))
                self.packages.clear()
                self.package_sizes.clear()              # (and their total: else the empty queue still looked full - GPT's re-review of #26)
                self.packages_bytes = 0
                self.launch = {"id": lid, "phase": phase, "started": _now(), "updated": _now(), "character": None}
            self.launch["phase"] = phase
            self.launch["updated"] = _now()
            if isinstance(character, dict):
                self.launch["character"] = {k: character.get(k) for k in ("id", "name", "persona")}
            try:
                self._save_packages()
            except OSError as e:
                sys.stderr.write(f"[bridge] package queue not saved at the launch: {e}\n")
            return dropped

    def character_now(self) -> str | None:
        # the app's character (its launch report, else its state): packages tagged for another are dropped
        with self.lock:
            return self._character_now_locked()

    def _character_now_locked(self) -> str | None:
        ch = self.launch.get("character") or (self.app_state or {}).get("character") or {}
        return ch.get("id") if isinstance(ch, dict) else None

    def drop_reason(self, pkg: dict) -> str:
        # (an untagged package: for the launch and the character of when the bridge took it - PR #24, GPT's point 3)
        lid = pkg.get("launch") if pkg.get("launch") is not None else pkg.get("_launch_at")
        if lid is not None and lid != self.launch.get("id"):
            return "old_launch"
        cid = pkg.get("character") if pkg.get("character") is not None else pkg.get("_character_at")
        now = self.character_now()
        if cid is not None and now is not None and cid != now:
            return "other_character"
        return ""

    def note_interrupted(self, rec: dict) -> None:
        # (#70 adopted, 1) a package begun and not finished when the app went: the blocks up to last_block were shown (their
        # operations done); the one after it is not known either way
        sys.stderr.write(f"[bridge] package interrupted: session={rec.get('session')} turn={rec.get('turn')} last_block={rec.get('last_block')}\n")
        self.add_event({"type": "package_interrupted", "package": rec.get("id"), "session": rec.get("session"), "turn": rec.get("turn"),
                        "beat": rec.get("beat"), "launch": rec.get("launch"), "character": rec.get("character"),
                        "last_block": rec.get("last_block"), "blocks": rec.get("blocks"), "next_block": "unknown"})

    def note_dropped(self, pkg: dict, reason: str) -> None:
        sys.stderr.write(f"[bridge] package dropped ({reason}): session={pkg.get('session')} turn={pkg.get('turn')}\n")
        self.add_event({"type": "package_dropped", "session": pkg.get("session"), "turn": pkg.get("turn"), "beat": pkg.get("beat"),
                        "reason": reason, "launch": pkg.get("launch"), "character": pkg.get("character"),
                        "choices": isinstance(pkg.get("choices"), dict), "by": "bridge"})

    def tts_lock(self, key: str) -> threading.Lock:
        with self.lock:
            return self.tts_locks.setdefault(key, threading.Lock())

    def kk_manifest(self) -> bytes:
        # The web build's data list: tools/kk_import.py's copy manifest (files present under kk_data), limited to the web
        # subset (tools/kk_web_list.txt: the start character, the locations, the motions) - the native app reads all of kk_data.
        root: Path = self.cfg["_kkdata_root"]
        src = root / "KK_COPY_MANIFEST.json"
        lst: Path = self.cfg["_web_data_list"]
        mtime = (src.stat().st_mtime if src.exists() else 0.0) + (lst.stat().st_mtime if lst.exists() else 0.0)
        if self.kk_manifest_cache and self.kk_manifest_cache[0] == mtime:
            return self.kk_manifest_cache[1]
        wanted = None
        if lst.exists():
            wanted = {l.strip() for l in lst.read_text(encoding="utf-8").splitlines() if l.strip() and not l.startswith("#")}
        files = []
        if src.exists():
            doc = json.loads(src.read_text(encoding="utf-8"))
            for rel, spec in sorted(doc.get("files", {}).items()):
                if (wanted is None or rel in wanted) and (root / rel).is_file():
                    files.append({"path": rel, "bytes": spec["bytes"], "sha256": spec["sha256"]})
        body = json.dumps({"kind": "kkdata-manifest", "files": files}, ensure_ascii=False).encode("utf-8")
        self.kk_manifest_cache = (mtime, body)
        return body


def make_handler(state: State):
    cfg = state.cfg
    quiet_paths = {"/api/packages/next", "/health"}
    quiet_gets = {"/api/events", "/api/state"}       # Muse polls these

    class Handler(BaseHTTPRequestHandler):
        server_version = "MetaMuseBridge/1.0"
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):
            path = self.path.split("?", 1)[0]
            refused = len(args) >= 2 and str(args[1])[:1] in ("4", "5")      # (a refusal is told, on the quiet paths too)
            if not refused and (path in quiet_paths or path.startswith("/kkdata/") or (self.command == "GET" and path in quiet_gets)):
                return
            line = "[bridge] %s %s\n" % (time.strftime("%H:%M:%S"), fmt % args)
            if refused:
                state.log_refusal(str(args[1]), line)      # (a flood of them does not fill the log)
            else:
                sys.stderr.write(line)

        # ---- responses ----
        def _send(self, code: int, body: bytes = b"", ctype: str = "application/json", extra: dict | None = None):
            self.send_response(code)
            if body or code not in (204, 304):
                self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store" if ctype.startswith("application/json") else "no-cache")
            allow = self._cors_origin()
            if allow:
                self.send_header("Access-Control-Allow-Origin", allow)
                self.send_header("Access-Control-Allow-Headers", "Content-Type")
                self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
                if allow != "*":
                    self.send_header("Vary", "Origin")
            for k, v in (extra or {}).items():
                self.send_header(k, v)
            self.end_headers()
            if body and self.command != "HEAD":
                self.wfile.write(body)

        def _json(self, code: int, obj):
            self._send(code, json.dumps(obj, ensure_ascii=False).encode("utf-8"))

        # ---- web pages in the user's browser (the review of 2026-11-05, point 2: the bridge has no authentication) ----
        # Muse (its VM's HTTP), the app and the tools send no browser headers and are served as ever. A browser marks its
        # requests (Origin, Sec-Fetch-Site): those are served only from the bridge's own pages - on this PC's own names
        # (127.0.0.1 / localhost / [::1] at the bridge's port; not a foreign name pointed here, DNS rebinding) - and from the
        # origins bridge_config.json "cors" lists. Turning CORS off alone would not do: a page elsewhere can still send a plain
        # POST (a package) without reading the answer. The reach itself (the tailnet, its ACL) is the other half, outside here.
        def _own_origins(self) -> set:
            port = self.server.server_address[1]
            own = {f"http://127.0.0.1:{port}", f"http://localhost:{port}", f"http://[::1]:{port}"}
            listed = cfg.get("cors", False)
            if isinstance(listed, list):
                own.update(str(o).rstrip("/").lower() for o in listed)
            return own

        def _cors_origin(self) -> str:
            listed = cfg.get("cors", False)
            if listed is True:
                return "*"
            origin = (self.headers.get("Origin") or "").rstrip("/").lower()
            return origin if isinstance(listed, list) and origin and origin in self._own_origins() else ""

        def _browser_refused(self) -> bool:
            site, origin = self.headers.get("Sec-Fetch-Site"), self.headers.get("Origin")
            if site is None and origin is None:
                return False                                   # (not a browser: Muse, the app, the tools)
            if cfg.get("cors", False) is True:
                return False                                   # (every site allowed: the old behaviour, asked for)
            own = self._own_origins()
            if origin is not None and origin.rstrip("/").lower() not in own:
                return True
            if site is not None and site not in ("same-origin", "none"):
                return True
            return "http://" + (self.headers.get("Host") or "").lower() not in own and origin is None

        def _refuse_browser(self) -> bool:
            # (first the Host, every request - SEC-06; then a web page of another site)
            if not host_allowed(self.headers.get_all("Host"), cfg):
                if self._refuse_rate("anon"):           # (refusals are counted too - GPT's re-review of #28)
                    return True
                state.log_refusal("host", f"[bridge] refused: Host {self.headers.get_all('Host')!r} is not this PC's (bridge_config.json allowed_hosts)\n")
                self._json(403, {"error": "this Host is not one of the bridge's names (bridge_config.json allowed_hosts lists the others)"})
                return True
            if not self._browser_refused():
                return False
            if self._refuse_rate("anon"):
                return True
            self._json(403, {"error": "a web page of another site may not use the bridge (bridge_config.json cors lists the allowed origins)"})
            return True

        def _role(self) -> str | None:
            # the role whose token this request carries (None: none, or not one of the two)
            auth = self.headers.get("Authorization") or ""
            if not auth.startswith("Bearer "):
                return None
            given = auth[len("Bearer "):].strip().encode("utf-8")
            for r, tok in state.tokens_now().items():
                if hmac.compare_digest(given, tok.encode("utf-8")):
                    return r
            return None

        def _refuse_unauthorized(self, method: str, path: str) -> bool:
            # SEC-01: no token, or not one of the two -> 401; a token of the other role -> 403. Off: everyone, as before.
            # Then the sender's rate (SEC-07): past it 429.
            if not state.auth:
                return self._refuse_rate("all")
            role = self._role()
            if role is None:
                if self._refuse_rate("anon"):
                    return True
                self._send(401, json.dumps({"error": "the bridge needs its token (Authorization: Bearer ...; MUSE_GUIDE 7.1)"}).encode("utf-8"),
                           extra={"WWW-Authenticate": "Bearer"})
                return True
            if role not in roles_for(method, path):
                if self._refuse_rate(role):              # (the other role's way: counted as its own - GPT's re-review of #28)
                    return True
                self._json(403, {"error": f"the {role} token may not use {method} {path}"})
                return True
            return self._refuse_rate(role)

        def _refuse_rate(self, sender: str) -> bool:
            wait = state.take(sender)
            if wait <= 0.0:
                return False
            secs = max(1, int(wait + 0.999))
            self._send(429, json.dumps({"error": f"too many requests from {sender}: again in {secs} s", "retry_after": secs}).encode("utf-8"),
                       extra={"Retry-After": str(secs)})
            return True

        def _active_window(self) -> bool:
            # Requests without the window headers (tools, Muse) are not app windows: always served. The headers count only
            # with the app's token (SEC-01: a window is the app's own)
            iid = self.headers.get("X-MMS-Instance")
            if not iid or (state.auth and self._role() != "app"):
                return True
            try:
                started = int(self.headers.get("X-MMS-Started") or 0)
            except ValueError:
                started = 0
            return state.note_instance(iid, started)

        def _body_json(self):
            n = int(self.headers.get("Content-Length") or 0)
            if n > MAX_BODY:
                raise TooLarge(f"the body passes {MAX_BODY} bytes")
            if n <= 0:
                raise ValueError("missing body")
            return json.loads(self.rfile.read(n).decode("utf-8"))

        def _file(self, path: Path, root: Path):
            try:
                path = path.resolve()
                path.relative_to(root.resolve())
            except (ValueError, OSError):
                return self._json(404, {"error": "not found"})
            if not path.is_file():
                return self._json(404, {"error": "not found"})
            ctype = MIME.get(path.suffix.lower()) or mimetypes.guess_type(path.name)[0] or "application/octet-stream"
            data = path.read_bytes()
            self._send(200, data, ctype)

        # ---- verbs ----
        def do_OPTIONS(self):
            if self._refuse_browser():
                return
            if self._refuse_rate((self._role() if state.auth else "all") or "anon"):     # (GPT's re-review of #28)
                return
            self._send(204)

        def do_HEAD(self):
            # (not on a way that takes from the queue: its package would go with no body to read - GPT's re-review of #27)
            # (the path as GET reads it - %6Eext is next; the old choice queue is emptied by its GET too)
            if urllib.parse.unquote(self.path.split("?", 1)[0]) in ("/api/packages/next", "/api/choices"):
                if self._refuse_browser():
                    return
                self.close_connection = True
                return self._send(405, b"", extra={"Allow": "GET"})
            self.do_GET()

        # ---- the match (muse_match.py) ----
        def _match(self, method: str, path: str, body):
            m = state.match
            if m is None:
                return self._json(503, {"available": False, "error": "the match needs the Python package 'cryptography' (pip install cryptography)"})
            parts = path.strip("/").split("/")          # api, match, rooms, <id>, <op>
            try:
                if method == "GET" and parts == ["api", "match"]:
                    return self._json(200, m.view())
                if method == "GET" and len(parts) == 4 and parts[2] == "rooms":
                    return self._json(200, m.view(parts[3]))
                if method == "POST" and parts == ["api", "match", "rooms"]:
                    return self._json(200, m.create(body))
                if method == "POST" and len(parts) == 5 and parts[2] == "rooms":
                    rid, op = parts[3], parts[4]
                    if op == "peer":
                        return self._json(200, m.set_peer(rid, body))
                    if op == "ping":
                        return self._json(200, m.ping(rid))
                    if op == "send":
                        return self._json(200, m.send(rid, body))
                    if op == "close":
                        return self._json(200, m.close(rid, str(body.get("reason") or "")))
            except KeyError:
                return self._json(404, {"error": "no such room"})
            except ValueError as e:
                return self._json(400, {"error": str(e)})
            except muse_match.NotAllowed as e:           # (a relay this bridge's settings do not list - SEC-05)
                return self._json(403, {"error": str(e), "reason": "relay_not_allowed"})
            except OSError as e:                         # the relay unreachable
                return self._json(502, {"error": f"relay: {e}"})
            return self._json(404, {"error": "not found"})

        def do_GET(self):
            if self._refuse_browser():
                return
            path = urllib.parse.unquote(self.path.split("?", 1)[0])
            query = urllib.parse.parse_qs(self.path.split("?", 1)[1]) if "?" in self.path else {}
            if path == "/health" and state.auth and self._role() is None:
                if self._refuse_rate("anon"):
                    return
                return self._json(200, {"ok": True, "auth": "required"})      # (alive - the rest needs a token)
            if self._refuse_unauthorized("GET", path):
                return
            if path == "/api/match" or path.startswith("/api/match/"):
                return self._match("GET", path, None)
            if path == "/health":
                with state.lock:
                    launch = dict(state.launch)
                    frozen = state.packages_ready_locked()
                return self._json(200, {"ok": True, "irodori": state.irodori.health(), "voice": state.irodori.enabled,
                                        "queue": len(state.packages), "packages_unavailable": frozen or None,
                                        "events": len(state.events), "app": time.time() - state.app_seen < APP_SEEN_SECONDS,
                                        "launch": launch})
            if path == "/api/packages/next":
                if not self._active_window():
                    return self._json(409, {"error": "another app window is active"})
                hold = query.get("hold", ["0"])[0] not in ("0", "")
                acked = query.get("ack", ["0"])[0] not in ("0", "")
                while True:
                    with state.lock:
                        state.app_seen = time.time()
                        if state.packages_ready_locked():
                            return self._send(204)          # (the package file not readable yet: nothing to give)
                        pkg = state.packages[0] if state.packages and not hold else None
                        if pkg is not None and not acked:
                            # (a caller without acks: taken out as before - received when taken)
                            state.packages.popleft()
                            state.packages_bytes -= state.package_sizes.popleft()
                    if pkg is None:
                        return self._send(204)
                    reason = state.drop_reason(pkg)
                    with state.lock:
                        pid = pkg.get("_pid")
                        if reason:
                            if acked:
                                state._take_out(pid)
                            state._mark(pid, "dropped", reason)
                        elif acked:
                            r = state._mark(pid, "delivered")
                            if r is not None:
                                r["deliveries"] = r.get("deliveries", 0) + 1
                        else:
                            state._mark(pid, "received")
                        if reason or not acked:
                            try:
                                state._save_packages()
                            except OSError as e:
                                sys.stderr.write(f"[bridge] package queue not saved: {e}\n")
                    if not reason:
                        return self._json(200, pkg)
                    state.note_dropped(pkg, reason)
            if path == "/api/packages/status":
                pid = query.get("id", [None])[0]
                ses, trn = query.get("session", [None])[0], query.get("turn", [None])[0]
                with state.lock:
                    frozen = state.packages_ready_locked()
                    recs = list(state.package_log.values())
                if frozen:
                    return self._json(503, {"error": frozen})
                shown = lambda r: {k: v for k, v in r.items() if k not in ("updated_ts", "queued_ts")}
                if pid is not None:
                    r = next((r for r in recs if r["id"] == pid), None)
                    return self._json(200, shown(r)) if r else self._json(404, {"error": "no package with this id (never taken, or its record is gone)"})
                if ses is not None and trn is not None:
                    hits = [shown(r) for r in recs if r.get("session") == ses and str(r.get("turn")) == trn]
                    return self._json(200, {"packages": hits}) if hits else self._json(404, {"error": "no package with this session and turn"})
                return self._json(200, {"packages": [shown(r) for r in recs[-50:]]})
            if path == "/api/packages":
                with state.lock:
                    return self._json(200, {"queue": len(state.packages)})
            if path == "/api/choices":
                with state.lock:
                    out, state.choices = state.choices, []
                return self._json(200, {"choices": out})
            if path == "/api/events":
                try:
                    after = int(query.get("after", ["0"])[0])
                    limit = max(1, min(500, int(query.get("limit", ["100"])[0])))
                except ValueError:
                    return self._json(400, {"error": "after and limit must be integers"})
                events = state.peek_events(after, limit)
                with state.lock:
                    return self._json(200, {"events": events, "pending": len(state.events), "next_id": state.next_event_id})
            if path == "/api/state":
                return self._json(200, state.state_view())
            if path == "/api/characters":
                if cfg.get("characters_source", "cards") == "cards":
                    with state.lock:
                        character = (state.app_state or {}).get("character")
                    return self._json(200, list_cards(cfg["_card_dirs"], character, cfg["_persona_file"]))
                return self._json(200, list_characters(cfg["_characters_dir"]))
            if path == "/api/catalog":
                with state.lock:
                    return self._json(200, {"catalog": state.catalog, "reported": state.catalog_time})
            if path in ("/api/labels/coordinates", "/api/labels/rooms"):
                kind = path.rsplit("/", 1)[1]
                with state.lock:
                    why = state.labels.ready()
                    if why:
                        return self._json(503, {"error": why})
                    return self._json(200, state.labels.view(kind, state.catalog))
            if path == "/api/voices":
                return self._json(200, voices_view(state.irodori, cfg["_voice_catalog"]))
            if path.startswith("/audio/"):
                return self._file(state.audio_dir / path[len("/audio/"):], state.audio_dir)
            if path == "/kkdata/manifest.json":
                return self._send(200, state.kk_manifest())
            if path.startswith("/kkdata/"):
                return self._file(cfg["_kkdata_root"] / path[len("/kkdata/"):], cfg["_kkdata_root"])
            if path.startswith("/static/"):
                return self._file(cfg["_static_root"] / path[len("/static/"):], cfg["_static_root"])
            web: Path = cfg["_web_root"]
            rel = path.lstrip("/") or "index.html"
            if (web / rel).is_file():
                return self._file(web / rel, web)
            if rel == "index.html":
                return self._file(cfg["_static_root"] / "no_web_build.html", cfg["_static_root"])
            return self._json(404, {"error": "not found"})

        def do_POST(self):
            if self._refuse_browser():
                return
            path = self.path.split("?", 1)[0]
            if self._refuse_unauthorized("POST", path):
                self.close_connection = True             # (its body not read)
                return
            if path.startswith("/api/match/") and int(self.headers.get("Content-Length") or 0) == 0:
                return self._match("POST", path, {})          # (ping / close need no body)
            try:
                body = self._body_json()
            except TooLarge as e:
                self.close_connection = True             # (its rest not read)
                return self._json(413, {"accepted": False, "errors": [str(e)]})
            except (ValueError, UnicodeDecodeError) as e:
                return self._json(400, {"accepted": False, "errors": [f"invalid JSON: {e}"]})
            if path.startswith("/api/match/"):
                return self._match("POST", path, body if isinstance(body, dict) else {})
            if path == "/api/packages":
                errors = validate_package(body)
                if errors:
                    return self._json(400, {"accepted": False, "errors": errors})
                key = package_key(body)
                # (when it came - the app's package_shown tells how long it waited to be shown: PR #12 adopted, the measuring)
                received = time.time()
                body["_received_ms"] = int(received * 1000)
                body["_pid"] = key
                body.pop("_launch_at", None)
                body.pop("_character_at", None)
                stamp = time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(received)) + ".%03d" % (int(received * 1000) % 1000)
                with state.lock:
                    # (the store first - a file read again here brings back its launch and character - then this package's
                    # stamps from that same moment, under the same lock: GPT's re-review of #39. Still not readable: the save
                    # below refuses, and the package is not taken)
                    frozen = state.packages_ready_locked()
                    char_now = state._character_now_locked()
                    seen = None if frozen else state.package_log.get(key)
                    if seen is not None and key.startswith("h:") and received - seen.get("queued_ts", 0) > CONTENT_DUP_SECONDS \
                            and not any(q.get("_pid") == key for q in state.packages):
                        del state.package_log[key]          # (the same words long after: a new package)
                        seen = None
                    if seen is not None:
                        # sent again (the answer lost): not queued twice - where the first one is now
                        n = len(state.packages)
                        dup = {"accepted": True, "duplicate": True, "id": key, "queue": n, "stage": seen.get("stage"), "reason": seen.get("reason")}
                    else:
                        dup = None
                        # an untagged package is for the launch and the character of now (PR #24, GPT's point 3)
                        if body.get("launch") is None:
                            body["_launch_at"] = state.launch.get("id")
                        if body.get("character") is None:
                            body["_character_at"] = char_now
                        size = _size(body)
                        if len(state.packages) >= MAX_PACKAGES or state.packages_bytes + size > MAX_PACKAGES_BYTES:
                            full = len(state.packages)
                        else:
                            full = None
                            state.packages.append(body)
                            state.package_sizes.append(size)
                            state.packages_bytes += size
                            state.package_log[key] = {"id": key, "session": body["session"], "turn": body["turn"], "beat": body.get("beat"),
                                                      "launch": body.get("launch") if body.get("launch") is not None else body.get("_launch_at"),
                                                      "character": body.get("character") if body.get("character") is not None else body.get("_character_at"),
                                                      "monologue": bool(body.get("monologue")), "stage": "queued", "queued": stamp, "queued_ts": received,
                                                      "updated_ts": received}
                            try:
                                state._save_packages()
                                saved = True
                            except OSError as e:
                                # not kept: taken back, answered as not taken (Muse sends it again)
                                state.packages.pop()
                                state.packages_bytes -= state.package_sizes.pop()
                                del state.package_log[key]
                                saved = False
                                sys.stderr.write(f"[bridge] package not saved, not taken: {e}\n")
                            n = len(state.packages)
                if dup is not None:
                    return self._json(200, dup)
                if full is not None:
                    return self._json(429, {"accepted": False, "queue": full,
                                            "errors": [f"the queue is full ({MAX_PACKAGES} packages / {MAX_PACKAGES_BYTES} bytes): send again after the app has taken some"]})
                if not saved:
                    why = frozen + " - not taken: send it again later" if frozen else "the bridge could not keep the package (its file not written): not taken - send it again"
                    return self._json(503, {"accepted": False, "errors": [why]})
                sys.stderr.write(f"[bridge] package accepted: session={body['session']} turn={body['turn']} queue={n}\n")
                return self._json(200, {"accepted": True, "id": key, "queue": n, "received_ms": body["_received_ms"], "received": stamp})
            if path == "/api/packages/ack":
                if not self._active_window():
                    return self._json(409, {"error": "another app window is active"})
                ids = body.get("ids") if isinstance(body, dict) else None
                stage = body.get("stage") if isinstance(body, dict) else None
                reason = body.get("reason") if isinstance(body, dict) else None
                block = body.get("block") if isinstance(body, dict) else None
                blocks = body.get("blocks") if isinstance(body, dict) else None
                count_ok = lambda v: v is None or (isinstance(v, int) and not isinstance(v, bool) and 0 <= v <= 10000)
                if (not isinstance(ids, list) or not ids or len(ids) > 200 or not all(isinstance(i, str) and i for i in ids)
                        or stage not in ACK_STAGES or (reason is not None and not isinstance(reason, str))
                        or not count_ok(block) or not count_ok(blocks) or (block is not None and stage not in ("started", "finished"))):
                    return self._json(400, {"error": "ack takes {\"ids\": [ids], \"stage\": received / started / finished / dropped, "
                                                     "\"reason\": string or null, \"block\": the block shown (started / finished), \"blocks\": their count}"})
                unknown = []
                with state.lock:
                    frozen = state.packages_ready_locked()
                    if frozen:
                        return self._json(503, {"ok": False, "error": frozen})
                    for pid in ids:
                        r = state._mark(pid, stage, reason if stage in ("dropped", "finished") else None)
                        if r is None:
                            unknown.append(pid)
                        elif r.get("stage") in ("started", "finished"):
                            # (#70 adopted, 1) the blocks shown: the last one known, their count
                            if block is not None and block > r.get("last_block", -1):
                                r["last_block"] = block
                                r["block_at"] = _now()
                            if blocks is not None:
                                r["blocks"] = blocks
                        state._take_out(pid)               # (received, begun or dropped by the app: no body kept)
                    try:
                        state._save_packages()
                    except OSError as e:
                        sys.stderr.write(f"[bridge] the acks not saved: {e}\n")
                        return self._json(503, {"ok": False, "error": state.packages_frozen or "not saved (the bridge's file could not be written)"})
                return self._json(200, {"ok": True, "unknown": unknown})
            if path == "/api/choices":
                errors = validate_choice(body)
                if errors:
                    return self._json(400, {"accepted": False, "errors": errors})
                body = {k: body[k] for k in ("session", "turn", "kind", "value")}
                body["received"] = _now()
                with state.lock:
                    state.choices.append(body)
                    del state.choices[:-MAX_CHOICES]
                    while len(state.choices) > 1 and sum(_size(c) for c in state.choices) > MAX_CHOICES_BYTES:
                        del state.choices[0]
                    n = len(state.choices)
                eid = state.add_event({"type": "choice", **{k: body[k] for k in ("session", "turn", "kind", "value")}})
                return self._json(200, {"accepted": True, "queue": n, "event_id": eid})
            if path == "/api/events":
                errors = validate_event(body)
                if errors:
                    return self._json(400, {"accepted": False, "errors": errors})
                if not self._active_window():
                    return self._json(409, {"accepted": False, "errors": ["another app window is active"]})
                if self.headers.get("X-MMS-Instance"):
                    state.app_seen = time.time()
                with state.lock:
                    fail = state.faults["fail_500"] > 0
                    if fail:
                        state.faults["fail_500"] -= 1
                if fail:
                    return self._json(500, {"accepted": False, "errors": ["(test) a failure before saving"]})
                cid = (self.headers.get("X-MMS-Event-Id") or "")[:128] or None
                try:
                    eid, again = state.add_app_event(body, cid)
                except TooLarge as e:
                    return self._json(413, {"accepted": False, "errors": [str(e)]})
                except OSError as e:                     # (not saved: nothing shown, the app sends it again)
                    sys.stderr.write(f"[bridge] the event not saved: {e}\n")
                    return self._json(503, {"accepted": False, "errors": [state.events_frozen or "the event feed could not be saved (its file could not be written)"]})
                sys.stderr.write(f"[bridge] event {eid}: {body['type']}{' (sent again: kept once)' if again else ''}\n")
                with state.lock:
                    drop = state.faults["drop_after_save"] > 0
                    if drop:
                        state.faults["drop_after_save"] -= 1
                if drop:
                    self.close_connection = True          # (test) saved, then the answer lost
                    return None
                return self._json(200, {"accepted": True, "id": eid, "again": again})
            if path == "/api/test/faults" and cfg.get("_test"):
                # (--test only: the checks' failures - the next N events answered 500 before saving / saved and the answer lost)
                with state.lock:
                    for k in ("fail_500", "drop_after_save", "slow_state", "fail_package_save"):
                        if isinstance(body.get(k), int) and not isinstance(body.get(k), bool):
                            state.faults[k] = max(0, body[k])
                    return self._json(200, {**state.faults, "duplicates": state.duplicates})
            if path == "/api/launch":
                if (not isinstance(body, dict) or not isinstance(body.get("launch"), str) or not body["launch"]
                        or body.get("phase") not in LAUNCH_PHASES
                        or (body.get("character") is not None and not isinstance(body.get("character"), dict))):
                    return self._json(400, {"error": "launch takes {\"launch\": id, \"phase\": " + " / ".join(LAUNCH_PHASES)
                                                     + ", \"character\": object or null}"})
                if not self._active_window():
                    return self._json(409, {"error": "another app window is active"})
                dropped = state.set_launch(body["launch"], body["phase"], body.get("character"))
                if dropped is None:
                    with state.lock:
                        why = state.packages_frozen
                    return self._json(503, {"ok": False, "error": why})
                for pkg, why in dropped:
                    if why == "interrupted":
                        state.note_interrupted(pkg)
                    else:
                        state.note_dropped(pkg, why)
                if self.headers.get("X-MMS-Instance"):
                    state.app_seen = time.time()
                sys.stderr.write(f"[bridge] launch {body['launch']}: {body['phase']}\n")
                return self._json(200, {"ok": True})
            if path == "/api/events/ack":
                ids = body.get("ids", []) if isinstance(body, dict) else None
                upto = body.get("upto") if isinstance(body, dict) else None
                if (not isinstance(ids, list) or not all(isinstance(i, int) and not isinstance(i, bool) for i in ids)
                        or (upto is not None and (isinstance(upto, bool) or not isinstance(upto, int)))):
                    return self._json(400, {"error": "ack takes {\"ids\": [integers]} and/or {\"upto\": integer}"})
                try:
                    n = state.ack_events(set(ids), upto)
                except OSError as e:
                    sys.stderr.write(f"[bridge] the acks not saved: {e}\n")
                    return self._json(503, {"error": state.events_frozen or "the event feed could not be saved (its file could not be written)"})
                with state.lock:
                    return self._json(200, {"acked": n, "pending": len(state.events)})
            if path == "/api/state":
                if not isinstance(body, dict):
                    return self._json(400, {"error": "state must be a JSON object"})
                with state.lock:
                    slow = state.faults["slow_state"] > 0
                    if slow:
                        state.faults["slow_state"] -= 1
                if slow:
                    time.sleep(1.5)                       # (test) a state held on the wire
                if not self._active_window():
                    return self._json(409, {"error": "another app window is active"})
                with state.lock:
                    state.app_state = {k: body.get(k) for k in STATE_FIELDS if k in body}
                    state.app_state_time = _now()
                    if self.headers.get("X-MMS-Instance"):
                        state.app_seen = time.time()     # the app's heartbeat (it does not poll during a long block)
                return self._json(200, {"ok": True})
            if path == "/api/catalog":
                if not isinstance(body, dict) or not isinstance(body.get("plays"), list):
                    return self._json(400, {"error": "catalog must be a JSON object with a plays list"})
                if not self._active_window():
                    return self._json(409, {"error": "another app window is active"})
                with state.lock:
                    state.catalog = body
                    state.catalog_time = _now()
                    state.labels.reconcile(body)            # (the labels set against what there is now - PR #46 §1)
                return self._json(200, {"ok": True})
            if path in ("/api/labels/coordinates", "/api/labels/rooms"):
                kind = path.rsplit("/", 1)[1]
                with state.lock:
                    code, answer = state.labels.write(kind, body, state.catalog, "muse")
                return self._json(code, answer)
            if path == "/api/tts":
                return self._tts(body)
            return self._json(404, {"error": "not found"})

        def _tts(self, body):
            if not isinstance(body, dict) or not isinstance(body.get("text"), str) or not body["text"].strip():
                return self._json(400, {"error": "text must be a non-empty string"})
            text = body["text"]
            voice = str(body.get("voice") or state.default_voice or "")
            caption = str(body.get("caption") or "")
            emoji = str(body.get("emoji") or "")
            seed = body.get("seed")
            if seed is not None and (isinstance(seed, bool) or not isinstance(seed, int) or seed < 0):
                return self._json(400, {"error": "seed must be a non-negative integer or null"})
            ir = state.irodori
            if not ir.enabled:
                return self._json(503, {"error": "the voice is off", "voice": False})
            if not ir.identity:
                ir.health()        # which model answers is part of the key (mock audio never stands in for the real voice)
            key_src = json.dumps([text, voice, caption, emoji, seed, ir.model, ir.caption_mode, ir.emoji_mode, ir.extra, ir.identity,
                                  voice_version(cfg["_voice_catalog"], voice)], ensure_ascii=False, sort_keys=True)
            h = hashlib.sha256(key_src.encode("utf-8")).hexdigest()[:32]
            out = state.audio_dir / f"{h}.wav"
            with state.tts_lock(h):
                if not out.is_file():
                    t0 = time.time()
                    try:
                        data = ir.synthesize(text, voice, caption, emoji, seed)
                    except IrodoriError as e:
                        sys.stderr.write(f"[bridge] tts failed: {e}\n")
                        return self._json(502, {"error": str(e)})
                    tmp = out.with_suffix(".part")
                    tmp.write_bytes(data)
                    os.replace(tmp, out)
                    sys.stderr.write(f"[bridge] tts generated {h} in {time.time() - t0:.1f}s ({len(data)} bytes)\n")
            return self._json(200, {"audio_url": f"/audio/{h}.wav"})

    return Handler


class BridgeServer(ThreadingHTTPServer):
    """One bridge per port. http.server sets SO_REUSEADDR, which on Windows lets a second process bind the same port as
    well: two bridges would answer at random and split the package queue. On Windows the port is bound exclusively."""
    daemon_threads = True
    allow_reuse_address = os.name != "nt"

    def server_bind(self) -> None:
        if os.name == "nt" and hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description="MuseStudio bridge")
    ap.add_argument("--config", default=str(HERE / "bridge_config.json"))
    ap.add_argument("--port", type=int, default=0, help="override the configured port")
    ap.add_argument("--test", action="store_true", help="(the checks) allow POST /api/test/faults")
    ap.add_argument("--print-token", choices=ROLES, help="print this role's token (made if missing) and quit")
    ap.add_argument("--new-tokens", action="store_true",
                    help="replace both tokens with new ones (a running bridge and the app take them; the old ones stop working) and quit")
    ap.add_argument("--list-machines", action="store_true",
                    help="list this PC's tailnet machines with their StableID (for muse_listener.machine) and quit")
    ap.add_argument("--forget-muse-placeholder", action="store_true",
                    help="forget the learned digest of Muse's Authorization (the next one from Muse's machine is learnt) and quit")
    ap.add_argument("--log-muse-requests", action="store_true",
                    help="(diagnosis) write every connection and request on Muse's listener in the log - the way, its headers' "
                         "names (no values), the answer and the time")
    args = ap.parse_args()
    conf = Path(args.config)
    if not conf.is_file() and conf == HERE / "bridge_config.json" and (HERE / "bridge_config.example.json").is_file():
        conf = HERE / "bridge_config.example.json"      # (a fresh clone: the example until this machine's own is made)
        sys.stderr.write("[bridge] no bridge_config.json: using bridge_config.example.json\n")
    cfg = load_config(conf)
    if args.new_tokens:
        # (the file replaced in one step, never deleted first: a running bridge cannot write the old ones back in between)
        save_tokens(cfg["_tokens_file"], {r: secrets.token_urlsafe(32) for r in ROLES})
        print(f"new tokens made in {cfg['_tokens_file']} - the old ones no longer work. Muse's: python bridge/bridge.py --print-token muse")
        return 0
    if args.print_token:
        print(load_tokens(cfg["_tokens_file"])[args.print_token])
        return 0
    if args.list_machines:
        cli = (cfg.get("muse_listener") or {}).get("tailscale") or muse_listener.DEFAULT_TAILSCALE
        return muse_listener.list_machines([str(x) for x in (cli if isinstance(cli, list) else [cli])])
    if args.forget_muse_placeholder:
        return muse_listener.forget_placeholder(cfg["_tokens_file"])
    cfg["_test"] = args.test
    cfg["_log_muse_requests"] = args.log_muse_requests
    port = args.port or int(cfg.get("port", 18765))
    host = str(cfg.get("host", "127.0.0.1"))
    state = State(cfg)
    try:
        server = BridgeServer((host, port), make_handler(state))
    except OSError as e:
        sys.stderr.write(f"[bridge] cannot listen on {host}:{port} ({e}). Another bridge is probably running there: "
                         f"check http://{host}:{port}/health\n")
        return 2
    sys.stderr.write(f"[bridge] listening on http://{host}:{port}/  irodori={state.irodori.base if state.irodori.enabled else 'off'}  "
                     f"web_root={cfg['_web_root']}  auth={'required' if state.auth else 'off'}\n")
    listener = muse_listener.start(cfg, state, port, host_allowed, MAX_BODY)     # (Muse's own port - PR #31; None: not asked)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        if listener is not None:
            listener.shutdown()
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
