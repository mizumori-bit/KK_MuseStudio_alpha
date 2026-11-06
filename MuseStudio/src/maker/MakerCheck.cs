using System;
using System.Collections;
using System.IO;
using System.Text;
using ChaCustom;
using UnityEngine;

namespace MuseStudio.Maker
{
    // The maker's checks (tools/run_checks.py runs game/Koikatu.exe -femalemaker --muse-maker-check=<results>): on the maker's
    // first character, edits as the maker's panels make them (the card's values, the character updated), then the list, the
    // checks, undo / redo, the drag's merge, the saved card (a stream: no file in the user's folders) and a card loaded as a
    // new starting point (a temporary file). Writes CHECK lines and a summary, then quits without the original's question.
    class MakerCheck
    {
        readonly MakerChanges ch;
        readonly string path;
        readonly StringBuilder lines = new StringBuilder();
        int pass, fail;

        public MakerCheck(MakerChanges changes, string results) { ch = changes; path = results; }

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            lines.AppendLine((ok ? "CHECK PASS " : "CHECK FAIL ") + "maker: " + name + (ok || detail.Length == 0 ? "" : " -- " + detail));
            File.WriteAllText(path, lines.ToString(), new UTF8Encoding(false));
        }

        static WaitForSecondsRealtime Wait(float s) { return new WaitForSecondsRealtime(s); }

        // the list settled: its own writes taken in (a slow frame at the maker's start), then a moment
        IEnumerator Settle()
        {
            float t0 = Time.realtimeSinceStartup;
            while (ch.Adopting && Time.realtimeSinceStartup - t0 < 10f) yield return null;
            yield return Wait(0.3f);
        }

        public IEnumerator Run()
        {
            IEnumerator body = Body();
            while (true)
            {
                object cur;
                try
                {
                    if (!body.MoveNext()) break;
                    cur = body.Current;
                }
                catch (Exception e)
                {
                    Check("the checks ran to the end", false, e.GetType().Name + ": " + e.Message);
                    break;
                }
                yield return cur;
            }
            foreach (var l in ch.LogForCheck) lines.AppendLine("NOTE maker step: " + l);
            lines.AppendLine(string.Format("CHECK SUMMARY pass={0} fail={1}", pass, fail));
            File.WriteAllText(path, lines.ToString(), new UTF8Encoding(false));
            yield return Wait(0.5f);
            try { Singleton<Manager.Scene>.Instance.isGameEndCheck = false; } catch (Exception) { }
            Application.Quit();
        }

