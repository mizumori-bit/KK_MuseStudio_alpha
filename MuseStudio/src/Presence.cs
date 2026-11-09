using System.Collections.Generic;

namespace MuseStudio
{
    // The user's presence (DECISIONS "Muse 側の資料との読み合わせ"): the 在席 / 離席 toggle at the top left is the user's real
    // presence, and Muse is given only what she can know of it (her-perspective presence): a toggle reaches her (the state's
    // presence.user_present, the event presence_changed) while the two are in the same room; one made apart reaches her when
    // she comes back to the user's room or the user comes into hers. The user's room is the camera's (the stage's location);
    // hers is where she went on her own (HerAt: the toilet, the shower) or the stage's. Muse's own judgement that he is away
    // is apart from it: a block's belief {"user_away": true / false}, the state's belief - false at the start and on a
    // character switch. Coming back (the toggle to 在席) drops the rest of her monologue (BlockPlayer.DropMonologue).
    public class Presence
    {
        readonly MuseStudioPlugin h;
        public bool Present = true;           // the toggle: the user's real presence
        public bool Perceived = true;         // what she knows of it
        public bool BeliefAway;               // Muse's: she thinks he is away
        public string HerAt = "";             // where she is apart from the stage (on her own, or left when he went alone; empty: with the stage)
        public string Cause = "";             // (who moved last: told with together_changed)
        public bool Hidden;                   // the user is in her room unseen (peeping)
        bool wasTogether = true;

        public Presence(MuseStudioPlugin host) { h = host; }

        public string UserRoom { get { return h.Stage.Location; } }

        public string HerRoom { get { return HerAt.Length > 0 ? HerAt : h.Stage.Location; } }

        public bool Together { get { return HerRoom == UserRoom && !Hidden; } }

        public void Toggle(bool present)
        {
            if (present == Present) return;
            Present = present;
            if (present) h.Blocks.DropMonologue("user_returned");
            Sync("seen");
        }

        void Sync(string cause)
        {
            if (!Together || Perceived == Present) return;
            Perceived = Present;
            h.Bridge.Event("presence_changed", new Dictionary<string, object> { { "user_present", Perceived }, { "cause", cause }, { "presence", Info() } });
        }

        public void Tick()
        {
            bool t = Together;
            if (t && !wasTogether) Sync("met");      // she came back to his room / he came into hers
            // he left her / came back to her, she came to him (the trial run of 2026-11-04: he moves alone, she stays) - not while
            // she is out on her own (nothing of where he is reaches her then: Outing)
            if (t != wasTogether && !h.Outing.Out && h.Stage.Ready)
            {
                h.Bridge.Event("together_changed", new Dictionary<string, object> { { "together", t }, { "cause", Cause.Length > 0 ? Cause : (t ? "met" : "parted") },
                    { "her_location", h.Stage.LocationInfo(HerRoom) }, { "presence", Info() } });
                Cause = "";                          // (kept till the change it names is told: a move takes some frames)
            }
            wasTogether = t;
        }

        public bool Apart { get { return HerAt.Length > 0 && !h.Outing.Out; } }

        // a block's belief (only user_away so far: true / false; other keys are not known)
        public void Believe(Dictionary<string, object> b)
        {
            object v;
            if (b.TryGetValue("user_away", out v) && v is bool) BeliefAway = (bool)v;
        }

        public void ResetBelief() { BeliefAway = false; }

        public Dictionary<string, object> Info()
        {
            // location_known: the state's location is his room now (false while she is out and does not see him: the room she
            // left him in - #101 item 1)
            return new Dictionary<string, object> { { "user_present", Perceived }, { "together", Together }, { "location_known", !h.Outing.Out || h.Outing.SeesHim } };
        }

        public Dictionary<string, object> BeliefInfo() { return new Dictionary<string, object> { { "user_away", BeliefAway } }; }
    }
}
