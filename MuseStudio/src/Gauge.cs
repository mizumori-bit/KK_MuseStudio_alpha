using System;
using System.Collections.Generic;
using UnityEngine;

namespace MuseStudio
{
    // The character's pleasure gauge (MUSE_GUIDE.md 3.6; data/gauge.json, the Godot app's gauge_controller.gd): 0..1, raised
    // by the scene a package plays (while its block is being presented) and by a play (PlayCtl), falling slowly when nothing
    // raises it. Muse hears of the milestones (half / near, once per rise) and of the climax (who: female), after which the
    // gauge rests for an afterglow.
    public class Gauge
    {
        readonly Dictionary<string, object> doc;
        public float Value;
        public string Phase = "rising";
        public int Climaxes;
        float idle, afterglowLeft;
        readonly Dictionary<string, bool> armed = new Dictionary<string, bool>();
        public Action<string, float> Milestone;
        public Action<int, Dictionary<string, object>> Climaxed;
        public Action AfterglowEnded;
        public Func<bool> SceneGate;        // may the playing scene raise the gauge now
        public Func<string> SceneKey;
        public Func<int> SceneIntensity;

        public Gauge()
        {
            doc = Data.Load("gauge.json");
            var m = Json.Obj(doc, "milestones");
            if (m != null) foreach (var k in m.Keys) armed[k] = true;
        }

        public Dictionary<string, object> Doc { get { return doc; } }

        // the data's multiplier x the user's setting (快感度の上がる速さ, PR #9 adopted)
        float RateFactor()
        {
            var host = MuseStudioPlugin.Instance;
            float user = host != null && host.GaugeSpeed != null ? host.GaugeSpeed.Value : 1f;
            return (float)Json.Num(doc, "multiplier", 1.0) * user;
        }

        // a touch's gain: area x kind x strength (a hold also x its seconds / 1.5, at most x3)
        public float TouchGain(string area, string kind, int strength, float seconds)
        {
            var t = Json.Obj(doc, "touch");
            float g = (float)Json.Num(Json.Obj(t, "area"), area, 0) * (float)Json.Num(Json.Obj(t, "kind"), kind, 1);
            var s = Json.Arr(t, "strength");
            if (s != null && s.Count > 0) g *= Convert.ToSingle(s[Mathf.Clamp(strength, 1, s.Count) - 1]);
            if (kind == "hold") g *= Mathf.Clamp(seconds / 1.5f, 1f, 3f);
            return g;
        }

        // a press while it lasts (Touch.PressGain; the trial runs of 2026-11-06): one touch's worth of a kind (hold / stroke) at a
        // strength - area x kind x strength; the press takes it once at strength 1 as it becomes that kind, then each second the
        // worth at its strength now / touch.press_worth_seconds
        public float TouchWorth(string area, string kind, int strength)
        {
            var t = Json.Obj(doc, "touch");
            float g = (float)Json.Num(Json.Obj(t, "area"), area, 0) * (float)Json.Num(Json.Obj(t, "kind"), kind, 1);
            var s = Json.Arr(t, "strength");
            if (s != null && s.Count > 0) g *= Convert.ToSingle(s[Mathf.Clamp(strength, 1, s.Count) - 1]);
            return g;
        }

        public float PressWorthSeconds { get { return (float)Json.Num(Json.Obj(doc, "touch"), "press_worth_seconds", 1.5); } }

        public void Add(float amount, Dictionary<string, object> cause)
        {
            if (amount <= 0f || Phase == "afterglow") return;
            idle = 0f;
            Value = Mathf.Min(1f, Value + amount * RateFactor());
            var marks = Json.Obj(doc, "milestones");
            if (marks != null)
            {
                var names = new List<string>(marks.Keys);
                names.Sort((a, b) => Convert.ToDouble(marks[a]).CompareTo(Convert.ToDouble(marks[b])));
                foreach (var n in names)
                {
                    bool on;
                    if (armed.TryGetValue(n, out on) && on && Value >= Convert.ToSingle(marks[n]))
                    {
                        armed[n] = false;
                        if (Milestone != null) Milestone(n, Value);
                    }
                }
            }
            if (Value >= 1f) Climax(cause);
        }

        void Climax(Dictionary<string, object> cause)
        {
            Climaxes++;
            Phase = "afterglow";
            afterglowLeft = (float)Json.Num(doc, "afterglow_seconds", 25.0);
            Value = (float)Json.Num(doc, "after_value", 0.2);
            foreach (var k in new List<string>(armed.Keys)) armed[k] = true;
            if (Climaxed != null) Climaxed(Climaxes, cause);
        }

        public void Tick(float dt)
        {
            if (Phase == "afterglow")
            {
                afterglowLeft -= dt;
                if (afterglowLeft <= 0f)
                {
                    Phase = "rising";
                    if (AfterglowEnded != null) AfterglowEnded();
                }
                return;
            }
            bool gained = false;
            string key = SceneKey != null ? SceneKey() : "";
            if (!string.IsNullOrEmpty(key) && (SceneGate == null || SceneGate()))
            {
                float rate = (float)Json.Num(Json.Obj(Json.Obj(doc, "scene"), "rate"), key, 0.0);
                if (rate > 0f)
                {
                    var f = Json.Arr(Json.Obj(doc, "scene"), "intensity");
                    int i = Mathf.Clamp(SceneIntensity != null ? SceneIntensity() : 1, 1, f != null ? f.Count : 3) - 1;
                    Add(rate * (f != null ? Convert.ToSingle(f[i]) : 1f) * dt,
                        new Dictionary<string, object> { { "source", "scene" }, { "scene", key }, { "intensity", i + 1 } });
                    gained = true;
                }
            }
            if (!gained) Idle(dt);
        }

        // a second without a gain (the play counts its own gains: PlayCtl calls Add, and Idle when its pace is 0)
        public void Idle(float dt)
        {
            idle += dt;
            if (idle > Json.Num(doc, "decay_after", 8.0) && Value > 0f)
            {
                Value = Mathf.Max(0f, Value - (float)Json.Num(doc, "decay", 0.01) * dt);
                var marks = Json.Obj(doc, "milestones");
                if (marks != null)
                    foreach (var kv in marks)
                        if (Value < Convert.ToSingle(kv.Value) - Json.Num(doc, "rearm", 0.1)) armed[kv.Key] = true;
            }
        }

        public void Reset()
        {
            Value = 0f;
            Phase = "rising";
            idle = afterglowLeft = 0f;
            foreach (var k in new List<string>(armed.Keys)) armed[k] = true;
        }

        public Dictionary<string, object> Info()
        {
            return new Dictionary<string, object> { { "value", Math.Round(Value, 3) }, { "phase", Phase }, { "climaxes", Climaxes },
                { "afterglow_left", Phase == "afterglow" ? Math.Round(Mathf.Max(afterglowLeft, 0f), 1) : 0.0 } };
        }
    }
}
