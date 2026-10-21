using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// The most expensive stats in a capture are recomputed over and over for the same pawn: ComfyTemperatureMin/Max
    /// (walks all apparel), PsychicSensitivity and its gear Offset, WorkSpeedGlobal, MeditationFocusGain, CE's
    /// CarryBulk/CarryWeight and so on - together tens of ms per second, all asked for by periodic per-pawn systems.
    ///
    /// Vanilla has the cache already: StatWorker.GetValue(Thing,...) stores every computed value in temporaryStatCache
    /// (for stats with cacheable=true) and serves it when the caller passes cacheStaleAfterTicks - but almost every
    /// caller passes -1 (never serve) or 1 (fresh for one tick). This fix (a) marks the configured stats cacheable so
    /// the vanilla cache exists for them, and (b) a prefix on GetValue raises the accepted staleness to StaleTicks.
    /// The cached value is always a real computed value, so the only behaviour change is up to StaleTicks (~2 s) of
    /// delay before an apparel/hediff/gene change shows in the stat.
    ///
    /// Deliberately NOT in the default list: MoveSpeed (1.6 async pathfinding reads it from worker threads, and combat
    /// reacts to injuries within a tick) and MarketValue (wealth and trade must not lag). The list is editable in
    /// config.txt (stat_cache_stats). Off by default.
    /// </summary>
    static class StatCacheFix
    {
        public static bool Installed;
        public static bool Enabled;              // off by default (set from config)

        static int staleTicks = 120;
        static long served;                      // calls whose staleness we raised
        static readonly HashSet<StatDef> targets = new HashSet<StatDef>();

        static readonly AccessTools.FieldRef<StatWorker, StatDef> statOf =
            AccessTools.FieldRefAccess<StatWorker, StatDef>("stat");

        public static void Apply(Harmony harmony, ProbeConfig cfg)
        {
            Enabled = cfg.FixStatCache;
            staleTicks = Math.Max(2, cfg.FixStatCacheTicks);
            var target = AccessTools.Method(typeof(StatWorker), "GetValue", new[] { typeof(Thing), typeof(bool), typeof(int) });
            if (target == null || statOf == null)
            {
                PerfFixes.Status.Add("stat cache: StatWorker.GetValue(Thing,bool,int) or its stat field not found (game changed?), NOT applied");
                return;
            }

            int madeCacheable = 0;
            var missing = new List<string>();
            foreach (var raw in (cfg.StatCacheStats ?? "").Split(','))
            {
                var name = raw.Trim();
                if (name.Length == 0) continue;
                var def = DefDatabase<StatDef>.GetNamedSilentFail(name);
                if (def == null) { missing.Add(name); continue; }
                if (def.immutable) continue;     // immutable stats have their own permanent cache already
                if (!def.cacheable)
                {
                    def.cacheable = true;
                    def.Worker.SetCacheability(def.immutable);   // creates the temporary cache for this worker
                    madeCacheable++;
                }
                targets.Add(def);
            }
            if (targets.Count == 0)
            {
                PerfFixes.Status.Add("stat cache: no known stats in the list (" + string.Join(", ", missing) + "), NOT applied");
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(StatCacheFix), nameof(Prefix)) { priority = Priority.First });
                Installed = true;
                PerfFixes.Status.Add("stat cache: " + (Enabled ? "active" : "installed but OFF (settings / config.txt)")
                    + " (" + targets.Count + " stats served up to " + staleTicks + " ticks stale, " + madeCacheable + " newly marked cacheable"
                    + (missing.Count > 0 ? "; unknown: " + string.Join(", ", missing) : "") + ")");
            }
            catch (Exception e)
            {
                PerfFixes.Status.Add("stat cache: could not patch (" + e.GetType().Name + "), NOT applied");
            }
        }

        public static string Live()
        {
            return "stat cache live: " + served + " reads within the raised staleness window";
        }

        public static void Prefix(StatWorker __instance, ref int cacheStaleAfterTicks)
        {
            if (!Enabled || cacheStaleAfterTicks >= staleTicks)
                return;
            if (Environment.CurrentManagedThreadId != ProbeCore.MainThreadId)
                return;                           // worker threads always compute fresh
            if (!targets.Contains(statOf(__instance)))
                return;
            cacheStaleAfterTicks = staleTicks;
            served++;
        }
    }
}
