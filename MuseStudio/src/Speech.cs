using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MuseStudio
{
    // The mouth with the words (PR #46 §6, handed over in PR #47; MUSE_GUIDE 3.1 and 5.2).
    //
    // The words' parse, one for every way a line comes (her Muse's package, the opponent's in a match, a replay): after her
    // inner thoughts in （） are gone (BlockPlayer.Shown - a ruby inside them goes with them), the ruby marks: 漢字《かんじ》
    // (the run of kanji - else of letters and digits - before 《 is the base) or ｜base《reading》 (the base named). The
    // line shows the bases; the readings are what the mouth says. Ruby is optional: a kanji without one counts as kanji_morae
    // beats, other letters as their own.
    //
    // The timeline (the owner's choice of 2026-11-07: the words come out at the pace she says them): beats at
    // morae_per_second; ー / ～ hold the sound, っ a short silent beat, small kana go with the beat before; 、 and 。 (and ！ ？ …)
    // are silent for a pause; brackets and quotes come out with no time. The line's shown part follows the beats (a ruby's
    // base shown in step with its reading) - BlockPlayer.Visible.
    //
    // What the mouth is given is a voice's loudness, as the original's lip sync reads it from a voice (the user's instruction
    // of 2026-11-07: its speed and its blend as the original's - Mouth below): a beat's vowel loud (voice_peak), the consonant
    // into the next a dip (voice_dip of it - the mouth not shut between the sounds of a word), ん lower (nasal_open), each beat
    // a little louder or softer than the last (voice_vary); a pause and っ silent.
    public class Spoken
    {
        public string Display = "";
        public readonly List<Step> Steps = new List<Step>();
        public float Seconds;                   // the whole line said

        public struct Step
        {
            public float Start, Length;
            public int Upto;                   // the characters shown from the step's start
            public float Open;                 // the mouth's peak in it (0: shut)
            public bool Hold;                  // held open (ー), not opened and closed
        }

        public int VisibleAt(float t)
        {
            if (t >= Seconds) return Display.Length;
            int upto = 0;
            foreach (var s in Steps) { if (s.Start > t) break; upto = s.Upto; }
            return Mathf.Min(upto, Display.Length);
        }

        public float Peak = 0.8f, Dip = 0.3f, Vary = 0.12f;      // (the speech settings: voice_peak, voice_dip, voice_vary)

        // the voice's loudness at t (0..1, as the original's lip sync reads a voice - the mouth's open share of the face's own
        // open range)
        public float OpenAt(float t)
        {
            if (t < 0f || t >= Seconds) return 0f;
            for (int i = 0; i < Steps.Count; i++)
            {
                var s = Steps[i];
                if (t < s.Start || t >= s.Start + s.Length) continue;
                if (s.Open <= 0f || s.Length <= 0f) return 0f;
                float u = (t - s.Start) / s.Length;
                float level = Mathf.Clamp01(Peak * s.Open * (1f + Vary * (((i * 37 + 11) % 17) / 8f - 1f)));
                if (s.Hold) return level;                                        // (ー: the sound held)
                return level * (Dip + (1f - Dip) * Mathf.Sqrt(Mathf.Sin(Mathf.PI * u)));   // (the vowel loud, the consonant into the next a dip)
            }
            return 0f;
        }

        public int Beats { get { int n = 0; foreach (var s in Steps) if (s.Open > 0f && !s.Hold) n++; return n; } }
    }

    public static class Speech
    {
        // ---- the ruby ----

        public struct Unit
        {
            public int Start, Length;          // in the display
            public string Reading;             // null: the display's own characters
        }

        static bool Kanji(char c)
        {
            return (c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿') || (c >= '豈' && c <= '﫿') || c == '々' || c == '〆' || c == 'ヶ' || c == '〇';
        }

        static bool Alnum(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || (c >= 'Ａ' && c <= 'Ｚ') || (c >= 'ａ' && c <= 'ｚ') || (c >= '０' && c <= '９');
        }

        // the display (ruby marks gone, their bases kept) and its units: one a character (a surrogate pair one), a ruby's base one
        public static string Parse(string text, List<Unit> units)
        {
            var d = new StringBuilder();
            text = text ?? "";
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '｜')
                {
                    int lt = text.IndexOf('《', i + 1), gt = lt >= 0 ? text.IndexOf('》', lt + 1) : -1;
                    int nl = text.IndexOf('\n', i + 1);
                    if (lt > i + 1 && gt > lt && (nl < 0 || nl > gt))
                    {
                        string bas = text.Substring(i + 1, lt - i - 1);
                        units.Add(new Unit { Start = d.Length, Length = bas.Length, Reading = text.Substring(lt + 1, gt - lt - 1) });
                        d.Append(bas);
                        i = gt;
                        continue;
                    }
                }
                else if (c == '《')
                {
                    int gt = text.IndexOf('》', i + 1), nl = text.IndexOf('\n', i + 1);
                    if (gt > i && (nl < 0 || nl > gt))
                    {
                        string reading = text.Substring(i + 1, gt - i - 1);
                        // the base: the plain characters just before, all kanji - else all letters / digits
                        int k = units.Count;
                        bool kanji = k > 0 && units[k - 1].Reading == null && units[k - 1].Length == 1 && Kanji(d[units[k - 1].Start]);
                        while (k > 0 && units[k - 1].Reading == null && units[k - 1].Length == 1
                               && (kanji ? Kanji(d[units[k - 1].Start]) : Alnum(d[units[k - 1].Start]))) k--;
                        if (k < units.Count)
                        {
                            int start = units[k].Start;
                            units.RemoveRange(k, units.Count - k);
                            units.Add(new Unit { Start = start, Length = d.Length - start, Reading = reading });
                        }
                        i = gt;                    // (no base: the reading is dropped, the marks too)
                        continue;
                    }
                }
                int len = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                units.Add(new Unit { Start = d.Length, Length = len, Reading = null });
                d.Append(text, i, len);
                i += len - 1;
            }
            return d.ToString();
        }

        public static string Display(string text) { return Parse(text, new List<Unit>()); }

        // ---- the timeline ----

        const string Small = "ぁぃぅぇぉゃゅょゎゕゖァィゥェォャュョヮヵヶ";
        const string Commas = "、，,";
        const string Periods = "。．.！？!?";
        const string Ellipses = "…‥";
        const string Holds = "ーｰ～〜";
        const string Silent = "「」『』【】（）()［］[]〈〉《》\"'“”‘’・";

        static bool Kana(char c) { return (c >= 'ぁ' && c <= 'ゖ') || (c >= 'ァ' && c <= 'ヺ'); }

        public static Spoken Line(string text, Dictionary<string, object> cfg)
        {
            var units = new List<Unit>();
            var sp = new Spoken { Display = Parse(text, units) };
            float rate = Mathf.Max(1f, (float)Json.Num(cfg, "morae_per_second", 8.0)), beat = 1f / rate;
            float kanjiBeats = (float)Json.Num(cfg, "kanji_morae", 2.0), latin = (float)Json.Num(cfg, "latin_morae", 0.6);
            float comma = (float)Json.Num(cfg, "pause_comma", 0.25), period = (float)Json.Num(cfg, "pause_period", 0.45);
            float ellipsis = (float)Json.Num(cfg, "pause_ellipsis", 0.2), space = (float)Json.Num(cfg, "pause_space", 0.1);
            float nasal = (float)Json.Num(cfg, "nasal_open", 0.6);
            float t = 0f;
            Action<int, float, float, bool> add = (upto, len, open, hold) =>
            {
                sp.Steps.Add(new Spoken.Step { Start = t, Length = len, Upto = upto, Open = open, Hold = hold });
                t += len;
            };
            foreach (var u in units)
            {
                int end = u.Start + u.Length;
                if (u.Reading != null)
                {
                    // a ruby: its reading's beats, its base shown in step with them
                    var beats = new List<float>();
                    var holds = new List<bool>();
                    Beats(u.Reading, beats, holds, nasal);
                    if (beats.Count == 0) { add(end, 0f, 0f, false); continue; }
                    for (int j = 0; j < beats.Count; j++)
                    {
                        int shown = u.Start + Mathf.Max(1, Mathf.CeilToInt((j + 1) * (float)u.Length / beats.Count));
                        add(Mathf.Min(end, shown), beats[j] < 0f ? beat * 0.5f : beat, Mathf.Max(0f, beats[j]), holds[j]);
                    }
                    continue;
                }
                char c = sp.Display[u.Start];
                if (u.Length == 2) add(end, 0f, 0f, false);                                     // (an emoji: out at once)
                else if (Commas.IndexOf(c) >= 0) add(end, comma, 0f, false);
                else if (Periods.IndexOf(c) >= 0) add(end, period, 0f, false);
                else if (Ellipses.IndexOf(c) >= 0) add(end, ellipsis, 0f, false);
                else if (char.IsWhiteSpace(c) || c == '　') add(end, space, 0f, false);
                else if (Silent.IndexOf(c) >= 0) add(end, 0f, 0f, false);
                else if (Holds.IndexOf(c) >= 0) add(end, beat, 1f, true);
                else if (Small.IndexOf(c) >= 0) add(end, 0f, 0f, false);                         // (with the beat before)
                else if (c == 'っ' || c == 'ッ') add(end, beat * 0.5f, 0f, false);
                else if (c == 'ん' || c == 'ン') add(end, beat, nasal, false);
                else if (Kana(c)) add(end, beat, 1f, false);
                else if (Kanji(c)) { for (int j = 0; j < Mathf.Max(1, Mathf.RoundToInt(kanjiBeats)); j++) add(end, beat, 1f, false); }
                else if (Alnum(c)) add(end, beat * latin, 1f, false);
                else add(end, 0f, 0f, false);                                                    // (♪ and the like: out at once)
            }
            sp.Seconds = t;
            sp.Peak = (float)Json.Num(cfg, "voice_peak", 0.8);
            sp.Dip = Mathf.Clamp01((float)Json.Num(cfg, "voice_dip", 0.3));
            sp.Vary = Mathf.Clamp01((float)Json.Num(cfg, "voice_vary", 0.12));
            return sp;
        }

        // a reading's beats: open peak per beat (-1: a short shut beat, っ), held (ー)
        static void Beats(string reading, List<float> beats, List<bool> holds, float nasal)
        {
            foreach (char c in reading)
            {
                if (Small.IndexOf(c) >= 0) continue;
                if (c == 'っ' || c == 'ッ') { beats.Add(-1f); holds.Add(false); }
                else if (Holds.IndexOf(c) >= 0) { beats.Add(1f); holds.Add(true); }
                else if (c == 'ん' || c == 'ン') { beats.Add(nasal); holds.Add(false); }
                else if (Kana(c) || Kanji(c) || Alnum(c)) { beats.Add(1f); holds.Add(false); }
            }
        }
    }

    // Her mouth moved by the words: the original's own way for a voice - the face's voice value, set where the original
    // sets it each frame (a postfix on ChaControl.UpdateBlendShapeVoice; without a voice it puts 0 there). The loudness of
    // the words' timeline goes through the original's own smoothing of a voice's loudness, frame by frame
    // (FBSAssist.AudioAssist.GetAudioWaveValue: rising, the mean of it and the frame before's; falling, 0.2 of it and 0.8 of
    // the frame before's - the user's instruction of 2026-11-07: the mouth's speed and its blend as the original's); the
    // face takes it as the original does (FBSCtrlMouth.CalcBlend: the pattern's shut and open shapes in that share, within
    // her face's open range - a face with the mouth shut stays shut; a new pattern blends in over 0.15 s). Studio keeps the
    // mouth fixed at its own opening (FixedRate - its 口の開き): unfixed while she speaks and fixed back when she stops, as
    // Studio does for a voice with lip sync (OCIChar.ChangeMouthOpen).
    public class Mouth
    {
        public static Mouth Instance;
        ChaControl target;
        float kept = -1f;                       // the fixed rate before the words (-1: not fixed)
        public float Value;                     // 0..1 now (the loudness)
        float before;                           // the frame before's loudness (AudioAssist's beforeVolume)
        public bool Active { get { return target != null; } }
        public ChaControl Target { get { return target; } }
        public int Moves;                       // (checks) frames it set a value above 0

        static readonly System.Reflection.FieldInfo FixedRate = HarmonyLib.AccessTools.Field(typeof(FBSBase), "FixedRate");

        public static void Patch(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(ChaControl), "UpdateBlendShapeVoice"), null,
                new HarmonyLib.HarmonyMethod(typeof(Mouth), "AfterVoice"));
        }

        static void AfterVoice(ChaControl __instance)
        {
            var m = Instance;
            if (m == null || m.target == null || !ReferenceEquals(__instance, m.target) || __instance.fbsCtrl == null) return;
            float v = m.Value, shown = v < m.before ? v * 0.2f + m.before * 0.8f : (v + m.before) * 0.5f;
            m.before = v;
            __instance.fbsCtrl.SetVoiceVaule(shown);
            if (shown > 0f) m.Moves++;
        }

        public static float FixedOf(ChaControl cha) { return cha != null && cha.mouthCtrl != null && FixedRate != null ? (float)FixedRate.GetValue(cha.mouthCtrl) : -1f; }

        // speak with this character's mouth now, this open
        public void Drive(ChaControl cha, float value)
        {
            if (cha == null || cha.mouthCtrl == null) { Stop(); return; }
            if (!ReferenceEquals(cha, target))
            {
                Stop();
                target = cha;
                kept = FixedOf(cha);
                if (kept >= 0f) FixedRate.SetValue(cha.mouthCtrl, -1f);
            }
            Value = Mathf.Clamp01(value);
        }

        // the words stopped (all said, all shown at a press, the next block, a stop): the face's mouth back
        public void Stop()
        {
            if (target != null && target.mouthCtrl != null)
            {
                if (kept >= 0f && FixedOf(target) < 0f) FixedRate.SetValue(target.mouthCtrl, kept);
                if (target.fbsCtrl != null) target.fbsCtrl.SetVoiceVaule(0f);
            }
            target = null;
            kept = -1f;
            Value = 0f;
            before = 0f;
        }
    }
}
