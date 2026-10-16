using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>What the far map patches and the patch bodies. Everything checks Active() first, so with the mode off the game runs its own code.</summary>
    public static partial class FarMap
    {
        static FieldInfo drawThingsField;
        static readonly List<Thing> pawnsOnly = new List<Thing>();
        static long weatherSkipped;

        public static void Apply(Harmony harmony, ProbeConfig cfg)
        {
            Enabled = cfg.FixFarMap;
            ThresholdPx = cfg.FarMapPx;
            ApplyLayerSwitches(cfg);
            SetColors(cfg);
            var missing = new List<string>();
            int patched = 0;

            Func<Type, string, Type[], MethodInfo> find = (t, name, args) => t == null ? null : AccessTools.Method(t, name, args);
            var skip = new HarmonyMethod(typeof(FarMap).GetMethod("SkipWhenFar", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };

            // 1) the map mesh: replaced by our own drawing
            var drawMesh = find(typeof(MapDrawer), "DrawMapMesh", Type.EmptyTypes);
            if (drawMesh != null)
            {
                harmony.Patch(drawMesh, prefix: new HarmonyMethod(typeof(FarMap).GetMethod("DrawMapMeshPrefix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
                patched++;
            }
            else missing.Add("MapDrawer.DrawMapMesh");

            // 2) dynamic things: only pawns stay in the list while the mode is on
            drawThingsField = AccessTools.Field(typeof(DynamicDrawManager), "drawThings");
            var dyn = find(typeof(DynamicDrawManager), "DrawDynamicThings", Type.EmptyTypes);
            if (dyn != null && drawThingsField != null && typeof(List<Thing>).IsAssignableFrom(drawThingsField.FieldType))
            {
                harmony.Patch(dyn,
                    prefix: new HarmonyMethod(typeof(FarMap).GetMethod("DynamicPrefix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(FarMap).GetMethod("DynamicFinalizer", BindingFlags.Static | BindingFlags.NonPublic)));
                patched++;
            }
            else missing.Add("DynamicDrawManager.DrawDynamicThings / drawThings");

            // 3) other things drawn every frame that the far map does not show
            foreach (var m in new[]
            {
                find(typeof(OverlayDrawer), "DrawAllOverlays", Type.EmptyTypes),
                find(typeof(DesignationManager), "DrawDesignations", Type.EmptyTypes),
                find(typeof(TemporaryThingDrawer), "Draw", Type.EmptyTypes),
                find(typeof(FleckManager), "FleckManagerDraw", Type.EmptyTypes),
                find(typeof(GameConditionManager), "GameConditionManagerDraw", new[] { typeof(Map) }),
            })
            {
                if (m != null && m.ReturnType == typeof(void)) { harmony.Patch(m, prefix: skip); patched++; }
                else missing.Add("one of OverlayDrawer/DesignationManager/TemporaryThingDrawer/FleckManager/GameConditionManager draw methods");
            }

            // 4) weather visuals: rain, snow, sandstorm overlays and lightning flashes (CameraDriver.OnPreCull calls this every frame).
            //    Only the drawing is skipped, WeatherManagerTick still runs, so the weather itself (wetness, snow cover, mood) is untouched.
            var weather = find(typeof(WeatherManager), "DrawAllWeather", Type.EmptyTypes);
            if (weather != null && weather.ReturnType == typeof(void))
            {
                harmony.Patch(weather, prefix: new HarmonyMethod(typeof(FarMap).GetMethod("WeatherPrefix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
                patched++;
            }
            else missing.Add("WeatherManager.DrawAllWeather");

            // 5) keep the wall texture up to date: a cell changed
            var dirtyM = find(typeof(MapDrawer), "MapMeshDirty", new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) });
            if (dirtyM != null)
            {
                harmony.Patch(dirtyM, postfix: new HarmonyMethod(typeof(FarMap).GetMethod("DirtyPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                patched++;
            }
            else missing.Add("MapDrawer.MapMeshDirty");

            // 6) UsefulMarks draws its mark icons (stars...) next to every pawn name in the world; on the far map they are noise.
            //    Only the in-world branch is skipped (its DrawingInWorld flag is true), the colonist bar keeps the marks. Soft dependency.
            var umPatch = AccessTools.TypeByName("UsefulMarks.NamePlatePatches+GenMapUI_DrawPawnLabel_Patch");
            var umPostfix = umPatch == null ? null : AccessTools.Method(umPatch, "Postfix");
            usefulMarksInWorld = umPatch == null ? null : AccessTools.Field(umPatch, "DrawingInWorld");
            if (umPostfix != null && usefulMarksInWorld != null && usefulMarksInWorld.FieldType == typeof(bool))
            {
                harmony.Patch(umPostfix, prefix: new HarmonyMethod(typeof(FarMap).GetMethod("MarksSkipPrefix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
                PerfFixes.Status.Add("far map: UsefulMarks icons next to in-world pawn names are hidden while the far map is active (the colonist bar keeps them)");
            }

            // 7) the Dubs Mint Minimap window shows the same map, so it is not drawn while the far map is active (soft dependency)
            var mini = AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap");
            var miniOnGui = mini == null ? null : AccessTools.Method(mini, "WindowOnGUI", Type.EmptyTypes);
            if (miniOnGui != null && miniOnGui.ReturnType == typeof(void))
            {
                harmony.Patch(miniOnGui, prefix: skip);
                PerfFixes.Status.Add("far map: Dubs Mint Minimap window is hidden while the far map is active");
            }

            Installed = patched > 0 && missing.Count == 0;
            PerfFixes.Status.Add("far map: " + (Installed ? (Enabled ? "ON" : "installed but OFF") : "NOT installed") + " (" + patched + " patches; the map is replaced by walls/rock/background and the weather visuals are hidden when a cell is smaller than "
                + ThresholdPx.ToString("F1", CultureInfo.InvariantCulture) + " px on screen)" + (missing.Count > 0 ? "; NOT found: " + string.Join(", ", missing.ToArray()) : ""));
            if (!Installed) Enabled = false;
        }

        // ------------------------------------------------------------------ patch bodies

        static bool SkipWhenFar() { return !Active(); }

        static FieldInfo usefulMarksInWorld;

        // UsefulMarks' postfix on GenMapUI.DrawPawnLabel: skipped only for its in-world branch while the far map is active.
        static bool MarksSkipPrefix()
        {
            try { if (Active() && (bool)usefulMarksInWorld.GetValue(null)) return false; } catch { }
            return true;
        }

        static bool WeatherPrefix()
        {
            if (!HideWeather || !Active()) return true;
            weatherSkipped++;
            return false;
        }

        static bool DrawMapMeshPrefix()
        {
            if (!Active()) return true;
            try
            {
                DrawFar(Find.CurrentMap);
                return false;
            }
            catch (Exception e) { Fail(e); return true; }
        }

        static bool DynamicPrefix(DynamicDrawManager __instance, out List<Thing> __state)
        {
            __state = null;
            if (!Active()) return true;
            try
            {
                var original = (List<Thing>)drawThingsField.GetValue(__instance);
                pawnsOnly.Clear();
                for (int i = 0; i < original.Count; i++)
                    if (original[i] is Pawn || original[i] is PawnFlyer) pawnsOnly.Add(original[i]);   // Camera+ also draws markers for flying pawns
                drawThingsField.SetValue(__instance, pawnsOnly);
                __state = original;
            }
            catch (Exception e) { Fail(e); }
            return true;
        }

        static void DynamicFinalizer(DynamicDrawManager __instance, List<Thing> __state)
        {
            if (__state == null) return;
            try { drawThingsField.SetValue(__instance, __state); }
            catch (Exception e) { Fail(e); }
        }

        // Runs for every MapMeshDirty of every map, including thousands of calls while a save is LOADING (spawning things marks cells dirty),
        // when activeNow/texMap may still be stale from the previous session. It must therefore check that the texture belongs to the map
        // that is current RIGHT NOW and never throw: an exception here breaks GenSpawn.Spawn and corrupts the whole map load.
        static void DirtyPostfix(IntVec3 loc)
        {
            try
            {
                if (!activeNow) return;
                var m = texMap;
                if (m == null || m != Find.CurrentMap) return;   // stale map after a reload, or a dirty cell of another colony's map
                if (loc.InBounds(m)) dirty.Add(m.cellIndices.CellToIndex(loc));
            }
            catch { }   // worst case is one stale pixel until the next rebuild
        }
    }
}
