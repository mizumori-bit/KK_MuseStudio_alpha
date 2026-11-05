using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // Who does what in a play (the Godot app's play_catalog.gd resolve, MUSE_GUIDE.md 3.7): the roles (giver / receiver; a
    // character alone: self, the user watching), the parts each side uses (a part with a need: breasts / vagina a female
    // body, penis a male body or the futanari attribute; the insertion alone puts a strapon on the side without one, and that
    // side feels strapon_feel of its rate), the strapon's wearer, the rates, the lead. Reversed: the giver changes sides.
    public class Resolved
    {
        public bool Ok = true;
        public string Reason = "";
        public Dictionary<string, string> Roles = new Dictionary<string, string>();
        public Dictionary<string, List<string>> Parts = new Dictionary<string, List<string>> { { "character", new List<string>() }, { "player", new List<string>() } };
        public string Strapon = "";
        public Dictionary<string, float> Rates = new Dictionary<string, float> { { "character", 0f }, { "player", 0f } };
        public string Lead = "";

        public static Resolved Of(Dictionary<string, object> doc, Dictionary<string, object> row, bool reversed, Dictionary<string, Dictionary<string, object>> who,
                                  bool anyRoles = false)
        {
            var g = Json.Obj(Json.Obj(doc, "groups"), Json.Str(row, "group", ""));
            string giver = Json.Str(row, "giver", null) ?? Json.Str(g, "giver", "player");     // (a play's own: the AL service plays he gives)
            bool self = Flag(g, "self");
            bool reversible = row.ContainsKey("reversible") ? Flag(row, "reversible") : Flag(g, "reversible");
            if (reversed && (!(reversible || anyRoles) || self)) return new Resolved { Ok = false, Reason = "攻守交代できないプレイです" };
            if (reversed) giver = giver == "player" ? "character" : "player";
            string receiver = giver == "player" ? "character" : "player";
            var targets = Json.Obj(row, "targets") ?? Json.Obj(g, "targets");
            var parts = Json.Obj(row, "parts") ?? new Dictionary<string, object>();
            bool straponOk = row.ContainsKey("strapon") ? Flag(row, "strapon") : Flag(g, "strapon");
            var o = new Resolved();
            var roleOf = self ? new Dictionary<string, string> { { "character", "self" }, { "player", "watch" } }
                              : new Dictionary<string, string> { { giver, "giver" }, { receiver, "receiver" } };
            o.Roles = Flag(row, "mutual") ? new Dictionary<string, string> { { "character", "mutual" }, { "player", "mutual" } } : new Dictionary<string, string>(roleOf);
            var reasons = new List<string>();
            foreach (var side in new[] { "character", "player" })
            {
                string role = roleOf[side];
                if (role == "watch") continue;
                var mine = Json.Arr(parts, role == "self" ? "giver" : role) ?? new List<object>();
                var got = new List<string>();
                float feel = 1f;
                foreach (var po in mine)
                {
                    string key = (string)po;
                    var spec = Json.Obj(Json.Obj(doc, "parts"), key);
                    var res = Json.Obj(spec, "resolve");
                    if (res != null) key = Json.Str(res, Json.Str(who[side], "body", "female"), key);
                    string need = Json.Str(spec, "needs", "");
                    if (need.Length > 0)
                    {
                        string has = HasNeed(doc, who[side], need, straponOk);
                        if (has == "no") reasons.Add((side == "character" ? "キャラクター" : "あなた") + "に" + NeedText(doc, need, straponOk) + "が必要");
                        else if (has == "strapon")
                        {
                            o.Strapon = side;
                            feel = (float)Json.Num(Json.Obj(Json.Obj(doc, "needs"), need), "strapon_feel", 0.3);
                        }
                    }
                    got.Add(key);
                }
                o.Parts[side] = got;
                o.Rates[side] = (float)Json.Num(targets, role == "self" ? "giver" : role, 0) * feel;
            }
            if (reasons.Count > 0) { o.Ok = false; o.Reason = string.Join("、", reasons.ToArray()); }
            string lead = Json.Str(row, "lead", "");
            o.Lead = lead == "giver" ? giver : lead == "receiver" ? receiver : self ? "character" : giver;
            return o;
        }

        static bool Flag(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v is bool && (bool)v; }

        // "" = has it naturally, "strapon" = a strapon (where the group allows one), "no" = cannot
        static string HasNeed(Dictionary<string, object> doc, Dictionary<string, object> who, string need, bool straponOk)
        {
            var n = Json.Obj(Json.Obj(doc, "needs"), need);
            if (n == null) return "";
            if ((Json.Arr(n, "bodies") ?? new List<object>()).Contains(Json.Str(who, "body", "female"))) return "";
            foreach (var a in Json.Arr(n, "attributes") ?? new List<object>()) if (Flag(who, (string)a)) return "";
            if (straponOk && Flag(n, "strapon")) return "strapon";
            return "no";
        }

        static string NeedText(Dictionary<string, object> doc, string need, bool straponOk)
        {
            var n = Json.Obj(Json.Obj(doc, "needs"), need);
            var ways = new List<string>();
            foreach (var b in Json.Arr(n, "bodies") ?? new List<object>()) ways.Add((string)b == "male" ? "男性の体" : (string)b == "female" ? "女性の体" : (string)b);
            foreach (var a in Json.Arr(n, "attributes") ?? new List<object>()) ways.Add((string)a == "futanari" ? "ふたなり" : (string)a);
            if (straponOk && Flag(n, "strapon")) ways.Add("ペニバン");
            return Json.Str(n, "name", need) + "（" + string.Join("・", ways.ToArray()) + "）";
        }
    }

    // The strapon: a mod clothing item (the Godot app's mod_strapon, MuseStudio's config "Strapon": guid|category|slot) in
    // the shorts' slot of whoever wears one, shown while it is on (the position's undressing leaves it on); the slot's own
    // item and state come back after the play.
    public static class Strapon
    {
        class Worn { public int Id; public byte State; }
        static readonly Dictionary<ChaControl, Worn> before = new Dictionary<ChaControl, Worn>();
        static int resolved = int.MinValue;

        // Sideloader's id of the item in this game (-1: not found)
        public static int Id()
        {
            if (resolved != int.MinValue) return resolved;
            resolved = -1;
            var spec = MuseStudioPlugin.Instance.StraponItem.Value.Split('|');
            int cat, slot;
            if (spec.Length != 3 || !int.TryParse(spec[1], out cat) || !int.TryParse(spec[2], out slot)) return resolved;
            try
            {
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    if ((t = asm.GetType("Sideloader.AutoResolver.UniversalAutoResolver")) != null) break;
                if (t == null) { MuseStudioPlugin.Log.LogWarning("MuseStudio: strapon: no Sideloader"); return resolved; }
                var list = Member(null, t, "LoadedResolutionInfo") as IEnumerable;
                if (list == null) return resolved;
                foreach (var ri in list)
                {
                    if (!string.Equals(Convert.ToString(Member(ri, ri.GetType(), "GUID")), spec[0], StringComparison.OrdinalIgnoreCase)) continue;
                    if (Convert.ToInt32(Member(ri, ri.GetType(), "CategoryNo")) != cat || Convert.ToInt32(Member(ri, ri.GetType(), "Slot")) != slot) continue;
                    resolved = Convert.ToInt32(Member(ri, ri.GetType(), "LocalSlot"));
                    break;
                }
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: strapon: " + e.Message); }
            MuseStudioPlugin.Log.LogInfo("MuseStudio: strapon " + MuseStudioPlugin.Instance.StraponItem.Value + " -> id " + resolved);
            return resolved;
        }

        static object Member(object o, Type t, string name)
        {
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
            var p = t.GetProperty(name, F);
            if (p != null) return p.GetValue(o, null);
            var f = t.GetField(name, F);
            return f != null ? f.GetValue(o) : null;
        }

        public static bool Put(ChaControl cha)
        {
            int id = Id();
            if (cha == null || id < 0) return false;
            if (!before.ContainsKey(cha)) before[cha] = new Worn { Id = cha.nowCoordinate.clothes.parts[3].id, State = cha.fileStatus.clothesState[3] };
            cha.nowCoordinate.clothes.parts[3].id = id;
            cha.ChangeClothesShorts(id, true);
            cha.SetClothesState(3, 0, true);
            return true;
        }

        public static bool On(ChaControl cha) { return cha != null && before.ContainsKey(cha); }

        public static void Off(ChaControl cha)
        {
            Worn w;
            if (cha == null || !before.TryGetValue(cha, out w)) return;
            before.Remove(cha);
            cha.nowCoordinate.clothes.parts[3].id = w.Id;
            cha.ChangeClothesShorts(w.Id, true);
            cha.SetClothesState(3, w.State, true);
        }

        public static void Forget(ChaControl cha) { if (cha != null) before.Remove(cha); }
    }
}
