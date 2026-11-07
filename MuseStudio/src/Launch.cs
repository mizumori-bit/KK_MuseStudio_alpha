using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MuseStudio
{
    // The start-up (the Muse side's launch notification spec; DECISIONS "Muse 側の資料との読み合わせ"): the launch id (made
    // when the plugin wakes) and its phases told to the bridge (POST /api/launch: app_starting at once, stage_ready when the
    // room is in and the character is to be chosen, character_selected), the chooser at the start (it waits; with "start with
    // the last character" on, the last one starts without it), and the packages tagged for another launch or another
    // character (dropped: package_dropped). The bridge keeps Muse's packages until the character is chosen (the response
    // pool: the app polls with ?hold=1 till then).
    public class Launch
    {
        public static readonly string Id = Guid.NewGuid().ToString("N").Substring(0, 12);
        readonly MuseStudioPlugin h;
        public string Phase = "";
        public bool Choosing;
        public string Chosen = "";            // the card chosen at the start
        public string ChosenBy = "";          // user / auto (the last character) / check
        public readonly List<string> Phases = new List<string>();   // (checks)

        public Launch(MuseStudioPlugin host) { h = host; }

        public void Tell(string phase)
        {
            Phase = phase;
            Phases.Add(phase);
            h.Bridge.Post("/api/launch", new Dictionary<string, object> { { "launch", Id }, { "phase", phase },
                { "character", phase == "character_selected" ? h.Chars.Info() : null } });
        }

        // (Stage.Setup) the room is in: the character to start with
        public IEnumerator Choose()
        {
            Tell("stage_ready");
            string last = h.CharacterCard.Value;
            if (MuseStudioPlugin.Args.ContainsKey("check"))
            {
                // the checks: the chooser shows a moment, then stage.json's card through the same confirm
                Choosing = true;
                yield return new WaitForSeconds(0.7f);
                string shots = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(MuseStudioPlugin.Args["check"]));
                Application.CaptureScreenshot(System.IO.Path.Combine(shots, "00_chooser.png"));
                yield return new WaitForSeconds(0.5f);
                // (a card picked: ID 設定・性格設定 over its picture)
                h.UiForCheck.PickForCheck(h.StageCard());
                yield return new WaitForSeconds(0.5f);
                Application.CaptureScreenshot(System.IO.Path.Combine(shots, "00b_chooser_picked.png"));
                foreach (var k in new[] { "このカードで始める", "アイデンティティリスト編集", "性格引き当てリスト編集" })
                {
                    Rect r;
                    if (h.UiForCheck.Buttons.TryGetValue(k, out r)) ChooserButtonsForCheck[k] = r;
                }
                yield return new WaitForSeconds(0.5f);
                Confirm(h.StageCard(), "check");
            }
            else if (h.AutoStartLast.Value && File.Exists(last)) Confirm(last, "auto");
            else Choosing = true;
            while (Choosing) yield return null;
        }

        public readonly Dictionary<string, Rect> ChooserButtonsForCheck = new Dictionary<string, Rect>();     // (checks) the footer's buttons

        public void Confirm(string path, string by)
        {
            Chosen = path;
            ChosenBy = by;
            Choosing = false;
            if (by != "check") h.CharacterCard.Value = path;      // the last character
        }

        // the launch and the character a package is for: its own tags, else what the bridge stamped when it took it (an
        // untagged package is for the launch and the character of then - PR #24; GPT's post-review of #26: checked here too)
        public static string LaunchOf(Dictionary<string, object> pkg) { return Json.Str(pkg, "launch") ?? Json.Str(pkg, "_launch_at"); }
        public static string CharacterOf(Dictionary<string, object> pkg) { return Json.Str(pkg, "character") ?? Json.Str(pkg, "_character_at"); }

        // a package of Muse's, right before it plays: one for another launch or another character than the one on the stage
        // is dropped - whether it waited in the bridge, in the receiving queue or in the player's (a switch while it was on
        // its way never shows it to the new character)
        public bool Accept(Dictionary<string, object> pkg)
        {
            string lid = LaunchOf(pkg), cid = CharacterOf(pkg);
            string reason = lid != null && lid != Id ? "old_launch" : cid != null && cid != h.CharacterId() ? "other_character" : "";
            if (reason.Length == 0) return true;
            h.Bridge.Ack(Json.Str(pkg, "_pid"), "dropped", reason);
            h.Bridge.Event("package_dropped", new Dictionary<string, object> { { "session", Json.Str(pkg, "session") }, { "turn", (int)Json.Num(pkg, "turn") },
                { "beat", Json.Has(pkg, "beat") ? (object)(int)Json.Num(pkg, "beat") : null }, { "reason", reason }, { "launch", lid }, { "character", cid },
                { "choices", Json.Has(pkg, "choices") }, { "by", "app" } });
            return false;
        }
    }

    // The relationship Muse keeps (the Muse side's relationship-values; DECISIONS "人物の層と好感度"): its values for a person
    // come in a package's "relationship" {"person": no / null (Muse herself), "affection": 0..100, "trust": 0..100} - at the
    // greeting and whenever they move. Taken only for the person on the stage (else block_refused other_person); the last
    // taken is in the state (relationship), so a lost package shows. The app's own use: the affection as the original's
    // relation stage of her touch reactions (stage.json relationship.stages: 0 first met, 1 friends, 2 girlfriend) - the
    // app's provisional marks, not Muse's (Muse writes from its own numbers). Not kept over a restart (Muse's store is the
    // record).
    public class Relationship
    {
        readonly MuseStudioPlugin h;
        public bool Has;
        public int Person;                 // 0: Muse herself
        public double Affection, Trust;
        public DateTime At;

        public Relationship(MuseStudioPlugin host) { h = host; }

        public void Apply(Dictionary<string, object> r)
        {
            string why = "";
            object pv;
            if (!r.ContainsKey("person")) why = "no_person";
            else if (!Json.Has(r, "affection") || !Json.Has(r, "trust")) why = "no_values";
            int person = r.TryGetValue("person", out pv) && pv is double ? (int)(double)pv : 0;
            if (why.Length == 0 && person != h.Personas.PersonNo(h.CharacterId())) why = "other_person";
            if (why.Length > 0)
            {
                h.Bridge.Event("block_refused", new Dictionary<string, object> { { "field", "relationship" }, { "value", person > 0 ? (object)person : null },
                    { "reason", why }, { "available", new List<object> { h.Personas.PersonNo(h.CharacterId()) > 0 ? (object)h.Personas.PersonNo(h.CharacterId()) : null } } });
                return;
            }
            Has = true;
            Person = person;
            Affection = Math.Max(0.0, Math.Min(100.0, Json.Num(r, "affection")));
            Trust = Math.Max(0.0, Math.Min(100.0, Json.Num(r, "trust")));
            At = DateTime.Now;
        }

        // the relation stage of her touch reactions: from the affection while it is the person on the stage's, else the setting
        public int Stage(int fallback)
        {
            if (!Has || Person != h.Personas.PersonNo(h.CharacterId())) return fallback;
            int st = fallback;
            foreach (var row in Json.Arr(Json.Obj(h.Stage.Cfg, "relationship"), "stages") ?? new List<object>())
            {
                var rr = (List<object>)row;
                if (Affection >= Convert.ToDouble(rr[0])) st = Convert.ToInt32(rr[1]);
            }
            return st;
        }

        public Dictionary<string, object> Info()
        {
            if (!Has) return null;
            return new Dictionary<string, object> { { "person", Person > 0 ? (object)Person : null }, { "affection", Math.Round(Affection, 1) },
                { "trust", Math.Round(Trust, 1) }, { "stage", Stage(-1) >= 0 ? (object)Stage(-1) : null }, { "applied", At.ToString("yyyy-MM-ddTHH:mm:ss") } };
        }
    }

    // Who she is (DECISIONS "人物の層と好感度"; the Muse side's ledger, its identity-layer proposal and its answers,
    // 2026-11-03), kept here (UserData/MuseStudio/personas.json), never in the card:
    // - the registered personas: numbered slots the user types in from the Muse side's ledger (the app never reads it); the
    //   number is the key Muse looks its persona skill up by, the name is for showing.
    // - the persons: issued here from their own counter (a plain running number - nothing in it comes from a card's name,
    //   file or content), never reused; one retired stays in the table as retired. A card belongs to a person only by the
    //   user's assignment (nothing is guessed); unassigned it is Muse herself (person null).
    // - a person's default persona; a card's own persona (or "none") wins over it.
    // character.person {"no", "name"} / null, character.persona {"no", "name"} / null (the card's, else its person's).
    public class Personas
    {
        public class Person { public int No; public string Name = ""; public int Persona; public bool Retired; }
        class Card { public int Person; public bool OwnPersona; public int Persona; public string PersonaName = ""; }

        readonly string file;
        public readonly List<KeyValuePair<int, string>> Slots = new List<KeyValuePair<int, string>>();
        public readonly List<Person> Persons = new List<Person>();
        public int NextPerson = 1;
        readonly Dictionary<string, Card> cards = new Dictionary<string, Card>();

        public Personas(string path)
        {
            file = path;
            try
            {
                if (!File.Exists(file)) return;
                var doc = Json.Parse(File.ReadAllText(file)) as Dictionary<string, object>;
                foreach (var s in Json.Arr(doc, "slots") ?? new List<object>())
                    if (Json.Has(s, "no")) Slots.Add(new KeyValuePair<int, string>((int)Json.Num(s, "no"), Json.Str(s, "name", "")));
                foreach (var pe in Json.Arr(doc, "persons") ?? new List<object>())
                {
                    if (!Json.Has(pe, "no")) continue;
                    object r;
                    Persons.Add(new Person { No = (int)Json.Num(pe, "no"), Name = Json.Str(pe, "name", ""), Persona = (int)Json.Num(pe, "persona", 0),
                        Retired = ((Dictionary<string, object>)pe).TryGetValue("retired", out r) && r is bool && (bool)r });
                }
                NextPerson = Math.Max(1, (int)Json.Num(doc, "next_person", 1));
                foreach (var pe in Persons) NextPerson = Math.Max(NextPerson, pe.No + 1);
                foreach (var kv in Json.Obj(doc, "cards") ?? new Dictionary<string, object>())
                {
                    var d = kv.Value as Dictionary<string, object>;
                    if (d == null) continue;
                    var c = new Card { Person = (int)Json.Num(d, "person", 0) };
                    object pz;
                    if (d.TryGetValue("persona", out pz)) { c.OwnPersona = true; c.Persona = (int)Json.Num(pz, "no", 0); c.PersonaName = Json.Str(pz, "name", ""); }
                    else if (Json.Has(d, "no")) { c.OwnPersona = true; c.Persona = (int)Json.Num(d, "no"); c.PersonaName = Json.Str(d, "name", ""); }   // (the first form)
                    cards[kv.Key] = c;
                }
                Slots.Sort((a, b) => a.Key.CompareTo(b.Key));
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: personas " + file + ": " + e.Message); }
        }

        Card CardOf(string cardId, bool make)
        {
            Card c;
            if (!cards.TryGetValue(cardId, out c) && make) cards[cardId] = c = new Card();
            return c;
        }

        public string NameOf(int no)
        {
            foreach (var s in Slots) if (s.Key == no) return s.Value;
            return null;
        }

        public Person PersonOf(int no) { return Persons.Find(p => p.No == no); }

        // ---- a card's person and persona ----

        public int PersonNo(string cardId) { var c = CardOf(cardId, false); return c != null ? c.Person : 0; }

        public Dictionary<string, object> PersonInfo(string cardId)
        {
            var pe = PersonOf(PersonNo(cardId));
            return pe == null ? null : new Dictionary<string, object> { { "no", pe.No }, { "name", pe.Name } };
        }

        // the card's own persona: -1 none set (its person's), 0 "none", else the slot
        public int OwnPersona(string cardId) { var c = CardOf(cardId, false); return c != null && c.OwnPersona ? c.Persona : -1; }

        // where the persona comes from: "card" / "person" / null (none)
        public string PersonaSource(string cardId)
        {
            int own = OwnPersona(cardId);
            if (own >= 0) return own > 0 ? "card" : null;
            var pe = PersonOf(PersonNo(cardId));
            return pe != null && pe.Persona > 0 ? "person" : null;
        }

        public Dictionary<string, object> Of(string cardId)
        {
            var c = CardOf(cardId, false);
            int no = 0;
            string stored = "";
            if (c != null && c.OwnPersona) { no = c.Persona; stored = c.PersonaName; }
            else { var pe = PersonOf(PersonNo(cardId)); if (pe != null) no = pe.Persona; }
            if (no <= 0) return null;
            return new Dictionary<string, object> { { "no", no }, { "name", NameOf(no) ?? stored } };
        }

        // the card's own persona: -1 back to its person's, 0 none, else the slot
        public void SetOwnPersona(string cardId, int no)
        {
            var c = CardOf(cardId, true);
            c.OwnPersona = no >= 0;
            c.Persona = Math.Max(0, no);
            c.PersonaName = no > 0 ? NameOf(no) ?? "" : "";
            Save();
        }

        public void Assign(string cardId, int person)
        {
            var pe = PersonOf(person);
            CardOf(cardId, true).Person = pe != null && !pe.Retired ? person : 0;
            Save();
        }

        public int CardCount(int person)
        {
            int n = 0;
            foreach (var c in cards.Values) if (c.Person == person) n++;
            return n;
        }

        // ---- the persons ----

        public int AddPerson(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return 0;
            var pe = new Person { No = NextPerson++, Name = name };
            Persons.Add(pe);
            Save();
            return pe.No;
        }

        public void RenamePerson(int no, string name)
        {
            var pe = PersonOf(no);
            if (pe != null && name.Trim().Length > 0) { pe.Name = name.Trim(); Save(); }
        }

        public void SetPersonPersona(int no, int persona)
        {
            var pe = PersonOf(no);
            if (pe != null) { pe.Persona = Math.Max(0, persona); Save(); }
        }

        // the person is retired (kept in the table, its number never issued again); its cards go back to Muse herself
        public List<string> Retire(int no)
        {
            var pe = PersonOf(no);
            var moved = new List<string>();
            if (pe == null || pe.Retired) return moved;
            pe.Retired = true;
            foreach (var kv in cards) if (kv.Value.Person == no) { kv.Value.Person = 0; moved.Add(kv.Key); }
            Save();
            return moved;
        }

        // ---- the registered personas (slots) ----

        public string AddSlot(int no, string name)
        {
            name = (name ?? "").Trim();
            if (no <= 0) return "番号は 1 以上の整数で";
            if (name.Length == 0) return "名前を入れてください";
            if (NameOf(no) != null) return "その番号はもうあります";
            Slots.Add(new KeyValuePair<int, string>(no, name));
            Slots.Sort((a, b) => a.Key.CompareTo(b.Key));
            Save();
            return "";
        }

        public void Rename(int no, string name)
        {
            for (int i = 0; i < Slots.Count; i++)
                if (Slots[i].Key == no && name.Trim().Length > 0) Slots[i] = new KeyValuePair<int, string>(no, name.Trim());
            foreach (var c in cards.Values) if (c.OwnPersona && c.Persona == no) c.PersonaName = name.Trim();    // (shown if the slot goes)
            Save();
        }

        // the slot goes; the cards that had it keep their number (and its last name) until set again
        public void RemoveSlot(int no)
        {
            Slots.RemoveAll(s => s.Key == no);
            Save();
        }

        public List<object> PersonsInfo()
        {
            var o = new List<object>();
            foreach (var pe in Persons)
                o.Add(new Dictionary<string, object> { { "no", pe.No }, { "name", pe.Name }, { "persona", pe.Persona > 0 ? (object)pe.Persona : null },
                    { "retired", pe.Retired }, { "cards", CardCount(pe.No) } });
            return o;
        }

        public void Save()
        {
            try
            {
                var slots = new List<object>();
                foreach (var s in Slots) slots.Add(new Dictionary<string, object> { { "no", s.Key }, { "name", s.Value } });
                var persons = new List<object>();
                foreach (var pe in Persons)
                    persons.Add(new Dictionary<string, object> { { "no", pe.No }, { "name", pe.Name }, { "persona", pe.Persona }, { "retired", pe.Retired } });
                var cs = new Dictionary<string, object>();
                foreach (var kv in cards)
                {
                    var d = new Dictionary<string, object> { { "person", kv.Value.Person } };
                    if (kv.Value.OwnPersona)
                        d["persona"] = kv.Value.Persona > 0 ? new Dictionary<string, object> { { "no", kv.Value.Persona }, { "name", kv.Value.PersonaName } } : null;
                    cs[kv.Key] = d;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, Json.Write(new Dictionary<string, object> { { "slots", slots }, { "persons", persons }, { "next_person", NextPerson }, { "cards", cs } }));
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: personas save: " + e.Message); }
        }
    }
}
