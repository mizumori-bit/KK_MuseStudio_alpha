using System;
using System.Collections.Generic;
using RootMotion.FinalIK;
using UnityEngine;

namespace MuseStudio
{
    // The original's motion IK in a play (DECISIONS "モーション IK と男性の身長"; the Godot app's motion_ik.gd / fbbik.gd):
    // the original's own MotionIK per body - per state the hands and feet to the partner's bones with offsets by the bodies'
    // shapes, the bends, and FrameCorrect's local corrections - over the position's data (h/list <param.path.file>, a TextAsset
    // of the override controller's name), recalculated on every state as HActionBase.SetPlay does. Studio keeps its own IK
    // targets (the "(work)" objects of its IK mode) on the body's FullBodyBipedIK; the original's targets (cf_t_hand_L ...,
    // with their BaseData) are put back on the solver for the play and Studio's come back afterwards.
    public class HIK
    {
        class Body
        {
            public ChaControl Cha;
            public MotionIK M;
            public FullBodyBipedIK Fb;
            public bool WasEnabled;
            public Transform[] Targets = new Transform[4], Bends = new Transform[4];
            public float[] PosW = new float[4], RotW = new float[4], BendW = new float[4];
            public float[] MapW = new float[5];
            public IKSolver.UpdateDelegate Keep;
        }

        // The solver's mappings - how much of what it solves reaches the arms, the legs and the spine's twist. Studio keeps them
        // at 0 (its own IK mode drives them), so the original's motion IK was solved every frame and never reached her bones:
        // her feet followed her hips instead of their targets ("her legs hang from her hips, the soles lose the floor" - the
        // user's note, PR #9) and the hands never reached their partner's bones. For the play they are 1, as the original's
        // (FinalIK's defaults), kept so before each solve; Studio's come back afterwards.
        static float[] Mappings(IKSolverFullBodyBiped s) { return new[] { s.leftArmMapping.weight, s.rightArmMapping.weight, s.leftLegMapping.weight, s.rightLegMapping.weight, s.spineMapping.twistWeight }; }

        static void SetMappings(IKSolverFullBodyBiped s, float[] w)
        {
            s.leftArmMapping.weight = w[0]; s.rightArmMapping.weight = w[1]; s.leftLegMapping.weight = w[2]; s.rightLegMapping.weight = w[3]; s.spineMapping.twistWeight = w[4];
        }

        static readonly float[] Full = { 1f, 1f, 1f, 1f, 1f };
        static readonly string[] TargetNames = { "cf_t_hand_L", "cf_t_hand_R", "cf_t_leg_L", "cf_t_leg_R" };
        static readonly string[] BendNames = { "cf_t_elbo_L", "cf_t_elbo_R", "cf_t_knee_L", "cf_t_knee_R" };
        readonly List<Body> bodies = new List<Body>();
        public int Calcs;                    // (checks)

        static IKEffector[] Effectors(IKSolverFullBodyBiped s) { return new[] { s.leftHandEffector, s.rightHandEffector, s.leftFootEffector, s.rightFootEffector }; }
        static IKConstraintBend[] BendsOf(IKSolverFullBodyBiped s) { return new[] { s.leftArmChain.bendConstraint, s.rightArmChain.bendConstraint, s.leftLegChain.bendConstraint, s.rightLegChain.bendConstraint }; }

        // the bodies (the female part's first) and their data files
        public void Begin(ChaControl female, string femaleFile, ChaControl male, string maleFile)
        {
            End();
            foreach (var pair in new[] { new KeyValuePair<ChaControl, string>(female, femaleFile), new KeyValuePair<ChaControl, string>(male, maleFile) })
            {
                var cha = pair.Key;
                if (cha == null || cha.animBody == null) continue;
                var fb = cha.animBody.GetComponent<FullBodyBipedIK>();
                if (fb == null) continue;
                var b = new Body { Cha = cha, Fb = fb, WasEnabled = fb.enabled };
                var eff = Effectors(fb.solver);
                var bends = BendsOf(fb.solver);
                var byName = new Dictionary<string, Transform>();
                foreach (var bd in cha.GetComponentsInChildren<Illusion.Component.Correct.BaseData>(true)) if (!byName.ContainsKey(bd.name)) byName[bd.name] = bd.transform;
                for (int i = 0; i < 4; i++)
                {
                    b.Targets[i] = eff[i].target; b.PosW[i] = eff[i].positionWeight; b.RotW[i] = eff[i].rotationWeight;
                    b.Bends[i] = bends[i].bendGoal; b.BendW[i] = bends[i].weight;
                    Transform t;
                    if (byName.TryGetValue(TargetNames[i], out t)) eff[i].target = t;
                    if (byName.TryGetValue(BendNames[i], out t)) bends[i].bendGoal = t;
                }
                b.MapW = Mappings(fb.solver);
                var solver = fb.solver;
                b.Keep = () => SetMappings(solver, Full);
                SetMappings(solver, Full);
                solver.OnPreUpdate += b.Keep;
                b.M = new MotionIK(cha, null);
                if (!string.IsNullOrEmpty(pair.Value))
                {
                    try { b.M.LoadData(GlobalMethod.LoadAllFolderInOneFile<TextAsset>("h/list/", pair.Value, null)); }
                    catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: motion IK " + pair.Value + ": " + e.Message); }
                }
                bodies.Add(b);
            }
            var all = new List<MotionIK>();
            foreach (var b in bodies) all.Add(b.M);
            foreach (var b in bodies) { b.M.SetPartners(all); b.M.Reset(); }
        }

