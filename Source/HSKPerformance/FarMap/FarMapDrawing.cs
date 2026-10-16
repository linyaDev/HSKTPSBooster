using System;
using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>The far map picture: the one-pixel-per-cell texture, its colours, and what is drawn on top of it (fires, selected things).</summary>
    public static partial class FarMap
    {
        // ---- looks: colours, set from the config / settings window as RRGGBB (dark on purpose so that the pawn dots stand out)
        public const string DefaultWall = "6E7680", DefaultRock = "3A302A", DefaultBackground = "121614";
        static Color32 WallColor = new Color32(0x6E, 0x76, 0x80, 255);
        static Color32 RockColor = new Color32(0x3A, 0x30, 0x2A, 255);
        static Color32 WaterColor = new Color32(0x26, 0x39, 0x4A, 255);   // very pale: only slightly lighter than the background; deep water only
        static Color32 DoorColor = new Color32(0x43, 0x4B, 0x53, 255);    // between the background and the walls: faint
        static Color32 BuildingColor = new Color32(0x2D, 0x34, 0x3A, 255);  // buildings that cannot be walked through (big machines, blocks): very pale
        static Color FireSpotColor = new Color(0xB5 / 255f, 0x40 / 255f, 0x0F / 255f, 1f);   // dark orange spot under the flame so that a fire is visible at nine pixels per cell
        static Material fireSpotMaterial;
        static Color32 StorageColor = new Color32(0x3A, 0x33, 0x48, 255);   // impassable storage buildings: another pale colour
        static Color BlueprintColor = new Color(0x35 / 255f, 0xA5 / 255f, 0xFF / 255f, 1f);   // bright azure: planned and unfinished construction must never blend with the grey walls
        static Material blueprintCellMaterial;
        static Color CorpseColor = new Color(0xE0 / 255f, 0x5F / 255f, 0xA0 / 255f, 1f);   // pink: fresh animal corpses, nothing else on the far map is pink
        static Material corpseMaterial;
        static Color ProjectileColor = new Color(0xFF / 255f, 0xE0 / 255f, 0x66 / 255f, 1f);   // tracer yellow: projectiles in flight
        static Material projectileMaterial;
        static readonly Color32 Clear = new Color32(0, 0, 0, 0);
        static Color BackgroundColor = new Color(0x12 / 255f, 0x16 / 255f, 0x14 / 255f, 1f);
        static bool colorsChanged;

        static Map texMap;
        static Texture2D tex;
        static Color32[] pixels;
        static Material wallMaterial, backgroundMaterial;
        static readonly HashSet<int> dirty = new HashSet<int>();
        static bool needApply;
        static float lastApply;
        static long rebuilds, dirtyApplied;

        static bool TryParseHex(string hex, out Color32 c)
        {
            c = default(Color32);
            if (string.IsNullOrEmpty(hex)) return false;
            hex = hex.Trim().TrimStart('#');
            if (hex.Length != 6) return false;
            int rgb;
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)) return false;
            c = new Color32((byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255), 255);
            return true;
        }

        /// <summary>Sets the colours from the config's RRGGBB strings; a string that is not valid keeps the current colour. The texture is redrawn on the next far map frame.</summary>
        public static void SetColors(ProbeConfig cfg)
        {
            SetColors(cfg.FarMapWall, cfg.FarMapRock, cfg.FarMapBackground, cfg.FarMapWater, cfg.FarMapDoor, cfg.FarMapBuilding, cfg.FarMapStorage, cfg.FarMapFire, cfg.FarMapBlueprint, cfg.FarMapCorpse, cfg.FarMapProjectile);
        }

        static void SetColors(string wall, string rock, string background, string water, string door, string building, string storage, string fire, string blueprint, string corpse, string projectile)
        {
            Color32 c;
            bool changed = false;
            if (TryParseHex(wall, out c) && !c.Equals(WallColor)) { WallColor = c; changed = true; }
            if (TryParseHex(rock, out c) && !c.Equals(RockColor)) { RockColor = c; changed = true; }
            if (TryParseHex(water, out c) && !c.Equals(WaterColor)) { WaterColor = c; changed = true; }
            if (TryParseHex(door, out c) && !c.Equals(DoorColor)) { DoorColor = c; changed = true; }
            if (TryParseHex(building, out c) && !c.Equals(BuildingColor)) { BuildingColor = c; changed = true; }
            if (TryParseHex(storage, out c) && !c.Equals(StorageColor)) { StorageColor = c; changed = true; }
            if (TryParseHex(fire, out c))
            {
                var f = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                if (f != FireSpotColor) { FireSpotColor = f; fireSpotMaterial = null; }
            }
            if (TryParseHex(blueprint, out c))
            {
                var bp = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                if (bp != BlueprintColor) { BlueprintColor = bp; blueprintCellMaterial = null; }
            }
            if (TryParseHex(corpse, out c))
            {
                var cc = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                if (cc != CorpseColor) { CorpseColor = cc; corpseMaterial = null; }
            }
            if (TryParseHex(projectile, out c))
            {
                var pc = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                if (pc != ProjectileColor) { ProjectileColor = pc; projectileMaterial = null; }
            }
            if (TryParseHex(background, out c))
            {
                var bg = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                if (bg != BackgroundColor) { BackgroundColor = bg; backgroundMaterial = null; changed = true; }
            }
            if (changed) colorsChanged = true;
        }

        // ------------------------------------------------------------------ drawing

        static void DrawFar(Map map)
        {
            EnsureTexture(map);

            // apply the cells that changed, but not more often than 4 times a second
            if (dirty.Count > 0)
            {
                foreach (int idx in dirty) pixels[idx] = PixelFor(map, idx);
                dirtyApplied += dirty.Count;
                dirty.Clear();
                needApply = true;
            }
            if (needApply && Time.realtimeSinceStartup - lastApply >= 0.25f)
            {
                tex.SetPixels32(pixels);
                tex.Apply(false);
                needApply = false;
                lastApply = Time.realtimeSinceStartup;
            }

            int w = map.Size.x, h = map.Size.z;
            var centre = new Vector3(w * 0.5f, 0f, h * 0.5f);
            var scale = new Vector3(w, 1f, h);
            var bg = centre; bg.y = AltitudeLayer.Terrain.AltitudeFor();
            var wall = centre; wall.y = AltitudeLayer.Building.AltitudeFor();
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(bg, Quaternion.identity, scale), backgroundMaterial, 0);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(wall, Quaternion.identity, scale), wallMaterial, 0);
            if (ShowBlueprints) DrawBlueprints(map);
            if (ShowCorpses) DrawCorpses(map);
            if (ShowProjectiles) DrawProjectiles(map);
            if (ShowIceVortex) DrawIceVortices(map);
            DrawFires(map);
            DrawSelectedThings(map);
        }

        static long projectilesDrawn;
        static bool projectileErrorLogged;

        /// <summary>
        /// Projectiles in flight (bullets, arrows, shells) as small tracer-yellow dots, so a firefight is visible from the far map.
        /// They move every tick, so there is nothing to cache; the list is empty outside of combat and small inside it.
        /// </summary>
        static void DrawProjectiles(Map map)
        {
            try
            {
                var list = map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile);
                if (list == null || list.Count == 0) return;
                if (projectileMaterial == null) projectileMaterial = SolidColorMaterials.SimpleSolidColorMaterial(ProjectileColor);
                float y = AltitudeLayer.MetaOverlays.AltitudeFor();
                float size = Mathf.Clamp(5f / Mathf.Max(1f, lastPx), 0.7f, 1.5f);
                var scale = new Vector3(size, 1f, size);
                int count = Math.Min(list.Count, 400);
                for (int i = 0; i < count; i++)
                {
                    var t = list[i];
                    if (t == null || !t.Spawned) continue;
                    var pos = t.DrawPos;
                    Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(new Vector3(pos.x, y, pos.z), Quaternion.identity, scale), projectileMaterial, 0);
                    projectilesDrawn++;
                }
            }
            catch (Exception e)
            {
                if (!projectileErrorLogged)
                {
                    projectileErrorLogged = true;
                    try { Verse.Log.Warning("[HSK TPS Booster] far map: could not draw projectiles: " + e.GetType().Name + " " + e.Message); } catch { }
                }
            }
        }

        static long corpsesDrawn;
        static bool corpseErrorLogged;
        static readonly List<Vector3> corpseSpots = new List<Vector3>();
        static float lastCorpseScan = -1f;
        static Map corpseScanMap;

        /// <summary>
        /// Fresh animal corpses that are not in a storage yet: hunting loot lying in the field, shown as pink spots so it is
        /// visible from the far map what is waiting to be hauled. Rotten and dessicated corpses and anything already in a
        /// stockpile or on a shelf are not shown. The spot keeps roughly a constant size on screen.
        /// Corpses do not move, so the expensive part (rot stage, storage lookup) runs only 4 times a second; every frame
        /// only replays the cached positions (Graphics.DrawMesh has to be called each frame).
        /// </summary>
        static void DrawCorpses(Map map)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (map != corpseScanMap || now - lastCorpseScan >= 0.25f)
                {
                    corpseScanMap = map;
                    lastCorpseScan = now;
                    corpseSpots.Clear();
                    var list = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
                    for (int i = 0; i < list.Count && corpseSpots.Count < 600; i++)
                    {
                        var corpse = list[i] as Corpse;
                        if (corpse == null || !corpse.Spawned) continue;
                        var pawn = corpse.InnerPawn;
                        if (pawn == null || pawn.RaceProps == null || !pawn.RaceProps.Animal) continue;
                        if (corpse.GetRotStage() != RotStage.Fresh) continue;
                        if (map.haulDestinationManager.SlotGroupAt(corpse.Position) != null) continue;   // already hauled to a storage
                        corpseSpots.Add(corpse.DrawPos);
                    }
                }
                if (corpseSpots.Count == 0) return;
                if (corpseMaterial == null) corpseMaterial = SolidColorMaterials.SimpleSolidColorMaterial(CorpseColor);
                float y = AltitudeLayer.BuildingOnTop.AltitudeFor();
                float size = Mathf.Clamp(9f / Mathf.Max(1f, lastPx), 1f, 3f);
                var scale = new Vector3(size, 1f, size);
                for (int i = 0; i < corpseSpots.Count; i++)
                {
                    var pos = corpseSpots[i];
                    Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(new Vector3(pos.x, y, pos.z), Quaternion.identity, scale), corpseMaterial, 0);
                    corpsesDrawn++;
                }
            }
            catch (Exception e)
            {
                if (!corpseErrorLogged)
                {
                    corpseErrorLogged = true;
                    try { Verse.Log.Warning("[HSK TPS Booster] far map: could not draw corpses: " + e.GetType().Name + " " + e.Message); } catch { }
                }
            }
        }

        static long blueprintsDrawn;
        static bool blueprintErrorLogged;

        /// <summary>
        /// Planned and unfinished construction: every blueprint and building frame paints its cells bright blueprint blue, so a wall
        /// being rebuilt reads as a bright line against the grey of the finished walls. No icons: the colour is the whole signal.
        /// Terrain work (floors, and HSK terraforming projects) is not shown - a terraforming zone would flood the map.
        /// </summary>
        static void DrawBlueprints(Map map)
        {
            try
            {
                var blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
                var frames = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame);
                if ((blueprints == null || blueprints.Count == 0) && (frames == null || frames.Count == 0)) return;
                if (blueprintCellMaterial == null) blueprintCellMaterial = SolidColorMaterials.SimpleSolidColorMaterial(BlueprintColor);
                int budget = 2000;   // safety cap on DrawMesh calls; far beyond any sane construction queue
                DrawBlueprintGroup(blueprints, ref budget);
                DrawBlueprintGroup(frames, ref budget);
            }
            catch (Exception e)
            {
                if (!blueprintErrorLogged)
                {
                    blueprintErrorLogged = true;
                    try { Verse.Log.Warning("[HSK TPS Booster] far map: could not draw blueprints: " + e.GetType().Name + " " + e.Message); } catch { }
                }
            }
        }

        // HSK terraforming projects ("rocky dirt", "dig water"...) are ThingDefs in the terraform designation category whose thingClass is
        // FertileFields.Building_Terraform. They cover whole zones, so they would flood the far map with hammers.
        static bool IsTerraformProject(ThingDef built)
        {
            if (built.designationCategory != null && string.Equals(built.designationCategory.defName, "SK_category_terraform", StringComparison.Ordinal)) return true;
            var tc = built.thingClass;
            return tc != null && tc.FullName != null && tc.FullName.StartsWith("FertileFields.Building_Terraform", StringComparison.Ordinal);
        }

        static void DrawBlueprintGroup(List<Thing> list, ref int budget)
        {
            if (list == null) return;
            float cellY = AltitudeLayer.BuildingOnTop.AltitudeFor();
            for (int i = 0; i < list.Count && budget > 0; i++)
            {
                var t = list[i];
                if (t == null || !t.Spawned) continue;
                var def = t.def;
                if (def == null || def.entityDefToBuild is TerrainDef) continue;   // floors: not construction worth showing here
                var builtThing = def.entityDefToBuild as ThingDef;
                if (builtThing != null && IsTerraformProject(builtThing)) continue; // HSK terraforming: fake "buildings" that swap the terrain
                budget--;
                var rect = t.OccupiedRect();
                var centre = new Vector3(rect.minX + rect.Width * 0.5f, cellY, rect.minZ + rect.Height * 0.5f);
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(centre, Quaternion.identity, new Vector3(rect.Width, 1f, rect.Height)), blueprintCellMaterial, 0);
                blueprintsDrawn++;
            }
        }

        static long iceVortexDrawn;
        static bool iceVortexErrorLogged, iceVortexResolved;
        static ThingDef iceVortexDef;

        /// <summary>
        /// HSK Core ice vortices (SK.Events.IceVortex, spawned by the IceVortex game condition) are large animated tornado-like hazards.
        /// The far map filters dynamic things down to pawns, so the vortex would vanish; here its real sprite is drawn directly, like fires,
        /// so the moving hazard and its wide animation stay visible from the zoomed-out map. Soft dependency: nothing happens without HSK Core.
        /// </summary>
        static void DrawIceVortices(Map map)
        {
            try
            {
                if (!iceVortexResolved)
                {
                    iceVortexResolved = true;
                    iceVortexDef = DefDatabase<ThingDef>.GetNamedSilentFail("IceVortex");
                }
                if (iceVortexDef == null) return;
                var list = map.listerThings.ThingsOfDef(iceVortexDef);
                if (list == null || list.Count == 0) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var vortex = list[i];
                    if (vortex == null || !vortex.Spawned) continue;
                    vortex.DrawNowAt(vortex.DrawPos);   // the real animated vortex sprite (its own DrawAt), the wide animation the player needs to see
                    iceVortexDrawn++;
                }
            }
            catch (Exception e)
            {
                if (!iceVortexErrorLogged)
                {
                    iceVortexErrorLogged = true;
                    try { Verse.Log.Warning("[HSK TPS Booster] far map: could not draw an ice vortex: " + e.GetType().Name + " " + e.Message); } catch { }
                }
            }
        }

        static long firesDrawn;
        static bool fireErrorLogged;

        /// <summary>
        /// Everything that burns is shown: the game's own animated flame texture (Fire.DrawNowAt, the same sprite the game draws) on top of a small dark orange spot
        /// that keeps it visible when a map cell is only a few pixels. Burning buildings and pawns have a Fire thing too, so they show up as well.
        /// </summary>
        static void DrawFires(Map map)
        {
            var fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            if (fires == null || fires.Count == 0) return;
            if (fireSpotMaterial == null) fireSpotMaterial = SolidColorMaterials.SimpleSolidColorMaterial(FireSpotColor);
            int count = Math.Min(fires.Count, 400);
            for (int i = 0; i < count; i++)
            {
                var fire = fires[i] as Fire;
                if (fire == null || !fire.Spawned) continue;
                try
                {
                    var pos = fire.DrawPos;
                    float size = Mathf.Clamp(0.9f + fire.fireSize * 0.8f, 1f, 2.2f);   // in cells: a bigger fire, a bigger spot
                    var spot = new Vector3(pos.x, AltitudeLayer.BuildingOnTop.AltitudeFor(), pos.z);
                    Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(spot, Quaternion.identity, new Vector3(size, 1f, size)), fireSpotMaterial, 0);
                    fire.DrawNowAt(pos);
                    firesDrawn++;
                }
                catch (Exception e)
                {
                    if (!fireErrorLogged)
                    {
                        fireErrorLogged = true;
                        try { Verse.Log.Warning("[HSK TPS Booster] far map: could not draw a fire: " + e.GetType().Name + " " + e.Message); } catch { }
                    }
                }
            }
        }

        static long selectedDrawn;
        static bool selectedErrorLogged;

        /// <summary>
        /// A building or item the player has selected is drawn as it really looks. Most things are printed into the map mesh (which is not drawn now),
        /// so the graphic is drawn directly: MapMeshOnly things with Graphic.Draw, real-time things with their own DrawAt (doors, animated benches),
        /// and things that are both get the two. Pawns are not handled here (the game draws them).
        /// </summary>
        static void DrawSelectedThings(Map map)
        {
            var selector = Find.Selector;
            if (selector == null) return;
            var list = selector.SelectedObjectsListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i] as Thing;
                if (t == null || t is Pawn || !t.Spawned || t.Map != map || t.def == null) continue;
                try
                {
                    var drawer = t.def.drawerType;
                    if (drawer == DrawerType.None) continue;
                    var pos = t.DrawPos;
                    if (drawer != DrawerType.RealtimeOnly) t.Graphic.Draw(pos, t.Rotation, t);   // the printed body
                    if (drawer != DrawerType.MapMeshOnly) t.DrawNowAt(pos);                        // real-time part: doors, animation, comps
                    selectedDrawn++;
                }
                catch (Exception e)
                {
                    if (!selectedErrorLogged)
                    {
                        selectedErrorLogged = true;
                        try { Verse.Log.Warning("[HSK TPS Booster] far map: could not draw the selected " + t + ": " + e.GetType().Name + " " + e.Message); } catch { }
                    }
                }
            }
        }

        /// <summary>One cell: door, rock, wall, an impassable storage building or other impassable building, then marsh and water (very pale), otherwise transparent so the background shows.</summary>
        static Color32 PixelFor(Map map, int index)
        {
            var b = map.edificeGrid[index];
            if (b != null)
            {
                if (b is Building_Door) return DoorColor;
                var def = b.def;
                if (def.building != null && def.building.isNaturalRock) return RockColor;
                var g = def.graphicData;
                if (g != null && (g.linkFlags & LinkFlags.Wall) != 0) return WallColor;
                if (def.passability == Traversability.Impassable
                    || (def.passability == Traversability.PassThroughOnly && (def.fillPercent >= 0.5f || def.pathCost >= 60)))
                {
                    // real structures: fully impassable (geothermal, watermill), or pass-through but either at least half filled (wind turbines
                    // and solar panels are 0.5, workbenches 0.5) or so slow to cross that pawns avoid them (Rimefeller refineries: pathCost 60,
                    // fillPercent not set). Flat walk-over devices (ground scanner 50/0.4, batteries 50/0.4, tables 30) stay hidden.
                    // Storage buildings (Adaptive Storage derives from Building_Storage) get their own pale colour.
                    return b is Building_Storage ? StorageColor : BuildingColor;
                }
            }
            var t = map.terrainGrid.TerrainAt(index);
            // only deep water (water that pawns cannot wade through); shallow water and marsh stay background so the map shows what blocks movement
            if (t != null && t.IsWater && t.passability == Traversability.Impassable) return WaterColor;
            return Clear;
        }

        static void EnsureTexture(Map map)
        {
            int w = map.Size.x, h = map.Size.z;
            bool fresh = tex == null || texMap != map || tex.width != w || tex.height != h;
            bool recolor = colorsChanged;
            colorsChanged = false;
            if (fresh)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                pixels = new Color32[w * h];
                texMap = map;
                wallMaterial = MaterialPool.MatFrom(tex, ShaderDatabase.Transparent, Color.white);
            }
            else if (activeFramesSinceLastRebuildNeeded() || recolor) fresh = true;
            if (backgroundMaterial == null) backgroundMaterial = SolidColorMaterials.SimpleSolidColorMaterial(BackgroundColor);

            if (fresh)
            {
                int n = w * h;
                for (int i = 0; i < n; i++) pixels[i] = PixelFor(map, i);
                dirty.Clear();
                tex.SetPixels32(pixels);
                tex.Apply(false);
                lastApply = Time.realtimeSinceStartup;
                needApply = false;
                rebuilds++;
                lastRebuiltFrame = Time.frameCount;
            }
        }

        // The dirty set is only filled while the mode is active, so after a pause in it (normal zoom for a while) the texture may be stale: rebuild once on re-entry.
        static int lastRebuiltFrame = -1, lastActiveFrame = -1;
        static bool activeFramesSinceLastRebuildNeeded()
        {
            int frame = Time.frameCount;
            bool gap = lastActiveFrame >= 0 && frame - lastActiveFrame > 1;
            lastActiveFrame = frame;
            return gap;
        }
    }
}
