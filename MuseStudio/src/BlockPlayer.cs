using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // Plays one turn package at a time (MUSE_GUIDE.md 5), one block per display unit: a block with "location" moves there
    // first, then its emotion / scene / camera / play take effect and its text shows - revealed quickly (reveal_cps), the
    // first press shows the rest, the next one advances. Advance: click / Enter / Space, or auto (characters x per_char +
    // base after the reveal; the voice is off). A block is presented when its text is all out (no voice): the scene's gauge
    // counts from then, the last block's presentation is the package's (the state's beat.since_presented counts from it); in
    // a play the next package may replace that last line without a tap. A package tagged for another launch or character is
    // dropped (Launch.Accept). After the last block: the idle scene and the standard face. A monologue package ("monologue":
    // true; Muse's batch while she is alone, MUSE_GUIDE.md 3.11) plays by itself, a block every monologue.min..max seconds;
    // what is left of it goes (package_dropped, reason monologue) when the user comes back, Muse answers him (a package that
    // is not a monologue), she moves or her mood's level changes. A block's belief sets her belief (Presence).
    public class BlockPlayer
    {
        readonly MuseStudioPlugin h;
        readonly Queue<Dictionary<string, object>> pending = new Queue<Dictionary<string, object>>();
        public Dictionary<string, object> Pkg;
        public List<object> Blocks;
        public int Index = -1;
        public string Text = "";
        public bool IsSound;
        public bool Moving;           // a block's location is loading
        public bool Auto;
        public float Pace = 1f;       // the reveal and the auto advance faster / slower (a replay's speed)
        public int Played;            // packages played (checks)
        public float ShownAt;         // when the block's text came out
        float presentedAt = -1f;      // when its text was all out (-1: still revealing)
        bool revealAll;
        float waitLeft = -1f, monoWait = -1f;
        readonly float perChar, baseSec, revealCps, monoMin, monoMax, monoPerChar;
        readonly Dictionary<string, object> speechCfg;     // (stage.json speech: the words' pace and the mouth's beats)
        public Spoken Spoken;         // the block's words parsed (its ruby; null: a sound block)
        public bool Speaking;         // its words come out at the pace she says them, her mouth with them (the setting 口の動き)
        public bool MonologueMoved;   // the monologue's own block moved her (not a change of the situation)
        public string Speaker = "self";   // the package's speaker (the match: "opponent" - the opponent's, forwarded by Muse)

        public bool Opponent { get { return Busy && Speaker == "opponent"; } }

        // the name over the dialogue box: hers, or the opponent's
        public string SpeakerName { get { return Opponent ? h.Match.OpponentName : h.Stage.Name; } }

        public BlockPlayer(MuseStudioPlugin host)
        {
            h = host;
            var adv = Json.Obj(h.Stage.Cfg, "advance");
            Auto = adv != null && adv.ContainsKey("auto") && adv["auto"] is bool && (bool)adv["auto"];
            perChar = (float)Json.Num(adv, "per_char", 0.06);
            baseSec = (float)Json.Num(adv, "base", 1.0);
            revealCps = (float)Json.Num(adv, "reveal_cps", 45.0);
            var mono = Json.Obj(h.Stage.Cfg, "monologue");
            monoMin = (float)Json.Num(mono, "min_seconds", 10.0);
            monoMax = (float)Json.Num(mono, "max_seconds", 20.0);
            monoPerChar = (float)Json.Num(mono, "per_char", 0.15);
            speechCfg = Json.Obj(h.Stage.Cfg, "speech") ?? new Dictionary<string, object>();
        }

        static bool IsMonologue(Dictionary<string, object> p)
        {
            object v;
            return p != null && p.TryGetValue("monologue", out v) && v is bool && (bool)v;
        }

        public bool Monologue { get { return Busy && IsMonologue(Pkg); } }

        // her monologue can be heard: she is with him, or out on her own (the toilet, the shower) with him in her room unseen
        public bool Audible { get { return h.Stage.Location == h.Presence.HerRoom; } }      // (he is where she is: out, or apart)

        // the box shows the block (a monologue while she is out and he is elsewhere: not)
        public bool Showing { get { return Busy && (!Monologue || Audible); } }

        // the monologue's blocks still to come (the one showing aside, the queued monologues with it)
        public int MonologueLeft
        {
            get
            {
                int n = Monologue ? Blocks.Count - Index - 1 : 0;
                foreach (var p in pending) if (IsMonologue(p)) n += (Json.Arr(p, "blocks") ?? new List<object>()).Count;
                return n;
            }
        }

        // what is left of her monologue goes (the line showing stays till its time)
        public int DropMonologue(string cause)
        {
            int dropped = 0;
            if (Monologue && Index + 1 < Blocks.Count)
            {
                int left = Blocks.Count - Index - 1;
                Blocks = Blocks.GetRange(0, Index + 1);
                dropped += left;
                MonologueDropped(Pkg, left, cause);
            }
            var keep = new Queue<Dictionary<string, object>>();
            foreach (var p in pending)
            {
                if (!IsMonologue(p)) { keep.Enqueue(p); continue; }
                int n = (Json.Arr(p, "blocks") ?? new List<object>()).Count;
                dropped += n;
                MonologueDropped(p, n, cause);
            }
            pending.Clear();
            foreach (var p in keep) pending.Enqueue(p);
            return dropped;
        }

        void MonologueDropped(Dictionary<string, object> p, int blocks, string cause)
        {
            if (p != Pkg) h.Bridge.Ack(Json.Str(p, "_pid"), "dropped", "monologue");     // (one waiting here: never begun)
            h.Bridge.Event("package_dropped", new Dictionary<string, object> { { "session", Json.Str(p, "session") }, { "turn", (int)Json.Num(p, "turn") },
                { "beat", null }, { "reason", "monologue" }, { "cause", cause }, { "blocks_dropped", blocks }, { "choices", Json.Has(p, "choices") }, { "by", "app" } });
        }

        static float Now { get { return Time.realtimeSinceStartup; } }

        // the characters out so far (the text is revealed reveal_cps a second)
        public int Visible
        {
            get
            {
                if (revealAll || Moving) return Text.Length;
                if (Speaking && Spoken != null) return Spoken.VisibleAt((Now - ShownAt) * Pace);      // (at the pace she says it)
                return Mathf.Min(Text.Length, (int)((Now - ShownAt) * revealCps * Pace));
            }
        }

        public int Pending { get { return pending.Count; } }

        // (a replay's end) what is queued and showing goes
        public void Clear()
        {
            pending.Clear();
            if (Busy) End(false);
        }

        public bool Revealing { get { return Busy && !Moving && Visible < Text.Length; } }

        public bool Busy { get { return Pkg != null; } }

        // the last block is out (the beat loop's presentation has ended)
        public bool PresentedLast { get { return Busy && !Moving && Blocks != null && Index == Blocks.Count - 1 && Text.Length > 0 && presentedAt >= 0f; } }

        // seconds since the block was presented (0 while it is revealing; very long without a block)
        public float ShownFor { get { return Busy && !Moving ? (presentedAt < 0f ? 0f : Now - presentedAt) : 1e9f; } }

        // seconds since the block's text came out
        public float SinceShown { get { return Busy && !Moving ? Now - ShownAt : 1e9f; } }

        public void Enqueue(Dictionary<string, object> pkg)
        {
            if (!IsMonologue(pkg)) DropMonologue("conversation");      // Muse answers him: what she was saying alone goes
            pending.Enqueue(pkg);
        }

        // Her inner thoughts (the Muse side's decision 2026-11-03): a dialogue block's text may hold parts in full-width （） - her
        // feelings, her fancies - that stay in the chat's log as a hidden extra and are not shown here. Muse keeps them flat
        // (no nesting, always paired) and decorative (nothing the scene needs is only inside them), so one pass removes them.
        static readonly System.Text.RegularExpressions.Regex Inner = new System.Text.RegularExpressions.Regex("（[^（）]*）");

        public static string Shown(string text) { return Inner.Replace(text ?? "", "").Trim(' ', '\u3000', '\n', '\r', '\t'); }

        bool skipEmpty;

        public void Update(float dt)
        {
            if (!h.Stage.Ready || h.Chars.Creating)                 // the creation is open: Muse's packages wait
            {
                if (Mouth.Instance != null && Mouth.Instance.Active) Mouth.Instance.Stop();
                return;
            }
            // a block that was only her inner thought: its effects are done, the next one at once
            if (skipEmpty && Busy && !Moving)
            {
                skipEmpty = false;
                if (Index == Blocks.Count - 1) h.Beat.Presented(Pkg);     // (the package's presentation ends with it)
                Advance(true);
            }
            // (Muse answering him cuts in on her monologue's line; her monologue while she is out on her own goes on its own
            // time, heard only while he is in her room - Muse's request 3 of 2026-11-04: its pace never tells that he peeps)
            if (pending.Count > 0 && (!Busy || (PresentedLast && h.Play.Active) || (Monologue && !IsMonologue(pending.Peek()))))
            {
                var next = pending.Dequeue();
                if (h.Launch.Accept(next))
                {
                    if (Busy) End(false);
                    Begin(next);
                }
            }
            if (Busy && !Moving && presentedAt < 0f && Index >= 0 && !Revealing)
            {
                presentedAt = Now;
                if (Index == Blocks.Count - 1) h.Beat.Presented(Pkg);     // no voice: the last block all out is the presentation's end
            }
            if (Busy && !Moving && Monologue && monoWait >= 0f && !Revealing)
            {
                monoWait -= dt;                       // (heard or not: her time)
                if (monoWait < 0f) Advance(true);
            }
            else if (Busy && !Moving && Auto && waitLeft >= 0f && !Revealing)
            {
                waitLeft -= dt;
                if (waitLeft < 0f) Advance(true);
            }
            MouthTick();
        }

        // the speaker's mouth with the words (PR #46 §6): while the line is being said - not past its end, not once a press
        // showed it all, not while the speaker's mouth is in use (a kiss, a service) or she is not on the stage; else the
        // face's own mouth again
        void MouthTick()
        {
            var m = Mouth.Instance;
            if (m == null) return;
            float t = (Now - ShownAt) * Pace;
            ChaControl who = Busy && !Moving && Speaking && Spoken != null && !revealAll && t < Spoken.Seconds ? SpeakerCha() : null;
            if (who == null) { if (m.Active) m.Stop(); return; }
            m.Drive(who, Spoken.OpenAt(t));
        }

        ChaControl SpeakerCha()
        {
            var st = h.Stage;
            if (Speaker == "opponent")
                return h.Match.Active && st.Partner != null && !h.Play.MouthBusy(true) ? st.Partner.charInfo : null;
            if (st.Heroine == null || h.Outing.Out || h.Presence.HerAt.Length > 0 || h.Play.MouthBusy(false)) return null;
            return st.Cha;
        }

        // the user switched the character: what waits here was for the one before (the new one's tagged packages stay) -
        // dropped (package_dropped other_character), never shown to the new character (PR #24, GPT's point 3)
        public int DropPendingFor(string character)
        {
            int n = 0;
            var keep = new Queue<Dictionary<string, object>>();
            foreach (var p in pending)
            {
                string cid = Launch.CharacterOf(p);           // (its tag, else the bridge's stamp)
                if (cid != null && cid == character) { keep.Enqueue(p); continue; }
                n++;
                h.Bridge.Event("package_dropped", new Dictionary<string, object> { { "session", Json.Str(p, "session") }, { "turn", (int)Json.Num(p, "turn") },
                    { "beat", Json.Has(p, "beat") ? (object)(int)Json.Num(p, "beat") : null }, { "reason", "other_character" }, { "launch", Json.Str(p, "launch") },
                    { "character", cid }, { "choices", Json.Has(p, "choices") }, { "by", "app" } });
                h.Bridge.Ack(Json.Str(p, "_pid"), "dropped", "other_character");
            }
            pending.Clear();
            foreach (var p in keep) pending.Enqueue(p);
            return n;
        }

        public void EnqueueForCheck(Dictionary<string, object> pkg) { pending.Enqueue(pkg); }

        void Begin(Dictionary<string, object> pkg)
        {
            if (!h.Replay.Playing && !h.Match.Replaying) h.Bridge.Ack(Json.Str(pkg, "_pid"), "started");     // (from here its operations may apply: never again)
            var rel = Json.Obj(pkg, "relationship");
            if (rel != null && !h.Match.Replaying) h.Relationship.Apply(rel);       // Muse's values for the person (the state shows the last taken)
            var sets = Json.Obj(pkg, "settings");
            if (sets != null && !h.Match.Replaying) h.MuseSettings(sets, pkg);      // the speeds the user asked Muse to change (PR #12 adopted)
            shownTold = false;
            // the match (MUSE_GUIDE 8): the package's match (export / start / end), its speaker, its turn
            var mt = Json.Obj(pkg, "match");
            if (mt != null) h.Match.FromPackage(mt);
            Speaker = Json.Str(pkg, "speaker", "self") == "opponent" ? "opponent" : "self";
            if (Speaker == "opponent" && !h.Match.Active)
            {
                Refuse("speaker", "opponent", "not_in_match");
                Speaker = "self";
            }
            h.Replay.Package(pkg, Speaker);              // (a live match: its record)
            h.Match.Played(pkg, Speaker);
            if (Speaker == "opponent") h.Match.OpponentClothes(Json.Obj(pkg, "clothes_now"), true);     // (its look as it sent this)
            Pkg = pkg;
            Blocks = Json.Arr(pkg, "blocks") ?? new List<object>();
            Index = -1;
            ownSerial = 0;
            h.Beat.Played(pkg);
            Advance(true);
        }

        // the user's press: the rest of a revealing text first, then the next block (force: the next block at once)
        public void Advance(bool force = false)
        {
            if (!Busy || Moving) return;
            if (!force && Revealing) { revealAll = true; return; }
            Index++;
            if (Index >= Blocks.Count) { End(true); return; }
            MuseStudioPlugin.Instance.StartCoroutine(Show(Blocks[Index] as Dictionary<string, object>));
        }

        IEnumerator Show(Dictionary<string, object> b)
        {
            if (b == null) yield break;
            string loc = Json.Str(b, "location", null);
            if (!string.IsNullOrEmpty(loc) && h.Places != null) loc = h.Places.KeyOf(loc) ?? loc;      // (named by its id: its key - PR #46 §4.1)
            bool opp = Speaker == "opponent";
            // Muse's start of a normal play with a move to another room while a play goes on: the move would end that play before
            // the start is judged, and a refused start must leave it as it was (GPT's review of PR #3) - neither is done, both
            // refused (the move and the start in their own blocks: the move, then the start)
            var plStart = Json.Obj(b, "play");
            bool startWithMove = !opp && !h.Match.Active && plStart != null && Json.Str(plStart, "start", null) != null && !string.IsNullOrEmpty(loc)
                && loc != h.Stage.Location && h.Play.Active && !h.Presence.Apart;
            if (startWithMove) { Refuse("location", loc, "with_start"); loc = null; }
            if (!string.IsNullOrEmpty(loc) && (h.Match.Active || opp)) { Refuse("location", loc, "in_match"); loc = null; }       // (the match's room stays)
            if (!string.IsNullOrEmpty(loc) && loc != h.Stage.Location && !h.Stage.Reachable(loc))
            {
                // no such room (MUSE_GUIDE.md 3.4: every registered room is a place - the catalog's rooms; PR #46 §4.1): refused,
                // the block shows here
                Refuse("location", loc, "unknown_location");
                loc = null;
            }
            if (!string.IsNullOrEmpty(loc) && h.Presence.Apart && !opp)
            {
                // (apart: she goes there on her own - to his room, together again; the stage stays his)
                h.Presence.HerAt = loc == h.Stage.Location ? "" : loc;
                if (loc == h.Stage.Location) h.Presence.Cause = "she_came";
                if (h.Presence.HerAt.Length == 0) h.Stage.Place(h.Stage.Heroine);
                else
                {
                    yield return h.Stage.EnsureRoom(loc);         // (a room not loaded: in first - PR #46 §4.1)
                    h.Stage.PlaceIn(h.Stage.Heroine, loc);
                }
                if (h.Presence.HerAt.Length == 0) h.SendCatalog();
                loc = null;
            }
            if (!string.IsNullOrEmpty(loc) && loc != h.Stage.Location)
            {
                Moving = true;
                Text = "";
                if (Monologue) MonologueMoved = true;
                if (h.Play.Active) Own(() => h.Play.End("location"));
                string prev = h.Stage.Location;
                yield return h.Stage.SetLocation(loc);
                Moving = false;
                if (h.Stage.Location == loc)
                {
                    h.CameraMemory.Recall(loc);          // (the room's remembered view; this block's own camera still comes after it)
                    h.Bridge.Event("location_changed", new Dictionary<string, object> { { "location", h.Stage.LocationInfo(loc) },
                        { "previous", prev }, { "source", "package" } });
                    h.SendCatalog();
                }
                // (an unknown location: the place stays, no event - MUSE_GUIDE.md 4)
            }
            var stage = h.Stage;
            var play = h.Play;
            h.Mood.Acted();
            h.Match.Block(b, Speaker);                   // (the match: a declared climax, the phase)
            if (opp)
            {
                // the opponent's block: its face on its body, the play's fields (the play is the two's), its words
                var oemo = Json.Obj(b, "emotion");
                if (oemo != null && Json.Has(oemo, "label") && h.Match.HasBody && !stage.ApplyFaceOn(stage.Partner, Json.Str(oemo, "label")))
                    stage.ApplyFaceOn(stage.Partner, "neutral");
                var ocl = Json.Obj(b, "clothes");
                if (ocl != null) h.Match.OpponentClothes(ocl, false);        // (its own clothes: on its body, as on its side)
                Pose(Json.Str(b, "pose", null), stage.Partner != null ? stage.Partner.charInfo : null);     // (its talk motion: on its body)
                MatchPlay(Json.Obj(b, "play"));
                Present(b);
                yield break;
            }
            // her climax now (Muse's request of 2026-11-05: a pair's own cue - Muse decides, the app shows): in the talk only. Not
            // counted, no gauge, nothing told - but a climax all the same for Muse's mood after it (satisfied: its window opens,
            // before this block's own mood); the block's scene after it, its face over the climax face
            string anim = Json.Str(b, "anim", null);
            if (anim != null)
            {
                var ownFace = Json.Obj(b, "emotion");
                if (anim != "climax") Refuse("anim", anim, "unknown_value", new List<object> { "climax" });
                else if (h.Match.Active) Refuse("anim", anim, "in_match");         // (there: declare)
                else if (play.Active) Refuse("anim", anim, "in_play");             // (there: her gauge, play.finish, play.climax)
                else if (h.Outing.Out) Refuse("anim", anim, "she_is_out");
                else if (!stage.TalkClimax(ownFace == null || !Json.Has(ownFace, "label"))) Refuse("anim", anim, "not_played");
                else h.Mood.OnClimax();
            }
            var md = Json.Obj(b, "mood");
            if (md != null && !h.Match.Replaying) h.Mood.MuseMood(md);         // her climax satisfied her / relieved / leaked (Muse's calls)
            var bl = Json.Obj(b, "belief");
            if (bl != null && !h.Match.Replaying) h.Presence.Believe(bl);      // she thinks he is away / not (Muse's call)
            var cl = Json.Obj(b, "clothes");
            if (cl != null && !h.Outing.Out) h.ChangeClothes(cl, "muse");     // she undresses / dresses (or he undresses her: Muse's telling)
            // an outfit card worn (PR #46 §3): the same way as the user's menu; refused - in a play / a match / she is out / no
            // such card / not readable - nothing changed (block_refused, field coordinate)
            string coord = Json.Str(b, "coordinate", null);
            if (coord != null && !h.Match.Replaying)
            {
                string cwhy = h.Coords.Wear(coord, "muse");
                if (cwhy.Length > 0)
                {
                    var ids = new List<object>();
                    if (cwhy == "no_coordinate") foreach (var e in h.Coords.List) ids.Add(e.Id);
                    Refuse("coordinate", coord, cwhy, ids);
                }
            }
            // what she does (Muse's entry): masturbation / toilet / shower / shower_together - a name, or {"kind", "play"}
            Pose(Json.Str(b, "pose", null), stage.Cha);        // (a match's talk: her motion)
            var act = Json.Obj(b, "action");
            string actKind = Json.Str(b, "action") ?? Json.Str(act, "kind");
            if (actKind != null) Own(() => h.Urges.Act(actKind, act));          // (in a match: refused, in_match)
            var emo = Json.Obj(b, "emotion");
            if (emo != null && Json.Has(emo, "label"))
            {
                // an unknown label: the standard face (MUSE_GUIDE.md 4)
                stage.MuseFace(Json.Str(emo, "label"));          // (held - PR #46 §2; an unknown label: the standard face, MUSE_GUIDE 4)
                h.Touch.PackageFace();
                stage.AfterClimax = null;          // (a face of Muse's own wins over a package end's after her climax)
            }
            var sc = Json.Obj(b, "scene");
            if (sc != null && Json.Has(sc, "key") && !play.Active && !h.Outing.Out && !(h.Match.Active && Json.Has(b, "pose")))     // (a match's pose wins)
            {
                int inten = Json.Has(sc, "intensity") ? Mathf.Clamp((int)Json.Num(sc, "intensity"), 1, 3) : 1;
                float sp = Json.Has(sc, "speed") ? Mathf.Clamp((float)Json.Num(sc, "speed"), 0.5f, 2f) : 1f;
                stage.ApplyScene(Json.Str(sc, "key"), inten, sp);        // (an unknown key: the motion stays)
            }
            string cam = Json.Str(b, "camera", null);
            if (!string.IsNullOrEmpty(cam) && !play.Active) stage.ApplyCamera(cam, false);      // (an unknown camera: it stays)
            var pl = Json.Obj(b, "play");
            // Muse's start or change of a normal play (outside a match): refused, none of this block's play fields take effect
            // Muse's end of a normal play (outside a match; PR #12 adopted): checked as the start is; refused, none of the block's
            // play fields take effect (the play goes on)
            if (pl != null && !h.Match.Active && Json.Str(pl, "start", null) == null && IsTrue(pl, "end"))
            {
                bool endedNow = false;
                Own(() => endedNow = MuseEnd(pl));
                if (!endedNow) pl = null;
            }
            if (pl != null && !h.Match.Active && Json.Str(pl, "start", null) != null)
            {
                bool started = false;
                Own(() => started = MuseStart(pl, startWithMove));
                if (!started) pl = null;
            }
            var plNow = pl;
            Own(() => MatchPlay(plNow));
            if (pl != null)
            {
                if (!play.Active)
                {
                    foreach (var k in new[] { "finish", "pull", "insert", "condom" })
                    {
                        object v;
                        if (pl.TryGetValue(k, out v) && v != null && !(v is bool && !(bool)v && k != "condom")) Refuse("play." + k, FieldValue(v), "no_play");
                    }
                }
                else foreach (var f in play.Apply(pl))
                {
                    object v;
                    pl.TryGetValue(f.Key.Substring(5), out v);
                    Refuse(f.Key, FieldValue(v), f.Value, f.Key == "play.finish" ? new List<object>(play.Climax.Places().ToArray()) : null);
                }
            }
            Present(b);
        }

        // the package's first block on the screen: package_shown - when the bridge took it and when it showed (PR #12 adopted:
        // Muse measures "from its making to its showing" without guessing; the bridge's clock and the app's are this PC's).
        // Only a block with words on the screen tells it: one that is only her inner thought (or empty) does not, and the next
        // block that shows does (GPT's post-review of #15); a package with nothing to show sends none
        bool shownTold;
        public int ShownToldForCheck;     // (checks)

        void TellShown()
        {
            if (shownTold || Pkg == null) return;
            shownTold = true;
            ShownToldForCheck++;
            long shown = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            object rv;
            long received = Pkg.TryGetValue("_received_ms", out rv) && rv != null ? Convert.ToInt64(rv) : 0L;
            h.Bridge.Event("package_shown", new Dictionary<string, object> { { "session", Json.Str(Pkg, "session") }, { "turn", (int)Json.Num(Pkg, "turn") },
                { "received_ms", received > 0 ? (object)received : null }, { "shown_ms", shown }, { "waited_ms", received > 0 ? (object)(shown - received) : null },
                { "monologue", Monologue }, { "speaker", Speaker } });
        }

        // the block's words on the screen (hers or the opponent's)
        void Present(Dictionary<string, object> b)
        {
            IsSound = Json.Str(b, "type", "dialogue") == "sound";
            // (a sound block's （） are its own: shown; a dialogue's words: its inner thoughts gone, then its ruby - the one
            // parse for her Muse's, the opponent's and a replay's: PR #46 §6)
            Spoken = IsSound ? null : Speech.Line(Shown(Json.Str(b, "text", "")), speechCfg);
            Text = IsSound ? Json.Str(b, "text", "") : Spoken.Display;
            Speaking = Spoken != null && Text.Length > 0 && h.SpeakMouth != null && h.SpeakMouth.Value;
            if (Text.Length > 0) TellShown();
            skipEmpty = Text.Length == 0;          // (all of it her inner thought: its effects, no box)
            ShownAt = Now;
            presentedAt = -1f;
            revealAll = Text.Length == 0;
            // (said at her pace: the line took its reading's time - the base's wait after it, as after a voice)
            waitLeft = Auto ? (Speaking ? (float)Json.Num(speechCfg, "after_seconds", baseSec) : Text.Length * perChar + baseSec) / Pace : -1f;
            monoWait = Monologue ? Mathf.Clamp(monoMin + Text.Length * monoPerChar, monoMin, monoMax) : -1f;
            h.Beat.Progress();
        }

        // the match's play: start (a play id, the original's male part by side, a spot, anal) / end - only in a match (the
        // users watch; a play is Muse's to begin there); pace and the rest as ever, from either side's package
        void MatchPlay(Dictionary<string, object> pl)
        {
            if (pl == null) return;
            var play = h.Play;
            string start = Json.Str(pl, "start", null);
            if (start != null)
            {
                if (!h.Match.Active) { }        // (outside a match: Muse's start of a normal play - MuseStart, before this)
                else
                {
                    // the part (male_part; the lesbian format: giver) is an absolute side; omitted: the speaker's other side - the
                    // same on both apps (the package is the speaker's)
                    string other = h.Match.SideOf(Speaker) == "host" ? "guest" : "host";
                    string male;
                    object an;
                    bool anal = pl.TryGetValue("anal", out an) && an is bool && (bool)an;
                    object sa;
                    if (h.Match.Lesbian)
                    {
                        // (no male part: the giver - the part the original's male motion plays - is one of the two women)
                        if (pl.ContainsKey("male_part")) Refuse("play.male_part", FieldValue(pl["male_part"]), "lesbian_format");
                        if (pl.ContainsKey("strapon")) Refuse("play.strapon", FieldValue(pl["strapon"]), "lesbian_format");
                        male = Json.Str(pl, "giver", other);
                        h.Match.StraponAgreed = false;
                    }
                    else
                    {
                        male = Json.Str(pl, "male_part", other);
                        h.Match.StraponAgreed = pl.TryGetValue("strapon", out sa) && sa is bool && (bool)sa;      // (agreed: a strapon, else drawn futanari)
                    }
                    string err = play.Begin(start, "muse", Json.Str(pl, "spot", null), male == h.Match.Side, anal);
                    if (err == "not_in_format") Refuse("play.start", start, err, h.Match.FormatPlays());
                    else if (err.Length > 0) Refuse("play.start", start, err);
                }
            }
            object e;
            if (h.Match.Active && pl.TryGetValue("end", out e) && e is bool && (bool)e && play.Active) play.End("muse");     // (a match's; outside it MuseEnd, before this)
            if (Speaker == "opponent" && play.Active)
                foreach (var f in play.Apply(pl))
                {
                    object v;
                    pl.TryGetValue(f.Key.Substring(5), out v);
                    Refuse(f.Key, FieldValue(v), f.Value);
                }
        }

        // Muse's start or change of a normal play (MUSE_GUIDE 3.7; GPT's handoff of Alpha PR #1): Muse sends it only when the
        // user asked for it in the chat (Muse's rule); the app checks it again as the block shows - the package aimed at this
        // launch and character, its request not seen before (sent again: refused, so nothing runs twice), the play's serial
        // still the one Muse saw (the user changed or stopped the play meanwhile: refused - no late start after a stop), the
        // room, the play's availability as the panel shows it (Availability), the spot. Started: play_started (source "muse",
        // the request), then the block's other play fields (pace and the rest) on the new play; refused: block_refused with the
        // request and none of the block's play fields - the play as it was. The panel's start, change and end stay the app's.
        readonly HashSet<string> startRequests = new HashSet<string>();
        readonly Queue<string> startOrder = new Queue<string>();

        // the play_serial changes this package's own blocks made (a move that ended a play, an action, a start, play.end): Muse's
        // basis is the number it saw before it sent the package, so the start's check counts them in - and nothing else (the
        // user's change meanwhile, the app's own: refused)
        int ownSerial;

        void Own(Action a)
        {
            int before = h.Play.Serial;
            a();
            ownSerial += h.Play.Serial - before;
        }

        bool MuseStart(Dictionary<string, object> pl, bool withMove)
        {
            var play = h.Play;
            string id = Json.Str(pl, "start", "");
            string req = Json.Str(pl, "request", null);
            Func<string, string, List<object>, bool> no = (reason, detail, avail) => { RefuseStart(id, req, reason, detail, avail); return false; };
            if (string.IsNullOrEmpty(req)) return no("no_request", "", null);
            if (startRequests.Contains(req)) return no("duplicate_request", "", null);
            startRequests.Add(req);                  // (refused or not: a late copy of it never runs)
            startOrder.Enqueue(req);
            while (startOrder.Count > 500) startRequests.Remove(startOrder.Dequeue());
            object ev;
            if (pl.TryGetValue("end", out ev) && ev is bool && (bool)ev) return no("with_end", "", null);
            if (withMove) return no("with_move", h.Stage.Location, null);
            string lid = Json.Str(Pkg, "launch", null), cid = Json.Str(Pkg, "character", null);
            if (lid == null || cid == null) return no("untargeted", "", null);
            if (lid != Launch.Id) return no("old_launch", Launch.Id, null);
            if (cid != h.CharacterId()) return no("other_character", h.CharacterId(), null);
            if (!Json.Has(pl, "basis")) return no("no_basis", "", null);
            // (the number Muse saw before the package, and since then only the package's own changes)
            if ((int)Json.Num(pl, "basis") + ownSerial != play.Serial) return no("stale_basis", Convert.ToString(play.Serial), null);
            string loc = Json.Str(pl, "location", null);
            if (loc == null) return no("no_location", "", null);
            if (h.Outing.Out) return no("she_is_out", "", null);
            if (h.Presence.Apart) return no("apart", "", null);
            if (loc != h.Stage.Location) return no("other_location", h.Stage.Location, null);
            var row = play.Row(id);
            if (row == null) return no("unknown_play", "", null);
            object rv, an;
            bool rev = pl.TryGetValue("reverse", out rv) && rv is bool && (bool)rv;
            bool anal = pl.TryGetValue("anal", out an) && an is bool && (bool)an;
            var av = play.Availability(row);
            if (!(bool)av[rev ? "reversed_ok" : "ok"]) return no("not_available", Json.Str(av, rev ? "reversed_reason" : "reason", ""), null);
            if (anal && !(bool)av["anal"]) return no("no_anal", "", null);
            string spot = Json.Str(pl, "spot", null);
            if (spot != null)
            {
                bool fits = false;
                foreach (var s in play.Spots(row)) if (s.Id == spot || s.Aliases.Contains(spot)) fits = true;
                if (!fits) return no("spot_not_fit", "", (List<object>)av["spots"]);
            }
            string err = play.Begin(id, "muse", spot, rev, anal, req);
            if (err.Length > 0) return no("not_started", err, null);
            return true;
        }

        static bool IsTrue(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v is bool && (bool)v; }

        // Muse's end of a normal play (PR #12 adopted; before, play.end outside a match ended whatever play was on, unchecked - a
        // late one could stop a play the user had started since): only on the user's wish in the chat (Muse's rule); checked as
        // the block shows, as a start is - aimed at this launch and character, its request not seen before, the play_serial
        // Muse saw (the package's own changes counted in), a play on. Ended: play_ended (reason "muse", the request); refused:
        // block_refused (field play.end) and none of the block's play fields.
        bool MuseEnd(Dictionary<string, object> pl)
        {
            var play = h.Play;
            string req = Json.Str(pl, "request", null);
            Func<string, string, bool> no = (reason, detail) =>
            {
                h.Bridge.Event("block_refused", new Dictionary<string, object> { { "field", "play.end" }, { "value", "true" }, { "reason", reason },
                    { "available", new List<object>() }, { "request", req }, { "detail", detail } });
                return false;
            };
            if (string.IsNullOrEmpty(req)) return no("no_request", "");
            if (startRequests.Contains(req)) return no("duplicate_request", "");
            startRequests.Add(req);
            startOrder.Enqueue(req);
            while (startOrder.Count > 500) startRequests.Remove(startOrder.Dequeue());
            string lid = Json.Str(Pkg, "launch", null), cid = Json.Str(Pkg, "character", null);
            if (lid == null || cid == null) return no("untargeted", "");
            if (lid != Launch.Id) return no("old_launch", Launch.Id);
            if (cid != h.CharacterId()) return no("other_character", h.CharacterId());
            if (!Json.Has(pl, "basis")) return no("no_basis", "");
            if ((int)Json.Num(pl, "basis") + ownSerial != play.Serial) return no("stale_basis", Convert.ToString(play.Serial));
            if (!play.Active) return no("no_play", "");
            play.End("muse", true, true, req);
            return true;
        }

        void RefuseStart(string id, string req, string reason, string detail, List<object> available)
        {
            h.Bridge.Event("block_refused", new Dictionary<string, object> { { "field", "play.start" }, { "value", id }, { "reason", reason },
                { "available", available ?? new List<object>() }, { "request", req }, { "detail", detail } });
        }

        // a block's pose (the original's talk motions, Talk): a match's talk - not in a play - on the speaker's body
        void Pose(string name, ChaControl cha)
        {
            if (name == null) return;
            if (!h.Match.Active) { Refuse("pose", name, "not_in_match"); return; }
            if (h.Play.Active) { Refuse("pose", name, "in_play"); return; }
            if (!h.Talk.Known(name)) { Refuse("pose", name, "unknown_pose", h.Talk.Names()); return; }
            if (cha != null && !h.Talk.Play(cha, name)) Refuse("pose", name, "not_played");
        }

        static string FieldValue(object v) { return v is bool ? ((bool)v ? "true" : "false") : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }

        void Refuse(string field, string value, string reason, List<object> available = null)
        {
            h.Bridge.Event("block_refused", new Dictionary<string, object> { { "field", field }, { "value", value }, { "reason", reason },
                { "available", available ?? new List<object>() } });
        }

        // 自動送り (the panel's switch; stage.json advance.auto at the start)
        public void SetAuto(bool on)
        {
            Auto = on;
            waitLeft = on && Busy ? (Text.Length * perChar + baseSec) / Pace : -1f;
        }

        void End(bool rest)
        {
            Pkg = null;
            Blocks = null;
            Index = -1;
            Text = "";
            Played++;
            if (rest && !h.Play.Active && !h.Outing.Out)      // (out on her own: her motion stays)
            {
                if (!h.Match.Active && !h.Stage.Leaking) h.Stage.ApplyScene(Json.Str(h.Stage.Cfg, "idle_scene", "idle"), 1, 1f);      // (a match: her talk pose stays; a leak: its motion; her climax in the talk: after it)
                // (the package's end face only when Muse holds none; held, her face of the package stays - a climax's face put back to it)
                Action face = () =>
                {
                    var st = h.Stage;
                    if (st.MuseLabel.Length == 0) st.ApplyFace(Json.Str(st.Cfg, "package_end_face", "service_smile"));
                    else if (st.AppFace) st.RestoreFace(st.MuseLabel);
                    h.Touch.PackageFace();
                };
                if (h.Stage.Climaxing) h.Stage.AfterClimax = face;
                else face();
            }
        }

        // the state's package (MUSE_GUIDE.md 7.4): playing, and while it plays its session / turn / block / blocks / beat
        public Dictionary<string, object> Info()
        {
            if (!Busy) return new Dictionary<string, object> { { "playing", false }, { "session", null }, { "turn", null }, { "block", null }, { "blocks", null }, { "beat", null },
                { "monologue", false }, { "monologue_left", MonologueLeft }, { "queued", pending.Count } };
            return new Dictionary<string, object> { { "playing", true }, { "session", Json.Str(Pkg, "session") }, { "turn", (int)Json.Num(Pkg, "turn") },
                { "block", Index + 1 }, { "blocks", Blocks.Count }, { "beat", Json.Has(Pkg, "beat") ? (object)(int)Json.Num(Pkg, "beat") : null },
                { "monologue", Monologue }, { "monologue_left", MonologueLeft }, { "queued", pending.Count } };
        }
    }
}
