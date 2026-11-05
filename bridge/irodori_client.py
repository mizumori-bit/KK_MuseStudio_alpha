"""Irodori-TTS-Server client (implementation-spec.md 4.2): the only module that knows how Irodori is called.

Irodori-TTS-Server (github.com/Aratako/Irodori-TTS-Server) is OpenAI compatible: POST /v1/audio/speech with
model / input / voice / response_format. Its README (checked 2026-10-31) documents Irodori options under an `irodori`
object; the caption (Voice Design / caption-controlled cloning) goes in `irodori.caption` and is ignored by checkpoints
without caption conditioning. Emoji annotations in the input text influence delivery (EMOJI_ANNOTATIONS.md of
Irodori-TTS-500M-v2: repeating an emoji strengthens it; its examples put the emoji right after the phrase it colours), so
the emoji goes after each sentence ("inline"), before the sentence's closing mark: the server splits long text into chunks
after those marks, and every chunk keeps its own emoji. The seed (a prompt number's chosen take, MUSE_GUIDE.md 3.8) goes in
`irodori.seed`. Caption / emoji handling is switchable in bridge_config.json (irodori.*). irodori.enabled false: the voice
is off - Irodori is never called (health False, no voices, synthesize raises VoiceOff). Standard library only.
"""
from __future__ import annotations

import json
import re
import threading
import time
import urllib.error
import urllib.request


class IrodoriError(Exception):
    pass


class VoiceOff(IrodoriError):
    pass


_CLOSE = "。！？!?．"           # sentence ends; Irodori-TTS-Server splits chunks after these (and after 、 , . line breaks)
_TRAIL = "…‥"


def inline_emoji(text: str, emoji: str) -> str:
    """The emoji after each sentence of text: before a run of closing marks, after a trailing ellipsis, and at the end."""
    if not emoji:
        return text
    out = re.sub(f"([{_TRAIL}]+)(?![{_TRAIL}{_CLOSE}])", lambda m: m.group(1) + emoji, text)
    out = re.sub(f"(?<=[^{_CLOSE}\\s])([{_CLOSE}]+)", lambda m: emoji + m.group(1), out)
    if not out.rstrip(_CLOSE + " \n").endswith(emoji):
        out = out.rstrip() + emoji
    return out


class IrodoriClient:
    def __init__(self, cfg: dict):
        self.enabled = cfg.get("enabled", True) is not False                 # false: the voice is off (never called)
        self.base = str(cfg.get("url", "http://127.0.0.1:8088")).rstrip("/")
        self.model = str(cfg.get("model", "irodori-tts"))
        self.timeout = float(cfg.get("timeout", 60))
        self.caption_mode = str(cfg.get("caption_mode", "irodori.caption"))   # "irodori.caption" | "none"
        self.emoji_mode = str(cfg.get("emoji_mode", "inline"))                # "inline" | "prefix" | "none"
        self.response_format = str(cfg.get("response_format", "wav"))
        self.api_key = str(cfg.get("api_key", ""))
        self.extra = dict(cfg.get("irodori_options", {}))                     # e.g. {"num_steps": 32, "seed": 0}
        self.fail_fast_seconds = float(cfg.get("fail_fast_seconds", 5))
        self._down_until = 0.0
        self._lock = threading.Lock()
        self.identity = ""        # what answered /health last (the model and precision, or "mock"): part of the voice cache key

    # ---- health ---------------------------------------------------------------------------------------------------
    def health(self, timeout: float = 2.0) -> bool:
        if not self.enabled:
            return False
        try:
            with urllib.request.urlopen(self.base + "/health", timeout=timeout) as r:
                ok = 200 <= r.status < 300
                body = r.read()
            try:
                doc = json.loads(body.decode("utf-8"))
                m = doc.get("model", {}) if isinstance(doc, dict) else {}
                self.identity = "mock" if doc.get("mock") else "|".join(
                    str(m.get(k, "")) for k in ("hf_checkpoint", "model_precision", "codec_precision"))
            except (ValueError, AttributeError):
                pass
        except (urllib.error.URLError, OSError, ValueError):
            ok = False
        with self._lock:
            self._down_until = 0.0 if ok else time.time() + self.fail_fast_seconds
        return ok

    def list_voices(self, timeout: float = 5.0) -> list[str] | None:
        """The ids of the voices Irodori knows (GET /v1/audio/voices), or None when it does not answer."""
        if not self.enabled:
            return None
        headers = {"Authorization": "Bearer " + self.api_key} if self.api_key else {}
        try:
            req = urllib.request.Request(self.base + "/v1/audio/voices", headers=headers)
            with urllib.request.urlopen(req, timeout=timeout) as r:
                doc = json.loads(r.read().decode("utf-8"))
        except (urllib.error.URLError, OSError, ValueError):
            return None
        return [str(v.get("id")) for v in doc.get("data", []) if isinstance(v, dict) and v.get("id")]

    # ---- synthesis ------------------------------------------------------------------------------------------------
    def request_body(self, text: str, voice: str, caption: str, emoji: str, seed: int | None = None) -> dict:
        inp = text
        if emoji and self.emoji_mode == "inline":
            inp = inline_emoji(text, emoji)
        elif emoji and self.emoji_mode == "prefix":
            inp = emoji + inp
        body: dict = {"model": self.model, "input": inp, "response_format": self.response_format}
        if voice:
            body["voice"] = voice
        opts = dict(self.extra)
        if caption and self.caption_mode == "irodori.caption":
            opts["caption"] = caption
        if seed is not None:
            opts["seed"] = int(seed)
        if opts:
            body["irodori"] = opts
        return body

    def synthesize(self, text: str, voice: str = "", caption: str = "", emoji: str = "", seed: int | None = None) -> bytes:
        """Returns the audio bytes (WAV by default) or raises IrodoriError (VoiceOff when the voice is off)."""
        if not self.enabled:
            raise VoiceOff("the voice is off (bridge_config.json irodori.enabled)")
        with self._lock:
            if time.time() < self._down_until:
                raise IrodoriError("Irodori unavailable (recent failure)")
        body = self.request_body(text, voice, caption, emoji, seed)
        headers = {"Content-Type": "application/json"}
        if self.api_key:
            headers["Authorization"] = "Bearer " + self.api_key
        req = urllib.request.Request(self.base + "/v1/audio/speech", data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
                                     headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as r:
                data = r.read()
        except urllib.error.HTTPError as e:
            detail = e.read()[:300].decode("utf-8", "replace")
            raise IrodoriError(f"HTTP {e.code}: {detail}") from e
        except (urllib.error.URLError, OSError, ValueError) as e:
            with self._lock:
                self._down_until = time.time() + self.fail_fast_seconds
            raise IrodoriError(f"connection failed: {e}") from e
        if self.response_format == "wav" and not (data[:4] == b"RIFF" and data[8:12] == b"WAVE"):
            raise IrodoriError("response is not WAV")
        return data
