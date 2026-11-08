using System;
using System.Reflection;
using UnityEngine;

namespace MuseStudio
{
    // Push Up (KK_Pushup) kept to its own result (the user's note of 2026-11-07: dressed, her nipples through her clothes now
    // and then). Push Up shapes her bust - flattens the nipples under a bra or top it is on for - only when a top's or a bra's
    // state changes, when she is reloaded, and when an outfit card is loaded; when it works it out while her clothes are still
    // loading she wears nothing to it, so her own bust goes on, and nothing makes it work it out again once they are in - till
    // the next of those (the log of the checks: Push Up on and flattening, her nipples at their own depth for seconds after a
    // reload or a match's scene). So ten times a second her bust's values are held against what Push Up itself makes of what she wears now (its own
    // data: the card's bra and top, her own body); a difference that stays (0.3 s: not one of Push Up's own one-frame steps) is
    // given back to Push Up to work out again. Nothing of its data is changed - only its own result put on.
    public class PushupKeeper
    {
        static readonly int[] Index = { 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
        static readonly string[] Names = { "Size", "VerticalPosition", "HorizontalAngle", "HorizontalPosition", "VerticalAngle", "Depth", "Roundness",
                                           "AreolaDepth", "NippleWidth", "NippleDepth" };
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        ChaControl of;
        MonoBehaviour ctl;
        PropertyInfo wearing, loading, started;
        FieldInfo baseData, pushData;
        MethodInfo calculate, recalculate, abmx;
        PropertyInfo[] values;
        float next, offSince = -1f, rebindAt;
        bool gaveUp;                       // (Push Up not as this keeper knows it: left alone for good)

        public string Who = "her";         // (the log) whose body: hers, or the other one's
        public int Repairs;                // (checks) how often Push Up was given its work again
        public int Binds;                  // (the log) how often its controller was found
        public string Last = "";           // (checks, the log) the last difference given back

        public void Tick(ChaControl cha)
        {
            float now = Time.realtimeSinceStartup;
            if (cha == null || cha.sex != 1 || now < next) return;
            next = now + 0.1f;
            // (bound again when she is another, or when the controller it had is gone - a reload may make Push Up's anew)
            if (!ReferenceEquals(of, cha) || (ctl == null && !gaveUp && now >= rebindAt)) { Bind(cha); rebindAt = now + 2f; }
            if (ctl == null || cha.objBody == null || cha.fileBody == null) { offSince = -1f; return; }
            try
            {
                if (started != null && !(bool)started.GetValue(ctl, null)) { offSince = -1f; return; }
                // (not while Push Up is loading her: what is written now Push Up may take for her own bust when its reload
                // comes - her nipples flat undressed after. A load's few seconds - frames stalled while she is built - are
                // Push Up's own to finish; this keeps what is left wrong after it)
                if (loading != null && (bool)loading.GetValue(ctl, null)) { offSince = -1f; return; }
                object w = wearing.GetValue(ctl, null);
                object want;
                if (Convert.ToInt32(w) == 0) want = baseData.GetValue(ctl);          // (nothing it is on for: her own bust)
                else { calculate.Invoke(ctl, new[] { w }); want = pushData.GetValue(ctl); }
                if (want == null) return;
                string diff = "";
                var body = cha.fileBody.shapeValueBody;
                for (int i = 0; i < Index.Length; i++)
                {
                    float v = Convert.ToSingle(values[i].GetValue(want, null));
                    if (Index[i] < body.Length && Mathf.Abs(body[Index[i]] - v) > 0.002f)
                        diff += string.Format("{0} {1:F2} -> {2:F2} ", Names[i], body[Index[i]], v);
                }
                if (diff.Length == 0) { offSince = -1f; return; }
                if (offSince < 0f) { offSince = now; return; }
                if (now - offSince < 0.3f) return;
                offSince = -1f;
                recalculate.Invoke(ctl, new object[] { true, false });
                if (abmx != null) abmx.Invoke(ctl, null);
                Repairs++;
                Last = "wearing " + w + ": " + diff.Trim();
                MuseStudioPlugin.Log.LogInfo("MuseStudio: pushup put back " + Who + " (" + Last + ") at " + now.ToString("F1") + " s");
            }
            catch (Exception e)
            {
                MuseStudioPlugin.Log.LogWarning("MuseStudio: pushup keeper: " + (e.InnerException ?? e).Message);
                ctl = null;             // (not as this keeper knows it: left alone)
                gaveUp = true;
            }
        }

        void Bind(ChaControl cha)
        {
            of = cha;
            ctl = null;
            offSince = -1f;
            foreach (var c in cha.GetComponents<MonoBehaviour>())
                if (c != null && c.GetType().FullName == "KK_Plugins.Pushup+PushupController") ctl = c;
            if (ctl == null) return;
            Binds++;
            MuseStudioPlugin.Log.LogInfo("MuseStudio: pushup keeper bound (" + Binds + ") at " + Time.realtimeSinceStartup.ToString("F1") + " s");
            var t = ctl.GetType();
            wearing = t.GetProperty("CurrentlyWearing", F);
            loading = t.GetProperty("CharacterLoading", F);
            started = t.GetProperty("Started", F);
            baseData = t.GetField("BaseData", F);
            pushData = t.GetField("CurrentPushupData", F);
            calculate = wearing != null ? t.GetMethod("CalculatePush", F, null, new[] { wearing.PropertyType }, null) : null;
            recalculate = t.GetMethod("RecalculateBody", F, null, new[] { typeof(bool), typeof(bool) }, null);
            abmx = t.GetMethod("UpdateABMX", F, null, Type.EmptyTypes, null);
            var bt = baseData != null ? baseData.FieldType : null;
            values = new PropertyInfo[Names.Length];
            for (int i = 0; i < Names.Length && bt != null; i++) values[i] = bt.GetProperty(Names[i], F);
            if (wearing == null || baseData == null || pushData == null || calculate == null || recalculate == null || Array.IndexOf(values, null) >= 0)
            {
                MuseStudioPlugin.Log.LogWarning("MuseStudio: pushup keeper: Push Up is not as it knows it - left alone");
                ctl = null;
                gaveUp = true;
            }
        }
    }
}
