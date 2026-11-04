using Verse;

namespace HSKPerformance
{
    internal static class MultiplayerPolicy
    {
        // Mothballing changes Defs at startup. Apply the policy while Multiplayer
        // is loaded, before any save is created or a connection is established.
        internal static bool Active => ModsConfig.IsActive("rwmt.multiplayer");

        internal static void Apply(ProbeConfig cfg)
        {
            if (!Active) return;
            var defaults = new ProbeConfig();
            cfg.FixRotStorage = defaults.FixRotStorage;
            cfg.FixPawnEffects = defaults.FixPawnEffects;
            cfg.FixMothballHediffs = defaults.FixMothballHediffs;
            cfg.MarkerBadges = defaults.MarkerBadges;
            cfg.FixCameraCache = defaults.FixCameraCache;
            cfg.ShowFpsTps = defaults.ShowFpsTps;
            cfg.LogVerbose = defaults.LogVerbose;
            cfg.FixSurvivalToolCache = defaults.FixSurvivalToolCache;
            cfg.FixStatCache = defaults.FixStatCache;
            // the interactive map is off by default in Multiplayer, until the player switches it there
            if (!cfg.FarMapChosenInMultiplayer) cfg.FixFarMap = false;
        }
    }
}
