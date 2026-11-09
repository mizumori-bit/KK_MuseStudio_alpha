using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // The bath (#70 adopted: the owner's decisions of 2026-11-09 and the answers to the points asked before it; MUSE_GUIDE
    // 3.9). Muse's action shower_together takes the two to the bath room; there both are nude (her accessories off too, but the
    // hair's) and no play begins - they face each other as in a room, and she knows where they are (bath_started) and talks.
    // The plays there are chosen as anywhere (the user's panel, Muse's play.start) and keep the two nude: a play's end or change
    // does not dress her. Her clothes stay as they are meanwhile - the panel's 服 greyed, Muse's clothes / coordinate refused
    // (in_bath) - and so do the place, her outings, the character, a 献精室 run, a match (in_bath). It ends by the user's
    // プレイを終える (in a play) or 入浴を終える (none): the two back to the room they left (from the bath room itself: there),
    // the outfit she has on worn in full, her accessories back - then bath_ended. Muse's play.end ends the play only. A move in
    // that fails changes nothing: no nude, neither event, block_refused (action shower_together). Not kept over a restart (the
    // stage comes back dressed)
    public class Bath
    {
        readonly MuseStudioPlugin h;
        public bool Active;                   // in the bath (arrived, nude) until dressed again
        public bool Going, Leaving;           // on the way in (nothing changed yet) / on the way out
        public string Room = "", ReturnRoom = "", Since = "";
        Dictionary<int, bool> accBefore;      // her accessories taken off for it
        float startedAt;
        public string RoomForCheck;           // (checks) another room for the bath - one that fails to load

        public Bath(MuseStudioPlugin host) { h = host; }

        // anything of the bath going on: what it holds still (the place, her clothes, the character...)
        public bool Busy { get { return Active || Going || Leaving; } }

        // Muse's action shower_together (Urges.Act): "" taken - the rest is told when it is done (action_started and
        // bath_started at the arrival) or refused then (block_refused)
        public string Go(Dictionary<string, object> opts)
        {
            if (Busy) return "in_bath";
            var st = Json.Obj(h.Urges.Doc, "shower_together");
            string room = !string.IsNullOrEmpty(RoomForCheck) ? RoomForCheck : Json.Str(st, "room", "bathroom");
            if (!h.Stage.Reachable(room)) return "no_room";
            // (1.43: no play begins with it - one starts there as anywhere, with its checks: the old play option is told unknown)
            object pv;
            if (opts != null && opts.TryGetValue("play", out pv))
                h.Bridge.Event("block_refused", new Dictionary<string, object> { { "field", "action.play" }, { "value", pv as string }, { "reason", "unknown_field" },
                    { "available", new List<object>() }, { "app_contract", MuseStudioPlugin.Contract } });
            Going = true;
            h.StartCoroutine(GoRoutine(room));
            return "";
        }

        IEnumerator GoRoutine(string room)
        {
            string from = h.Stage.Location;
            // the room in first: one that does not load changes nothing - the play, where they are, her clothes
            if (room != from)
            {
                yield return h.Stage.EnsureRoom(room);
                if (!h.Rooms.Has(room)) { Going = false; Refused("load_failed"); h.RoomNotLoaded(room); yield break; }
            }
            if (h.Play.Active) h.Play.End("action");
            if (room != h.Stage.Location)
            {
                yield return h.MoveRoutine(room, "bath");
                if (h.Stage.Location != room) { Going = false; Refused("move_failed"); yield break; }
            }
            // arrived: both nude (the user's body in a play there: PlayCtl's nude while the bath lasts), she where a room's idle has her
            var cha = h.Stage.Cha;
            for (int k = 0; k < cha.fileStatus.clothesState.Length; k++) cha.SetClothesState(k, 3, false);
            accBefore = BathAccessories.Off(cha);
            Room = room;
            ReturnRoom = from;
            Since = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            startedAt = Time.realtimeSinceStartup;
            Active = true;
            Going = false;
            h.Bridge.Event("action_started", new Dictionary<string, object> { { "action", "shower_together" }, { "location", h.Stage.Location },
                { "her_location", h.Presence.HerRoom } });
            h.Bridge.Event("bath_started", new Dictionary<string, object> { { "room", h.Stage.LocationInfo(Room) }, { "return_room", h.Stage.LocationInfo(ReturnRoom) },
                { "clothes", Clothes.Info(cha) } });
            h.Mood.ActionDone("shower_together");        // (the wish to shower back to 0; today's shower)
            h.Urges.Answered("shower_together");
        }

        void Refused(string why)
        {
            h.Bridge.Event("block_refused", new Dictionary<string, object> { { "field", "action" }, { "value", "shower_together" }, { "reason", why },
                { "available", new List<object>(Urges.Actions) } });
        }

        // the bath's end - the user's プレイを終える (a play on: it ends first) or 入浴を終える: back to the room left, dressed
        public void Finish(string reason)
        {
            if (!Active || Leaving) return;
            Leaving = true;
            h.StartCoroutine(FinishRoutine(reason));
        }

        IEnumerator FinishRoutine(string reason)
        {
            if (h.Play.Active) h.Play.End(reason);
            bool back = ReturnRoom == h.Stage.Location;
            if (!back && h.Stage.Reachable(ReturnRoom))
            {
                yield return h.MoveRoutine(ReturnRoom, "bath");
                back = h.Stage.Location == ReturnRoom;
            }
            // (a room that would not load: dressed where they are - the bath is over all the same)
            var cha = h.Stage.Cha;
            for (int k = 0; k < cha.fileStatus.clothesState.Length; k++) cha.SetClothesState(k, 0, false);
            BathAccessories.Back(cha, accBefore);
            accBefore = null;
            Active = Leaving = false;
            h.Bridge.Event("bath_ended", new Dictionary<string, object> { { "reason", reason }, { "location", h.Stage.LocationInfo(h.Stage.Location) },
                { "return_room", h.Stage.LocationInfo(ReturnRoom) }, { "returned", back }, { "seconds", Math.Round(Time.realtimeSinceStartup - startedAt, 1) },
                { "clothes", Clothes.Info(cha) } });
            Room = ReturnRoom = Since = "";
        }

        // the state's bath: null when none; else where, the room they go back to, since when, and whether on the way out
        public Dictionary<string, object> Info()
        {
            if (!Active) return null;
            return new Dictionary<string, object> { { "phase", Leaving ? "leaving" : "bathing" }, { "room", Room }, { "return_room", ReturnRoom }, { "since", Since } };
        }
    }
}
