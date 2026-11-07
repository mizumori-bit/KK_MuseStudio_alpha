using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using ChaCustom;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MuseStudio.Maker
{
    // The creation's changes in the original's maker (PR #12 adopted, 2026-11-06; the user's decision: this project's own - the
    // app keeps the diffs, a general plugin would need the original to pool them). The Godot app's creation_changes.gd on the
    // original's card: every item the maker edits is read each tick against the starting point (the maker's first character,
    // or a card loaded since: a new starting point and a new history); the changed ones are listed with a check each.
    // Unchecking writes the starting value back, checking the user's value; a change to an unchecked item checks it again.
    // Undo / redo (Ctrl+Z / Ctrl+Y, at most 200 steps) go through the edits and the checks; the same item changed again within
    // 0.8 s is one step (a slider's drag). The saved card has what is on the character: the checked changes only. Built by
    // tools/build.py into MuseMaker.dll against the original's Koikatu_Data (the maker is game/Koikatu.exe -femalemaker /
    // -malemaker, opened by MuseStudio's creation).
    [BepInPlugin(GUID, "MuseStudio maker", Version)]
    [BepInProcess("Koikatu")]
    public class MakerPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.metamuse.musestudio.maker";
        public const string Version = "1.0.0";
        internal static MakerPlugin Instance;
        internal static ManualLogSource Log;
        internal MakerChanges Changes;
        string checkOut;
        bool checkStarted;

        void Awake()
        {
            Instance = this;
            Log = Logger;
            Changes = new MakerChanges();
            foreach (var a in Environment.GetCommandLineArgs())
                if (a.StartsWith("--muse-maker-check=")) checkOut = a.Substring("--muse-maker-check=".Length);
            try { Harmony.CreateAndPatchAll(typeof(MakerHooks), GUID); }
            catch (Exception e) { Log.LogWarning("MuseStudio maker: hooks: " + e.Message); }
        }

        void Update()
        {
            Changes.Update();
            if (checkOut != null && !checkStarted && Changes.Ready && Changes.ReadyFor > 3f)
            {
                checkStarted = true;
                StartCoroutine(new MakerCheck(Changes, checkOut).Run());
            }
        }

        void LateUpdate() { Changes.LateUpdate(); }

        void OnGUI() { Changes.OnGUI(); }
    }

    static class MakerHooks
    {
        // a card loaded into the maker's character (its load window, or the check): a new starting point
        [HarmonyPostfix, HarmonyPatch(typeof(ChaFileControl), "LoadFileLimited", new[] { typeof(string), typeof(byte), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
        static void Loaded(ChaFileControl __instance)
        {
            if (MakerPlugin.Instance != null) MakerPlugin.Instance.Changes.CardLoaded(__instance);
        }
    }

    enum Kind { Text, Slider, Color, ListItem, Bool, Number, Vec }

    class Item
    {
        public string Group, Name;
        public Kind Kind;
        public int Cat = -1;              // the list's category (the item's name)
        public bool Loads;                // an item to load: written first when several are written (its colours after it)
        public Func<ChaFileControl, object> Get;
        public Action<ChaFileControl, ChaControl, object> Set;
        public Func<object, string> Fmt;  // (its own wording)
        public string Label { get { return Group + "：" + Name; } }
    }

    class Entry { public object User; public bool Check; }

    class State
    {
        public Dictionary<int, Entry> E = new Dictionary<int, Entry>();
        public int Item = -1;            // the one item this step changed (for the drag's merge)
        public float At;
        public State Clone()
        {
            var s = new State();
            foreach (var kv in E) s.E[kv.Key] = new Entry { User = kv.Value.User, Check = kv.Value.Check };
            return s;
        }
    }

    public class MakerChanges
    {
        const int MaxSteps = 200;
        const float MergeSeconds = 0.8f, TickSeconds = 0.1f;
        List<Item> items = new List<Item>();
        object[] start, cur;
        readonly List<State> hist = new List<State>();
        int pos;
        float nextTick, readySince = -1f;
        int adoptFrames;                 // reads that are our own writes (no step): for some frames and some time
        float adoptUntil;
        public bool Adopting { get { return adoptFrames > 0 || Time.realtimeSinceStartup < adoptUntil; } }
        int resetIn = -1;                // frames until a loaded card becomes the starting point
        ChaControl cha;
        public bool Open;
        Vector2 scroll;
        GameObject blocker;
        RectTransform blockWin, blockBtn, blockAll;     // (blockAll: the whole screen while the panels sync after an apply)
        Font font;
        bool fontTried;
        public int StepsForCheck { get { return hist.Count; } }
        public readonly List<string> LogForCheck = new List<string>();      // (checks) the steps as they came
        public int ResetsForCheck;

        public static ChaControl Cha
        {
            get
            {
                try { return Singleton<CustomBase>.IsInstance() && Singleton<CustomBase>.Instance != null ? Singleton<CustomBase>.Instance.chaCtrl : null; }
                catch (Exception) { return null; }
            }
        }

        public bool Ready
        {
            get
            {
                var c = Cha;
                if (c == null || c.chaFile == null || !c.loadEnd) return false;
                try { var sc = Singleton<Manager.Scene>.Instance; if (sc != null && (sc.IsNowLoading || sc.IsFadeNow)) return false; } catch (Exception) { }
                return true;
            }
        }

        public float ReadyFor { get { return readySince < 0f ? 0f : Time.realtimeSinceStartup - readySince; } }

        public void CardLoaded(ChaFileControl f)
        {
            var c = Cha;
            if (c != null && ReferenceEquals(c.chaFile, f)) resetIn = 3;
        }

        public void Update()
        {
            if (!Ready)
            {
                readySince = -1f;
                if (cha != null) { cha = null; start = null; Open = false; }
                Block(false);
                return;
            }
            if (readySince < 0f) readySince = Time.realtimeSinceStartup;
            if (cha != Cha || start == null)
            {
                if (ReadyFor < 1.5f) return;         // (the maker's first character settles)
                Reset();
            }
            if (resetIn >= 0 && resetIn-- == 0) Reset();
            if (adoptFrames > 0) adoptFrames--;
            if (Time.realtimeSinceStartup >= nextTick) { nextTick = Time.realtimeSinceStartup + TickSeconds; Tick(); }
            Keys();
        }

        // the starting point: what the character is now; a new history
        public void Reset()
        {
            cha = Cha;
            items = Build(cha.chaFile);
            start = new object[items.Count];
            cur = new object[items.Count];
            for (int i = 0; i < items.Count; i++) start[i] = cur[i] = Read(i);
            hist.Clear();
            hist.Add(new State());
            pos = 0;
            Adopt();                     // (the maker's panels read the card again and write their values back)
            ResetsForCheck++;
            MakerPlugin.Log.LogInfo("MuseStudio maker: the starting point (" + items.Count + " items)");
        }

        object Read(int i)
        {
            try { return items[i].Get(cha.chaFile); } catch (Exception) { return null; }
        }

        public void Tick()
        {
            if (cha == null || start == null) return;
            var changed = new List<int>();
            for (int i = 0; i < items.Count; i++)
            {
                var v = Read(i);
                if (!Same(v, cur[i])) { cur[i] = v; changed.Add(i); }
            }
            if (Adopting)
            {
                if (changed.Count > 0) LogForCheck.Add(string.Format("{0:F1} adopted {1}", Time.realtimeSinceStartup, Labels(changed)));
                return;
            }
            if (changed.Count == 0) return;
            LogForCheck.Add(string.Format("{0:F1} changed {1}", Time.realtimeSinceStartup, Labels(changed)));
            var s = hist[pos].Clone();
            foreach (int i in changed)
            {
                if (Same(cur[i], start[i])) s.E.Remove(i);
                else s.E[i] = new Entry { User = cur[i], Check = true };
            }
            float now = Time.realtimeSinceStartup;
            bool merge = changed.Count == 1 && pos > 0 && pos == hist.Count - 1 && hist[pos].Item == changed[0] && now - hist[pos].At < MergeSeconds;
            s.Item = changed.Count == 1 ? changed[0] : -1;
            s.At = now;
            if (merge) hist[pos] = s;
            else Push(s);
        }

        string Labels(List<int> ids)
        {
            var b = new List<string>();
            for (int k = 0; k < ids.Count && k < 6; k++) b.Add(items[ids[k]].Label + "=" + Text(ids[k], cur[ids[k]]));
            return string.Join(", ", b.ToArray()) + (ids.Count > 6 ? " (+" + (ids.Count - 6) + ")" : "");
        }

        void Push(State s)
        {
            if (pos < hist.Count - 1) hist.RemoveRange(pos + 1, hist.Count - pos - 1);
            hist.Add(s);
            pos = hist.Count - 1;
            while (hist.Count > MaxSteps + 1) { hist.RemoveAt(0); pos--; }
        }

        // ---- what the list shows, and the user's acts ----

        public int Count { get { return hist.Count > 0 ? hist[pos].E.Count : 0; } }
        public bool CanUndo { get { return pos > 0; } }
        public bool CanRedo { get { return pos < hist.Count - 1; } }

        public int Find(string group, string name)
        {
            for (int i = 0; i < items.Count; i++) if (items[i].Group == group && items[i].Name == name) return i;
            return -1;
        }

        public bool Listed(int i) { return hist.Count > 0 && hist[pos].E.ContainsKey(i); }
        public bool Checked(int i) { Entry e; return hist.Count > 0 && hist[pos].E.TryGetValue(i, out e) && e.Check; }
        public object Now(int i) { return Read(i); }
        public object Start(int i) { return start != null && i >= 0 && i < start.Length ? start[i] : null; }
        public object User(int i) { Entry e; return hist.Count > 0 && hist[pos].E.TryGetValue(i, out e) ? e.User : null; }

        // (GPT's post-review of #17) the user's acts look at the character first: an edit made since the last look (0.1 s) is
        // a step of its own before the act - undo then takes that edit back and redo brings it, a check keeps it listed
        void Catch() { if (!Adopting) Tick(); }

        public void Toggle(int i)
        {
            Catch();
            Entry e;
            if (!hist[pos].E.TryGetValue(i, out e)) return;
            var s = hist[pos].Clone();
            s.E[i].Check = !e.Check;
            s.Item = -1;
            s.At = Time.realtimeSinceStartup;
            Push(s);
            Apply(s);
        }

        public void Undo() { Catch(); if (!CanUndo) return; pos--; Apply(hist[pos]); }
        public void Redo() { Catch(); if (!CanRedo) return; pos++; Apply(hist[pos]); }

        // the character as the state has it: the checked items the user's value, the rest the starting one; the items to load
        // first, then their colours and the rest; one reload, the maker's panels read again
        void Apply(State s)
        {
            var f = cha.chaFile;
            var writes = new List<int>();
            for (int i = 0; i < items.Count; i++)
            {
                object want = Want(s, i);
                if (!Same(Read(i), want)) writes.Add(i);
            }
            writes.Sort((a, b) => items[a].Loads == items[b].Loads ? a.CompareTo(b) : items[a].Loads ? -1 : 1);
            foreach (int i in writes)
            {
                try { items[i].Set(f, cha, Want(s, i)); }
                catch (Exception e) { MakerPlugin.Log.LogWarning("MuseStudio maker: write " + items[i].Label + ": " + e.Message); }
            }
            if (writes.Count > 0)
            {
                try
                {
                    cha.Reload();
                    cha.UpdateAccessoryMoveAllFromInfo();
                    for (int k = 0; k < cha.nowCoordinate.accessory.parts.Length; k++) cha.ChangeAccessoryColor(k);
                    for (int k = 0; k < 4; k++) cha.ChangeSettingHairColor(k, true, true, true);
                }
                catch (Exception e) { MakerPlugin.Log.LogWarning("MuseStudio maker: reload: " + e.Message); }
                try { Singleton<CustomBase>.Instance.updateCustomUI = true; } catch (Exception) { }
            }
            for (int i = 0; i < items.Count; i++) cur[i] = Read(i);
            Adopt();                     // (the maker's panels write their values back as they read them)
        }

        void Adopt() { adoptFrames = 4; adoptUntil = Time.realtimeSinceStartup + 0.4f; }

        object Want(State s, int i)
        {
            Entry e;
            return s.E.TryGetValue(i, out e) && e.Check ? e.User : start[i];
        }

        void Keys()
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (!ctrl || Typing()) return;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetKeyDown(KeyCode.Z)) { if (shift) Redo(); else Undo(); }
            else if (Input.GetKeyDown(KeyCode.Y)) Redo();
        }

        static bool Typing()
        {
            try
            {
                var es = EventSystem.current;
                var go = es != null ? es.currentSelectedGameObject : null;
                if (go == null) return false;
                var inf = go.GetComponent<InputField>();
                if (inf != null) return inf.isFocused;
                var tmp = go.GetComponent("TMP_InputField");        // (TextMeshPro's, by name: the maker's name boxes)
                var pi = tmp != null ? tmp.GetType().GetProperty("isFocused") : null;
                return pi != null && (bool)pi.GetValue(tmp, null);
            }
            catch (Exception) { return false; }
        }

        // ---- values ----

        public static bool Same(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is float && b is float) return Mathf.Abs((float)a - (float)b) < 0.0005f;
            if (a is Color && b is Color)
            {
                Color x = (Color)a, y = (Color)b;
                return Mathf.Abs(x.r - y.r) < 0.002f && Mathf.Abs(x.g - y.g) < 0.002f && Mathf.Abs(x.b - y.b) < 0.002f && Mathf.Abs(x.a - y.a) < 0.002f;
            }
            if (a is Vector3 && b is Vector3) return ((Vector3)a - (Vector3)b).sqrMagnitude < 1e-7f;
            if (a is Vector4 && b is Vector4) return ((Vector4)a - (Vector4)b).sqrMagnitude < 1e-7f;
            return a.Equals(b);
        }

        public string Text(int i, object v)
        {
            var it = items[i];
            if (it.Fmt != null) return it.Fmt(v);
            if (v == null) return "";
            switch (it.Kind)
            {
                case Kind.Slider: return Mathf.RoundToInt((float)v * 100f).ToString();
                case Kind.Color: return "#" + ColorUtility.ToHtmlStringRGB((Color)v).ToLowerInvariant();
                case Kind.ListItem:
                    {
                        int id = Convert.ToInt32(v);
                        return ItemName(it.Cat, id) ?? (id == 0 ? "なし" : id.ToString());
                    }
                case Kind.Bool: return (bool)v ? "あり" : "なし";
                case Kind.Vec:
                    {
                        if (v is Vector4) { var q = (Vector4)v; return string.Format("{0:0.##}, {1:0.##}, {2:0.##}, {3:0.##}", q.x, q.y, q.z, q.w); }
                        var w = (Vector3)v;
                        return string.Format("{0:0.##}, {1:0.##}, {2:0.##}", w.x, w.y, w.z);
                    }
                default: return Convert.ToString(v);
            }
        }

        static string ItemName(int cat, int id)
        {
            if (cat < 0) return null;
            try
            {
                var li = Singleton<Manager.Character>.Instance.chaListCtrl.GetListInfo((ChaListDefine.CategoryNo)cat, id);
                return li != null ? li.Name : null;
            }
            catch (Exception) { return null; }
        }

        // ---- the items (the units the creation's `changed` has: the profile, each slider, the face and the body, the hair, every
        // outfit's clothes and accessories) ----

        static readonly string[] HairNames = { "後ろ髪", "前髪", "横髪", "エクステ" };
        static readonly string[] SlotNames = { "トップス", "ボトムス", "ブラ", "ショーツ", "手袋", "パンスト", "靴下", "上履き", "外履き" };
        static readonly string[] CoordNames = { "制服", "下校", "体操着", "水着", "部活", "私服", "お泊り" };
        static readonly string[] AccTypes = { "なし", "髪", "頭", "顔", "首", "胴", "腰", "脚", "腕", "手", "股間" };
        static readonly string[] Attributes = { "hinnyo:トイレが近い", "harapeko:腹ペコ", "donkan:鈍感", "choroi:チョロい", "bitch:ビッチ", "mutturi:むっつり", "dokusyo:読書好き",
            "ongaku:音楽好き", "kappatu:活発", "ukemi:受け身", "friendly:友好的", "kireizuki:きれい好き", "taida:怠惰", "sinsyutu:神出鬼没", "hitori:孤独好き",
            "undo:運動好き", "majime:真面目", "likeGirls:女好き" };
        static readonly string[] Likes = { "animal:動物", "eat:食べること", "cook:料理", "exercise:運動", "study:勉強", "fashionable:おしゃれ", "blackCoffee:ブラックコーヒー",
            "spicy:辛い物", "sweet:甘い物" };
        static readonly string[] Denials = { "kiss:キス", "aibu:愛撫", "anal:アナル", "massage:バイブ", "notCondom:生" };

        List<Item> Build(ChaFileControl f0)
        {
            var o = new List<Item>();
            void add(string g, string n, Kind k, int cat, bool loads, Func<ChaFileControl, object> get, Action<ChaFileControl, object> set)
            {
                o.Add(new Item { Group = g, Name = n, Kind = k, Cat = cat, Loads = loads, Get = get, Set = (f, c, v) => set(f, v) });
            }
            // the profile
            add("プロフィール", "姓", Kind.Text, -1, false, f => f.parameter.lastname, (f, v) => f.parameter.lastname = (string)v);
            add("プロフィール", "名", Kind.Text, -1, false, f => f.parameter.firstname, (f, v) => f.parameter.firstname = (string)v);
            add("プロフィール", "あだ名", Kind.Text, -1, false, f => f.parameter.nickname, (f, v) => f.parameter.nickname = (string)v);
            o.Add(new Item { Group = "プロフィール", Name = "性格", Kind = Kind.Number, Get = f => f.parameter.personality, Set = (f, c, v) => f.parameter.personality = (int)v, Fmt = v => Personality((int)v) });
            add("プロフィール", "声の高さ", Kind.Slider, -1, false, f => f.parameter.voiceRate, (f, v) => f.parameter.voiceRate = (float)v);
            o.Add(new Item { Group = "プロフィール", Name = "血液型", Kind = Kind.Number, Get = f => (int)f.parameter.bloodType, Set = (f, c, v) => f.parameter.bloodType = (byte)(int)v,
                Fmt = v => { int b = (int)v; return b == 0 ? "A" : b == 1 ? "B" : b == 2 ? "O" : b == 3 ? "AB" : b.ToString(); } });
            o.Add(new Item { Group = "プロフィール", Name = "誕生日", Kind = Kind.Text, Get = f => f.parameter.birthMonth + "/" + f.parameter.birthDay,
                Set = (f, c, v) => { var p = ((string)v).Split('/'); f.parameter.birthMonth = byte.Parse(p[0]); f.parameter.birthDay = byte.Parse(p[1]); }, Fmt = v => ((string)v).Replace("/", "月") + "日" });
            add("プロフィール", "部活", Kind.Number, -1, false, f => (int)f.parameter.clubActivities, (f, v) => f.parameter.clubActivities = (byte)(int)v);
            add("プロフィール", "弱点", Kind.Number, -1, false, f => f.parameter.weakPoint, (f, v) => f.parameter.weakPoint = (int)v);
            add("プロフィール", "積極性", Kind.Number, -1, false, f => f.parameter.aggressive, (f, v) => f.parameter.aggressive = (int)v);
            add("プロフィール", "勤勉さ", Kind.Number, -1, false, f => f.parameter.diligence, (f, v) => f.parameter.diligence = (int)v);
            add("プロフィール", "優しさ", Kind.Number, -1, false, f => f.parameter.kindness, (f, v) => f.parameter.kindness = (int)v);
            o.Add(Flags("プロフィール", "属性", Attributes, f => f.parameter.attribute));
            o.Add(Flags("プロフィール", "好きなもの", Likes, f => f.parameter.awnser));
            o.Add(Flags("プロフィール", "H で嫌がること", Denials, f => f.parameter.denial));
            // the sliders
            var bodyNames = ChaFileDefine.cf_bodyshapename;
            for (int i = 0; i < f0.custom.body.shapeValueBody.Length; i++)
            {
                int k = i;
                add("体型", k < bodyNames.Length ? bodyNames[k] : k.ToString(), Kind.Slider, -1, false, f => f.custom.body.shapeValueBody[k], (f, v) => f.custom.body.shapeValueBody[k] = (float)v);
            }
            var faceNames = ChaFileDefine.cf_headshapename;
            for (int i = 0; i < f0.custom.face.shapeValueFace.Length; i++)
            {
                int k = i;
                add("顔", k < faceNames.Length ? faceNames[k] : k.ToString(), Kind.Slider, -1, false, f => f.custom.face.shapeValueFace[k], (f, v) => f.custom.face.shapeValueFace[k] = (float)v);
            }
            // the body
            add("体", "肌の色", Kind.Color, -1, false, f => f.custom.body.skinMainColor, (f, v) => f.custom.body.skinMainColor = (Color)v);
            add("体", "肌の影の色", Kind.Color, -1, false, f => f.custom.body.skinSubColor, (f, v) => f.custom.body.skinSubColor = (Color)v);
            add("体", "肌のツヤ", Kind.Slider, -1, false, f => f.custom.body.skinGlossPower, (f, v) => f.custom.body.skinGlossPower = (float)v);
            add("体", "肌の質感", Kind.ListItem, 420, true, f => f.custom.body.detailId, (f, v) => f.custom.body.detailId = (int)v);
            add("体", "肌の質感の強さ", Kind.Slider, -1, false, f => f.custom.body.detailPower, (f, v) => f.custom.body.detailPower = (float)v);
            add("体", "胸の柔らかさ", Kind.Slider, -1, false, f => f.custom.body.bustSoftness, (f, v) => f.custom.body.bustSoftness = (float)v);
            add("体", "胸の重さ", Kind.Slider, -1, false, f => f.custom.body.bustWeight, (f, v) => f.custom.body.bustWeight = (float)v);
            add("体", "日焼け", Kind.ListItem, 422, true, f => f.custom.body.sunburnId, (f, v) => f.custom.body.sunburnId = (int)v);
            add("体", "日焼けの色", Kind.Color, -1, false, f => f.custom.body.sunburnColor, (f, v) => f.custom.body.sunburnColor = (Color)v);
            add("体", "乳首", Kind.ListItem, 423, true, f => f.custom.body.nipId, (f, v) => f.custom.body.nipId = (int)v);
            add("体", "乳首の色", Kind.Color, -1, false, f => f.custom.body.nipColor, (f, v) => f.custom.body.nipColor = (Color)v);
            add("体", "乳首のツヤ", Kind.Slider, -1, false, f => f.custom.body.nipGlossPower, (f, v) => f.custom.body.nipGlossPower = (float)v);
            add("体", "乳輪の大きさ", Kind.Slider, -1, false, f => f.custom.body.areolaSize, (f, v) => f.custom.body.areolaSize = (float)v);
            add("体", "アンダーヘア", Kind.ListItem, 424, true, f => f.custom.body.underhairId, (f, v) => f.custom.body.underhairId = (int)v);
            add("体", "アンダーヘアの色", Kind.Color, -1, false, f => f.custom.body.underhairColor, (f, v) => f.custom.body.underhairColor = (Color)v);
            add("体", "爪の色", Kind.Color, -1, false, f => f.custom.body.nailColor, (f, v) => f.custom.body.nailColor = (Color)v);
            add("体", "爪のツヤ", Kind.Slider, -1, false, f => f.custom.body.nailGlossPower, (f, v) => f.custom.body.nailGlossPower = (float)v);
            add("体", "肌の線", Kind.Bool, -1, false, f => f.custom.body.drawAddLine, (f, v) => f.custom.body.drawAddLine = (bool)v);
            for (int i = 0; i < f0.custom.body.paintId.Length; i++)
            {
                int k = i;
                add("体", "ボディペイント" + (k + 1), Kind.ListItem, 421, true, f => f.custom.body.paintId[k], (f, v) => f.custom.body.paintId[k] = (int)v);
                add("体", "ボディペイント" + (k + 1) + "の色", Kind.Color, -1, false, f => f.custom.body.paintColor[k], (f, v) => f.custom.body.paintColor[k] = (Color)v);
                add("体", "ボディペイント" + (k + 1) + "の位置", Kind.Number, -1, false, f => f.custom.body.paintLayoutId[k], (f, v) => f.custom.body.paintLayoutId[k] = (int)v);
            }
            // the face
            add("顔の部品", "輪郭", Kind.ListItem, 100, true, f => f.custom.face.headId, (f, v) => f.custom.face.headId = (int)v);
            add("顔の部品", "肌の質感", Kind.ListItem, 400, true, f => f.custom.face.detailId, (f, v) => f.custom.face.detailId = (int)v);
            add("顔の部品", "肌の質感の強さ", Kind.Slider, -1, false, f => f.custom.face.detailPower, (f, v) => f.custom.face.detailPower = (float)v);
            add("顔の部品", "頬のツヤ", Kind.Slider, -1, false, f => f.custom.face.cheekGlossPower, (f, v) => f.custom.face.cheekGlossPower = (float)v);
            add("目・眉", "眉", Kind.ListItem, 406, true, f => f.custom.face.eyebrowId, (f, v) => f.custom.face.eyebrowId = (int)v);
            add("目・眉", "眉の色", Kind.Color, -1, false, f => f.custom.face.eyebrowColor, (f, v) => f.custom.face.eyebrowColor = (Color)v);
            for (int e = 0; e < f0.custom.face.pupil.Length; e++)
            {
                int k = e;
                string side = k == 0 ? "右" : "左";
                add("目・眉", "瞳（" + side + "）", Kind.ListItem, 408, true, f => f.custom.face.pupil[k].id, (f, v) => f.custom.face.pupil[k].id = (int)v);
                add("目・眉", "瞳（" + side + "）の色", Kind.Color, -1, false, f => f.custom.face.pupil[k].baseColor, (f, v) => f.custom.face.pupil[k].baseColor = (Color)v);
                add("目・眉", "瞳（" + side + "）の影の色", Kind.Color, -1, false, f => f.custom.face.pupil[k].subColor, (f, v) => f.custom.face.pupil[k].subColor = (Color)v);
                add("目・眉", "瞳（" + side + "）のグラデ", Kind.ListItem, 409, true, f => f.custom.face.pupil[k].gradMaskId, (f, v) => f.custom.face.pupil[k].gradMaskId = (int)v);
                add("目・眉", "瞳（" + side + "）のグラデの強さ", Kind.Slider, -1, false, f => f.custom.face.pupil[k].gradBlend, (f, v) => f.custom.face.pupil[k].gradBlend = (float)v);
            }
            add("目・眉", "瞳の幅", Kind.Slider, -1, false, f => f.custom.face.pupilWidth, (f, v) => f.custom.face.pupilWidth = (float)v);
            add("目・眉", "瞳の高さ", Kind.Slider, -1, false, f => f.custom.face.pupilHeight, (f, v) => f.custom.face.pupilHeight = (float)v);
            add("目・眉", "瞳の横の位置", Kind.Slider, -1, false, f => f.custom.face.pupilX, (f, v) => f.custom.face.pupilX = (float)v);
            add("目・眉", "瞳の縦の位置", Kind.Slider, -1, false, f => f.custom.face.pupilY, (f, v) => f.custom.face.pupilY = (float)v);
            add("目・眉", "白目", Kind.ListItem, 407, true, f => f.custom.face.whiteId, (f, v) => f.custom.face.whiteId = (int)v);
            add("目・眉", "白目の色", Kind.Color, -1, false, f => f.custom.face.whiteBaseColor, (f, v) => f.custom.face.whiteBaseColor = (Color)v);
            add("目・眉", "白目の影の色", Kind.Color, -1, false, f => f.custom.face.whiteSubColor, (f, v) => f.custom.face.whiteSubColor = (Color)v);
            add("目・眉", "ハイライト上", Kind.ListItem, 410, true, f => f.custom.face.hlUpId, (f, v) => f.custom.face.hlUpId = (int)v);
            add("目・眉", "ハイライト上の色", Kind.Color, -1, false, f => f.custom.face.hlUpColor, (f, v) => f.custom.face.hlUpColor = (Color)v);
            add("目・眉", "ハイライト上の位置", Kind.Slider, -1, false, f => f.custom.face.hlUpY, (f, v) => f.custom.face.hlUpY = (float)v);
            add("目・眉", "ハイライト下", Kind.ListItem, 411, true, f => f.custom.face.hlDownId, (f, v) => f.custom.face.hlDownId = (int)v);
            add("目・眉", "ハイライト下の色", Kind.Color, -1, false, f => f.custom.face.hlDownColor, (f, v) => f.custom.face.hlDownColor = (Color)v);
            add("目・眉", "ハイライト下の位置", Kind.Slider, -1, false, f => f.custom.face.hlDownY, (f, v) => f.custom.face.hlDownY = (float)v);
            add("目・眉", "アイライン上", Kind.ListItem, 412, true, f => f.custom.face.eyelineUpId, (f, v) => f.custom.face.eyelineUpId = (int)v);
            add("目・眉", "アイライン上の太さ", Kind.Slider, -1, false, f => f.custom.face.eyelineUpWeight, (f, v) => f.custom.face.eyelineUpWeight = (float)v);
            add("目・眉", "アイライン下", Kind.ListItem, 413, true, f => f.custom.face.eyelineDownId, (f, v) => f.custom.face.eyelineDownId = (int)v);
            add("目・眉", "アイラインの色", Kind.Color, -1, false, f => f.custom.face.eyelineColor, (f, v) => f.custom.face.eyelineColor = (Color)v);
            add("顔の部品", "鼻", Kind.ListItem, 414, true, f => f.custom.face.noseId, (f, v) => f.custom.face.noseId = (int)v);
            add("顔の部品", "八重歯", Kind.Bool, -1, false, f => f.custom.face.doubleTooth, (f, v) => f.custom.face.doubleTooth = (bool)v);
            add("メイク・肌", "ほくろ", Kind.ListItem, 415, true, f => f.custom.face.moleId, (f, v) => f.custom.face.moleId = (int)v);
            add("メイク・肌", "ほくろの色", Kind.Color, -1, false, f => f.custom.face.moleColor, (f, v) => f.custom.face.moleColor = (Color)v);
            add("メイク・肌", "ほくろの位置", Kind.Vec, -1, false, f => f.custom.face.moleLayout, (f, v) => f.custom.face.moleLayout = (Vector4)v);
            add("メイク・肌", "リップライン", Kind.ListItem, 404, true, f => f.custom.face.lipLineId, (f, v) => f.custom.face.lipLineId = (int)v);
            add("メイク・肌", "リップラインの色", Kind.Color, -1, false, f => f.custom.face.lipLineColor, (f, v) => f.custom.face.lipLineColor = (Color)v);
            add("メイク・肌", "唇のツヤ", Kind.Slider, -1, false, f => f.custom.face.lipGlossPower, (f, v) => f.custom.face.lipGlossPower = (float)v);
            add("メイク・肌", "アイシャドウ", Kind.ListItem, 401, true, f => f.custom.face.baseMakeup.eyeshadowId, (f, v) => f.custom.face.baseMakeup.eyeshadowId = (int)v);
            add("メイク・肌", "アイシャドウの色", Kind.Color, -1, false, f => f.custom.face.baseMakeup.eyeshadowColor, (f, v) => f.custom.face.baseMakeup.eyeshadowColor = (Color)v);
            add("メイク・肌", "チーク", Kind.ListItem, 402, true, f => f.custom.face.baseMakeup.cheekId, (f, v) => f.custom.face.baseMakeup.cheekId = (int)v);
            add("メイク・肌", "チークの色", Kind.Color, -1, false, f => f.custom.face.baseMakeup.cheekColor, (f, v) => f.custom.face.baseMakeup.cheekColor = (Color)v);
            add("メイク・肌", "口紅", Kind.ListItem, 403, true, f => f.custom.face.baseMakeup.lipId, (f, v) => f.custom.face.baseMakeup.lipId = (int)v);
            add("メイク・肌", "口紅の色", Kind.Color, -1, false, f => f.custom.face.baseMakeup.lipColor, (f, v) => f.custom.face.baseMakeup.lipColor = (Color)v);
            for (int i = 0; i < f0.custom.face.baseMakeup.paintId.Length; i++)
            {
                int k = i;
                add("メイク・肌", "フェイスペイント" + (k + 1), Kind.ListItem, 405, true, f => f.custom.face.baseMakeup.paintId[k], (f, v) => f.custom.face.baseMakeup.paintId[k] = (int)v);
                add("メイク・肌", "フェイスペイント" + (k + 1) + "の色", Kind.Color, -1, false, f => f.custom.face.baseMakeup.paintColor[k], (f, v) => f.custom.face.baseMakeup.paintColor[k] = (Color)v);
                add("メイク・肌", "フェイスペイント" + (k + 1) + "の位置", Kind.Vec, -1, false, f => f.custom.face.baseMakeup.paintLayout[k], (f, v) => f.custom.face.baseMakeup.paintLayout[k] = (Vector4)v);
            }
            // the hair
            add("髪", "髪の種類", Kind.Number, -1, false, f => f.custom.hair.kind, (f, v) => f.custom.hair.kind = (int)v);
            add("髪", "髪のツヤ", Kind.ListItem, 433, true, f => f.custom.hair.glossId, (f, v) => f.custom.hair.glossId = (int)v);
            for (int i = 0; i < f0.custom.hair.parts.Length && i < HairNames.Length; i++)
            {
                int k = i;
                string n = HairNames[k];
                add("髪", n, Kind.ListItem, 101 + k, true, f => f.custom.hair.parts[k].id, (f, v) => f.custom.hair.parts[k].id = (int)v);
                add("髪", n + "の色", Kind.Color, -1, false, f => f.custom.hair.parts[k].baseColor, (f, v) => f.custom.hair.parts[k].baseColor = (Color)v);
                add("髪", n + "の根元の色", Kind.Color, -1, false, f => f.custom.hair.parts[k].startColor, (f, v) => f.custom.hair.parts[k].startColor = (Color)v);
                add("髪", n + "の毛先の色", Kind.Color, -1, false, f => f.custom.hair.parts[k].endColor, (f, v) => f.custom.hair.parts[k].endColor = (Color)v);
                add("髪", n + "の輪郭の色", Kind.Color, -1, false, f => f.custom.hair.parts[k].outlineColor, (f, v) => f.custom.hair.parts[k].outlineColor = (Color)v);
                add("髪", n + "の長さ", Kind.Slider, -1, false, f => f.custom.hair.parts[k].length, (f, v) => f.custom.hair.parts[k].length = (float)v);
            }
            // every outfit: its clothes and its accessories (written to the outfit, and to the shown one when it is that)
            for (int c = 0; c < f0.coordinate.Length; c++) Outfit(o, f0, c);
            return o;
        }

        static void Coord(List<Item> o, int c, string g, string n, Kind k, int cat, bool loads, Func<ChaFileCoordinate, object> get, Action<ChaFileCoordinate, object> set, Func<object, string> fmt = null)
        {
            o.Add(new Item
            {
                Group = g, Name = n, Kind = k, Cat = cat, Loads = loads, Fmt = fmt,
                Get = f => get(f.coordinate[c]),
                Set = (f, cha, v) =>
                {
                    set(f.coordinate[c], v);
                    if (cha != null && f.status.coordinateType == c && cha.nowCoordinate != null && !ReferenceEquals(cha.nowCoordinate, f.coordinate[c])) set(cha.nowCoordinate, v);
                }
            });
        }

        static void Outfit(List<Item> o, ChaFileControl f0, int c)
        {
            string cn = c < CoordNames.Length ? CoordNames[c] : "コーデ" + (c + 1);
            var cl = f0.coordinate[c].clothes;
            for (int i = 0; i < cl.parts.Length && i < SlotNames.Length; i++)
            {
                int s = i;
                string g = "服（" + cn + "）", n = SlotNames[s];
                int cat = 105 + Mathf.Min(s, 7);
                Coord(o, c, g, n, Kind.ListItem, cat, true, x => x.clothes.parts[s].id, (x, v) => x.clothes.parts[s].id = (int)v);
                for (int k = 0; k < cl.parts[s].colorInfo.Length; k++)
                {
                    int q = k;
                    Coord(o, c, g, n + "の色" + (q + 1), Kind.Color, -1, false, x => x.clothes.parts[s].colorInfo[q].baseColor, (x, v) => x.clothes.parts[s].colorInfo[q].baseColor = (Color)v);
                    Coord(o, c, g, n + "の柄" + (q + 1), Kind.ListItem, 430, true, x => x.clothes.parts[s].colorInfo[q].pattern, (x, v) => x.clothes.parts[s].colorInfo[q].pattern = (int)v);
                    Coord(o, c, g, n + "の柄" + (q + 1) + "の色", Kind.Color, -1, false, x => x.clothes.parts[s].colorInfo[q].patternColor, (x, v) => x.clothes.parts[s].colorInfo[q].patternColor = (Color)v);
                }
                Coord(o, c, g, n + "のエンブレム", Kind.ListItem, 431, true, x => x.clothes.parts[s].emblemeId, (x, v) => x.clothes.parts[s].emblemeId = (int)v);
            }
            string[] pn = { "上", "下", "飾り" };
            for (int i = 0; i < cl.subPartsId.Length && i < pn.Length; i++)
            {
                int k = i;
                Coord(o, c, "服（" + cn + "）", "セーラー服・ブレザーの" + pn[k], Kind.Number, -1, true, x => x.clothes.subPartsId[k], (x, v) => x.clothes.subPartsId[k] = (int)v);
            }
            var ac = f0.coordinate[c].accessory;
            for (int i = 0; i < ac.parts.Length; i++)
            {
                int s = i;
                string g = "アクセサリ（" + cn + "）", n = "スロット" + (s + 1);
                Coord(o, c, g, n, Kind.ListItem, -1, true, x => x.accessory.parts[s].type + ":" + x.accessory.parts[s].id,
                    (x, v) => { var p = ((string)v).Split(':'); x.accessory.parts[s].type = int.Parse(p[0]); x.accessory.parts[s].id = int.Parse(p[1]); }, AccText);
                Coord(o, c, g, n + "の付け場所", Kind.Text, -1, false, x => x.accessory.parts[s].parentKey ?? "", (x, v) => x.accessory.parts[s].parentKey = (string)v, ParentName);
                for (int k = 0; k < ac.parts[s].color.Length; k++)
                {
                    int q = k;
                    Coord(o, c, g, n + "の色" + (q + 1), Kind.Color, -1, false, x => x.accessory.parts[s].color[q], (x, v) => x.accessory.parts[s].color[q] = (Color)v);
                }
                string[] mv = { "位置", "向き", "大きさ" };
                for (int a = 0; a < 2; a++)
                    for (int k = 0; k < 3; k++)
                    {
                        int aa = a, kk = k;
                        Coord(o, c, g, n + (aa == 0 ? "" : "（調整 2）") + "の" + mv[kk], Kind.Vec, -1, false, x => x.accessory.parts[s].addMove[aa, kk], (x, v) => x.accessory.parts[s].addMove[aa, kk] = (Vector3)v);
                    }
                Coord(o, c, g, n + "の揺れ止め", Kind.Bool, -1, false, x => x.accessory.parts[s].noShake, (x, v) => x.accessory.parts[s].noShake = (bool)v);
            }
        }

        static Item Flags(string g, string n, string[] names, Func<ChaFileControl, object> owner)
        {
            return new Item
            {
                Group = g, Name = n, Kind = Kind.Text,
                Get = f =>
                {
                    var obj = owner(f);
                    var on = new List<string>();
                    foreach (var pair in names)
                    {
                        var key = pair.Split(':')[0];
                        var pi = obj.GetType().GetProperty(key);
                        if (pi != null && (bool)pi.GetValue(obj, null)) on.Add(key);
                    }
                    return string.Join(",", on.ToArray());
                },
                Set = (f, c, v) =>
                {
                    var obj = owner(f);
                    var want = new HashSet<string>(((string)v).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                    foreach (var pair in names)
                    {
                        var key = pair.Split(':')[0];
                        var pi = obj.GetType().GetProperty(key);
                        if (pi != null && pi.CanWrite) pi.SetValue(obj, want.Contains(key), null);
                    }
                },
                Fmt = v =>
                {
                    var on = new HashSet<string>(((string)v).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                    var words = new List<string>();
                    foreach (var pair in names) { var p = pair.Split(':'); if (on.Contains(p[0])) words.Add(p[1]); }
                    return words.Count == 0 ? "なし" : string.Join("・", words.ToArray());
                }
            };
        }

        static string AccText(object v)
        {
            var p = ((string)v).Split(':');
            int type = int.Parse(p[0]), id = int.Parse(p[1]);
            if (type <= 120) return "なし";
            return AccTypes[Mathf.Clamp(type - 120, 0, AccTypes.Length - 1)] + "：" + (ItemName(type, id) ?? id.ToString());
        }

        static string ParentName(object v)
        {
            var key = (string)v;
            if (string.IsNullOrEmpty(key)) return "";
            try
            {
                int i = (int)Enum.Parse(typeof(ChaAccessoryDefine.AccessoryParentKey), key);
                return i < ChaAccessoryDefine.AccessoryParentName.Length ? ChaAccessoryDefine.AccessoryParentName[i] : key;
            }
            catch (Exception) { return key; }
        }

        static string Personality(int no)
        {
            try
            {
                VoiceInfo.Param p;
                if (Singleton<Manager.Voice>.Instance.voiceInfoDic.TryGetValue(no, out p)) return p.Personality;
            }
            catch (Exception) { }
            return no.ToString();
        }

        // ---- the screen: a button (変更点 and the count) opens the list; the list's rows (a check, the item, start → now), undo and
        // redo; a clear uGUI panel under them keeps the maker's own panels and camera from taking the clicks ----

        float S { get { return Mathf.Max(0.6f, Screen.height / 1080f); } }
        public Rect ButtonRect { get { float s = S; return new Rect(Screen.width * 0.5f - 100f * s, 8f * s, 200f * s, 36f * s); } }
        // (right of the character, clear of the maker's own panel on the right edge; its bottom above the camera buttons other
        // plugins put at the lower right)
        public Rect WindowRect { get { float s = S, w = 600f * s, h = Mathf.Min(720f * s, Screen.height * 0.62f - 56f * s); return new Rect(Screen.width - w - 250f * s, 56f * s, w, h); } }

        Font UiFont
        {
            get
            {
                if (fontTried) return font;
                fontTried = true;
                try
                {
                    var have = new HashSet<string>(Font.GetOSInstalledFontNames());
                    var use = new List<string>();
                    foreach (var n in new[] { "Yu Gothic UI Semibold", "Yu Gothic UI", "Meiryo UI", "Meiryo" }) if (have.Contains(n)) use.Add(n);
                    if (use.Count > 0) { font = Font.CreateDynamicFontFromOSFont(use.ToArray(), 16); font.hideFlags = HideFlags.HideAndDontSave; }
                }
                catch (Exception) { }
                return font;
            }
        }

        public void OnGUI()
        {
            if (cha == null || start == null || !Ready) return;
            float s = S;
            var oldFont = GUI.skin.font;
            if (UiFont != null) GUI.skin.font = UiFont;
            int fs = Mathf.RoundToInt(17f * s);
            var btn = new GUIStyle(GUI.skin.button) { fontSize = fs };
            var lbl = new GUIStyle(GUI.skin.label) { fontSize = fs, wordWrap = false, clipping = TextClipping.Clip };
            var small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(14f * s), wordWrap = true };
            var tog = new GUIStyle(GUI.skin.toggle) { fontSize = fs };
            if (GUI.Button(ButtonRect, (Open ? "▲ " : "▼ ") + "変更点（" + Count + "）", btn)) Open = !Open;
            if (Open)
            {
                var r = WindowRect;
                GUI.Box(r, "");
                GUI.Box(r, "");
                float x = r.x + 12f * s, y = r.y + 10f * s, w = r.width - 24f * s, bh = 34f * s;
                GUI.Label(new Rect(x, y, w, bh), "変更点：開いたとき（読み込んだカード）からの変更", lbl);
                y += bh;
                GUI.enabled = CanUndo;
                if (GUI.Button(new Rect(x, y, 170f * s, bh), "元に戻す（Ctrl+Z）", btn)) Undo();
                GUI.enabled = CanRedo;
                if (GUI.Button(new Rect(x + 180f * s, y, 170f * s, bh), "やり直す（Ctrl+Y）", btn)) Redo();
                GUI.enabled = true;
                y += bh + 8f * s;
                float listH = r.yMax - y - 64f * s, rowH = 30f * s;
                var rows = new List<int>(hist[pos].E.Keys);
                rows.Sort();
                var view = new Rect(0, 0, w - 20f * s, Mathf.Max(listH, rows.Count * rowH));
                scroll = GUI.BeginScrollView(new Rect(x, y, w, listH), scroll, view);
                float ry = 0f;
                if (rows.Count == 0) GUI.Label(new Rect(0, 0, view.width, rowH), "（変更はまだありません）", lbl);
                foreach (int i in rows)
                {
                    var e = hist[pos].E[i];
                    var keep = GUI.color;
                    if (!e.Check) GUI.color = new Color(1f, 1f, 1f, 0.45f);
                    bool now = GUI.Toggle(new Rect(0, ry, 28f * s, rowH), e.Check, "", tog);
                    if (now != e.Check) { GUI.color = keep; Toggle(i); break; }
                    float tx = 30f * s, half = (view.width - tx) * 0.5f;
                    GUI.Label(new Rect(tx, ry, half, rowH), items[i].Label, lbl);
                    ValueCell(new Rect(tx + half, ry, half, rowH), i, start[i], e.User, lbl, s);
                    GUI.color = keep;
                    ry += rowH;
                }
                GUI.EndScrollView();
                GUI.Label(new Rect(x, r.yMax - 60f * s, w, 56f * s), "チェックを外すと、その変更は開いたときの値に戻ります。保存されるのは、チェックの付いた変更だけです。", small);
            }
            GUI.skin.font = oldFont;
        }

        void ValueCell(Rect r, int i, object from, object to, GUIStyle lbl, float s)
        {
            if (items[i].Kind == Kind.Color && from is Color && to is Color)
            {
                float sw = r.height - 8f * s;
                Swatch(new Rect(r.x, r.y + 4f * s, sw, sw), (Color)from);
                GUI.Label(new Rect(r.x + sw + 6f * s, r.y, 40f * s, r.height), "→", lbl);
                Swatch(new Rect(r.x + sw + 40f * s, r.y + 4f * s, sw, sw), (Color)to);
                GUI.Label(new Rect(r.x + 2f * sw + 50f * s, r.y, r.width - 2f * sw - 50f * s, r.height), Text(i, to), lbl);
                return;
            }
            GUI.Label(r, Text(i, from) + " → " + Text(i, to), lbl);
        }

        static void Swatch(Rect r, Color c)
        {
            var keep = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, keep.a);
            GUI.DrawTexture(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f), Texture2D.whiteTexture);
            GUI.color = new Color(c.r, c.g, c.b, keep.a);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = keep;
        }

        // the clear uGUI panel under the button and the list (the maker's panels and its camera see the pointer on uGUI there)
        void Block(bool on)
        {
            if (!on) { if (blocker != null && blocker.activeSelf) blocker.SetActive(false); return; }
            if (blocker == null)
            {
                blocker = new GameObject("MuseMakerBlocker");
                UnityEngine.Object.DontDestroyOnLoad(blocker);
                var cv = blocker.AddComponent<Canvas>();
                cv.renderMode = RenderMode.ScreenSpaceOverlay;
                cv.sortingOrder = 32000;
                blocker.AddComponent<GraphicRaycaster>();
                blockBtn = Panel("button");
                blockWin = Panel("list");
                blockAll = Panel("sync");
            }
            if (!blocker.activeSelf) blocker.SetActive(true);
            Place(blockBtn, ButtonRect, true);
            Place(blockWin, WindowRect, Open);
            // (while the maker's panels write their values back after an apply - an undo, a redo, a check, a reset - the user's
            // clicks and drags do not reach the maker: what changes then is the sync's, never an edit of the user's lost unseen)
            Place(blockAll, new Rect(0f, 0f, Screen.width, Screen.height), Adopting);
        }

        RectTransform Panel(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(blocker.transform, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);
            img.raycastTarget = true;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
            return rt;
        }

        static void Place(RectTransform rt, Rect r, bool on)
        {
            if (rt.gameObject.activeSelf != on) rt.gameObject.SetActive(on);
            rt.anchoredPosition = new Vector2(r.x, Screen.height - r.yMax);
            rt.sizeDelta = new Vector2(r.width, r.height);
        }

        public void LateUpdate() { Block(cha != null && start != null && Ready); }
        public bool BlockerOnForCheck { get { return blocker != null && blocker.activeSelf && blockWin != null && blockWin.gameObject.activeSelf; } }
        public bool SyncBlockedForCheck { get { return blockAll != null && blockAll.gameObject.activeSelf; } }
    }
}
