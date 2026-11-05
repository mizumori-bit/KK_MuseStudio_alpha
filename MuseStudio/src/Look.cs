using UnityEngine;

namespace MuseStudio
{
    // Her face to the user (the trial run of 2026-11-04: her eyes followed him, her face did not - the original's talk, whose
    // camera stands before her, keeps the neck by the motion). The original's free roam (ActionGame.Chara.NPC, Base.
    // LookForTarget) turns her head to the player - the neck's pattern 1 (TARGET) at him - when he is close enough to speak
    // to her (his notice area: 0.5 m round him reaching her body's 0.2 m), she is not angry, they are friends or more
    // (relation >= 1) and her motion allows it; else the neck by the motion (3). Here the conversation goes on all the time
    // they are together, so being together - the same room, she knows he is there (not peeping unseen) and thinks him present
    // - stands for his notice area, within stage.json look.max_distance of her head; the relation is Relationship.Stage
    // (Muse's affection, else the talk setting) >= look.min_stage. Not during a play (the position's own lists, HEyeNeck),
    // nor a touch's reaction pose (the original's talk: 3), nor her climax in the talk (the motion's own head).
    // The neck never bends past what a neck can: the original's own pattern 1 does the turning (NeckLookCalcVer2) - beyond
    // its field of view (90 degrees from her upper body's front, 100 once she follows) she looks ahead again, within it each
    // of the neck and the head bends 40 degrees at most to a side and 25 up / down (80 / 50 in all), in place of the motion's
    // own neck (not added to it), at the pattern's speed.
    // In a match (and its replay) the two look at each other, not at the user (the user's note of 2026-11-04: the user only
    // watches): her eyes and face at the opponent's head, the opponent's at hers - the same patterns (1), their targets the
    // other's head, held every frame (a face's eyes may set them meanwhile); in a play the position's lists as ever. After
    // it, at the camera again.
    public class Look
    {
        readonly MuseStudioPlugin h;
        public bool Turned;          // her face to him now
        public string Why = "";      // why it is / is not (checks)

        bool atEachOther;

        public Look(MuseStudioPlugin host) { h = host; }

        public void Tick()
        {
            var st = h.Stage;
            if (!st.Ready || st.Heroine == null || st.Cha == null || h.Play.Active || Camera.main == null) return;
            var cha = st.Cha;
            if (h.Match.Active && st.Partner != null && st.Partner.charInfo != null)
            {
                Turned = false;
                Why = "match";
                Aim(st.Heroine, cha, st.Partner.charInfo);
                Aim(st.Partner, st.Partner.charInfo, cha);
                atEachOther = true;
                return;
            }
            if (atEachOther)
            {
                atEachOther = false;
                if (cha.eyeLookCtrl != null) cha.eyeLookCtrl.target = Camera.main.transform;      // (Studio's pattern 1 keeps a target it is given)
                if (cha.neckLookCtrl != null) cha.neckLookCtrl.target = Camera.main.transform;
            }
            Turned = Wants(out Why);
            if (Turned && cha.neckLookCtrl != null && cha.neckLookCtrl.target != Camera.main.transform) cha.neckLookCtrl.target = Camera.main.transform;
            int want = Turned ? 1 : 3;
            if (cha.fileStatus.neckLookPtn != want) st.Heroine.ChangeLookNeckPtn(want);
        }

        // eyes and face (patterns 1) at the other's head
        static void Aim(Studio.OCIChar oci, ChaControl me, ChaControl other)
        {
            var head = other.objHeadBone != null ? other.objHeadBone.transform : null;
            if (head == null || oci == null) return;
            if (me.eyeLookCtrl != null && me.eyeLookCtrl.target != head) me.eyeLookCtrl.target = head;
            if (me.fileStatus.eyesLookPtn != 1) oci.ChangeLookEyesPtn(1);
            if (me.neckLookCtrl != null && me.neckLookCtrl.target != head) me.neckLookCtrl.target = head;
            if (me.fileStatus.neckLookPtn != 1) oci.ChangeLookNeckPtn(1);
        }

        bool Wants(out string why)
        {
            var st = h.Stage;
            var cfg = Json.Obj(st.Cfg, "look");
            if (st.ReactUntil >= 0f) { why = "reaction"; return false; }
            if (st.Climaxing) { why = "climax"; return false; }
            if (!h.Presence.Together) { why = h.Presence.Hidden ? "unseen" : "apart"; return false; }
            if (!h.Presence.Perceived || h.Presence.BeliefAway) { why = "away"; return false; }
            int stage = h.Relationship.Stage((int)Json.Num(Json.Obj(h.Touch.Doc, "talk"), "stage", 2));
            if (stage < (int)Json.Num(cfg, "min_stage", 1)) { why = "relation"; return false; }
            var head = st.Cha.objHeadBone != null ? st.Cha.objHeadBone.transform.position : st.Heroine.oiCharInfo.changeAmount.pos + Vector3.up * 1.4f;
            if (Vector3.Distance(head, Camera.main.transform.position) > Json.Num(cfg, "max_distance", 5.0)) { why = "far"; return false; }
            why = "together";
            return true;
        }
    }
}
