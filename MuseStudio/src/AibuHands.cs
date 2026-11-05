using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // The user's hands, fingers, tongue and toys on her body during an aibu play, as the original's HandCtrl (the Godot
    // app's aibu_hands.gd): the original's own item prefabs (h/list AibuItemObject) and layer rows (aibulayer_NN), on her
    // body's animator (the H controller Studio plays) and the items' own animators. Each touch area has a chosen item (the
    // wheel over the area, or the panel; SetIconTexture: the area's rows the clothes allow - plays[] by GetClothState: the
    // crotch / anal covered 0, else 2; -1 not now): hand, finger, tongue, massager (denma), vibe. A press on the area puts the
    // item there (SetItem: parented to her bone of the row's nameParents, its EliminateScale fitted to her shape) and it
    // stays resting until a right click on the area, 外す, another item taking its slot, or the play's end. Slots (idUse):
    // the left hand, the right hand, the mouth; a row of "either hand" takes a free one, else the one used longest ago. The
    // press: every item on her plays its click action (layer action 1, front or back by paramFemale.lstFrontAndBehind[0]),
    // Restart; when her click layer has played through it goes on to the drag (action 2; 0 when plays[] is 0) if the press is
    // still held or moved, clicks again if pressed again meanwhile, else finishes (the rest layers stay). The drag: the mouse
    // moves <area>_X / _Y (0..1) on her animator and the items'; the release finishes. speedHand = 1 + the item speed (each
    // press adds, it falls by itself). The kiss: a press on her face (kiss_area) with the mouth free plays her K_Touch /
    // K_Loop, the original's kiss camera, the items on her in their drag; the release ends it. Not here: the sounds (the
    // sound effects come later), the silhouettes, the original's gates (experience, the club's items, the virgin's vibe:
    // Muse decides what she accepts).
    public class AibuHands
    {
        static readonly string[] Kinds = { "hand", "finger", "tongue", "massager", "vibe" };        // AibuItemObject idObj
        public static readonly Dictionary<string, string> KindNames = new Dictionary<string, string> {
            { "hand", "手" }, { "finger", "指" }, { "tongue", "舌" }, { "massager", "電マ" }, { "vibe", "バイブ" }, { "kiss", "キス" } };
        static readonly Dictionary<string, string[]> AreaOf = new Dictionary<string, string[]> {      // touch area -> HandCtrl area by side
            { "chest", new[] { "muneL", "muneR" } }, { "crotch", new[] { "kokan", "kokan" } }, { "hips", new[] { "siriL", "siriR" } }, { "anal", new[] { "anal", "anal" } } };
        static readonly string[] AreaNames = { "muneL", "muneR", "kokan", "anal", "siriL", "siriR" };   // HandCtrl's areas by index
        public static readonly string[] TouchAreas = { "chest", "crotch", "hips", "anal" };

        class Item { public int Id; public GameObject Obj; public Animator Anm; public string Kind = ""; public int Use; public string Prefab = ""; public EliminateScale Elim; public string SeAsset = "", SeFile = ""; }
        struct Pair { public int Item, Body; }
        class Action { public int DynArea = -1; public bool DynEnable; public Pair Front, Back; }
        class Layer
        {
            public int Id, Use, Icon = -1;
            public int[] Objects = { -1, -1 };
            public string[] Parents = { "", "" };
            public int[] Plays = { -1, -1, -1 };
            public int DragType;
            public string DragSeAsset = "", DragSe = "";
            public Action[] Actions = { new Action(), new Action(), new Action() };
            public string Kind = "";
        }
        class On { public Layer L; public Item I; public int Slot; public string Parent; public int ActionNo = -1; public readonly List<int> Body = new List<int>(); public readonly List<int> ItemLayers = new List<int>(); public Transform Loop; public int Cycle = -1; }

        readonly MuseStudioPlugin h;
        readonly Dictionary<string, object> cfg;
        readonly Dictionary<int, string[]> itemRows = new Dictionary<int, string[]>();
        readonly Dictionary<int, Item> built = new Dictionary<int, Item>();
        readonly List<Layer>[] layers = new List<Layer>[6];
        bool loaded;
        public string LastError = "";
        readonly Dictionary<string, string> choice = new Dictionary<string, string> { { "chest", "hand" }, { "crotch", "hand" }, { "hips", "hand" }, { "anal", "hand" } };
        readonly Dictionary<int, On> attached = new Dictionary<int, On>();
        readonly int[] slots = { -1, -1, -1 };          // the left hand, the right hand, the mouth -> the area index on it
        readonly List<int> history = new List<int>();   // area indexes, used longest ago first
        public string Ctrl = "";                        // "" / click / drag / kiss (HandCtrl.ctrl)
        int ctrlArea = -1;
        float pressT;
        bool more;
        string wasPressing = "";
        float speedItem;
        readonly Vector2[] xy = new Vector2[6];
        Vector2 xyKiss;
        Vector2 mousePrev;
        readonly Dictionary<string, string> lastKind = new Dictionary<string, string>();
        public readonly List<Dictionary<string, object>> Log = new List<Dictionary<string, object>>();

        public AibuHands(MuseStudioPlugin host, Dictionary<string, object> touchDoc)
        {
            h = host;
            cfg = Json.Obj(touchDoc, "aibu_hands") ?? new Dictionary<string, object>();
            for (int i = 0; i < 6; i++) xy[i] = new Vector2(0.5f, 0.5f);
        }

        ChaControl Cha { get { return h.Stage.Cha; } }

        static int I(string s, int def = 0) { int v; return int.TryParse(s, out v) ? v : def; }

        static string[][] Rows(string file)
        {
            string text = GlobalMethod.LoadAllListText("h/list/", file, null);
            string[,] a;
            GlobalMethod.GetListString(text, out a);
            var o = new string[a.GetLength(0)][];
            for (int i = 0; i < o.Length; i++)
            {
                o[i] = new string[a.GetLength(1)];
                for (int j = 0; j < a.GetLength(1); j++) o[i][j] = a[i, j] ?? "";
            }
            return o;
        }

        bool Ensure()
        {
            if (loaded) return LastError.Length == 0;
            loaded = true;
            try
            {
                foreach (var r in Rows("AibuItemObject")) if (r.Length >= 10) itemRows[I(r[0])] = r;
                for (int i = 0; i < 6; i++)
                {
                    layers[i] = new List<Layer>();
                    foreach (var r in Rows("aibulayer_" + i.ToString("00")))
                    {
                        if (r.Length < 24) continue;
                        var l = new Layer { Id = I(r[0]), Icon = I(r[1], -1), Use = I(r[2]), DragType = I(r[12]), DragSeAsset = r[13], DragSe = r[14] };
                        var ob = r[4].Split('/');
                        for (int k = 0; k < 2 && k < ob.Length; k++) if (ob[k].Length > 0) l.Objects[k] = I(ob[k], -1);
                        var pa = r[5].Split('/');
                        for (int k = 0; k < 2 && k < pa.Length; k++) l.Parents[k] = pa[k];
                        for (int k = 0; k < 3; k++) l.Plays[k] = I(r[6 + k], -1);
                        for (int a = 0; a < 3; a++)
                        {
                            var d = r[15 + a * 3].Split('/');
                            l.Actions[a].DynArea = I(d[0], -1);
                            l.Actions[a].DynEnable = d.Length == 2 && d[1] == "1";
                            l.Actions[a].Front = PairOf(r[16 + a * 3]);
                            l.Actions[a].Back = PairOf(r[17 + a * 3]);
                        }
                        string[] ir;
                        if (itemRows.TryGetValue(l.Objects[0], out ir)) l.Kind = Kinds[Mathf.Clamp(I(ir[8]), 0, Kinds.Length - 1)];
                        layers[i].Add(l);
                    }
                }
            }
            catch (Exception e)
            {
                LastError = "aibu lists: " + e.Message;
                MuseStudioPlugin.Log.LogError("MuseStudio: " + LastError);
            }
            return LastError.Length == 0;
        }

        static Pair PairOf(string s)
        {
            var p = s.Split('/');
            return p.Length == 2 ? new Pair { Item = I(p[0], -1), Body = I(p[1], -1) } : new Pair { Item = -1, Body = -1 };
        }

        // the item's object (HandCtrl.LoadItemObject, loaded the first time it is used)
        Item Build(int id)
        {
            Item it;
            if (built.TryGetValue(id, out it)) return it;
            string[] r;
            if (!itemRows.TryGetValue(id, out r)) return null;
            var obj = CommonLib.LoadAsset<GameObject>(r[2], r[3], true, r[1]);
            if (obj == null) { MuseStudioPlugin.Log.LogWarning("MuseStudio: aibu item " + id + " " + r[3] + " did not load"); return null; }
            it = new Item { Id = id, Obj = obj, Anm = obj.GetComponent<Animator>(), Kind = Kinds[Mathf.Clamp(I(r[8]), 0, Kinds.Length - 1)], Use = I(r[9]), Prefab = r[3],
                SeAsset = r.Length > 11 ? r[10] : "", SeFile = r.Length > 11 ? r[11] : "" };
            if (r[4].Length > 0)
            {
                var body = Find(obj.transform, r[4]);
                if (body != null)
                {
                    body.gameObject.SetActive(true);
                    // AibuItem.SetHandColor: a hand / tongue takes the man's skin colour (HSceneProc: the male's skinMainColor)
                    var smr = body.GetComponent<SkinnedMeshRenderer>();
                    if (r[6] == "1" && smr != null) smr.material.SetColor(ChaShader._Color, HandColor());
                }
            }
            if (r[7].Length > 0)
            {
                var sil = Find(obj.transform, r[7]);          // the silhouette (the original's simple body option): not drawn
                if (sil != null) sil.gameObject.SetActive(false);
            }
            var el = obj.GetComponentsInChildren<EliminateScale>(true);
            if (el != null && el.Length > 0) { it.Elim = el[el.Length - 1]; it.Elim.LoadList(id); }
            obj.SetActive(false);
            built[id] = it;
            return it;
        }

        // the user's skin: the partner's body's colour; the default body's is white (its skin texture carries the tone, the
        // hand's material takes the colour as it is), so a skin tone then
        Color HandColor()
        {
            var pt = h.Stage.Partner;
            var c = pt != null ? pt.charInfo.chaFile.custom.body.skinMainColor : Color.white;
            return c.r > 0.99f && c.g > 0.99f && c.b > 0.99f ? new Color(0.96f, 0.84f, 0.75f) : c;
        }

        static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var f = Find(t.GetChild(i), name);
                if (f != null) return f;
            }
            return null;
        }

        // ---- the items chosen ----

        public bool OnNow { get { var pc = h.Play; return pc != null && pc.Active && pc.Mode == 0 && !pc.Reversed && pc.HInfo != null && Cha != null; } }

        // HandCtrl.GetClothState: the crotch / the anal covered 0, else 2 (the breasts always 2)
        int Cloth(int ai) { return (ai == 2 || ai == 3) && Cha.IsKokanHide() ? 0 : 2; }

        Layer RowOf(int ai, string kind)
        {
            if (ai < 0 || ai >= 6 || layers[ai] == null) return null;
            foreach (var l in layers[ai]) if (l.Kind == kind && l.Plays[Cloth(ai)] != -1) return l;
            return null;
        }

        static int AreaIndex(string area, string side)
        {
            string[] a;
            if (!AreaOf.TryGetValue(area, out a)) return -1;
            return Array.IndexOf(AreaNames, side == "R" ? a[1] : a[0]);
        }

        // the items the area takes now, in the original's order
        public List<string> KindsFor(string area)
        {
            var o = new List<string>();
            if (!OnNow || !Ensure()) return o;
            int ai = AreaIndex(area, "L");
            foreach (var k in Kinds) if (RowOf(ai, k) != null) o.Add(k);
            return o;
        }

        // the area's item now (the chosen one, or the first the clothes allow)
        public string Chosen(string area)
        {
            var ks = KindsFor(area);
            if (ks.Count == 0) return "";
            string c;
            choice.TryGetValue(area, out c);
            return ks.Contains(c) ? c : ks[0];
        }

        // the next item for the area (the wheel / the panel); an item on the area is swapped at once
        public string Cycle(string area, int dir)
        {
            var ks = KindsFor(area);
            if (ks.Count == 0) return "";
            int i = ks.IndexOf(Chosen(area));
            choice[area] = ks[((i + (dir >= 0 ? 1 : -1)) % ks.Count + ks.Count) % ks.Count];
            foreach (var ai in new List<int>(attached.Keys))
                if (Array.IndexOf(AreaOf[area], AreaNames[ai]) >= 0) Attach(ai, choice[area]);
            Log.Add(new Dictionary<string, object> { { "event", "choice" }, { "area", area }, { "kind", choice[area] } });
            return choice[area];
        }

        public bool Wheel(string area, int dir)
        {
            if (!OnNow || !AreaOf.ContainsKey(area)) return false;
            Cycle(area, dir);
            return true;
        }

        // the cursor's icon over a touch area (HandCtrl: the area's row's icon of h/list AibuIconTexture; the kiss's lips 0 with
        // the mouth free); -2: no item there (the plain hand)
        public int IconFor(string area, string side)
        {
            if (!OnNow || !Ensure()) return -2;
            if (area == Json.Str(cfg, "kiss_area", "cheek") && slots[2] < 0) return 0;
            var l = AreaOf.ContainsKey(area) ? RowOf(AreaIndex(area, side), Chosen(area)) : null;
            return l != null && l.Icon >= 0 ? l.Icon : -2;
        }

        // the item last used on a touch area (touch events): "" outside an aibu play
        public string KindFor(string area)
        {
            string k;
            return OnNow && lastKind.TryGetValue(area, out k) ? k : "";
        }

        public float GainFactor(string area)
        {
            string k = KindFor(area);
            return k.Length > 0 ? (float)Json.Num(Json.Obj(cfg, "gain"), k, 1.0) : 1f;
        }

        public bool Worn(string area)
        {
            string[] a;
            if (!AreaOf.TryGetValue(area, out a)) return false;
            foreach (var ai in attached.Keys) if (Array.IndexOf(a, AreaNames[ai]) >= 0) return true;
            return false;
        }

        // ---- putting on, taking off ----

        bool Attach(int ai, string kind)
        {
            var li = RowOf(ai, kind);
            if (li == null) return false;
            On cur;
            if (attached.TryGetValue(ai, out cur))
            {
                if (cur.L == li) return true;
                Detach(ai);
            }
            int slot = li.Use, k = 0;
            if (slot == 3)
            {
                // either hand: a free one, else the one used longest ago
                slot = -1;
                for (int s = 0; s < 2; s++) if (slots[s] < 0) { slot = s; break; }
                if (slot < 0)
                    foreach (var hi in history)
                        if (attached.ContainsKey(hi) && attached[hi].Slot < 2) { slot = attached[hi].Slot; break; }
                k = Mathf.Max(slot, 0);
            }
            if (slot < 0 || slot > 2) return false;
            if (slots[slot] >= 0) Detach(slots[slot]);
            int obj = li.Objects[k] >= 0 ? li.Objects[k] : li.Objects[0];
            string parent = li.Parents[k].Length > 0 ? li.Parents[k] : li.Parents[0];
            var it = Build(obj);
            if (it == null) return false;
            var bone = Find(Cha.objBodyBone.transform, parent);
            if (bone == null) { MuseStudioPlugin.Log.LogWarning("MuseStudio: aibu parent " + parent + " not on her"); return false; }
            var on = new On { L = li, I = it, Slot = slot, Parent = parent };
            attached[ai] = on;
            slots[slot] = ai;
            history.Remove(ai);
            history.Add(ai);
            it.Obj.SetActive(true);
            it.Obj.transform.SetParent(bone, false);
            if (it.Elim != null) { it.Elim.SetShapeList(ai); it.Elim.chaCtrl = Cha; }
            if (ai <= 1) Shape(ai, true);
            // a toy sounds its own loop while it is on her (HandCtrl.SetItem: the item's SE, looped, at the item)
            if (it.SeAsset.Length > 0 && it.SeFile.Length > 0)
            {
                on.Loop = Sounds.Play(Manager.Sound.Type.GameSE3D, it.SeAsset, it.SeFile, it.Obj.transform.position);
                var src = on.Loop != null ? on.Loop.GetComponent<AudioSource>() : null;
                if (src != null) src.loop = true;
            }
            SetAction(ai, 0);
            Restart();
            Log.Add(new Dictionary<string, object> { { "event", "put" }, { "item", obj }, { "kind", kind }, { "prefab", it.Prefab }, { "area", AreaNames[ai] }, { "parent", parent }, { "slot", slot },
                { "plays", new List<object> { li.Plays[0], li.Plays[1], li.Plays[2] } }, { "cloth", Cloth(ai) }, { "drag", new List<object> { li.Actions[2].Front.Item, li.Actions[2].Front.Body, li.Actions[2].Back.Item, li.Actions[2].Back.Body } } });
            return true;
        }

        // HandCtrl.EnableShape / SetShapeON: a breast with an item on it has its bust shapes held (the hand shapes it)
        void Shape(int lr, bool held)
        {
            Cha.DisableShapeBust(lr, held);
            Cha.DisableShapeNip(lr, held);
            Cha.DisableShapeBodyID(lr, ChaFileDefine.cf_ShapeMaskNipStand, held);
            if (!held) Cha.playDynamicBoneBust(lr == 0 ? ChaInfo.DynamicBoneKind.BreastL : ChaInfo.DynamicBoneKind.BreastR, true);
        }

        void Detach(int ai)
        {
            On a;
            if (!attached.TryGetValue(ai, out a)) return;
            var anim = Cha != null ? Cha.animBody : null;
            foreach (var l in a.Body) if (anim != null) anim.SetLayerWeight(l, 0f);
            if (a.I.Obj != null)
            {
                a.I.Obj.transform.SetParent(null, false);
                a.I.Obj.SetActive(false);
            }
            if (ai <= 1 && Cha != null) Shape(ai, false);
            if (a.Loop != null) { try { Singleton<Manager.Sound>.Instance.Stop(a.Loop); } catch (Exception) { } a.Loop = null; }
            slots[a.Slot] = -1;
            attached.Remove(ai);
            history.Remove(ai);
            if (ctrlArea == ai && Ctrl != "kiss") { Ctrl = ""; ctrlArea = -1; }
            Log.Add(new Dictionary<string, object> { { "event", "remove" }, { "item", a.I.Id }, { "area", AreaNames[ai] } });
        }

        // a right click on a touch area: its item comes off (HandCtrl: DetachItem); the other side's if this one has none
        public bool DetachArea(string area, string side)
        {
            if (!OnNow || !AreaOf.ContainsKey(area)) return false;
            int ai = AreaIndex(area, side);
            if (!attached.ContainsKey(ai))
            {
                int other = AreaIndex(area, side == "R" ? "L" : "R");
                if (!attached.ContainsKey(other)) return false;
                ai = other;
            }
            Detach(ai);
            return true;
        }

        // 外す (HSprite: DetachAllItem)
        public void DetachAll()
        {
            if (Ctrl == "kiss") EndKiss();
            foreach (var ai in new List<int>(attached.Keys)) Detach(ai);
            Ctrl = "";
            ctrlArea = -1;
        }

        public bool Active { get { return attached.Count > 0; } }

        // (checks) the item object on a HandCtrl area (muneL, kokan, ...), null when none
        public Transform ObjectOn(string areaName)
        {
            int ai = Array.IndexOf(AreaNames, areaName);
            On a;
            return ai >= 0 && attached.TryGetValue(ai, out a) && a.I.Obj != null ? a.I.Obj.transform : null;
        }

        // ---- the actions ----

        bool Front { get { var fb = h.Play.HInfo.paramFemale.lstFrontAndBehind; return fb == null || fb.Count == 0 || fb[0] == 0; } }

        Pair PairFor(Layer li, int action) { var a = li.Actions[Mathf.Clamp(action, 0, 2)]; return Front ? a.Front : a.Back; }

        // the row's layer action on top of its rest: the item's layers and her body's layers (the item's others off)
        void SetAction(int ai, int action)
        {
            var a = attached[ai];
            var anim = Cha.animBody;
            Pair rest = PairFor(a.L, 0), act = action > 0 ? PairFor(a.L, action) : new Pair { Item = -1, Body = -1 };
            if (a.I.Anm != null)
                for (int l = 1; l < a.I.Anm.layerCount; l++)
                    a.I.Anm.SetLayerWeight(l, l == rest.Item || l == act.Item ? 1f : 0f);
            foreach (var l in a.Body) anim.SetLayerWeight(l, 0f);
            a.Body.Clear();
            foreach (var p in new[] { rest, act })
                if (p.Body > 0 && p.Body < anim.layerCount) { anim.SetLayerWeight(p.Body, 1f); a.Body.Add(p.Body); }
            a.ActionNo = action;
            var dyn = a.L.Actions[Mathf.Clamp(action, 0, 2)];
            if (dyn.DynArea >= 0) Cha.playDynamicBoneBust(dyn.DynArea, dyn.DynEnable);    // HandCtrl.EnableDynamicBone
            Log.Add(new Dictionary<string, object> { { "event", "action" }, { "area", AreaNames[ai] }, { "action", action }, { "body", new List<object> { rest.Body, act.Body } } });
        }

        // the drag's layer action: 2, or the rest when the clothes allow only that (plays[] 0)
        int DragAction(int ai) { return attached[ai].L.Plays[Cloth(ai)] > 0 ? 2 : 0; }

        void All(int action) { foreach (var ai in new List<int>(attached.Keys)) SetAction(ai, action == 2 ? DragAction(ai) : action); }

        void Restart()
        {
            Cha.setAnimatorParamTrigger("Restart");
            foreach (var a in attached.Values) if (Has(a.I.Anm, "Restart")) a.I.Anm.SetTrigger("Restart");
        }

        // a new press on a touch area (HandCtrl.OnCollision -> SetItem, JudgeProc)
        void Press(string area, string side)
        {
            if (area == Json.Str(cfg, "kiss_area", "cheek") && slots[2] < 0) { StartKiss(); return; }
            if (!AreaOf.ContainsKey(area)) return;
            int ai = AreaIndex(area, side);
            if (Ctrl == "click" && attached.ContainsKey(ai)) { more = true; SpeedUp(); return; }
            string kind = Chosen(area);
            if (kind.Length == 0 || !Attach(ai, kind)) return;
            lastKind[area] = kind;
            h.Play.Touched(area);        // her Touch state (HAibu: the click)
            All(1);
            Restart();
            DragSe();
            SpeedUp();
            Ctrl = "click";
            ctrlArea = ai;
            pressT = 0f;
            more = false;
            xy[ai] = new Vector2(0.5f, 0.5f);
        }

        void SpeedUp() { speedItem = Mathf.Min((float)Json.Num(cfg, "speed_max", 1.5), speedItem + (float)Json.Num(cfg, "speed_per_press", 0.25)); }

        // HandCtrl.FinishAction: the actions off, the rest layers stay
        void Finish()
        {
            All(0);
            Restart();
            Ctrl = "";
            ctrlArea = -1;
            more = false;
        }

        // her click layer has played through (or the click time without one)
        bool ClickDone()
        {
            On a;
            if (!attached.TryGetValue(ctrlArea, out a)) return true;
            var act = PairFor(a.L, 1);
            var anim = Cha.animBody;
            // (the Restart takes effect at the animator's next update: the layer is read a moment after the press)
            if (act.Body > 0 && act.Body < anim.layerCount) return pressT >= 0.2f && !anim.IsInTransition(act.Body) && anim.GetCurrentAnimatorStateInfo(act.Body).normalizedTime >= 1f || pressT > 4f;
            return pressT >= Json.Num(cfg, "click_seconds", 0.8);
        }

        void StartKiss()
        {
            if (!h.Play.Kiss(true)) return;
            lastKind[Json.Str(cfg, "kiss_area", "cheek")] = "kiss";
            All(2);
            h.Play.KissCamera();
            Ctrl = "kiss";
            ctrlArea = -1;
            xyKiss = new Vector2(0.5f, 0.5f);
            Log.Add(new Dictionary<string, object> { { "event", "kiss" } });
        }

        void EndKiss()
        {
            h.Play.Kiss(false);
            h.Play.Camera(false);
            All(0);
            Ctrl = "";
            ctrlArea = -1;
            Log.Add(new Dictionary<string, object> { { "event", "kiss_end" } });
        }

        // the mouse moves the area's X / Y (0..1); the items with drag type 3 follow it
        void Drag(int ai, Vector2 deltaPx)
        {
            float px = (float)Json.Num(cfg, "drag_pixels", 240.0);
            Vector2 p = ai >= 0 ? xy[ai] : xyKiss;
            p = new Vector2(Mathf.Clamp01(p.x + deltaPx.x / px), Mathf.Clamp01(p.y + deltaPx.y / px));
            if (ai < 0) { xyKiss = p; return; }
            xy[ai] = p;
            foreach (var kv in attached) if (kv.Key != ai && kv.Value.L.DragType == 3) xy[kv.Key] = p;
        }

        // the play ended / changed: everything off - and both breasts free again, their shapes and their sway (an action's
        // EnableDynamicBone may have stopped the other breast's than its item's: the user's note of 2026-11-05, her right
        // breast not swaying in her room after the plays)
        public void Clear()
        {
            if (Ctrl == "kiss") h.Play.Kiss(false);
            foreach (var ai in new List<int>(attached.Keys)) Detach(ai);
            if (Cha != null) { Shape(0, false); Shape(1, false); }
            Ctrl = "";
            ctrlArea = -1;
            wasPressing = "";
            lastKind.Clear();
            speedItem = 0f;
        }

        // every frame (after the touch input)
        public void Tick(float dt)
        {
            if (!OnNow)
            {
                if (attached.Count > 0 || Ctrl.Length > 0) Clear();
                wasPressing = "";
                return;
            }
            if (!Ensure()) return;
            var t = h.Touch;
            string pressing = t.Pressing;
            Vector2 mouse = Input.mousePosition;
            Vector2 moved = mouse - mousePrev;
            mousePrev = mouse;
            if (pressing.Length > 0 && pressing != wasPressing) Press(pressing, t.PressingSide);
            wasPressing = pressing;
            pressT += dt;
            if (Ctrl == "click")
            {
                if (ClickDone())
                {
                    if (pressing.Length > 0 && (pressT >= Json.Num(cfg, "drag_seconds", 0.5) || t.Stroking))
                    {
                        All(2);
                        Ctrl = "drag";
                        if (ctrlArea >= 0) xy[ctrlArea] = new Vector2(0.5f, 0.5f);
                    }
                    else if (more) { more = false; pressT = 0f; Restart(); DragSe(); }
                    else Finish();
                }
            }
            else if (Ctrl == "drag")
            {
                if (pressing.Length == 0) Finish();
                else { Drag(ctrlArea, moved); DragCycles(); }
            }
            else if (Ctrl == "kiss")
            {
                if (pressing.Length == 0) EndKiss();
                else Drag(-1, moved);
            }
            speedItem = Mathf.Max(0f, speedItem - (float)Json.Num(cfg, "speed_fall", 0.5) * dt);
            Params();
        }

        // the rows' drag SE at their items (HandCtrl: pathDragSE)
        void DragSe()
        {
            foreach (var a in attached.Values)
                if (a.L.DragSe.Length > 0) Sounds.Play(Manager.Sound.Type.GameSE3D, a.L.DragSeAsset, a.L.DragSe, a.I.Obj.transform.position);
        }

        // the drag SE once per loop of her drag layer
        void DragCycles()
        {
            On a;
            if (!attached.TryGetValue(ctrlArea, out a)) return;
            var act = PairFor(a.L, 2);
            var anim = Cha.animBody;
            if (act.Body <= 0 || act.Body >= anim.layerCount) return;
            int n = Mathf.FloorToInt(anim.GetCurrentAnimatorStateInfo(act.Body).normalizedTime);
            if (a.Cycle >= 0 && n > a.Cycle) DragSe();
            a.Cycle = n;
        }

        // HActionBase / HandCtrl.SetItemAnimatorParam: speedHand, Breast and the areas' X / Y on her and on every item
        void Params()
        {
            var cha = Cha;
            float sh = 1f + speedItem, breast = cha.GetShapeBodyValue(4);
            var anims = new List<Animator> { cha.animBody };
            foreach (var a in attached.Values)
            {
                if (a.I.Anm != null) anims.Add(a.I.Anm);
                if (a.Loop != null) a.Loop.position = a.I.Obj.transform.position;      // the toy's loop follows it
            }
            foreach (var an in anims)
            {
                Float(an, "speedHand", sh);
                if (an != cha.animBody) Float(an, "Breast", breast);
                for (int i = 0; i < 6; i++)
                {
                    Float(an, AreaNames[i] + "_X", xy[i].x);
                    Float(an, AreaNames[i] + "_Y", xy[i].y);
                }
            }
        }

        // a controller's parameters (a missing one is not set: Unity warns about each)
        static readonly Dictionary<int, HashSet<string>> paramCache = new Dictionary<int, HashSet<string>>();

        static bool Has(Animator an, string name)
        {
            if (an == null || an.runtimeAnimatorController == null) return false;
            int id = an.runtimeAnimatorController.GetInstanceID();
            HashSet<string> set;
            if (!paramCache.TryGetValue(id, out set))
            {
                set = new HashSet<string>();
                foreach (var p in an.parameters) set.Add(p.name);
                paramCache[id] = set;
            }
            return set.Contains(name);
        }

        static void Float(Animator an, string name, float v) { if (Has(an, name)) an.SetFloat(name, v); }

        public Dictionary<string, object> Info()
        {
            var onHer = new Dictionary<string, object>();
            foreach (var kv in attached) onHer[AreaNames[kv.Key]] = kv.Value.L.Kind;
            var ch = new Dictionary<string, object>();
            foreach (var kv in choice) ch[kv.Key] = kv.Value;
            return new Dictionary<string, object> { { "choice", ch }, { "attached", onHer }, { "ctrl", Ctrl } };
        }
    }
}
