using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// Survival Tools Lite (skyarkhangel) recomputes "which tool is best for this stat" from scratch on every call:
    /// SurvivalToolUtility.GetBestSurvivalTool enumerates the pawn's equipment and inventory, allocates several Lists and
    /// does an O(n*m) scan over tools x their WorkStatFactors. It is called from StatPart_SurvivalTool.TransformValue every
    /// time a tool-affected work stat is evaluated (measured ~41 us/call, ~7 ms/s).
    ///
    /// A pawn's tool set changes rarely, so the answer is stable for long stretches. This fix caches GetBestSurvivalTool's
    /// result per (pawn, stat) for a short window of ticks and replays it, returning the SAME tool the original would — only
    /// skipping the scan. Behaviour is unchanged apart from at most RefreshTicks of staleness after a pawn picks up or drops
    /// a tool (self-heals on the next refresh). A destroyed cached tool is never returned (it recomputes). Any exception
    /// switches the fix off for the session. Off by default.
    /// Soft dependency: nothing is patched when Survival Tools Lite is absent or its method has a different shape.
    /// </summary>
    static class SurvivalToolCacheFix
    {
        public static bool Installed;
        public static bool Enabled;          // off by default (set from config)
        static bool disabled;

        const int RefreshTicks = 60;         // ~1 s at 60 TPS: a tool swap is reflected within this
        const int ClearEvery = 60000;        // drop the whole cache once a day to release dead-pawn entries

        sealed class Entry { public int Tick; public object Tool; }
        static readonly Dictionary<Pawn, Dictionary<StatDef, Entry>> cache = new Dictionary<Pawn, Dictionary<StatDef, Entry>>();
        static int lastClear;
        static long hits, misses;

        public static void Apply(Harmony harmony, ProbeConfig cfg)
        {
            Enabled = cfg.FixSurvivalToolCache;
            var type = AccessTools.TypeByName("SurvivalToolsLite.SurvivalToolUtility");
            var target = type == null ? null : AccessTools.Method(type, "GetBestSurvivalTool", new[] { typeof(Pawn), typeof(StatDef) });
            if (target == null)
            {
                PerfFixes.Status.Add("survival tool cache: SurvivalToolsLite.SurvivalToolUtility.GetBestSurvivalTool not found (Survival Tools Lite not loaded), nothing to cache");
                return;
            }
            if (!target.IsStatic || !typeof(Thing).IsAssignableFrom(target.ReturnType))
            {
                PerfFixes.Status.Add("survival tool cache: GetBestSurvivalTool has an unexpected shape (mod changed?), NOT patched");
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(SurvivalToolCacheFix).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(SurvivalToolCacheFix).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)));
                Installed = true;
                PerfFixes.Status.Add("survival tool cache: " + (Enabled ? "active" : "installed but OFF (settings / config.txt)") + " (best-tool-per-stat cached for " + RefreshTicks + " ticks)");
            }
            catch (Exception e)
            {
                PerfFixes.Status.Add("survival tool cache: could not patch (" + e.GetType().Name + "), NOT applied");
            }
        }

        public static string Live()
        {
            long total = hits + misses;
            return "survival tool cache live: " + hits + " hits / " + total + " calls" + (disabled ? ", SWITCHED OFF" : "");
        }

        static int CurTick()
        {
            try { return Find.TickManager != null ? Find.TickManager.TicksGame : 0; }
            catch { return 0; }
        }

        // Return false (skip original, use cached result) only on a fresh, still-valid hit.
        static bool Prefix(Pawn pawn, StatDef stat, ref object __result)
        {
            if (disabled || !Enabled || pawn == null || stat == null) return true;
            if (Environment.CurrentManagedThreadId != ProbeCore.MainThreadId) return true;
            try
            {
                int now = CurTick();
                if (now - lastClear > ClearEvery || cache.Count > 4000) { cache.Clear(); lastClear = now; }

                Dictionary<StatDef, Entry> byStat;
                if (cache.TryGetValue(pawn, out byStat))
                {
                    Entry e;
                    if (byStat.TryGetValue(stat, out e) && now - e.Tick < RefreshTicks)
                    {
                        var t = e.Tool as Thing;
                        if (e.Tool == null || (t != null && !t.Destroyed))   // never serve a destroyed tool
                        {
                            __result = e.Tool;
                            hits++;
                            return false;
                        }
                    }
                }
                misses++;
                return true;
            }
            catch (Exception ex)
            {
                Disable("exception: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        static void Postfix(Pawn pawn, StatDef stat, object __result)
        {
            if (disabled || !Enabled || pawn == null || stat == null) return;
            if (Environment.CurrentManagedThreadId != ProbeCore.MainThreadId) return;
            try
            {
                Dictionary<StatDef, Entry> byStat;
                if (!cache.TryGetValue(pawn, out byStat)) { byStat = new Dictionary<StatDef, Entry>(); cache[pawn] = byStat; }
                Entry e;
                if (!byStat.TryGetValue(stat, out e)) { e = new Entry(); byStat[stat] = e; }
                e.Tick = CurTick();
                e.Tool = __result;
            }
            catch (Exception ex) { Disable("exception (store): " + ex.GetType().Name + ": " + ex.Message); }
        }

        static void Disable(string why)
        {
            disabled = true;
            cache.Clear();
            PerfFixes.Status.Add("survival tool cache: SWITCHED OFF: " + why);
            PerfLog.Error("survival tool cache switched off: " + why);
        }
    }
}
