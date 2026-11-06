using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimExodus.Maps;
using RimWorld;
using Verse;

// Native cell queries, Room getters/setters and prefab XML/placement. Topology is
// preassembled; this deliberately does not claim to run Unity or full map generation.
static class PrefabEnvironmentChecks
{
    static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    static void Set(object obj, string field, object value) => AccessTools.Field(obj.GetType(), field).SetValue(obj, value);
    static readonly BiomeDef biome = new() { inVacuum = true };
    static bool Biome(ref BiomeDef __result) { __result = biome; return false; }
    static bool Quiet(ref bool __result) { __result = false; return false; }
    static bool SkipAreaNotifications() => false;

    internal static void Run()
    {
        int count = 0;
        void Check(bool value, string label) { count++; if (!value) throw new Exception(label); }
        void Near(float actual, float expected, string label) => Check(Math.Abs(actual - expected) < 0.0001f, label + $": {actual} != {expected}");
        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, label);
        }
        var oldGame = Current.Game;
        var oldGeneratingMap = MapGenerator.mapBeingGenerated;
        var oldCulture = CultureInfo.CurrentCulture;
        bool oldProfiler = DeepProfiler.enabled;
        DeepProfiler.enabled = false;
        var harmony = new Harmony("REF.Regression.PrefabEnvironment");
        harmony.Patch(AccessTools.PropertyGetter(typeof(Map), nameof(Map.Biome)),
            prefix: new HarmonyMethod(typeof(PrefabEnvironmentChecks), nameof(Biome)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.LogVerbose)),
            prefix: new HarmonyMethod(typeof(PrefabEnvironmentChecks), nameof(Quiet)));
        // Native Area setters still write their real BoolGrid. Drawing/pathfinder
        // notifications require a live map and are outside this content fixture.
        harmony.Patch(AccessTools.Method(typeof(Area), "MarkDirty"),
            prefix: new HarmonyMethod(typeof(PrefabEnvironmentChecks), nameof(SkipAreaNotifications)));
        try
        {
            var game = Raw<Game>();
            var map = Raw<Map>();
            Set(game, "maps", new List<Map> { map }); Current.Game = game;
            map.info = new MapInfo { Size = new IntVec3(40, 1, 40) };
            map.cellIndices = new CellIndices(map);
            map.regionGrid = new RegionGrid(map);
            map.regionDirtyer = new RegionDirtyer(map);
            map.regionAndRoomUpdater = new RegionAndRoomUpdater(map);
            Set(map.regionAndRoomUpdater, "initialized", true);
            map.listerThings = new ListerThings(ListerThingsUse.Global);
            map.terrainGrid = new TerrainGrid(map);
            map.roofGrid = new RoofGrid(map);
            map.uniqueID = 77;
            var rooms = new List<Room>();
            Room RoomAt(float temperature, float vacuum, params IntVec3[] cells)
            {
                var room = Raw<Room>();
                room.ID = rooms.Count + 1;
                var district = Raw<District>(); district.mapIndex = 0;
                var region = Raw<Region>(); region.mapIndex = 0; region.type = RegionType.Normal; region.valid = true;
                region.extentsClose = CellRect.FromCellList(cells.ToList());
                Set(region, "districtInt", district); Set(region, "cachedCellCount", cells.Length);
                Set(district, "roomInt", room); Set(district, "regions", new List<Region> { region });
                Set(district, "cachedCellCount", cells.Length);
                Set(room, "districts", new List<District> { district });
                Set(room, "uniqueContainedThings", new List<Thing>());
                Set(room, "cachedCellCount", cells.Length);
                Set(room, "cachedExposedThreshold", 1);
                Set(room, "tempTracker", Raw<RoomTempTracker>());
                room.Temperature = temperature; room.Vacuum = vacuum;
                foreach (var cell in cells) map.regionGrid.SetRegionAt(cell, region);
                rooms.Add(room);
                return room;
            }
            IntVec3 C(int x, int z) => new(x, 0, z);
            var a = RoomAt(-12.5f, 0.25f, C(4,4), C(5,4));
            var b = RoomAt(31.25f, 0.75f, C(4,6));
            var outside = RoomAt(-100, 1, C(6,6)); Set(outside, "cachedOpenRoofCount", 1);
            var doc = MapPrefabs.Capture(map, new CellSelection(new[] { C(4,4), C(5,4), C(4,6), C(6,6) }), "environment");
            Check(doc.environment.Count == 2 && doc.environment.Sum(e => e.cells.Count) == 3, "capture only indoor selected cells");
            Check(doc.sourceOrigin == C(4,4) && doc.environment[0].cells[0] == IntVec3.Zero, "capture local origin");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string xml = doc.ToXml();
            doc = MapPrefabDocument.FromXml(xml);
            Near(doc.environment[0].temperature, -12.5f, "XML temperature across cultures");
            Near(doc.environment[1].vacuum, 0.75f, "XML vacuum roundtrip");
            Check(doc.mask.Count == 4 && doc.environment[0].cells.Count == 2, "XML mask and grouped cells");
            for (int turn = 0; turn < 4; turn++)
            {
                Rot4 rotation = new(turn);
                var core = doc.BuildPrefabDef();
                IntVec3 position = C(20,20), root = PrefabUtility.GetRoot(core, position, rotation);
                IntVec3 Place(IntVec3 local) => root + PrefabUtility.GetAdjustedLocalPosition(local, rotation);
                Room left = RoomAt(5, 1, doc.environment[0].cells.Select(Place).ToArray());
                Room right = RoomAt(5, 1, doc.environment[1].cells.Select(Place).ToArray());
                bool completed = false;
                var options = new PrefabPlacement { Completed = (_, _, _, _) =>
                {
                    Near(left.Temperature, -12.5f, "environment restored before Completed"); completed = true;
                } };
                var result = MapPrefabs.Rebuild(doc, map, position, rotation, options);
                Check(result.Success && completed, "production rebuild succeeds at rotation " + turn + string.Join(";", result.Errors));
                Near(left.Vacuum, .25f, "first room vacuum rotation " + turn);
                Near(right.Temperature, 31.25f, "second room temperature rotation " + turn);
                Near(right.Vacuum, .75f, "second room vacuum rotation " + turn);
                Check(MapPrefabs.Rebuild(doc, map, position, rotation, options).Success, "repeat rebuild " + turn);
            }
            IntVec3 origin = C(12,12);
            IntVec3[] all = doc.environment.SelectMany(e => e.cells).Select(c => origin + c).ToArray();
            var merged = RoomAt(0, 1, all);
            MapPrefabEnvironment.Apply(doc, map, origin, Rot4.North, null);
            Near(merged.Temperature, 2.0833333f, "merged temperature weighted by sampled cells");
            Near(merged.Vacuum, 1.25f / 3, "merged vacuum weighted by sampled cells");
            var partial = RoomAt(40, 1, all.Append(C(13,13)).ToArray());
            MapPrefabEnvironment.Apply(doc, map, origin, Rot4.North, null);
            Near(partial.Temperature, 11.5625f, "partial selection retains unsampled room contribution");
            Near(partial.Vacuum, 2.25f / 4, "partial vacuum contribution");
            partial.Temperature = 40; partial.Vacuum = 1;
            MapPrefabEnvironment.Apply(doc, map, origin, Rot4.North, (_, cell) => cell == C(13,13));
            Near(partial.Temperature, 40, "protected shared room remains unchanged");
            Near(partial.Vacuum, 1, "protected shared room vacuum remains unchanged");
            Set(partial, "cachedOpenRoofCount", 4);
            MapPrefabEnvironment.Apply(doc, map, origin, Rot4.North, null);
            Near(partial.Temperature, 40, "outdoor target not initialized");
            Set(partial, "cachedOpenRoofCount", 0); Set(partial, "cachedExposedCount", 1);
            MapPrefabEnvironment.Apply(doc, map, origin, Rot4.North, null);
            Near(partial.Vacuum, 1, "breach forces native vacuum");
            map.regionAndRoomUpdater.Enabled = false;
            MapPrefabEnvironment.Apply(doc, map, origin, Rot4.North, null);
            Check(!map.regionAndRoomUpdater.Enabled, "generation updater enabled flag restored");
            bool enabledAtCompletion = false;
            var disabledResult = MapPrefabs.Rebuild(doc, map, C(20,20), Rot4.North,
                new PrefabPlacement { Completed = (_, _, _, _) => enabledAtCompletion = map.regionAndRoomUpdater.Enabled });
            Check(disabledResult.Success && enabledAtCompletion && !map.regionAndRoomUpdater.Enabled,
                "public prefab placement enables rooms through completion and restores generation flag");
            map.regionAndRoomUpdater.Enabled = true;
            // Generation destroys Room identities after prefab restoration. Capture
            // current values, then replace the topology with native-default rooms.
            var generation = new PrefabGenerationEnvironment();
            Check(generation.CaptureBeforeRebuild(map) == null, "ordinary maps have no generation intervention");
            generation.Track(map);
            a.Temperature = 18.5f; a.Vacuum = .125f;
            b.Temperature = 15f; b.Vacuum = 0f;
            var snapshot = generation.CaptureBeforeRebuild(map);
            a = RoomAt(-100, 1, C(4,4), C(5,4));
            b = RoomAt(-100, 1, C(4,6));
            PrefabGenerationEnvironment.RestoreAfterRebuild(map, snapshot);
            Near(a.Temperature, 18.5f, "fresh room retains pre-rebuild temperature, not authored defaults");
            Near(a.Vacuum, .125f, "fresh room retains actual vacuum");
            Near(b.Temperature, 15f, "separate equipment room retains its own environment");
            Near(b.Vacuum, 0f, "sealed room remains pressurized after identity replacement");
            Near(outside.Vacuum, 1f, "generation preservation does not pressurize exterior");
            a.Temperature = 24;
            snapshot = generation.CaptureBeforeRebuild(map);
            a = RoomAt(-100, 1, C(4,4), C(5,4));
            PrefabGenerationEnvironment.RestoreAfterRebuild(map, snapshot);
            Near(a.Temperature, 24, "repeated full rebuild uses latest live state");
            generation.Forget(map);
            Check(generation.CaptureBeforeRebuild(map) == null, "generation completion/deletion releases pending map");
            var runtime = new MapRuntime(game);
            Set(game, "components", new List<GameComponent> { runtime });
            MapGenerator.mapBeingGenerated = map;
            Check(MapPrefabs.Rebuild(doc, map, C(20,20), Rot4.North, new PrefabPlacement()).Success,
                "production prefab succeeds during generation");
            Check(runtime.PrefabEnvironment.CaptureBeforeRebuild(map) != null,
                "production rebuild tracks generating map automatically");
            runtime.Forget(map);
            Check(runtime.PrefabEnvironment.CaptureBeforeRebuild(map) == null,
                "runtime map removal clears pending preservation");
            MapGenerator.mapBeingGenerated = null;
            Check(MapPrefabs.Rebuild(doc, map, C(20,20), Rot4.North, new PrefabPlacement()).Success,
                "production import succeeds on existing map");
            Check(runtime.PrefabEnvironment.CaptureBeforeRebuild(map) == null,
                "ordinary prefab import does not leave generation work");
            Reject(() => MapPrefabDocument.FromXml(xml.Replace("<temperature>-12.5</temperature>", "<temperature>NaN</temperature>")), "NaN rejected");
            Reject(() => MapPrefabDocument.FromXml(xml.Replace("<vacuum>0.25</vacuum>", "<vacuum>2</vacuum>")), "invalid vacuum rejected");
            Reject(() => MapPrefabDocument.FromXml(xml.Replace("<vacuum>0.25</vacuum>", "")), "missing vacuum rejected");
            var first = doc.environment[0]; first.cells.Add(first.cells[0]);
            Reject(() => doc.ToXml(), "duplicate cells rejected"); first.cells.RemoveAt(first.cells.Count - 1);
            first.cells.Add(C(1,1)); Reject(() => doc.ToXml(), "cells outside mask rejected"); first.cells.RemoveAt(first.cells.Count - 1);
            first.cells.Add(C(100,100)); Reject(() => doc.ToXml(), "cells outside document rejected"); first.cells.RemoveAt(first.cells.Count - 1);
            var outOfBounds = MapPrefabs.Rebuild(doc, map, C(0,0), Rot4.North, new PrefabPlacement());
            Check(!outOfBounds.Success, "placement bounds rejected before changes");
            var invalid = doc.environment[0].vacuum; doc.environment[0].vacuum = float.PositiveInfinity;
            Check(!MapPrefabs.Rebuild(doc, map, C(20,20), Rot4.North, new PrefabPlacement()).Success, "programmatic invalid document rejected");
            doc.environment[0].vacuum = invalid;
            map.areaManager = new AreaManager(map);
            foreach (Area area in new Area[] { Raw<Area_NoRoof>(), Raw<Area_BuildRoof>() })
            {
                area.areaManager = map.areaManager; Set(area, "innerGrid", new BoolGrid(map));
                map.areaManager.AllAreas.Add(area);
            }
            var roofDoc = new MapPrefabDocument { size = new IntVec2(5, 5), noRoof = new List<IntVec3> { C(1,2) } };
            roofDoc = MapPrefabDocument.FromXml(roofDoc.ToXml());
            Check(roofDoc.noRoof.SequenceEqual(new[] { C(1,2) }), "no-roof XML roundtrip");
            for (int turn = 0; turn < 4; turn++)
            {
                Rot4 rotation = new(turn);
                IntVec3 position = C(25,25), root = PrefabUtility.GetRoot(roofDoc.BuildPrefabDef(), position, rotation);
                IntVec3 cell = root + PrefabUtility.GetAdjustedLocalPosition(roofDoc.noRoof[0], rotation);
                map.areaManager.BuildRoof[cell] = true; map.areaManager.NoRoof[cell] = false;
                var blocked = MapPrefabs.Rebuild(roofDoc, map, position, rotation,
                    new PrefabPlacement { ProtectCell = (_, c) => c == cell });
                Check(blocked.Success && map.areaManager.BuildRoof[cell] && !map.areaManager.NoRoof[cell],
                    "protected roof area unchanged at rotation " + turn);
                Check(MapPrefabs.Rebuild(roofDoc, map, position, rotation, new PrefabPlacement()).Success,
                    "native roof area placement at rotation " + turn);
                Check(map.areaManager.NoRoof[cell] && !map.areaManager.BuildRoof[cell],
                    "native no-roof area suppresses build roof at rotation " + turn);
                var captured = MapPrefabs.Capture(map, new CellSelection(new[] { cell }), "roof area");
                Check(captured.noRoof.SequenceEqual(new[] { IntVec3.Zero }), "capture native roof area as local cell");
            }
            roofDoc.noRoof.Add(C(1,2)); Reject(() => roofDoc.ToXml(), "duplicate roof area rejected");
            roofDoc.noRoof.RemoveAt(1);
            roofDoc.noRoof.Add(C(5,2)); Reject(() => roofDoc.ToXml(), "roof area outside document rejected");
            roofDoc.noRoof.RemoveAt(1);
            Check(!MapPrefabs.Rebuild(roofDoc, map, C(0,0), Rot4.North, new PrefabPlacement()).Success,
                "roof area outside destination rejected before mutation");
            Console.WriteLine($"{count} prefab environment checks passed (native Room/cell/XML, preassembled topology).");
        }
        finally
        {
            Current.Game = oldGame; CultureInfo.CurrentCulture = oldCulture;
            MapGenerator.mapBeingGenerated = oldGeneratingMap;
            DeepProfiler.enabled = oldProfiler;
            harmony.UnpatchAll(harmony.Id);
        }
    }
}
