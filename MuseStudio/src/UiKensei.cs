using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // 献精室's panel (#113-#115): its setup and start (コンテンツ… → 献精…), the run going on, the unfinished run left before,
    // the records (a turn list: what his screen showed / what Muse was told / the truth), and the gauge over the screen while a
    // run goes - his own screen only (nothing of it goes to Muse but what the run's disclosure lets out: Kensei)
    public partial class Ui
    {
        float kenseiH, kenseiRecH;
        bool kenseiStopArmed;
        float kenseiStopAt = -100f;
        List<string> kenseiFiles;
        Dictionary<string, object> kenseiRec;
        string kenseiView = "user";
        Vector2 kenseiScroll;
        int kenseiPage;
        SortedDictionary<string, List<string>> kenseiCands;
        int kenseiUntried;
        string kenseiCandKey = "";
        float kenseiCandAt = -100f;
        public string KenseiShown = "";       // (checks) what the gauge over the screen shows

        static string WhoName(string who) { return who == "her" ? "彼女が主導" : who == "him" ? "あなたが主導" : who; }

        static string ComboName(string key)
        {
            var c = KenseiCombo.Of(key);
            return c.Part + "（" + WhoName(c.Who) + "）";
        }

        string PlayName(string id)
        {
            var row = host.Play.Row(id);
            return row != null ? Json.Str(row, "name", id) : id;
        }

        // the candidates of the settings now (kept a second: the room's plays are many)
        SortedDictionary<string, List<string>> KenseiCands()
        {
            var k = host.Kensei;
            string key = k.Range + "|" + host.Stage.Location;
            if (kenseiCands == null || key != kenseiCandKey || Time.realtimeSinceStartup - kenseiCandAt > 1f)
            {
                kenseiCands = k.Candidates(k.Range);
                kenseiUntried = k.Untried(k.Range).Count;
                kenseiCandKey = key;
                kenseiCandAt = Time.realtimeSinceStartup;
            }
            return kenseiCands;
        }

        int Cycle(List<int> choices, int now)
        {
            if (choices == null || choices.Count == 0) return now;
            int i = choices.IndexOf(now);
            return choices[(i + 1) % choices.Count];
        }

        void DrawKenseiMenu(float x, float y, float w, float s)
        {
            var k = host.Kensei;
            float lh = 26f * s, top = y, w2 = (w - 4f) / 2f, w3 = (w - 8f) / 3f;
            if (kenseiH > 0f) GUI.Box(new Rect(x - 8f * s, y - 2f * s, w + 16f * s, kenseiH + 8f * s), GUIContent.none, box);
            Buttons["コンテンツへ戻る"] = new Rect(x, y, w, lh);
            if (GUI.Button(new Rect(x, y, w, lh), "← コンテンツへ戻る", button)) { menu = "content"; return; }
            y += lh + 4f * s;
            GUI.Label(new Rect(x + 6f, y, w - 12f, lh), "献精室", small);
            y += lh;
            if (k.Pending != null)
            {
                string note = string.Format("前の献精が終わっていません（{0} に始めて、{1} 手まで）。続けるか、ここで終えるか（中断・報酬なし）を選んでください。",
                    Json.Str(k.Pending, "started", "").Replace("T", " "), (Json.Arr(k.Pending, "turns") ?? new List<object>()).Count);
                float nh = wrapSmall.CalcHeight(new GUIContent(note), w - 12f);
                GUI.Label(new Rect(x + 6f, y, w - 12f, nh), note, wrapSmall);
                y += nh + 4f * s;
                Buttons["献精：続ける"] = new Rect(x, y, w2, lh);
                Buttons["献精：終える"] = new Rect(x + w2 + 4f, y, w2, lh);
                if (GUI.Button(new Rect(x, y, w2, lh), "続ける", accentButton)) k.Resume();
                if (GUI.Button(new Rect(x + w2 + 4f, y, w2, lh), "ここで終える", button)) k.EndPending("interrupted");
                y += lh + 4f * s;
            }
            else if (k.Active)
            {
                var r = k.Run;
                GUI.Label(new Rect(x + 6f, y, w - 12f, lh), string.Format("献精中：{0} / {1} 手　累計 {2} / {3} ml", Math.Min(r.Turn, r.Limit), r.Limit, r.Total, r.Need), small);
                y += lh;
                const string about = "手を選ぶのは Muse です。ゲージの動きを見て、Muse の問いにチャットで答えてください。";
                float ah = wrapSmall.CalcHeight(new GUIContent(about), w - 12f);
                GUI.Label(new Rect(x + 6f, y, w - 12f, ah), about, wrapSmall);
                y += ah + 4f * s;
                if (kenseiStopArmed && Time.realtimeSinceStartup - kenseiStopAt > 4f) kenseiStopArmed = false;
                Buttons["献精：やめる"] = new Rect(x, y, w, lh);
                if (GUI.Button(new Rect(x, y, w, lh), kenseiStopArmed ? "もう一度押すと、やめます（報酬なし）" : "やめる（中断）", kenseiStopArmed ? warnButton : button))
                {
                    if (kenseiStopArmed) { k.Abandon(); kenseiStopArmed = false; }
                    else { kenseiStopArmed = true; kenseiStopAt = Time.realtimeSinceStartup; }
                }
                y += lh + 4f * s;
            }
            else
            {
                if (k.Run != null)
                {
                    string res = k.Run.Result == "success" ? "成功" : k.Run.Result == "failure" ? "失敗（時間切れ）" : "中断";
                    GUI.Label(new Rect(x + 6f, y, w - 12f, lh), string.Format("前回：{0}　累計 {1} ml　スコア {2}{3}", res, k.Run.Total, k.Score,
                        k.Title.Length > 0 ? "　称号「" + k.Title + "」" : ""), small);
                    y += lh;
                }
                // the settings (the next run's)
                Buttons["献精：奉仕だけ"] = new Rect(x, y, w2, lh);
                Buttons["献精：奉仕と挿入"] = new Rect(x + w2 + 4f, y, w2, lh);
                if (GUI.Toggle(new Rect(x, y, w2, lh), k.Range == "houshi", "奉仕だけ", button) && k.Range != "houshi") k.Range = "houshi";
                if (GUI.Toggle(new Rect(x + w2 + 4f, y, w2, lh), k.Range == "both", "奉仕と挿入", button) && k.Range != "both") k.Range = "both";
                y += lh + 4f * s;
                Buttons["献精：おまかせ"] = new Rect(x, y, w, lh);
                k.RangeRandom = GUI.Toggle(new Rect(x, y, w, lh), k.RangeRandom, k.RangeRandom ? "部位の範囲：おまかせ（難しくなる）" : "部位の範囲：全部", button);
                y += lh + 4f * s;
                if (GUI.Button(new Rect(x, y, w3, lh), "−", button)) k.WeakN = Math.Max(1, k.WeakN - 1);
                GUI.Label(new Rect(x + w3 + 4f, y, w3, lh), "弱点 " + k.WeakN + " 組", small);
                if (GUI.Button(new Rect(x + 2f * (w3 + 4f), y, w3, lh), "＋", button)) k.WeakN = Math.Min(k.Rules.WeakMax, k.WeakN + 1);
                y += lh + 4f * s;
                Buttons["献精：変動"] = new Rect(x, y, w2, lh);
                Buttons["献精：減衰"] = new Rect(x + w2 + 4f, y, w2, lh);
                k.Vary = GUI.Toggle(new Rect(x, y, w2, lh), k.Vary, k.Vary ? "弱点変動：あり" : "弱点変動：なし", button);
                k.DecayOn = GUI.Toggle(new Rect(x + w2 + 4f, y, w2, lh), k.DecayOn, k.DecayOn ? "減衰：あり" : "減衰：なし", button);
                y += lh + 4f * s;
                GUI.Label(new Rect(x + 6f, y, w - 12f, lh), "Muse に知らせる量", small);
                y += lh;
                foreach (var opt in new[] { new[] { "numbers", "数値" }, new[] { "levels", "段階" }, new[] { "none", "伏せる" } })
                {
                    int i = opt[0] == "numbers" ? 0 : opt[0] == "levels" ? 1 : 2;
                    Buttons["献精：開示" + opt[1]] = new Rect(x + i * (w3 + 4f), y, w3, lh);
                    if (GUI.Toggle(new Rect(x + i * (w3 + 4f), y, w3, lh), k.Disclose == opt[0], opt[1], button) && k.Disclose != opt[0]) k.Disclose = opt[0];
                }
                y += lh + 4f * s;
                foreach (var opt in new[] { new[] { "loose", "ゆったり" }, new[] { "standard", "標準" }, new[] { "tight", "きつめ" } })
                {
                    int i = opt[0] == "loose" ? 0 : opt[0] == "standard" ? 1 : 2;
                    Buttons["献精：" + opt[1]] = new Rect(x + i * (w3 + 4f), y, w3, lh);
                    if (GUI.Toggle(new Rect(x + i * (w3 + 4f), y, w3, lh), k.Tighten == opt[0], opt[1], button) && k.Tighten != opt[0]) k.Tighten = opt[0];
                }
                y += lh + 4f * s;
                Buttons["献精：必要量"] = new Rect(x, y, w2, lh);
                Buttons["献精：公差"] = new Rect(x + w2 + 4f, y, w2, lh);
                if (GUI.Button(new Rect(x, y, w2, lh), "必要量 " + k.Need + " ml", button)) k.Need = Cycle(k.Rules.NeedChoices, k.Need);
                if (GUI.Button(new Rect(x + w2 + 4f, y, w2, lh), "量の揺れ ±" + k.Tol + "%", button)) k.Tol = Cycle(k.Rules.TolChoices, k.Tol);
                y += lh + 6f * s;
                // what the settings make here: the candidates (the plays whose finish outside was tried and plays: #115, GPT's
                // post-review of #118, 5), the check of them, the limit and the coefficient
                var cands = KenseiCands();
                if (k.Scanning)
                {
                    GUI.Label(new Rect(x + 6f, y, w - 12f, lh), "プレイの射精の流れを確かめています（残り " + k.ScanLeft + " 件）", small);
                    y += lh;
                    Buttons["献精：確かめるのを止める"] = new Rect(x, y, w, lh);
                    if (GUI.Button(new Rect(x, y, w, lh), "止める", button)) k.StopScan();
                    y += lh + 4f * s;
                }
                else if (kenseiUntried > 0)
                {
                    string un = "この部屋のプレイのうち " + kenseiUntried + " 件は、射精（外）の流れをまだ確かめていません。確かめたプレイだけが候補になります（1 件 1 秒ほど。プレイが順に動きます）。";
                    float uh = wrapSmall.CalcHeight(new GUIContent(un), w - 12f);
                    GUI.Label(new Rect(x + 6f, y, w - 12f, uh), un, wrapSmall);
                    y += uh + 4f * s;
                    Buttons["献精：確かめる"] = new Rect(x, y, w, lh);
                    if (GUI.Button(new Rect(x, y, w, lh), "確かめる（" + kenseiUntried + " 件）", button)) { kenseiCandAt = -100f; MuseStudioPlugin.Instance.StartCoroutine(k.Scan(k.Range)); }
                    y += lh + 4f * s;
                }
                var names = new List<string>();
                var parts = new HashSet<string>();
                var leads = new HashSet<string>();
                foreach (var key in cands.Keys)
                {
                    names.Add(ComboName(key));
                    var c = KenseiCombo.Of(key);
                    parts.Add(c.Part);
                    leads.Add(c.Who);
                }
                string cl = "この部屋の候補 " + cands.Count + " 組" + (k.RangeRandom ? "（この中から、始めるときに範囲を選びます）" : "") + "：" + string.Join("、", names.ToArray());
                float ch = wrapSmall.CalcHeight(new GUIContent(cl), w - 12f);
                GUI.Label(new Rect(x + 6f, y, w - 12f, ch), cl, wrapSmall);
                y += ch + 4f * s;
                string why = k.CannotStart(cands.Count);
                if (why.Length == 0)
                {
                    int pts;
                    int lim = k.Rules.Limit(parts.Count, leads.Count, k.WeakN, k.Vary, k.Tighten, k.Need, k.Tol);
                    double coef = k.Rules.Coefficient(cands.Count, k.WeakN, k.Vary, k.Tighten, k.Need, k.Tol, k.Disclose, k.DecayOn, k.RangeRandom, out pts);
                    GUI.Label(new Rect(x + 6f, y, w - 12f, lh), string.Format("制限 {0} 手　難度係数 ×{1:0.0#}", lim, coef), small);
                    y += lh;
                }
                else
                {
                    string wt = Kensei.Why(why);
                    float wh = wrapSmall.CalcHeight(new GUIContent(wt), w - 12f);
                    GUI.Label(new Rect(x + 6f, y, w - 12f, wh), wt, warn);
                    y += wh + 4f * s;
                }
                Buttons["献精：始める"] = new Rect(x, y, w, lh);
                GUI.enabled = why.Length == 0;
                if (GUI.Button(new Rect(x, y, w, lh), "始める", accentButton)) { k.Start(); menu = ""; }
                GUI.enabled = true;
                y += lh + 4f * s;
            }
            if (k.Error.Length > 0)
            {
                float eh = wrapSmall.CalcHeight(new GUIContent(k.Error), w - 12f);
                GUI.Label(new Rect(x + 6f, y, w - 12f, eh), k.Error, warn);
                y += eh + 4f * s;
            }
            Buttons["献精：記録"] = new Rect(x, y, w, lh);
            if (GUI.Button(new Rect(x, y, w, lh), "記録を見る", button)) { menu = "kensei_records"; kenseiFiles = null; kenseiRec = null; kenseiPage = 0; }
            y += lh + 6f * s;
            kenseiH = y - top;
            drawing.Add(new Rect(x, top, w, kenseiH + 4f));
        }

        // ---- the records: the runs, then one run's turns in one of three views ----

        public string KenseiRecordShown = "";     // (checks) the record view's text

        void DrawKenseiRecords(Rect r, float s)
        {
            var k = host.Kensei;
            float x = r.x, y = r.y, w = r.width, lh = 26f * s, top = y, w3 = (w - 8f) / 3f;
            if (kenseiRecH > 0f) GUI.Box(new Rect(x - 8f * s, y - 2f * s, w + 16f * s, kenseiRecH + 8f * s), GUIContent.none, box);
            Buttons["献精へ戻る"] = new Rect(x, y, w, lh);
            if (GUI.Button(new Rect(x, y, w, lh), kenseiRec != null ? "← 記録の一覧へ" : "← 献精へ戻る", button))
            {
                if (kenseiRec != null) kenseiRec = null; else menu = "kensei";
                return;
            }
            y += lh + 4f * s;
            if (kenseiRec == null)
            {
                if (kenseiFiles == null) kenseiFiles = k.RecordFiles();
                var scores = k.Scores();
                GUI.Label(new Rect(x + 6f, y, w - 12f, lh), kenseiFiles.Count == 0 ? "献精の記録はまだありません" : "献精の記録（" + kenseiFiles.Count + " 件）", small);
                y += lh;
                const int per = 6;
                for (int i = kenseiPage * per; i < Math.Min(kenseiFiles.Count, (kenseiPage + 1) * per); i++)
                {
                    string f = kenseiFiles[i];
                    string id = System.IO.Path.GetFileNameWithoutExtension(f).Substring("kensei-".Length);
                    var sc = Json.Obj(scores, id);
                    string label = id.Substring(1, Math.Min(12, id.Length - 1)) + (sc != null ? "　" + Json.Str(sc, "result", "") + "　" + Json.Num(sc, "score") : "　（続き）");
                    Buttons["献精の記録" + i] = new Rect(x, y, w, lh);
                    if (GUI.Button(new Rect(x, y, w, lh), label, button)) { kenseiRec = Kensei.ReadRecord(f); kenseiScroll = Vector2.zero; }
                    y += lh + 2f * s;
                }
                if (kenseiFiles.Count > per)
                {
                    if (GUI.Button(new Rect(x, y, w / 2f - 4f, lh), "前へ", button)) kenseiPage = Math.Max(0, kenseiPage - 1);
                    if (GUI.Button(new Rect(x + w / 2f + 4f, y, w / 2f - 4f, lh), "次へ", button)) kenseiPage = Math.Min((kenseiFiles.Count - 1) / per, kenseiPage + 1);
                    y += lh + 4f * s;
                }
                KenseiRecordShown = "";
            }
            else
            {
                var rec = kenseiRec;
                var st = Json.Obj(rec, "settings");
                GUI.Label(new Rect(x + 6f, y, w - 12f, lh), string.Format("{0}　{1}　累計 {2} / {3} ml　スコア {4}", Json.Str(rec, "started", "").Replace("T", " "),
                    Json.Str(rec, "result", "") == "" ? "（続き）" : Json.Str(rec, "result", ""), LastTotal(rec), Json.Num(st, "need"), Json.Num(rec, "score")), small);
                y += lh;
                foreach (var opt in new[] { new[] { "user", "あなたの画面" }, new[] { "muse", "Muse に知らせたもの" }, new[] { "truth", "答え" } })
                {
                    int i = opt[0] == "user" ? 0 : opt[0] == "muse" ? 1 : 2;
                    Buttons["献精の見方：" + opt[1]] = new Rect(x + i * (w3 + 4f), y, w3, lh);
                    GUI.enabled = opt[0] != "truth" || Json.Str(rec, "result", "").Length > 0;      // (the answer: after the end)
                    if (GUI.Toggle(new Rect(x + i * (w3 + 4f), y, w3, lh), kenseiView == opt[0], opt[1], button) && kenseiView != opt[0]) kenseiView = opt[0];
                    GUI.enabled = true;
                }
                if (kenseiView == "truth" && Json.Str(rec, "result", "").Length == 0) kenseiView = "user";
                y += lh + 4f * s;
                var lines = new List<string>();
                if (kenseiView == "truth")
                    foreach (var p in Json.Arr(rec, "phases") ?? new List<object>())
                    {
                        var ws = new List<string>();
                        foreach (var c in Json.Arr(p, "weak") ?? new List<object>()) ws.Add(ComboName((string)c));
                        lines.Add(string.Format("{0} 手目から：弱点 {1}", Json.Num(p, "from_turn"), string.Join("・", ws.ToArray())));
                    }
                foreach (var o in Json.Arr(rec, "turns") ?? new List<object>()) lines.Add(TurnLine(o as Dictionary<string, object>));
                string body = string.Join("\n", lines.ToArray());
                KenseiRecordShown = body;
                float bh = wrapSmall.CalcHeight(new GUIContent(body), w - 28f);
                float vh = Mathf.Max(lh * 3f, r.height - (y - top) - 8f * s);
                kenseiScroll = GUI.BeginScrollView(new Rect(x, y, w, vh), kenseiScroll, new Rect(0f, 0f, w - 20f, bh));
                GUI.Label(new Rect(0f, 0f, w - 20f, bh), body, wrapSmall);
                GUI.EndScrollView();
                y += vh + 4f * s;
            }
            kenseiRecH = y - top;
            drawing.Add(new Rect(x, top, w, kenseiRecH + 4f));
        }

        static double LastTotal(Dictionary<string, object> rec)
        {
            var t = Json.Arr(rec, "turns");
            return t != null && t.Count > 0 ? Json.Num(t[t.Count - 1], "total") : 0;
        }

        // one turn of a record in the view chosen
        string TurnLine(Dictionary<string, object> t)
        {
            if (t == null) return "";
            string line = "";
            foreach (var b in Json.Arr(Json.Obj(t, "package"), "blocks") ?? new List<object>())
                if (Json.Obj(b, "move") != null) line = BlockPlayer.Shown(Json.Str(b, "text", ""));
            var ans = Json.Obj(t, "answer");
            string a = ans == null ? "（未転記）" : string.Join(" ", new[] { Json.Str(ans, "tap", ""), Json.Str(ans, "text", "") }).Trim();
            int turn = (int)Json.Num(t, "turn");
            string play = PlayName(Json.Str(t, "play", ""));
            bool came = Json.Has(t, "came") && t["came"] is bool && (bool)t["came"];
            if (kenseiView == "user")
                return string.Format("{0}. {1}　+{2}（ゲージ {3}→{4}）{5}\n　「{6}」　答え：{7}", turn, play, Json.Num(t, "rise"), Json.Num(t, "gauge_before"),
                    came ? Json.Num(t, "gauge_before") + Json.Num(t, "rise") : Json.Num(t, "gauge_after"), came ? "　射精 " + Json.Num(t, "amount") + " ml" : "", line, a);
            if (kenseiView == "muse")
            {
                var told = Json.Obj(t, "told");
                string more = "";
                if (told != null && Json.Has(told, "amount")) more += "　量 " + Json.Num(told, "amount") + " ml";
                if (told != null && Json.Has(told, "total")) more += "　累計 " + Json.Num(told, "total") + " ml";
                if (told != null && Json.Obj(told, "progress") != null) more += "　進み「" + Json.Str(Json.Obj(told, "progress"), "name", "") + "」";
                return string.Format("{0}. {1}　{2}{3}\n　答え：{4}", turn, play, came ? "射精した" : "―", more, a);
            }
            string grade = Json.Str(t, "grade", "");
            string gn = grade == "big" ? "大弱点" : grade == "near" ? "近傍" : "非弱点";
            return string.Format("{0}. {1}［{2}］　{3}　+{4}{5}{6}", turn, play, ComboName(Json.Str(t, "combo", "/")), gn, Json.Num(t, "rise"),
                Json.Num(t, "decay_uses") > 0 ? "（減衰 " + Json.Num(t, "decay_uses") + " 回）" : "",
                came ? "　射精 " + Json.Num(t, "amount") + " ml（うち超過 " + Json.Num(t, "overkill") + "）" : "");
        }

        // ---- his gauge over the screen while a run goes (and a while after it) ----

        void DrawKenseiHud(float s)
        {
            var k = host.Kensei;
            if (k == null || k.Run == null || (!k.Active && Time.realtimeSinceStartup - k.EndedAt > 20f)) { KenseiShown = ""; return; }
            var r = k.Run;
            float w = 340f * s, lh = 22f * s, x = (Screen.width - w) / 2f, y = 10f * s;
            var last = k.Last;
            var lines = new List<string>();
            lines.Add(string.Format("献精室　{0} / {1} 手　累計 {2} / {3} ml", Math.Min(r.Turn, r.Limit), r.Limit, r.Total, r.Need));
            if (last != null)
                lines.Add(last.Came ? string.Format("前の手：+{0}　満ちて射精 {1} ml", last.Rise, last.Amount) : string.Format("前の手：+{0}", last.Rise));
            if (!k.Active) lines.Add(r.Result == "success" ? "成功！ スコア " + k.Score + (k.Title.Length > 0 ? "　称号「" + k.Title + "」" : "") : r.Result == "failure" ? "時間切れ（失敗）" : "中断しました");
            float h = lh * (lines.Count + 1) + 12f * s;
            GUI.Box(new Rect(x - 6f * s, y - 4f * s, w + 12f * s, h), GUIContent.none, box);
            GUI.Label(new Rect(x, y, w, lh), lines[0], small);
            y += lh;
            // the gauge: its fill is his gauge now (out of the threshold)
            float frac = Mathf.Clamp01(r.Gauge / (float)Math.Max(1, k.Rules.Threshold));
            var bar = new Rect(x, y + 4f * s, w, lh - 8f * s);
            var was = GUI.color;
            GUI.color = new Color(0.25f, 0.25f, 0.3f, 0.9f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = new Color(0.95f, 0.45f, 0.6f, 0.95f);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * frac, bar.height), Texture2D.whiteTexture);
            GUI.color = was;
            y += lh;
            for (int i = 1; i < lines.Count; i++) { GUI.Label(new Rect(x, y, w, lh), lines[i], small); y += lh; }
            KenseiShown = string.Join(" | ", lines.ToArray()) + " | gauge " + r.Gauge;
            drawing.Add(new Rect(x - 6f * s, 6f * s, w + 12f * s, h));
        }
    }
}
