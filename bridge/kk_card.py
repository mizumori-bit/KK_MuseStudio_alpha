"""Reads a Koikatsu character card (PNG) for the bridge's character list: the profile (name, sex, personality), the
uncensor's "show the penis" (the futanari attribute, as the original's maker sets it) and a unique id when the card has
one (Koikatsu Sunshine's About block; Koikatsu cards have none). Standard library only.

The card (ChaFileControl.SaveCharaFile): the thumbnail PNG, then int32 product no, the marker string "【KoiKatuChara】", the
version string, int32 + the face PNG, int32 + the block header (MessagePack: lstInfo [{name, version, pos, size}]), int64
the data length, then the blocks (Parameter, KKEx, ...) at their positions. Strings are .NET BinaryWriter strings (a 7-bit
length, UTF-8).
"""
from __future__ import annotations

import struct
from pathlib import Path

UNCENSOR = "com.deathweasel.bepinex.uncensorselector"


class _Reader:
    def __init__(self, b: bytes, pos: int = 0):
        self.b, self.p = b, pos

    def take(self, n: int) -> bytes:
        if n < 0 or self.p + n > len(self.b):
            raise ValueError("card: truncated")
        v = self.b[self.p:self.p + n]
        self.p += n
        return v

    def int32(self) -> int:
        return struct.unpack("<i", self.take(4))[0]

    def int64(self) -> int:
        return struct.unpack("<q", self.take(8))[0]

    def dotnet_str(self) -> str:
        n, shift = 0, 0
        while True:
            c = self.take(1)[0]
            n |= (c & 0x7F) << shift
            if not c & 0x80:
                break
            shift += 7
        return self.take(n).decode("utf-8", "replace")


def unpack(b: bytes, pos: int = 0):
    """A minimal MessagePack decoder: (value, next position)."""
    t = b[pos]
    pos += 1
    if t <= 0x7F:
        return t, pos
    if 0x80 <= t <= 0x8F:
        return _map(b, pos, t & 0x0F)
    if 0x90 <= t <= 0x9F:
        return _arr(b, pos, t & 0x0F)
    if 0xA0 <= t <= 0xBF:
        n = t & 0x1F
        return b[pos:pos + n].decode("utf-8", "replace"), pos + n
    if t >= 0xE0:
        return t - 0x100, pos
    simple = {0xC0: None, 0xC2: False, 0xC3: True}
    if t in simple:
        return simple[t], pos
    if t in (0xC4, 0xC5, 0xC6):                      # bin
        size = {0xC4: 1, 0xC5: 2, 0xC6: 4}[t]
        n = int.from_bytes(b[pos:pos + size], "big")
        pos += size
        return b[pos:pos + n], pos + n
    if t in (0xC7, 0xC8, 0xC9):                      # ext
        size = {0xC7: 1, 0xC8: 2, 0xC9: 4}[t]
        n = int.from_bytes(b[pos:pos + size], "big")
        pos += size + 1
        return b[pos:pos + n], pos + n
    if t == 0xCA:
        return struct.unpack(">f", b[pos:pos + 4])[0], pos + 4
    if t == 0xCB:
        return struct.unpack(">d", b[pos:pos + 8])[0], pos + 8
    if t in (0xCC, 0xCD, 0xCE, 0xCF):
        size = {0xCC: 1, 0xCD: 2, 0xCE: 4, 0xCF: 8}[t]
        return int.from_bytes(b[pos:pos + size], "big"), pos + size
    if t in (0xD0, 0xD1, 0xD2, 0xD3):
        size = {0xD0: 1, 0xD1: 2, 0xD2: 4, 0xD3: 8}[t]
        return int.from_bytes(b[pos:pos + size], "big", signed=True), pos + size
    if t in (0xD4, 0xD5, 0xD6, 0xD7, 0xD8):          # fixext
        n = {0xD4: 1, 0xD5: 2, 0xD6: 4, 0xD7: 8, 0xD8: 16}[t]
        return b[pos + 1:pos + 1 + n], pos + 1 + n
    if t in (0xD9, 0xDA, 0xDB):                      # str 8 / 16 / 32
        size = {0xD9: 1, 0xDA: 2, 0xDB: 4}[t]
        n = int.from_bytes(b[pos:pos + size], "big")
        pos += size
        return b[pos:pos + n].decode("utf-8", "replace"), pos + n
    if t in (0xDC, 0xDD):
        size = 2 if t == 0xDC else 4
        return _arr(b, pos + size, int.from_bytes(b[pos:pos + size], "big"))
    if t in (0xDE, 0xDF):
        size = 2 if t == 0xDE else 4
        return _map(b, pos + size, int.from_bytes(b[pos:pos + size], "big"))
    raise ValueError(f"msgpack: type 0x{t:02x}")


def _arr(b: bytes, pos: int, n: int):
    out = []
    for _ in range(n):
        v, pos = unpack(b, pos)
        out.append(v)
    return out, pos


def _map(b: bytes, pos: int, n: int):
    out = {}
    for _ in range(n):
        k, pos = unpack(b, pos)
        v, pos = unpack(b, pos)
        out[k if isinstance(k, (str, int, float, bool)) or k is None else repr(k)] = v
    return out, pos


def read_card(path: Path) -> dict | None:
    """{name, sex, personality, futanari, uid} of a character card; None for anything else."""
    b = path.read_bytes()
    end = b.find(b"IEND")
    if not b.startswith(b"\x89PNG") or end < 0:
        return None
    r = _Reader(b, end + 8)                          # IEND, then its CRC
    r.int32()                                        # loadProductNo
    if "KoiKatuChara" not in r.dotnet_str():
        return None
    r.dotnet_str()                                   # version
    r.take(r.int32())                                # the face PNG
    header, _ = unpack(r.take(r.int32()))
    r.int64()
    data_start = r.p
    blocks = {}
    for info in header.get("lstInfo", []) if isinstance(header, dict) else []:
        try:
            start = data_start + int(info["pos"])
            blocks[info["name"]] = unpack(b[start:start + int(info["size"])])[0]
        except (KeyError, ValueError, IndexError, struct.error):
            continue
    param = blocks.get("Parameter") or {}
    if not isinstance(param, dict):
        return None
    name = (str(param.get("lastname") or "") + " " + str(param.get("firstname") or "")).strip()
    futa = False
    ext = blocks.get("KKEx")
    pd = ext.get(UNCENSOR) if isinstance(ext, dict) else None
    data = (pd[1] if isinstance(pd, list) and len(pd) > 1 else pd.get("data") if isinstance(pd, dict) else None)
    if isinstance(data, dict):
        futa = bool(data.get("DisplayPenis", False))
    about = blocks.get("About")
    uid = about.get("dataID") if isinstance(about, dict) and about.get("dataID") else None
    sex = int(param.get("sex", 1) or 0)
    return {"name": name, "sex": sex, "personality": param.get("personality"), "futanari": futa and sex == 1, "uid": uid}
