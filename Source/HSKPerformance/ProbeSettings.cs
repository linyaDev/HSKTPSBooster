using RimWorld;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// Options > Mod settings > HSK TPS Booster: four switches for the fixes and one slider for the far map.
    /// Everything else (recording length, hook groups, colours, speed thresholds) lives in config.txt, which the recorder and the fixes read.
    /// </summary>
    public sealed class HSKPerformanceMod : Mod
    {
        ProbeConfig cfg;
        Vector2 scroll;
        bool sRot, sMap, sEff, sMoth, sBadge, sCam, sFps, sLog, sTool, sComfy; // what was last applied, to notice a click

        public HSKPerformanceMod(ModContentPack content) : base(content) { }

        public override string SettingsCategory() { return "HSK TPS Booster"; }

        void Ensure()
        {
            if (cfg != null) return;
            cfg = ProbeConfig.Load();
            sRot = cfg.FixRotStorage; sMap = cfg.FixFarMap; sEff = cfg.FixPawnEffects; sMoth = cfg.FixMothballHediffs; sBadge = cfg.MarkerBadges; sCam = cfg.FixCameraCache; sFps = cfg.ShowFpsTps; sLog = cfg.LogVerbose; sTool = cfg.FixSurvivalToolCache; sComfy = cfg.FixStatCache;
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Ensure();
            var view = new Rect(0f, 0f, inRect.width - 20f, 560f);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var l = new Listing_Standard();
            l.Begin(view);

            l.Label("HSKTPS.Fixes.Header".Translate());
            l.Gap(4f);
            l.CheckboxLabeled("HSKTPS.RotStorage.Label".Translate(), ref cfg.FixRotStorage, "HSKTPS.RotStorage.Desc".Translate());
            l.CheckboxLabeled("HSKTPS.PawnEffects.Label".Translate(), ref cfg.FixPawnEffects, "HSKTPS.PawnEffects.Desc".Translate());
            l.CheckboxLabeled("HSKTPS.Mothball.Label".Translate(), ref cfg.FixMothballHediffs, "HSKTPS.Mothball.Desc".Translate());
            l.CheckboxLabeled("HSKTPS.FarMap.Label".Translate(), ref cfg.FixFarMap, "HSKTPS.FarMap.Desc".Translate());
            if (cfg.FixFarMap)
            {
                l.Gap(2f);
                l.Label("HSKTPS.FarMap.Slider".Translate(cfg.FarMapPx.ToString("F0"), FarMap.CurrentPx.ToString("F0")));
                cfg.FarMapPx = l.Slider(cfg.FarMapPx, 4f, 60f);
                FarMap.ThresholdPx = cfg.FarMapPx;
                l.Indent(20f);
                l.ColumnWidth -= 20f;
                l.CheckboxLabeled("HSKTPS.Badges.Label".Translate(), ref cfg.MarkerBadges, "HSKTPS.Badges.Desc".Translate());
                l.CheckboxLabeled("HSKTPS.CameraCache.Label".Translate(), ref cfg.FixCameraCache, "HSKTPS.CameraCache.Desc".Translate());
                l.ColumnWidth += 20f;
                l.Outdent(20f);
            }

            string fpsTip = "HSKTPS.FpsTps.Desc".Translate();
            if (!FpsTpsCounter.Available) fpsTip += " " + "HSKTPS.FpsTps.HiddenSuffix".Translate();
            l.CheckboxLabeled("HSKTPS.FpsTps.Label".Translate(), ref cfg.ShowFpsTps, fpsTip);
            l.CheckboxLabeled("HSKTPS.LogVerbose.Label".Translate(), ref cfg.LogVerbose, "HSKTPS.LogVerbose.Desc".Translate());

            l.Gap(14f);
            l.Label("HSKTPS.Experimental.Header".Translate());
            l.Gap(4f);
            l.CheckboxLabeled("HSKTPS.SurvivalTool.Label".Translate(), ref cfg.FixSurvivalToolCache, "HSKTPS.SurvivalTool.Desc".Translate());
            l.CheckboxLabeled("HSKTPS.StatCache.Label".Translate(), ref cfg.FixStatCache, "HSKTPS.StatCache.Desc".Translate());

            if (cfg.FixRotStorage != sRot || cfg.FixFarMap != sMap || cfg.FixPawnEffects != sEff || cfg.FixMothballHediffs != sMoth || cfg.MarkerBadges != sBadge || cfg.FixCameraCache != sCam || cfg.ShowFpsTps != sFps || cfg.LogVerbose != sLog || cfg.FixSurvivalToolCache != sTool || cfg.FixStatCache != sComfy)
            {
                sRot = cfg.FixRotStorage; sMap = cfg.FixFarMap; sEff = cfg.FixPawnEffects; sMoth = cfg.FixMothballHediffs; sBadge = cfg.MarkerBadges; sCam = cfg.FixCameraCache; sFps = cfg.ShowFpsTps; sLog = cfg.LogVerbose; sTool = cfg.FixSurvivalToolCache; sComfy = cfg.FixStatCache;
                cfg.Save();
                PerfFixes.Reload(cfg);
            }

            l.Gap(14f);
            l.Label("HSKTPS.Recording.Note".Translate(ProbeConfig.Dir));
            l.End();
            Widgets.EndScrollView();
        }

        public override void WriteSettings()
        {
            if (cfg != null) cfg.Save();
            base.WriteSettings();
        }
    }
}
