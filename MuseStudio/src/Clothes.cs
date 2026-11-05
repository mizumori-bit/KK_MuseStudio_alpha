using System.Collections.Generic;

namespace MuseStudio
{
    // Her clothes on or off (the trial run of 2026-11-03: there was no way to undress her, nor for her to undress): each
    // garment she has on (the outfit's slots, co_top ... co_shoes) is on / half (opened, shifted: the original's state 1) /
    // off (3). The user changes them from the panel's 服; Muse from a block's clothes ({"co_top": "off", "all": "off"}).
    // Told as clothes_changed (by user / muse) and in the state's clothes. A play undresses as the original's position does
    // and puts back what she wore before it when it ends (the original after H).
    public static class Clothes
    {
        public static readonly string[] Keys = CharaSummary.Slots;
        public static readonly string[] Names = { "上着", "下", "ブラ", "ショーツ", "手袋", "パンスト", "靴下", "靴" };
        public static readonly string[] Values = { "on", "half", "off" };
        public class AccHold { public Dictionary<int, bool> Off; }
        static readonly AccHold herAcc = new AccHold();     // her accessories taken off with "all" (put back with "all" on)

        public static void Forget() { herAcc.Off = null; }     // (another character)

        static string Of(byte b) { return b == 0 ? "on" : b >= 3 ? "off" : "half"; }

        static byte To(string v) { return v == "on" ? (byte)0 : v == "half" ? (byte)1 : (byte)3; }

        // the slots she has something on
        public static List<int> Worn(ChaControl cha)
        {
            var o = new List<int>();
            if (cha == null) return o;
            var parts = cha.nowCoordinate.clothes.parts;
            for (int k = 0; k < Keys.Length && k < parts.Length; k++)
                if (parts[k].id > 0 || (k == 7 && parts.Length > 8 && parts[8].id > 0)) o.Add(k);
            return o;
        }

        public static Dictionary<string, object> Info(ChaControl cha)
        {
            var o = new Dictionary<string, object>();
            if (cha == null) return o;
            foreach (int k in Worn(cha)) o[Keys[k]] = Of(cha.fileStatus.clothesState[k]);
            return o;
        }

        public static string State(ChaControl cha, int k) { return Of(cha.fileStatus.clothesState[k]); }

        static void Put(ChaControl cha, int k, string v)
        {
            cha.SetClothesState(k, To(v), false);
            if (k == 7 && cha.fileStatus.clothesState.Length > 8) cha.SetClothesState(8, To(v), false);     // (the outdoor shoes with the indoor)
        }

        // a change: {slot or "all": on / half / off}; returns what changed ("" keys unknown are left)
        public static List<object> Apply(ChaControl cha, Dictionary<string, object> want, List<string> unknown, AccHold acc = null)
        {
            acc = acc ?? herAcc;
            var changed = new List<object>();
            if (cha == null || want == null) return changed;
            var worn = Worn(cha);
            foreach (var kv in want)
            {
                string v = kv.Value as string;
                if (System.Array.IndexOf(Values, v) < 0) { unknown.Add(kv.Key); continue; }
                if (kv.Key == "all")
                {
                    foreach (int k in worn) if (State(cha, k) != v) { Put(cha, k, v); changed.Add(Keys[k]); }
                    // all off: her accessories too but the hair's (as in the bath); all on: they come back
                    if (v == "off" && acc.Off == null) acc.Off = BathAccessories.Off(cha);
                    else if (v == "on" && acc.Off != null) { BathAccessories.Back(cha, acc.Off); acc.Off = null; }
                    continue;
                }
                int i = System.Array.IndexOf(Keys, kv.Key);
                if (i < 0 || !worn.Contains(i)) { unknown.Add(kv.Key); continue; }
                if (State(cha, i) != v) { Put(cha, i, v); changed.Add(kv.Key); }
            }
            return changed;
        }

        // the panel's button: on -> half -> off -> on
        public static string Next(string v) { return v == "on" ? "half" : v == "half" ? "off" : "on"; }
    }
}
