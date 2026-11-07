using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace MuseStudio
{
    // The room's position references edited in the game (the user's requests, PR #11 and PR #23): while 「位置の調整」 is on, the
    // room's references of one kind show as markers - a disc with an arrow for its facing and a pole, in the kind's colour - to
    // drag along the floor (the camera holds still meanwhile), with finer moves, turns and height on its panel. The kinds:
    //   play      the plays' spots (the original's H points: floor, wall, chair ...)
    //   action    where her outings in this room put her (the toilet, the shower, alone, changing: Outing.Place)
    //   stand     where she stands ("here")
    //   approach  where 「近付く」 walks her to (none set: towards the camera, as ever)
    // A new kind of reference is one more row in Kinds and one more case in Refs. Each change is saved at once, as the camera is
    // (UserData/MuseStudio/spots.json, the checks: their own): per room and reference, the offset from the original's place
    // (the approach: its place itself); the references are read with it (Stage.LoadSpots / SpotsIn / StandOf, Outing.Place,
    // Stage.StartWalk), and a play on the spot moves with it. Also the user's way out of a motion that misses its furniture
    // (the toilet paper the motion reaches for) and of a walk through it (the approach's place).
    //
    // Three layers (PR #46 §4.2, handed over in PR #47): the initial place (the original's - generated, no record), the
    // user's default (「今の位置をデフォルトにする」: spots.json's defaults) and the current one (spots.json's rooms: what is
    // used). 「デフォルトに戻す」 puts the default back (none saved: the initial place); 「初期化」 the initial place, the
    // default kept. Edited is a record in either layer (not a place off the original's: a spot moved back by hand is still
    // edited until it is put back). The play spots shown by their kind (plays.json's places: 床・ベッド, 椅子, 壁 ...): only
    // that kind's markers; a spot of several kinds is one spot - the same marker under each, one place.
    public class SpotEditor
    {
        readonly string path;
        readonly Dictionary<string, object> rooms;          // the current adjustments: room -> id -> {move, turn} (the approach: {pos, turn})
        readonly Dictionary<string, object> defaults;       // the user's defaults, the same shape
        public bool On;
        public string Kind = "play";
        public string Category = "";                        // the play spots' kind shown ("" every one, "other" none of the known)
        public bool EditedOnly;                             // only the references with a record (current or default)
        public string Selected = "";
        public bool Dragging;
        readonly List<GameObject> markers = new List<GameObject>();
        readonly List<Spot> refs = new List<Spot>();        // the room's references of the kind shown (the play spots: the stage's own)
        List<Spot> builtFor;                                // (the room's spots the markers stand for: read again -> built again)
        string builtKind = "";
        readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();
        Vector3 dragFrom, spotFrom;
        float dragPlane, dragReach;
        bool held;                                          // (a press on a marker, to its release: the editor's, not a touch's or a click's)
        const float PickPixels = 34f;
        public readonly Dictionary<string, Rect> Buttons = new Dictionary<string, Rect>();     // (its panel's: the checks' real clicks)

        public static readonly string[] Kinds = { "play", "action", "stand", "approach" };
        public static readonly Dictionary<string, string> KindNames = new Dictionary<string, string> {
            { "play", "プレイ位置" }, { "action", "行動の位置" }, { "stand", "立ち位置" }, { "approach", "近付く" } };
        public static readonly Dictionary<string, Color> KindColors = new Dictionary<string, Color> {
            { "play", new Color(0.95f, 0.45f, 0.65f, 0.6f) }, { "action", new Color(1f, 0.55f, 0.15f, 0.6f) },
            { "stand", new Color(0.3f, 0.8f, 1f, 0.6f) }, { "approach", new Color(0.4f, 0.9f, 0.35f, 0.6f) } };
        public const string ApproachId = "approach";

        public SpotEditor(string path)
        {
            this.path = path;
            Dictionary<string, object> doc = null;
            try { if (File.Exists(path)) doc = Json.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>; }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: spots " + path + ": " + e.Message); }
            rooms = Json.Obj(doc, "rooms") ?? new Dictionary<string, object>();
            defaults = Json.Obj(doc, "defaults") ?? new Dictionary<string, object>();
        }

        static MuseStudioPlugin H { get { return MuseStudioPlugin.Instance; } }

        // ---- the offsets ----

        // a reference as the room's offset for it moves it (its place by the room's axes, its facing turned about up)
        public void Apply(string room, Spot sp)
        {
            var o = Json.Obj(Json.Obj(rooms, room), sp.Id);
            if (o == null) return;
            var d = Json.Arr(o, "move");
            if (d != null && d.Count >= 3) sp.Pos += new Vector3(Convert.ToSingle(d[0]), Convert.ToSingle(d[1]), Convert.ToSingle(d[2]));
            sp.Rot = Quaternion.Euler(0f, (float)Json.Num(o, "turn", 0.0), 0f) * sp.Rot;
        }

        public bool HasOffset(string room, string id) { return Json.Obj(Json.Obj(rooms, room), id) != null; }

        public bool HasDefault(string room, string id) { return Json.Obj(Json.Obj(defaults, room), id) != null; }

        // edited: a record in either layer (PR #46 §4.2 - the record, not whether it sits off the original's place)
        public bool Edited(string room, string id) { return HasOffset(room, id) || HasDefault(room, id); }

        static Dictionary<string, object> Copy(Dictionary<string, object> o)
        {
            var c = new Dictionary<string, object>();
            foreach (var kv in o) c[kv.Key] = kv.Value is List<object> ? new List<object>((List<object>)kv.Value) : kv.Value;
            return c;
        }

        static Dictionary<string, object> Room(Dictionary<string, object> layer, string room)
        {
            var r = Json.Obj(layer, room);
            if (r == null) { r = new Dictionary<string, object>(); layer[room] = r; }
            return r;
        }

        // a record's offset taken off a reference (sign -1) or put on it (+1)
        static void Shift(Spot sp, Dictionary<string, object> o, float sign)
        {
            if (o == null) return;
            var d = Json.Arr(o, "move");
            if (d != null && d.Count >= 3) sp.Pos += sign * new Vector3(Convert.ToSingle(d[0]), Convert.ToSingle(d[1]), Convert.ToSingle(d[2]));
            sp.Rot = Quaternion.Euler(0f, sign * (float)Json.Num(o, "turn", 0.0), 0f) * sp.Rot;
        }

        // 「今の位置をデフォルトにする」: the current place kept as the user's default (at the initial place - no record: the
        // default goes, the initial place is the default again)
        public void SaveDefault(Spot sp, string room = null)
        {
            if (sp == null) return;
            room = room ?? H.Stage.Location;
            var cur = Json.Obj(Json.Obj(rooms, room), sp.Id);
            var d = Room(defaults, room);
            if (cur == null)
            {
                d.Remove(sp.Id);
                foreach (var a in sp.Aliases) d.Remove(a);
            }
            else
            {
                var c = Copy(cur);
                d[sp.Id] = c;
                foreach (var a in sp.Aliases) d[a] = c;
            }
            if (d.Count == 0) defaults.Remove(room);
            Save();
        }

        // 「デフォルトに戻す」: the user's default as the current place again (none saved: the initial place - Reset)
        public void BackToDefault(Spot sp, string room = null)
        {
            if (sp == null) return;
            room = room ?? H.Stage.Location;
            var def = Json.Obj(Json.Obj(defaults, room), sp.Id);
            if (def == null) { Reset(sp, room); return; }
            var r = Room(rooms, room);
            var c = Copy(def);
            if (sp.Id == ApproachId)
            {
                r[ApproachId] = c;
                Save();
                Vector3 pos;
                float yaw;
                ApproachIn(room, out pos, out yaw);
                sp.Pos = pos;
                sp.Rot = Quaternion.Euler(0f, yaw, 0f);
                sp.Name = KindNames["approach"] + "の行き先";
                return;
            }
            Shift(sp, Json.Obj(r, sp.Id), -1f);
            Shift(sp, c, 1f);
            r[sp.Id] = c;
            foreach (var a in sp.Aliases) r[a] = c;
            Save();
            if (room == H.Stage.Location) Follow(sp);
        }

        // the room's approach place, set by the user (false: none - she walks towards the camera)
        public bool ApproachIn(string room, out Vector3 pos, out float yaw)
        {
            pos = Vector3.zero;
            yaw = 0f;
            var o = Json.Obj(Json.Obj(rooms, room), ApproachId);
            var p = Json.Arr(o, "pos");
            if (p == null || p.Count < 3) return false;
            pos = H.Rooms.OffsetOf(room) + new Vector3(Convert.ToSingle(p[0]), Convert.ToSingle(p[1]), Convert.ToSingle(p[2]));
            yaw = (float)Json.Num(o, "turn", 0.0);
            return true;
        }

        // a change to a reference of this room (or another's: an outing's place there): moved by `move` (the room's axes), turned
        // by `turn` degrees; saved; what stands on it follows
        public void Change(Spot sp, Vector3 move, float turn, string room = null)
        {
            room = room ?? H.Stage.Location;
            var r = Json.Obj(rooms, room);
            if (r == null) { r = new Dictionary<string, object>(); rooms[room] = r; }
            sp.Pos += move;
            sp.Rot = Quaternion.Euler(0f, turn, 0f) * sp.Rot;
            if (sp.Id == ApproachId)
            {
                // (the approach: its place itself, the room's own coordinates - from where its marker stood, set or not)
                Vector3 rel = sp.Pos - H.Rooms.OffsetOf(room);
                r[ApproachId] = new Dictionary<string, object> { { "pos", new List<object> { Math.Round(rel.x, 4), Math.Round(rel.y, 4), Math.Round(rel.z, 4) } },
                    { "turn", Math.Round(sp.Rot.eulerAngles.y, 2) } };
                sp.Name = KindNames["approach"] + "の行き先";
                Save();
                return;
            }
            var o = Json.Obj(r, sp.Id) ?? new Dictionary<string, object>();
            var d = Json.Arr(o, "move");
            Vector3 m = d != null && d.Count >= 3 ? new Vector3(Convert.ToSingle(d[0]), Convert.ToSingle(d[1]), Convert.ToSingle(d[2])) : Vector3.zero;
            m += move;
            o["move"] = new List<object> { Math.Round(m.x, 4), Math.Round(m.y, 4), Math.Round(m.z, 4) };
            o["turn"] = Math.Round(((float)Json.Num(o, "turn", 0.0) + turn) % 360f, 2);
            r[sp.Id] = o;
            foreach (var a in sp.Aliases) r[a] = o;          // (the points merged into it: an outing reads them one by one)
            Save();
            if (room == H.Stage.Location) Follow(sp);
        }

        // 「初期化」: a reference back to the original's place, its initial one (the approach: towards the camera again); the
        // user's default stays (「デフォルトに戻す」 brings it back)
        public void Reset(Spot sp, string room = null)
        {
            room = room ?? H.Stage.Location;
            var r = Json.Obj(rooms, room);
            var o = Json.Obj(r, sp.Id);
            if (o == null) return;
            if (sp.Id == ApproachId)
            {
                r.Remove(ApproachId);
                Save();
                var d0 = ApproachSpot(room);
                sp.Pos = d0.Pos;
                sp.Rot = d0.Rot;
                sp.Name = d0.Name;
                return;
            }
            var d = Json.Arr(o, "move");
            if (d != null && d.Count >= 3) sp.Pos -= new Vector3(Convert.ToSingle(d[0]), Convert.ToSingle(d[1]), Convert.ToSingle(d[2]));
            sp.Rot = Quaternion.Euler(0f, -(float)Json.Num(o, "turn", 0.0), 0f) * sp.Rot;
            r.Remove(sp.Id);
            foreach (var a in sp.Aliases) r.Remove(a);
            Save();
            if (room == H.Stage.Location) Follow(sp);
        }

        // every room's adjustments back to the original's (the settings' button - the user's request, PR #23): the room's
        // references read again, she stands where the original puts her. The user's defaults stay (each 「デフォルトに戻す」
        // brings its own back: PR #46 §4.2's layers - nothing the user saved goes by a reset)
        public void ResetAll()
        {
            rooms.Clear();
            Save();
            Selected = "";
            Dragging = false;
            H.Stage.ReloadSpots();
            if (On) { Clear(); Build(); }
        }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Json.Write(new Dictionary<string, object> { { "rooms", rooms }, { "defaults", defaults } }), new UTF8Encoding(false));
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: spots " + path + ": " + e.Message); }
        }

        // a play on the spot moves with it; "here" (where she stands) takes her along outside a play (an outing's place: the next
        // outing there)
        static void Follow(Spot sp)
        {
            var pc = H.Play;
            if (pc.Active && pc.CurrentSpot == sp) pc.PlaceAgain();
            else if (!pc.Active && sp.Here && H.Stage.Heroine != null && !H.Outing.Out && H.Presence.HerAt.Length == 0) H.Stage.Place(H.Stage.Heroine);
        }

        // ---- the references of a kind in this room ----

        public List<Spot> Refs(string kind)
        {
            var o = new List<Spot>();
            var st = H.Stage;
            switch (kind)
            {
                case "play": foreach (var sp in st.Spots) if (!sp.Here) o.Add(sp); break;
                case "stand": foreach (var sp in st.Spots) if (sp.Here) o.Add(sp); break;
                case "action": foreach (var k in H.Outing.KindsIn(st.Location)) o.Add(H.Outing.Place(k)); break;
                case "approach": o.Add(ApproachSpot(st.Location)); break;
            }
            return o;
        }

        // the approach's reference: its place as set, else where she would walk to now (towards the camera)
        public Spot ApproachSpot(string room)
        {
            var sp = new Spot { Id = ApproachId };
            Vector3 pos;
            float yaw;
            if (ApproachIn(room, out pos, out yaw)) sp.Name = KindNames["approach"] + "の行き先";
            else
            {
                H.Stage.ApproachDefault(out pos, out yaw);
                sp.Name = KindNames["approach"] + "の行き先（未設定：カメラへ向かう）";
            }
            sp.Pos = pos;
            sp.Rot = Quaternion.Euler(0f, yaw, 0f);
            return sp;
        }

        // ---- on / off, the kind shown, the markers ----

        public void Toggle(bool on)
        {
            On = on;
            Selected = "";
            Dragging = false;
            Clear();
            if (on) Build();
        }

        public void ShowKind(string kind)
        {
            if (Array.IndexOf(Kinds, kind) < 0) return;
            Kind = kind;
            Selected = "";
            Dragging = false;
            if (On) { Clear(); Build(); }
        }

        // the play spots of one kind shown ("" every one): the user's choice, kept from room to room while the room has it
        public void ShowCategory(string cat)
        {
            Category = cat ?? "";
            categoryChosen = true;
            Selected = "";
            Dragging = false;
            if (On) { Clear(); Build(); }
        }

        public void ShowEditedOnly(bool only)
        {
            EditedOnly = only;
            Selected = "";
            Dragging = false;
            if (On) { Clear(); Build(); }
        }

        // ---- the play spots' kinds (plays.json's places: the original's H point categories by name) ----

        public const string OtherCategory = "other";
        bool categoryChosen;

        internal void CategoryAsNewForCheck() { categoryChosen = false; Category = ""; }
        Dictionary<string, object> places;                  // (read at a build: plays.json's places)
        readonly List<KeyValuePair<string, string>> cats = new List<KeyValuePair<string, string>>();     // the room's: key -> name

        static bool OfPlace(Spot sp, Dictionary<string, object> place)
        {
            foreach (var t in Json.Arr(place, "toggles") ?? new List<object>()) if (sp.Categories.Contains(Convert.ToInt32(t))) return true;
            return false;
        }

        public bool InCategory(Spot sp, string cat)
        {
            if (string.IsNullOrEmpty(cat)) return true;
            if (places == null) places = Json.Obj(Data.Load("plays.json"), "places");
            if (cat == OtherCategory)
            {
                if (places != null) foreach (var kv in places) if (OfPlace(sp, kv.Value as Dictionary<string, object>)) return false;
                return true;
            }
            return OfPlace(sp, Json.Obj(places, cat));
        }

        // (checks, the panel) the kinds this room's play spots have, in plays.json's order - then その他 for spots of none
        public List<KeyValuePair<string, string>> CategoriesHere { get { return new List<KeyValuePair<string, string>>(cats); } }

        void ReadCategories(List<Spot> spots)
        {
            places = Json.Obj(Data.Load("plays.json"), "places");
            cats.Clear();
            if (places != null)
                foreach (var kv in places)
                {
                    var pl = kv.Value as Dictionary<string, object>;
                    if (spots.Exists(sp => OfPlace(sp, pl))) cats.Add(new KeyValuePair<string, string>(kv.Key, Json.Str(pl, "name", kv.Key)));
                }
            if (spots.Exists(sp => InCategory(sp, OtherCategory))) cats.Add(new KeyValuePair<string, string>(OtherCategory, "その他"));
            // (the first build: the room's first kind - not all at once, PR #46 §4.2; a kind this room lacks: its first)
            if ((!categoryChosen || Category.Length > 0) && !cats.Exists(c => c.Key == Category)) Category = cats.Count > 0 ? cats[0].Key : "";
        }

        void Clear()
        {
            foreach (var g in markers) if (g != null) UnityEngine.Object.Destroy(g);
            markers.Clear();
            refs.Clear();
        }

        Material Mat(string key, Color c)
        {
            Material m;
            if (mats.TryGetValue(key, out m) && m != null) return m;
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return null;
            m = new Material(sh);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_Cull", 0);
            m.SetInt("_ZWrite", 0);
            m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            m.renderQueue = 3100;
            m.color = c;
            mats[key] = m;
            return m;
        }

        void Build()
        {
            string room = H.Stage.Location;
            var all = Refs(Kind);
            if (Kind == "play") ReadCategories(all);
            foreach (var sp in all)
                if ((Kind != "play" || InCategory(sp, Category)) && (!EditedOnly || Edited(room, sp.Id))) refs.Add(sp);
            // (a layer Studio's camera draws - the body's, as the hit shapes: the default layer is not drawn)
            var cha = H.Stage.Cha;
            int layer = cha != null && cha.objBody != null ? cha.objBody.layer : Mathf.Max(0, LayerMask.NameToLayer("Chara"));
            foreach (var sp in refs)
            {
                var root = new GameObject("MuseStudio spot " + Kind + " " + sp.Id);
                var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                UnityEngine.Object.Destroy(disc.GetComponent<Collider>());
                disc.transform.SetParent(root.transform, false);
                disc.transform.localScale = new Vector3(0.32f, 0.004f, 0.32f);
                var arrow = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.Destroy(arrow.GetComponent<Collider>());
                arrow.transform.SetParent(root.transform, false);
                arrow.transform.localPosition = new Vector3(0f, 0.004f, 0.22f);
                arrow.transform.localScale = new Vector3(0.05f, 0.006f, 0.16f);
                // (a thin pole: seen from a standing camera too, the discs lying flat on the floor)
                var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                UnityEngine.Object.Destroy(pole.GetComponent<Collider>());
                pole.transform.SetParent(root.transform, false);
                pole.transform.localPosition = new Vector3(0f, 0.3f, 0f);
                pole.transform.localScale = new Vector3(0.015f, 0.3f, 0.015f);
                foreach (var t in root.GetComponentsInChildren<Transform>()) t.gameObject.layer = layer;
                root.AddComponent<Marker>().Spot = sp;
                markers.Add(root);
            }
            builtFor = H.Stage.Spots;
            builtKind = Kind;
            Place();
        }

        class Marker : MonoBehaviour { public Spot Spot; }

        void Place()
        {
            var normal = Mat(Kind, KindColors[Kind]);
            var chosen = Mat("chosen", new Color(1f, 0.9f, 0.3f, 0.85f));
            foreach (var g in markers)
            {
                if (g == null) continue;
                var sp = g.GetComponent<Marker>().Spot;
                g.transform.position = sp.Pos + Vector3.up * 0.01f;
                g.transform.rotation = Quaternion.Euler(0f, sp.Rot.eulerAngles.y, 0f);
                foreach (var r in g.GetComponentsInChildren<Renderer>()) r.sharedMaterial = sp.Id == Selected ? chosen : normal;
            }
        }

        public Spot SelectedSpot { get { return refs.Find(s => s.Id == Selected); } }

        // (checks) the references the markers show now
        public List<Spot> Shown { get { return new List<Spot>(refs); } }

        // the mouse's: over a marker (within PickPixels on the screen), or a press on one until its release - the camera, the
        // touch and the click that advances the words leave it alone (GPT's review of PR #22)
        public bool HoldsMouse { get { return On && (held || Dragging || Hover(Input.mousePosition) != null); } }

        Spot Hover(Vector2 mouse)
        {
            var cam = Camera.main;
            if (cam == null) return null;
            Spot best = null;
            float bd = PickPixels;
            foreach (var sp in refs)
            {
                var p = cam.WorldToScreenPoint(sp.Pos);
                if (p.z <= 0f) continue;
                float d = Vector2.Distance(new Vector2(p.x, p.y), mouse);
                if (d < bd) { bd = d; best = sp; }
            }
            return best;
        }

        public void Tick()
        {
            if (!On) return;
            if (!ReferenceEquals(H.Stage.Spots, builtFor) || builtKind != Kind || (markers.Count > 0 && markers[0] == null))
            {
                Selected = "";
                Dragging = false;
                Clear();
                Build();
            }
            var cam = Camera.main;
            Vector2 mouse = Input.mousePosition;
            if (held && !Input.GetMouseButton(0) && !Input.GetMouseButtonUp(0)) held = false;      // (the frame after the release)
            if (Input.GetMouseButtonDown(0) && !H.UiOver(mouse) && cam != null)
            {
                var sp = Hover(mouse);
                if (sp != null)
                {
                    held = true;
                    Selected = sp.Id;
                    dragPlane = sp.Pos.y;
                    spotFrom = sp.Pos;
                    dragReach = Mathf.Max(MinReach, 2f * Vector3.Distance(cam.transform.position, sp.Pos));
                    Dragging = FloorAt(cam.ScreenPointToRay(mouse), dragPlane, dragReach, out dragFrom);
                }
            }
            if (Dragging)
            {
                var sp = SelectedSpot;
                if (sp == null || !Input.GetMouseButton(0))
                {
                    Dragging = false;
                    if (sp != null && (sp.Pos - spotFrom).sqrMagnitude > 1e-8f)
                    {
                        // (the drag's way, kept while it went: now its offset saved and the play on it set again)
                        Vector3 moved = sp.Pos - spotFrom;
                        sp.Pos = spotFrom;
                        Change(sp, moved, 0f);
                    }
                }
                else
                {
                    // (the mouse's ray off the floor - along it, away from it, too far: the last good place stays)
                    Vector3 now;
                    if (cam != null && FloorAt(cam.ScreenPointToRay(mouse), dragPlane, dragReach, out now))
                        sp.Pos = spotFrom + new Vector3(now.x - dragFrom.x, 0f, now.z - dragFrom.z);
                }
            }
            Place();
        }

        // where a ray meets the floor's plane (height y): not along it, not behind its start, not beyond reach (GPT's review of
        // PR #22: a ray near the horizon met the floor some 1,500 m away)
        const float MinReach = 12f;

        internal static bool FloorAt(Ray ray, float y, float reach, out Vector3 at)
        {
            at = ray.origin;
            if (Mathf.Abs(ray.direction.y) < 1e-3f) return false;
            float t = (y - ray.origin.y) / ray.direction.y;
            if (t <= 0f || t > reach) return false;
            at = ray.origin + ray.direction * t;
            return true;
        }

        // ---- its panel (Ui: while on) ----

        GUIStyle catStyle;

        // a reference's layers by its name: adjusted (a current record), its default saved, at its default
        string Marks(string room, Spot sp)
        {
            var cur = Json.Obj(Json.Obj(rooms, room), sp.Id);
            var def = Json.Obj(Json.Obj(defaults, room), sp.Id);
            if (cur != null && def != null) return Json.Write(cur) == Json.Write(def) ? "（デフォルトの位置）" : "（調整済み・デフォルトあり）";
            return cur != null ? "（調整済み）" : def != null ? "（初期値・デフォルトあり）" : "";
        }

        public void DrawGui(float s, GUIStyle box, GUIStyle button, GUIStyle small, List<Rect> drawing)
        {
            if (!On) return;
            var cam = Camera.main;
            string room = H.Stage.Location;
            // each reference's name by its marker
            if (cam != null)
                foreach (var sp in refs)
                {
                    var p = cam.WorldToScreenPoint(sp.Pos);
                    if (p.z <= 0f) continue;
                    string nm = (sp.Here ? "その場" : sp.Name) + Marks(room, sp);
                    GUI.Label(new Rect(p.x + 12f * s, Screen.height - p.y - 10f * s, 300f * s, 22f * s), nm, small);
                }
            if (catStyle == null || catStyle.fontSize != small.fontSize) catStyle = new GUIStyle(button) { fontSize = small.fontSize, clipping = TextClipping.Clip, wordWrap = false };
            var shownCats = new List<KeyValuePair<string, string>>(cats);
            shownCats.Add(new KeyValuePair<string, string>("", "全部"));
            int catRows = Kind == "play" ? (shownCats.Count + 2) / 3 : 0;
            float w = 360f * s, lh = 26f * s, g = 4f * s, x = (Screen.width - w) * 0.5f;
            float height = (9 + catRows) * (lh + g) - g, y = Screen.height - height - 24f * s;
            var area = new Rect(x - 8f * s, y - 6f * s, w + 16f * s, height + 12f * s);
            drawing.Add(area);
            GUI.Box(area, GUIContent.none, box);
            // the kinds: one shown at a time, in its colour (the user's request, PR #23)
            float wk = (w - 3f * g) / 4f;
            for (int i = 0; i < Kinds.Length; i++)
            {
                string k = Kinds[i];
                var prev = GUI.backgroundColor;
                if (k == Kind) GUI.backgroundColor = new Color(KindColors[k].r, KindColors[k].g, KindColors[k].b, 1f);
                if (Btn(new Rect(x + i * (wk + g), y, wk, lh), KindNames[k], "kind " + k, button)) ShowKind(k);
                GUI.backgroundColor = prev;
            }
            y += lh + g;
            // the play spots' kinds: only the chosen one's markers (PR #46 §4.2), 全部 last
            if (Kind == "play")
            {
                float wc = (w - 2f * g) / 3f;
                for (int i = 0; i < shownCats.Count; i++)
                {
                    var c = shownCats[i];
                    var prev = GUI.backgroundColor;
                    if (c.Key == Category) GUI.backgroundColor = new Color(KindColors["play"].r, KindColors["play"].g, KindColors["play"].b, 1f);
                    if (Btn(new Rect(x + (i % 3) * (wc + g), y + (i / 3) * (lh + g), wc, lh), c.Value, "cat " + (c.Key.Length > 0 ? c.Key : "all"), catStyle)) ShowCategory(c.Key);
                    GUI.backgroundColor = prev;
                }
                y += catRows * (lh + g);
            }
            // only the edited ones (a record in either layer)
            float w2 = (w - g) / 2f;
            var prevE = GUI.backgroundColor;
            if (EditedOnly) GUI.backgroundColor = new Color(1f, 0.9f, 0.3f, 1f);
            if (Btn(new Rect(x, y, w2, lh), EditedOnly ? "編集したものだけ：入" : "編集したものだけ：切", "edited", button)) ShowEditedOnly(!EditedOnly);
            GUI.backgroundColor = prevE;
            GUI.Label(new Rect(x + w2 + g, y, w2, lh), "印 " + refs.Count + " か所", small);
            y += lh + g;
            var sel = SelectedSpot;
            string hint = refs.Count > 0 ? "印をドラッグ（床の上を動かす）"
                          : EditedOnly ? "編集したものはありません" : "この部屋には「" + KindNames[Kind] + "」がありません";
            GUI.Label(new Rect(x, y, w, lh), sel == null ? KindNames[Kind] + "：" + hint : "選択：" + (sel.Here ? "その場" : sel.Name) + Marks(room, sel), small);
            y += lh + g;
            GUI.enabled = sel != null;
            float w4 = (w - 3f * g) / 4f;
            // fine moves along the view's own left / right and forward / back (on the floor), 2 cm
            Vector3 right = cam != null ? Vector3.ProjectOnPlane(cam.transform.right, Vector3.up).normalized : Vector3.right;
            Vector3 fwd = Vector3.Cross(right, Vector3.up);      // (away from the camera: right x up, Unity's left-handed axes)
            if (Btn(new Rect(x, y, w4, lh), "← 2cm", "左", button)) Change(sel, -right * 0.02f, 0f);
            if (Btn(new Rect(x + w4 + g, y, w4, lh), "→ 2cm", "右", button)) Change(sel, right * 0.02f, 0f);
            if (Btn(new Rect(x + 2f * (w4 + g), y, w4, lh), "奥 2cm", "奥", button)) Change(sel, fwd * 0.02f, 0f);
            if (Btn(new Rect(x + 3f * (w4 + g), y, w4, lh), "手前 2cm", "手前", button)) Change(sel, -fwd * 0.02f, 0f);
            y += lh + g;
            if (Btn(new Rect(x, y, w4, lh), "左回り 15°", "左15", button)) Change(sel, Vector3.zero, -15f);
            if (Btn(new Rect(x + w4 + g, y, w4, lh), "左回り 3°", "左3", button)) Change(sel, Vector3.zero, -3f);
            if (Btn(new Rect(x + 2f * (w4 + g), y, w4, lh), "右回り 3°", "右3", button)) Change(sel, Vector3.zero, 3f);
            if (Btn(new Rect(x + 3f * (w4 + g), y, w4, lh), "右回り 15°", "右15", button)) Change(sel, Vector3.zero, 15f);
            y += lh + g;
            // (the approach's place: on her floor - a walk keeps her height; GPT's re-review of #30)
            GUI.enabled = sel != null && sel.Id != ApproachId;
            if (Btn(new Rect(x, y, w2, lh), sel != null && sel.Id == ApproachId ? "高さ：床のまま" : "上へ 1cm", "上", button)) Change(sel, Vector3.up * 0.01f, 0f);
            if (Btn(new Rect(x + w2 + g, y, w2, lh), "下へ 1cm", "下", button)) Change(sel, Vector3.down * 0.01f, 0f);
            y += lh + g;
            // the layers (PR #46 §4.2): the current place kept as the default; the default back; the initial place back
            GUI.enabled = sel != null;
            if (Btn(new Rect(x, y, w, lh), "今の位置をデフォルトにする", "default save", button)) SaveDefault(sel);
            y += lh + g;
            GUI.enabled = sel != null && HasDefault(room, sel.Id);
            if (Btn(new Rect(x, y, w2, lh), "デフォルトに戻す", "default back", button)) BackToDefault(sel);
            GUI.enabled = sel != null && HasOffset(room, sel.Id);
            if (Btn(new Rect(x + w2 + g, y, w2, lh), sel != null && sel.Id == ApproachId ? "初期化（カメラへ向かう）" : "初期化（原作の位置へ）", "init", catStyle)) Reset(sel);
            GUI.enabled = true;
            y += lh + g;
            if (Btn(new Rect(x, y, w, lh), "編集を終える", "end", button)) Toggle(false);
        }

        bool Btn(Rect r, string label, string key, GUIStyle style)
        {
            Buttons[key] = r;
            return GUI.Button(r, label, style);
        }
    }
}
