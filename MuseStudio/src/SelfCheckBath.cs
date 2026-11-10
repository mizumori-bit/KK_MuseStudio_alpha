using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // The bath's checks (#70 adopted; MUSE_GUIDE 3.9 "入浴") and the pace put back after the idle stop (3.7, the owner's B): a
    // move in that fails changes nothing; shower together - both nude in the bath room, no play, bath_started (the old play
    // option told unknown), today's shower; what waits in it (the place, her clothes, outings, the character, a run, a match);
    // its plays nude, a change of play too, Muse's play.end the play's only; its end by 入浴を終える (a real click) and by
    // プレイを終える - back to the room left (the bath room itself: there), the outfit she has on worn in full, bath_ended. Then
    // the idle stop's pace: put back by her next block shown, not by one with nothing on the screen, not over Muse's pace 0,
    // the user's touch or a change of play
    public partial class SelfCheck
    {
        static readonly string[] BathPlays = { "sonyu_18", "sonyu_07", "aibu_00", "houshi_40" };

        // the first of these that can start here (its row, available, a spot)
        string PlayHere(IEnumerable<string> ids, string not = null)
        {
            var pc = h.Play;
            foreach (var id in ids)
            {
                if (id == not) continue;
                var row = pc.Row(id);
                if (row != null && (bool)pc.Availability(row)["ok"] && pc.Spots(row).Count > 0) return id;
            }
            return null;
        }

        bool Bare()
        {
            var cha = h.Stage.Cha;
            foreach (var cs in cha.fileStatus.clothesState) if (cs != 3) return false;
            return AccShown() == 0;
        }

        // her accessories shown but the hair's (and the kind 120: none)
        int AccShown()
        {
            var cha = h.Stage.Cha;
            int n = 0;
            var parts = cha.nowCoordinate.accessory.parts;
            for (int i = 0; i < Math.Min(parts.Length, cha.fileStatus.showAccessory.Length); i++)
                if (parts[i].type != 120 && !BathAccessories.IsHair(parts[i]) && cha.fileStatus.showAccessory[i]) n++;
            return n;
        }

        bool Dressed()
        {
            var cha = h.Stage.Cha;
            foreach (int k in Clothes.Worn(cha)) if (cha.fileStatus.clothesState[k] != 0) return false;
            return true;
        }

        IEnumerator BathChecks()
        {
            var st = h.Stage;
            var pc = h.Play;
            var bp = h.Blocks;
            var mood = h.Mood;
            var bath = h.Bath;
            if (pc.Active) pc.End("check");
            yield return Wait(0.5f);
            string from = st.Location;
            int accBefore = AccShown();

            // (a) the bath room does not load: nothing changed - no nude, neither event, refused
            {
                var bad = h.Places != null ? h.Places.All.Find(e => !e.Curated && e.Id.StartsWith("orig:") && !h.Rooms.Has(e.Key)) : null;
                bool kept = false;
                string why = "no unloaded room to fail";
                if (bad != null)
                {
                    bath.RoomForCheck = bad.Key;
                    h.Rooms.FailForCheck = bad.Key;
                    int f0 = Refusals("action", "load_failed"), bs0 = EventCount("bath_started"), as0 = EventCount("action_started");
                    var cs0 = (byte[])st.Cha.fileStatus.clothesState.Clone();
                    Send(With(Line("一緒に入ろ？"), "action", "shower_together"));
                    yield return Until(() => Refusals("action", "load_failed") > f0, 15f);
                    yield return Wait(0.6f);
                    bool same = true;
                    for (int k = 0; k < cs0.Length; k++) if (st.Cha.fileStatus.clothesState[k] != cs0[k]) same = false;
                    kept = Refusals("action", "load_failed") == f0 + 1 && EventCount("bath_started") == bs0 && EventCount("action_started") == as0
                           && st.Location == from && same && AccShown() == accBefore && !bath.Busy;
                    var stA = Json.Parse(Http("GET", "/api/state", null)) as Dictionary<string, object>;
                    kept = kept && stA != null && stA.ContainsKey("bath") && stA["bath"] == null;
                    why = "refused " + (Refusals("action", "load_failed") - f0) + " started " + (EventCount("bath_started") - bs0) + " at " + st.Location + " clothes kept " + same;
                    bath.RoomForCheck = null;
                    h.Rooms.FailForCheck = "";
                    h.Rooms.Failed.RemoveAll(f => f.StartsWith(bad.Key + " (check"));      // (the check's own failure: not a kept room's)
                    h.Notices.ExpireForCheck("room_load_failed");
                    bp.Advance(true);
                    yield return Wait(0.4f);
                }
                Check("bath (#70 adopted): a bath room that does not load changes nothing - not nude, no bath_started nor action_started, the place as it was, block_refused (action, load_failed)",
                      kept, why);
            }

            // (b) shower together: both to the bath room, nude, no play begins; bath_started
            int kBath = -1;                          // (a garment off before - dressed in full after the bath)
            foreach (int k in new[] { 6, 4, 5, 7 }) if (kBath < 0 && Clothes.Worn(st.Cha).Contains(k)) kBath = k;
            if (kBath >= 0) h.ChangeClothes(new Dictionary<string, object> { { Clothes.Keys[kBath], "off" } }, "user");
            mood.Values["shower"] = 0.5f;
            string pre = PlayHere(new[] { "aibu_00", "houshi_40", "houshi_00", "aibu_01" });
            string ePre = pre != null ? pc.Begin(pre, "check") : "no play here";
            yield return Wait(0.8f);
            int ps0 = EventCount("play_started"), up0 = Refusals("action.play", "unknown_field");
            bath.HoldForCheck = true;
            Send(With(Line("一緒に入ろ？"), "action", D("kind", "shower_together", "play", "sonyu_18")));
            yield return Until(() => bath.Going && !pc.Active, 20f);
            bool heldIn = bath.Going && !pc.Active;
            string eIn = pre != null ? pc.Begin(pre, "check") : "";
            bp.Advance(true);
            int rsIn = Refusals("play.start", "in_bath"), raIn = Refusals("action", "in_bath");
            SendPkg(KTags(), With(With(Line("先にしよ？"), "play", D("start", pre ?? "aibu_00", "request", "bath-in-" + Time.frameCount, "basis", pc.Serial, "location", st.Location)),
                "action", "masturbation"));
            yield return Until(() => bp.Busy && bp.Text == "先にしよ？", 10f);
            yield return Wait(0.6f);
            bool refusedIn = heldIn && !pc.Active && eIn.Contains("bath") && Refusals("play.start", "in_bath") == rsIn + 1 && Refusals("action", "in_bath") == raIn + 1;
            bp.Advance(true);
            bath.HoldForCheck = false;
            yield return Until(() => bath.Active, 30f);
            yield return Wait(1.0f);
            Check("bath (GPT's post-review of #124): on the way in no play begins - a play on before it ended, then his list's start, Muse's play.start and her masturbation refused (in_bath) while it moves",
                  ePre.Length == 0 && refusedIn && EventCount("play_started") == ps0, pre + ": " + ePre + " held " + heldIn + " his " + eIn + " refused " + refusedIn);
            var bs = LastEvent("bath_started");
            var stB = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "bath");
            Check("bath (#70 adopted): shower together - both in the bath room nude (her accessories off but the hair's), no play begun, bath_started (the room, the room to go back to), the state's bath",
                  bath.Active && st.Location == "bathroom" && Bare() && !pc.Active && EventCount("play_started") == ps0 && bs != null
                  && Json.Str(Json.Obj(bs, "room"), "key") == "bathroom" && Json.Str(Json.Obj(bs, "return_room"), "key") == from
                  && Json.Str(stB, "phase") == "bathing" && Json.Str(stB, "return_room") == from && Json.Str(LastEvent("action_started"), "action") == "shower_together",
                  st.Location + " bare " + Bare() + " play " + pc.Active + " " + Json.Write(bs) + " / " + Json.Write(stB));
            Check("bath: the action's old play option is told unknown (action.play, unknown_field) - the bath begins all the same; the wish to shower back to 0 at the arrival, today's shower",
                  Refusals("action.play", "unknown_field") == up0 + 1 && mood.Values["shower"] < 0.001f && !h.Urges.Pending("shower"), mood.Values["shower"].ToString("F3"));
            yield return Shot("16_bath_arrived.png");
            bp.Advance(true);
            yield return Wait(0.4f);

            // (c) what waits in the bath: refused in_bath (Muse), not done (his panel), nothing changed
            {
                var cs0 = (byte[])st.Cha.fileStatus.clothesState.Clone();
                int rc = Refusals("clothes", "in_bath"), rco = Refusals("coordinate", "in_bath"), rl = Refusals("location", "in_bath"), ra = Refusals("action", "in_bath"),
                    rm = Refusals("match.start", "in_bath");
                string coordId = h.Coords.List.Count > 0 ? h.Coords.List[0].Id : "none";
                string elsewhere = from != "bathroom" ? from : "myroom";
                var blk = With(With(With(With(Line("服、着ちゃおうかな。"), "clothes", D("all", "on")), "coordinate", coordId), "location", elsewhere), "action", "toilet");
                Send(blk);
                yield return Until(() => bp.Busy && bp.Text == "服、着ちゃおうかな。", 15f);
                yield return Wait(0.6f);
                bool museRefused = Refusals("clothes", "in_bath") == rc + 1 && Refusals("coordinate", "in_bath") == rco + 1 && Refusals("location", "in_bath") == rl + 1
                                   && Refusals("action", "in_bath") == ra + 1;
                bp.Advance(true);
                // his own: the panel's 服 (refused quietly), his move, a switch, a run, a match
                h.ChangeClothes(new Dictionary<string, object> { { "all", "on" } }, "user");
                h.MoveTo(elsewhere, "user");
                yield return Wait(1.5f);
                string other = null;
                foreach (var c in h.Chars.List()) if (!string.Equals(c.Key, h.Chars.CardPath, StringComparison.OrdinalIgnoreCase)) { other = c.Key; break; }
                string sw = other != null ? h.Chars.Switch(other, "check") : "in the bath";
                string ks = h.Kensei.CannotStart(10);
                h.Match.Start(D("room", "check-bath", "side", "host"));
                yield return Wait(0.3f);
                bool same = true;
                for (int k = 0; k < cs0.Length; k++) if (st.Cha.fileStatus.clothesState[k] != cs0[k]) same = false;
                Check("bath: what would change the place, her clothes, her outings, the character, a run or a match waits - Muse's clothes / coordinate / location / action refused in_bath, his 服 and his move not done, a switch / 献精室 / a match refused; still nude in the bath room",
                      museRefused && sw == "in the bath" && ks == "in_bath" && h.Coords.Blocked(false) == "in_bath" && Refusals("match.start", "in_bath") == rm + 1 && !h.Match.Active && !h.Match.Loading
                      && same && Bare() && st.Location == "bathroom" && bath.Active,
                      "muse " + museRefused + " switch " + sw + " run " + ks + " coord " + h.Coords.Blocked(false) + " match " + (Refusals("match.start", "in_bath") - rm) + " at " + st.Location + " clothes kept " + same);
            }

            // (d) its plays: nude, a change of play too (#70's first bug), Muse's play.end the play's only
            string p1 = PlayHere(BathPlays), p2 = PlayHere(BathPlays, p1);
            {
                string e1 = p1 != null ? pc.Begin(p1, "check") : "no play here";
                yield return Wait(1.0f);
                bool nude1 = pc.Active && Bare() && pc.Info()["player_clothes"] as string == "nude";
                string e2 = p2 != null ? pc.Begin(p2, "check") : "no second play";
                yield return Wait(1.0f);
                bool nude2 = pc.Active && Bare() && pc.Info()["player_clothes"] as string == "nude";
                Check("bath: a play there keeps the two nude, and so does a change of play (the bath's clothes are not put on by it - #70's first bug)",
                      e1.Length == 0 && e2.Length == 0 && nude1 && nude2, p1 + ": " + e1 + " " + nude1 + " / " + p2 + ": " + e2 + " " + nude2);
                int be0 = EventCount("bath_ended");
                SendPkg(KTags(), With(Line("ここまでにしよ。"), "play", D("end", true, "request", "bath-end-" + Time.frameCount, "basis", pc.Serial)));
                yield return Until(() => !pc.Active, 15f);
                yield return Wait(0.8f);
                var pe = LastEvent("play_ended");
                Check("bath: Muse's play.end ends the play only - the bath goes on (still nude in the bath room, no bath_ended), and the play adds no wish to shower",
                      !pc.Active && Json.Str(pe, "reason") == "muse" && bath.Active && Bare() && st.Location == "bathroom" && EventCount("bath_ended") == be0 && mood.Values["shower"] < 0.001f,
                      Json.Str(pe, "reason") + " bath " + bath.Active + " bare " + Bare() + " shower " + mood.Values["shower"].ToString("F3"));
                bp.Advance(true);
            }

            // (e) 入浴を終える (no play; a real click): back to the room left, dressed in full, accessories back, bath_ended
            {
                yield return Wait(0.6f);
                bath.HoldForCheck = true;
                yield return RealInput.Click(Btn(h.UiForCheck, "入浴を終える"));
                string err = RealInput.LastError;
                if (err.Length > 0 && bath.Active && !bath.Leaving) bath.Finish("user");
                yield return Until(() => bath.Leaving, 10f);
                bool heldOut = bath.Leaving;
                string eOut = p1 != null ? pc.Begin(p1, "check") : "";
                int rsOut = Refusals("play.start", "in_bath");
                SendPkg(KTags(), With(Line("もう一回？"), "play", D("start", p1 ?? "aibu_00", "request", "bath-out-" + Time.frameCount, "basis", pc.Serial, "location", st.Location)));
                yield return Until(() => bp.Busy && bp.Text == "もう一回？", 10f);
                yield return Wait(0.6f);
                bool refusedOut = heldOut && !pc.Active && eOut.Contains("bath") && Refusals("play.start", "in_bath") == rsOut + 1;
                bp.Advance(true);
                bath.HoldForCheck = false;
                yield return Until(() => !bath.Busy, 30f);
                yield return Wait(1.0f);
                Check("bath (GPT's post-review of #124): on the way out no play begins either - his list's start and Muse's play.start refused (in_bath) while it moves",
                      refusedOut, "held " + heldOut + " his " + eOut + " refused " + refusedOut);
                var be = LastEvent("bath_ended");
                Check("bath: 入浴を終える (a real click, no play on) - the two back in the room left, the outfit she has on worn in full (a garment off before too), her accessories back, bath_ended (returned), the state's bath null",
                      err.Length == 0 && !bath.Busy && st.Location == from && Dressed() && AccShown() == accBefore && kBath >= 0 && Json.Str(be, "reason") == "user" && True(be, "returned")
                      && Json.Str(Json.Obj(be, "return_room"), "key") == from && Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "bath") == null,
                      err + " at " + st.Location + " dressed " + Dressed() + " acc " + AccShown() + "/" + accBefore + " " + Json.Write(be));
                yield return Shot("16b_bath_ended.png");
                // after it: her clothes change again, and a play here is not the bath's (not nude; its end dresses her as ever)
                int kTop = Clothes.Worn(st.Cha).Contains(0) ? 0 : -1;
                int changed = kTop >= 0 ? h.ChangeClothes(new Dictionary<string, object> { { Clothes.Keys[0], "off" } }, "user").Count : 0;
                if (kTop >= 0) h.ChangeClothes(new Dictionary<string, object> { { Clothes.Keys[0], "on" } }, "user");
                string pre2 = PlayHere(new[] { "aibu_00", "houshi_40", "houshi_00", "aibu_01" });
                string eAfter = pre2 != null ? pc.Begin(pre2, "check") : "no play here";
                yield return Wait(1.0f);
                bool notNude = pc.Active && !Bare();
                if (pc.Active) pc.End("check");
                yield return Wait(0.6f);
                Check("bath: after it nothing of it is left - her clothes change again (the panel's 服), a play there is not nude, its end dresses her as ever",
                      changed > 0 && eAfter.Length == 0 && notNude && !pc.Active && Dressed(), "changed " + changed + " " + pre2 + ": " + eAfter + " not nude " + notNude + " dressed " + Dressed());
            }

            // (f) chosen in the bath room itself: it stays there; プレイを終える in a play ends the bath
            {
                h.MoveTo("bathroom", "check");
                yield return Until(() => st.Location == "bathroom" && !st.Moving, 20f);
                yield return Wait(0.5f);
                int lc0 = EventCount("location_changed");
                Send(With(Line("ここで一緒に浴びよ。"), "action", "shower_together"));
                yield return Until(() => bath.Active, 20f);
                yield return Wait(0.6f);
                bool hereBack = bath.ReturnRoom == "bathroom" && EventCount("location_changed") == lc0 && Bare();
                bp.Advance(true);
                string e = p1 != null ? pc.Begin(p1, "check") : "no play";
                yield return Wait(1.2f);
                yield return RealInput.Click(Btn(h.UiForCheck, "プレイを終える"));
                string err = RealInput.LastError;
                yield return Until(() => !bath.Busy, 20f);
                yield return Wait(0.8f);
                var be = LastEvent("bath_ended");
                Check("bath: chosen in the bath room - it goes back there; プレイを終える in its play (a real click) ends the play and the bath: dressed where they are (bath_ended returned, the bath room)",
                      hereBack && e.Length == 0 && err.Length == 0 && !pc.Active && !bath.Busy && st.Location == "bathroom" && Dressed() && Json.Str(LastEvent("play_ended"), "reason") == "user"
                      && True(be, "returned") && Json.Str(Json.Obj(be, "location"), "key") == "bathroom",
                      "here " + hereBack + " " + e + " " + err + " at " + st.Location + " dressed " + Dressed() + " " + Json.Write(be));
                h.MoveTo(from, "check");
                yield return Until(() => st.Location == from && !st.Moving, 20f);
                yield return Wait(0.5f);
            }
        }

        // the idle stop's pace put back by her next block (#70 adopted: B)
        IEnumerator ResumeChecks()
        {
            var pc = h.Play;
            var bp = h.Blocks;
            if (pc.Active) pc.End("check");
            yield return Wait(0.4f);
            string play = PlayHere(new[] { "aibu_00", "houshi_40", "houshi_00", "aibu_01" });
            string err = play != null ? pc.Begin(play, "check") : "no play here";
            yield return Wait(0.8f);
            // stopped by the idle wait (its 45 seconds passed at once)
            Func<IEnumerator> stop = () => StopIdle();
            pc.SetPace(2, "muse");
            yield return stop();
            var ev = LastEvent("play_pace_changed");
            var stP = Json.Obj(Json.Parse(Http("GET", "/api/state", null)), "play");
            bool told = Json.Str(ev, "source") == "idle" && (int)Json.Num(ev, "resume_pace", -1) == 2 && (int)Json.Num(stP, "resume_pace", -1) == 2 && pc.Pace == 0;
            // a block with nothing on the screen (her inner thought only): not put back; her next line: put back
            Send(Line("（どうしよう……）"));
            yield return Wait(1.2f);
            bool notByEmpty = pc.Pace == 0 && pc.ResumePace == 2;
            bp.Advance(true);
            Send(Line("続けるね。"));
            yield return Until(() => bp.Busy && bp.Text == "続けるね。", 10f);
            yield return Wait(0.5f);
            var evR = LastEvent("play_pace_changed");
            Check("pace after the idle stop (#70 adopted: B) - the stop tells the pace to put back (resume_pace 2, the state's play too); a block with nothing on the screen does not, her next line puts it back (source resume)",
                  err.Length == 0 && told && notByEmpty && pc.Pace == 2 && Json.Str(evR, "source") == "resume" && pc.ResumePace == -1,
                  play + ": " + err + " told " + told + " empty " + notByEmpty + " pace " + pc.Pace + " " + Json.Write(evR));
            bp.Advance(true);
            // Muse's pace 0 wins
            yield return stop();
            Send(With(Line("ちょっと止めとくね。"), "play", D("pace", 0)));
            yield return Until(() => bp.Busy && bp.Text == "ちょっと止めとくね。", 10f);
            yield return Wait(0.5f);
            bool zeroWins = pc.Pace == 0 && pc.ResumePace == -1;
            bp.Advance(true);
            // the user's input after the stop (a touch: aibu; else his pace)
            pc.SetPace(2, "muse");
            yield return stop();
            if (pc.Mode == 0) pc.Touched("chest");
            else pc.SetPace(0, "user");
            Send(Line("触られちゃった。"));
            yield return Until(() => bp.Busy && bp.Text == "触られちゃった。", 10f);
            yield return Wait(0.5f);
            bool userWins = pc.Pace == 0 && pc.ResumePace == -1;
            bp.Advance(true);
            // a change of play after the stop
            pc.SetPace(2, "muse");
            yield return stop();
            string other = PlayHere(new[] { "houshi_40", "houshi_00", "aibu_00", "aibu_01" }, play);
            string e2 = other != null ? pc.Begin(other, "check") : "no other play";
            Send(Line("別のにしよ。"));
            yield return Until(() => bp.Busy && bp.Text == "別のにしよ。", 10f);
            yield return Wait(0.5f);
            bool changeWins = e2.Length == 0 && pc.Pace == 0 && pc.ResumePace == -1;
            bp.Advance(true);
            Check("pace after the idle stop: not put back over Muse's pace 0, the user's input after the stop (a touch), a change of play",
                  zeroWins && userWins && changeWins, "pace 0 " + zeroWins + " user " + userWins + " change " + changeWins + " (" + e2 + ")");
            if (pc.Active) pc.End("check");
            yield return Wait(0.4f);
        }

        IEnumerator StopIdle()
        {
            var pc = h.Play;
            yield return Wait(0.3f);
            pc.LastInput = Time.realtimeSinceStartup - 60f;
            yield return Until(() => pc.Pace == 0, 5f);
            yield return Wait(0.3f);
        }
    }
}
