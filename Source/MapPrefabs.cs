using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus.Maps
{
    public sealed class PrefabPlacement
    {
        public Faction Faction;
        public bool Clone = true;
        public bool OmitSpace;
        public MapParent Parent;
        public Action<Map, IntVec3> FoundationsPlaced;
        public Action<Thing, MapPrefabThingEntry, IntVec3, Rot4> BeforeSpawn;
        public Func<Thing, Map, IntVec3, Rot4, Thing> PlaceThing;
        public Action<Map, List<Thing>, IntVec3, Rot4> Completed;
    }
    public sealed class PrefabPlacementResult
    {
        public Map Map;
        public readonly List<Thing> Things = new List<Thing>();
        public readonly List<string> Errors = new List<string>();
        public bool Success => Errors.Count == 0;
    }

    /// <summary>One region capture/rebuild entry point. Full-map state is a native persistent-state layer.</summary>
    public static class MapPrefabs
    {
        public static MapPrefabDocument Capture(Map map, IRegionSelection selection, string name,
            Func<Thing, bool> include = null, Func<IEnumerable<Thing>, IEnumerable<Thing>> order = null,
            bool includeMapState = false)
        {
            if (map == null || map.Disposed) throw new ArgumentException("A live source map is required.");
            var cells = new HashSet<IntVec3>(selection.Cells(map));
            if (cells.Count == 0 || cells.Any(c => !c.InBounds(map)))
                throw new ArgumentException("Selection must be nonempty and inside the source map.");
            CellRect bounds = CellRect.FromCellList(cells.ToList());
            var doc = new MapPrefabDocument { name = name, size = bounds.Size,
                sourceMapId = map.uniqueID, sourceOrigin = bounds.Min,
                mask = cells.OrderBy(c => c.z).ThenBy(c => c.x).Select(c => c - bounds.Min).ToList() };
            if (includeMapState)
            {
                if (cells.Count != map.Size.x * map.Size.z)
                    throw new ArgumentException("Map-level persistent state requires the complete map selection.");
                if (!MapArchive.TryCapture(map, out doc.mapState))
                    throw new InvalidDataException("Could not capture native map state.");
                return doc;
            }
            IEnumerable<Thing> things = map.listerThings.AllThings.Where(t => cells.Contains(t.Position)
                && (include?.Invoke(t) ?? (t.def.category == ThingCategory.Building || t.def.category == ThingCategory.Item)));
            things = order != null ? order(things) : things.OrderBy(t => t.def.building?.isAttachment == true ? 1 : 0)
                .ThenByDescending(t => t.def.category == ThingCategory.Building).ThenBy(t => t.thingIDNumber);
            foreach (Thing thing in things)
                doc.thingEntries.Add(new MapPrefabThingEntry { pos = thing.Position - bounds.Min, def = thing.def,
                    stuff = thing.Stuff, rotation = thing.Rotation, hitPoints = thing.HitPoints, stackCount = thing.stackCount,
                    stateXml = MapPrefabThingState.Capture(thing) });
            foreach (IntVec3 cell in cells)
            {
                IntVec3 local = cell - bounds.Min;
                TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
                if (terrain != null && !terrain.isFoundation)
                    doc.terrain.Add(new PrefabTerrainData { def = terrain, color = map.terrainGrid.ColorAt(cell),
                        rects = new List<CellRect> { new CellRect(local.x, local.z, 1, 1) } });
                TerrainDef foundation = map.terrainGrid.FoundationAt(cell);
                if (foundation != null) doc.foundations.Add(new PrefabFoundationEntry { pos = local, def = foundation });
                RoofDef roof = map.roofGrid.RoofAt(cell);
                if (roof != null) doc.roofs.Add(new PrefabRoofEntry { pos = local, def = roof });
            }
            return doc;
        }

        public static PrefabPlacementResult Rebuild(MapPrefabDocument doc, Map map, IntVec3 position, Rot4 rot,
            PrefabPlacement options)
        {
            var result = new PrefabPlacementResult();
            if (doc == null || options == null) throw new ArgumentNullException(doc == null ? nameof(doc) : nameof(options));
            if (!string.IsNullOrEmpty(doc.mapState))
            {
                if (options.Clone || map != null || options.Parent == null)
                    throw new ArgumentException("Native map state requires identity-preserving reconstruction and an explicit parent.");
                if (!MapArchive.TryRestore(doc.mapState, doc.sourceMapId, options.Parent, out result.Map))
                    result.Errors.Add("Could not restore native map state; retain the prefab document for retry.");
                return result;
            }
            if (map == null || map.Disposed) throw new ArgumentException("A live destination map is required.", nameof(map));
            result.Map = map;
            PrefabDef terrain = doc.BuildPrefabDef(options.OmitSpace);
            Rot4 rotation = PrefabUtility.ValidateRotation(terrain, rot);
            IntVec3 root = PrefabUtility.GetRoot(terrain, position, rotation);
            // Validate every footprint and decode every instance before touching the destination.
            foreach (IntVec3 cell in doc.mask)
                if (!(root + PrefabUtility.GetAdjustedLocalPosition(cell, rotation)).InBounds(map))
                    result.Errors.Add("Selected region extends outside the destination map.");
            foreach (MapPrefabThingEntry entry in doc.thingEntries)
            {
                IntVec3 cell = root + PrefabUtility.GetAdjustedThingLocalPosition(entry.def, rotation, entry.pos);
                Rot4 facing = entry.def.rotatable ? rotation.Rotated(Rot4.GetRelativeRotation(Rot4.North, entry.rotation)) : entry.def.defaultPlacingRot;
                if (!GenAdj.OccupiedRect(cell, facing, entry.def.Size).InBounds(map)) result.Errors.Add($"Out of bounds: {entry.def.defName}.");
            }
            if (!result.Success) return result;
            List<Thing> loaded;
            try { loaded = MapPrefabThingState.Instantiate(doc.thingEntries, doc.name, options.Faction, options.Clone); }
            catch (Exception error) { result.Errors.Add(error.ToString()); return result; }
            IntVec3 first = IntVec3.Invalid;
            foreach (PrefabFoundationEntry entry in doc.foundations)
            {
                IntVec3 cell = root + PrefabUtility.GetAdjustedLocalPosition(entry.pos, rotation);
                map.terrainGrid.SetFoundation(cell, entry.def);
                if (map.terrainGrid.FoundationAt(cell) != entry.def)
                { result.Errors.Add($"Foundation rejected at {cell}."); return result; }
                if (!first.IsValid) first = cell;
            }
            options.FoundationsPlaced?.Invoke(map, first);
            PrefabUtility.SpawnPrefab(terrain, map, position, rotation, options.Faction);
            for (int i = 0; i < loaded.Count; i++)
            {
                Thing thing = loaded[i];
                MapPrefabThingEntry entry = doc.thingEntries[i];
                IntVec3 cell = root + PrefabUtility.GetAdjustedThingLocalPosition(entry.def, rotation, entry.pos);
                Rot4 facing = entry.def.rotatable ? rotation.Rotated(Rot4.GetRelativeRotation(Rot4.North, entry.rotation)) : entry.def.defaultPlacingRot;
                try
                {
                    if (options.Clone) { thing.spawnedTick = Find.TickManager.TicksGame; thing.despawnedTick = -1; }
                    options.BeforeSpawn?.Invoke(thing, entry, root, rotation);
                    Thing placed = options.PlaceThing != null ? options.PlaceThing(thing, map, cell, facing)
                        : GenSpawn.Spawn(thing, cell, map, facing, WipeMode.Vanish, respawningAfterLoad: true);
                    if (placed != null) result.Things.Add(placed);
                    else if (!thing.Destroyed) result.Errors.Add($"Placement rejected: {entry.def.defName} at {cell}.");
                }
                catch (Exception error) { result.Errors.Add($"{entry.def.defName} at {cell}: {error}"); }
            }
            foreach (PrefabRoofEntry entry in doc.roofs)
                map.roofGrid.SetRoof(root + PrefabUtility.GetAdjustedLocalPosition(entry.pos, rotation), entry.def);
            if (result.Success) options.Completed?.Invoke(map, result.Things, root, rotation);
            return result;
        }

        public static bool CaptureWholeMap(Map map, out string data)
        {
            data = null;
            try { data = Capture(map, new WholeMapSelection(), "map-" + map.uniqueID, includeMapState: true).mapState; return true; }
            catch (Exception error) { Log.Error($"[RimExodus.Maps] Map capture failed: {error}"); return false; }
        }
        public static bool RestoreWholeMap(string data, int mapId, MapParent parent, out Map map)
        {
            map = null;
            if (string.IsNullOrEmpty(data) || parent == null) return false;
            var doc = new MapPrefabDocument { mapState = data, sourceMapId = mapId };
            PrefabPlacementResult result = Rebuild(doc, null, IntVec3.Zero, Rot4.North,
                new PrefabPlacement { Clone = false, Parent = parent });
            map = result.Map;
            return result.Success;
        }
    }
}
