using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // What a character is like, for Muse (MUSE_GUIDE.md 7.3: the creation summary "creation-summary/1", the state's outfit, the
    // creation's changes), read from the card with the original's own lists (item names) and slider names (ChaFileDefine):
    // the Godot app's character_summary.gd and creation_changes.gd on the original's data. Colours as plain words.
    public static class CharaSummary
    {
        public const string Schema = "creation-summary/1";
        // the main body sliders: key -> index (cf_bodyshapename), low word, high word
        static readonly object[][] BodyKeys = {
            new object[] { "height", 0, "低い", "高い" }, new object[] { "head", 1, "小さい", "大きい" }, new object[] { "bust", 4, "小さい", "大きい" },
            new object[] { "shoulders", 37, "狭い", "広い" }, new object[] { "waist", 18, "細い", "太い" }, new object[] { "hips", 26, "小さい", "大きい" },
            new object[] { "thighs", 28, "細い", "太い" } };
        const float NotableFrom = 0.25f;
        // the worn slots Muse reads (the coordinate's parts by index; the outdoor shoes left out)
        public static readonly string[] Slots = { "co_top", "co_bot", "co_bra", "co_shorts", "co_gloves", "co_panst", "co_socks", "co_shoes" };
        static readonly string[] SlotNames = { "トップス", "ボトムス", "ブラ", "ショーツ", "手袋", "パンスト", "靴下", "靴" };
        static readonly string[] HairKeys = { "back", "front", "side", "extension" };
        static readonly string[] HairNames = { "後ろ髪", "前髪", "横髪", "エクステ" };
        static readonly string[] AccTypes = { "なし", "髪", "頭", "顔", "首", "胴", "腰", "脚", "腕", "手", "股間" };

        static string ItemName(int cat, int id)
        {
            try
            {
                var li = Singleton<Manager.Character>.Instance.chaListCtrl.GetListInfo((ChaListDefine.CategoryNo)cat, id);
                return li != null ? li.Name : null;
            }
            catch (Exception) { return null; }
        }

        public static string PersonalityName(int no)
        {
            try
            {
                VoiceInfo.Param p;
                if (Singleton<Manager.Voice>.Instance.voiceInfoDic.TryGetValue(no, out p)) return p.Personality;
            }
            catch (Exception) { }
            return "";
        }

        static string Level(float v, string low, string high)
        {
            if (v < 0.2f) return "とても" + low;
            if (v < 0.4f) return "やや" + low;
            if (v <= 0.6f) return "普通";
            if (v <= 0.8f) return "やや" + high;
            return "とても" + high;
        }

        // a plain colour word (hue buckets with light / dark)
        public static string ColorName(Color c)
        {
            float h, s, v;
            Color.RGBToHSV(c, out h, out s, out v);
            if (v < 0.16f) return "黒";
            string tone = v > 0.8f ? "明るい" : v < 0.4f ? "暗い" : "";
            if (s < 0.15f) return v > 0.85f ? "白" : tone + (v > 0.6f ? "銀" : "灰色");
            float d = h * 360f;
            string name = "赤";
            if (d >= 15 && d < 40) name = v < 0.65f ? "茶" : "オレンジ";
            else if (d >= 40 && d < 70) name = s < 0.75f ? "金" : "黄";
            else if (d >= 70 && d < 165) name = "緑";
            else if (d >= 165 && d < 200) name = "水色";
            else if (d >= 200 && d < 255) name = "青";
            else if (d >= 255 && d < 300) name = "紫";
            else if (d >= 300 && d < 345) name = "ピンク";
            return tone + name;
        }

        public static string Hex(Color c) { return "#" + ColorUtility.ToHtmlStringRGB(c).ToLowerInvariant(); }

        static Dictionary<string, object> HexName(Color c) { return new Dictionary<string, object> { { "hex", Hex(c) }, { "name", ColorName(c) } }; }

        static List<object> Notable(float[] values, string[] names)
        {
            var o = new List<object>();
            for (int i = 0; i < values.Length; i++)
                if (Mathf.Abs(values[i] - 0.5f) >= NotableFrom)
                    o.Add(new Dictionary<string, object> { { "name", i < names.Length ? names[i] : i.ToString() }, { "value", Math.Round(values[i], 2) },
                        { "direction", values[i] > 0.5f ? "大きめ" : "小さめ" } });
            return o;
        }

        static ChaFileCoordinate Coord(ChaFileControl f) { return f.coordinate[Mathf.Clamp(f.status.coordinateType, 0, f.coordinate.Length - 1)]; }

        // the colours an item uses (1..3)
        static List<object> ItemColors(int cat, ChaFileClothes.PartsInfo p)
        {
            var o = new List<object>();
            int n = 3;
            try
            {
                var li = Singleton<Manager.Character>.Instance.chaListCtrl.GetListInfo((ChaListDefine.CategoryNo)cat, p.id);
                if (li != null)
                {
                    // (an approximation: the main colour, and one more for each colour mask the item's material has)
                    n = 1;
                    foreach (var k in new[] { ChaListDefine.KeyType.ColorMaskTex, ChaListDefine.KeyType.ColorMask02Tex })
                    {
                        string v = li.GetInfo(k);
                        if (!string.IsNullOrEmpty(v) && v != "0") n++;
                    }
                }
            }
            catch (Exception) { }
            for (int i = 0; i < n && i < p.colorInfo.Length; i++) o.Add(ColorName(p.colorInfo[i].baseColor));
            return o;
        }

        public static Dictionary<string, object> Outfit(ChaFileControl f)
        {
            var o = new Dictionary<string, object>();
            var parts = Coord(f).clothes.parts;
            for (int i = 0; i < Slots.Length && i < parts.Length; i++)
                o[Slots[i]] = new Dictionary<string, object> { { "id", parts[i].id }, { "name", ItemName(105 + Mathf.Min(i, 7), parts[i].id) ?? "" } };
            return o;
        }

        // what she has on now (the body's coordinate: what the play put on - the strapon - included)
        public static Dictionary<string, object> OutfitNow(ChaControl cha)
        {
            var o = new Dictionary<string, object>();
            var parts = cha.nowCoordinate.clothes.parts;
            for (int i = 0; i < Slots.Length && i < parts.Length; i++)
                o[Slots[i]] = new Dictionary<string, object> { { "id", parts[i].id }, { "name", ItemName(105 + Mathf.Min(i, 7), parts[i].id) ?? "" } };
            return o;
        }

        public static Dictionary<string, string> OutfitNames(ChaControl cha)
        {
            var o = new Dictionary<string, string>();
            foreach (var kv in OutfitNow(cha)) o[kv.Key] = Json.Str(kv.Value, "name", "");
            return o;
        }

        static Dictionary<string, object> Part(int cat, int id, Color? color, Color? color2 = null)
        {
            if (id <= 0 && cat != 407 && cat != 406 && cat != 408 && cat != 414) return null;     // nothing on (a makeup's 0)
            var d = new Dictionary<string, object> { { "name", ItemName(cat, id) ?? id.ToString() } };
            if (color.HasValue) d["color"] = ColorName(color.Value);
            if (color2.HasValue) d["color2"] = ColorName(color2.Value);
            return d;
        }

        public static Dictionary<string, object> Build(ChaFileControl f, bool futanari)
        {
            var body = f.custom.body.shapeValueBody;
            var b = new Dictionary<string, object>();
            foreach (var k in BodyKeys)
            {
                float v = body[(int)k[1]];
                b[(string)k[0]] = new Dictionary<string, object> { { "value", Math.Round(v, 2) }, { "level", Level(v, (string)k[2], (string)k[3]) } };
            }
            b["notable"] = Notable(body, ChaFileDefine.cf_bodyshapename);
            var hair = new Dictionary<string, object>();
            var hp = f.custom.hair.parts;
            for (int i = 0; i < 4 && i < hp.Length; i++) hair[HairKeys[i]] = hp[i].id > 0 || i == 0 ? ItemName(101 + i, hp[i].id) : null;
            if (hp.Length > 0) hair["color"] = HexName(hp[0].baseColor);
            var face = f.custom.face;
            var outfitColors = new Dictionary<string, object>();
            var parts = Coord(f).clothes.parts;
            for (int i = 0; i < Slots.Length && i < parts.Length; i++) if (parts[i].id > 0) outfitColors[Slots[i]] = ItemColors(105 + Mathf.Min(i, 7), parts[i]);
            var outfit = new Dictionary<string, object>();
            foreach (var kv in Outfit(f)) outfit[kv.Key] = Json.Str(kv.Value, "name", "");
            var mk = face.baseMakeup;
            var acc = new List<object>();
            var ap = Coord(f).accessory.parts;
            for (int i = 0; i < ap.Length; i++)
            {
                if (ap[i].type <= 120) continue;
                var cols = new List<object>();
                for (int k = 0; k < 2 && k < ap[i].color.Length; k++) cols.Add(ColorName(ap[i].color[k]));
                acc.Add(new Dictionary<string, object> { { "slot", i + 1 }, { "type", AccTypes[Mathf.Clamp(ap[i].type - 120, 0, AccTypes.Length - 1)] },
                    { "item", ItemName(ap[i].type, ap[i].id) ?? ap[i].id.ToString() }, { "parent", ParentName(ap[i].parentKey) }, { "colors", cols } });
            }
            int pers = f.parameter.personality;
            return new Dictionary<string, object> {
                { "schema", Schema }, { "name", f.parameter.fullname.Trim() },
                { "personality", new Dictionary<string, object> { { "no", pers }, { "name", PersonalityName(pers) } } },
                { "attributes", new Dictionary<string, object> { { "futanari", futanari } } },
                { "body", b }, { "face", new Dictionary<string, object> { { "notable", Notable(face.shapeValueFace, ChaFileDefine.cf_headshapename) } } },
                { "hair", hair }, { "skin", HexName(f.custom.body.skinMainColor) }, { "outfit", outfit }, { "outfit_colors", outfitColors },
                { "face_parts", new Dictionary<string, object> {
                    { "eyebrow", Part(406, face.eyebrowId, face.eyebrowColor) },
                    { "eyes", Part(408, face.pupil[0].id, face.pupil[0].baseColor, face.pupil[0].subColor) },
                    { "eye_white", Part(407, face.whiteId, null) },
                    { "eyeline_upper", Part(412, face.eyelineUpId, face.eyelineColor) },
                    { "eyeline_lower", Part(413, face.eyelineDownId, face.eyelineColor) },
                    { "nose", Part(414, face.noseId, null) } } },
                { "makeup", new Dictionary<string, object> {
                    { "eyeshadow", Part(401, mk.eyeshadowId, mk.eyeshadowColor) }, { "cheek", Part(402, mk.cheekId, mk.cheekColor) },
                    { "lip", Part(403, mk.lipId, mk.lipColor) }, { "lipline", Part(404, face.lipLineId, face.lipLineColor) },
                    { "mole", Part(415, face.moleId, face.moleColor) },
                    { "paint1", Part(405, mk.paintId[0], mk.paintColor[0]) }, { "paint2", Part(405, mk.paintId[1], mk.paintColor[1]) } } },
                { "nails", HexName(f.custom.body.nailColor) }, { "accessories", acc } };
        }

        static string ParentName(string key)
        {
            try
            {
                int i = (int)Enum.Parse(typeof(ChaAccessoryDefine.AccessoryParentKey), key);
                return i < ChaAccessoryDefine.AccessoryParentName.Length ? ChaAccessoryDefine.AccessoryParentName[i] : key;
            }
            catch (Exception) { return key ?? ""; }
        }

        // ---- the changes between two cards (the creation's "changed": what, from, to - in words / numbers / #RRGGBB) ----

        public static List<object> Changes(ChaFileControl a, ChaFileControl b, bool futA, bool futB)
        {
            var o = new List<object>();
            Action<string, string, string, string> add = (g, item, from, to) => { if (from != to) o.Add(new Dictionary<string, object> { { "group", g }, { "item", item }, { "from", from }, { "to", to } }); };
            add("プロフィール", "名前", a.parameter.fullname.Trim(), b.parameter.fullname.Trim());
            add("プロフィール", "性格", PersonalityName(a.parameter.personality), PersonalityName(b.parameter.personality));
            add("プロフィール", "ふたなり", futA ? "あり" : "なし", futB ? "あり" : "なし");
            Sliders(o, "体型", a.custom.body.shapeValueBody, b.custom.body.shapeValueBody, ChaFileDefine.cf_bodyshapename);
            Sliders(o, "顔", a.custom.face.shapeValueFace, b.custom.face.shapeValueFace, ChaFileDefine.cf_headshapename);
            var ha = a.custom.hair.parts; var hb = b.custom.hair.parts;
            for (int i = 0; i < 4 && i < ha.Length && i < hb.Length; i++)
            {
                add("髪", HairNames[i], ItemName(101 + i, ha[i].id) ?? "なし", ItemName(101 + i, hb[i].id) ?? "なし");
                add("髪", HairNames[i] + "の色", Hex(ha[i].baseColor), Hex(hb[i].baseColor));
            }
            var ca = Coord(a).clothes.parts; var cb = Coord(b).clothes.parts;
            for (int i = 0; i < Slots.Length && i < ca.Length && i < cb.Length; i++)
            {
                int cat = 105 + Mathf.Min(i, 7);
                add("服", SlotNames[i], ca[i].id > 0 ? ItemName(cat, ca[i].id) ?? ca[i].id.ToString() : "なし", cb[i].id > 0 ? ItemName(cat, cb[i].id) ?? cb[i].id.ToString() : "なし");
                for (int k = 0; k < 3 && k < ca[i].colorInfo.Length && k < cb[i].colorInfo.Length; k++)
                    if (cb[i].id > 0) add("服の色", SlotNames[i] + "の色" + (k + 1), Hex(ca[i].colorInfo[k].baseColor), Hex(cb[i].colorInfo[k].baseColor));
            }
            // the top's sailor / blazer parts (the top of kind 1 sailor: categories 200..202, 2 blazer: 210..212)
            if (ca.Length > 0 && cb.Length > 0 && (cb[0].id == 1 || cb[0].id == 2))
            {
                string[] pn = { "上", "下", "飾り" };
                int baseCat = cb[0].id == 1 ? 200 : 210;
                string kind = cb[0].id == 1 ? "セーラー" : "ブレザー";
                for (int k = 0; k < 3 && k < Coord(b).clothes.subPartsId.Length; k++)
                {
                    string from = ca[0].id == cb[0].id && k < Coord(a).clothes.subPartsId.Length ? ItemName(baseCat + k, Coord(a).clothes.subPartsId[k]) ?? Coord(a).clothes.subPartsId[k].ToString() : "なし";
                    add("服", kind + "の" + pn[k], from, ItemName(baseCat + k, Coord(b).clothes.subPartsId[k]) ?? Coord(b).clothes.subPartsId[k].ToString());
                }
            }
            var aa = Coord(a).accessory.parts; var ab = Coord(b).accessory.parts;
            for (int i = 0; i < aa.Length && i < ab.Length; i++)
            {
                add("アクセサリ", "スロット" + (i + 1), AccText(aa[i]), AccText(ab[i]));
                if (ab[i].type <= 120) continue;
                add("アクセサリ", "スロット" + (i + 1) + "の付け場所", aa[i].type <= 120 ? "" : ParentName(aa[i].parentKey), ParentName(ab[i].parentKey));
                for (int k = 0; k < 3 && k < aa[i].color.Length && k < ab[i].color.Length; k++)
                    add("アクセサリの色", "スロット" + (i + 1) + "の色" + (k + 1), Hex(aa[i].color[k]), Hex(ab[i].color[k]));
                // the position, the angle, the size (its first adjustment: addMove[0, 0..2])
                string[] mv = { "位置", "向き", "大きさ" };
                for (int k = 0; k < 3; k++) add("アクセサリの位置", "スロット" + (i + 1) + "の" + mv[k], Vec(aa[i].addMove[0, k]), Vec(ab[i].addMove[0, k]));
            }
            var fa = a.custom.face; var fb = b.custom.face;
            add("目・眉", "眉", ItemName(406, fa.eyebrowId) ?? "", ItemName(406, fb.eyebrowId) ?? "");
            add("目・眉", "眉の色", Hex(fa.eyebrowColor), Hex(fb.eyebrowColor));
            add("目・眉", "瞳", ItemName(408, fa.pupil[0].id) ?? "", ItemName(408, fb.pupil[0].id) ?? "");
            add("目・眉", "瞳の色", Hex(fa.pupil[0].baseColor), Hex(fb.pupil[0].baseColor));
            add("目・眉", "白目", ItemName(407, fa.whiteId) ?? "", ItemName(407, fb.whiteId) ?? "");
            add("目・眉", "アイライン上", ItemName(412, fa.eyelineUpId) ?? "なし", ItemName(412, fb.eyelineUpId) ?? "なし");
            add("目・眉", "アイライン下", ItemName(413, fa.eyelineDownId) ?? "なし", ItemName(413, fb.eyelineDownId) ?? "なし");
            add("目・眉", "鼻", ItemName(414, fa.noseId) ?? "", ItemName(414, fb.noseId) ?? "");
            add("目・眉", "ハイライト上", ItemName(410, fa.hlUpId) ?? "なし", ItemName(410, fb.hlUpId) ?? "なし");
            add("目・眉", "ハイライト上の色", Hex(fa.hlUpColor), Hex(fb.hlUpColor));
            add("目・眉", "ハイライト下", ItemName(411, fa.hlDownId) ?? "なし", ItemName(411, fb.hlDownId) ?? "なし");
            add("目・眉", "ハイライト下の色", Hex(fa.hlDownColor), Hex(fb.hlDownColor));
            var ma = fa.baseMakeup; var mb = fb.baseMakeup;
            add("メイク・肌", "アイシャドウ", ItemName(401, ma.eyeshadowId) ?? "なし", ItemName(401, mb.eyeshadowId) ?? "なし");
            add("メイク・肌", "アイシャドウの色", Hex(ma.eyeshadowColor), Hex(mb.eyeshadowColor));
            add("メイク・肌", "チーク", ItemName(402, ma.cheekId) ?? "なし", ItemName(402, mb.cheekId) ?? "なし");
            add("メイク・肌", "チークの色", Hex(ma.cheekColor), Hex(mb.cheekColor));
            add("メイク・肌", "口紅", ItemName(403, ma.lipId) ?? "なし", ItemName(403, mb.lipId) ?? "なし");
            add("メイク・肌", "口紅の色", Hex(ma.lipColor), Hex(mb.lipColor));
            add("メイク・肌", "リップライン", ItemName(404, fa.lipLineId) ?? "なし", ItemName(404, fb.lipLineId) ?? "なし");
            add("メイク・肌", "リップラインの色", Hex(fa.lipLineColor), Hex(fb.lipLineColor));
            add("メイク・肌", "ほくろ", ItemName(415, fa.moleId) ?? "なし", ItemName(415, fb.moleId) ?? "なし");
            add("メイク・肌", "ほくろの色", Hex(fa.moleColor), Hex(fb.moleColor));
            add("メイク・肌", "ほくろの位置", Vec4(fa.moleLayout), Vec4(fb.moleLayout));
            for (int k = 0; k < 2 && k < ma.paintId.Length && k < mb.paintId.Length; k++)
            {
                add("メイク・肌", "フェイスペイント" + (k + 1), ItemName(405, ma.paintId[k]) ?? "なし", ItemName(405, mb.paintId[k]) ?? "なし");
                add("メイク・肌", "フェイスペイント" + (k + 1) + "の色", Hex(ma.paintColor[k]), Hex(mb.paintColor[k]));
            }
            var ba = a.custom.body; var bb = b.custom.body;
            for (int k = 0; k < 2 && k < ba.paintId.Length && k < bb.paintId.Length; k++)
            {
                add("メイク・肌", "ボディペイント" + (k + 1), ItemName(421, ba.paintId[k]) ?? "なし", ItemName(421, bb.paintId[k]) ?? "なし");
                add("メイク・肌", "ボディペイント" + (k + 1) + "の色", Hex(ba.paintColor[k]), Hex(bb.paintColor[k]));
            }
            add("メイク・肌", "日焼け", ItemName(422, ba.sunburnId) ?? "なし", ItemName(422, bb.sunburnId) ?? "なし");
            add("体", "乳首", ItemName(423, ba.nipId) ?? "なし", ItemName(423, bb.nipId) ?? "なし");
            add("体", "乳首の色", Hex(ba.nipColor), Hex(bb.nipColor));
            add("体", "乳輪の大きさ", Mathf.RoundToInt(ba.areolaSize * 100f).ToString(), Mathf.RoundToInt(bb.areolaSize * 100f).ToString());
            add("体", "アンダーヘア", ItemName(424, ba.underhairId) ?? "なし", ItemName(424, bb.underhairId) ?? "なし");
            add("体", "アンダーヘアの色", Hex(ba.underhairColor), Hex(bb.underhairColor));
            add("メイク・肌", "肌の色", Hex(a.custom.body.skinMainColor), Hex(b.custom.body.skinMainColor));
            add("メイク・肌", "爪の色", Hex(a.custom.body.nailColor), Hex(b.custom.body.nailColor));
            return o;
        }

        static void Sliders(List<object> o, string group, float[] a, float[] b, string[] names)
        {
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                int va = Mathf.RoundToInt(a[i] * 100f), vb = Mathf.RoundToInt(b[i] * 100f);
                if (va != vb) o.Add(new Dictionary<string, object> { { "group", group }, { "item", i < names.Length ? names[i] : i.ToString() }, { "from", va.ToString() }, { "to", vb.ToString() } });
            }
        }

        static string Vec(Vector3 v) { return string.Format("{0:0.##}, {1:0.##}, {2:0.##}", v.x, v.y, v.z); }

        static string Vec4(Vector4 v) { return string.Format("{0:0.##}, {1:0.##}, {2:0.##}, {3:0.##}", v.x, v.y, v.z, v.w); }

        static string AccText(ChaFileAccessory.PartsInfo p)
        {
            if (p.type <= 120) return "なし";
            return AccTypes[Mathf.Clamp(p.type - 120, 0, AccTypes.Length - 1)] + "：" + (ItemName(p.type, p.id) ?? p.id.ToString());
        }
    }
}
