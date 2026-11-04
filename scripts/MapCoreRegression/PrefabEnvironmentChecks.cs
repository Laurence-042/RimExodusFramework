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
        var oldCulture = CultureInfo.CurrentCulture;
        bool oldProfiler = DeepProfiler.enabled;
        DeepProfiler.enabled = false;
        var harmony = new Harmony("REF.Regression.PrefabEnvironment");
        harmony.Patch(AccessTools.PropertyGetter(typeof(Map), nameof(Map.Biome)),
            prefix: new HarmonyMethod(typeof(PrefabEnvironmentChecks), nameof(Biome)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Prefs), nameof(Prefs.LogVerbose)),
            prefix: new HarmonyMethod(typeof(PrefabEnvironmentChecks), nameof(Quiet)));
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
            map.regionAndRoomUpdater.Enabled = true;
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
            Console.WriteLine($"{count} prefab environment checks passed (native Room/cell/XML, preassembled topology).");
        }
        finally
        {
            Current.Game = oldGame; CultureInfo.CurrentCulture = oldCulture;
            DeepProfiler.enabled = oldProfiler;
            harmony.UnpatchAll(harmony.Id);
        }
    }
}
