using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace MuseStudio
{
    // The free camera remembered per room (the user's request, PR #11): each time the user's own drag, wheel or keys moved it
    // (CamInput.UserMovedAt - not Muse's presets, not a play's camera, not the match's), the view it left is taken at once with
    // its room, and written once the moves have settled (a room change or a preset meanwhile is not what is kept - GPT's
    // review of PR #21; the end writes it too); at the start, and on coming into a room again, that view (a camera Muse's
    // block asks for still comes after it).
    // Room-relative (the kept rooms sit apart in the scene: Rooms.OffsetOf). UserData/MuseStudio/cameras.json (the checks:
    // their own). A small file written once a move settles: no weight to speak of.
    public class CameraMemory
    {
        readonly string path;
        readonly Dictionary<string, object> rooms;
        float seenAt = -1f;                 // (the user's move last taken)
        string pendingRoom;                 // the view the user's last move left, its room with it: written once settled
        Studio.CameraControl.CameraData pendingCam;
        const float Settle = 0.6f;
        public bool Saving = true;          // (the checks: off but in its own check - their real drags are not the user's)

        public CameraMemory(string path)
        {
            this.path = path;
            Dictionary<string, object> doc = null;
            try { if (File.Exists(path)) doc = Json.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>; }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: cameras " + path + ": " + e.Message); }
            rooms = Json.Obj(doc, "rooms") ?? new Dictionary<string, object>();
        }

        static Studio.CameraControl Cam { get { return Singleton<Studio.Studio>.Instance != null ? Singleton<Studio.Studio>.Instance.cameraCtrl : null; } }

        public void Tick()
        {
            float t = CamInput.UserMovedAt;
            if (!Saving || t < 0f) return;
            if (t > seenAt)
            {
                seenAt = t;
                var h = MuseStudioPlugin.Instance;
                string loc = h.Stage.Location;
                if (h.Stage.Ready && !h.Play.Active && !h.Match.Active && Cam != null && !string.IsNullOrEmpty(loc) && loc == CamInput.UserMovedIn)
                {
                    if (pendingRoom != null && pendingRoom != loc) Flush();      // (another room's move not yet written: written now - GPT's re-review of #25)
                    pendingRoom = loc;
                    pendingCam = Cam.Export();
                }
            }
            if (pendingRoom != null && Time.realtimeSinceStartup - t >= Settle) Flush();
        }

        public bool Has(string loc) { return Json.Obj(rooms, loc) != null; }

        // the room's view forgotten (the next time: the presets again)
        public void Forget(string loc)
        {
            if (pendingRoom == loc) pendingRoom = null;
            if (!rooms.Remove(loc)) return;
            Write();
        }

        // every room's view forgotten (the settings' button - the user's request, PR #23): the presets again everywhere
        public void ResetAll()
        {
            pendingRoom = null;
            seenAt = CamInput.UserMovedAt;          // (a move made before it is not taken after it)
            rooms.Clear();
            Write();
        }

        // the view taken from the user's last move, written now (the moves settled; the end of the game - not lost)
        public void Flush()
        {
            if (pendingRoom == null) return;
            var cd = pendingCam;
            Vector3 off = MuseStudioPlugin.Instance.Rooms.OffsetOf(pendingRoom);
            rooms[pendingRoom] = new Dictionary<string, object> { { "pos", V(cd.pos - off) }, { "rotate", V(cd.rotate) }, { "distance", V(cd.distance) },
                { "fov", (double)cd.parse } };
            pendingRoom = null;
            Write();
        }

        void Write()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Json.Write(new Dictionary<string, object> { { "rooms", rooms } }), new UTF8Encoding(false));
            }
            catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: cameras " + path + ": " + e.Message); }
        }

        // the room's remembered view, at once (false: none - the caller's preset stays)
        public bool Recall(string loc)
        {
            if (pendingRoom == loc) Flush();            // (its newest view, not yet written: that one)
            var r = Json.Obj(rooms, loc);
            if (r == null || Cam == null) return false;
            Vector3 off = MuseStudioPlugin.Instance.Rooms.OffsetOf(loc);
            var cd = Cam.Export();
            cd.pos = P(Json.Arr(r, "pos"), cd.pos - off) + off;
            cd.rotate = P(Json.Arr(r, "rotate"), cd.rotate);
            cd.distance = P(Json.Arr(r, "distance"), cd.distance);
            cd.parse = (float)Json.Num(r, "fov", cd.parse);
            MuseStudioPlugin.Instance.Stage.ImportCamera(cd);        // (a preset's move under way stops: this view stays)
            return true;
        }

        static List<object> V(Vector3 v) { return new List<object> { (double)v.x, (double)v.y, (double)v.z }; }

        static Vector3 P(List<object> a, Vector3 fallback)
        {
            if (a == null || a.Count < 3) return fallback;
            return new Vector3(Convert.ToSingle(a[0]), Convert.ToSingle(a[1]), Convert.ToSingle(a[2]));
        }
    }
}
