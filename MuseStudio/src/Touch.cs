using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // Touch (MUSE_GUIDE.md 3.6; data/touch.json; the Godot app's touch_controller.gd / touch_reaction.gd): the mouse on the
    // character. Invisible hit shapes on her bones (spheres / capsules in the bone's frame, so a shaped body is hit where it
    // is drawn) give the area under the mouse (the nearest hit; the small anal wins within priority_slack). A gesture is a
    // tap, a stroke (moving while pressed) or a hold (pressed without moving), with a strength 1-3 (stroke speed / hold
    // time). Each gesture: she reacts at once (the app's own answer, before Muse's) - outside a play the original talk's touch
    // reaction (her expression and pose by the touch point, the count and her personality), during a play a face by the gauge
    // - the gauge rises, and it is counted; the touches of one area end up in one "touch" event when none came for
    // window_seconds. While a press is on her, Studio's camera does not take the mouse. Over her the cursor is the
    // original's (the Godot app's hand): in an aibu play H's icon of the area's item, else its hand. F4 shows the shapes.
    public class Touch
    {
        class Shape { public Transform Bone; public bool Capsule; public Vector3 Center; public float Radius, Height; public int Axis; }
        class Area { public string Key, Name; public int Priority; public readonly List<Shape> Shapes = new List<Shape>(); }
        class Pending
        {
            public int Count, Strength;
            public readonly Dictionary<string, int> Kinds = new Dictionary<string, int>();
            public float First, Last, Seconds;
            public string Item;
            public Dictionary<string, object> Reaction;
        }

        readonly MuseStudioPlugin h;
        public readonly Dictionary<string, object> Doc;
        readonly List<Area> areas = new List<Area>();
        ChaControl bound;
        public string Hover = "", Pressing = "", PressingSide = "", PickSide = "";
        public bool Stroking;
        float pressTime, path;
        Vector2 lastMouse;
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        float lastTouch;
        public string RestoreFace = "";     // the face to put back after a reaction ("" none)
        float restoreAt;
        readonly Dictionary<int, int> talkCounts = new Dictionary<int, int>();
        float talkLast = -1e9f;
        readonly System.Random rng = new System.Random();
        public readonly List<Dictionary<string, object>> Sent = new List<Dictionary<string, object>>();   // (checks)
        public Dictionary<string, object> LastReaction;

        public Touch(MuseStudioPlugin host)
        {
            h = host;
            Doc = Data.Load("touch.json");
        }

        static float Now { get { return Time.realtimeSinceStartup; } }

        public bool Enabled { get { return h.Stage.Ready && !h.Stage.Moving && h.Stage.Cha != null && h.Stage.StudioUiHidden && !h.Chars.Creating; } }

        public string AreaName(string key)
        {
            var a = Json.Obj(Json.Obj(Doc, "areas"), key);
            return Json.Str(a, "name", key);
        }

        // ---- the shapes on her bones (again when she is another body) ----

        public void Rebind() { bound = null; areas.Clear(); Clear(); }

        // ---- the cursor over her: the original's (H's aibu icons by area and item - h/list AibuIconTexture, the kiss's lips;
        // elsewhere its hand without the label; the Godot app's hand cursor) ----

        Texture2D[] icons;
        Texture2D handCursor;
        public int CursorNow = -1;           // the icon shown: -1 the arrow, -2 the plain hand, else AibuIconTexture's id (checks)

        void LoadIcons()
        {
            if (icons != null) return;
            var list = new List<Texture2D>();
            try
            {
                string text = GlobalMethod.LoadAllListText("h/list/", "AibuIconTexture", null);
                string[,] a;
                GlobalMethod.GetListString(text, out a);
                for (int i = 0; i < a.GetLength(0); i++)
                {
                    int id;
                    if (!int.TryParse(a[i, 0], out id) || id < 0) continue;
                    while (list.Count <= id) list.Add(null);
                    list[id] = CommonLib.LoadAsset<Texture2D>(a[i, 1], a[i, 2], false, string.Empty);       // (GameCursor: the first size)
                }
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: cursor icons: " + e.Message); }
            icons = list.ToArray();
            if (icons.Length > 1 && icons[1] != null) handCursor = Unlabelled(icons[1]);
        }

        // an icon's picture without its label (its lower third), moved down to the middle
        static Texture2D Unlabelled(Texture2D src)
        {
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var full = new Texture2D(src.width, src.height, TextureFormat.ARGB32, false);
            full.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            full.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            int w = src.width, hh = src.height, label = (int)(hh * 0.36f), shift = label / 2;
            var o = new Texture2D(w, hh, TextureFormat.ARGB32, false);
            var clear = new Color(0f, 0f, 0f, 0f);
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < w; x++)
                {
                    int sy = y + shift;
                    o.SetPixel(x, y, sy >= label && sy < hh ? full.GetPixel(x, sy) : clear);
                }
            o.Apply();
            return o;
        }

        public bool IconReady(int id) { LoadIcons(); return id == -2 ? handCursor != null : id >= 0 && id < icons.Length && icons[id] != null; }

        public void SetCursor(int id)
        {
            if (id == CursorNow) return;
            CursorNow = id;
            LoadIcons();
            Texture2D t = id >= 0 && id < icons.Length && icons[id] != null ? icons[id] : id == -1 ? null : handCursor;
            if (t == null) Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
            else Cursor.SetCursor(t, new Vector2(t.width * 0.5f, t.height * 0.5f), CursorMode.ForceSoftware);      // (GameCursor's)
        }

        public void CursorOff() { SetCursor(-1); }

        // ---- F4: the hit shapes shown (the Godot app's F4), a colour per area ----

        readonly List<GameObject> shown = new List<GameObject>();
        bool wantShapes;
        public bool ShapesWanted { get { return wantShapes; } }
        public int ShapesShown { get { return shown.Count; } }      // (checks)
        public string ShapeShader = "";

        public void ShowShapes(bool on)
        {
            wantShapes = on;
            Clear();
            if (on && h.Stage.Cha != null) Build();
        }

        void Clear()
        {
            foreach (var g in shown) if (g != null) UnityEngine.Object.Destroy(g);
            shown.Clear();
        }

        void Build()
        {
            if (bound != h.Stage.Cha || areas.Count == 0) Bind();
            // Unity's flat colour, drawn over the body (the shapes lie mostly inside it)
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return;
            ShapeShader = sh.name;
            int layer = h.Stage.Cha.objBody != null ? h.Stage.Cha.objBody.layer : h.Stage.Cha.gameObject.layer;
            int n = 0;
            foreach (var a in areas)
            {
                var mat = new Material(sh);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_Cull", 0);
                mat.SetInt("_ZWrite", 0);
                mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                mat.renderQueue = 3100;
                var c = Color.HSVToRGB((n++ * 0.137f) % 1f, 0.75f, 1f);
                c.a = 0.35f;
                mat.color = c;
                foreach (var sp in a.Shapes)
                {
                    var g = GameObject.CreatePrimitive(sp.Capsule ? PrimitiveType.Capsule : PrimitiveType.Sphere);
                    UnityEngine.Object.Destroy(g.GetComponent<Collider>());
                    g.name = "MuseStudio hit " + a.Key;
                    g.layer = layer;
                    g.transform.SetParent(sp.Bone, false);        // (the bone's frame and scale: as it is hit)
                    g.transform.localPosition = sp.Center;
                    if (sp.Capsule)
                    {
                        // Unity's capsule: 2 tall, radius 0.5, along y
                        g.transform.localScale = new Vector3(sp.Radius * 2f, Mathf.Max(sp.Height, sp.Radius * 2f) * 0.5f, sp.Radius * 2f);
                        g.transform.localRotation = sp.Axis == 0 ? Quaternion.Euler(0f, 0f, 90f) : sp.Axis == 2 ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
                    }
                    else g.transform.localScale = Vector3.one * sp.Radius * 2f;
                    g.GetComponent<Renderer>().sharedMaterial = mat;
                    shown.Add(g);
                }
            }
        }

        void Bind()
        {
            areas.Clear();
            bound = h.Stage.Cha;
            if (bound == null) return;
            var bones = new Dictionary<string, Transform>();
            foreach (var t in bound.objBodyBone.GetComponentsInChildren<Transform>(true)) if (!bones.ContainsKey(t.name)) bones[t.name] = t;
            var src = Json.Obj(Doc, "areas");
            if (src == null) return;
            foreach (var kv in src)
            {
                var a = new Area { Key = kv.Key, Name = Json.Str(kv.Value, "name", kv.Key), Priority = (int)Json.Num(kv.Value, "priority", 0) };
                foreach (var so in Json.Arr(kv.Value, "shapes") ?? new List<object>())
                {
                    Transform b;
                    if (!bones.TryGetValue(Json.Str(so, "bone", ""), out b)) continue;
                    var c = Json.Arr(so, "center");
                    a.Shapes.Add(new Shape { Bone = b, Capsule = Json.Str(so, "type", "sphere") == "capsule",
                        Center = c != null && c.Count >= 3 ? new Vector3(Convert.ToSingle(c[0]), Convert.ToSingle(c[1]), Convert.ToSingle(c[2])) : Vector3.zero,
                        Radius = (float)Json.Num(so, "radius", 0.05), Height = (float)Json.Num(so, "height", 0), Axis = (int)Json.Num(so, "axis", 1) });
                }
                if (a.Shapes.Count > 0) areas.Add(a);
            }
        }

        // ---- picking ----

        // the first hit's ray parameter, +inf for none or a ray starting inside (PhysX does not hit from inside)
        static float RaySphere(Vector3 o, Vector3 d, Vector3 c, float r)
        {
            Vector3 oc = o - c;
            float a = Vector3.Dot(d, d), b = Vector3.Dot(oc, d), cc = Vector3.Dot(oc, oc) - r * r;
            if (cc < 0f) return float.PositiveInfinity;
            float hh = b * b - a * cc;
            if (hh < 0f || a < 1e-12f) return float.PositiveInfinity;
            float t = (-b - Mathf.Sqrt(hh)) / a;
            return t >= 0f ? t : float.PositiveInfinity;
        }

        static float RayCapsule(Vector3 o, Vector3 d, Vector3 p, Vector3 q, float r)
        {
            float t = float.PositiveInfinity;
            Vector3 ba = q - p, oa = o - p;
            float baba = Vector3.Dot(ba, ba), bard = Vector3.Dot(ba, d), baoa = Vector3.Dot(ba, oa), rdoa = Vector3.Dot(d, oa), oaoa = Vector3.Dot(oa, oa);
            float a = baba * Vector3.Dot(d, d) - bard * bard, b = baba * rdoa - baoa * bard, c = baba * oaoa - baoa * baoa - r * r * baba;
            float hh = b * b - a * c;
            if (hh >= 0f && a > 1e-12f)
            {
                float tc = (-b - Mathf.Sqrt(hh)) / a, y = baoa + tc * bard;
                if (tc >= 0f && y > 0f && y < baba) t = tc;
            }
            return Mathf.Min(t, Mathf.Min(RaySphere(o, d, p, r), RaySphere(o, d, q, r)));
        }

        static float Hit(Shape s, Vector3 origin, Vector3 dir)
        {
            // in the bone's frame with its scale (the body shape), the parameter stays the world one
            var m = s.Bone.worldToLocalMatrix;
            Vector3 o = m.MultiplyPoint3x4(origin), d = m.MultiplyVector(dir);
            if (!s.Capsule) return RaySphere(o, d, s.Center, s.Radius);
            float half = Mathf.Max(s.Height * 0.5f - s.Radius, 0f);
            Vector3 ax = Vector3.zero;
            ax[Mathf.Clamp(s.Axis, 0, 2)] = 1f;
            return RayCapsule(o, d, s.Center - ax * half, s.Center + ax * half, s.Radius);
        }

        public string Pick(Vector2 screen)
        {
            if (h.Stage.Cha == null) return "";
            if (bound != h.Stage.Cha || areas.Count == 0) Bind();
            var cam = Camera.main;
            if (cam == null) return "";
            var ray = cam.ScreenPointToRay(screen);
            string best = "", pri = "";
            float bestT = float.PositiveInfinity, priT = float.PositiveInfinity;
            Transform bestBone = null, priBone = null;
            foreach (var a in areas)
                foreach (var s in a.Shapes)
                {
                    float t = Hit(s, ray.origin, ray.direction);
                    if (t < bestT) { bestT = t; best = a.Key; bestBone = s.Bone; }
                    if (a.Priority > 0 && t < priT) { priT = t; pri = a.Key; priBone = s.Bone; }
                }
            if (pri.Length > 0 && !float.IsInfinity(priT) && priT <= bestT + (float)Json.Num(Doc, "priority_slack", 0.15)) { best = pri; bestBone = priBone; }
            string bn = bestBone != null ? bestBone.name : "";
            PickSide = bn.EndsWith("_L") ? "L" : bn.EndsWith("_R") ? "R" : "";
            return best;
        }

        // ---- input (every frame, before Studio's camera reads the mouse) ----

        public void Update()
        {
            if (wantShapes && shown.Count == 0 && h.Stage.Cha != null) Build();
            if (!Enabled)
            {
                Pressing = Hover = "";
                SetCursor(-1);
                return;
            }
            Vector2 mouse = Input.mousePosition;
            Vector2 moved = mouse - lastMouse;
            lastMouse = mouse;
            bool overUi = h.UiOver(mouse) || (h.SpotEditor != null && h.SpotEditor.HoldsMouse);    // (a play position's marker: its press is the editor's)
            var hands = h.Hands;
            if (Pressing.Length == 0) Hover = overUi || h.Match.Active ? "" : Pick(mouse);     // (a match: the users watch - no touch, the click still advances)
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Hover.Length > 0 && wheel != 0f && hands.Wheel(Hover, wheel < 0f ? 1 : -1)) WheelTaken = true;
            else WheelTaken = false;
            if (Hover.Length > 0 && Input.GetMouseButtonDown(1)) hands.DetachArea(Hover, PickSide);
            string over = Pressing.Length > 0 ? Pressing : Hover;
            SetCursor(over.Length == 0 ? -1 : hands.IconFor(over, Pressing.Length > 0 ? PressingSide : PickSide));
            // a click (no drag) off her body and off the UI advances the dialogue (a press on her is a touch)
            if (Input.GetMouseButtonDown(0) && Hover.Length == 0 && !overUi) clickFrom = mouse;
            if (Input.GetMouseButtonUp(0) && clickFrom.x >= 0f)
            {
                if ((mouse - clickFrom).magnitude < 6f && h.Blocks.Busy) h.Blocks.Advance();
                clickFrom = new Vector2(-1f, -1f);
            }
            if (Pressing.Length == 0)
            {
                if (Input.GetMouseButtonDown(0) && Hover.Length > 0)
                {
                    Pressing = Hover;
                    PressingSide = PickSide;
                    pressTime = Now;
                    path = 0f;
                    Stroking = false;
                    gropeTried = false;
                    PressCounted = false;
                }
                return;
            }
            path += Hold ? 0f : moved.magnitude;         // (a check's press: the real mouse does not move it)
            var g = Json.Obj(Doc, "gesture");
            if (!Stroking && path >= Json.Num(g, "stroke_pixels", 14)) Stroking = true;
            // outside a play, holding or rubbing her chest (crotch, hips) gropes her: the standing aibu starts where she is
            // (the user's wish 2026-11-03: not only from the play menu)
            if (!h.Play.Active && !gropeTried && (Stroking || Now - pressTime >= Json.Num(g, "hold_seconds", 0.6)) && GropeStart(Pressing)) gropeTried = true;
            PressGain(g);
            if (Input.GetMouseButton(0) || Hold) return;
            float secs = Now - pressTime;
            string kind = "tap";
            int strength = 1;
            if (Stroking)
            {
                kind = "stroke";
                float speed = path / Mathf.Max(secs, 0.05f);
                var sp = Json.Arr(g, "stroke_speed");
                strength = 1 + (speed >= At(sp, 0, 250f) ? 1 : 0) + (speed >= At(sp, 1, 700f) ? 1 : 0);
            }
            else if (secs >= Json.Num(g, "hold_seconds", 0.6))
            {
                kind = "hold";
                var hs = Json.Arr(g, "hold_strength_seconds");
                strength = 1 + (secs >= At(hs, 0, 2f) ? 1 : 0) + (secs >= At(hs, 1, 4f) ? 1 : 0);
            }
            string area = Pressing;
            Pressing = "";
            Gesture(area, kind, strength, secs, PressCounted);
        }

        bool gropeTried;

        // A press counts while it lasts - a still hold and a stroke alike (the trial runs 5 and 7: first a hold let go was
        // counted once, nothing while pressed; then a still hold rose while held, but a real hand is never still - a few pixels
        // made the press a stroke, counted only when let go, so the gauge jumped at the release; GPT's handoff, PR #5 adopted).
        // Once the press is a hold (hold_seconds without moving) or a stroke (stroke_pixels moved): one touch's worth of that
        // kind at strength 1 at once, then each second the worth at its strength now (a hold's by its time, a stroke's by its
        // speed) / press_worth_seconds; a hold moved on into a stroke goes on as a stroke. Let go, it adds nothing more (counted
        // while pressed: no double count); a tap (let go before either) is counted at its release as ever. The area's item as
        // ever (the aibu's), the gauge's own bounds (at most 1, nothing in the afterglow); the play hears the touch each second
        // (its idle stop).
        public bool PressCounted;             // (this press has been counted while pressed)
        public float PressAdded;              // (checks) what this press added while pressed
        float pressLast, pressTold;

        void PressGain(Dictionary<string, object> g)
        {
            if (Pressing.Length == 0) return;
            float now = Now, held = now - pressTime, holdSec = (float)Json.Num(g, "hold_seconds", 0.6);
            if (!Stroking && held < holdSec) return;       // (a tap so far)
            string kind = Stroking ? "stroke" : "hold";
            var gauge = h.Gauge;
            float factor = h.Hands.GainFactor(Pressing);
            var cause = new Dictionary<string, object> { { "source", "touch" }, { "area", Pressing }, { "kind", kind } };
            float add;
            if (!PressCounted)
            {
                PressCounted = true;
                PressAdded = 0f;
                pressLast = pressTold = now;
                add = gauge.TouchWorth(Pressing, kind, 1) * factor;
                lastTouch = now;
                h.Play.Touched(Pressing);
            }
            else
            {
                int strength;
                if (Stroking)
                {
                    float speed = path / Mathf.Max(held, 0.05f);
                    var sp = Json.Arr(g, "stroke_speed");
                    strength = 1 + (speed >= At(sp, 0, 250f) ? 1 : 0) + (speed >= At(sp, 1, 700f) ? 1 : 0);
                }
                else
                {
                    var hs = Json.Arr(g, "hold_strength_seconds");
                    strength = 1 + (held >= At(hs, 0, 2f) ? 1 : 0) + (held >= At(hs, 1, 4f) ? 1 : 0);
                }
                add = gauge.TouchWorth(Pressing, kind, strength) * factor * (now - pressLast) / Mathf.Max(0.1f, gauge.PressWorthSeconds);
                pressLast = now;
                if (now - pressTold >= 1f) { pressTold = now; lastTouch = now; h.Play.Touched(Pressing); }
            }
            gauge.Add(add, cause);
            PressAdded += add;
        }

        // stage.json grope: the areas, the play (the standing aibu) at "here"; play_started source "touch"
        bool GropeStart(string area)
        {
            var gr = Json.Obj(h.Stage.Cfg, "grope");
            var areas = Json.Arr(gr, "areas");
            string play = Json.Str(gr, "play", "aibu_00");
            if (areas == null || !areas.Contains(area) || h.Outing.Out || h.Chars.Creating || h.Stage.Moving || (h.Kensei != null && h.Kensei.Active)) return false;     // (献精室: the plays are the moves')
            var row = h.Play.Row(play);
            if (row == null || !(bool)h.Play.Availability(row)["ok"]) return false;
            if (h.Play.Begin(play, "touch", "here").Length > 0) return false;
            h.Play.Touched(area);
            return true;
        }

        public bool WheelTaken;
        Vector2 clickFrom = new Vector2(-1f, -1f);
        public bool Hold;                   // (checks) the press stays down without the mouse button

        // (checks) a press on an area, held until Hold is cleared
        public void PressForCheck(string area, string side)
        {
            Pressing = area;
            PressingSide = side;
            pressTime = Now;
            path = 0f;
            Stroking = false;
            PressCounted = false;
            Hold = true;
        }

        static float At(List<object> a, int i, float def) { return a != null && a.Count > i ? Convert.ToSingle(a[i]) : def; }

        // One finished touch (also the entry for checks): reaction, gauge, count.
        // counted: a press already counted while it was pressed (PressGain) - the gauge left alone at its release
        public void Gesture(string area, string kind, int strength, float seconds, bool counted = false)
        {
            float now = Now;
            Pending p;
            if (!pending.TryGetValue(area, out p)) pending[area] = p = new Pending { First = now - seconds };
            p.Count++;
            int n;
            p.Kinds.TryGetValue(kind, out n);
            p.Kinds[kind] = n + 1;
            p.Strength = Mathf.Max(p.Strength, strength);
            p.Last = now;
            p.Seconds += seconds;
            string item = h.Hands.KindFor(area);
            if (item.Length > 0) p.Item = item;      // in an aibu play: the user's item there (hand / finger / tongue / massager / vibe / kiss)
            lastTouch = now;
            var r = React(area);
            if (r != null) p.Reaction = r;
            if (!counted) h.Gauge.Add(h.Gauge.TouchGain(area, kind, strength, seconds) * h.Hands.GainFactor(area),
                new Dictionary<string, object> { { "source", "touch" }, { "area", area }, { "kind", kind } });
            h.Play.Touched(area);
            if (Touched != null) Touched(area);
        }

        public Action<string> Touched;

        // The app's own answer, before Muse's, then the face it had before (unless a package changed it meanwhile): outside a
        // play the original talk's reaction (expression and pose; returned), during a play a face by the gauge. Not during the
        // afterglow (its own face).
        Dictionary<string, object> React(string area)
        {
            var stage = h.Stage;
            if (h.Gauge.Phase == "afterglow") return null;
            if (!h.Play.Active)
            {
                var r = TalkReaction(area);
                if (r == null) return null;
                if (RestoreFace.Length == 0) RestoreFace = stage.Face;
                if (stage.ApplyCells(Json.Arr(r, "cells")))
                {
                    stage.Face = "expr:" + Json.Str(r, "expression", "");
                    restoreAt = Now + (float)Json.Num(Doc, "reaction_seconds", 2.0);
                }
                var an = Json.Arr(r, "anime");
                if (an != null && !h.Blocks.Busy) stage.React(an, (float)Json.Num(Json.Obj(h.Stage.Cfg, "touch"), "pose_max_seconds", 12.0));
                var o = new Dictionary<string, object> { { "level", r["level"] }, { "expression", Json.Str(r, "expression", "") }, { "pose", Json.Str(r, "pose", "") } };
                LastReaction = o;
                return o;
            }
            string face = "";
            foreach (var row in Json.Arr(Json.Obj(Doc, "reactions"), area) ?? new List<object>())
            {
                var rr = (List<object>)row;
                if (h.Gauge.Value >= Convert.ToSingle(rr[0])) face = (string)rr[1];
            }
            if (face.Length == 0) return null;
            if (RestoreFace.Length == 0) RestoreFace = stage.Face;
            stage.ApplyPreset(face);
            restoreAt = Now + (float)Json.Num(Doc, "reaction_seconds", 2.0);
            return null;
        }

        // TalkScene.TouchFunc / Info.GetTouchVoice: a touch counts on its part; the first touch level 0, then 1 by chance
        // ((n - 1) x 50 %, the count then raised by 2), from the fourth 2; the personality's rows for the stage (the relation,
        // a setting) give her expression and pose. The counts start again after reset_seconds without a touch.
        Dictionary<string, object> TalkReaction(string area)
        {
            var talk = Json.Obj(Doc, "talk");
            int part = (int)Json.Num(Json.Obj(talk, "parts"), area, -1);
            if (part < 0) return null;
            float now = Now;
            if (now - talkLast > Json.Num(talk, "reset_seconds", 60.0)) talkCounts.Clear();
            talkLast = now;
            int n;
            talkCounts.TryGetValue(part, out n);
            n++;
            talkCounts[part] = n;
            int level = 0;
            if (n >= 4) level = 2;
            else if (n > 1 && rng.NextDouble() * 100.0 < Mathf.Clamp((n - 1) * 50f, 0f, 100f)) level = 1;
            if (level == 1) talkCounts[part] = n + 2;
            var rows = Json.Obj(talk, "rows");
            string pers = h.Stage.Cha.chaFile.parameter.personality.ToString();
            if (!Json.Has(rows, pers)) pers = ((int)Json.Num(talk, "fallback_personality", 0)).ToString();
            int stage = h.Relationship.Stage((int)Json.Num(talk, "stage", 2));     // (Muse's affection for her person, else the setting)
            var levels = Json.Arr(Json.Obj(Json.Obj(rows, pers), stage.ToString()), part.ToString());
            if (levels == null || level >= levels.Count) return null;
            var r = new Dictionary<string, object>((Dictionary<string, object>)levels[level]);
            r["level"] = level;
            r["count"] = n;
            return r;
        }

        // a package's face wins over a reaction's
        public void PackageFace() { RestoreFace = ""; }

        void Flush()
        {
            foreach (var kv in pending)
            {
                var p = kv.Value;
                string kind = "tap";
                int most = -1;
                foreach (var k in new[] { "tap", "hold", "stroke" })
                {
                    int c;
                    if (p.Kinds.TryGetValue(k, out c) && c > 0 && c >= most) { most = c; kind = k; }
                }
                var kinds = new Dictionary<string, object>();
                foreach (var k in p.Kinds) kinds[k.Key] = k.Value;
                var ev = new Dictionary<string, object> { { "area", kv.Key }, { "area_name", AreaName(kv.Key) }, { "kind", kind }, { "kinds", kinds },
                    { "strength", p.Strength }, { "count", p.Count }, { "duration", Math.Round(Mathf.Max(p.Last - p.First, 0f), 1) },
                    { "pressed", Math.Round(p.Seconds, 1) } };
                if (p.Reaction != null) ev["reaction"] = p.Reaction;   // her last reaction to it (the original's expression and pose names)
                if (p.Item != null) ev["item"] = p.Item;
                ev["gauge"] = h.Gauge.Info();
                ev["location"] = h.Stage.Location;
                ev["scene"] = h.Stage.SceneKey;
                ev["play"] = h.Play.Info();
                ev["mood"] = h.Mood.Info();
                h.Bridge.Event("touch", ev);
                Sent.Add(ev);
            }
            pending.Clear();
        }

        public void Tick()
        {
            float now = Now;
            if (pending.Count > 0 && Pressing.Length == 0 && now - lastTouch >= Json.Num(Doc, "window_seconds", 2.5)) Flush();
            if (RestoreFace.Length > 0 && now >= restoreAt && h.Gauge.Phase != "afterglow")
            {
                h.Stage.RestoreFace(RestoreFace);
                RestoreFace = "";
            }
        }
    }
}
