"""The install check (tools/doctor.py, #70 adopted, 4): each part told OK / 要対応 / 未確認 with what to do; a token never
shown (its length and fingerprint only), no folder's path, nothing written. No game, no network (the bridge's port not
probed).

  python -B -m unittest discover -s MuseStudio/tools/tests
"""
from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import doctor  # noqa: E402


def dll(path: Path, version: str):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b"MZ\x00\x00" + b"\x00" * 64 + "FileVersion".encode("utf-16-le") + b"\x00\x00\x00\x00"
                     + version.encode("utf-16-le") + b"\x00\x00" + b"\x00" * 16)


class Doctor(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="doctor_test_"))

    def statuses(self, r: doctor.Report) -> dict[str, str]:
        return {item: status for status, item, _, _ in r.lines}

    def test_no_original_is_to_do(self):
        r = doctor.Report()
        self.assertIsNone(doctor.check_original(r, ""))
        doctor.check_mods(r, None)
        st = self.statuses(r)
        self.assertEqual((st["原作の場所"], st["前提の Mod"]), ("要対応", "未確認"))
        self.assertIn("original_path.txt", r.text())

    def test_an_original_with_its_mods(self):
        orig = self.tmp / "orig"
        (orig / "abdata").mkdir(parents=True)
        (orig / "CharaStudio.exe").write_bytes(b"MZ")
        dll(orig / "BepInEx" / "core" / "BepInEx.dll", "5.4.23.2")
        dll(orig / "BepInEx" / "plugins" / "KKAPI.dll", "1.42.2")
        dll(orig / "BepInEx" / "plugins" / "KK_BepisPlugins" / "Sideloader.dll", "20.0")
        r = doctor.Report()
        p = doctor.check_original(r, str(orig))
        doctor.check_mods(r, p)
        st = self.statuses(r)
        self.assertEqual((st["原作の場所"], st["BepInEx"], st["KKAPI"], st["Sideloader"]), ("OK", "OK", "OK", "OK"))
        self.assertEqual(st["ExtensibleSaveFormat"], "要対応")
        self.assertEqual((st["Push Up"], st["HideAllUI"]), ("OK", "OK"))     # (used when there: not needed)
        text = r.text()
        self.assertIn("5.4.23.2", text)
        self.assertIn("1.42.2", text)
        self.assertNotIn(str(self.tmp), text)                                # (no folder's path)

    def test_a_release_of_another_commit_is_to_do(self):
        game = self.tmp / "game"
        for n in doctor.LINKS:
            (game / n).mkdir(parents=True)
        dest = game / "BepInEx" / "plugins" / "MuseStudio"
        dest.mkdir(parents=True)
        for n in ("MuseStudio.dll", "MuseMaker.dll"):
            (dest / n).write_bytes(b"MZ")
        (dest / "musestudio_dll.json").write_text(json.dumps({"source_commit": "a" * 40, "plugin_version": "0.1.0", "contract": "1.37"}), encoding="utf-8")
        r = doctor.Report()
        doctor.check_launch_folder(r, game, "b" * 40)
        self.assertEqual(self.statuses(r)["MuseStudio の DLL"], "要対応")
        r = doctor.Report()
        doctor.check_launch_folder(r, game, "a" * 40)
        self.assertEqual(self.statuses(r)["MuseStudio の DLL"], "OK")

    def test_tokens_shown_only_as_length_and_fingerprint(self):
        bridge = self.tmp / "bridge"
        (bridge / "cache").mkdir(parents=True)
        app, muse = "A" * 43, "secret-muse-token-value-0123456789abcdef"
        (bridge / "cache" / "tokens.json").write_text(json.dumps({"app": app, "muse": muse}), encoding="utf-8")
        (bridge / "bridge_config.json").write_text(json.dumps({"auth": "required", "port": 18799,
                                                               "muse_listener": {"enabled": True, "machine": "nXYZ123"}}), encoding="utf-8")
        r = doctor.Report()
        doctor.check_bridge(r, bridge, probe=False)
        text = r.text()
        self.assertNotIn(app, text)
        self.assertNotIn(muse, text)
        self.assertNotIn("nXYZ123", text)                                    # (Muse's machine not named)
        self.assertIn(doctor.fingerprint(muse), text)
        self.assertIn("長さ 40", text)
        self.assertEqual(self.statuses(r)["Muse の受け口"], "OK")

    # (GPT's post-review of #86) a setting of the wrong form: told as 要対応 (what rests on it 未確認), the rest looked at -
    # never an exception, no path, no value of it in the words
    def test_settings_of_the_wrong_form_do_not_stop_it(self):
        cases = (("[]", None, "ブリッジの設定"), ('{"port": "invalid"}', None, "ブリッジのポート"), ('{"port": 70000}', None, "ブリッジのポート"),
                 ('{"tokens_file": 5}', None, "ブリッジの鍵"), ('{"muse_listener": "on"}', None, "Muse の受け口"),
                 ("{}", "0", "ブリッジの鍵"), ("{}", '{"app": 5, "muse": null}', "ブリッジの鍵"))
        for k, (cfg, tokens, item) in enumerate(cases):
            bridge = self.tmp / f"bridge{k}"
            (bridge / "cache").mkdir(parents=True)
            (bridge / "bridge_config.json").write_text(cfg, encoding="utf-8")
            if tokens is not None:
                (bridge / "cache" / "tokens.json").write_text(tokens, encoding="utf-8")
            r = doctor.Report()
            doctor.check_bridge(r, bridge, probe=True)            # (a bad port: not probed at all)
            st = self.statuses(r)
            self.assertEqual(st.get(item), "要対応", (cfg, tokens, r.text()))
            self.assertNotIn(str(self.tmp), r.text())
        # the whole run: a check that fails inside is 未確認, the rest go on
        r = doctor.Report()

        def broken(rep):
            raise RuntimeError("C:\secret\path inside")
        doctor.safely(r, "壊れた項目", broken)
        doctor.check_python(r)
        self.assertEqual(self.statuses(r)["壊れた項目"], "未確認")
        self.assertNotIn("secret", r.text())
        self.assertIn("Python", self.statuses(r))
        man_game = self.tmp / "game_m"
        for n in doctor.LINKS:
            (man_game / n).mkdir(parents=True)
        dest = man_game / "BepInEx" / "plugins" / "MuseStudio"
        dest.mkdir(parents=True)
        for n in ("MuseStudio.dll", "MuseMaker.dll"):
            (dest / n).write_bytes(b"MZ")
        (dest / "musestudio_dll.json").write_text("[1, 2]", encoding="utf-8")
        r = doctor.Report()
        doctor.check_launch_folder(r, man_game, "a" * 40)
        self.assertEqual(self.statuses(r)["MuseStudio の DLL"], "要対応")

    def test_nothing_is_written(self):
        before = sorted(p.relative_to(self.tmp) for p in self.tmp.rglob("*"))
        r = doctor.Report()
        doctor.check_bridge(r, self.tmp / "bridge_none", probe=False)
        doctor.check_launch_folder(r, self.tmp / "game_none", "")
        self.assertEqual(sorted(p.relative_to(self.tmp) for p in self.tmp.rglob("*")), before)


if __name__ == "__main__":
    unittest.main()
