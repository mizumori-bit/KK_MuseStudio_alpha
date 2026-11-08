using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MuseStudio
{
    // MuseStudio's own UI (IMGUI): the dialogue box at the bottom (name tag, text; a sound block in its own style), and a
    // small panel at the top right (bridge status, location and play menus, the play's pace). Click the box / Enter / Space:
    // next block. At the start, the character chooser (the cards with their faces and registered personas; it waits until
    // one is chosen); the registered personas' editor (numbered slots) from there or the character menu.
    public class Ui
    {
        readonly MuseStudioPlugin host;
        GUIStyle text, sound, nameTag, box, small, button, waitLine, marker, title, field, warn, warnButton, accentButton, wrapSmall, hintBox, openButton;
        Texture2D boxTex, lineTex, dimTex, pickTex;
        GUIStyle dialogBox;
        float styledFor = -1f;
        string menu = "";             // "", "location", "play"
        string playGroup = "sonyu";
        bool bodyList;                         // the play menu's list of your bodies
        string bodyNameFor, bodyName = "標準の体";
        List<KeyValuePair<string, string>> bodyCards;
        string pickPlay;              // the play whose spots are listed
        bool pickReverse, pickAnal;   // 攻守交代 / アナル for the picked play
        Vector2 scroll;
        List<Rect> rects = new List<Rect>(), drawing = new List<Rect>();   // where the UI is (a press there is not a touch)
        // the play menu's tabs, a second row of the group's categories (PlayCtl.Category), and the added plays (AnimationLoader's) on or off
        static readonly string[][] Groups = { new[] { "aibu", "愛撫" }, new[] { "houshi", "奉仕" }, new[] { "sonyu", "挿入" },
                                             new[] { "masturbation", "自慰" }, new[] { "lesbian", "レズ" } };
        string playCat = "";            // (the category picked: "" all)
        bool withAdded = true;

        // a tab's plays: the original's first, then the added ones (AnimationLoader's: source al) when they are on; of the category
        List<Dictionary<string, object>> TabPlays(string tab)
        {
            var o = new List<Dictionary<string, object>>();
            foreach (bool added in new[] { false, true })
            {
                if (added && !withAdded) continue;
                foreach (var p in host.Play.List(tab))
                    if ((Json.Str(p, "source", "") == "al") == added && (playCat.Length == 0 || host.Play.Category(p) == playCat)) o.Add(p);
            }
            return o;
        }

        public Ui(MuseStudioPlugin host) { this.host = host; }

        // Muse's panel (the launcher's --app window over the game) and its bridge bar take a strip of the screen: the right
        // one by default (the user's request of 2026-11-04: by the mouse hand), the left one before. The dialogue box keeps out
        // of it, and the panel's column (キャラ / 設定 / 場所 ...) and the presence button go to the other side. The side and the
        // width: the launcher's settings (launcher/panel_config.json panel.side / width), else stage.json ui.panel_side /
        // reserve_px.
        bool? panelRight;
        float panelReserve = 420f;

        // The part of the picture nothing of ours lies over, in viewport terms (0..1, y up): right of the column, left of Muse's
        // panel (when it is on the right; on the left the column takes the right side), above the dialogue box's usual height,
        // under the keys' hint. The play camera frames her in it.
        public Rect FreeView
        {
            get
            {
                float s = Scale, W = Mathf.Max(1f, Screen.width), col = (300f + 28f) * s / W, panel = (MuseStudioPlugin.Args.ContainsKey("check") ? 0f : panelReserve) / W;
                bool pr = PanelRight;
                float x0 = pr ? col : panel, x1 = 1f - (pr ? panel : col);
                float y0 = Mathf.Clamp01((float)Json.Num(Json.Obj(host.Stage.Cfg, "ui"), "dialogue_share", 0.2)), y1 = 0.95f;
                if (x1 - x0 < 0.3f) { x0 = 0f; x1 = 1f; }
                return new Rect(x0, y0, x1 - x0, y1 - y0);
            }
        }

        public bool PanelRight
        {
            get
            {
                if (panelRight == null)
                {
                    var ui = Json.Obj(host.Stage.Cfg, "ui");
                    string side = Json.Str(ui, "panel_side", "auto");
                    panelReserve = (float)Json.Num(ui, "reserve_px", 420.0);
                    if (side == "auto")
                    {
                        side = "right";
                        try
                        {
                            string f = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(MuseStudioPlugin.DataDir.TrimEnd('\\', '/'))), Path.Combine("launcher", "panel_config.json"));
                            var p = Json.Obj(Json.Parse(File.ReadAllText(f, System.Text.Encoding.UTF8)), "panel");
                            side = Json.Str(p, "side", Json.Has(p, "left") ? "left" : "right");
                            panelReserve = (float)Json.Num(p, "width", 400.0) + 20f;
                        }
                        catch (Exception) { }
                    }
                    panelRight = side != "left";
                }
                return panelRight.Value;
            }
        }

        float columnH;                // the panel's column, as tall as it was drawn last

        // an x laid out from the left edge, on the side away from the panel
        float Away(float x, float w) { return PanelRight ? Screen.width - x - w : x; }

        float Scale { get { return Mathf.Max(0.6f, Screen.height / 900f); } }

        void Styles()
        {
            if (Mathf.Approximately(styledFor, Scale)) return;
            styledFor = Scale;
            // (the look: Theme - one skin for MuseStudio's own screens)
            var sk = Theme.Skin(Scale);
            int fs = Mathf.RoundToInt(26 * Scale);
            boxTex = Theme.Solid(Theme.Ink);
            lineTex = Theme.Solid(new Color(Theme.Rose.r, Theme.Rose.g, Theme.Rose.b, 0.75f));
            dialogBox = Theme.Box(sk.box);
            text = new GUIStyle(sk.label) { fontSize = fs, wordWrap = true, richText = false, alignment = TextAnchor.UpperLeft };
            text.normal.textColor = Theme.Text;
            sound = new GUIStyle(text) { fontStyle = FontStyle.Italic, alignment = TextAnchor.UpperCenter, fontSize = Mathf.RoundToInt(24 * Scale) };
            sound.normal.textColor = Theme.Sound;
            nameTag = new GUIStyle(sk.label) { fontSize = Mathf.RoundToInt(20 * Scale), alignment = TextAnchor.MiddleCenter, border = new RectOffset(7, 7, 7, 7) };
            nameTag.normal.textColor = Color.white;
            nameTag.normal.background = Theme.Rounded(new Color(Theme.Rose.r * 0.82f, Theme.Rose.g * 0.82f, Theme.Rose.b * 0.82f, 0.96f), Theme.RoseSoft);
            box = Theme.Box(sk.box);
            waitLine = new GUIStyle(sk.label) { fontSize = Mathf.RoundToInt(16 * Scale) };
            waitLine.normal.textColor = Theme.Faint;
            marker = new GUIStyle(sk.label) { fontSize = Mathf.RoundToInt(18 * Scale) };
            marker.normal.textColor = Theme.RoseSoft;
            small = new GUIStyle(sk.label) { fontSize = Mathf.RoundToInt(14 * Scale), wordWrap = false };
            small.normal.textColor = Theme.Text;
            warn = new GUIStyle(small) { wordWrap = true };
            wrapSmall = new GUIStyle(small) { wordWrap = true };
            hintBox = new GUIStyle(Theme.Box(sk.box));
            hintBox.normal.background = Theme.Rounded(new Color(Theme.Ink.r, Theme.Ink.g, Theme.Ink.b, 0.55f), new Color(0f, 0f, 0f, 0f), 5, 0f);
            warn.normal.textColor = Theme.Warn;
            button = new GUIStyle(sk.button);
            warnButton = Theme.WarnButton(button);
            accentButton = Theme.AccentButton(button);
            openButton = Theme.OpenButton(button);
            title = new GUIStyle(sk.label) { fontSize = Mathf.RoundToInt(24 * Scale), alignment = TextAnchor.MiddleLeft };
            title.normal.textColor = Theme.Text;
            field = new GUIStyle(sk.textField);
            dimTex = Theme.Solid(Theme.Deep);
            pickTex = Theme.Solid(Theme.RoseSoft);
        }

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // a text field has the keyboard (the editor's names and numbers): the keys are its, not the stage's
        public static bool Typing { get { return GUIUtility.keyboardControl != 0; } }

        public bool Modal { get { return editPersonas || editIdentities; } }
        public Rect MenuRect;           // (checks) the open menu's rectangle (height 0: none)
        internal void OpenListForCheck(string which) { editIdentities = which == "identities"; editPersonas = which == "personas"; }
        internal string ListOpen { get { return editIdentities ? "identities" : editPersonas ? "personas" : ""; } }

        // (checks) where the panel's buttons are now (drawn this frame): the checks click them as one does
        public readonly Dictionary<string, Rect> Buttons = new Dictionary<string, Rect>();

        public bool ShotMode;          // (the photo probe: nothing of the app's on the screen)

        public void Draw()
        {
            var stage = host.Stage;
            if (stage == null || ShotMode) return;
            if (host.Launch != null && host.Launch.Choosing)
            {
                Styles();
                GUI.skin = Theme.Skin(Scale);
                drawing = new List<Rect>();
                DrawChooser(Scale);
                if (LinkKind.Length > 0) DrawLinkPicker(Scale);
                if (editPersonas) DrawPersonaEditor(Scale);
                if (editIdentities) DrawIdentityEditor(Scale);
                rects = drawing;
                return;
            }
            if (!stage.Ready) return;
            Styles();
            GUI.skin = Theme.Skin(Scale);         // (this OnGUI's only: the other plugins' windows keep the default)
            float s = Scale;
            drawing = new List<Rect>();
            Buttons.Clear();
            MismatchShown = "";
            if (host.Match.Active && host.MatchBroadcast.Value) DrawBroadcast(s);
            DrawDialogue(s);
            if (!UiHidden)
            {
                if (host.Notices != null) host.Notices.Draw(s, small, box, drawing);     // (#70 adopted, 3: the app's technical notices)
                DrawPanel(s);
                DrawPresence(s);
                DrawClock(s);
                DrawKeyHints(s);
            }
            else { KeyHintsNow = ""; ClockShown = ""; }
            DrawHideButton(s);
            if (host.SpotEditor != null) host.SpotEditor.DrawGui(s, box, button, small, drawing);
            if (LinkKind.Length > 0) DrawLinkPicker(s);
            if (editPersonas) DrawPersonaEditor(s);
            if (editIdentities) DrawIdentityEditor(s);
            rects = drawing;
        }

        // the UI hidden (the user's request, PR #11: one key - 画面の UI を隠す・出すキー, F3 - or this button at the menu's top
        // right): the menu's column, the key hints and the presence out of the way; the words' box and this button stay
        public bool UiHidden;

        void DrawHideButton(float s)
        {
            float w = 300f * s, x = PanelRight ? 12f * s : Screen.width - w - 12f * s;
            var r = new Rect(x + w - 76f * s, 12f * s, 76f * s, 24f * s);
            Buttons["UI"] = r;
            drawing.Add(r);
            if (GUI.Button(r, UiHidden ? "UI を出す" : "UI を隠す", UiHidden ? openButton : button)) UiHidden = !UiHidden;
        }

        // the keys one can press now (the setting キー操作を画面に出す, default on; the user's note of 2026-11-04): small, at the top
        // between the column and the presence, by the keys as set
        public string KeyHintsNow = "";        // (checks)

        void DrawKeyHints(float s)
        {
            KeyHintsNow = "";
            if (!host.KeyHints.Value || (host.Match.Active && host.MatchBroadcast.Value)) return;
            // (F1, F2, F3 in order - the user's note of 2026-11-05; the keys as they are set)
            var parts = new List<string> { "Space／Enter：送る", "F1：設定", Key(host.StudioUiKey.Value) + "：Studio の画面部品", Key(host.HideUiKey.Value) + "：UI を隠す" };
            var pc = host.Play;
            if (pc != null && pc.Active)
            {
                parts.Add(Key(host.BodyShownKey.Value) + "／" + Key(host.PenisShownKey.Value) + "／" + Key(host.SilhouetteKey.Value) + "／" + Key(host.TranslucentKey.Value) + "：あなたの体・ペニス・影・透過");
                if (host.Hands.OnNow) parts.Add(Key(host.HitShapesKey.Value) + "：当たりの形");
            }
            var k = host.CamKeys;
            if (pc != null && pc.Active && pc.View == "pov")
                parts.Add("目線：ドラッグ・" + Key(k["turn_left"].Value) + Key(k["turn_right"].Value) + Key(k["turn_up"].Value) + Key(k["turn_down"].Value)
                    + "（頭の向き）・" + Key(k["turn_reset"].Value) + "（正面へ）");
            else parts.Add("カメラ：ドラッグ・ホイール・" + Key(k["move_forward"].Value) + Key(k["move_back"].Value) + Key(k["move_left"].Value) + Key(k["move_right"].Value)
                + "（移動）・" + Key(k["turn_left"].Value) + Key(k["turn_right"].Value) + Key(k["turn_up"].Value) + Key(k["turn_down"].Value) + "（回転）");
            KeyHintsNow = string.Join("　", parts.ToArray());
            float left = PanelRight ? 330f * s : 180f * s, right = PanelRight ? 180f * s : 330f * s;
            var r = new Rect(left, 10f * s, Screen.width - left - right, 22f * s);
            var st = new GUIStyle(wrapSmall) { fontSize = Mathf.RoundToInt(12 * s), alignment = TextAnchor.UpperCenter };
            st.normal.textColor = Theme.Faint;
            float hh = st.CalcHeight(new GUIContent(KeyHintsNow), r.width) + 6f * s;
            r.height = hh;
            GUI.Box(new Rect(r.x - 6f * s, r.y - 3f * s, r.width + 12f * s, hh + 2f * s), GUIContent.none, hintBox);
            GUI.Label(r, KeyHintsNow, st);
        }

        static string Key(BepInEx.Configuration.KeyboardShortcut k)
        {
            string t = k.ToString();
            if (t.Length == 0 || t == "Not set") return "―";
            return t.Replace("UpArrow", "↑").Replace("DownArrow", "↓").Replace("LeftArrow", "←").Replace("RightArrow", "→").Replace("Keypad", "テンキー");
        }

        // ---- the match's broadcast view (Muse's request of 2026-11-04, a plaything; each user's own, told to no one): a LIVE
        // badge with the time on air, the score as a telop (the names, the declared climaxes against the limit, the phase and
        // its turns), a flash when a climax is declared; only what the app has of the match ----
        Texture2D telopEdge, flashTex;
        GUIStyle telop, telopBig, liveStyle, liveBox, telopBox;
        float onAirFrom = -1f;

        void BroadcastStyles(float s)
        {
            if (liveBox == null)
            {
                liveBox = new GUIStyle(Theme.Box(GUI.skin.box)) { padding = new RectOffset(0, 0, 0, 0) };
                liveBox.normal.background = Theme.Rounded(Theme.Live, Theme.Live, 5, 0f);
                telopBox = new GUIStyle(Theme.Box(GUI.skin.box)) { padding = new RectOffset(0, 0, 0, 0) };
                telopEdge = Theme.Solid(new Color(Theme.Rose.r, Theme.Rose.g, Theme.Rose.b, 0.9f));
                flashTex = Theme.Solid(Theme.RoseSoft);
            }
            telop = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(17 * s), alignment = TextAnchor.MiddleCenter, wordWrap = false };
            telop.normal.textColor = Color.white;
            telopBig = new GUIStyle(telop) { fontSize = Mathf.RoundToInt(30 * s), fontStyle = FontStyle.Bold };
            liveStyle = new GUIStyle(telop) { fontSize = Mathf.RoundToInt(16 * s), fontStyle = FontStyle.Bold };
        }

        void DrawBroadcast(float s)
        {
            BroadcastStyles(s);
            var mt = host.Match;
            if (onAirFrom < 0f) onAirFrom = Time.realtimeSinceStartup;
            // LIVE, blinking dot, the time on air
            float x = Away(170f * s, 190f * s), y = 12f * s;
            bool dot = Mathf.Repeat(Time.realtimeSinceStartup, 1.2f) < 0.8f;
            var live = new Rect(x, y, 92f * s, 28f * s);
            GUI.Box(live, GUIContent.none, liveBox);
            GUI.Label(live, (dot ? "● " : "　") + "LIVE", liveStyle);
            int secs = (int)(Time.realtimeSinceStartup - onAirFrom);
            GUI.Label(new Rect(x + 98f * s, y, 90f * s, 28f * s), string.Format("{0:00}:{1:00}", secs / 60, secs % 60), liveStyle);
            // the score telop at the top centre
            string hn = mt.Side == "host" ? host.Stage.Name : mt.OpponentName, gn = mt.Side == "guest" ? host.Stage.Name : mt.OpponentName;
            int lim = mt.Limit("climax");
            float w = Mathf.Min(720f * s, Screen.width - 2f * (300f * s + 30f * s)), h0 = 46f * s, tx = (Screen.width - w) * 0.5f, ty = 12f * s;
            if (w < 360f * s) { w = 360f * s; tx = (Screen.width - w) * 0.5f; ty = 48f * s; }
            bool flash = Time.realtimeSinceStartup - mt.TallyAt < 1.2f && Mathf.Repeat(Time.realtimeSinceStartup, 0.3f) < 0.15f;
            var bar = new Rect(tx, ty, w, h0);
            GUI.Box(bar, GUIContent.none, telopBox);
            GUI.DrawTexture(new Rect(tx + 10f * s, ty + h0 - 3f * s, w - 20f * s, 2f * s), flash ? flashTex : telopEdge);
            float third = w / 3f;
            GUI.Label(new Rect(tx, ty, third, h0), hn, telop);
            GUI.Label(new Rect(tx + third, ty, third, h0), Pips(mt.Tally["host"], lim) + "  " + mt.Tally["host"] + " - " + mt.Tally["guest"] + "  " + Pips(mt.Tally["guest"], lim), telopBig);
            GUI.Label(new Rect(tx + 2f * third, ty, third, h0), gn, telop);
            // the phase and its turns under it
            string ph = mt.Phase == "intro" ? "自己紹介" : mt.Phase == "negotiation" ? "協議" : mt.Phase == "sex" ? "本番" : "試合終了";
            string turns = mt.Phase == "negotiation" ? string.Format("　{0}・{1}／{2} ターン", mt.Turns["host"], mt.Turns["guest"], mt.Limit("negotiation_turns"))
                : mt.Phase == "sex" ? string.Format("　{0}／{1} ターン", mt.PhaseTurns, mt.Limit("sex_turns")) : "";
            string res = mt.Result == null ? "" : (Json.Has(mt.Result, "draw") && (bool)mt.Result["draw"]) ? "　引き分け"
                : "　勝者 " + (Json.Str(mt.Result, "winner") == "host" ? hn : gn);
            var sub = new Rect(tx + w * 0.2f, ty + h0, w * 0.6f, 26f * s);
            GUI.Box(sub, GUIContent.none, telopBox);
            GUI.Label(sub, ph + turns + res, telop);
            drawing.Add(new Rect(tx, ty, w, h0 + 26f * s));
            drawing.Add(new Rect(x, y, 190f * s, 28f * s));
        }

        static string Pips(int n, int lim)
        {
            var o = new System.Text.StringBuilder();
            for (int i = 0; i < lim; i++) o.Append(i < n ? "●" : "○");
            return o.ToString();
        }

        // the user's presence (在席 / 離席) at the top left: what reaches her is what she can know (Presence)
        void DrawPresence(float s)
        {
            var pr = host.Presence;
            var r = new Rect(Away(12f * s, 150f * s), 12f * s, 150f * s, 30f * s);
            drawing.Add(r);
            bool present = GUI.Toggle(r, pr.Present, pr.Present ? "● 在席" : "○ 離席中（押して在席）", button);
            if (present != pr.Present) pr.Toggle(present);
        }

        // the clock (PR #46 §8, the owner's decision: on / off only - the setting 時計を出す, default on): this PC's time, small,
        // under the presence; hidden with the UI
        public string ClockShown = "";     // (checks) the time shown now, "" when none
        GUIStyle clock;

        void DrawClock(float s)
        {
            ClockShown = "";
            if (host.ShowClock == null || !host.ShowClock.Value) return;
            if (clock == null || !Mathf.Approximately(clockFor, s))
            {
                clockFor = s;
                // (no padding, no clipping: the box's own padding left the digits' lower part cut off - the user's note of
                // 2026-11-07)
                clock = new GUIStyle(box) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(18 * s),
                    padding = new RectOffset(0, 0, 0, 0), clipping = TextClipping.Overflow };
            }
            var r = new Rect(Away(12f * s, 150f * s), 46f * s, 150f * s, 30f * s);
            ClockShown = DateTime.Now.ToString("HH:mm");
            drawing.Add(r);
            GUI.Box(r, ClockShown, clock);
        }

        float clockFor = -1f;

        // ---- the chooser at the start (it waits: no character until one is chosen) ----

        List<KeyValuePair<string, string>> chooserCards;
        string picked;
        Vector2 chooserScroll;
        readonly Dictionary<string, Texture2D> faces = new Dictionary<string, Texture2D>();
        public bool ChooserDrawn;     // (checks)

        Texture2D Face(string path, ref int budget)
        {
            Texture2D t;
            if (faces.TryGetValue(path, out t)) return t;
            if (budget <= 0) return null;           // a few per frame (the first frame stays quick)
            budget--;
            t = new Texture2D(2, 2);
            try { t.LoadImage(System.IO.File.ReadAllBytes(path)); } catch (Exception) { t = null; }
            faces[path] = t;
            return t;
        }

        static string Id(string path) { return "card:" + System.IO.Path.GetFileName(path); }

        string SlotLabel(int no) { return no <= 0 ? "選択なし（元の性格）" : no + " " + (host.Personas.NameOf(no) ?? "（登録に無い番号）"); }

        string PersonLabel(string cardId)
        {
            var pe = host.Personas.PersonOf(host.Personas.PersonNo(cardId));
            return "人物：" + (pe == null ? "本人（Muse）" : pe.No + " " + pe.Name);
        }

        // the card's persona: its own, or its person's ("人物に従う")
        string PersonaLabel(string cardId)
        {
            var pz = host.Personas.Of(cardId);
            string now = pz == null ? "選択なし（元の性格）" : Convert.ToInt32(pz["no"]) + " " + Json.Str(pz, "name", "");
            return "登録性格：" + (host.Personas.OwnPersona(cardId) < 0 ? "人物に従う（" + now + "）" : now);
        }

        // ---- a card's links (the user's note of 2026-11-04: the cycling buttons were not intuitive): a picked card shows two
        // buttons over its picture - ID 設定 (its person) and 性格設定 (its registered persona); each opens a list to link it
        // with one (current marked); the registering itself stays in 人物と登録性格の編集 ----
        public string LinkKind = "";          // "person" / "persona" while the list is open (checks)
        string linkCard = "";

        public void OpenLink(string cardPath, string kind) { linkCard = cardPath; LinkKind = kind; }

        // (checks) a card picked on the chooser / in the character menu, a menu opened
        public void PickForCheck(string cardPath) { picked = cardPath; menuPick = cardPath; }
        public void MenuForCheck(string m) { menu = m; }

        // a choice: person - 0 Muse herself, else a person's number; persona - -1 its person's, 0 none, else a slot
        public void ChooseLink(int value)
        {
            if (linkCard.Length == 0) return;
            string id = Id(linkCard);
            if (LinkKind == "person") host.AssignPerson(id, value);
            else if (LinkKind == "persona") host.SetCardPersona(id, value);
            LinkKind = "";
        }

        // the two buttons over a card's picture (its bottom), what it is linked to above them
        void DrawLinkButtons(Rect pic, string cardPath, float s)
        {
            string pid = Id(cardPath);
            float bh = 26f * s, pad = 4f * s, w2 = (pic.width - 3f * pad) / 2f;
            var info = new Rect(pic.x, pic.yMax - bh - pad - 40f * s, pic.width, 38f * s);
            GUI.Box(info, GUIContent.none, hintBox);
            var st = new GUIStyle(wrapSmall) { fontSize = Mathf.RoundToInt(11 * s), alignment = TextAnchor.MiddleCenter };
            GUI.Label(info, PersonLabel(pid) + "\n" + PersonaLabel(pid), st);
            bool can = LinkKind.Length == 0 && !Modal;
            GUI.enabled = can;
            Buttons["ID 設定"] = new Rect(pic.x + pad, pic.yMax - bh - pad, w2, bh);
            Buttons["性格設定"] = new Rect(pic.x + 2f * pad + w2, pic.yMax - bh - pad, w2, bh);
            if (GUI.Button(Buttons["ID 設定"], "ID 設定", accentButton)) OpenLink(cardPath, "person");
            if (GUI.Button(Buttons["性格設定"], "性格設定", accentButton)) OpenLink(cardPath, "persona");
            GUI.enabled = true;
        }

        Vector2 linkScroll;

        void DrawLinkPicker(float s)
        {
            float w = 460f * s, hh = Mathf.Min(Screen.height - 40f, 520f * s), lh = 28f * s;
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - hh) / 2f, w, hh);
            drawing.Add(r);
            GUI.DrawTexture(r, dimTex);
            GUI.Box(r, GUIContent.none, dialogBox);
            string id = Id(linkCard), name = System.IO.Path.GetFileNameWithoutExtension(linkCard);
            if (cards != null) foreach (var c in cards) if (c.Key == linkCard) name = c.Value;
            if (chooserCards != null) foreach (var c in chooserCards) if (c.Key == linkCard) name = c.Value;
            GUILayout.BeginArea(new Rect(r.x + 16f * s, r.y + 12f * s, r.width - 32f * s, r.height - 24f * s));
            bool person = LinkKind == "person";
            GUILayout.Label(person ? "ID（人物）を選ぶ" : "性格（登録性格）を選ぶ", title);
            GUILayout.Label("カード：" + name + "\n" + (person ? "同じ人物にしたカードは、同じ人として Muse に扱われます。割り当てない（本人）なら Muse 本人です。"
                : "カードに決めた性格は、人物の既定より優先します。「人物に従う」で人物の既定に戻ります。"), wrapSmall);
            linkScroll = GUILayout.BeginScrollView(linkScroll, GUILayout.Height(hh - 200f * s));
            if (person)
            {
                int now = host.Personas.PersonNo(id);
                if (GUILayout.Button((now <= 0 ? "● " : "") + "本人（Muse）", button, GUILayout.Height(lh))) ChooseLink(0);
                foreach (var pe in host.Personas.Persons)
                {
                    if (pe.Retired) continue;
                    if (GUILayout.Button((pe.No == now ? "● " : "") + pe.No + " " + pe.Name, button, GUILayout.Height(lh))) { ChooseLink(pe.No); break; }
                }
            }
            else
            {
                int own = host.Personas.OwnPersona(id);
                var pz = host.Personas.Of(id);
                string follows = pz == null ? "選択なし（元の性格）" : Convert.ToInt32(pz["no"]) + " " + Json.Str(pz, "name", "");
                if (GUILayout.Button((own < 0 ? "● " : "") + "人物に従う（今：" + (own < 0 ? follows : "人物の既定") + "）", button, GUILayout.Height(lh))) ChooseLink(-1);
                if (GUILayout.Button((own == 0 ? "● " : "") + "選択なし（元の性格）", button, GUILayout.Height(lh))) ChooseLink(0);
                foreach (var sl in host.Personas.Slots)
                    if (GUILayout.Button((own == sl.Key ? "● " : "") + sl.Key + " " + sl.Value, button, GUILayout.Height(lh))) { ChooseLink(sl.Key); break; }
            }
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("性格引き当てリスト編集…", button, GUILayout.Height(lh))) { LinkKind = ""; editPersonas = true; }
            if (GUILayout.Button("アイデンティティリスト編集…", button, GUILayout.Height(lh))) { LinkKind = ""; editIdentities = true; }
            if (GUILayout.Button("閉じる", button, GUILayout.Height(lh))) LinkKind = "";
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // the next person for the card: Muse herself, then the persons in order (the retired not)
        void CyclePerson(string cardId)
        {
            var live = host.Personas.Persons.FindAll(pe => !pe.Retired);
            int now = host.Personas.PersonNo(cardId), next = 0;
            if (live.Count > 0)
            {
                int i = live.FindIndex(pe => pe.No == now);
                next = i < 0 ? live[0].No : i + 1 < live.Count ? live[i + 1].No : 0;
            }
            host.AssignPerson(cardId, next);
        }

        // the next own persona for the card: its person's, none, then the slots
        void CyclePersona(string cardId)
        {
            var order = new List<int> { -1, 0 };
            foreach (var sl in host.Personas.Slots) order.Add(sl.Key);
            int i = order.IndexOf(host.Personas.OwnPersona(cardId));
            host.SetCardPersona(cardId, order[(i + 1) % order.Count]);
        }

        // a person's default persona: none, then the slots
        int NextSlot(int now)
        {
            var order = new List<int> { 0 };
            foreach (var sl in host.Personas.Slots) order.Add(sl.Key);
            int i = order.IndexOf(now);
            return order[(i + 1) % order.Count];
        }

        void DrawChooser(float s)
        {
            ChooserDrawn = true;
            var full = new Rect(0, 0, Screen.width, Screen.height);
            drawing.Add(full);
            GUI.DrawTexture(full, dimTex);
            if (chooserCards == null) chooserCards = host.Chars.List();
            float m = 40f * s, footer = 150f * s;
            GUI.Label(new Rect(m, 20f * s, Screen.width - 2 * m, 44f * s), "キャラを選んでください", title);
            var area = new Rect(m, 74f * s, Screen.width - 2 * m, Screen.height - 74f * s - footer);
            float cw = 168f * s, ch = cw * 1.4f, lh = 24f * s, gap = 18f * s;
            int cols = Mathf.Max(1, Mathf.FloorToInt((area.width + gap) / (cw + gap)));
            int rows = (chooserCards.Count + cols - 1) / cols;
            chooserScroll = GUI.BeginScrollView(area, chooserScroll, new Rect(0, 0, area.width - 20f, rows * (ch + 2 * lh + gap)));
            int budget = 2;
            for (int i = 0; i < chooserCards.Count; i++)
            {
                var c = chooserCards[i];
                var cell = new Rect((i % cols) * (cw + gap), (i / cols) * (ch + 2 * lh + gap), cw, ch + 2 * lh);
                if (c.Key == picked) GUI.DrawTexture(new Rect(cell.x - 4f, cell.y - 4f, cell.width + 8f, ch + 8f), pickTex);
                var face = Face(c.Key, ref budget);
                if (face != null) GUI.DrawTexture(new Rect(cell.x, cell.y, cw, ch), face, ScaleMode.ScaleAndCrop);
                else GUI.DrawTexture(new Rect(cell.x, cell.y, cw, ch), boxTex);
                GUI.Label(new Rect(cell.x, cell.y + ch + 2f, cw, lh), c.Value, small);
                var pe = host.Personas.PersonOf(host.Personas.PersonNo(Id(c.Key)));
                GUI.Label(new Rect(cell.x, cell.y + ch + lh, cw, lh), pe == null ? "本人" : pe.Name, small);
                if (c.Key == picked) DrawLinkButtons(new Rect(cell.x, cell.y, cw, ch), c.Key, s);       // (ID 設定・性格設定 over its picture)
                else if (!Modal && LinkKind.Length == 0 && GUI.Button(cell, GUIContent.none, GUIStyle.none)) picked = c.Key;     // (the lists over it take the clicks)
            }
            GUI.EndScrollView();
            if (chooserCards.Count == 0) GUI.Label(new Rect(m, area.y, area.width, lh), "カードがありません（UserData/chara/female、キャラカード）", small);
            // the footer: the picked card (picked is not loaded) - who it is (person, persona) set before meeting her; start;
            // the auto start (turned off from the settings / the character menu)
            float y = Screen.height - footer + 12f * s, bh = 30f * s;
            string pickedName = "";
            foreach (var c in chooserCards) if (c.Key == picked) pickedName = c.Value;
            // (the buttons together from the left edge - start, the identities' list, the personas' list - and the hint in what is
            // left of the row on the right, else on the line above; a screen too narrow: the two lists' buttons on a row of their
            // own. PR #12 adopted, the user's note: 「このカードで始める」 at the right edge lay under the Muse panel's window, the
            // launcher's app-mode browser on the screen's right. GPT's re-review of #33: at 1280 x 1024 nothing may overlap)
            const string hint = "人物・性格は、カードの上の「ID 設定」「性格設定」で";
            float fgap = 10f * s, wStart = 240f * s, wList = 220f * s;
            float usable = Mathf.Max(Screen.width * 0.5f, Screen.width - m - PanelRoom);       // (the Muse panel's window: the screen's right)
            float xStart = m, xId = xStart + wStart + fgap, xPersona = xId + wList + fgap, right = xPersona + wList;
            bool wrap = right > usable, hintHere = !wrap && usable - (right + fgap) >= 260f * s;
            string top = picked == null ? "カードをクリックして選びます（選んだだけでは読み込みません）" : "選んだカード：" + pickedName + (hintHere ? "" : "　" + hint);
            GUI.Label(new Rect(m, y, usable - m, lh), top, small);
            y += lh + 6f * s;
            if (hintHere && picked != null) GUI.Label(new Rect(right + fgap, y, usable - right - fgap, bh), hint, small);
            GUI.enabled = picked != null && !Modal && LinkKind.Length == 0;
            Buttons["このカードで始める"] = new Rect(xStart, y, wStart, bh);
            if (GUI.Button(new Rect(xStart, y, wStart, bh), "このカードで始める", button)) host.Launch.Confirm(picked, "user");
            if (wrap) { y += bh + 6f * s; xId = m; xPersona = m + wList + fgap; }
            GUI.enabled = !Modal && LinkKind.Length == 0;
            Buttons["アイデンティティリスト編集"] = new Rect(xId, y, wList, bh);
            Buttons["性格引き当てリスト編集"] = new Rect(xPersona, y, wList, bh);
            if (GUI.Button(new Rect(xId, y, wList, bh), "アイデンティティリスト編集", button)) editIdentities = true;
            if (GUI.Button(new Rect(xPersona, y, wList, bh), "性格引き当てリスト編集", button)) editPersonas = true;
            GUI.enabled = true;
            y += bh + 8f * s;
            GUI.enabled = !Modal;
            bool auto = GUI.Toggle(new Rect(m, y, usable - m, lh), host.AutoStartLast.Value, " 次回から、前回のキャラで自動的に始める（外すときは設定から）");
            GUI.enabled = true;
            if (auto != host.AutoStartLast.Value) host.AutoStartLast.Value = auto;
        }

        // the room the Muse panel's window takes on the screen's right (the launcher's panel: 400 px wide, on the right, at the
        // bottom of the work area - panel_config.example.json), kept clear by the chooser's footer
        public const float PanelRoom = 420f;

        // ---- the persons (numbers issued here) and the registered personas (numbers from the Muse side's ledger) ----

        bool editPersonas;
        bool editIdentities;            // (the identities' list: its own window - the user's note of 2026-11-05)
        string newNo = "", newName = "", newPerson = "", personaError = "";
        readonly Dictionary<int, string> renames = new Dictionary<int, string>(), personNames = new Dictionary<int, string>();
        Vector2 personaScroll, slotScroll;

        // the identities (人物): who a card is - Muse's own, or a person of the list, each with its default persona (the user's
        // note of 2026-11-05: apart from the personas' list, which looked the same at a glance)
        void DrawIdentityEditor(float s)
        {
            float w = 640f * s, hh = Mathf.Min(Screen.height - 40f, 560f * s), lh = 28f * s;
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - hh) / 2f, w, hh);
            drawing.Add(r);
            GUI.DrawTexture(r, dimTex);
            GUI.Box(r, GUIContent.none, dialogBox);
            GUILayout.BeginArea(new Rect(r.x + 16f * s, r.y + 12f * s, r.width - 32f * s, r.height - 24f * s));
            GUILayout.Label("アイデンティティ（人物）", title);
            GUILayout.Label("番号はアプリが出します。\nカードを同じ人物にまとめるのは、カードの「ID 設定」で。\n割り当ての無いカードは、Muse 本人です。", wrapSmall);
            personaScroll = GUILayout.BeginScrollView(personaScroll, GUILayout.Height(hh - 190f * s));
            foreach (var pe in new List<Personas.Person>(host.Personas.Persons))
            {
                if (pe.Retired) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(pe.No.ToString(), small, GUILayout.Width(36f * s));
                string cur;
                if (!personNames.TryGetValue(pe.No, out cur)) cur = pe.Name;
                personNames[pe.No] = GUILayout.TextField(cur, field, GUILayout.Width(150f * s), GUILayout.Height(lh));
                GUI.enabled = personNames[pe.No].Trim().Length > 0 && personNames[pe.No] != pe.Name;
                if (GUILayout.Button("名前を保存", button, GUILayout.Width(90f * s), GUILayout.Height(lh))) host.Personas.RenamePerson(pe.No, personNames[pe.No]);
                GUI.enabled = true;
                if (GUILayout.Button("既定の性格：" + SlotLabel(pe.Persona), button, GUILayout.Height(lh))) host.SetPersonPersona(pe.No, NextSlot(pe.Persona));
                GUILayout.Label("カード " + host.Personas.CardCount(pe.No), small, GUILayout.Width(64f * s));
                if (GUILayout.Button("廃止", button, GUILayout.Width(50f * s), GUILayout.Height(lh))) host.RetirePerson(pe.No);
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("新しい人物の名前", small, GUILayout.Width(130f * s));
            newPerson = GUILayout.TextField(newPerson, field, GUILayout.Width(240f * s), GUILayout.Height(lh));
            GUI.enabled = newPerson.Trim().Length > 0;
            if (GUILayout.Button("作る", button, GUILayout.Width(70f * s), GUILayout.Height(lh))) { host.Personas.AddPerson(newPerson); newPerson = ""; }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            var retired = host.Personas.Persons.FindAll(pe => pe.Retired);
            if (retired.Count > 0) GUILayout.Label("廃止した番号（使い回しません）：" + string.Join("、", retired.ConvertAll(pe => pe.No + " " + pe.Name).ToArray()), wrapSmall);
            if (host.Personas.Slots.Count == 0)
                GUILayout.Label("既定の性格を選ぶには、先に「性格引き当てリスト編集」で性格を登録します。", wrapSmall);
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("性格引き当てリスト編集…", button, GUILayout.Height(lh))) { editIdentities = false; editPersonas = true; }
            if (GUILayout.Button("閉じる", button, GUILayout.Height(lh))) editIdentities = false;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // the personas (登録性格): the slots of Muse's ledger, by its numbers
        void DrawPersonaEditor(float s)
        {
            float w = 640f * s, hh = Mathf.Min(Screen.height - 40f, 560f * s), lh = 28f * s;
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - hh) / 2f, w, hh);
            drawing.Add(r);
            GUI.DrawTexture(r, dimTex);
            GUI.Box(r, GUIContent.none, dialogBox);
            GUILayout.BeginArea(new Rect(r.x + 16f * s, r.y + 12f * s, r.width - 32f * s, r.height - 24f * s));
            GUILayout.Label("性格引き当てリスト（登録性格）", title);
            GUILayout.Label("番号は Muse 側の台帳のものです。\nその番号で、Muse の性格と結びつきます。", wrapSmall);
            slotScroll = GUILayout.BeginScrollView(slotScroll, GUILayout.Height(hh - 190f * s));
            foreach (var sl in new List<KeyValuePair<int, string>>(host.Personas.Slots))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(sl.Key.ToString(), small, GUILayout.Width(36f * s));
                string cur;
                if (!renames.TryGetValue(sl.Key, out cur)) cur = sl.Value;
                renames[sl.Key] = GUILayout.TextField(cur, field, GUILayout.Width(240f * s), GUILayout.Height(lh));
                GUI.enabled = renames[sl.Key].Trim().Length > 0 && renames[sl.Key] != sl.Value;
                if (GUILayout.Button("名前を保存", button, GUILayout.Width(90f * s), GUILayout.Height(lh))) host.SetSlotName(sl.Key, renames[sl.Key]);
                GUI.enabled = true;
                if (GUILayout.Button("削除", button, GUILayout.Width(50f * s), GUILayout.Height(lh))) { host.Personas.RemoveSlot(sl.Key); renames.Remove(sl.Key); }
                GUILayout.EndHorizontal();
            }
            if (host.Personas.Slots.Count == 0) GUILayout.Label("（まだありません）", small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("追加：番号と名前", small, GUILayout.Width(130f * s));
            newNo = GUILayout.TextField(newNo, field, GUILayout.Width(60f * s), GUILayout.Height(lh));
            newName = GUILayout.TextField(newName, field, GUILayout.Width(240f * s), GUILayout.Height(lh));
            if (GUILayout.Button("追加", button, GUILayout.Width(70f * s), GUILayout.Height(lh)))
            {
                int no;
                personaError = int.TryParse(newNo.Trim(), out no) ? host.Personas.AddSlot(no, newName) : "番号は 1 以上の整数で";
                if (personaError.Length == 0) { newNo = ""; newName = ""; }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(personaError, wrapSmall);
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("アイデンティティリスト編集…", button, GUILayout.Height(lh))) { editPersonas = false; personaError = ""; editIdentities = true; }
            if (GUILayout.Button("閉じる", button, GUILayout.Height(lh))) { editPersonas = false; personaError = ""; }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // the mouse (screen coordinates, y up) is over the UI
        public bool Over(Vector2 mouse)
        {
            var p = new Vector2(mouse.x, Screen.height - mouse.y);
            foreach (var r in rects) if (r.Contains(p)) return true;
            return false;
        }

        static readonly Dictionary<string, string> ConditionNames = new Dictionary<string, string> {
            { "arousal_start", "発情の開始値" }, { "format", "形式" }, { "climax", "規定の絶頂回数" }, { "negotiation_turns", "協議のターン" },
            { "sex_turns", "プレイのターン" }, { "chars", "字数" } };

        static string MismatchText(Match mt, List<string> keys)
        {
            var c = mt.Conditions;
            var ours = Json.Obj(c, "ours");
            var theirs = Json.Obj(c, "theirs");
            var parts = new List<string>();
            foreach (var k in keys)
            {
                string n;
                if (!ConditionNames.TryGetValue(k, out n)) n = k;
                parts.Add(string.Format("{0} {1}／相手 {2}", n, CondValue(ours, k), CondValue(theirs, k)));
            }
            return string.Join("、", parts.ToArray());
        }

        static string CondValue(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return "なし";
            if (v is string) return (string)v == "lesbian" ? "レズプレイ" : (string)v == "sex" ? "セックス" : (string)v;
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public string MismatchShown = "";     // (checks) the match panel's line of the conditions' mismatch this frame
        public int FontNow;          // (checks) the text's font size this frame
        public Rect BoxNow;          // (checks) the box this frame

        void DrawDialogue(float s)
        {
            var bp = host.Blocks;
            bool pr = PanelRight;
            float left = pr ? 28f * s : Mathf.Max(28f, panelReserve + 20f), right = pr ? Mathf.Max(28f * s, panelReserve + 20f) : 28f * s;
            float w = Mathf.Max(320f, Screen.width - left - right);
            if (bp == null || !bp.Showing || string.IsNullOrEmpty(bp.Text))
            {
                // between packages: a quiet waiting line (not while the creation is open)
                if (!host.Chars.Creating) GUI.Label(new Rect(left + 6f, Screen.height - 44f * s, w, 30f * s), "（次のターンを待っています）", waitLine);
                BoxNow = new Rect();
                return;
            }
            float mx = 34f * s, mt = 26f * s, mb = 22f * s;
            // the box for the whole text (the reveal never reflows): taller up to 45 % of the screen, then a smaller font
            var style = new GUIStyle(bp.IsSound ? sound : text);
            int fs = style.fontSize, minFs = Mathf.RoundToInt(16 * s);
            float cap = Screen.height * 0.45f, need;
            while (true)
            {
                style.fontSize = fs;
                need = style.CalcHeight(new GUIContent(bp.Text), w - 2f * mx) + mt + mb;
                if (need <= cap || fs <= minFs) break;
                fs -= 2;
            }
            float h = Mathf.Clamp(need, 150f * s, Screen.height - 60f);
            var r = new Rect(left, Screen.height - h - 26f * s, w, h);
            FontNow = fs;
            BoxNow = r;
            drawing.Add(r);
            GUI.Box(r, GUIContent.none, dialogBox);
            GUI.DrawTexture(new Rect(r.x + 14f * s, r.y + 1f, r.width - 28f * s, 2f), lineTex);
            if (!bp.IsSound && !string.IsNullOrEmpty(bp.SpeakerName))
            {
                var nc = new GUIContent(bp.SpeakerName);
                float nw = nameTag.CalcSize(nc).x + 36f * s;
                GUI.Label(new Rect(r.x + 26f * s, r.y - 20f * s, nw, 32f * s), nc, nameTag);
            }
            GUI.Label(new Rect(r.x + mx, r.y + mt, r.width - 2f * mx, r.height - mt - mb), bp.Text.Substring(0, bp.Visible), style);
            if (!bp.Revealing && Mathf.Repeat(Time.realtimeSinceStartup, 1f) < 0.65f)
                GUI.Label(new Rect(r.xMax - 42f * s, r.yMax - 36f * s, 30f * s, 26f * s), "▼", marker);
            if (!editPersonas && GUI.Button(r, GUIContent.none, GUIStyle.none)) bp.Advance();
        }

        void DrawPanel(float s)
        {
            var b = host.Bridge;
            var pc = host.Play;
            float w = 300f * s, x = PanelRight ? 12f * s : Screen.width - w - 12f * s, y = 12f * s, lh = 26f * s;
            // (a quiet panel under the column: its words readable over a bright room - its height as the last frame drew it)
            if (columnH > 0f) GUI.Box(new Rect(x - 8f * s, 4f * s, w + 16f * s, columnH + 14f * s), GUIContent.none, box);
            string status = b == null ? "" : b.Superseded ? "○ 別のゲーム窓が使われています" : b.Connected ? "● Muse ブリッジ接続中" : "○ ブリッジ未接続（再試行中）";
            GUI.Label(new Rect(x, y, w - 80f * s, lh), status + (b != null && b.VoiceOff ? "（声オフ）" : ""), small);     // (the UI button at its right)
            y += lh;
            GUI.Label(new Rect(x, y, w, lh), "場所：" + Json.Str(host.Stage.LocationInfo(host.Stage.Location), "name", ""), small);
            y += lh;
            // he is in her room unseen (peeping her own time): he may call out - the first she knows of him there
            if (host.Outing.CanCall)
            {
                Buttons["声をかける"] = new Rect(x, y, w, lh);
                if (GUI.Button(new Rect(x, y, w, lh), "声をかける（覗いているのが知られます）", button)) host.Outing.Call();
                y += lh + 4f * s;
            }
            if (host.Chars.Creating)
            {
                GUI.Label(new Rect(x, y, w, lh), host.Chars.ForUser ? "あなたの体をキャラメイクで作っています…" : "キャラメイクを開いています（保存して閉じると反映）", small);
                y += lh;
            }
            if (host.Match.Active) GUI.Label(new Rect(x, y, w, lh), "キャラ：" + host.Stage.Name + "（対戦中）", small);
            else if (GUI.Button(new Rect(x, y, w, lh), menu == "chara" ? "閉じる" : "キャラ：" + host.Stage.Name, menu == "chara" ? openButton : button)) menu = menu == "chara" ? "" : "chara";
            y += lh + 4f * s;
            bool auto = GUI.Toggle(new Rect(x, y, w / 2f - 4f, lh), host.Blocks.Auto, host.Blocks.Auto ? "自動送り：入" : "自動送り：切", button);
            if (auto != host.Blocks.Auto) host.Blocks.SetAuto(auto);
            // the settings (ConfigurationManager's window; F1 too)
            GUI.enabled = MuseStudioPlugin.SettingsAvailable;
            if (GUI.Button(new Rect(x + w / 2f + 4f, y, w / 2f - 4f, lh), "設定（F1）", button))
            {
                if (MuseStudioPlugin.SettingsShown) MuseStudioPlugin.SettingsShown = false;
                else MuseStudioPlugin.OpenSettings();
            }
            GUI.enabled = true;
            y += lh + 4f * s;
            float w3 = (w - 8f) / 3f;
            var mt = host.Match;
            if (mt.Active)
            {
                // the match: the score, the phase and its turns, leaving (the users watch: no other menu)
                if (menu == "location" || menu == "play" || menu == "clothes" || menu == "chara" || menu == "replay" || menu == "items") menu = "";
                var rp = host.Replay;
                if (mt.Replaying)
                {
                    // a replay: where it is, and that it reaches no one
                    GUI.Label(new Rect(x, y, w, lh), string.Format("リプレイ　{0}／{1}{2}", rp.Done, rp.Count, rp.Finished ? "　おわり" : ""), small);
                    y += lh;
                    GUI.Label(new Rect(x, y, w, lh), "（記録の再生です。Muse にも相手にも届きません）", small);
                    y += lh;
                }
                string hn = mt.Side == "host" ? host.Stage.Name : mt.OpponentName, gn = mt.Side == "guest" ? host.Stage.Name : mt.OpponentName;
                int lim = mt.Limit("climax");
                GUI.Label(new Rect(x, y, w, lh), string.Format("対戦　{0} {1}／{2}　対　{3} {4}／{2}", hn, mt.Tally["host"], lim, gn, mt.Tally["guest"]), small);
                y += lh;
                string ph = mt.Phase == "intro" ? "自己紹介" : mt.Phase == "negotiation" ? "協議" : mt.Phase == "sex" ? (mt.Lesbian ? "プレイ" : "セックス") : "終了";
                string res = mt.Result == null ? "" : (Json.Has(mt.Result, "draw") && (bool)mt.Result["draw"]) ? "　引き分け"
                    : "　勝ち：" + (Json.Str(mt.Result, "winner") == "host" ? hn : gn);
                GUI.Label(new Rect(x, y, w, lh), string.Format("{0}（{1} ターン）{2}{3}", ph, mt.PhaseTurns, mt.HasBody ? "" : "　相手は姿なし", res), small);
                y += lh;
                // the users' notice (Muse's request of 2026-11-04): the inserting side is drawn futanari - not the strapon rule;
                // the lesbian format has no male part
                GUI.Label(new Rect(x, y, w, lh), mt.Lesbian ? "形式：レズプレイ（男性の役なし）" : "※挿入側はふたなりで描かれます（設定に説明）", small);
                y += lh;
                // the two apps' conditions, compared by the bridges: a mismatch shown (the host's values are meant for both)
                var mm = mt.Mismatch();
                if (mm.Count > 0)
                {
                    MismatchShown = "⚠ 相手と条件が違います：" + MismatchText(mt, mm);
                    GUI.Label(new Rect(x, y, w, lh * 2f), MismatchShown, warn);
                    y += lh * 2f;
                }
                if (mt.Replaying)
                {
                    // next / auto / speed (the replay's own; the broadcast view as live)
                    Buttons["次へ"] = new Rect(x, y, w3, lh);
                    Buttons["自動再生"] = new Rect(x + w3 + 4f, y, w3, lh);
                    Buttons["速度"] = new Rect(x + 2f * (w3 + 4f), y, w3, lh);
                    GUI.enabled = !rp.Finished;
                    if (GUI.Button(new Rect(x, y, w3, lh), "次へ", button)) host.Blocks.Advance();
                    GUI.enabled = true;
                    bool ap = GUI.Toggle(new Rect(x + w3 + 4f, y, w3, lh), rp.AutoPlay, rp.AutoPlay ? "自動：入" : "自動：切", button);
                    if (ap != rp.AutoPlay) { rp.AutoPlay = ap; host.Blocks.SetAuto(ap); }
                    if (GUI.Button(new Rect(x + 2f * (w3 + 4f), y, w3, lh), "速度 ×" + rp.Speed.ToString("0.#"), button))
                    {
                        int at = Array.IndexOf(Replay.Speeds, rp.Speed);
                        rp.Speed = Replay.Speeds[(at + 1) % Replay.Speeds.Length];
                        host.Blocks.Pace = rp.Speed;
                    }
                    y += lh + 4f * s;
                }
                Buttons["対戦を抜ける"] = new Rect(x, y, w / 2f - 4f, lh);
                Buttons["リプレイを終える"] = new Rect(x, y, w / 2f - 4f, lh);
                Buttons["中継風"] = new Rect(x + w / 2f + 4f, y, w / 2f - 4f, lh);
                if (mt.Replaying) { if (GUI.Button(new Rect(x, y, w / 2f - 4f, lh), "リプレイを終える", button)) rp.Stop(); }
                else if (GUI.Button(new Rect(x, y, w / 2f - 4f, lh), "対戦を抜ける", button)) mt.Leave("user_left");
                bool bc = GUI.Toggle(new Rect(x + w / 2f + 4f, y, w / 2f - 4f, lh), host.MatchBroadcast.Value, host.MatchBroadcast.Value ? "中継風：入" : "中継風：切", button);
                if (bc != host.MatchBroadcast.Value) { host.MatchBroadcast.Value = bc; onAirFrom = -1f; if (!bc) mt.Camera(); }
                y += lh + 6f * s;
                if (pc != null && pc.Active)
                {
                    GUI.Label(new Rect(x, y, w, lh), string.Format("{0}（{1}）{2}", Json.Str(pc.Current, "name"), Json.Str(pc.Info(), "pace_name"),
                        pc.Climax.Running ? " " + pc.Climax.SeqName : ""), small);
                    y += lh;
                    Buttons["カメラを戻す"] = new Rect(x, y, w, lh);
                    if (GUI.Button(new Rect(x, y, w, lh), "カメラを戻す", button)) pc.Camera(false);
                    y += lh + 6f * s;
                }
                else
                {
                    Buttons["カメラを戻す"] = new Rect(x, y, w, lh);
                    if (GUI.Button(new Rect(x, y, w, lh), "カメラを戻す", button)) mt.Camera();
                    y += lh + 6f * s;
                }
                drawing.Add(new Rect(x, 12f * s, w, y - 12f * s));
                columnH = y - 12f * s;
                return;
            }
            Buttons["場所"] = new Rect(x, y, w3, lh);
            Buttons["プレイ"] = new Rect(x + w3 + 4f, y, w3, lh);
            Buttons["服"] = new Rect(x + 2f * (w3 + 4f), y, w3, lh);
            if (GUI.Button(new Rect(x, y, w3, lh), menu == "location" ? "閉じる" : "場所", menu == "location" ? openButton : button)) menu = menu == "location" ? "" : "location";
            if (GUI.Button(new Rect(x + w3 + 4f, y, w3, lh), menu == "play" ? "閉じる" : "プレイ", menu == "play" ? openButton : button)) menu = menu == "play" ? "" : "play";
            GUI.enabled = !host.Outing.Out;
            if (GUI.Button(new Rect(x + 2f * (w3 + 4f), y, w3, lh), menu == "clothes" ? "閉じる" : "服", menu == "clothes" ? openButton : button)) menu = menu == "clothes" ? "" : "clothes";
            GUI.enabled = true;
            y += lh + 6f * s;
            if (pc != null && pc.Active)
            {
                GUI.Label(new Rect(x, y, w, lh), string.Format("{0}（{1}）{2}", Json.Str(pc.Current, "name"), Json.Str(pc.Info(), "pace_name"),
                    pc.Climax.Running ? " " + pc.Climax.SeqName : ""), small);
                y += lh;
                // where, and who leads (MUSE_GUIDE 3.7; the Godot app's panel)
                GUI.Label(new Rect(x, y, w, lh), string.Format("場所：{0}　{1}", pc.CurrentSpot != null ? pc.CurrentSpot.Name : "―",
                    pc.Res == null ? "" : pc.Res.Lead == "player" ? "あなたが主導" : pc.Res.Lead == "character" ? "彼女が主導" : ""), small);
                y += lh;
                // the pace is Muse's (the user's decision 2026-11-02): shown, not set here
                string fin = pc.Climax.Running ? (pc.Climax.Holding ? "　フィニッシュ待ち" : "　フィニッシュ中") : "";
                GUI.Label(new Rect(x, y, w, lh), string.Format("ペース：{0}（Muse）{1}", Json.Str(pc.Info(), "pace_name"), fin), small);
                y += lh;
                GUI.Label(new Rect(x, y, w, lh), string.Format("彼女 {0:P0}", host.Gauge.Value), small);
                y += lh;
                if (pc.Group == "sonyu")
                {
                    string ct = "ゴム：" + (pc.Condom == null ? "未定（Muse が聞きます）" : pc.Condom == true ? "あり" : "なし");
                    GUI.Label(new Rect(x, y, w, lh), (pc.Inserted ? "入っている　" : "抜けている（入れるかは Muse）　") + ct, small);
                    y += lh;
                    GUI.enabled = pc.Inserted && !pc.Climax.Running;
                    if (GUI.Button(new Rect(x, y, w, lh), pc.Inserted ? "抜く" : "抜いた", button)) { pc.LastInput = Time.realtimeSinceStartup; pc.Pull(); }
                    GUI.enabled = true;
                    y += lh;
                }
                y += 4f * s;
                if (host.Hands.OnNow) y = DrawHands(x, y, w, lh, s);      // (the aibu, and a service and an insertion as the original's)
                if (host.Stage.Partner != null)
                {
                    // the user's eyes / the position's camera; the user's clothes
                    if (GUI.Button(new Rect(x, y, w / 2f - 4f, lh), pc.View == "pov" ? "体位カメラへ" : "目線へ", button)) pc.SetView(pc.View == "pov" ? "position" : "pov");
                    int cl = Mathf.Clamp(host.YourClothes.Value, 0, 2);
                    if (GUI.Button(new Rect(x + w / 2f + 4f, y, w / 2f - 4f, lh), "あなたの服：" + new[] { "着たまま", "半脱ぎ", "脱ぐ" }[cl], button)) pc.SetPartnerClothes((cl + 1) % 3);
                    y += lh + 4f * s;
                    // your body as the original H's male: shown / see-through / a silhouette / hidden (Z / V / C), the penis (X)
                    int bm = !host.YourBodyShown.Value ? 3 : !pc.SilhouetteAvailable ? 0 : host.YourTranslucent.Value ? 1 : host.YourSilhouette.Value ? 2 : 0;
                    Buttons["体"] = new Rect(x, y, w / 2f - 4f, lh);
                    if (GUI.Button(new Rect(x, y, w / 2f - 4f, lh), "体：" + new[] { "表示", "透過", "シルエット", "非表示" }[bm], button))
                    {
                        int next = (bm + 1) % 4;
                        if ((next == 1 || next == 2) && !pc.SilhouetteAvailable) next = 3;      // (透過・シルエット: a male body's, as in the original)
                        host.YourTranslucent.Value = next == 1;
                        host.YourSilhouette.Value = next == 2;
                        pc.SetYourBody("body", next != 3);
                    }
                    if (GUI.Button(new Rect(x + w / 2f + 4f, y, w / 2f - 4f, lh), "ペニス：" + (host.YourPenisShown.Value ? "表示" : "非表示"), button))
                        pc.SetYourBody("penis", !host.YourPenisShown.Value);
                    y += lh + 4f * s;
                    // where her eyes go in the position: the camera (the original's) or your body
                    Buttons["視線"] = new Rect(x, y, w, lh);
                    bool gz = GUI.Toggle(new Rect(x, y, w, lh), host.GazeAtYourBody.Value, host.GazeAtYourBody.Value ? "彼女の視線：あなたの体" : "彼女の視線：カメラ", button);
                    if (gz != host.GazeAtYourBody.Value) host.GazeAtYourBody.Value = gz;
                    y += lh + 4f * s;
                }
                // the original's camera of the position again after the user turned it (the Godot app's カメラを戻す)
                if (GUI.Button(new Rect(x, y, w / 2f - 4f, lh), "カメラを戻す", button)) { if (pc.View == "pov") pc.SetView("pov"); else pc.Camera(false); }
                if (GUI.Button(new Rect(x + w / 2f + 4f, y, w / 2f - 4f, lh), "プレイを終える", button)) pc.End("user");
                y += lh + 6f * s;
            }
            else
            {
                // the stage's preset again after the user turned the camera; the matches' records (リプレイ)
                if (GUI.Button(new Rect(x, y, w / 2f - 4f, lh), "カメラを戻す", button))
                {
                    string key = host.Stage.CameraKey;
                    host.Stage.ApplyCamera(key.Length > 0 && !key.StartsWith("play") ? key : "medium", false);
                }
                Buttons["リプレイ"] = new Rect(x + w / 2f + 4f, y, w / 2f - 4f, lh);
                if (GUI.Button(new Rect(x + w / 2f + 4f, y, w / 2f - 4f, lh), menu == "replay" ? "閉じる" : "リプレイ", menu == "replay" ? openButton : button))
                {
                    menu = menu == "replay" ? "" : "replay";
                    replays = null;
                    replayPage = 0;
                    deleteArmed = "";
                    host.Replay.Error = "";
                }
                y += lh + 4f * s;
                // the items and the budget (PR #46 §7): his own screen only
                Buttons["持ち物"] = new Rect(x, y, w, lh);
                if (host.Items != null && GUI.Button(new Rect(x, y, w, lh), menu == "items" ? "閉じる" : "持ち物（予算 " + host.Items.Budget + "）" + (host.Items.OffersToUser.Count > 0 ? " ●" : ""),
                                                     menu == "items" ? openButton : button))
                {
                    menu = menu == "items" ? "" : "items";
                    ItemsNote = "";
                }
                y += lh + 4f * s;
                // the way into a match (the trial run of 2026-11-04: nowhere to be found): the user asks Muse - the invitation
                // goes between the two Muses' chats
                Buttons["通信対戦"] = new Rect(x, y, w, lh);
                if (GUI.Button(new Rect(x, y, w, lh), menu == "match" ? "閉じる" : "通信対戦（Muse 同士）…", menu == "match" ? openButton : accentButton)) menu = menu == "match" ? "" : "match";
                y += lh + 6f * s;
            }
            drawing.Add(new Rect(x, 12f * s, w, y - 12f * s));
            columnH = y - 12f * s;
            // (each menu within what is left of the screen below the column - the user's note of 2026-11-05: the plays' list ran
            // past the bottom in a play, the column being long; its list scrolls inside)
            float left = Mathf.Max(0f, Screen.height - y - 12f * s);         // (no floor under it: it never runs past the bottom - GPT's re-review of #33)
            MenuRect = new Rect(x, y, w, 0f);
            if (menu == "chara") DrawCharas(MenuRect = new Rect(x, y, w, Mathf.Min(Screen.height * 0.6f, left)), s);
            else if (menu == "location") DrawLocations(MenuRect = new Rect(x, y, w, Mathf.Min(Screen.height * 0.5f, left)), s);
            else if (menu == "clothes") DrawClothes(MenuRect = new Rect(x, y, w, Mathf.Min(Screen.height * 0.5f, left)), s);
            else if (menu == "play") DrawPlays(MenuRect = new Rect(x, y, w, Mathf.Min(Screen.height * 0.6f, left)), s);
            else if (menu == "replay") DrawReplays(x, y, w, s);
            else if (menu == "match") DrawMatchMenu(x, y, w, s);
            else if (menu == "items") DrawItems(MenuRect = new Rect(x, y, w, Mathf.Min(Screen.height * 0.6f, left)), s);
        }

        // the match's start, the user's side of it: the host's conditions (the format, the arousal to start from: the settings) and
        // a request to Muse (the event match_requested: role host / guest) - Muse makes the room and the invitation (MUSE_GUIDE 8.2)
        // and tells the user in its chat what to pass to the other side
        float matchMenuH, askedAt = -100f;
        string askedRole = "";

        void DrawMatchMenu(float x, float y, float w, float s)
        {
            float lh = 26f * s, top = y, w2 = (w - 4f) / 2f;
            if (matchMenuH > 0f) GUI.Box(new Rect(x - 8f * s, y - 2f * s, w + 16f * s, matchMenuH + 8f * s), GUIContent.none, box);
            // (the user's note of 2026-11-04: one looks for a start button here - in this game Muse starts and runs the match)
            const string about = "対戦の開始と進行は Muse が受け持ちます。下の「招く／招かれる」は Muse への依頼です。部屋づくり・招待コードのやり取り・開始は、Muse がチャットで進めます（Muse に「対戦したい」と話すだけでも始められます）。";
            float ah = wrapSmall.CalcHeight(new GUIContent(about), w - 12f);
            GUI.Label(new Rect(x + 6f, y, w - 12f, ah), about, wrapSmall);
            y += ah + 6f * s;
            GUI.Label(new Rect(x + 6f, y, w - 12f, lh), "招く側（ホスト）の条件", small);
            y += lh;
            Buttons["形式：セックス"] = new Rect(x, y, w2, lh);
            Buttons["形式：レズ"] = new Rect(x + w2 + 4f, y, w2, lh);
            bool lez = host.MatchFormat.Value == "lesbian";
            if (GUI.Toggle(new Rect(x, y, w2, lh), !lez, "セックス", button) && lez) host.MatchFormat.Value = "sex";
            if (GUI.Toggle(new Rect(x + w2 + 4f, y, w2, lh), lez, "レズプレイ", button) && !lez) host.MatchFormat.Value = "lesbian";
            y += lh + 4f * s;
            float a = host.MatchArousalStart.Value, w3 = (w - 8f) / 3f;
            if (GUI.Button(new Rect(x, y, w3, lh), "−", button)) host.MatchArousalStart.Value = Mathf.Clamp01(Mathf.Round((a - 0.1f) * 10f) / 10f);
            GUI.Label(new Rect(x + w3 + 4f, y, w3, lh), string.Format("発情 {0:0.0}", host.MatchArousalStart.Value), small);
            if (GUI.Button(new Rect(x + 2f * (w3 + 4f), y, w3, lh), "＋", button)) host.MatchArousalStart.Value = Mathf.Clamp01(Mathf.Round((a + 0.1f) * 10f) / 10f);
            y += lh + 6f * s;
            Buttons["対戦を招く"] = new Rect(x, y, w2, lh);
            Buttons["対戦に招かれる"] = new Rect(x + w2 + 4f, y, w2, lh);
            bool can = !host.Outing.Out && !host.Chars.Creating && !host.Replay.Playing;
            GUI.enabled = can;
            if (GUI.Button(new Rect(x, y, w2, lh), "招く（ホスト）", accentButton)) AskMatch("host");
            if (GUI.Button(new Rect(x + w2 + 4f, y, w2, lh), "招かれる（ゲスト）", accentButton)) AskMatch("guest");
            GUI.enabled = true;
            y += lh + 4f * s;
            string note = !can ? "今は始められません（お出かけ・キャラメイク・リプレイの間）"
                : Time.realtimeSinceStartup - askedAt < 30f ? (askedRole == "host" ? "Muse に招待の用意を頼みました。Muse のチャットを見てください。" : "Muse に伝えました。相手の招待のコードを Muse のチャットに貼ってください。")
                : "";
            if (note.Length > 0) { GUI.Label(new Rect(x + 6f, y, w - 12f, lh * 2f), note, warn); y += lh * 2f; }
            matchMenuH = y - top;
            drawing.Add(new Rect(x, top, w, matchMenuH + 4f));
        }

        public string LastMatchAsk = "";       // (checks)

        void AskMatch(string role)
        {
            askedRole = role;
            askedAt = Time.realtimeSinceStartup;
            LastMatchAsk = role;
            var e = new Dictionary<string, object> { { "role", role }, { "source", "panel" } };
            if (role == "host")
            {
                e["format"] = host.MatchFormat.Value == "lesbian" ? "lesbian" : "sex";
                e["arousal_start"] = Math.Round(host.MatchArousalStart.Value, 3);
            }
            host.Bridge.Event("match_requested", e);
        }

        // the matches' records (Replay): the newest first, a page at a time - 再生 / 削除 (asked again before it goes)
        List<Replay.Item> replays;
        int replayPage;
        string deleteArmed = "";
        const int ReplaysAPage = 6;
        float replaysH;

        void DrawReplays(float x, float y, float w, float s)
        {
            float lh = 26f * s, top = y;
            var rp = host.Replay;
            if (replays == null) replays = rp.List();
            if (replaysH > 0f) GUI.Box(new Rect(x - 8f * s, y - 2f * s, w + 16f * s, replaysH + 8f * s), GUIContent.none, box);
            var bg = new Rect(x, y, w, 0f);
            GUI.Label(new Rect(x + 6f, y, w - 12f, lh), replays.Count == 0 ? "対戦の記録はまだありません" : string.Format("対戦の記録（{0} 件）", replays.Count), small);
            y += lh;
            string why = rp.CanPlay();
            int pages = Mathf.Max(1, (replays.Count + ReplaysAPage - 1) / ReplaysAPage);
            replayPage = Mathf.Clamp(replayPage, 0, pages - 1);
            for (int i = replayPage * ReplaysAPage; i < Math.Min(replays.Count, (replayPage + 1) * ReplaysAPage); i++)
            {
                var it = replays[i];
                int row = i - replayPage * ReplaysAPage;
                string when = it.Started.Length >= 16 ? it.Started.Substring(5, 11).Replace('T', ' ') : it.Started;
                GUI.Label(new Rect(x + 6f, y, w - 12f, lh), string.Format("{0}　{1} 対 {2}{3}", when, it.Self, it.Opponent, it.Format == "lesbian" ? "（レズ）" : ""), small);
                y += lh * 0.9f;
                GUI.Label(new Rect(x + 6f, y, w * 0.56f, lh), it.Result, small);
                Buttons["再生" + row] = new Rect(x + w * 0.56f, y, w * 0.2f, lh);
                Buttons["削除" + row] = new Rect(x + w * 0.78f, y, w * 0.22f - 6f, lh);
                GUI.enabled = why.Length == 0;
                if (GUI.Button(new Rect(x + w * 0.56f, y, w * 0.2f, lh), "再生", button) && rp.Play(it.Path).Length == 0) menu = "";      // (else the reason stays shown)
                GUI.enabled = true;
                bool armed = deleteArmed == it.Path;
                if (GUI.Button(new Rect(x + w * 0.78f, y, w * 0.22f - 6f, lh), armed ? "本当に削除" : "削除", armed ? warnButton : button))
                {
                    if (!armed) deleteArmed = it.Path;
                    else { rp.Delete(it.Path); deleteArmed = ""; replays = null; return; }
                }
                y += lh + 4f * s;
            }
            if (pages > 1)
            {
                if (GUI.Button(new Rect(x, y, w / 3f - 4f, lh), "前へ", button)) replayPage = (replayPage + pages - 1) % pages;
                GUI.Label(new Rect(x + w / 3f, y, w / 3f, lh), (replayPage + 1) + "／" + pages, small);
                if (GUI.Button(new Rect(x + 2f * w / 3f + 4f, y, w / 3f - 4f, lh), "次へ", button)) replayPage = (replayPage + 1) % pages;
                y += lh + 4f * s;
            }
            string note = rp.Error.Length > 0 ? rp.Error : why;
            if (note.Length > 0) { GUI.Label(new Rect(x + 6f, y, w - 12f, lh), note, warn); y += lh; }
            bg.height = y - top + 4f;
            replaysH = y - top;
            drawing.Add(bg);
        }

        // aibu: the item per area (the button cycles it, as the wheel over the area) and 外す
        float DrawHands(float x, float y, float w, float lh, float s)
        {
            var hands = host.Hands;
            GUI.Label(new Rect(x, y, w, lh), "道具（部位の上でホイール／右クリックで外す）", small);
            y += lh;
            var names = new Dictionary<string, string> { { "chest", "胸" }, { "crotch", "股間" }, { "hips", "お尻" }, { "anal", "アナル" } };
            float bw = w / 4f;
            int i = 0;
            foreach (var area in AibuHands.TouchAreas)
            {
                string k = hands.Chosen(area);
                string label = names[area] + "：" + (k.Length > 0 ? AibuHands.KindNames[k] : "―");
                GUI.enabled = k.Length > 0;
                if (GUI.Button(new Rect(x + i * bw, y, bw - 4f, lh), label, button)) hands.Cycle(area, 1);
                GUI.enabled = true;
                i++;
            }
            y += lh + 4f * s;
            GUI.enabled = hands.Active;
            if (GUI.Button(new Rect(x, y, w, lh), "外す", button)) hands.DetachAll();
            GUI.enabled = true;
            return y + lh + 4f * s;
        }

        List<KeyValuePair<string, string>> cards;

        // the cards (the maker's saves, the project's card folder), the creation (the original's maker), the user's body
        void DrawCharas(Rect r, float s)
        {
            float lh = 26f * s;
            drawing.Add(r);
            GUILayout.BeginArea(r, box);
            GUI.enabled = !host.Chars.Creating;
            if (GUILayout.Button("クリエイト（原作のキャラメイクを開く）", button, GUILayout.Height(lh))) { menu = ""; Warn(host.Chars.Open(false)); }
            if (GUILayout.Button("あなたの体を作る・直す（キャラメイク）", button, GUILayout.Height(lh))) { menu = ""; Warn(host.Chars.Open(true)); }
            GUI.enabled = true;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("アイデンティティリスト編集", button, GUILayout.Height(lh))) editIdentities = true;
            if (GUILayout.Button("性格引き当てリスト編集", button, GUILayout.Height(lh))) editPersonas = true;
            GUILayout.EndHorizontal();
            bool auto = GUILayout.Toggle(host.AutoStartLast.Value, " 起動時、前回のキャラで自動的に始める");
            if (auto != host.AutoStartLast.Value) host.AutoStartLast.Value = auto;
            GUILayout.Label("カード（選んだだけでは読み込みません）", small);
            if (cards == null || GUILayout.Button("一覧を更新", button, GUILayout.Height(lh))) cards = host.Chars.List();
            if (menuPick == null) menuPick = host.Chars.CardPath;
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(r.height * 0.22f));
            foreach (var c in cards)
            {
                bool now = string.Equals(c.Key, host.Chars.CardPath, StringComparison.OrdinalIgnoreCase);
                bool sel = string.Equals(c.Key, menuPick, StringComparison.OrdinalIgnoreCase);
                var pe = host.Personas.PersonOf(host.Personas.PersonNo(Id(c.Key)));
                if (GUILayout.Toggle(sel, (now ? "● " : "") + c.Value + "（" + (pe == null ? "本人" : pe.Name) + "）", button, GUILayout.Height(lh)) && !sel) menuPick = c.Key;
            }
            GUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(menuPick))
            {
                // its picture, ID 設定・性格設定 over it (the user's note of 2026-11-04)
                float ph = Mathf.Min(r.height * 0.36f, 240f * s), pw = ph / 1.4f;
                var slot = GUILayoutUtility.GetRect(r.width - 16f * s, ph + 4f * s);
                var pic = new Rect(slot.x + (slot.width - pw) / 2f, slot.y + 2f * s, pw, ph);
                int budgetM = 1;
                var faceM = Face(menuPick, ref budgetM);
                GUI.DrawTexture(pic, faceM != null ? faceM : boxTex, ScaleMode.ScaleAndCrop);
                DrawLinkButtons(pic, menuPick, s);
                bool isNow = string.Equals(menuPick, host.Chars.CardPath, StringComparison.OrdinalIgnoreCase);
                GUI.enabled = !isNow && !host.Chars.Creating;
                if (GUILayout.Button(isNow ? "（このカードが舞台にいます）" : "このカードに切り替える", button, GUILayout.Height(lh)))
                {
                    menu = "";
                    Warn(host.Chars.Switch(menuPick, "user"));
                }
                GUI.enabled = true;
            }
            GUILayout.EndArea();
        }

        string menuPick;      // the card picked in the character menu (not loaded until its button)

        static void Warn(string err) { if (!string.IsNullOrEmpty(err)) MuseStudioPlugin.Log.LogWarning("MuseStudio: " + err); }

        // her clothes: each garment she has on, on -> half -> off (clothes_changed by user)
        void DrawClothes(Rect r, float s)
        {
            float lh = 26f * s;
            drawing.Add(r);
            GUILayout.BeginArea(r, box);
            var cha = host.Stage.Cha;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("全部脱がせる", button, GUILayout.Height(lh))) host.ChangeClothes(new Dictionary<string, object> { { "all", "off" } }, "user");
            if (GUILayout.Button("全部着せる", button, GUILayout.Height(lh))) host.ChangeClothes(new Dictionary<string, object> { { "all", "on" } }, "user");
            GUILayout.EndHorizontal();
            var label = new Dictionary<string, string> { { "on", "着ている" }, { "half", "はだけ・ずらし" }, { "off", "脱いだ" } };
            foreach (int k in Clothes.Worn(cha))
            {
                string v = Clothes.State(cha, k);
                if (GUILayout.Button(Clothes.Names[k] + "：" + label[v], button, GUILayout.Height(lh)))
                    host.ChangeClothes(new Dictionary<string, object> { { Clothes.Keys[k], Clothes.Next(cha, k, v) } }, "user");
            }
            DrawCoordinates(s, lh);
            GUILayout.EndArea();
        }

        // the coordinate cards (PR #46 §3): the cards in game/UserData/coordinate, worn as Studio's outfit loading does; not
        // offered in a play, a match, while she is out (the way Muse's block is refused)
        Vector2 coordScroll;
        public string CoordinateNote = "";        // (checks) the last outcome shown

        void DrawCoordinates(float s, float lh)
        {
            GUILayout.Space(6f * s);
            GUILayout.Label("コーデ（カード）", small);
            var co = host.Coords;
            if (co == null) return;
            string blocked = co.Blocked(false);
            if (blocked.Length > 0)
            {
                GUILayout.Label(blocked == "in_play" ? "プレイ中は着替えられません" : blocked == "in_match" ? "対戦中は着替えられません"
                    : blocked == "she_is_out" ? "彼女が出かけています" : "今は着替えられません", small);
                return;
            }
            var list = co.List;
            if (list.Count == 0) { GUILayout.Label("game/UserData/coordinate にカードがありません", small); return; }
            int budget = 2;
            coordScroll = GUILayout.BeginScrollView(coordScroll, GUILayout.Height(Mathf.Min(list.Count, 5) * (lh + 6f * s) + 8f * s));
            foreach (var e in list)
            {
                var tex = Face(e.Path, ref budget);
                var content = tex != null ? new GUIContent(" " + e.Name, tex) : new GUIContent(e.Name);
                if (GUILayout.Button(content, button, GUILayout.Height(lh + 4f * s)))
                {
                    string why = co.Wear(e.Id, "user");
                    CoordinateNote = why.Length == 0 ? e.Name + " に着替えました" + (co.LastReplaced.Count > 0 ? "（無い Mod の部品 " + co.LastReplaced.Count + " 個は原作の品に）" : "")
                        : why == "unreadable" ? "このカードは読めませんでした（着替えていません）" : "着替えられませんでした";
                }
            }
            GUILayout.EndScrollView();
            if (CoordinateNote.Length > 0) GUILayout.Label(CoordinateNote, small);
        }

        public bool AllRooms;             // (the 場所 menu: every registered room, not only the usual ones)

        // ---- the items (PR #46 §7): the budget (his alone), what she offers him, his own (give her / to the shelf / suggest /
        // use), the shelf (take / suggest), hers, the shop (his purchase: his gift) ----
        Vector2 itemsScroll;
        public string ItemsNote = "";

        static string Why(string why)
        {
            switch (why)
            {
                case "": return "";
                case "budget": return "予算が足りません";
                case "full": return "これ以上は持てません";
                case "not_enough": return "ありません";
                case "no_use": return "使う品ではありません";
                case "she_is_out": return "彼女は出かけています";
                default: return why;
            }
        }

        void DrawItems(Rect r, float s)
        {
            var it = host.Items;
            float bw = 46f * s;
            drawing.Add(r);
            GUILayout.BeginArea(r, box);
            int unit = (int)Json.Num(it.Reward, "unit_seconds", 3600);
            int mins = Mathf.CeilToInt((float)((unit - it.Fraction) / 60.0));
            GUILayout.Label("予算 " + it.Budget + "（起動している 1 時間ごとに " + (int)Json.Num(it.Reward, "per_unit", 100) + "。次まで " + mins + " 分）", small);
            if (ItemsNote.Length > 0) GUILayout.Label(ItemsNote, small);
            itemsScroll = GUILayout.BeginScrollView(itemsScroll);
            foreach (var o in it.OffersToUser)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("彼女から：" + Json.Str(it.ItemDef(o.Item), "name", o.Item), small);
                if (GUILayout.Button("受け取る", button, GUILayout.Width(bw * 1.6f))) ItemsNote = Why(it.Answer(o.Id, true, "user")) is string n1 && n1.Length > 0 ? n1 : "受け取りました";
                if (GUILayout.Button("断る", button, GUILayout.Width(bw))) { it.Answer(o.Id, false, "user"); ItemsNote = "断りました"; }
                GUILayout.EndHorizontal();
            }
            foreach (var o in it.OffersToHer) GUILayout.Label("彼女の返事待ち：" + Json.Str(it.ItemDef(o.Item), "name", o.Item), small);
            GUILayout.Label("── あなたの持ち物 ──", small);
            foreach (var kv in new List<KeyValuePair<string, object>>(it.Stock("user")))
            {
                string id = kv.Key;
                bool drink = Json.Str(it.ItemDef(id), "kind", "") == "drink";
                GUILayout.BeginHorizontal();
                GUILayout.Label(Json.Str(it.ItemDef(id), "name", id) + " ×" + Convert.ToInt32(kv.Value), small);
                if (GUILayout.Button("渡す", button, GUILayout.Width(bw))) { string w = Why(it.OfferToHer(id)); ItemsNote = w.Length > 0 ? w : "渡そうとしています（彼女の返事を待っています）"; }
                if (GUILayout.Button("棚へ", button, GUILayout.Width(bw))) ItemsNote = Why(it.MoveUser(id, true));
                if (drink && GUILayout.Button("勧める", button, GUILayout.Width(bw * 1.3f))) { string w = Why(it.Suggest(id, "user")); ItemsNote = w.Length > 0 ? w : "勧めました（使うかは彼女が決めます）"; }
                if (drink && GUILayout.Button("使う", button, GUILayout.Width(bw))) ItemsNote = Why(it.Use(id, "user", "user", "own")) is string n2 && n2.Length > 0 ? n2 : "使いました";
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("── 棚（家のもの） ──", small);
            foreach (var kv in new List<KeyValuePair<string, object>>(it.Stock("shelf")))
            {
                string id = kv.Key;
                GUILayout.BeginHorizontal();
                GUILayout.Label(Json.Str(it.ItemDef(id), "name", id) + " ×" + Convert.ToInt32(kv.Value), small);
                if (GUILayout.Button("取る", button, GUILayout.Width(bw))) ItemsNote = Why(it.MoveUser(id, false));
                if (Json.Str(it.ItemDef(id), "kind", "") == "drink" && GUILayout.Button("勧める", button, GUILayout.Width(bw * 1.3f)))
                {
                    string w = Why(it.Suggest(id, "shelf"));
                    ItemsNote = w.Length > 0 ? w : "勧めました（使うかは彼女が決めます）";
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("── 彼女の持ち物 ──", small);
            string her = host.CharacterId();
            if (!string.IsNullOrEmpty(her))
                foreach (var kv in it.Stock(her)) GUILayout.Label(Json.Str(it.ItemDef(kv.Key), "name", kv.Key) + " ×" + Convert.ToInt32(kv.Value), small);
            GUILayout.Label("── 店 ──", small);
            foreach (var kv in Json.Obj(it.Def, "items") ?? new Dictionary<string, object>())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Json.Str(kv.Value, "name", kv.Key) + "　" + (int)Json.Num(kv.Value, "price", 0), small);
                if (GUILayout.Button("買う", button, GUILayout.Width(bw))) { string w = Why(it.Buy(kv.Key)); ItemsNote = w.Length > 0 ? w : "買いました（あなたの持ち物に）"; }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawLocations(Rect r, float s)
        {
            float lh = 26f * s;
            drawing.Add(r);
            GUILayout.BeginArea(r, box);
            // the room's play positions edited in the game (the user's request, PR #11: while the button is on)
            var ed = host.SpotEditor;
            if (ed != null && GUILayout.Button(ed.On ? "位置の調整を終える" : "この部屋の位置を調整", ed.On ? openButton : button, GUILayout.Height(lh)))
            {
                ed.Toggle(!ed.On);
                menu = "";
            }
            // the usual rooms (the kept ones), or every registered room (PR #46 §4.1: loaded when one goes there)
            int total = host.Places != null ? host.Places.Count : 0;
            if (total > 0 && GUILayout.Button(AllRooms ? "いつもの部屋だけ" : "全部の部屋（" + total + "）", button, GUILayout.Height(lh)))
            {
                AllRooms = !AllRooms;
                scroll = Vector2.zero;
            }
            scroll = GUILayout.BeginScrollView(scroll);
            var usual = new List<string>(host.Stage.LocationKeys());
            bool apartTold = false;
            foreach (var key in AllRooms ? host.Stage.AllLocationKeys() : usual)
            {
                // (all of them: the kept rooms - the home - first, a line before the rest)
                if (AllRooms && !apartTold && !usual.Contains(key))
                {
                    apartTold = true;
                    GUILayout.Label("── ほかの部屋（行くときに読み込み） ──", small);
                }
                string nm = Json.Str(host.Stage.LocationInfo(key), "name", key);
                if (host.Rooms.Loading(key)) nm += "（読み込み中）";
                if (key == host.Presence.HerRoom && key != host.Stage.Location) nm += "（彼女がいる）";
                if (key == host.Stage.Location) nm = "● " + nm;
                if (GUILayout.Button(nm, button, GUILayout.Height(lh)))
                {
                    menu = "";
                    host.MoveTo(key, "user");
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawPlays(Rect r, float s)
        {
            float lh = 26f * s;
            drawing.Add(r);
            GUILayout.BeginArea(r, box);
            float inner = r.width - box.padding.horizontal, gap = 4f;
            GUILayout.BeginHorizontal();
            GUILayout.Label("ゴム", small, GUILayout.Width(40f * s));
            var modes = new[] { "ask", "on", "off" };
            var modeNames = new[] { "毎回聞く", "付ける", "付けない" };
            float cw = (inner - 40f * s - 3f * gap) / 3f;
            for (int i = 0; i < 3; i++)
                if (GUILayout.Toggle(host.CondomMode.Value == modes[i], modeNames[i], button, GUILayout.Height(lh), GUILayout.Width(cw)) && host.CondomMode.Value != modes[i])
                    host.CondomMode.Value = modes[i];
            GUILayout.EndHorizontal();
            // the user's body (male / female, futanari): which plays can start and who plays which part
            GUILayout.BeginHorizontal();
            GUILayout.Label("あなた", small, GUILayout.Width(40f * s));
            if (GUILayout.Toggle(host.YourBody.Value != "female", "男性", button, GUILayout.Height(lh), GUILayout.Width(cw)) && host.YourBody.Value == "female") { host.YourBody.Value = "male"; host.SendCatalog(); }
            if (GUILayout.Toggle(host.YourBody.Value == "female", "女性", button, GUILayout.Height(lh), GUILayout.Width(cw)) && host.YourBody.Value != "female") { host.YourBody.Value = "female"; host.SendCatalog(); }
            bool fu = GUILayout.Toggle(host.YourFutanari.Value, "ふたなり", button, GUILayout.Height(lh), GUILayout.Width(cw));
            if (fu != host.YourFutanari.Value) { host.YourFutanari.Value = fu; host.SendCatalog(); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            // the body itself: the default, or one you made (the Godot app's choice)
            string bc = host.YourBodyCard.Value, bcKey = bc + "|" + host.YourBody.Value;
            if (bodyNameFor != bcKey)
            {
                bodyNameFor = bcKey;
                bodyName = "標準の体";
                if (!string.IsNullOrEmpty(bc) && System.IO.File.Exists(bc))
                {
                    bodyName = System.IO.Path.GetFileNameWithoutExtension(bc);
                    foreach (var kv in host.Chars.Bodies(host.YourBody.Value != "female")) if (kv.Key == bc) { bodyName = kv.Value; break; }
                }
            }
            if (GUILayout.Button(bodyList ? "閉じる" : "体：" + bodyName, bodyList ? openButton : button, GUILayout.Height(lh), GUILayout.Width(inner)))
            {
                bodyList = !bodyList;
                if (bodyList) bodyCards = host.Chars.Bodies(host.YourBody.Value != "female");
            }
            GUILayout.EndHorizontal();
            // the tabs; the group's categories (two rows when they are many); the added plays
            int gi = Array.FindIndex(Groups, g => g[0] == playGroup);
            var gnames = new string[Groups.Length];
            for (int i = 0; i < Groups.Length; i++) gnames[i] = Groups[i][1];
            int ng = GUILayout.SelectionGrid(gi < 0 ? 0 : gi, gnames, Groups.Length, button, GUILayout.Height(lh), GUILayout.Width(inner));
            if (Groups[ng][0] != playGroup) { playGroup = Groups[ng][0]; playCat = ""; pickPlay = null; }
            var cl = host.Play.Categories(playGroup);
            if (cl.Count > 0)
            {
                cl.Insert(0, "すべて");
                int ci = Math.Max(0, cl.IndexOf(playCat.Length == 0 ? "すべて" : playCat));
                int per = cl.Count > 5 ? (cl.Count + 1) / 2 : cl.Count;
                int nc = GUILayout.SelectionGrid(ci, cl.ToArray(), per, button, GUILayout.Height(lh * ((cl.Count + per - 1) / per)), GUILayout.Width(inner));
                playCat = nc == 0 ? "" : cl[nc];
            }
            if (playGroup == "houshi" || playGroup == "sonyu")
                withAdded = GUILayout.Toggle(withAdded, withAdded ? "追加のプレイ（AL）も出す：入" : "追加のプレイ（AL）も出す：切", button, GUILayout.Height(lh), GUILayout.Width(inner));
            scroll = GUILayout.BeginScrollView(scroll);
            if (bodyList)
            {
                bool male = host.YourBody.Value != "female";
                if (GUILayout.Button("標準の体" + (string.IsNullOrEmpty(host.YourBodyCard.Value) ? "（今）" : ""), button, GUILayout.Height(lh))) { host.YourBodyCard.Value = ""; bodyList = false; host.SendCatalog(); }
                foreach (var kv in bodyCards ?? new List<KeyValuePair<string, string>>())
                    if (GUILayout.Button(kv.Value + (kv.Key == host.YourBodyCard.Value ? "（今）" : ""), button, GUILayout.Height(lh))) { host.YourBodyCard.Value = kv.Key; bodyList = false; host.SendCatalog(); }
                GUILayout.Label(male ? "男性の体：キャラメイクで保存した男性のカード" : "女性の体：キャラメイクで保存した女性のカードとキャラカードのフォルダ", small);
            }
            else if (pickPlay != null)
            {
                // 攻守交代 / アナル where the play has them, then the spot: the location's spots this play takes (nearest first)
                var row = host.Play.Row(pickPlay);
                var av = host.Play.Availability(row);
                GUILayout.Label(Json.Str(row, "name") + "：場所を選ぶ", small);
                bool okNow = (bool)av[pickReverse ? "reversed_ok" : "ok"];
                GUILayout.BeginHorizontal();
                if (host.Play.Reversible(row))
                {
                    GUI.enabled = (bool)av["reversed_ok"] || pickReverse;
                    pickReverse = GUILayout.Toggle(pickReverse, "攻守交代", button, GUILayout.Height(lh));
                    GUI.enabled = true;
                }
                if ((bool)av["anal"]) pickAnal = GUILayout.Toggle(pickAnal, "アナル（後ろに入れる）", button, GUILayout.Height(lh));
                GUILayout.EndHorizontal();
                if (!okNow) GUILayout.Label("始められません：" + Json.Str(av, pickReverse ? "reversed_reason" : "reason"), small);
                GUI.enabled = okNow;
                var shown = new HashSet<string>();
                foreach (var sp in host.Play.Spots(row))
                {
                    if (!shown.Add(sp.Name)) continue;              // (the same place's other points: its nearest only)
                    if (GUILayout.Button(sp.Name, button, GUILayout.Height(lh)))
                    {
                        menu = "";
                        string err = host.Play.Begin(pickPlay, "user", sp.Id, pickReverse, pickAnal && (bool)av["anal"]);
                        if (err.Length > 0) MuseStudioPlugin.Log.LogWarning("MuseStudio: " + err);
                        pickPlay = null;
                    }
                }
                GUI.enabled = true;
                if (GUILayout.Button("戻る", button, GUILayout.Height(lh))) pickPlay = null;
            }
            else
            {
                foreach (var p in TabPlays(playGroup))
                {
                    var av = host.Play.Availability(p);
                    bool ok = (bool)av["ok"] || (bool)av["reversed_ok"];
                    GUI.enabled = ok;
                    string why = ok ? ((bool)av["ok"] ? "" : "（攻守交代のみ）") : "（" + Json.Str(av, "reason") + "）";
                    if (GUILayout.Button(Json.Str(p, "name") + why, button, GUILayout.Height(lh))) { pickPlay = Json.Str(p, "id"); pickReverse = !(bool)av["ok"]; pickAnal = false; }
                    GUI.enabled = true;
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