        public bool On { get { return bodies.Count > 0; } }

        public bool Enabled { get { foreach (var b in bodies) if (b.M.enabled) return true; return false; } }

        // (checks) the female part's solver's leg mappings now
        public float LegMapping { get { return bodies.Count > 0 && bodies[0].Fb != null ? Mathf.Min(bodies[0].Fb.solver.leftLegMapping.weight, bodies[0].Fb.solver.rightLegMapping.weight) : -1f; } }

        // a state starts (HActionBase.SetPlay: every body's MotionIK.Calc of the state)
        public void Calc(string state)
        {
            foreach (var b in bodies)
            {
                try { b.M.Calc(state); Calcs++; }
                catch (Exception e) { MuseStudioPlugin.Log.LogWarning("MuseStudio: motion IK calc " + state + ": " + e.Message); }
            }
        }

        // the play ends: the IK and the corrections off, Studio's targets and weights back
        public void End()
        {
            foreach (var b in bodies)
            {
                try
                {
                    b.M.enabled = false;
                    b.M.InitFrameCalc();
                    b.M.Release();
                }
                catch (Exception) { }
                if (b.Fb == null) continue;
                if (b.Keep != null) b.Fb.solver.OnPreUpdate -= b.Keep;
                SetMappings(b.Fb.solver, b.MapW);
                var eff = Effectors(b.Fb.solver);
                var bends = BendsOf(b.Fb.solver);
                for (int i = 0; i < 4; i++)
                {
                    eff[i].target = b.Targets[i]; eff[i].positionWeight = b.PosW[i]; eff[i].rotationWeight = b.RotW[i];
                    bends[i].bendGoal = b.Bends[i]; bends[i].weight = b.BendW[i];
                }
                b.Fb.enabled = b.WasEnabled;
            }
            bodies.Clear();
        }

        // ---- the male part's height (the original's males are always 0.6; its motions are made for that) ----

        // the body playing the male part stands so that its penis base is where a 0.6 tall body's would be: the shift by
        // the base's position from the root x (scale at 0.6 / scale now - 1) (the Godot app's _anchor); k = that ratio
        public static string LastProbe = "";      // (checks) the last measure

        public static float HeightRatio(ChaControl cha)
        {
            var hb = Find(cha.objBodyBone != null ? cha.objBodyBone.transform : cha.transform, "cf_n_height");
            if (hb == null || cha.sibBody == null) return 1f;
            float now = cha.fileBody.shapeValueBody[0];
            if (Mathf.Abs(now - 0.6f) < 0.005f) return 1f;
            // (the update applies the height only with its mask's bit 4: the bust's mask pass leaves it off - ChaControl's
            // shape body mask update sets 1 / 2 last - so it is on for this measure)
            var sb = cha.sibBody as ShapeBodyInfoFemale;
            int mask = sb != null ? sb.updateMask : 0;
            if (sb != null) sb.updateMask = 7;
            float s1 = hb.localScale.y;
            cha.sibBody.ChangeValue(0, 0.6f);
            cha.sibBody.Update();
            float s06 = hb.localScale.y;
            cha.sibBody.ChangeValue(0, now);
            cha.sibBody.Update();
            if (sb != null) sb.updateMask = mask;
            LastProbe = string.Format("now {0:F3} s1 {1:F4} s06 {2:F4} hb {3}", now, s1, s06, hb.name);
            return s1 > 1e-4f ? s06 / s1 : 1f;
        }

        // The user's male body's height (the setting "あなたの体の身長"; the Godot app's 0.35..0.85): the original keeps every
        // male at 0.6 - ChaControl's load, SetShapeBodyValue and UpdateShapeBodyValueFromCustomInfo set it back - so it goes on
        // the shape itself, and again after the original's re-read of the card's shape (the postfix: a reload of the body).
        public static void SetMaleHeight(ChaControl cha, float v)
        {
            if (cha == null || cha.sex != 0 || cha.sibBody == null || !cha.sibBody.InitEnd) return;
            cha.chaFile.custom.body.shapeValueBody[0] = v;
            cha.sibBody.ChangeValue(0, v);
            cha.updateShapeBody = true;
        }

        public static void PatchHeight(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(ChaControl), "UpdateShapeBodyValueFromCustomInfo"), null,
                new HarmonyLib.HarmonyMethod(typeof(HIK), "AfterShapeRead"));
            // (and after the original's own setter: other plugins - Push Up's bust - set the body's values again through it)
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(ChaControl), "SetShapeBodyValue", new[] { typeof(int), typeof(float) }), null,
                new HarmonyLib.HarmonyMethod(typeof(HIK), "AfterShapeSet"));
        }

        static void AfterShapeSet(ChaControl __instance, int index)
        {
            if (index == 0) AfterShapeRead(__instance);
        }

        static void AfterShapeRead(ChaControl __instance)
        {
            var p = MuseStudioPlugin.Instance;
            if (p == null || p.Stage == null || p.Stage.Partner == null || __instance != p.Stage.Partner.charInfo || __instance.sex != 0 || p.Match.Active) return;
            float v = Mathf.Clamp(p.YourHeight.Value, 0.35f, 0.85f);
            if (Mathf.Abs(__instance.chaFile.custom.body.shapeValueBody[0] - v) > 0.0001f) HeightResets++;
            SetMaleHeight(__instance, v);
        }

        public static int HeightResets;           // (checks) how often the original (or a plugin) set it back

        public static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var f = Find(t.GetChild(i), name); if (f != null) return f; }
            return null;
        }
    }
}
