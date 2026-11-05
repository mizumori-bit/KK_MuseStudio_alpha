using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace MuseStudio
{
    // The Muse-to-Muse match (DECISIONS "Muse 同士の通信対戦：基盤の設計"; MUSE_GUIDE 8): the app's side. The bridge carries the
    // packages between the two sides (bridge/muse_match.py); the local Muse checks the opponent's and hands them here with
    // "speaker": "opponent". Here: the match's room and two bodies - hers (the local Muse's character) and the opponent's,
    // built from its creation (the card's numbers and part names, never its pictures) only when every part is here (all or
    // nothing: else the opponent is a guest without a body - its words, its body hidden as the original H hides the male);
    // the speakers' turns, the phases (intro -> negotiation -> sex -> end) and the outer limits the system holds (the
    // negotiation's turns per side, the sex's turns, the characters of the whole match), the climaxes each Muse declares and
    // the result (the climax limit loses at once; at the sex's limit fewer wins, the same a draw); leaving at any time (the
    // panel's 対戦を抜ける; the bridge's room closed by the peer ends it here too). The users only watch: no touch, no menus but
    // the camera's, the panel's leave.
    public class Match
    {
        readonly MuseStudioPlugin h;
        public bool Active;
        public string Room = "", Side = "", OpponentName = "", Phase = "", CardPath = "";
        public bool HasBody, OppMale, OppFutanari, Loading;
        public bool Replaying;               // a replay (Replay): the record's numbers, nothing counted, nothing told
        public string OpponentCreation;      // (the record) the opponent's creation as it came
        public int PicturesFound;            // (checks) the opponent's pictures found here
        public readonly List<string> Missing = new List<string>();
        public readonly Dictionary<string, int> Tally = new Dictionary<string, int> { { "host", 0 }, { "guest", 0 } };
        public readonly Dictionary<string, int> Turns = new Dictionary<string, int> { { "host", 0 }, { "guest", 0 } };    // in the phase
        public int PhaseTurns, Letters;
        public Dictionary<string, object> Limits = new Dictionary<string, object>();
        public Dictionary<string, object> Result;
        string returnTo = "";
        readonly Dictionary<int, byte> oppClothes = new Dictionary<int, byte>();
        static readonly string[] Phases = { "intro", "negotiation", "sex", "end" };
        static readonly Dictionary<string, object> Defaults = new Dictionary<string, object> {
            { "negotiation_turns", 10 }, { "sex_turns", 40 }, { "chars", 20000 }, { "climax", 3 } };

        public Match(MuseStudioPlugin host) { h = host; }

        public string OppSide { get { return Side == "host" ? "guest" : "host"; } }

        public string SideOf(string speaker) { return speaker == "opponent" ? OppSide : Side; }

        float arousalBefore = -1f;

        // the match's arousal to start from (limits.arousal_start: the host user's setting; else this side's setting)
        public float ArousalStart { get { return Mathf.Clamp01(Json.Has(Limits, "arousal_start") ? (float)Json.Num(Limits, "arousal_start") : h.MatchArousalStart.Value); } }

        public int Limit(string key) { return (int)Json.Num(Limits, key, Convert.ToDouble(Defaults[key])); }

        // the match's format (limits.format: the host's - the host user's setting, or Muse's choice - the guest's from the
        // invitation, the bridge asks the guest for it): "sex" (with the original's male part) / "lesbian" (two women: no male
        // part; the plays two women play - the original's 女性同士 and the two-body plays without a penis)
        public string Format { get { string f = Json.Str(Limits, "format", null) ?? (Side == "host" ? h.MatchFormat.Value : "sex"); return f == "lesbian" ? "lesbian" : "sex"; } }
        public bool Lesbian { get { return (Active || Loading) && Format == "lesbian"; } }

        // the lesbian format's plays: the two-body plays two women can play - a second body's motion, no penis on either side (the
        // original's 女性同士, the AL ones of tongue and fingers). Which suit a contest (both feeling it) is Muse's to choose; a
        // one-sided one may start when the talk goes there (the user's decision of 2026-11-04, the 貝合わせ-only limit lifted)
        public static bool TwoWomen(Dictionary<string, object> row)
        {
            if (row == null || !row.ContainsKey("male")) return false;
            var parts = Json.Obj(row, "parts");
            foreach (var side in new[] { "giver", "receiver" })
            {
                var ps = Json.Arr(parts, side);
                if (ps != null && ps.Contains("penis")) return false;
            }
            return true;
        }

        public bool InFormat(Dictionary<string, object> row) { return !Lesbian || TwoWomen(row); }

        public List<object> FormatPlays()
        {
            var o = new List<object>();
            foreach (var r in h.Play.List(null)) if (InFormat(r)) o.Add(Json.Str(r, "id"));
            return o;
        }

        // the two apps' conditions as the bridges compared them (GET /api/match/rooms/<room>: conditions) - a mismatch shown
        public Dictionary<string, object> Conditions { get { return Active && !Replaying ? h.Bridge.RoomConditions : null; } }

        public List<string> Mismatch()
        {
            var o = new List<string>();
            var c = Conditions;
            foreach (var k in Json.Arr(c, "mismatch") ?? new List<object>()) o.Add(Convert.ToString(k));
            return o;
        }

        // ---- the package's match field: export_creation / start / end ----
        public void FromPackage(Dictionary<string, object> mt)
        {
            if (Json.Has(mt, "export_creation") && mt["export_creation"] is bool && (bool)mt["export_creation"]) ExportCreation();
            var st = Json.Obj(mt, "start");
            if (st != null) Start(st);
            string end = Json.Str(mt, "end", null);
            if (end != null && Active) Leave(end.Length > 0 ? end : "muse");
        }

        public void Start(Dictionary<string, object> st)
        {
            if (Active || Loading) { Refuse("match.start", Json.Str(st, "room", ""), "match_running"); return; }
            if (h.Outing.Out) { Refuse("match.start", Json.Str(st, "room", ""), "she_is_out"); return; }
            h.StartCoroutine(StartRoutine(st));
        }

        IEnumerator StartRoutine(Dictionary<string, object> st)
        {
            Loading = true;
            Room = Json.Str(st, "room", "");
            Side = Json.Str(st, "side", "host") == "guest" ? "guest" : "host";
            var op = Json.Obj(st, "opponent");
            OpponentName = Json.Str(op, "name", "相手");
            OpponentCreation = Json.Str(op, "creation", null);
            Limits = Json.Obj(st, "limits") ?? new Dictionary<string, object>();
            Tally["host"] = Tally["guest"] = 0;
            Turns["host"] = Turns["guest"] = 0;
            PhaseTurns = Letters = 0;
            Result = null;
            Missing.Clear();
            h.Bridge.RoomConditions = null;
            oppAcc.Off = null;
            HasBody = false;
            OppMale = false;
            OppFutanari = false;
            CardPath = "";
            // the opponent's body from its creation: all its parts here, else none
            string err = Build(Json.Str(op, "creation", null));
            if (err.Length > 0) Missing.Add(err);
            if (h.Play.Active) h.Play.End("match");
            h.Stage.StopWalk(false);
            returnTo = h.Stage.Location;
            h.Presence.HerAt = "";                     // (the two go to the match's room)
            yield return h.Stage.SetLocation(Json.Str(h.Stage.Cfg, "match_room", "match_room"));
            if (h.Stage.Location == returnTo && returnTo != "match_room") MuseStudioPlugin.Log.LogWarning("MuseStudio: the match's room did not load; the match stays here");
            h.Stage.AddPartner(HasBody ? CardPath : "", OppMale);
            Active = true;
            Loading = false;
            Phase = "intro";
            h.Bridge.WatchRoom = Room;
            // her arousal (the mood) starts at the match's value - the host user's, carried in limits.arousal_start - for both
            // sides alike; it builds as ever from there; the urges are not told meanwhile (Urges)
            float ar;
            arousalBefore = h.Mood.Values.TryGetValue("arousal", out ar) ? ar : -1f;
            if (arousalBefore >= 0f) h.Mood.Values["arousal"] = ArousalStart;
            AfterPlay();
            if (!h.Talk.Stand(h.Stage.Cha)) h.Stage.ApplyScene(Json.Str(h.Stage.Cfg, "idle_scene", "idle"), 1, 1f);      // (her personality's stance)
            yield return null;
            Camera();
            h.Replay.Begin(this, h.MatchRecord.Value ? Creation() : null);       // (the record: her creation as she is now)
            h.Bridge.Event("match_started", new Dictionary<string, object> { { "match", Info() } });
            h.Bridge.Event("match_opponent", new Dictionary<string, object> { { "name", OpponentName }, { "body", HasBody }, { "missing", new List<object>(Missing.ToArray()) },
                { "male", OppMale }, { "futanari", OppFutanari } });
            h.SendCatalog();
        }

        // ---- a creation -> a card file of this session (never the user's folders), all parts or none ----
        public class Built { public string Error = "", Path = "", Name = ""; public bool Male, Futa; public List<string> Missing = new List<string>(); public int Pictures; }

        static Built BuildCard(string creation, string file)
        {
            var o = new Built();
            if (string.IsNullOrEmpty(creation)) { o.Error = "no creation"; return o; }
            byte[] raw;
            // (bounded before it is unpacked and while it is - the security review of 2026-11-05, SEC-03: a creation is the
            // card's numbers, well under these)
            if (creation.Length > MaxCreationChars) { o.Error = "creation too large"; return o; }
            try { raw = Unzlib(Convert.FromBase64String(creation)); }
            catch (Exception e) { o.Error = "creation unreadable: " + e.Message; return o; }
            string dir = Path.Combine(Path.GetTempPath(), "MuseStudio_match");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, file);
            File.WriteAllBytes(path, raw);
            var f = new ChaFileControl();
            if (!f.LoadCharaFile(path, 255, true, false)) { o.Error = "creation not a card"; return o; }
            o.Male = f.parameter.sex == 0;
            o.Futa = Chars.CardFutanari(f);
            o.Name = f.parameter.fullname.Trim();
            o.Missing.AddRange(MissingParts(f));
            // the pictures: each put back from the cards here, or missing (the face's / eyes'), or left out (a pattern's)
            try
            {
                int found;
                if (MatchImages.Resolve(f, o.Missing, out found))
                    using (var fs = File.Create(path)) f.SaveCharaFile(fs, false);
                o.Pictures = found;
            }
            catch (Exception e) { o.Missing.Add("pictures not read: " + e.Message); }
            o.Path = path;
            return o;
        }

        string Build(string creation)
        {
            var b = BuildCard(creation, SafeName(Room) + "_opponent.png");
            if (b.Error.Length > 0) return b.Error;
            OppMale = b.Male;
            OppFutanari = b.Futa;
            if (string.IsNullOrEmpty(OpponentName) || OpponentName == "相手") OpponentName = b.Name;
            Missing.AddRange(b.Missing);
            PicturesFound = b.Pictures;
            HasBody = Missing.Count == 0;
            CardPath = b.Path;
            return "";
        }

        internal static string SafeName(string s)
        {
            var o = new System.Text.StringBuilder();
            foreach (char c in s ?? "") o.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return o.Length > 0 ? o.ToString() : "room";
        }

        // what the card names that this game has not: its mods (Sideloader's GUIDs) and the parts of the outfit it wears, the
        // hair, the face's and the body's materials (ChaListControl)
        public static List<string> MissingParts(ChaFileControl f)
        {
            var miss = new List<string>();
            MissingMods(f, miss);
            var lc = Singleton<Manager.Character>.Instance.chaListCtrl;
            Action<int, int, string> need = (cat, id, what) =>
            {
                if (id > 0 && lc.GetListInfo((ChaListDefine.CategoryNo)cat, id) == null) miss.Add(what + " " + cat + "/" + id);
            };
            var fc = f.custom.face; var bd = f.custom.body; var hr = f.custom.hair.parts;
            for (int i = 0; i < 4 && i < hr.Length; i++) need(101 + i, hr[i].id, "hair");
            var co = f.coordinate[Mathf.Clamp(f.status.coordinateType, 0, f.coordinate.Length - 1)];
            var cl = co.clothes.parts;
            for (int i = 0; i < cl.Length; i++) need(105 + Mathf.Min(i, 7), cl[i].id, "clothes");
            if (cl.Length > 0 && (cl[0].id == 1 || cl[0].id == 2))
                for (int k = 0; k < 3 && k < co.clothes.subPartsId.Length; k++) need((cl[0].id == 1 ? 200 : 210) + k, co.clothes.subPartsId[k], "clothes part");
            foreach (var a in co.accessory.parts) if (a.type > 120) need(a.type, a.id, "accessory");
            need(406, fc.eyebrowId, "eyebrow"); need(407, fc.whiteId, "eye white"); need(408, fc.pupil[0].id, "eye");
            need(410, fc.hlUpId, "highlight"); need(411, fc.hlDownId, "highlight"); need(412, fc.eyelineUpId, "eyeline"); need(413, fc.eyelineDownId, "eyeline");
            need(414, fc.noseId, "nose"); need(415, fc.moleId, "mole"); need(404, fc.lipLineId, "lipline");
            var mk = fc.baseMakeup;
            need(401, mk.eyeshadowId, "eyeshadow"); need(402, mk.cheekId, "cheek"); need(403, mk.lipId, "lip");
            for (int k = 0; k < mk.paintId.Length; k++) need(405, mk.paintId[k], "face paint");
            for (int k = 0; k < bd.paintId.Length; k++) need(421, bd.paintId[k], "body paint");
            need(422, bd.sunburnId, "sunburn"); need(423, bd.nipId, "nipple"); need(424, bd.underhairId, "underhair");
            return miss;
        }

        // Sideloader's list on the card (its extended data: each mod item's GUID, category and slot) against the mods loaded
        static void MissingMods(ChaFileControl f, List<string> miss)
        {
            try
            {
                Type es = null, ri = null, uar = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (es == null) es = asm.GetType("ExtensibleSaveFormat.ExtendedSave");
                    if (ri == null) ri = asm.GetType("Sideloader.AutoResolver.ResolveInfo");
                    if (uar == null) uar = asm.GetType("Sideloader.AutoResolver.UniversalAutoResolver");
                }
                if (es == null || ri == null || uar == null) return;
                var pd = es.GetMethod("GetExtendedDataById", new[] { typeof(ChaFile), typeof(string) }).Invoke(null, new object[] { f, "com.bepis.sideloader.universalautoresolver" });
                if (pd == null) return;
                var data = pd.GetType().GetField("data").GetValue(pd) as Dictionary<string, object>;
                object infos;
                if (data == null || !data.TryGetValue("info", out infos) || !(infos is IEnumerable)) return;
                var loaded = new HashSet<string>();
                var lr = Member(null, uar, "LoadedResolutionInfo") as IEnumerable;
                if (lr != null) foreach (var x in lr) loaded.Add(Key(x));
                var de = ri.GetMethod("Deserialize", new[] { typeof(byte[]) });
                foreach (var b in (IEnumerable)infos)
                {
                    var info = b is byte[] && de != null ? de.Invoke(null, new object[] { (byte[])b }) : null;
                    if (info == null) continue;
                    if (!loaded.Contains(Key(info))) miss.Add("mod " + Convert.ToString(Member(info, ri, "GUID")) + " " + Key(info));
                }
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: match: the card's mods not checked: " + e.Message); }
        }

        static string Key(object info)
        {
            var t = info.GetType();
            return (Convert.ToString(Member(info, t, "GUID")) ?? "").ToLowerInvariant() + "|" + Convert.ToString(Member(info, t, "CategoryNo")) + "|" + Convert.ToString(Member(info, t, "Slot"));
        }

        static object Member(object o, Type t, string name)
        {
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
            var p = t.GetProperty(name, F);
            if (p != null) return p.GetValue(o, null);
            var fl = t.GetField(name, F);
            return fl != null ? fl.GetValue(o) : null;
        }

        // ---- her creation for the other side (match_creation): the card's numbers and part names - no picture: the card's and
        // the face crop it carries left out, the skin overlays' and MaterialEditor's pictures as their hashes (MatchImages: the
        // receiver puts back those it has), the clothes' overlays, the card's author and notes (personal) and the extra outfits
        // left out; the rest kept (numbers: the mods' list, the extra accessories, the bone adjustments, the uncensor...); every
        // outfit made the one she wears (only that one is shown) ----
        static readonly string[] DropExtended = { "KCOX", "marco.authordata", "Additional_Card_Info", "com.deathweasel.bepinex.moreoutfits" };

        public void ExportCreation() { h.Bridge.Event("match_creation", Creation()); }

        public Dictionary<string, object> Creation()
        {
            string card = h.Chars.CardPath;
            // (and this user's arousal to start a match from: the host's goes into the invitation)
            var o = new Dictionary<string, object> { { "name", h.Stage.Name }, { "arousal_start", Math.Round(h.MatchArousalStart.Value, 3) },
                { "format", h.MatchFormat.Value == "lesbian" ? "lesbian" : "sex" } };
            try
            {
                if (string.IsNullOrEmpty(card) || !File.Exists(card)) throw new Exception("no card (the default body)");
                var f = new ChaFileControl();
                if (!f.LoadCharaFile(card, 255, true, false)) throw new Exception("the card did not load");
                var images = MatchImages.HashOut(f);      // (the skin overlays' / MaterialEditor's pictures: their hashes only)
                var dropped = StripExtended(f);
                f.pngData = null;                 // (the pictures: the card's and its face crop)
                f.facePngData = null;
                int cur = Mathf.Clamp(f.status.coordinateType, 0, f.coordinate.Length - 1);
                var worn = f.coordinate[cur].SaveBytes();
                for (int i = 0; i < f.coordinate.Length; i++) if (i != cur) f.coordinate[i].LoadBytes(worn, ChaFileDefine.ChaFileCoordinateVersion);
                byte[] raw;
                using (var ms = new MemoryStream())
                {
                    f.SaveCharaFile(ms, false);
                    raw = ms.ToArray();
                }
                string creation = Convert.ToBase64String(Zlib(raw));
                o["creation"] = creation;
                o["size"] = creation.Length;
                o["male"] = f.parameter.sex == 0;
                o["dropped"] = dropped;
                o["images"] = images;
            }
            catch (Exception e) { o["creation"] = null; o["error"] = e.Message; }
            return o;
        }

        static List<object> StripExtended(ChaFile f)
        {
            var dropped = new List<object>();
            Type es = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) if ((es = asm.GetType("ExtensibleSaveFormat.ExtendedSave")) != null) break;
            if (es == null) return dropped;
            var all = es.GetMethod("GetAllExtendedData", new[] { typeof(ChaFile) }).Invoke(null, new object[] { f }) as IDictionary;
            var set = es.GetMethod("SetExtendedDataById", new[] { typeof(ChaFile), typeof(string), es.Assembly.GetType("ExtensibleSaveFormat.PluginData") });
            if (all == null || set == null) return dropped;
            var keys = new List<string>();
            foreach (var k in all.Keys) keys.Add(Convert.ToString(k));
            foreach (var k in keys)
                if (Array.IndexOf(DropExtended, k) >= 0) { set.Invoke(null, new object[] { f, k, null }); dropped.Add(k); }
            return dropped;
        }

        // zlib (RFC 1950: the Python side's zlib), with System's deflate inside
        public static byte[] Zlib(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(data, 0, data.Length);
                uint a = 1, b = 0;
                foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
                uint ad = (b << 16) | a;
                ms.WriteByte((byte)(ad >> 24)); ms.WriteByte((byte)(ad >> 16)); ms.WriteByte((byte)(ad >> 8)); ms.WriteByte((byte)ad);
                return ms.ToArray();
            }
        }

        public const int MaxCreationChars = 1536 * 1024, MaxCreationBytes = 8 * 1024 * 1024;

        // zlib -> bytes, at most `max` of them (past it: IOException, nothing more unpacked)
        public static byte[] Unzlib(byte[] z, int max = MaxCreationBytes)
        {
            using (var src = new MemoryStream(z, 2, z.Length - 2))
            using (var ds = new DeflateStream(src, CompressionMode.Decompress))
            using (var o = new MemoryStream())
            {
                var buf = new byte[65536];
                int n;
                while ((n = ds.Read(buf, 0, buf.Length)) > 0)
                {
                    if (o.Length + n > max) throw new IOException("unpacks past " + max + " bytes");
                    o.Write(buf, 0, n);
                }
                return o.ToArray();
            }
        }

        // ---- the bodies: the opponent faces her when no play has it; in a play it is the partner ----
        public void PlaceOpponent()
        {
            var st = h.Stage;
            if (!Active || st.Partner == null || st.Heroine == null) return;
            var ca = st.Heroine.oiCharInfo.changeAmount;
            float dist = (float)Json.Num(Json.Obj(Json.Obj(st.Locations, "locations"), st.Location), "opponent_distance", 1.1);
            var rot = Quaternion.Euler(0f, ca.rot.y, 0f);
            Stage.PlaceAt(st.Partner, ca.pos + rot * Vector3.forward * dist, rot * Quaternion.Euler(0f, 180f, 0f));
        }

        // after a play, and at the start: dressed as it came, its body shown (or hidden: no body), standing, looking at her
        public void AfterPlay()
        {
            var pt = h.Stage.Partner;
            if (!Active || pt == null) return;
            var cha = pt.charInfo;
            foreach (var kv in oppClothes) cha.SetClothesState(kv.Key, kv.Value, false);
            oppClothes.Clear();
            cha.fileStatus.visibleBodyAlways = HasBody;
            pt.SetVisibleSimple(false);
            pt.SetVisibleSon(false);
            PlaceOpponent();
            // (standing as its personality does - the original's talk; a male body: Studio's 男 待機)
            var an = OppMale ? new[] { 0, 14, 0 } : new[] { 0, 0, 7 };       // (Studio: 男 待機 / 基本 立ち7)
            pt.LoadAnime(an[0], an[1], an[2], 0f);
            if (!OppMale) h.Talk.Stand(cha);
            pt.ChangeLookEyesPtn(1);
            pt.ChangeLookNeckPtn(3);
            if (UnityEngine.Camera.main != null && cha.eyeLookCtrl != null) cha.eyeLookCtrl.target = h.Stage.Cha.objHeadBone != null ? h.Stage.Cha.objHeadBone.transform : UnityEngine.Camera.main.transform;
        }

        // in a play: the opponent's body as the play needs it - the female part undressed as the position says (as hers), the
        // male part bare; its penis by its body; no body: hidden as the original H's hidden male (its penis stays)
        public void ForPlay(HSceneProc.AnimationListInfo hinfo, bool femalePart)
        {
            var pt = h.Stage.Partner;
            if (pt == null) return;
            var cha = pt.charInfo;
            oppClothes.Clear();
            for (int k = 0; k < cha.fileStatus.clothesState.Length; k++) oppClothes[k] = cha.fileStatus.clothesState[k];
            if (femalePart && hinfo != null)
            {
                GlobalMethod.SetAllClothState(cha, true, hinfo.numFemaleUpperCloth, false);
                GlobalMethod.SetAllClothState(cha, false, hinfo.numFemaleLowerCloth, false);
            }
            else for (int k = 0; k < cha.fileStatus.clothesState.Length; k++) cha.SetClothesState(k, 3, false);
            cha.fileStatus.visibleBodyAlways = HasBody;
            pt.SetVisibleSon(OppMale || OppFutanari);
        }

        // ---- the opponent's clothes (from_muse/request-match-clothes-and-parse.md 1): the opponent's own package's clothes on
        // its body here, at its block (as on its side); its clothes as they were when it sent the package (clothes_now, the
        // bridge's) at the package's start - a package lost on the way, its look comes back with the next. Its own only: a
        // package's clothes are the speaker's character's. In a play the position's undressing is the play's: a change at a
        // block stays after the play, the package's snapshot waits for the play's end (it has the position's undressing in it) ----
        readonly Clothes.AccHold oppAcc = new Clothes.AccHold();

        public List<object> OpponentClothes(Dictionary<string, object> want, bool snapshot)
        {
            var pt = h.Stage.Partner;
            if (!Active || pt == null || want == null || (snapshot && h.Play.Active)) return new List<object>();
            var cha = pt.charInfo;
            var changed = Clothes.Apply(cha, want, new List<string>(), oppAcc);
            if (h.Play.Active)
            {
                // (what it comes back to after the play: each slot asked, whether or not the position had it off already)
                var worn = Clothes.Worn(cha);
                foreach (var kv in want)
                {
                    string v = kv.Value as string;
                    if (Array.IndexOf(Clothes.Values, v) < 0) continue;
                    byte b = v == "on" ? (byte)0 : v == "half" ? (byte)1 : (byte)3;
                    var slots = new List<int>();
                    if (kv.Key == "all") slots.AddRange(worn);
                    else { int k = Array.IndexOf(Clothes.Keys, kv.Key); if (k >= 0 && worn.Contains(k)) slots.Add(k); }
                    foreach (int k in slots)
                    {
                        oppClothes[k] = b;
                        if (k == 7 && cha.fileStatus.clothesState.Length > 8) oppClothes[8] = b;
                    }
                }
            }
            return changed;
        }

        // the opponent's clothes as they stand (the state's match.opponent.clothes): its own choice, the play's undressing aside
        public Dictionary<string, object> OpponentClothesInfo()
        {
            var pt = h.Stage.Partner;
            if (pt == null) return null;
            var o = Clothes.Info(pt.charInfo);
            if (h.Play.Active)
                foreach (var kv in oppClothes)
                    if (kv.Key < Clothes.Keys.Length && o.ContainsKey(Clothes.Keys[kv.Key])) o[Clothes.Keys[kv.Key]] = kv.Value == 0 ? "on" : kv.Value >= 3 ? "off" : "half";
            return o;
        }

        // ---- the inserting side drawn futanari (Muse's request of 2026-11-04): in a match the body in the original's male part
        // without a penis gets one for the play (the uncensor's "show the penis"; its creation otherwise kept, and a futanari
        // character stays one whatever its part); a strapon only when the two agreed (play.start strapon true) ----
        public bool StraponAgreed;
        readonly Dictionary<ChaControl, bool> futaBefore = new Dictionary<ChaControl, bool>();
        const System.Reflection.BindingFlags FL = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        static Component Uncensor(ChaControl cha)
        {
            foreach (var c in cha.GetComponents<MonoBehaviour>()) if (c != null && c.GetType().Name == "UncensorSelectorController") return c;
            return null;
        }

        public bool DrawFuta(Studio.OCIChar oci)
        {
            var cha = oci != null ? oci.charInfo : null;
            var uc = cha != null ? Uncensor(cha) : null;
            var dp = uc != null ? uc.GetType().GetProperty("DisplayPenis", FL) : null;
            if (dp == null) return false;
            if (!futaBefore.ContainsKey(cha)) futaBefore[cha] = (bool)dp.GetValue(uc, null);
            dp.SetValue(uc, true, null);
            uc.GetType().GetMethod("UpdateUncensor").Invoke(uc, null);
            oci.SetVisibleSon(true);
            return true;
        }

        public bool Futa(ChaControl cha) { return futaBefore.ContainsKey(cha); }

        public void UndoFuta()
        {
            foreach (var kv in futaBefore)
            {
                if (kv.Key == null) continue;
                var uc = Uncensor(kv.Key);
                var dp = uc != null ? uc.GetType().GetProperty("DisplayPenis", FL) : null;
                if (dp == null) continue;
                dp.SetValue(uc, kv.Value, null);
                uc.GetType().GetMethod("UpdateUncensor").Invoke(uc, null);
            }
            futaBefore.Clear();
        }

        public void Camera()
        {
            var st = h.Stage;
            if (st.Partner == null || st.Cha == null || st.Cha.objHeadBone == null) return;
            Vector3 a = st.Cha.objHeadBone.transform.position, b = st.Partner.charInfo.objHeadBone != null ? st.Partner.charInfo.objHeadBone.transform.position : a;
            Vector3 mid = (a + b) * 0.5f, across = b - a;
            across.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, across.sqrMagnitude > 1e-4f ? across.normalized : Vector3.forward);
            st.SetCamera(mid + side * 2.4f + Vector3.up * 0.05f, mid - Vector3.up * 0.25f, 45f, 0.9f);
        }

        // ---- the turns, the declarations, the phases ----
        public void Played(Dictionary<string, object> pkg, string speaker)
        {
            if (!Active) return;
            if (Replaying) { h.Replay.AtPackage(this); return; }
            string side = SideOf(speaker);
            Turns[side]++;
            PhaseTurns++;
            foreach (var b in Json.Arr(pkg, "blocks") ?? new List<object>()) Letters += Json.Str(b, "text", "").Length;
            Caps();
            h.Replay.Mark(this, -1);
            if (Result != null) h.Replay.Save(this, null);
        }

        public void Block(Dictionary<string, object> b, string speaker)
        {
            if (!Active) return;
            bool declared = Json.Str(b, "declare", null) == "climax";
            string ph = Json.Str(b, "match_phase", null);
            if (Replaying)
            {
                // (a replay: the declaration shown as live, the numbers as recorded)
                if (declared) { Show(SideOf(speaker)); TallyAt = Time.realtimeSinceStartup; }
                h.Replay.AtBlock(this, h.Blocks.Index);
                return;
            }
            if (declared) Declare(SideOf(speaker));
            if (ph != null) ToPhase(ph, "muse");
            if (declared || ph != null)
            {
                h.Replay.Mark(this, h.Blocks.Index);
                if (Result != null) h.Replay.Save(this, null);
            }
        }

        void Declare(string side)
        {
            if (Result != null) return;
            Tally[side]++;
            h.Bridge.Event("match_climax", new Dictionary<string, object> { { "side", side }, { "count", Tally[side] }, { "tally", TallyInfo() } });
            Show(side);
            TallyAt = Time.realtimeSinceStartup;
            if (Tally[side] >= Limit("climax")) Finish("climax_limit", side == "host" ? "guest" : "host");
        }

        // The declared climax shown on the declaring body (Muse's request of 2026-11-04: for the users who watch, told to
        // neither Muse - no event, no state of it): its climax in the play (Climax.PresentDeclared), and its climax face for a
        // while (a face the block itself asks for wins); no play for it, or no motion: the face only
        float faceUntil = -1f;
        string faceSide = "", faceBack = "";
        public float TallyAt = -10f;           // (the broadcast view's flash)

        void Show(string side)
        {
            var st = h.Stage;
            bool self = side == Side;
            if (h.Play.Active) h.Play.Climax.PresentDeclared(self ? "character" : "player", Lesbian);
            if (self) { faceBack = st.Face; st.ApplyPreset("ecstasy"); }
            else if (HasBody) st.ApplyPresetOn(st.Partner, "ecstasy");
            faceSide = side;
            faceUntil = Time.realtimeSinceStartup + (float)Json.Num(Json.Obj(st.Cfg, "match"), "climax_face_seconds", 5.0);
        }

        void FaceTick()
        {
            if (faceUntil < 0f || Time.realtimeSinceStartup < faceUntil) return;
            faceUntil = -1f;
            var st = h.Stage;
            if (faceSide == Side) { if (st.Face == "preset:ecstasy") st.RestoreFace(faceBack); }
            else if (HasBody && st.Partner != null) st.ApplyFaceOn(st.Partner, "neutral");
        }

        // the broadcast view's camera (Ui: 中継風): outside a play it goes slowly round the two
        float orbit;
        public void BroadcastCamera(float dt)
        {
            var st = h.Stage;
            if (h.Play.Active || st.Partner == null || st.Cha == null || st.Cha.objHeadBone == null || st.Partner.charInfo.objHeadBone == null) return;
            orbit += dt * 6f;
            Vector3 a = st.Cha.objHeadBone.transform.position, b = st.Partner.charInfo.objHeadBone.transform.position;
            Vector3 mid = (a + b) * 0.5f, across = b - a;
            across.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, across.sqrMagnitude > 1e-4f ? across.normalized : Vector3.forward);
            Vector3 dir = Quaternion.AngleAxis(Mathf.Sin(orbit * Mathf.Deg2Rad) * 35f, Vector3.up) * side;
            st.SetCamera(mid + dir * 2.6f + Vector3.up * 0.15f, mid - Vector3.up * 0.25f, 42f, 0f);
        }

        void ToPhase(string ph, string by)
        {
            int now = Array.IndexOf(Phases, Phase), want = Array.IndexOf(Phases, ph);
            if (want <= now) { if (want < now) Refuse("match_phase", ph, "backward"); return; }
            Phase = ph;
            Turns["host"] = Turns["guest"] = 0;
            PhaseTurns = 0;
            h.Bridge.Event("match_phase", new Dictionary<string, object> { { "phase", Phase }, { "by", by }, { "match", Info() } });
            if (Phase == "end") Finish("muse");
        }

        // the outer limits (the system's; not the negotiation's): the negotiation's turns of each side -> sex; the sex's turns
        // or the whole match's characters -> the end by the counts
        void Caps()
        {
            if (Result != null) return;
            if (Letters >= Limit("chars")) { ToPhaseSystem("end"); Finish("chars"); return; }
            if (Phase == "negotiation" && Turns["host"] >= Limit("negotiation_turns") && Turns["guest"] >= Limit("negotiation_turns")) ToPhaseSystem("sex");
            else if (Phase == "sex" && PhaseTurns >= Limit("sex_turns")) { ToPhaseSystem("end"); Finish("sex_turns"); }
        }

        void ToPhaseSystem(string ph) { if (Phase != ph) ToPhase(ph, "system"); }

        void Finish(string reason, string winner = null)
        {
            if (Result != null) return;
            bool draw = false;
            if (winner == null)
            {
                if (Tally["host"] == Tally["guest"]) draw = true;
                else winner = Tally["host"] < Tally["guest"] ? "host" : "guest";       // (more climaxes lose)
            }
            Result = new Dictionary<string, object> { { "winner", winner }, { "draw", draw }, { "reason", reason }, { "tally", TallyInfo() } };
            if (Phase != "end")
            {
                Phase = "end";
                h.Bridge.Event("match_phase", new Dictionary<string, object> { { "phase", Phase }, { "by", "system" }, { "match", Info() } });
            }
            h.Bridge.Event("match_result", new Dictionary<string, object> { { "result", Result }, { "match", Info() } });
        }

        // ---- leaving: at any time (the user's, Muse's match.end, the peer's close) ----
        public void Leave(string reason)
        {
            if (!Active || Replaying) return;
            h.Replay.End(this, reason);              // (the record saved with how it ended)
            h.Talk.Forget(h.Stage.Cha);
            if (h.Play.Active) h.Play.End("match");
            UndoFuta();
            Active = false;
            // her arousal: back to what it was before the match (a visit: home's mood goes on unchanged), unless the setting
            // keeps what built up; the urges holding are told again
            if (arousalBefore >= 0f && !h.MatchKeepMood.Value) h.Mood.Values["arousal"] = arousalBefore;
            arousalBefore = -1f;
            h.Urges.Start("match_ended");
            h.Bridge.WatchRoom = null;
            h.Stage.RemovePartner();
            if (reason == "user_left") h.Bridge.Post("/api/match/rooms/" + Room + "/close", new Dictionary<string, object> { { "reason", reason } });
            h.Bridge.Event("match_left", new Dictionary<string, object> { { "reason", reason }, { "room", Room }, { "result", Result } });
            try { if (CardPath.Length > 0 && File.Exists(CardPath)) File.Delete(CardPath); } catch (Exception) { }
            string back = returnTo.Length > 0 ? returnTo : Json.Str(h.Stage.Cfg, "location", "myroom");
            h.StartCoroutine(Back(back));
        }

        // ---- a replay (Replay): the match's stage again from its record - both bodies from the recorded creations (all or
        // nothing, as live; hers too: the character on the stage changes for it and comes back after, dressed as she was) ----
        string selfBack = "", selfTemp = "", selfBackTemp = "", nameBack = "";
        int coordBack;
        byte[] clothesBack;
        bool selfSwapped, selfHidden;
        public bool SelfHasBody { get { return !selfHidden; } }

        public IEnumerator StartReplay(Dictionary<string, object> doc)
        {
            Loading = true;
            Replaying = true;
            Room = "replay";
            Side = Json.Str(doc, "side", "host") == "guest" ? "guest" : "host";
            var op = Json.Obj(doc, "opponent");
            var me = Json.Obj(doc, "self");
            OpponentName = Json.Str(op, "name", "相手");
            OpponentCreation = null;
            Limits = new Dictionary<string, object>(Json.Obj(doc, "limits") ?? new Dictionary<string, object>());
            if (!Limits.ContainsKey("format")) Limits["format"] = Json.Str(doc, "format", "sex");
            Tally["host"] = Tally["guest"] = 0;
            Turns["host"] = Turns["guest"] = 0;
            PhaseTurns = Letters = 0;
            Result = null;
            Missing.Clear();
            HasBody = OppMale = OppFutanari = StraponAgreed = false;
            CardPath = "";
            oppAcc.Off = null;
            if (h.Play.Active) h.Play.End("replay");
            h.Stage.StopWalk(false);
            // her body from her recorded creation (as she was then), else hidden - all or nothing
            var cha = h.Stage.Cha;
            nameBack = h.Stage.Name;
            coordBack = cha.fileStatus.coordinateType;
            clothesBack = (byte[])cha.fileStatus.clothesState.Clone();
            selfSwapped = selfHidden = false;
            selfBack = h.Chars.CardPath;
            selfBackTemp = "";
            var mine = BuildCard(Json.Str(me, "creation", null), "replay_self.png");
            if (mine.Error.Length == 0 && mine.Missing.Count == 0)
            {
                if (string.IsNullOrEmpty(selfBack) || !File.Exists(selfBack))
                {
                    // (the stage's default body: kept as it is to come back to)
                    selfBackTemp = Path.Combine(Path.Combine(Path.GetTempPath(), "MuseStudio_match"), "replay_back.png");
                    using (var fs = File.Create(selfBackTemp)) cha.chaFile.SaveCharaFile(fs, false);
                    selfBack = selfBackTemp;
                }
                selfTemp = mine.Path;
                SwapHeroine(mine.Path, Json.Str(me, "name", mine.Name));
                selfSwapped = true;
            }
            else
            {
                cha.fileStatus.visibleBodyAlways = false;
                selfHidden = true;
            }
            // the opponent's
            var theirs = BuildCard(Json.Str(op, "creation", null), "replay_opponent.png");
            if (theirs.Error.Length > 0) Missing.Add(theirs.Error);
            else
            {
                OppMale = theirs.Male;
                OppFutanari = theirs.Futa;
                Missing.AddRange(theirs.Missing);
                CardPath = theirs.Path;
                HasBody = Missing.Count == 0;
            }
            returnTo = h.Stage.Location;
            h.Presence.HerAt = "";
            yield return h.Stage.SetLocation(Json.Str(h.Stage.Cfg, "match_room", "match_room"));
            h.Stage.AddPartner(HasBody ? CardPath : "", OppMale);
            Active = true;
            Loading = false;
            Phase = "intro";
            AfterPlay();
            if (!h.Talk.Stand(h.Stage.Cha)) h.Stage.ApplyScene(Json.Str(h.Stage.Cfg, "idle_scene", "idle"), 1, 1f);
            yield return null;
            Camera();
        }

        void SwapHeroine(string path, string name)
        {
            var st = h.Stage;
            st.Heroine.ChangeChara(path);
            st.Name = string.IsNullOrEmpty(name) ? st.Cha.chaFile.parameter.fullname.Trim() : name;
            st.Heroine.treeNodeObject.textName = st.Name;
            st.Rebuilt();
            h.Touch.Rebind();
        }

        public IEnumerator EndReplay()
        {
            if (h.Play.Active) h.Play.End("replay");
            UndoFuta();
            Active = false;
            faceUntil = -1f;
            h.Talk.Forget(h.Stage.Cha);
            h.Stage.RemovePartner();
            var cha = h.Stage.Cha;
            if (selfSwapped && File.Exists(selfBack))
            {
                SwapHeroine(selfBack, nameBack);
                cha = h.Stage.Cha;
                h.Stage.Heroine.SetCoordinateInfo((ChaFileDefine.CoordinateType)Mathf.Clamp(coordBack, 0, cha.chaFile.coordinate.Length - 1), true);
                for (int k = 0; k < Math.Min(clothesBack.Length, cha.fileStatus.clothesState.Length); k++) cha.SetClothesState(k, clothesBack[k], false);
            }
            if (selfHidden) cha.fileStatus.visibleBodyAlways = true;
            foreach (var f in new[] { CardPath, selfTemp, selfBackTemp })
                try { if (!string.IsNullOrEmpty(f) && File.Exists(f)) File.Delete(f); } catch (Exception) { }
            CardPath = selfTemp = selfBackTemp = "";
            selfSwapped = selfHidden = false;
            Replaying = false;
            Room = "";
            Result = null;
            string back = returnTo.Length > 0 ? returnTo : Json.Str(h.Stage.Cfg, "location", "myroom");
            yield return Back(back);
        }

        IEnumerator Back(string loc)
        {
            yield return h.Stage.SetLocation(loc);
            h.Stage.Place(h.Stage.Heroine);
            h.Stage.ApplyScene(Json.Str(h.Stage.Cfg, "idle_scene", "idle"), 1, 1f);
            h.Stage.ApplyCamera(Json.Str(h.Stage.Cameras, "default", "medium"), true);
            h.SendCatalog();
        }

        public void Tick()
        {
            if (!Active) return;
            FaceTick();
            if (h.MatchBroadcast.Value) BroadcastCamera(Time.deltaTime);
            if (h.Bridge.RoomClosedBy == "peer") { h.Bridge.RoomClosedBy = null; Leave("peer_left"); }
        }

        void Refuse(string field, string value, string reason)
        {
            h.Bridge.Event("block_refused", new Dictionary<string, object> { { "field", field }, { "value", value }, { "reason", reason }, { "available", new List<object>() } });
        }

        Dictionary<string, object> TallyInfo() { return new Dictionary<string, object> { { "host", Tally["host"] }, { "guest", Tally["guest"] } }; }

        public Dictionary<string, object> Info()
        {
            if (!Active) return null;
            var lim = new Dictionary<string, object>();
            foreach (var k in Defaults.Keys) lim[k] = Limit(k);
            lim["arousal_start"] = Math.Round(ArousalStart, 3);
            lim["format"] = Format;
            var cond = Conditions;
            return new Dictionary<string, object> { { "room", Room }, { "side", Side }, { "phase", Phase }, { "format", Format },
                { "conditions", cond == null ? null : new Dictionary<string, object> { { "agreed", cond.ContainsKey("agreed") ? cond["agreed"] : null }, { "mismatch", Json.Arr(cond, "mismatch") ?? new List<object>() } } },
                { "turns", new Dictionary<string, object> { { "host", Turns["host"] }, { "guest", Turns["guest"] }, { "phase", PhaseTurns } } },
                { "chars", Letters }, { "limits", lim }, { "tally", TallyInfo() }, { "result", Result },
                { "pose", h.Talk.NowOf(h.Stage.Cha) }, { "personality", h.Talk.PersonalityInfo(h.Stage.Cha) },
                { "opponent", new Dictionary<string, object> { { "name", OpponentName }, { "body", HasBody }, { "male", OppMale }, { "futanari", OppFutanari }, { "clothes", OpponentClothesInfo() },
                    { "pose", h.Stage.Partner != null ? h.Talk.NowOf(h.Stage.Partner.charInfo) : null }, { "personality", h.Stage.Partner != null ? h.Talk.PersonalityInfo(h.Stage.Partner.charInfo) : null },
                    { "missing", new List<object>(Missing.ToArray()) } } } };
        }
    }
}
