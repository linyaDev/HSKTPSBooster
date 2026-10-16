using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// FPS / TPS readout in the bottom-right corner, drawn exactly the way Dubs Performance Analyzer does it: a prefix on
    /// GlobalControlsUtility.DoTimespeedControls that stacks two labels above the vanilla date/speed controls (so it sits in
    /// the same place and shows in dev mode too). Only active when Dubs Performance Analyzer is NOT loaded, so the two never
    /// overlap. Measurement is Dubs' too: TPS from GenTicks.TicksAbs over a one-second window, FPS from frames in that second.
    /// </summary>
    public static class FpsTpsCounter
    {
        public static bool Enabled = true;      // config switch (show_fps_tps)
        public static bool Available = true;    // false when Dubs Performance Analyzer is active
        public static bool Installed;

        static DateTime prevTime;
        static int prevTicks = -1;
        static int tpsActual, tpsTarget, prevFrames, fpsActual;

        /// <summary>Checked once at startup: a few known package ids of Dubs Performance Analyzer.</summary>
        public static void DetectDubs()
        {
            try
            {
                Available = !(ModsConfig.IsActive("dubwise.dubsperformanceanalyzer.steam")
                    || ModsConfig.IsActive("dubwise.dubsperformanceanalyzer")
                    || ModsConfig.IsActive("Dubwise.DubsPerformanceAnalyzer"));
            }
            catch { Available = true; }
        }

        /// <summary>Installs the prefix on the vanilla time-speed controls (same target Dubs uses). Called from PerfFixes.Init.</summary>
        public static void Install(Harmony harmony)
        {
            try
            {
                if (!Available) { PerfFixes.Status.Add("fps/tps counter: off (Dubs Performance Analyzer is active)"); return; }
                var target = AccessTools.Method(typeof(GlobalControlsUtility), "DoTimespeedControls");
                if (target == null) { PerfFixes.Status.Add("fps/tps counter: GlobalControlsUtility.DoTimespeedControls not found, NOT installed"); return; }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(FpsTpsCounter).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)));
                Installed = true;
                PerfFixes.Status.Add("fps/tps counter: " + (Enabled ? "ON (bottom-right, above date/speed)" : "installed but OFF (settings / config.txt)"));
            }
            catch (Exception e)
            {
                PerfFixes.Status.Add("fps/tps counter: not installed (" + e.GetType().Name + ")");
            }
        }

        // Mirrors Analyzer.GUIElement_TPS.Prefix: counts frames/ticks over a one-second window and stacks two labels above curBaseY.
        static void Prefix(float leftX, float width, ref float curBaseY)
        {
            if (!Enabled || !Available) return;
            try
            {
                float mult = Find.TickManager.TickRateMultiplier;
                tpsTarget = (int)Math.Round(mult == 0f ? 0f : 60f * mult);

                if (prevTicks == -1)
                {
                    prevTicks = GenTicks.TicksAbs;
                    prevTime = DateTime.Now;
                }
                else
                {
                    DateTime now = DateTime.Now;
                    if (now.Second != prevTime.Second)
                    {
                        prevTime = now;
                        tpsActual = GenTicks.TicksAbs - prevTicks;
                        prevTicks = GenTicks.TicksAbs;
                        fpsActual = prevFrames;
                        prevFrames = 0;
                    }
                }
                prevFrames++;

                var r = new Rect(leftX - 20f, curBaseY - 26f, width + 20f - 7f, 26f);
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(r, "TPS: " + tpsActual + "(" + tpsTarget + ")");
                r.y -= 26f;
                Widgets.Label(r, "FPS: " + fpsActual);
                curBaseY -= 52f;
                Text.Anchor = TextAnchor.UpperLeft;
            }
            catch { Text.Anchor = TextAnchor.UpperLeft; }
        }
    }
}
