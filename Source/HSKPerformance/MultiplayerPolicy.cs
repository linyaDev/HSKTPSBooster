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
            cfg.FixRotStorage = false;
            cfg.FixPawnEffects = false;
            cfg.FixMothballHediffs = false;
            cfg.MarkerBadges = false;
            cfg.FixCameraCache = false;
            cfg.ShowFpsTps = false;
            cfg.LogVerbose = false;
            cfg.FixSurvivalToolCache = false;
            cfg.FixStatCache = false;
        }
    }
}
