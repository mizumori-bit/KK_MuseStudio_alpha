using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // The look of MuseStudio's own screens (the user's request of 2026-11-04; DECISIONS "UI デザインの統一"): modern and a
    // little lewd, quiet - deep plum panels, one rose accent, thin lines, small rounded corners, the system's Japanese UI font
    // - with nothing that pulls at the eye (no gold, no ornament, no gradient play): the function first. One skin for all of
    // it (buttons, switches, fields, lists and their scroll bars), set at the start of MuseStudio's own OnGUI only (Unity
    // gives each OnGUI the default skin: the other plugins' windows keep theirs). The launcher's bar uses the same colours.
    public static class Theme
    {
        static Color C(int r, int g, int b, float a = 1f) { return new Color(r / 255f, g / 255f, b / 255f, a); }

        public static readonly Color Ink = C(0x15, 0x10, 0x1a, 0.88f);       // panels, the dialogue box
        public static readonly Color Deep = C(0x0d, 0x0a, 0x10, 0.94f);      // behind the chooser / a dialog
        public static readonly Color Raised = C(0x2a, 0x1e, 0x2f, 0.92f);    // a button
        public static readonly Color Hover = C(0x3c, 0x29, 0x42, 0.96f);
        public static readonly Color Press = C(0x58, 0x2d, 0x4e, 0.98f);
        public static readonly Color On = C(0x6c, 0x29, 0x51, 0.96f);        // a switch that is on
        public static readonly Color Rose = C(0xe2, 0x5d, 0x8f);             // the accent
        public static readonly Color RoseSoft = C(0xf3, 0xa9, 0xc6);
        public static readonly Color Line = C(0xf3, 0xa9, 0xc6, 0.30f);
        public static readonly Color Text = C(0xf5, 0xef, 0xf6);
        public static readonly Color Muted = C(0xc4, 0xb3, 0xc9);
        public static readonly Color Faint = C(0xf5, 0xef, 0xf6, 0.5f);
        public static readonly Color Warn = C(0xff, 0xb7, 0x8c);
        public static readonly Color Sound = C(0xff, 0xd9, 0xa8);
        public static readonly Color Live = C(0xd8, 0x2a, 0x4a, 0.95f);       // the broadcast view's LIVE (a TV's red, toned)

        const int Radius = 6;

        // a rounded rectangle for 9-slicing (its middle stretches): the fill, a line of the border colour round it
        public static Texture2D Rounded(Color fill, Color border, int radius = Radius, float line = 1f)
        {
            int n = radius * 2 + 4;
            var t = new Texture2D(n, n, TextureFormat.ARGB32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            float half = n * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - radius), qy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                    float d = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    float cover = Mathf.Clamp01(0.5f - d);
                    var c = Color.Lerp(fill, border, line <= 0f ? 0f : Mathf.Clamp01(d + line + 0.5f));
                    c.a *= cover;
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            return t;
        }

        public static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // the system's UI font (stage.json ui.fonts: the first installed), else Unity's own
        static Font font;
        static bool fontTried;

        public static Font Font
        {
            get
            {
                if (fontTried) return font;
                fontTried = true;
                try
                {
                    var want = Json.Arr(Json.Obj(MuseStudioPlugin.Instance.Stage.Cfg, "ui"), "fonts");
                    var names = new List<string>();
                    foreach (var w in want ?? new List<object> { "Yu Gothic UI Semibold", "Yu Gothic UI", "Meiryo UI", "Meiryo" }) names.Add(Convert.ToString(w));
                    var have = new HashSet<string>(Font.GetOSInstalledFontNames());
                    var use = names.FindAll(have.Contains);
                    if (use.Count > 0) font = Font.CreateDynamicFontFromOSFont(use.ToArray(), 16);
                    if (font != null) font.hideFlags = HideFlags.HideAndDontSave;
                    FontName = use.Count > 0 ? use[0] : "";
                }
                catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: the UI font: " + e.Message); }
                return font;
            }
        }

        public static string FontName = "";

        static GUISkin skin;
        static float skinFor = -1f;

        public static GUISkin Current { get { return skin; } }      // (checks: outside OnGUI) the skin as last made

        // the skin at a scale (the screen's height / 900)
        public static GUISkin Skin(float scale)
        {
            if (skin != null && Mathf.Approximately(skinFor, scale)) return skin;
            skinFor = scale;
            var def = GUI.skin;
            if (skin == null) { skin = ScriptableObject.CreateInstance<GUISkin>(); skin.hideFlags = HideFlags.HideAndDontSave; }
            int fs = Mathf.RoundToInt(14 * scale);
            skin.font = Font != null ? Font : def.font;
            skin.box = Box(def.box);
            skin.label = Label(def.label, fs, Text);
            skin.button = Button(def.button, fs);
            skin.toggle = Button(def.toggle, fs);
            skin.textField = Field(def.textField, fs);
            skin.textArea = Field(def.textArea, fs);
            skin.window = Box(def.window);
            skin.scrollView = new GUIStyle(def.scrollView);
            skin.verticalScrollbar = Bar(def.verticalScrollbar, true);
            skin.verticalScrollbarThumb = Thumb(def.verticalScrollbarThumb, true);
            skin.verticalScrollbarUpButton = new GUIStyle(def.verticalScrollbarUpButton);
            skin.verticalScrollbarDownButton = new GUIStyle(def.verticalScrollbarDownButton);
            skin.horizontalScrollbar = Bar(def.horizontalScrollbar, false);
            skin.horizontalScrollbarThumb = Thumb(def.horizontalScrollbarThumb, false);
            skin.horizontalScrollbarLeftButton = new GUIStyle(def.horizontalScrollbarLeftButton);
            skin.horizontalScrollbarRightButton = new GUIStyle(def.horizontalScrollbarRightButton);
            skin.horizontalSlider = new GUIStyle(def.horizontalSlider);
            skin.horizontalSliderThumb = new GUIStyle(def.horizontalSliderThumb);
            skin.verticalSlider = new GUIStyle(def.verticalSlider);
            skin.verticalSliderThumb = new GUIStyle(def.verticalSliderThumb);
            skin.customStyles = def.customStyles;
            skin.settings.cursorColor = RoseSoft;
            skin.settings.selectionColor = new Color(Rose.r, Rose.g, Rose.b, 0.45f);
            skin.settings.doubleClickSelectsWord = def.settings.doubleClickSelectsWord;
            skin.settings.tripleClickSelectsLine = def.settings.tripleClickSelectsLine;
            return skin;
        }

        static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();

        static Texture2D Tex(string key, Color fill, Color border, float line = 1f)
        {
            Texture2D t;
            if (!textures.TryGetValue(key, out t) || t == null) textures[key] = t = Rounded(fill, border, Radius, line);
            return t;
        }

        static RectOffset Edge { get { return new RectOffset(Radius + 1, Radius + 1, Radius + 1, Radius + 1); } }

        public static GUIStyle Box(GUIStyle from)
        {
            var s = new GUIStyle(from) { border = Edge, padding = new RectOffset(10, 10, 8, 8) };
            s.normal.background = Tex("box", Ink, Line);
            s.normal.textColor = Text;
            return s;
        }

        public static GUIStyle Label(GUIStyle from, int fs, Color c)
        {
            var s = new GUIStyle(from) { fontSize = fs };
            s.normal.textColor = c;
            return s;
        }

        // a button / a switch drawn as one (GUI.Toggle with it: on is the rose)
        public static GUIStyle Button(GUIStyle from, int fs)
        {
            var s = new GUIStyle(from) { fontSize = fs, border = Edge, padding = new RectOffset(10, 10, 4, 4), alignment = TextAnchor.MiddleCenter, wordWrap = false };
            s.normal.background = Tex("raised", Raised, Line);
            s.hover.background = Tex("hover", Hover, RoseSoft * new Color(1f, 1f, 1f, 0.55f));
            s.active.background = Tex("press", Press, Rose);
            s.focused.background = s.normal.background;
            s.onNormal.background = Tex("on", On, Rose);
            s.onHover.background = Tex("onhover", Press, RoseSoft);
            s.onActive.background = Tex("press", Press, Rose);
            s.onFocused.background = s.onNormal.background;
            s.normal.textColor = s.focused.textColor = Text;
            s.hover.textColor = s.active.textColor = Color.white;
            s.onNormal.textColor = s.onHover.textColor = s.onActive.textColor = s.onFocused.textColor = Color.white;
            return s;
        }

        // a way in (the match's): the rose filled, so it is found
        public static GUIStyle AccentButton(GUIStyle from)
        {
            var s = new GUIStyle(from);
            s.normal.background = Tex("accent", new Color(Rose.r * 0.62f, Rose.g * 0.62f, Rose.b * 0.62f, 0.96f), RoseSoft);
            s.hover.background = Tex("accenthover", new Color(Rose.r * 0.78f, Rose.g * 0.78f, Rose.b * 0.78f, 0.98f), Color.white);
            s.normal.textColor = s.hover.textColor = Color.white;
            return s;
        }

        // a menu that is open (its button reads 閉じる): light, the rose's pale, dark letters - not like any other button
        public static GUIStyle OpenButton(GUIStyle from)
        {
            var s = new GUIStyle(from);
            s.normal.background = Tex("open", new Color(RoseSoft.r, RoseSoft.g, RoseSoft.b, 0.95f), Color.white, 1.5f);
            s.hover.background = Tex("openhover", Color.white, Rose, 1.5f);
            s.active.background = Tex("openpress", RoseSoft, Rose, 1.5f);
            s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = C(0x2a, 0x10, 0x22);
            s.fontStyle = FontStyle.Bold;
            return s;
        }

        public static GUIStyle WarnButton(GUIStyle from)
        {
            var s = new GUIStyle(from);
            s.normal.background = Tex("warn", C(0x5a, 0x24, 0x2c, 0.95f), Warn);
            s.hover.background = Tex("warnhover", C(0x74, 0x2c, 0x36, 0.98f), Warn);
            s.normal.textColor = s.hover.textColor = C(0xff, 0xe2, 0xd2);
            return s;
        }

        public static GUIStyle Field(GUIStyle from, int fs)
        {
            var s = new GUIStyle(from) { fontSize = fs, border = Edge, padding = new RectOffset(8, 8, 4, 4) };
            s.normal.background = Tex("field", C(0x0f, 0x0b, 0x13, 0.92f), Line);
            s.hover.background = Tex("fieldhover", C(0x14, 0x0f, 0x19, 0.95f), RoseSoft * new Color(1f, 1f, 1f, 0.5f));
            s.focused.background = s.onFocused.background = Tex("fieldfocus", C(0x14, 0x0f, 0x19, 0.98f), Rose);
            s.normal.textColor = s.hover.textColor = s.focused.textColor = s.onFocused.textColor = Text;
            return s;
        }

        static GUIStyle Bar(GUIStyle from, bool vertical)
        {
            var s = new GUIStyle(from) { border = new RectOffset(3, 3, 3, 3) };
            s.normal.background = Tex("bar", C(0x0f, 0x0b, 0x13, 0.6f), C(0x0f, 0x0b, 0x13, 0.6f), 0f);
            if (vertical) s.fixedWidth = 8f; else s.fixedHeight = 8f;
            return s;
        }

        static GUIStyle Thumb(GUIStyle from, bool vertical)
        {
            var s = new GUIStyle(from) { border = new RectOffset(3, 3, 3, 3) };
            s.normal.background = Tex("thumb", C(0x8a, 0x4d, 0x72, 0.9f), C(0x8a, 0x4d, 0x72, 0.9f), 0f);
            if (vertical) s.fixedWidth = 8f; else s.fixedHeight = 8f;
            return s;
        }
    }
}
