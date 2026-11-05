using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace MuseStudio
{
    // The match's pictures (Muse's correction of 2026-11-04: whether an opponent can be made is the receiver's stock-taking,
    // as for its parts and mods). The creation carries no picture - the skin overlays' (KSOX) and MaterialEditor's images go
    // as their SHA-256 ("mmsha256:<hex>" in the image's place), the rest of their data as it is (which picture is where). The
    // receiver looks for each among the pictures this side has (ImageIndex: the cards here) and puts it back; a picture not
    // here that the identity rests on (the face's, the eyes', the eyebrows', the eyelines' - KSOX's kinds, MaterialEditor's
    // materials by name) is a part missing (no body); one of a pattern only (the body's, other materials) is left out and
    // the part shows without it (a pattern that differs, as ever).
    public static class MatchImages
    {
        public const string Mark = "mmsha256:";
        static readonly int[] CoreTex = { 2, 4, 5, 6, 7, 8, 9, 10, 20, 30 };        // KSOX TexType: FaceOver / FaceUnder / Eye* / EyebrowUnder / EyelineUnder
        static readonly string[] CoreTexNames = { "FaceOver", "FaceUnder", "EyeUnder", "EyeOver", "EyeUnderL", "EyeOverL", "EyeUnderR", "EyeOverR", "EyebrowUnder", "EyelineUnder" };
        static readonly string[] CoreMaterials = { "hitomi", "sirome", "cf_m_face", "mayuge", "eyeline", "namida" };
        const string KSOX = "KSOX", ME = "com.deathweasel.bepinex.materialeditor";

        public static string Sha(byte[] b)
        {
            using (var sha = SHA256.Create())
            {
                var hb = sha.ComputeHash(b);
                var sb = new StringBuilder(hb.Length * 2);
                foreach (var x in hb) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        // ---- the extended data (ExtensibleSaveFormat, by reflection) ----
        static Type es;
        static Type ES { get { if (es == null) foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) if ((es = asm.GetType("ExtensibleSaveFormat.ExtendedSave")) != null) break; return es; } }

        static Dictionary<string, object> Data(ChaFile f, string id, out int version)
        {
            version = 0;
            if (ES == null) return null;
            var pd = ES.GetMethod("GetExtendedDataById", new[] { typeof(ChaFile), typeof(string) }).Invoke(null, new object[] { f, id });
            if (pd == null) return null;
            version = Convert.ToInt32(pd.GetType().GetField("version").GetValue(pd));
            return pd.GetType().GetField("data").GetValue(pd) as Dictionary<string, object>;
        }

        static void Drop(ChaFile f, string id)
        {
            var pdT = ES.Assembly.GetType("ExtensibleSaveFormat.PluginData");
            ES.GetMethod("SetExtendedDataById", new[] { typeof(ChaFile), typeof(string), pdT }).Invoke(null, new object[] { f, id, null });
        }

        // ---- the sender: each picture becomes its hash; what the pictures were (for Muse: kind, core, hash) ----
        public static List<object> HashOut(ChaFile f)
        {
            var o = new List<object>();
            int coord = Mathf.Clamp(f.status.coordinateType, 0, f.coordinate.Length - 1);
            int ver;
            var ks = Data(f, KSOX, out ver);
            if (ks != null)
            {
                var core = KsoxCoreIds(ks, ver, coord);
                foreach (var k in new List<string>(ks.Keys))
                {
                    var b = ks[k] as byte[];
                    if (b == null || k == "Lookup") continue;
                    string sha = Sha(b);
                    ks[k] = Mark + sha;
                    bool c = ver >= 2 ? core.Contains(TexId(k)) : Array.IndexOf(CoreTexNames, k) >= 0;
                    o.Add(new Dictionary<string, object> { { "plugin", "KSOX" }, { "key", k }, { "sha256", sha }, { "core", c } });
                }
            }
            var me = Data(f, ME, out ver);
            object td;
            if (me != null && me.TryGetValue("TextureDictionary", out td) && td is byte[])
            {
                var core = MeCoreIds(me);
                var map = MsgPack.Read((byte[])td) as MsgPack.Map;
                if (map != null)
                {
                    for (int i = 0; i < map.Count; i++)
                    {
                        var b = map[i].Value as byte[];
                        if (b == null) continue;
                        string sha = Sha(b);
                        long id = Convert.ToInt64(map[i].Key);
                        map[i] = new KeyValuePair<object, object>(map[i].Key, Mark + sha);
                        o.Add(new Dictionary<string, object> { { "plugin", "MaterialEditor" }, { "key", id }, { "sha256", sha }, { "core", core.Contains(id) } });
                    }
                    me["TextureDictionary"] = MsgPack.Write(map);
                }
            }
            return o;
        }

        static long TexId(string key) { long v; return key.StartsWith("_TextureID_") && long.TryParse(key.Substring(11), out v) ? v : -1; }

        // the texture ids KSOX shows at a kind of the face / eyes / eyebrows / eyelines for the outfit worn
        static HashSet<long> KsoxCoreIds(Dictionary<string, object> ks, int ver, int coord)
        {
            var o = new HashSet<long>();
            object lk;
            if (ver < 2 || !ks.TryGetValue("Lookup", out lk) || !(lk is byte[])) return o;
            var all = MsgPack.Read((byte[])lk) as MsgPack.Map;
            if (all == null) return o;
            foreach (var kv in all)
            {
                if (Convert.ToInt64(kv.Key) != coord) continue;
                var inner = kv.Value as MsgPack.Map;
                if (inner == null) continue;
                foreach (var t in inner) if (Array.IndexOf(CoreTex, Convert.ToInt32(t.Key)) >= 0) o.Add(Convert.ToInt64(t.Value));
            }
            return o;
        }

        // the texture ids MaterialEditor puts on the face's / eyes' / eyebrows' / eyelines' materials
        static HashSet<long> MeCoreIds(Dictionary<string, object> me)
        {
            var o = new HashSet<long>();
            object tl;
            if (!me.TryGetValue("MaterialTexturePropertyList", out tl) || !(tl is byte[])) return o;
            var list = MsgPack.Read((byte[])tl) as List<object>;
            if (list == null) return o;
            foreach (var e in list)
            {
                var m = e as MsgPack.Map;
                if (m == null) continue;
                string name = Convert.ToString(m.Get("MaterialName")) ?? "";
                object id = m.Get("TexID");
                if (id == null) continue;
                foreach (var c in CoreMaterials) if (name.IndexOf(c, StringComparison.Ordinal) >= 0) { o.Add(Convert.ToInt64(id)); break; }
            }
            return o;
        }

        // ---- the receiver: each hash back to its picture from this side's cards; what is not here: a core one missing (the
        // body is not made), a pattern's left out (its entries out of the data); true when the card changed ----
        public static bool Resolve(ChaFile f, List<string> missing, out int found)
        {
            found = 0;
            bool changed = false;
            int coord = Mathf.Clamp(f.status.coordinateType, 0, f.coordinate.Length - 1);
            int ver;
            var ks = Data(f, KSOX, out ver);
            if (ks != null)
            {
                var core = KsoxCoreIds(ks, ver, coord);
                var gone = new HashSet<long>();
                foreach (var k in new List<string>(ks.Keys))
                {
                    string s = ks[k] as string;
                    if (s == null || !s.StartsWith(Mark)) continue;
                    var b = ImageIndex.Find(s.Substring(Mark.Length));
                    changed = true;
                    if (b != null) { ks[k] = b; found++; continue; }
                    bool c = ver >= 2 ? core.Contains(TexId(k)) : Array.IndexOf(CoreTexNames, k) >= 0;
                    if (c) missing.Add("picture " + KsoxKind(k, ks, ver, coord) + " (KSOX " + s.Substring(Mark.Length, 12) + ")");
                    ks.Remove(k);
                    gone.Add(TexId(k));
                }
                if (ver >= 2 && gone.Count > 0)
                {
                    object lk;
                    var all = ks.TryGetValue("Lookup", out lk) && lk is byte[] ? MsgPack.Read((byte[])lk) as MsgPack.Map : null;
                    if (all != null)
                    {
                        foreach (var kv in all)
                        {
                            var inner = kv.Value as MsgPack.Map;
                            if (inner != null) inner.RemoveAll(t => gone.Contains(Convert.ToInt64(t.Value)));
                        }
                        ks["Lookup"] = MsgPack.Write(all);
                    }
                }
            }
            var me = Data(f, ME, out ver);
            object td;
            if (me != null && me.TryGetValue("TextureDictionary", out td) && td is byte[])
            {
                var map = MsgPack.Read((byte[])td) as MsgPack.Map;
                var core = MeCoreIds(me);
                var gone = new HashSet<long>();
                if (map != null)
                {
                    for (int i = map.Count - 1; i >= 0; i--)
                    {
                        string s = map[i].Value as string;
                        if (s == null || !s.StartsWith(Mark)) continue;
                        changed = true;
                        long id = Convert.ToInt64(map[i].Key);
                        var b = ImageIndex.Find(s.Substring(Mark.Length));
                        if (b != null) { map[i] = new KeyValuePair<object, object>(map[i].Key, b); found++; continue; }
                        if (core.Contains(id)) missing.Add("picture a texture of the face / eyes (MaterialEditor " + s.Substring(Mark.Length, 12) + ")");
                        map.RemoveAt(i);
                        gone.Add(id);
                    }
                    me["TextureDictionary"] = MsgPack.Write(map);
                    object tl;
                    if (gone.Count > 0 && me.TryGetValue("MaterialTexturePropertyList", out tl) && tl is byte[])
                    {
                        var list = MsgPack.Read((byte[])tl) as List<object>;
                        if (list != null)
                        {
                            list.RemoveAll(e => { var m = e as MsgPack.Map; object id = m != null ? m.Get("TexID") : null; return id != null && gone.Contains(Convert.ToInt64(id)); });
                            me["MaterialTexturePropertyList"] = MsgPack.Write(list);
                        }
                    }
                }
            }
            return changed;
        }

        static string KsoxKind(string key, Dictionary<string, object> ks, int ver, int coord)
        {
            if (ver < 2) return key;
            long id = TexId(key);
            object lk;
            var all = ks.TryGetValue("Lookup", out lk) && lk is byte[] ? MsgPack.Read((byte[])lk) as MsgPack.Map : null;
            if (all != null)
                foreach (var kv in all)
                {
                    if (Convert.ToInt64(kv.Key) != coord || !(kv.Value is MsgPack.Map)) continue;
                    foreach (var t in (MsgPack.Map)kv.Value)
                        if (Convert.ToInt64(t.Value) == id)
                        {
                            int tt = Convert.ToInt32(t.Key);
                            return tt == 2 || tt == 4 ? "face overlay" : tt == 20 ? "eyebrow overlay" : tt == 30 ? "eyeline overlay" : "eye overlay";
                        }
                }
            return "overlay";
        }

        // (checks) every picture's hash in a creation made one nobody has
        public static int Unknown(ChaFile f)
        {
            int n = 0, ver;
            var ks = Data(f, KSOX, out ver);
            if (ks != null) foreach (var k in new List<string>(ks.Keys)) if (ks[k] is string && ((string)ks[k]).StartsWith(Mark)) { ks[k] = Mark + new string('0', 64); n++; }
            return n;
        }

        // the pictures a card holds (KSOX's and MaterialEditor's), by hash
        public static Dictionary<string, byte[]> Pictures(ChaFile f)
        {
            var o = new Dictionary<string, byte[]>();
            int ver;
            var ks = Data(f, KSOX, out ver);
            if (ks != null) foreach (var kv in ks) if (kv.Value is byte[] && kv.Key != "Lookup") o[Sha((byte[])kv.Value)] = (byte[])kv.Value;
            var me = Data(f, ME, out ver);
            object td;
            if (me != null && me.TryGetValue("TextureDictionary", out td) && td is byte[])
            {
                var map = MsgPack.Read((byte[])td) as MsgPack.Map;
                if (map != null) foreach (var kv in map) if (kv.Value is byte[]) o[Sha((byte[])kv.Value)] = (byte[])kv.Value;
            }
            return o;
        }
    }

    // The pictures this side has: the cards here (the maker's saves - female / male - and the project's card folder), their
    // KSOX and MaterialEditor pictures by hash; the list kept in a file by each card's time (only the hashes; the picture is
    // read from its card when wanted)
    public static class ImageIndex
    {
        public static string CachePath = "";
        static Dictionary<string, KeyValuePair<long, List<string>>> cards;     // path -> (time, hashes)
        public static int Scanned;            // (checks) cards read this time

        static IEnumerable<string> Files()
        {
            var dirs = new List<KeyValuePair<string, bool>> {
                new KeyValuePair<string, bool>(Chars.Dir(false), false), new KeyValuePair<string, bool>(Chars.Dir(true), false),
                new KeyValuePair<string, bool>(Path.GetFullPath(Path.Combine(MuseStudioPlugin.DataDir, Path.Combine("..", Path.Combine("..", "キャラカード")))), true) };
            foreach (var d in dirs)
            {
                if (!Directory.Exists(d.Key)) continue;
                foreach (var f in Directory.GetFiles(d.Key, "*.png", d.Value ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)) yield return f;
            }
        }

        static void Refresh()
        {
            if (cards == null)
            {
                cards = new Dictionary<string, KeyValuePair<long, List<string>>>();
                try
                {
                    if (CachePath.Length > 0 && File.Exists(CachePath))
                        foreach (var kv in Json.Obj(Json.Parse(File.ReadAllText(CachePath, Encoding.UTF8)), "cards") ?? new Dictionary<string, object>())
                        {
                            var hs = new List<string>();
                            foreach (var x in Json.Arr(kv.Value, "hashes") ?? new List<object>()) hs.Add((string)x);
                            cards[kv.Key] = new KeyValuePair<long, List<string>>((long)Json.Num(kv.Value, "time"), hs);
                        }
                }
                catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: match images: the list not read: " + e.Message); }
            }
            bool dirty = false;
            var seen = new HashSet<string>();
            foreach (var path in Files())
            {
                seen.Add(path);
                long t = File.GetLastWriteTimeUtc(path).Ticks;
                KeyValuePair<long, List<string>> have;
                if (cards.TryGetValue(path, out have) && have.Key == t) continue;
                var f = new ChaFileControl();
                var hs = new List<string>();
                try { if (f.LoadCharaFile(path, 255, true, true)) hs.AddRange(MatchImages.Pictures(f).Keys); }
                catch (Exception) { }
                cards[path] = new KeyValuePair<long, List<string>>(t, hs);
                Scanned++;
                dirty = true;
            }
            foreach (var p in new List<string>(cards.Keys)) if (!seen.Contains(p)) { cards.Remove(p); dirty = true; }
            if (dirty && CachePath.Length > 0)
            {
                try
                {
                    var doc = new Dictionary<string, object>();
                    foreach (var kv in cards) doc[kv.Key] = new Dictionary<string, object> { { "time", kv.Value.Key }, { "hashes", new List<object>(kv.Value.Value.ToArray()) } };
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                    File.WriteAllText(CachePath, Json.Write(new Dictionary<string, object> { { "cards", doc } }), Encoding.UTF8);
                }
                catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: match images: the list not kept: " + e.Message); }
            }
        }

        public static void Forget() { cards = null; }

        // the picture of that hash from a card here (null: none here)
        public static byte[] Find(string sha)
        {
            Refresh();
            foreach (var kv in cards)
            {
                if (!kv.Value.Value.Contains(sha)) continue;
                var f = new ChaFileControl();
                try
                {
                    if (!f.LoadCharaFile(kv.Key, 255, true, true)) continue;
                    byte[] b;
                    if (MatchImages.Pictures(f).TryGetValue(sha, out b)) return b;
                }
                catch (Exception) { }
            }
            return null;
        }
    }

    // MessagePack, read and written again as it was (maps keep their order; ints, floats, strings, binaries, arrays, maps,
    // nil, booleans, ext): enough for the overlays' lookup and MaterialEditor's lists
    public static class MsgPack
    {
        public class Map : List<KeyValuePair<object, object>>
        {
            public object Get(string key)
            {
                foreach (var kv in this) if (kv.Key is string && (string)kv.Key == key) return kv.Value;
                return null;
            }
        }

        public class Ext { public sbyte Type; public byte[] Data; }

        public static object Read(byte[] b) { int i = 0; return Next(b, ref i); }

        static int Be(byte[] b, ref int i, int n) { int v = 0; for (int k = 0; k < n; k++) v = (v << 8) | b[i++]; return v; }

        static long BeL(byte[] b, ref int i, int n) { long v = 0; for (int k = 0; k < n; k++) v = (v << 8) | b[i++]; return v; }

        static object Next(byte[] b, ref int i)
        {
            byte c = b[i++];
            if (c <= 0x7f) return (long)c;
            if (c >= 0xe0) return (long)(sbyte)c;
            if (c >= 0x80 && c <= 0x8f) return ReadMap(b, ref i, c & 0x0f);
            if (c >= 0x90 && c <= 0x9f) return ReadArray(b, ref i, c & 0x0f);
            if (c >= 0xa0 && c <= 0xbf) return Str(b, ref i, c & 0x1f);
            switch (c)
            {
                case 0xc0: return null;
                case 0xc2: return false;
                case 0xc3: return true;
                case 0xc4: return Bin(b, ref i, Be(b, ref i, 1));
                case 0xc5: return Bin(b, ref i, Be(b, ref i, 2));
                case 0xc6: return Bin(b, ref i, Be(b, ref i, 4));
                case 0xc7: { int n = Be(b, ref i, 1); sbyte t = (sbyte)b[i++]; return new Ext { Type = t, Data = Bin(b, ref i, n) }; }
                case 0xc8: { int n = Be(b, ref i, 2); sbyte t = (sbyte)b[i++]; return new Ext { Type = t, Data = Bin(b, ref i, n) }; }
                case 0xc9: { int n = Be(b, ref i, 4); sbyte t = (sbyte)b[i++]; return new Ext { Type = t, Data = Bin(b, ref i, n) }; }
                case 0xca: { var x = new byte[4]; Array.Copy(b, i, x, 0, 4); i += 4; if (BitConverter.IsLittleEndian) Array.Reverse(x); return BitConverter.ToSingle(x, 0); }
                case 0xcb: { var x = new byte[8]; Array.Copy(b, i, x, 0, 8); i += 8; if (BitConverter.IsLittleEndian) Array.Reverse(x); return BitConverter.ToDouble(x, 0); }
                case 0xcc: return (long)Be(b, ref i, 1);
                case 0xcd: return (long)Be(b, ref i, 2);
                case 0xce: return BeL(b, ref i, 4);
                case 0xcf: return BeL(b, ref i, 8);
                case 0xd0: return (long)(sbyte)b[i++];
                case 0xd1: return (long)(short)Be(b, ref i, 2);
                case 0xd2: return (long)Be(b, ref i, 4);
                case 0xd3: return BeL(b, ref i, 8);
                case 0xd4: { sbyte t = (sbyte)b[i++]; return new Ext { Type = t, Data = Bin(b, ref i, 1) }; }
                case 0xd5: { sbyte t = (sbyte)b[i++]; return new Ext { Type = t, Data = Bin(b, ref i, 2) }; }
                case 0xd6: { sbyte t = (sbyte)b[i++]; return new Ext { Type = t, Data = Bin(b, ref i, 4) }; }
                case 0xd7: { sbyte t = (sbyte)b[i++]; return new Ext { Type = t, Data = Bin(b, ref i, 8) }; }
                case 0xd8: { sbyte t = (sbyte)b[i++]; return new Ext { Type = t, Data = Bin(b, ref i, 16) }; }
                case 0xd9: return Str(b, ref i, Be(b, ref i, 1));
                case 0xda: return Str(b, ref i, Be(b, ref i, 2));
                case 0xdb: return Str(b, ref i, Be(b, ref i, 4));
                case 0xdc: return ReadArray(b, ref i, Be(b, ref i, 2));
                case 0xdd: return ReadArray(b, ref i, Be(b, ref i, 4));
                case 0xde: return ReadMap(b, ref i, Be(b, ref i, 2));
                case 0xdf: return ReadMap(b, ref i, Be(b, ref i, 4));
            }
            throw new FormatException("MessagePack 0x" + c.ToString("x2"));
        }

        static byte[] Bin(byte[] b, ref int i, int n) { var o = new byte[n]; Array.Copy(b, i, o, 0, n); i += n; return o; }

        static string Str(byte[] b, ref int i, int n) { string s = Encoding.UTF8.GetString(b, i, n); i += n; return s; }

        static List<object> ReadArray(byte[] b, ref int i, int n) { var o = new List<object>(n); for (int k = 0; k < n; k++) o.Add(Next(b, ref i)); return o; }

        static Map ReadMap(byte[] b, ref int i, int n)
        {
            var o = new Map();
            for (int k = 0; k < n; k++) { var key = Next(b, ref i); o.Add(new KeyValuePair<object, object>(key, Next(b, ref i))); }
            return o;
        }

        public static byte[] Write(object v)
        {
            using (var ms = new MemoryStream()) { Put(ms, v); return ms.ToArray(); }
        }

        static void BeW(Stream s, long v, int n) { for (int k = n - 1; k >= 0; k--) s.WriteByte((byte)(v >> (8 * k))); }

        static void Put(Stream s, object v)
        {
            if (v == null) { s.WriteByte(0xc0); return; }
            if (v is bool) { s.WriteByte((bool)v ? (byte)0xc3 : (byte)0xc2); return; }
            if (v is float) { s.WriteByte(0xca); var x = BitConverter.GetBytes((float)v); if (BitConverter.IsLittleEndian) Array.Reverse(x); s.Write(x, 0, 4); return; }
            if (v is double) { s.WriteByte(0xcb); var x = BitConverter.GetBytes((double)v); if (BitConverter.IsLittleEndian) Array.Reverse(x); s.Write(x, 0, 8); return; }
            if (v is string)
            {
                var x = Encoding.UTF8.GetBytes((string)v);
                if (x.Length < 32) s.WriteByte((byte)(0xa0 | x.Length));
                else if (x.Length < 256) { s.WriteByte(0xd9); BeW(s, x.Length, 1); }
                else if (x.Length < 65536) { s.WriteByte(0xda); BeW(s, x.Length, 2); }
                else { s.WriteByte(0xdb); BeW(s, x.Length, 4); }
                s.Write(x, 0, x.Length);
                return;
            }
            if (v is byte[])
            {
                var x = (byte[])v;
                if (x.Length < 256) { s.WriteByte(0xc4); BeW(s, x.Length, 1); }
                else if (x.Length < 65536) { s.WriteByte(0xc5); BeW(s, x.Length, 2); }
                else { s.WriteByte(0xc6); BeW(s, x.Length, 4); }
                s.Write(x, 0, x.Length);
                return;
            }
            if (v is Ext)
            {
                var e = (Ext)v;
                s.WriteByte(0xc9); BeW(s, e.Data.Length, 4); s.WriteByte((byte)e.Type); s.Write(e.Data, 0, e.Data.Length);
                return;
            }
            if (v is Map)
            {
                var m = (Map)v;
                if (m.Count < 16) s.WriteByte((byte)(0x80 | m.Count));
                else if (m.Count < 65536) { s.WriteByte(0xde); BeW(s, m.Count, 2); }
                else { s.WriteByte(0xdf); BeW(s, m.Count, 4); }
                foreach (var kv in m) { Put(s, kv.Key); Put(s, kv.Value); }
                return;
            }
            if (v is IList)
            {
                var l = (IList)v;
                if (l.Count < 16) s.WriteByte((byte)(0x90 | l.Count));
                else if (l.Count < 65536) { s.WriteByte(0xdc); BeW(s, l.Count, 2); }
                else { s.WriteByte(0xdd); BeW(s, l.Count, 4); }
                foreach (var x in l) Put(s, x);
                return;
            }
            long n = Convert.ToInt64(v);
            if (n >= 0 && n <= 0x7f) s.WriteByte((byte)n);
            else if (n < 0 && n >= -32) s.WriteByte((byte)(sbyte)n);
            else if (n >= 0 && n <= 0xff) { s.WriteByte(0xcc); BeW(s, n, 1); }
            else if (n >= 0 && n <= 0xffff) { s.WriteByte(0xcd); BeW(s, n, 2); }
            else if (n >= 0 && n <= 0xffffffffL) { s.WriteByte(0xce); BeW(s, n, 4); }
            else if (n >= sbyte.MinValue && n < 0) { s.WriteByte(0xd0); BeW(s, n, 1); }
            else if (n >= short.MinValue && n < 0) { s.WriteByte(0xd1); BeW(s, n, 2); }
            else if (n >= int.MinValue && n < 0) { s.WriteByte(0xd2); BeW(s, n, 4); }
            else { s.WriteByte(0xd3); BeW(s, n, 8); }
        }
    }
}