        IEnumerator Body()
        {
            var cha = MakerChanges.Cha;
            var f = cha.chaFile;
            ch.Reset();
            yield return Settle();
            Check("at the start nothing is listed, no undo", ch.Count == 0 && !ch.CanUndo, "count " + ch.Count);

            // edits as the maker's panels make them
            int iHeight = ch.Find("体型", ChaFileDefine.cf_bodyshapename[0]);
            int iHair = ch.Find("髪", "前髪の色");
            int iFirst = ch.Find("プロフィール", "名");
            int ct = f.status.coordinateType;
            string cn = new[] { "制服", "下校", "体操着", "水着", "部活", "私服", "お泊り" }[Mathf.Clamp(ct, 0, 6)];
            int iTop = ch.Find("服（" + cn + "）", "トップスの色1");
            float h0 = f.custom.body.shapeValueBody[0];
            Color hair0 = f.custom.hair.parts[1].baseColor, top0 = f.coordinate[ct].clothes.parts[0].colorInfo[0].baseColor;
            string first0 = f.parameter.firstname;
            float h1 = h0 > 0.5f ? h0 - 0.25f : h0 + 0.25f;
            cha.SetShapeBodyValue(0, h1);
            yield return Wait(1.0f);
            yield return Settle();
            f.custom.hair.parts[1].baseColor = new Color(0.9f, 0.1f, 0.1f);
            cha.ChangeSettingHairColor(1, true, true, true);
            yield return Wait(1.0f);
            yield return Settle();
            var red = new Color(0.1f, 0.2f, 0.85f);
            f.coordinate[ct].clothes.parts[0].colorInfo[0].baseColor = red;
            cha.nowCoordinate.clothes.parts[0].colorInfo[0].baseColor = red;
            cha.ChangeCustomClothes(true, 0, true, false, false, false, false);
            yield return Wait(1.0f);
            f.parameter.firstname = "点検";
            yield return Wait(1.0f);
            ch.LogForCheck.Add("top colour read " + ch.Text(iTop, ch.Now(iTop)) + " start " + ch.Text(iTop, ch.Start(iTop)) + " coordinate " + ct + " same object " + ReferenceEquals(cha.nowCoordinate, f.coordinate[ct])
                + " top id " + f.coordinate[ct].clothes.parts[0].id);
            Check("each edit is listed with a check (a slider, the hair's colour, the shown outfit's top colour, the name)",
                  iHeight >= 0 && iHair >= 0 && iFirst >= 0 && iTop >= 0 && ch.Count == 4 && ch.Checked(iHeight) && ch.Checked(iHair) && ch.Checked(iTop) && ch.Checked(iFirst),
                  string.Format("items {0} {1} {2} {3}, count {4}", iHeight, iHair, iTop, iFirst, ch.Count));
            Check("the list's wording: start → now (a slider in 0..100, a colour #rrggbb, the name)",
                  ch.Text(iHeight, ch.Start(iHeight)) == Mathf.RoundToInt(h0 * 100f).ToString() && ch.Text(iHair, ch.User(iHair)) == "#" + ColorUtility.ToHtmlStringRGB(new Color(0.9f, 0.1f, 0.1f)).ToLowerInvariant() && (string)ch.User(iFirst) == "点検",
                  ch.Text(iHeight, ch.Start(iHeight)) + " / " + ch.Text(iHair, ch.User(iHair)) + " / " + ch.User(iFirst));

            // a check taken off: the starting value back on the character (the shown outfit too); on again: the user's value
            ch.Toggle(iTop);
            yield return Wait(1.0f);
            yield return Settle();
            bool topBack = MakerChanges.Same(f.coordinate[ct].clothes.parts[0].colorInfo[0].baseColor, top0) && MakerChanges.Same(cha.nowCoordinate.clothes.parts[0].colorInfo[0].baseColor, top0)
                && !ch.Checked(iTop) && ch.Listed(iTop) && ch.Count == 4;
            ch.Toggle(iHeight);
            yield return Wait(1.0f);
            yield return Settle();
            bool heightBack = Mathf.Abs(f.custom.body.shapeValueBody[0] - h0) < 0.001f && !ch.Checked(iHeight);
            Check("a check taken off writes the starting value back (the outfit's colour on the card and the shown outfit, the slider); the row stays, unchecked",
                  topBack && heightBack, "top " + topBack + " height " + heightBack + " " + f.custom.body.shapeValueBody[0].ToString("F3"));
            yield return Shot("maker_changes.png");
            // undo: the check back on, the user's value again; redo: off again
            ch.Undo();
            yield return Wait(1.0f);
            yield return Settle();
            bool undoCheck = ch.Checked(iHeight) && Mathf.Abs(f.custom.body.shapeValueBody[0] - h1) < 0.001f;
            ch.Redo();
            yield return Wait(1.0f);
            yield return Settle();
            bool redoCheck = !ch.Checked(iHeight) && Mathf.Abs(f.custom.body.shapeValueBody[0] - h0) < 0.001f;
            Check("undo puts a taken-off check back (the user's value again), redo takes it off again", undoCheck && redoCheck, "undo " + undoCheck + " redo " + redoCheck);
            // undo past the edits: the name back, then nothing listed at the very start
            while (ch.CanUndo && ch.Listed(iFirst)) { ch.Undo(); yield return Wait(0.6f); yield return Settle(); }
            bool nameBack = f.parameter.firstname == first0 && !ch.Listed(iFirst);
            while (ch.CanUndo) { ch.Undo(); yield return Wait(0.6f); yield return Settle(); }
            bool allBack = ch.Count == 0 && Mathf.Abs(f.custom.body.shapeValueBody[0] - h0) < 0.001f && MakerChanges.Same(f.custom.hair.parts[1].baseColor, hair0)
                && MakerChanges.Same(f.coordinate[ct].clothes.parts[0].colorInfo[0].baseColor, top0) && f.parameter.firstname == first0;
            Check("undo goes back edit by edit (the name back), to the start: nothing listed, every value the starting one", nameBack && allBack, "name " + nameBack + " all " + allBack);
            while (ch.CanRedo) { ch.Redo(); yield return Wait(0.6f); yield return Settle(); }
            bool allAgain = ch.Count == 4 && ch.Checked(iHair) && f.parameter.firstname == "点検" && MakerChanges.Same(f.custom.hair.parts[1].baseColor, new Color(0.9f, 0.1f, 0.1f));
            Check("redo brings every step back", allAgain, "count " + ch.Count);

            // a slider's drag (the same item again and again within 0.8 s) is one step; a new edit after an undo drops the redo
            int steps0 = ch.StepsForCheck;
            for (int k = 1; k <= 5; k++) { cha.SetShapeBodyValue(1, 0.2f + 0.1f * k); yield return Wait(0.25f); }
            yield return Wait(1.0f);
            int dragSteps = ch.StepsForCheck - steps0;
            ch.Undo();
            yield return Wait(0.8f);
            yield return Settle();
            bool canRedo = ch.CanRedo;
            f.parameter.nickname = "てすと";
            yield return Wait(1.0f);
            Check("a slider's drag is one step; a new edit after an undo drops the redo", dragSteps == 1 && canRedo && !ch.CanRedo, "drag steps " + dragSteps + " redo before " + canRedo + " after " + ch.CanRedo);

            // the saved card has the checked changes only (an unchecked one at its starting value)
            int iNick = ch.Find("プロフィール", "あだ名");
            ch.Toggle(iNick);
            yield return Wait(1.0f);
            yield return Settle();
            if (f.pngData == null || f.pngData.Length == 0) f.pngData = new Texture2D(4, 4).EncodeToPNG();       // (a card's picture: the load reads past it)
            var saved = new ChaFileControl();
            bool savedOk = false;
            byte[] bytes;
            using (var ms = new MemoryStream()) { f.SaveCharaFile(ms, true); bytes = ms.ToArray(); }      // (the save closes the stream)
            using (var ms = new MemoryStream(bytes)) savedOk = saved.LoadCharaFile(ms, false, true);
            Check("the saved card has the checked changes only (the name and the hair colour in, the unchecked nickname and height out)",
                  savedOk && saved.parameter.firstname == "点検" && saved.parameter.nickname == f.parameter.nickname && f.parameter.nickname != "てすと"
                  && MakerChanges.Same(saved.custom.hair.parts[1].baseColor, new Color(0.9f, 0.1f, 0.1f)) && Mathf.Abs(saved.custom.body.shapeValueBody[0] - h0) < 0.001f,
                  "loaded " + savedOk + " name " + saved.parameter.firstname + " nick " + saved.parameter.nickname);

            // the list on the screen (the button, the rows) over a clear uGUI panel
            ch.Open = true;
            yield return Wait(0.8f);
            yield return Shot("maker_list.png");
            Check("the list opens over a clear panel that keeps the maker's own panels and camera from the clicks", ch.BlockerOnForCheck);
            ch.Open = false;

            // a card loaded into the maker: a new starting point, the list and the history begin again
            string tmp = Path.Combine(Path.GetTempPath(), "musemaker_check_" + DateTime.Now.Ticks + ".png");
            int resets0 = ch.ResetsForCheck;
            bool loaded = false;
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)) f.SaveCharaFile(fs, true);
                f.LoadFileLimited(tmp, cha.sex, true, true, true, true, true);          // (the original's returns false always)
                loaded = true;
                cha.ChangeCoordinateType(true);
                cha.Reload();
                Singleton<CustomBase>.Instance.updateCustomUI = true;
            }
            catch (Exception e) { Check("a card loads", false, e.Message); }
            yield return Wait(1.5f);
            Check("a card loaded is a new starting point: nothing listed, no undo", ch.ResetsForCheck > resets0 && ch.Count == 0 && !ch.CanUndo,
                  "loaded " + loaded + " resets " + resets0 + "->" + ch.ResetsForCheck + " count " + ch.Count);
            try { File.Delete(tmp); } catch (Exception) { }
        }

        IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            try { Application.CaptureScreenshot(Path.Combine(Path.GetDirectoryName(path), name)); } catch (Exception) { }
            yield return Wait(0.5f);
        }
    }
}
