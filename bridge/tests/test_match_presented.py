"""What crosses in a match (bridge/muse_match.py presented; MUSE_GUIDE 8, contract 1.30): a dialogue's text without its
（） inner thoughts - a ruby inside them gone with them - and its ruby 漢字《かんじ》 kept, for the receiving app's own parse
(PR #46 §6: the parse is the showing side's, one for every way a line comes)."""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _helpers import ROOT  # noqa: E402

sys.path.insert(0, str(ROOT / "bridge"))
import muse_match  # noqa: E402


class Presented(unittest.TestCase):
    def test_ruby_kept_inner_thoughts_gone(self):
        pkg = {"session": "s", "turn": 1, "blocks": [
            {"type": "dialogue", "text": "今日《きょう》は（心《こころ》の声）｜東京駅《とうきょうえき》へ。"},
            {"type": "sound", "text": "（足音《あしおと》）"},
        ]}
        out = muse_match.presented(pkg)
        self.assertEqual(out["blocks"][0]["text"], "今日《きょう》は｜東京駅《とうきょうえき》へ。")
        self.assertEqual(out["blocks"][1]["text"], "（足音《あしおと》）", "a sound block crosses as it is")
        self.assertEqual(pkg["blocks"][0]["text"], "今日《きょう》は（心《こころ》の声）｜東京駅《とうきょうえき》へ。", "the package sent to her own app unchanged")


if __name__ == "__main__":
    unittest.main()
