using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace MuseStudio
{
    // The bridge (bridge/bridge.py, MUSE_GUIDE.md 7): a background thread takes turn packages (GET /api/packages/next) and
    // sends what the stage queues (POST /api/state, /api/events, /api/catalog). Like the Godot app it names its window
    // (X-MMS-Instance / X-MMS-Started): the bridge serves only the newest polling window.
    // The packages' delivery (PR #24): taken with ?ack=1, a package stays at the bridge until this side says received (POST
    // /api/packages/ack) - an answer lost on the way brings it again, known by its _pid and not queued twice; the stage tells
    // the bridge when one begins (started) or is dropped here. A bridge restarted under this launch: its /health shows
    // another launch or phase, so the last launch report goes again.
    public class Bridge
    {
        public readonly string Base;
        public volatile bool Connected;
        public volatile bool Superseded;      // a newer app window took the bridge (409)
        public volatile bool VoiceOff = true; // GET /health "voice"
        public volatile bool Hold;
        public volatile bool Mute;            // a replay plays: no event goes out (Replay)            // not ready to present (before the character is chosen): the bridge keeps the packages
        public string LastError = "";
        public volatile string WatchRoom;     // the match's room: its state polled (closed by the peer -> RoomClosedBy "peer")
        public volatile string RoomClosedBy;
        public volatile Dictionary<string, object> RoomConditions;     // the room's conditions compared by the two bridges
        readonly string instance;
        readonly long started = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        readonly Queue<Dictionary<string, object>> packages = new Queue<Dictionary<string, object>>();
        readonly HashSet<string> receivedIds = new HashSet<string>();       // (this launch's packages taken: one given again is known)
        readonly Queue<string> receivedOrder = new Queue<string>();
        Dictionary<string, object> lastLaunch;                               // (the last launch report: sent again to a restarted bridge)
        public int AgainForCheck;                                            // (checks) packages given again and not queued twice
        // what goes to the bridge, in order. An event carries its own id (X-MMS-Event-Id) so that one sent again after a lost
        // answer is kept once by the bridge (the review of 2026-11-05, point 3); a 5xx is tried again after a while (the
        // order kept), a 4xx is a refusal (dropped, logged), a bridge not reached keeps it (no event lost), and so does a
        // bridge that does not take this app's token yet (401: its tokens made again - kept, tried again; MUSE_GUIDE 7.1. A
        // 403 is a refusal like any 4xx: kept, it would hold back all after it) or one that asks it to slow down (429: past
        // its rate - kept, tried again)
        // (Sending: on the wire now - a newer state does not take it out, and only that item is finished after it: GPT's
        // review of PR #14 - a finished state took the event after it out, unsent)
        class Out { public string Path, Body, Id; public int Tries; public DateTime Next; public bool Sending; }
        readonly Queue<Out> outbox = new Queue<Out>();
        int eventSeq;
        const int MaxTries = 5;
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread thread;
        volatile bool running;
        readonly float pollSeconds;

        public Bridge(string baseUrl, float pollSeconds, string instanceId)
        {
            Base = baseUrl;
            instance = instanceId;
            this.pollSeconds = Math.Max(0.2f, pollSeconds);
        }

        public void Start()
        {
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "MuseStudio bridge" };
            thread.Start();
        }

        public void Stop() { running = false; wake.Set(); }

        public Dictionary<string, object> NextPackage()
        {
            lock (gate) return packages.Count > 0 ? packages.Dequeue() : null;
        }

        public void Post(string path, object body) { Post(path, body, null); }

        void Post(string path, object body, string id)
        {
            lock (gate)
            {
                if (path == "/api/launch" && body is Dictionary<string, object>) lastLaunch = new Dictionary<string, object>((Dictionary<string, object>)body);
                // a newer state replaces one not sent yet
                if (path == "/api/state")
                {
                    var keep = new Queue<Out>();
                    foreach (var o in outbox) if (o.Path != path || o.Sending) keep.Enqueue(o);
                    outbox.Clear();
                    foreach (var o in keep) outbox.Enqueue(o);
                }
                outbox.Enqueue(new Out { Path = path, Body = Json.Write(body), Id = id });
            }
            wake.Set();
        }

        // a package taken from the bridge: queued here once (one given again - its received lost on the way - is known by its
        // _pid), and said received either way
        void Received(Dictionary<string, object> pkg)
        {
            if (pkg == null) return;
            string pid = Json.Str(pkg, "_pid");
            bool again = false;
            lock (gate)
            {
                if (pid != null && receivedIds.Contains(pid)) again = true;
                else
                {
                    if (pid != null)
                    {
                        receivedIds.Add(pid);
                        receivedOrder.Enqueue(pid);
                        if (receivedOrder.Count > 2000) receivedIds.Remove(receivedOrder.Dequeue());
                    }
                    packages.Enqueue(pkg);
                }
            }
            if (again) AgainForCheck++;
            Ack(pid, "received");
        }

        public void ReceiveForCheck(Dictionary<string, object> pkg) { Received(pkg); }

        // a package's delivery stage for the bridge: received (taken here), started (it began), dropped (with the reason)
        // (#70 adopted, 1) block: the block shown (its operations done), blocks: their count - started / finished
        public volatile int OldestUnreadSeconds = -1;     // (#70 adopted, 3) from /health's event_feed

        public void Ack(string pid, string stage, string reason = null, int block = -1, int blocks = -1)
        {
            if (string.IsNullOrEmpty(pid)) return;          // (a replay's, a test's own: not the bridge's)
            var body = new Dictionary<string, object> { { "ids", new List<object> { pid } }, { "stage", stage } };
            if (reason != null) body["reason"] = reason;
            if (block >= 0) body["block"] = block;
            if (blocks >= 0) body["blocks"] = blocks;
            Post("/api/packages/ack", body);
        }

        public Func<string, bool> Quiet;          // (献精室's trial of the plays' finishes: their play events not sent meanwhile)

        public void Event(string type, Dictionary<string, object> fields)
        {
            if (Mute) return;
            if (Quiet != null && Quiet(type)) return;
            var e = fields ?? new Dictionary<string, object>();
            e["type"] = type;
            Post("/api/events", e, instance + "-" + Interlocked.Increment(ref eventSeq));
        }

        void Loop()
        {
            DateTime nextPoll = DateTime.MinValue, nextHealth = DateTime.MinValue, nextRoom = DateTime.MinValue;
            while (running)
            {
                try
                {
                    if (DateTime.UtcNow >= nextHealth)
                    {
                        nextHealth = DateTime.UtcNow.AddSeconds(10);
                        int hc;
                        string h = Call("GET", "/health", null, out hc);
                        if (hc == 200)
                        {
                            var doc = Json.Parse(h);
                            VoiceOff = !(Json.Has(doc, "voice") && ((Dictionary<string, object>)doc)["voice"] is bool && (bool)((Dictionary<string, object>)doc)["voice"]);
                            // (#70 adopted, 3) how long the oldest event Muse has not read has waited (-1: none, or not told)
                            var feed = Json.Obj(doc, "event_feed");
                            OldestUnreadSeconds = feed != null && Json.Num(feed, "pending", 0) > 0 ? (int)Json.Num(feed, "oldest_pending_seconds", -1) : -1;
                            // a bridge restarted under this launch shows another launch or phase: the last report again
                            var la = Json.Obj(doc, "launch");
                            Dictionary<string, object> mine;
                            lock (gate) mine = lastLaunch;
                            if (mine != null && la != null && (Json.Str(la, "id") != Json.Str(mine, "launch") || Json.Str(la, "phase") != Json.Str(mine, "phase")))
                                Post("/api/launch", mine);
                        }
                    }
                    while (true)
                    {
                        Out item;
                        lock (gate)
                        {
                            if (outbox.Count == 0) break;
                            item = outbox.Peek();
                            if (DateTime.UtcNow < item.Next) break;      // (a 5xx's wait: the ones after it wait too)
                            item.Sending = true;
                        }
                        int code;
                        try { Call("POST", item.Path, item.Body, out code, item.Id); }      // (not reached / the answer lost: throws - kept, sent again)
                        finally { lock (gate) item.Sending = false; }
                        if (code == 0) break;
                        if (code >= 500)
                        {
                            item.Tries++;
                            if (item.Tries < MaxTries)
                            {
                                item.Next = DateTime.UtcNow.AddSeconds(Math.Pow(2, item.Tries - 1));     // 1, 2, 4, 8 s
                                LastError = item.Path + ": " + code + ", again in " + Math.Pow(2, item.Tries - 1) + " s";
                                break;
                            }
                            LastError = item.Path + ": " + code + " " + MaxTries + " times - dropped";
                        }
                        else if (code == 401 || code == 429)
                        {
                            // (429: after the seconds the bridge asks for - its Retry-After; GPT's re-review of #28)
                            int wait = code == 429 ? Math.Max(1, Math.Min(60, retryAfter)) : 2;
                            item.Next = DateTime.UtcNow.AddSeconds(wait);
                            LastError = item.Path + ": " + code + (code == 401 ? " (the bridge's token)" : " (too many)") + " - kept, again in " + wait + " s";
                            break;
                        }
                        else if (code >= 400 && code != 409) LastError = item.Path + ": refused " + code + " - dropped";
                        lock (gate) if (outbox.Count > 0 && ReferenceEquals(outbox.Peek(), item)) outbox.Dequeue();
                        if (code == 409) Superseded = true;
                    }
                    string room = WatchRoom;
                    if (room != null && DateTime.UtcNow >= nextRoom)
                    {
                        nextRoom = DateTime.UtcNow.AddSeconds(2);
                        int rc;
                        string rb = Call("GET", "/api/match/rooms/" + room, null, out rc);
                        var rd = rc == 200 ? Json.Parse(rb) : null;
                        if (Json.Str(rd, "state", "") == "closed" && Json.Str(rd, "closed_by", "") == "peer") RoomClosedBy = "peer";
                        if (rd != null) RoomConditions = Json.Obj(rd, "conditions");
                    }
                    if (DateTime.UtcNow >= nextPoll)
                    {
                        nextPoll = DateTime.UtcNow.AddSeconds(pollSeconds);
                        int code;
                        string body = Call("GET", Hold ? "/api/packages/next?hold=1&ack=1" : "/api/packages/next?ack=1", null, out code);
                        Connected = code > 0;
                        Superseded = code == 409;
                        if (code == 200 && !string.IsNullOrEmpty(body))
                        {
                            Received(Json.Parse(body) as Dictionary<string, object>);
                            nextPoll = DateTime.UtcNow;       // the next one may be waiting too
                        }
                    }
                }
                catch (Exception ex)
                {
                    Connected = false;
                    LastError = ex.Message;
                    nextPoll = DateTime.UtcNow.AddSeconds(pollSeconds);
                }
                wake.WaitOne(100);
            }
        }

        int retryAfter;          // (the last answer's Retry-After, in seconds: 0 none)

        string Call(string method, string path, string body, out int code, string eventId = null)
        {
            code = 0;
            retryAfter = 0;
            var req = (HttpWebRequest)WebRequest.Create(Base + path);
            req.Method = method;
            req.Proxy = null;
            req.Timeout = 5000;
            req.ReadWriteTimeout = 5000;
            req.KeepAlive = false;
            req.Headers["X-MMS-Instance"] = instance;
            req.Headers["X-MMS-Started"] = started.ToString();
            string token = MuseStudioPlugin.BridgeToken("app");
            if (token.Length > 0) req.Headers["Authorization"] = "Bearer " + token;
            if (eventId != null) req.Headers["X-MMS-Event-Id"] = eventId;
            try
            {
                if (body != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    req.ContentType = "application/json; charset=utf-8";
                    req.ContentLength = data.Length;
                    using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    code = (int)resp.StatusCode;
                    using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
                }
            }
            catch (WebException we)
            {
                var resp = we.Response as HttpWebResponse;
                if (resp == null) throw;
                code = (int)resp.StatusCode;
                int.TryParse(resp.Headers["Retry-After"] ?? "", out retryAfter);
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
            }
        }
    }
}
