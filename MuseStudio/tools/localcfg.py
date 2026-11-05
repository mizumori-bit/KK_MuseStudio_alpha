"""This machine's paths for the tools, kept out of the repository: the original install's folder.

From the environment variable MUSESTUDIO_ORIGINAL, else MuseStudio/tools/original_path.txt (one line; not tracked), else
the tool's --original. Standard library only.
"""
from __future__ import annotations

import os
from pathlib import Path

HERE = Path(__file__).resolve().parent


def original_path() -> str:
    env = os.environ.get("MUSESTUDIO_ORIGINAL", "").strip()
    if env:
        return env
    local = HERE / "original_path.txt"
    if local.is_file():
        return local.read_text(encoding="utf-8").strip()
    return ""
