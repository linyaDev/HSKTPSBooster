using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// Camera+ draws pawns as markers when zoomed out. Its colours come from DotTools.GetMarkerColors: a matching rule of the player (DotConfig), else
    /// for animals and foreign pawns the outline is PawnNameColorUtility.PawnNameColorOf (light blue for factionless pawns, i.e. wild animals), and
    /// colonists get white with a black outline. We post-process that result, only when the player has no rule for the pawn (dotConfig == null):
    ///   - colonists get a bright green marker with a darker green outline (not while selected, downed or in a mental state, those keep Camera+'s own colours),
    ///   - drones and robots (race defName starts with one of marker_drone_prefixes: Drone, AIRobot_) get a pale grey-blue fill instead of white,
    ///   - guests and visitors (host faction is the player, quest lodgers, or a humanlike pawn of a faction that is not hostile to us) get a fill of the same colour
    ///     as their Camera+ outline (light blue for neutral factions), unless marker_guest is set to a fixed colour; prisoners of the colony orange, slaves of the colony yellow,
    ///   - predators that are not ours (RaceProps.predator, wild or hostile faction) get a red marker with a darker red outline, even when a Camera+ rule matches them.
    ///
    /// A selected pawn that Camera+ shows as a marker also gets its name drawn under the marker (Camera+ hides the vanilla name of pawns in marker mode).
    /// The name is drawn with the vanilla GenMapUI.DrawPawnLabel at the vanilla label position; a truncateToWidth different from 9999 makes
    /// Camera+'s own DrawPawnLabel patch let the call through.
    ///
    /// If the Pawn Badge mod (SaucyPigeon.PawnBadge) is loaded, the FIRST badge of every colonist is drawn at the top left of its marker,
    /// at a constant size on screen, so a glance at the far map still tells who is who.
    /// Soft dependency: nothing is patched when Camera+ is not loaded or its method has a different signature.
    /// </summary>
    static class MarkerColors
    {
        public static bool Installed;
        public static bool Enabled = true;
        public static bool BadgesEnabled = true;   // Pawn Badge icons at colonist markers (marker_badges, checkbox in settings)
        public static bool CacheEnabled = true;    // per-frame Camera+ caches (fix_camera_cache, checkbox in settings)

        static Color colonist = new Color(0x33 / 255f, 0xE0 / 255f, 0x55 / 255f, 1f);
        static Color predator = new Color(0xE6 / 255f, 0x30 / 255f, 0x30 / 255f, 1f);
        static Color? guestFixed;   // null: the guest fill is the colour of the pawn's own outline (Camera+ colours neutral factions light blue)
        static Color prisoner = new Color(0xFF / 255f, 0x95 / 255f, 0x00 / 255f, 1f);  // orange
        static Color slave = new Color(0xF0 / 255f, 0xD0 / 255f, 0x20 / 255f, 1f);     // yellow
        static Color drone = new Color(0x74 / 255f, 0x7E / 255f, 0x88 / 255f, 1f);     // pale grey-blue: drones and robots are white otherwise and dominate the map
        static string[] dronePrefixes = { "Drone", "AIRobot_" };                          // race defNames: Odyssey drones, Misc. Robots
        static readonly Dictionary<ThingDef, bool> droneByDef = new Dictionary<ThingDef, bool>();
        static long recolored, labelsDrawn, calls, predatorsSeen, predatorsByRule, colonistsSeen, colonistsByRule;
        static MethodInfo shouldShowMarker;

        public static void Apply(Harmony harmony, ProbeConfig cfg)
        {
            Enabled = cfg.FixFarMap;   // part of the interactive map: one switch for both
            BadgesEnabled = cfg.MarkerBadges;
            CacheEnabled = cfg.FixCameraCache;
            SetColors(cfg.MarkerColonist, cfg.MarkerPredator, cfg.MarkerGuest, cfg.MarkerPrisoner, cfg.MarkerSlave, cfg.MarkerDrone);
            SetDronePrefixes(cfg.MarkerDronePrefixes);
            var type = AccessTools.TypeByName("CameraPlus.DotTools");
            MethodInfo target = null;
            if (type != null)
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "GetMarkerColors") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 4 && ps[0].ParameterType == typeof(Pawn) && ps[2].ParameterType == typeof(Color).MakeByRefType() && ps[3].ParameterType == typeof(Color).MakeByRefType()
                        && ps[3].Name == "outerColor" && ps[1].Name == "dotConfig" && m.ReturnType == typeof(bool)) { target = m; break; }
                }
            }
            if (target == null)
            {
                PerfFixes.Status.Add("marker colors: Camera+ (CameraPlus.DotTools.GetMarkerColors) not found, nothing to recolor");
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(MarkerColors).GetMethod("ColorsCachePrefix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(MarkerColors).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)));
                Installed = true;
                InstallNameLabel(harmony, type);
                InstallForcedMarkers(harmony);
                InstallZoomCache(harmony);
                InitBadges();
                PerfFixes.Status.Add("marker colors: " + (Enabled ? "ON" : "installed but OFF") + " (Camera+ markers: colonists fully green unless you have a Camera+ rule for them, predators that are not ours fully red)");
            }
            catch (Exception e)
            {
                PerfFixes.Status.Add("marker colors: could not patch Camera+ (" + e.GetType().Name + "), NOT applied");
            }
        }

        static bool TryHex(string hex, out Color c)
        {
            c = default(Color);
            if (string.IsNullOrEmpty(hex)) return false;
            hex = hex.Trim().TrimStart('#');
            int rgb;
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)) return false;
            c = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
            return true;
        }

        /// <summary>RRGGBB strings; one that is not valid keeps the current colour.</summary>
        public static void SetColors(string colonistHex, string predatorHex, string guestHex, string prisonerHex, string slaveHex, string droneHex)
        {
            Color c;
            if (TryHex(colonistHex, out c)) colonist = c;
            if (TryHex(predatorHex, out c)) predator = c;
            guestFixed = TryHex(guestHex, out c) ? (Color?)c : null;
            if (TryHex(prisonerHex, out c)) prisoner = c;
            if (TryHex(slaveHex, out c)) slave = c;
            if (TryHex(droneHex, out c)) drone = c;
        }

        /// <summary>Comma separated race defName prefixes whose markers get the pale drone fill (for example "Drone,AIRobot_").</summary>
        public static void SetDronePrefixes(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return;
            var list = new List<string>();
            foreach (var p in csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) { var t = p.Trim(); if (t.Length > 0) list.Add(t); }
            dronePrefixes = list.ToArray();
            droneByDef.Clear();
        }

        static bool IsDrone(ThingDef def)
        {
            if (def == null || def.defName == null) return false;
            bool r;
            if (droneByDef.TryGetValue(def, out r)) return r;
            r = false;
            foreach (var p in dronePrefixes)
                if (def.defName.StartsWith(p, StringComparison.Ordinal)) { r = true; break; }
            droneByDef[def] = r;
            return r;
        }

        // guests of the colony (host faction, quest lodgers) and visitors: humanlike pawns of another faction that is not hostile to the player
        static bool IsGuestLike(Pawn pawn)
        {
            var player = Faction.OfPlayer;
            if (pawn.HostFaction == player || pawn.IsQuestLodger()) return true;
            var f = pawn.Faction;
            return f != null && f != player && !f.HostileTo(player) && !pawn.IsPrisoner && !pawn.IsSlave;
        }

        static void Paint(ref Color inner, ref Color outer, Color c)
        {
            inner = c;
            outer = new Color(c.r * 0.35f, c.g * 0.35f, c.b * 0.35f, 1f);   // a darker outline of the same colour keeps the shape readable
        }

        // ---- markers are ours while the far map is active
        // Camera+ turns a pawn into a marker when the size of a map cell on screen is at most its threshold (settings.dotSize or the rule's showBelowPixels).
        // MarkerDecision.For reads that size once from FastUI.CurUICellSize. While the far map is active we make that read return 0, so every pawn that
        // Camera+ may draw as a marker (style, animal policy and rules still apply) is one, whatever the threshold says.
        //
        // The same MarkerDecision.For also asks Tools.MouseDistanceSquared whether the mouse is near the pawn: when it is, Camera+ draws the real
        // pawn instead of the marker AND reveals its name (both hang on that one distance). We want only the name: the distance read is replaced
        // with "far away" (the marker never leaves), and a postfix on Tools.ShouldShowLabel shows the vanilla name for the pawn under the mouse.
        static Func<float> realCellSize;
        static MethodInfo cellSizeGetter, mouseDistMethod;
        static Func<UnityEngine.Vector3, bool, float> realMouseDist;
        static int replacedCalls, mouseReplacedCalls;
        static long hoverNamesShown;

        static void InstallForcedMarkers(Harmony harmony)
        {
            try
            {
                var decisionType = AccessTools.TypeByName("CameraPlus.MarkerDecision");
                var fastUi = AccessTools.TypeByName("CameraPlus.FastUI");
                var toolsType = AccessTools.TypeByName("CameraPlus.Tools");
                cellSizeGetter = fastUi == null ? null : AccessTools.PropertyGetter(fastUi, "CurUICellSize");
                mouseDistMethod = toolsType == null ? null : AccessTools.Method(toolsType, "MouseDistanceSquared", new[] { typeof(Vector3), typeof(bool) });
                if (mouseDistMethod != null && mouseDistMethod.ReturnType == typeof(float) && mouseDistMethod.IsStatic)
                    realMouseDist = (Func<Vector3, bool, float>)Delegate.CreateDelegate(typeof(Func<Vector3, bool, float>), mouseDistMethod);
                else mouseDistMethod = null;
                MethodInfo forMethod = null;
                if (decisionType != null)
                    foreach (var m in decisionType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        if (m.Name == "For" && m.GetParameters().Length == 3) { forMethod = m; break; }
                if (forMethod == null || cellSizeGetter == null || cellSizeGetter.ReturnType != typeof(float))
                {
                    PerfFixes.Status.Add("far map: Camera+ markers are NOT forced (MarkerDecision.For or FastUI.CurUICellSize not found)");
                    return;
                }
                realCellSize = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), cellSizeGetter);
                replacedCalls = 0;
                mouseReplacedCalls = 0;
                harmony.Patch(forMethod, transpiler: new HarmonyMethod(typeof(MarkerColors).GetMethod("ForTranspiler", BindingFlags.Static | BindingFlags.NonPublic)));
                PerfFixes.Status.Add(replacedCalls > 0
                    ? "far map: while it is active every pawn is drawn as a Camera+ marker (contour), whatever Camera+'s size threshold says"
                    : "far map: Camera+ markers are NOT forced (the cell size read was not found in MarkerDecision.For)");
                InstallHoverName(harmony, toolsType);
            }
            catch (Exception e)
            {
                PerfFixes.Status.Add("far map: Camera+ markers not forced (" + e.GetType().Name + ")");
            }
        }

        static void InstallHoverName(Harmony harmony, Type toolsType)
        {
            try
            {
                var shouldShowLabel = toolsType == null ? null : AccessTools.Method(toolsType, "ShouldShowLabel");
                if (mouseReplacedCalls == 0 || shouldShowLabel == null || shouldShowLabel.ReturnType != typeof(bool))
                {
                    PerfFixes.Status.Add("marker colors: hover keeps its Camera+ behaviour (MouseDistanceSquared or ShouldShowLabel not found)");
                    return;
                }
                harmony.Patch(shouldShowLabel, postfix: new HarmonyMethod(typeof(MarkerColors).GetMethod("ShouldShowLabelPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                PerfFixes.Status.Add("marker colors: hovering a marker shows only the pawn's name (the marker is not replaced by the real pawn)");
            }
            catch (Exception e)
            {
                PerfFixes.Status.Add("marker colors: hover name not installed (" + e.GetType().Name + ")");
            }
        }

        static IEnumerable<CodeInstruction> ForTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var replacement = typeof(MarkerColors).GetMethod("CellSizeForMarkers", BindingFlags.Static | BindingFlags.NonPublic);
            var mouseReplacement = typeof(MarkerColors).GetMethod("MouseDistanceForMarkers", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var ins in instructions)
            {
                bool isCall = ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt;
                if (isCall && ins.operand as MethodInfo == cellSizeGetter)
                {
                    replacedCalls++;
                    yield return new CodeInstruction(OpCodes.Call, replacement) { labels = ins.labels, blocks = ins.blocks };
                }
                else if (isCall && mouseDistMethod != null && ins.operand as MethodInfo == mouseDistMethod)
                {
                    mouseReplacedCalls++;
                    yield return new CodeInstruction(OpCodes.Call, mouseReplacement) { labels = ins.labels, blocks = ins.blocks };
                }
                else yield return ins;
            }
        }

        static float CellSizeForMarkers()
        {
            try { if (FarMap.Active()) return 0f; } catch { }
            return realCellSize();
        }

        // Only inside MarkerDecision.For: with the fix on, the mouse never counts as "near", so a hovered pawn keeps its marker.
        static float MouseDistanceForMarkers(Vector3 pos, bool mapCoordinates)
        {
            if (Enabled) return float.MaxValue;
            return realMouseDist != null ? realMouseDist(pos, mapCoordinates) : 0f;
        }

        // Camera+ asks Tools.ShouldShowLabel before it lets a vanilla pawn name through. The pawn under the mouse gets its name back
        // (the marker stays, because MarkerDecision.For above no longer sees the mouse).
        static void ShouldShowLabelPostfix(Pawn pawn, ref bool __result)
        {
            if (__result || !Enabled || pawn == null) return;
            try
            {
                if (!pawn.Spawned || pawn.Dead || pawn.Map != Find.CurrentMap) return;
                var mouse = UI.MouseMapPosition();
                float dx = pawn.DrawPos.x - mouse.x, dz = pawn.DrawPos.z - mouse.z;
                if (dx * dx + dz * dz > 2.25f) return;   // the same 1.5 cells Camera+ itself uses
                __result = true;
                hoverNamesShown++;
            }
            catch { }
        }

        static void InstallNameLabel(Harmony harmony, Type dotTools)
        {
            try
            {
                foreach (var m in dotTools.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "ShouldShowMarker" && ps.Length == 2 && ps[0].ParameterType == typeof(Pawn) && m.ReturnType == typeof(bool)) { shouldShowMarker = m; break; }
                }
                var hook = AccessTools.Method(typeof(RimWorld.MapInterface), "MapInterfaceOnGUI_AfterMainTabs", Type.EmptyTypes);
                if (shouldShowMarker == null || hook == null) { PerfFixes.Status.Add("marker colors: selected pawn name under the marker NOT installed (Camera+ or the game changed)"); return; }
                harmony.Patch(hook, postfix: new HarmonyMethod(typeof(MarkerColors).GetMethod("NamePostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                PerfFixes.Status.Add("marker colors: the name of a selected pawn is drawn under its Camera+ marker");
            }
            catch (Exception e)
            {
                PerfFixes.Status.Add("marker colors: name under the selected marker not installed (" + e.GetType().Name + ")");
            }
        }

        // ---- Pawn Badge (soft dependency): the first badge of a colonist is drawn at the top left of its marker
        static Type compBadgeType, badgeDefType;
        static FieldInfo badgesField;
        static PropertyInfo badgeSymbolProp;
        static bool badgesAvailable;
        static long badgesDrawn;
        static readonly Dictionary<Pawn, ThingComp> badgeCompCache = new Dictionary<Pawn, ThingComp>();
        static readonly Dictionary<string, Texture2D> badgeTexCache = new Dictionary<string, Texture2D>();

        static void InitBadges()
        {
            try
            {
                compBadgeType = AccessTools.TypeByName("RR_PawnBadge.CompBadge");
                badgesField = compBadgeType == null ? null : AccessTools.Field(compBadgeType, "badges");
                badgeDefType = AccessTools.TypeByName("RR_PawnBadge.BadgeDef");
                badgeSymbolProp = badgeDefType == null ? null : AccessTools.Property(badgeDefType, "Symbol");
                badgesAvailable = badgesField != null && badgesField.FieldType == typeof(string[]) && badgeSymbolProp != null && badgeSymbolProp.PropertyType == typeof(Texture2D);
                if (badgesAvailable) PerfFixes.Status.Add("marker colors: the first Pawn Badge of a colonist is drawn at the top left of its marker");
                else if (compBadgeType != null) PerfFixes.Status.Add("marker colors: Pawn Badge is loaded but its layout changed, badges on markers NOT drawn");
            }
            catch (Exception e)
            {
                badgesAvailable = false;
                PerfFixes.Status.Add("marker colors: Pawn Badge check failed (" + e.GetType().Name + "), badges on markers NOT drawn");
            }
        }

        static Texture2D FirstBadgeTex(Pawn pawn)
        {
            ThingComp comp;
            if (!badgeCompCache.TryGetValue(pawn, out comp))
            {
                comp = null;
                var comps = pawn.AllComps;
                for (int i = 0; i < comps.Count; i++)
                    if (compBadgeType.IsInstanceOfType(comps[i])) { comp = comps[i]; break; }
                badgeCompCache[pawn] = comp;   // null is cached too: pawns without the comp are not searched again
            }
            if (comp == null) return null;
            var arr = badgesField.GetValue(comp) as string[];
            string defName = arr != null && arr.Length > 0 ? arr[0] : null;
            if (string.IsNullOrEmpty(defName)) return null;
            Texture2D tex;
            if (badgeTexCache.TryGetValue(defName, out tex)) return tex;
            tex = null;
            try
            {
                var def = GenDefDatabase.GetDefSilentFail(badgeDefType, defName, false);
                if (def != null) tex = badgeSymbolProp.GetValue(def, null) as Texture2D;
            }
            catch { }
            badgeTexCache[defName] = tex;   // a miss is cached too, so a broken badge name costs one lookup, not one per frame
            return tex;
        }

        static void DrawBadges(Map map)
        {
            if (!badgesAvailable || !BadgesEnabled) return;
            var colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                var pawn = colonists[i];
                try
                {
                    if (pawn.Dead || !(bool)shouldShowMarker.Invoke(null, new object[] { pawn, null })) continue;   // real body drawn: Pawn Badge is a colonist bar thing, the map stays clean
                    var tex = FirstBadgeTex(pawn);
                    if (tex == null) continue;
                    Vector2 ui = pawn.DrawPos.MapToUIPosition();
                    const float size = 16f;
                    if (ui.x < -size || ui.y < -size || ui.x > UI.screenWidth + size || ui.y > UI.screenHeight + size) continue;
                    GUI.DrawTexture(new Rect(ui.x - size - 3f, ui.y - size - 3f, size, size), tex, ScaleMode.ScaleToFit);
                    badgesDrawn++;
                }
                catch { }
            }
        }

        // Runs in the map GUI; draws the vanilla name label for every selected pawn that is currently a Camera+ marker.
        static void NamePostfix()
        {
            if (!Enabled || shouldShowMarker == null) return;
            var e = Event.current;
            if (e == null || e.type != EventType.Repaint) return;
            var selector = Find.Selector;
            var map = Find.CurrentMap;
            if (selector == null || map == null) return;
            DrawBadges(map);
            var list = selector.SelectedObjectsListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                var pawn = list[i] as Pawn;
                if (pawn == null || !pawn.Spawned || pawn.Map != map || pawn.Dead) continue;
                try
                {
                    if (!(bool)shouldShowMarker.Invoke(null, new object[] { pawn, null })) continue;   // real body drawn: the vanilla name is there already
                    GenMapUI.DrawPawnLabel(pawn, GenMapUI.LabelDrawPosFor(pawn, -0.6f), 1f, 9998f, null, GameFont.Tiny, true, true);
                    labelsDrawn++;
                }
                catch { }
            }
        }

        public static string Live()
        {
            return "marker colors live: " + (Enabled ? "ON" : "off") + ", GetMarkerColors called " + calls + " times, colonists seen " + colonistsSeen + " (+" + colonistsByRule + " with a Camera+ rule), predators seen " + predatorsSeen
                + " (" + predatorsByRule + " of them with a Camera+ rule), " + recolored + " markers recolored, " + labelsDrawn + " selected names drawn, " + badgesDrawn + " badges drawn, " + hoverNamesShown + " hover names shown, "
                + colorCacheHits + " color cache hits, " + zoomCacheHits + " zoom cache hits";
        }

        // ---- per-frame caches: Camera+ recomputes two things far too often (measured on Ultrafast, 74 pawns):
        //  - CameraDriver.CurrentZoom: its prefix redoes the zoom maths on every call, ~94000 calls/s (~10 ms/s);
        //  - DotTools.GetMarkerColors: the rule walk plus our recolour, ~42 calls per pawn per frame (~57000 calls/s).
        // Neither result can change inside a frame, so the first call of a frame computes and the rest are lookups.
        // A prefix that returns false also skips Camera+'s own bool prefix (Harmony skips later bool prefixes), so the
        // cached value wins over the whole chain. All caches are main-thread only and dead when Enabled is off.

        struct ColorEntry { public object Cfg; public bool Result; public Color Inner, Outer; }
        static readonly Dictionary<Pawn, ColorEntry> colorCache = new Dictionary<Pawn, ColorEntry>();
        static int colorCacheFrame = -1;
        static bool servedFromCache;
        static long colorCacheHits, zoomCacheHits;

        static bool ColorsCachePrefix(Pawn pawn, object dotConfig, ref Color innerColor, ref Color outerColor, ref bool __result)
        {
            servedFromCache = false;
            if (!Enabled || !CacheEnabled || pawn == null || Environment.CurrentManagedThreadId != ProbeCore.MainThreadId) return true;
            try
            {
                int frame = UnityEngine.Time.frameCount;
                if (frame != colorCacheFrame) { colorCache.Clear(); colorCacheFrame = frame; return true; }
                ColorEntry e;
                if (colorCache.TryGetValue(pawn, out e) && ReferenceEquals(e.Cfg, dotConfig))
                {
                    innerColor = e.Inner; outerColor = e.Outer; __result = e.Result;
                    servedFromCache = true;
                    colorCacheHits++;
                    return false;   // skips the original; our postfix sees servedFromCache and does nothing
                }
            }
            catch { }
            return true;
        }

        static int zoomFrame = -1;
        static CameraZoomRange zoomCached;

        static void InstallZoomCache(Harmony harmony)
        {
            try
            {
                var getter = AccessTools.PropertyGetter(typeof(CameraDriver), "CurrentZoom");
                if (getter == null) { PerfFixes.Status.Add("marker colors: CameraDriver.CurrentZoom getter not found, zoom cache NOT installed"); return; }
                harmony.Patch(getter,
                    prefix: new HarmonyMethod(typeof(MarkerColors).GetMethod("ZoomCachePrefix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(MarkerColors).GetMethod("ZoomCachePostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                PerfFixes.Status.Add("marker colors: CurrentZoom and GetMarkerColors results are cached per frame (Camera+ recomputed them thousands of times per frame)");
            }
            catch (Exception e)
            {
                PerfFixes.Status.Add("marker colors: zoom cache not installed (" + e.GetType().Name + ")");
            }
        }

        static bool ZoomCachePrefix(ref CameraZoomRange __result)
        {
            if (!Enabled || !CacheEnabled || Environment.CurrentManagedThreadId != ProbeCore.MainThreadId) return true;
            if (UnityEngine.Time.frameCount != zoomFrame) return true;
            __result = zoomCached;
            zoomCacheHits++;
            return false;
        }

        static void ZoomCachePostfix(CameraZoomRange __result)
        {
            if (!Enabled || !CacheEnabled || Environment.CurrentManagedThreadId != ProbeCore.MainThreadId) return;
            zoomCached = __result;
            zoomFrame = UnityEngine.Time.frameCount;
        }

        // Camera+ signature: bool GetMarkerColors(Pawn pawn, DotConfig dotConfig, out Color innerColor, out Color outerColor)
        static void Postfix(Pawn pawn, object dotConfig, ref Color innerColor, ref Color outerColor, ref bool __result)
        {
            if (servedFromCache) { servedFromCache = false; return; }
            if (!Enabled || pawn == null) return;
            if (!__result) { StoreColors(pawn, dotConfig, __result, innerColor, outerColor); return; }
            calls++;
            try
            {
                var selector = Find.Selector;
                if (selector != null && selector.IsSelected(pawn)) return;            // the selected pawn keeps Camera+'s highlight
                Color originalOutline = outerColor;                                    // what Camera+ chose (the faction colour for foreign pawns)
                // drones and robots (Odyssey Drone_*, Misc. Robots AIRobot_*): only the fill is toned down, the outline (faction colour) stays
                if (IsDrone(pawn.def))
                {
                    innerColor = drone; recolored++; return;
                }
                // guests, prisoners and slaves of the colony each get their own colour (also for pawns that count as colonists, e.g. quest lodgers)
                if (pawn.RaceProps != null && pawn.RaceProps.Humanlike)
                {
                    if (pawn.IsSlaveOfColony) { Paint(ref innerColor, ref outerColor, slave); recolored++; return; }
                    if (pawn.IsPrisonerOfColony) { Paint(ref innerColor, ref outerColor, prisoner); recolored++; return; }
                    if (IsGuestLike(pawn)) { Paint(ref innerColor, ref outerColor, guestFixed ?? originalOutline); recolored++; return; }
                }
                bool predatorNotOurs = !pawn.IsColonist && pawn.RaceProps != null && pawn.RaceProps.predator && pawn.Faction != Faction.OfPlayer;
                if (predatorNotOurs)
                {
                    // red for every predator that is not ours (wild, or belonging to a hostile faction), also when the player has a Camera+ rule for it
                    predatorsSeen++;
                    if (dotConfig != null) predatorsByRule++;
                    Paint(ref innerColor, ref outerColor, predator);
                    recolored++;
                    return;
                }
                if (dotConfig != null) { if (pawn.IsColonist) colonistsByRule++; return; }   // colonists: a Camera+ rule of the player wins
                if (pawn.IsColonist)
                {
                    colonistsSeen++;
                    if (pawn.Downed || pawn.MentalStateDef != null) return;            // keep Camera+'s downed and mental colours
                    Paint(ref innerColor, ref outerColor, colonist);
                    recolored++;
                }
            }
            catch { }
            finally { StoreColors(pawn, dotConfig, __result, innerColor, outerColor); }   // whatever branch ran, the final colours are this frame's answer
        }

        static void StoreColors(Pawn pawn, object cfg, bool result, Color inner, Color outer)
        {
            if (!CacheEnabled || Environment.CurrentManagedThreadId != ProbeCore.MainThreadId) return;
            try
            {
                int frame = UnityEngine.Time.frameCount;
                if (frame != colorCacheFrame) { colorCache.Clear(); colorCacheFrame = frame; }
                colorCache[pawn] = new ColorEntry { Cfg = cfg, Result = result, Inner = inner, Outer = outer };
            }
            catch { }
        }
    }
}
