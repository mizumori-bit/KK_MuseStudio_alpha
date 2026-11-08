using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // Items and resources (PR #46 §7, handed over in PR #47; MUSE_GUIDE 3.11). The definitions are data/items.json (what
    // there is: an item is never made by a use or a purchase - only the stock of a defined one moves); the budget's reward is
    // data/rewards.json.
    //
    // The ledger - the budget, the part of an hour kept, the stock (the house's shelf, the user's own, each character's own),
    // the date of the last restock, the requests done - is kept in the moods' file ("_items", Mood) and written with it in one
    // step: a use that takes the stock and adds its effect to her moods is one write, never half of it.
    //
    //  - the budget: time alone (the owner's decision) - per_unit every unit_seconds the app runs, a part kept to the second
    //    over restarts. Shown on the user's screen only; Muse is told only when the user's setting says so (default off).
    //    Muse never buys: a purchase is the user's (his gift to her).
    //  - stock: the shelf is the house's (both use it); the user's and each character's own. Initial stock: the shelf's, and
    //    each one's own the same; the shelf filled a little when the date changes.
    //  - giving: an offer, done when the one given to takes it (a character's: Muse's block accepts; the user's: his
    //    button) - checked again then: the giver still has it, the taker's room for it, the same character.
    //  - using: the user suggests (an event: Muse decides); Muse uses by a block; the app never uses one by itself. On her:
    //    the effects flow into her moods over their minutes (Mood's pending) - a dose of the same mood within
    //    tolerance_minutes dulled, the grade's cap not passed. On the user: nothing in the numbers (his body is his own) - an
    //    event; the setting may forbid Muse such items (checked right before it is applied).
    //  - Muse's operations carry a request id: a resend - the same id - is done once (kept in the ledger: over restarts too).
    public class Items
    {
        readonly MuseStudioPlugin h;
        public readonly Dictionary<string, object> Def, Reward;
        Dictionary<string, object> doc = new Dictionary<string, object>();
        public bool Imported;                       // (the ledger read from the moods' file)
        readonly List<Offer> offers = new List<Offer>();
        readonly List<Suggestion> suggestions = new List<Suggestion>();
        int offerSerial;
        float nextDateCheck;
        public int RewardsTold;                     // (checks)
        public readonly List<string> Log = new List<string>();     // (checks)

        public class Offer { public string Id = "", Item = "", From = "", To = "", Char = ""; public float At; }
        public class Suggestion { public string Id = "", Item = "", From = "", Char = ""; public float At; }

        public Items(MuseStudioPlugin host)
        {
            h = host;
            Def = Data.Load("items.json");
            Reward = Data.Load("rewards.json");
        }

        Dictionary<string, object> ItemDefs { get { return Json.Obj(Def, "items") ?? new Dictionary<string, object>(); } }
        public Dictionary<string, object> ItemDef(string id) { return Json.Obj(ItemDefs, id ?? ""); }

        // ---- the ledger: read from / written into the moods' file ----
        public void Import(Dictionary<string, object> d)
        {
            doc = d != null ? new Dictionary<string, object>(d) : new Dictionary<string, object>();
            Imported = true;
            if (!Json.Has(doc, "started"))
            {
                // the first start: the budget's start, the shelf's and the user's initial stock (a character's when she comes)
                doc["started"] = DateTime.Now.ToString("yyyy-MM-dd");
                doc["budget"] = (double)Json.Num(Reward, "start", 0);
                doc["fraction"] = 0.0;
                doc["shelf"] = Counts(Json.Obj(Json.Obj(Def, "initial"), "shelf"));
                doc["user"] = Counts(Json.Obj(Json.Obj(Def, "initial"), "own"));
                doc["chars"] = new Dictionary<string, object>();
                doc["restocked"] = DateTime.Now.ToString("yyyy-MM-dd");
                doc["requests"] = new List<object>();
                doc["doses"] = new Dictionary<string, object>();
            }
        }

        public Dictionary<string, object> Export() { return doc; }

        static Dictionary<string, object> Counts(Dictionary<string, object> d)
        {
            var o = new Dictionary<string, object>();
            if (d != null) foreach (var kv in d) o[kv.Key] = (double)Convert.ToDouble(kv.Value);
            return o;
        }

        // written now - within a block's operation not yet: its one write comes with its request (FromBlock)
        bool inTx;
        List<Action> afterTx;
        void Persist() { if (!inTx) h.Mood.Persist(); }

        // what is told and shown of a change: at once, or - within a block's operation - once its write is done
        void After(Action a) { if (inTx && afterTx != null) afterTx.Add(a); else a(); }

        public int Budget { get { return (int)Json.Num(doc, "budget", 0); } }
        public double Fraction { get { return Json.Num(doc, "fraction", 0); } }

        // a holder's stock: "shelf", "user", or a character's id
        public Dictionary<string, object> Stock(string holder)
        {
            if (holder == "shelf" || holder == "user")
            {
                var s = Json.Obj(doc, holder);
                if (s == null) { s = new Dictionary<string, object>(); doc[holder] = s; }
                return s;
            }
            var chars = Json.Obj(doc, "chars");
            if (chars == null) { chars = new Dictionary<string, object>(); doc["chars"] = chars; }
            var c = Json.Obj(chars, holder);
            if (c == null)
            {
                c = Counts(Json.Obj(Json.Obj(Def, "initial"), "own"));      // (a character's first time: the same as the user's)
                chars[holder] = c;
            }
            return c;
        }

        public int Count(string holder, string item) { return (int)Json.Num(Stock(holder), item, 0); }

        string Her { get { return h.CharacterId(); } }

        void Change(string holder, string item, int by)
        {
            var s = Stock(holder);
            int n = Mathf.Max(0, (int)Json.Num(s, item, 0) + by);
            if (n == 0) s.Remove(item); else s[item] = (double)n;
        }

        int Max(string item) { return (int)Json.Num(ItemDef(item), "max", 99); }

        // ---- the budget and the shelf's restock (every frame) ----
        public void Tick(float dt)
        {
            if (!Imported) return;
            float unit = Mathf.Max(1f, (float)Json.Num(Reward, "unit_seconds", 3600));
            double f = Fraction + Mathf.Min(dt, (float)Json.Num(Reward, "max_frame_seconds", 5));
            int units = 0;
            while (f >= unit) { f -= unit; units++; }
            doc["fraction"] = f;
            if (units > 0) AddBudget(units * (int)Json.Num(Reward, "per_unit", 100), "time");
            if (Time.realtimeSinceStartup >= nextDateCheck)
            {
                nextDateCheck = Time.realtimeSinceStartup + 30f;
                Restock(DateTime.Now);
            }
        }

        public void AddBudget(int amount, string cause)
        {
            doc["budget"] = (double)(Budget + amount);
            RewardsTold++;
            Persist();
            Log.Add("budget +" + amount + " (" + cause + ") = " + Budget);
            if (h.TellBudget != null && h.TellBudget.Value)
                h.Bridge.Event("budget_added", new Dictionary<string, object> { { "amount", amount }, { "balance", Budget } });
        }

        // the date changed: the shelf filled a little (up to each item's max) - once a date
        public void Restock(DateTime now)
        {
            string today = now.ToString("yyyy-MM-dd");
            if (Json.Str(doc, "restocked", "") == today) return;
            doc["restocked"] = today;
            var added = new Dictionary<string, object>();
            foreach (var kv in Json.Obj(Def, "daily") ?? new Dictionary<string, object>())
            {
                if (ItemDef(kv.Key) == null) continue;
                int n = Mathf.Min(Convert.ToInt32(kv.Value), Max(kv.Key) - Count("shelf", kv.Key));
                if (n <= 0) continue;
                Change("shelf", kv.Key, n);
                added[kv.Key] = n;
            }
            Persist();
            if (added.Count > 0) h.Bridge.Event("items_restocked", new Dictionary<string, object> { { "items", added }, { "shelf", ShelfView() } });
        }

        // ---- the user's own: buying, the shelf ----
        public string Buy(string item, int n = 1)
        {
            var d = ItemDef(item);
            if (d == null) return "unknown_item";
            int price = (int)Json.Num(d, "price", 0) * n;
            if (price > Budget) return "budget";
            if (Count("user", item) + n > Max(item)) return "full";
            doc["budget"] = (double)(Budget - price);
            Change("user", item, n);
            Persist();
            Log.Add("bought " + item + " x" + n);
            return "";
        }

        // the user's own to the shelf, or from it
        public string MoveUser(string item, bool toShelf)
        {
            string from = toShelf ? "user" : "shelf", to = toShelf ? "shelf" : "user";
            if (Count(from, item) < 1) return "not_enough";
            if (Count(to, item) + 1 > Max(item)) return "full";
            Change(from, item, -1);
            Change(to, item, 1);
            Persist();
            return "";
        }

        // ---- giving: an offer, done when the one given to takes it ----
        public string OfferToHer(string item)
        {
            if (ItemDef(item) == null) return "unknown_item";
            if (Count("user", item) < 1) return "not_enough";
            var o = new Offer { Id = "o" + (++offerSerial) + "-" + Launch.Id.Substring(0, Math.Min(6, Launch.Id.Length)), Item = item, From = "user", To = "character",
                                Char = Her, At = Time.realtimeSinceStartup };
            offers.Add(o);
            h.Bridge.Event("item_offered", new Dictionary<string, object> { { "offer", o.Id }, { "item", ItemInfo(item) }, { "from", "user" }, { "to", "character" } });
            Log.Add("offered " + item + " " + o.Id);
            return "";
        }

        public List<Offer> OffersToUser { get { return offers.FindAll(o => o.To == "user" && o.Char == Her); } }
        public List<Offer> OffersToHer { get { return offers.FindAll(o => o.To == "character" && o.Char == Her); } }

        // the one given to answers: "" done (taken or declined), else why not
        public string Answer(string offerId, bool take, string by)
        {
            var o = offers.Find(x => x.Id == offerId);
            if (o == null) return "no_offer";
            if (o.To != by) return "not_yours";
            if (o.Char != Her) { offers.Remove(o); return "other_character"; }
            string giver = o.From == "user" ? "user" : o.Char, taker = o.To == "user" ? "user" : o.Char;
            if (take)
            {
                if (Count(giver, o.Item) < 1) return "not_enough";                // (the giver no longer has it)
                if (Count(taker, o.Item) + 1 > Max(o.Item)) return "full";
                Change(giver, o.Item, -1);
                Change(taker, o.Item, 1);
                Persist();
            }
            offers.Remove(o);
            After(() =>
            {
                h.Bridge.Event("item_given", new Dictionary<string, object> { { "offer", o.Id }, { "item", ItemInfo(o.Item) }, { "from", o.From }, { "to", o.To },
                    { "taken", take } });
                Log.Add((take ? "taken " : "declined ") + o.Id);
            });
            return "";
        }

        // ---- the user's suggestion: told, Muse decides ----
        public string Suggest(string item, string from)
        {
            if (ItemDef(item) == null) return "unknown_item";
            if (!Usable(item)) return "no_use";
            if (Count(from == "shelf" ? "shelf" : "user", item) < 1) return "not_enough";
            var s = new Suggestion { Id = "s" + (++offerSerial), Item = item, From = from == "shelf" ? "shelf" : "user", Char = Her, At = Time.realtimeSinceStartup };
            suggestions.Add(s);
            h.Bridge.Event("item_suggested", new Dictionary<string, object> { { "suggestion", s.Id }, { "item", ItemInfo(item) }, { "from", s.From } });
            return "";
        }

        bool Usable(string item) { return Json.Str(ItemDef(item), "kind", "") == "drink"; }

        // ---- Muse's block "item" (MUSE_GUIDE 3.11): "" done (or a resend of one done), else why not ----
        public string FromBlock(Dictionary<string, object> it)
        {
            string req = Json.Str(it, "request", "");
            if (req.Length == 0) return "no_request";
            // the ledger and her record read first (GPT's re-review of #71): not readable yet - refused, nothing changed; the
            // same request may come again once it reads
            if (!h.Mood.EnsureRead()) return "save_failed";
            var done = Json.Arr(doc, "requests");
            if (done == null) { done = new List<object>(); doc["requests"] = done; }
            if (done.Contains(req)) return "";                  // (a resend: done once)
            // one commit (GPT's post-review of #65: the use was written, then its request in a second write - a stop between
            // them, and the same request used it again): the change, her effects to come and the request in one write, its
            // events and her drink once it is written; a write that fails puts all back as it was - refused save_failed,
            // nothing changed, the same request may come again
            string before = Json.Write(doc);
            var offersWas = new List<Offer>(offers);
            var suggestionsWas = new List<Suggestion>(suggestions);
            int pendingWas = h.Mood.PendingCount;
            inTx = true;
            afterTx = new List<Action>();
            string why;
            try { why = Operate(it); }
            finally { inTx = false; }
            var after = afterTx;
            afterTx = null;
            if (why.Length > 0) return why;
            done = Json.Arr(doc, "requests");
            if (done == null) { done = new List<object>(); doc["requests"] = done; }
            done.Add(req);
            while (done.Count > 500) done.RemoveAt(0);
            if (!h.Mood.Persist())
            {
                doc = Json.Parse(before) as Dictionary<string, object> ?? doc;
                offers.Clear();
                offers.AddRange(offersWas);
                suggestions.Clear();
                suggestions.AddRange(suggestionsWas);
                h.Mood.TruncatePending(pendingWas);
                MuseStudioPlugin.Log.LogWarning("MuseStudio: items: " + req + " not written - put back, refused (save_failed)");
                return "save_failed";
            }
            foreach (var a in after) a();
            return "";
        }

        // a block's one operation, in memory (FromBlock writes it): "" done, else why not (nothing changed)
        string Operate(Dictionary<string, object> it)
        {
            string why;
            if (Json.Has(it, "accept")) why = Answer(Json.Str(it, "accept"), true, "character");
            else if (Json.Has(it, "decline")) why = Answer(Json.Str(it, "decline"), false, "character");
            else if (Json.Has(it, "suggestion"))
            {
                // (the user's suggestion taken: from where he offered it - his own or the shelf)
                var s = suggestions.Find(x => x.Id == Json.Str(it, "suggestion") && x.Char == Her);
                if (s == null) why = "no_suggestion";
                else { why = Use(s.Item, "character", "self", s.From); if (why.Length == 0) suggestions.Remove(s); }
            }
            else if (Json.Has(it, "use") && Json.Str(it, "from", "own") == "user") why = "not_yours";        // (his own: only as he suggested)
            else if (Json.Has(it, "use")) why = Use(Json.Str(it, "use"), "character", Json.Str(it, "on", "self"), Json.Str(it, "from", "own"));
            else if (Json.Has(it, "give")) why = HerOffer(Json.Str(it, "give"), Json.Str(it, "from", "own"));
            else why = "no_operation";
            return why;
        }

        // she gives the user one of hers (or the shelf's): his to take or decline (his button)
        string HerOffer(string item, string from)
        {
            if (ItemDef(item) == null) return "unknown_item";
            string holder = from == "shelf" ? "shelf" : Her;
            if (Count(holder, item) < 1) return "not_enough";
            if (from == "shelf") return "from_shelf";              // (the shelf is both of theirs: nothing to give)
            var o = new Offer { Id = "o" + (++offerSerial) + "-" + Launch.Id.Substring(0, Math.Min(6, Launch.Id.Length)), Item = item, From = "character", To = "user",
                                Char = Her, At = Time.realtimeSinceStartup };
            offers.Add(o);
            After(() => Log.Add("she offered " + item + " " + o.Id));
            return "";
        }

        // a use: on her (self: her moods), or on the user (nothing in the numbers). by: "character" (Muse's block) / "user"
        public string Use(string item, string by, string on, string from)
        {
            var d = ItemDef(item);
            if (d == null) return "unknown_item";
            if (!Usable(item)) return "no_use";
            if (h.Outing.Out && by == "character") return "she_is_out";
            string holder = from == "shelf" ? "shelf" : from == "user" || by == "user" ? "user" : Her;
            if (Count(holder, item) < 1) return "not_enough";
            bool onUser = by == "character" ? on == "user" : on != "character";
            // (the setting, right before it is applied: Muse may not use what affects the user)
            if (by == "character" && onUser && Json.Has(d, "affects_user") && (bool)d["affects_user"] && h.MuseItemsOnUser != null && !h.MuseItemsOnUser.Value)
                return "user_items_off";
            Change(holder, item, -1);
            var applied = new List<object>();
            if (!onUser)
            {
                // her moods: each effect flows in over its minutes, dulled by the doses before it, never past the grade's cap
                var grade = Json.Obj(Json.Obj(Def, "grades"), ((int)Json.Num(d, "grade", 1)).ToString());
                float cap = (float)Json.Num(grade, "cap", 1.0), dull = (float)Json.Num(grade, "dull", 0.5);
                foreach (var eo in Json.Arr(d, "effects") ?? new List<object>())
                {
                    var e = eo as Dictionary<string, object>;
                    string mood = Json.Str(e, "mood", "");
                    if (!h.Mood.Values.ContainsKey(mood)) continue;
                    int doses = Doses(mood);
                    float amount = (float)Json.Num(e, "amount", 0) * Mathf.Pow(1f - dull, doses);
                    float room = Mathf.Max(0f, cap - h.Mood.Values[mood] - h.Mood.PendingOf(mood));
                    amount = Mathf.Min(amount, room);
                    if (amount > 0f) h.Mood.AddPending(mood, amount, (float)Json.Num(e, "minutes", 10), item, cap);
                    Dose(mood);
                    applied.Add(new Dictionary<string, object> { { "mood", mood }, { "amount", Math.Round(amount, 3) } });
                }
            }
            Persist();
            var ev = new Dictionary<string, object> { { "item", ItemInfo(item) }, { "by", by }, { "on", onUser ? "user" : "character" },
                { "from", holder == "shelf" ? "shelf" : holder == "user" ? "user" : "hers" } };
            After(() =>
            {
                if (!onUser) h.Stage.HoldProp(Json.Obj(d, "hold"), (float)Json.Num(Def, "drink_seconds", 12), Json.Obj(Def, "drinking"));
                Log.Add("used " + item + " by " + by + " on " + (onUser ? "user" : "character") + " " + Json.Write(applied));
                h.Bridge.Event("item_used", ev);
            });
            return "";
        }

        // the doses of a mood within tolerance_minutes, hers (the ledger: over restarts too)
        int Doses(string mood)
        {
            double now = UnixNow, window = Json.Num(Def, "tolerance_minutes", 60) * 60.0;
            var list = DoseList(mood);
            list.RemoveAll(t => now - Convert.ToDouble(t) > window);
            return list.Count;
        }

        void Dose(string mood) { DoseList(mood).Add(UnixNow); }

        List<object> DoseList(string mood)
        {
            var all = Json.Obj(doc, "doses");
            if (all == null) { all = new Dictionary<string, object>(); doc["doses"] = all; }
            var mine = Json.Obj(all, Her);
            if (mine == null) { mine = new Dictionary<string, object>(); all[Her] = mine; }
            var list = Json.Arr(mine, mood);
            if (list == null) { list = new List<object>(); mine[mood] = list; }
            return list;
        }

        static double UnixNow { get { return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; } }

        // ---- what Muse sees ----
        // an item as told: its name and kind; its effects only when it is known (else her moods' numbers alone tell)
        public Dictionary<string, object> ItemInfo(string id)
        {
            var d = ItemDef(id);
            var o = new Dictionary<string, object> { { "id", id }, { "name", Json.Str(d, "name", id) }, { "kind", Json.Str(d, "kind", "") }, { "grade", (int)Json.Num(d, "grade", 1) } };
            bool known = !Json.Has(d, "known") || (d["known"] is bool && (bool)d["known"]);
            o["known"] = known;
            if (known) o["effects"] = Json.Arr(d, "effects") ?? new List<object>();
            if (Json.Has(d, "affects_user")) o["affects_user"] = d["affects_user"];
            return o;
        }

        Dictionary<string, object> ShelfView() { return new Dictionary<string, object>(Stock("shelf")); }

        // the catalog's items: what there is (no price, no budget - the user's own)
        public List<object> Catalog()
        {
            var o = new List<object>();
            foreach (var kv in ItemDefs) { var i = ItemInfo(kv.Key); i["max"] = Max(kv.Key); o.Add(i); }
            return o;
        }

        // the state's items: the shelf, hers, the offers waiting for her answer, the user's suggestions; the budget only when
        // the user's setting tells it
        public Dictionary<string, object> State()
        {
            if (!Imported || string.IsNullOrEmpty(Her)) return null;
            var mine = new List<object>();
            foreach (var o in OffersToHer) mine.Add(new Dictionary<string, object> { { "offer", o.Id }, { "item", o.Item }, { "from", o.From } });
            var sug = new List<object>();
            foreach (var s in suggestions.FindAll(x => x.Char == Her && Time.realtimeSinceStartup - x.At < 600f))
                sug.Add(new Dictionary<string, object> { { "suggestion", s.Id }, { "item", s.Item }, { "from", s.From } });
            var giving = new List<object>();
            foreach (var o in OffersToUser) giving.Add(new Dictionary<string, object> { { "offer", o.Id }, { "item", o.Item } });
            var st = new Dictionary<string, object> { { "shelf", ShelfView() }, { "hers", new Dictionary<string, object>(Stock(Her)) }, { "offers", mine },
                { "suggestions", sug }, { "giving", giving }, { "user_items", h.MuseItemsOnUser == null || h.MuseItemsOnUser.Value } };
            if (h.TellBudget != null && h.TellBudget.Value) st["budget"] = Budget;
            return st;
        }

        // (checks) the ledger as if
        public void SetBudgetForCheck(int v) { doc["budget"] = (double)v; }
        public void SetRestockedForCheck(string date) { doc["restocked"] = date; }
        public void SetFractionForCheck(double seconds) { doc["fraction"] = seconds; }
        public void ClearDosesForCheck() { doc["doses"] = new Dictionary<string, object>(); }
    }
}
