"""A throwaway bridge for the tests: its own config from the example (its files in a temporary folder), on a spare port."""
from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(ROOT / "bridge"))
import bridge as _bridge  # noqa: E402


class Bridge:
    def __init__(self, port: int, extra: dict | None = None, test: bool = False):
        self.port = port
        self.tmp = Path(tempfile.mkdtemp(prefix="bridge_test_"))
        cfg = json.loads((ROOT / "bridge/bridge_config.example.json").read_text(encoding="utf-8"))
        for k in ("web_root", "static_root", "kkdata_root", "audio_cache_dir", "voice_catalog", "characters_dir"):
            cfg[k] = str((ROOT / "bridge" / cfg[k]).resolve())
        cfg.update(events_file=str(self.tmp / "events.json"), persona_file=str(self.tmp / "personas.json"), card_dirs=[])
        cfg["audio_cache_dir"] = str(self.tmp / "audio")
        cfg["rate_limits"] = {k: [100000, 100000] for k in ("muse", "app", "all", "anon")}    # (the rates: test_rate.py's own)
        cfg.update(extra or {})
        (self.tmp / "config.json").write_text(json.dumps(cfg), encoding="utf-8")
        args = [sys.executable, "-B", str(ROOT / "bridge/bridge.py"), "--config", str(self.tmp / "config.json"), "--port", str(port)]
        self.proc = subprocess.Popen(args + (["--test"] if test else []), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(100):
            try:
                urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1)
                return
            except OSError:
                time.sleep(0.1)
        raise RuntimeError("the bridge did not start")

    def token(self, role: str) -> str:
        return json.loads((self.tmp / "cache" / "tokens.json").read_text(encoding="utf-8"))[role]

    def call(self, method: str, path: str, body=None, raw: bytes | None = None, headers: dict | None = None, role: str | None = "auto"):
        # role: the token sent - "auto" (Muse's where Muse may, else the app's), "muse", "app", or None (no token)
        data = raw if raw is not None else (json.dumps(body).encode("utf-8") if body is not None else None)
        h = {"Content-Type": "application/json"}
        if role == "auto":
            allowed = _bridge.roles_for(method, path.split("?", 1)[0])
            role = "muse" if "muse" in allowed else "app"
        if role is not None:
            h["Authorization"] = "Bearer " + self.token(role)
        h.update(headers or {})
        req = urllib.request.Request(f"http://127.0.0.1:{self.port}{path}", data=data, method=method, headers=h)
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                text = r.read()
                return r.status, json.loads(text) if text else None
        except urllib.error.HTTPError as e:
            try:
                text = e.read()
            except OSError:                  # (a 413 closes the connection: on Windows its body may not be read)
                return e.code, None
            try:
                return e.code, json.loads(text) if text else None
            except ValueError:
                return e.code, None

    def close(self):
        self.proc.terminate()
        self.proc.wait()


def package(text: str = "x", turn: int = 1) -> dict:
    return {"session": "t", "turn": turn, "blocks": [{"type": "dialogue", "text": text}]}
