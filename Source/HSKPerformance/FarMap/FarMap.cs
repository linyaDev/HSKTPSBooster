using System;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// "Far map": when the camera is zoomed out so far that a map cell is smaller than a threshold, the game stops drawing the map and
    /// everything on it except pawns, and we draw a simple map instead: one colour for the background, one for constructed walls, one for natural rock,
    /// all from a single texture (one pixel per cell) drawn as one quad. Pawns, animals and enemies are still drawn by the game (and reduced to dots by Camera+).
    ///
    /// Why: at the farthest zoom the map costs about 5800 Graphics.DrawMesh calls (280 sections, ~20 layers each) plus ~660 dynamic things
    /// and the camera has to submit all of it. Measured: about 15 of the 22 ms per frame that are not ticks.
    ///
    /// Skipped while active: MapDrawer.DrawMapMesh (terrain, buildings, plants, items, lighting, fog, roofs, zones, shadows), every dynamic thing that
    /// is not a pawn, overlays, designations, temporary things, fleck drawing, game condition overlays and the weather visuals (rain, snow, lightning).
    /// Any exception switches the mode off for the rest of the session and the game draws as usual.
    ///
    /// The feature lives in this folder: FarMap.cs (state and the on/off decision), FarMapPatches.cs (what is patched and the patch bodies),
    /// FarMapDrawing.cs (the cell texture and everything drawn on top of it).
    /// </summary>
    public static partial class FarMap
    {
        public static bool Installed;
        public static bool Enabled = true;
        public static float ThresholdPx = 30f;
        // layer switches (config.txt: far_map_blueprints / far_map_corpses / far_map_projectiles / far_map_hide_weather)
        public static bool ShowBlueprints = true, ShowCorpses = true, ShowProjectiles = true, HideWeather = true, ShowIceVortex = true;

        /// <summary>Size of a map cell on screen in pixels at the last check (0 before the first one), for the settings window.</summary>
        public static float CurrentPx { get { return lastPx; } }

        // ---- state
        static bool failed;
        static int decidedFrame = -1;
        static bool activeNow;
        static float lastPx;
        static long activeFrames, inactiveFrames;

        public static string Live()
        {
            return "far map live: " + (failed ? "SWITCHED OFF after an error" : (activeNow ? "ACTIVE" : "off")) + ", cell " + lastPx.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                + " px (threshold " + ThresholdPx.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "), " + activeFrames + " frames drawn, " + rebuilds + " texture rebuilds, " + dirtyApplied
                + " cells updated, " + selectedDrawn + " selected things drawn, " + firesDrawn + " fires drawn, " + blueprintsDrawn + " blueprints drawn, " + corpsesDrawn + " corpses drawn, " + projectilesDrawn + " projectiles drawn, " + iceVortexDrawn + " ice vortices drawn, " + weatherSkipped + " weather draws skipped";
        }

        public static string ContextLine()
        {
            return (failed ? "off (error)" : (Enabled ? (activeNow ? "ACTIVE" : "enabled, not active") : "disabled")) + ", cell " + lastPx.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                + " px, threshold " + ThresholdPx.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ decision

        /// <summary>True while the far map replaces the normal drawing. Cached per frame, with a little hysteresis at the threshold.</summary>
        public static bool Active()
        {
            int frame = Time.frameCount;
            if (frame == decidedFrame) return activeNow;
            decidedFrame = frame;
            bool on = false;
            try
            {
                if (Enabled && !failed && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null && WorldRendererUtility.DrawingMap && Find.CameraDriver != null)
                {
                    lastPx = Screen.height / (2f * Find.CameraDriver.RootSize);
                    on = activeNow ? lastPx < ThresholdPx * 1.05f : lastPx < ThresholdPx;
                }
            }
            catch (Exception e) { Fail(e); on = false; }
            if (on) activeFrames++; else inactiveFrames++;
            if (on != activeNow) { activeNow = on; if (!on) dirty.Clear(); }
            return on;
        }

        public static void ApplyLayerSwitches(ProbeConfig cfg)
        {
            ShowBlueprints = cfg.FarMapBlueprints;
            ShowCorpses = cfg.FarMapCorpses;
            ShowProjectiles = cfg.FarMapProjectiles;
            HideWeather = cfg.FarMapHideWeather;
            ShowIceVortex = cfg.FarMapIceVortex;
        }

        static void Fail(Exception e)
        {
            if (failed) return;
            failed = true; activeNow = false;
            try { Verse.Log.Error("[HSK TPS Booster] far map switched off after an error, normal drawing restored: " + e); } catch { }
        }
    }
}
