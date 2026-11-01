using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Xml;
using HarmonyLib;
using RimExodus.Maps;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

// Execute the production archive entry points and real Scribe. Replace only heavyweight
// Map serialization/finalization and UI/resource disposal with a minimal map fixture.
static class ArchiveContextChecks
{
    static readonly List<string> errors = new();
    static readonly List<Map> disposed = new();
    static bool failSave, failLoad, failFinalize, failAdd, mainThread = true;
    static bool finalizedWithContext, parentWithContext;
    static Map lastLoaded;
    static bool Capture(string text) { errors.Add(text); return false; }
    static bool MainThread(ref bool __result) { __result = mainThread; return false; }
    static bool NoChildren() => false;
    static bool DisposeMap(Map __instance) { disposed.Add(__instance); return false; }
    static bool ColonistBar(ref ColonistBar __result) { __result = Raw<ColonistBar>(); return false; }
    static void AfterAddMap() { if (failAdd) throw new InvalidOperationException("fixture registration notification failure"); }
    static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    static void Set(object obj, string field, object value) => AccessTools.Field(obj.GetType(), field).SetValue(obj, value);

    static void PrepareMap(Map map)
    {
        map.info ??= new MapInfo(); map.info.Size = new IntVec3(4, 1, 4);
        map.components = new List<MapComponent>();
        map.spawnedThings = new ThingOwner<Thing>(map);
        map.cellIndices = new CellIndices(map);
        map.listerThings = new ListerThings(ListerThingsUse.Global);
        map.thingGrid = new ThingGrid(map); map.edificeGrid = new EdificeGrid(map);
        map.regionGrid = new RegionGrid(map);
        map.mapPawns = new MapPawns(map);
        map.reservationManager = new ReservationManager(map);
        map.designationManager = Raw<DesignationManager>();
        var designations = Raw<DefMap<DesignationDef, List<Designation>>>();
        Set(designations, "values", Enumerable.Range(0, DefDatabase<DesignationDef>.DefCount).Select(_ => new List<Designation>()).ToList());
        Set(map.designationManager, "designationsByDef", designations);
        map.lordManager = Raw<LordManager>(); map.lordManager.lords = new List<Lord>();
    }

    static bool ExposeMap(Map __instance)
    {
        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            lastLoaded = __instance; PrepareMap(__instance);
            if (failLoad) throw new InvalidOperationException("fixture map construction failure");
        }
        Scribe_Values.Look(ref __instance.uniqueID, "uniqueID", -1);
        Scribe_Deep.Look(ref __instance.info, "mapInfo");
        List<Thing> things = new(); Scribe_Collections.Look(ref things, "things", LookMode.Deep);
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            __instance.compressor = new MapFileCompressor(__instance);
            if (failSave) throw new InvalidOperationException("fixture native save failure");
            __instance.compressor.BuildCompressedString();
            __instance.compressor.ExposeData();
            __instance.compressor = null;
        }
        return false;
    }

    static bool FinalizeMap(Map __instance)
    {
        finalizedWithContext = Current.ProgramState == ProgramState.MapInitializing
            && Scribe.mode == LoadSaveMode.Inactive && Find.Maps.Contains(__instance)
            && ScribeMetaHeaderUtility.loadedGameVersionMajor == VersionControl.CurrentMajor
            && ScribeMetaHeaderUtility.loadedGameVersionMinor == VersionControl.CurrentMinor
            && __instance.Parent?.Map == __instance;
        if (failFinalize) throw new InvalidOperationException("fixture finalize failure");
        return false;
    }

    sealed class Parent : MapParent
    {
        public override void FinalizeLoading()
        {
            parentWithContext = finalizedWithContext && Map != null
                && Current.ProgramState == ProgramState.MapInitializing;
        }
    }

    internal static void Run()
    {
        int count = 0;
        void Check(bool value, string label) { count++; if (!value) throw new Exception(label + "\n" + string.Join("\n", errors)); }
        var oldGame = Current.Game; var oldState = Current.ProgramState;
        var prefs = AccessTools.Field(typeof(Prefs), "data"); var oldPrefs = prefs.GetValue(null);
        var oldGenerating = MapGenerator.mapBeingGenerated;
        using var oldMetadata = new ArchiveMetadataScope();
        var harmony = new Harmony("REF.Regression.ArchiveContext");
        void Patch(Type type, string name, string prefix) => harmony.Patch(AccessTools.Method(type, name),
            prefix: new HarmonyMethod(typeof(ArchiveContextChecks), prefix));
        harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }),
            prefix: new HarmonyMethod(typeof(ArchiveContextChecks), nameof(Capture)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(UnityData), nameof(UnityData.IsInMainThread)),
            prefix: new HarmonyMethod(typeof(ArchiveContextChecks), nameof(MainThread)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.ColonistBar)),
            prefix: new HarmonyMethod(typeof(ArchiveContextChecks), nameof(ColonistBar)));
        Patch(typeof(Map), nameof(Map.ExposeData), nameof(ExposeMap));
        Patch(typeof(Map), nameof(Map.FinalizeLoading), nameof(FinalizeMap));
        Patch(typeof(Map), nameof(Map.Dispose), nameof(DisposeMap));
        Patch(typeof(Map), nameof(Map.GetChildHolders), nameof(NoChildren));
        harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.AddMap)),
            postfix: new HarmonyMethod(typeof(ArchiveContextChecks), nameof(AfterAddMap)));
        harmony.Patch(typeof(ModsConfig).TypeInitializer,
            prefix: new HarmonyMethod(typeof(ArchiveContextChecks), nameof(NoChildren)));
        try
        {
            prefs.SetValue(null, Raw<PrefsData>());
            var game = Raw<Game>(); var maps = new List<Map>(); Set(game, "maps", maps);
            game.components = new List<GameComponent>();
            var runtime = new MapRuntime(game); game.components.Add(runtime);
            game.currentMapIndex = -1; game.tickManager = new TickManager();
            game.questManager = new QuestManager(); game.battleLog = new BattleLog(); game.playLog = new PlayLog();
            game.drugPolicyDatabase = Raw<DrugPolicyDatabase>(); Set(game.drugPolicyDatabase, "policies", new List<DrugPolicy>());
            game.foodRestrictionDatabase = Raw<FoodRestrictionDatabase>(); Set(game.foodRestrictionDatabase, "foodRestrictions", new List<FoodPolicy>());
            game.outfitDatabase = Raw<OutfitDatabase>(); Set(game.outfitDatabase, "outfits", new List<ApparelPolicy>());
            game.readingPolicyDatabase = Raw<ReadingPolicyDatabase>(); Set(game.readingPolicyDatabase, "readingPolicies", new List<ReadingPolicy>());
            game.taleManager = new TaleManager();
            var world = Raw<World>(); Set(game, "worldInt", world);
            world.factionManager = new FactionManager(); world.worldObjects = new WorldObjectsHolder();
            world.worldPawns = new WorldPawns(); world.pocketMaps = new List<PocketMapParent>();
            Current.Game = game; Current.ProgramState = ProgramState.Playing;
            var parent = new Parent { ID = 41 }; world.worldObjects.AllWorldObjects.Add(parent);
            var map = Raw<Map>(); PrepareMap(map); map.uniqueID = 42; map.info.parent = parent; maps.Add(map);
            ScribeMetaHeaderUtility.loadedGameVersion = "0.18.0";
            ScribeMetaHeaderUtility.loadedGameVersionMajor = 0;
            ScribeMetaHeaderUtility.loadedGameVersionMinor = 18;
            ScribeMetaHeaderUtility.loadedGameVersionBuild = 1234;
            var oldMods = new List<string> { "old-context" }; ScribeMetaHeaderUtility.loadedModIdsList = oldMods;
            var oldNames = new List<string> { "Old mod" }; ScribeMetaHeaderUtility.loadedModNamesList = oldNames;
            var oldSteamIds = new List<int> { 123 }; ScribeMetaHeaderUtility.loadedModSteamIdsList = oldSteamIds;
            ScribeMetaHeaderUtility.modListChanged = true;
            var lastMode = AccessTools.Field(typeof(ScribeMetaHeaderUtility), "lastMode");
            lastMode.SetValue(null, ScribeMetaHeaderUtility.ScribeHeaderMode.Scenario);
            bool RestoredContext() => Current.ProgramState == ProgramState.Playing && ScribeOperation.IsAvailable
                && ScribeMetaHeaderUtility.loadedGameVersion == "0.18.0"
                && ScribeMetaHeaderUtility.loadedGameVersionMajor == 0 && ScribeMetaHeaderUtility.loadedGameVersionMinor == 18
                && ScribeMetaHeaderUtility.loadedGameVersionBuild == 1234 && ScribeMetaHeaderUtility.modListChanged
                && ReferenceEquals(ScribeMetaHeaderUtility.loadedModIdsList, oldMods)
                && ReferenceEquals(ScribeMetaHeaderUtility.loadedModNamesList, oldNames)
                && ReferenceEquals(ScribeMetaHeaderUtility.loadedModSteamIdsList, oldSteamIds)
                && (ScribeMetaHeaderUtility.ScribeHeaderMode)lastMode.GetValue(null) == ScribeMetaHeaderUtility.ScribeHeaderMode.Scenario;

            Check(MapArchive.TryCapture(map, out string data), "production capture succeeds with native metadata");
            using (var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(data)), CompressionMode.Decompress))
            {
                var xml = new XmlDocument(); xml.Load(gzip);
                Check(xml.DocumentElement?["meta"]?["gameVersion"]?.InnerText == VersionControl.CurrentVersionStringWithRev,
                    "captured XML records actual writer version instead of stale global version");
            }
            failSave = true;
            Check(!MapArchive.TryCapture(map, out string broken) && broken == null && map.compressor == null && RestoredContext(),
                "swallowed Map save exception rejects archive and clears native compressor");
            failSave = false;
            using (new ScribeOperation())
            {
                Check(!MapArchive.TryCapture(map, out _) && !MapArchive.TryRestore(data, 42, parent, out _),
                    "entry points reject nested lease even while Scribe is inactive");
            }
            runtime.Generation.Acquire("fixture", () => runtime.Generation.Release("fixture"));
            Check(!MapArchive.TryCapture(map, out _) && !MapArchive.TryUnload(map)
                && !MapArchive.TryRestore(data, 42, parent, out _), "archive operations defer during coordinated generation");
            runtime.Generation.Release("fixture");
            MapGenerator.mapBeingGenerated = map;
            Check(!MapArchive.TryCapture(map, out _), "uncoordinated native generation is also protected");
            MapGenerator.mapBeingGenerated = null;
            mainThread = false;
            Check(!MapArchive.TryCapture(map, out _), "background-thread caller cannot enter Scribe");
            mainThread = true;
            Check(!MapArchive.TryRestore(data, 42, parent, out _), "live map identity blocks duplicate restore");
            Check(MapArchive.TryUnload(map, preserveExternalEvents: false) && !maps.Contains(map),
                "production unload removes source before restore");
            errors.Clear(); disposed.Clear();
            for (int repeat = 0; repeat < 2; repeat++)
            {
                errors.Clear();
                finalizedWithContext = parentWithContext = false;
                Check(MapArchive.TryRestore(data, 42, parent, out Map restored), "production restore succeeds");
                Check(finalizedWithContext && parentWithContext && restored.Parent == parent && restored.uniqueID == 42,
                    "map and parent finalize with correct metadata, loading state, identity and registration");
                Check(RestoredContext(), "successful restore restores caller globals and Scribe lease");
                Check(errors.Count == 0, "successful restore emits no Scribe/context errors");
                Check(MapArchive.TryUnload(restored, preserveExternalEvents: false), "restored map can be unloaded again");
            }
            disposed.Clear(); failLoad = true;
            Check(!MapArchive.TryRestore(data, 42, parent, out _) && disposed.Contains(lastLoaded)
                && maps.Count == 0 && RestoredContext(), "Scribe-null partial map is disposed and global state restored");
            failLoad = false; disposed.Clear(); failFinalize = true;
            Check(!MapArchive.TryRestore(data, 42, parent, out _) && disposed.Contains(lastLoaded)
                && maps.Count == 0 && RestoredContext(), "post-registration failure removes partial map and restores globals");
            failFinalize = false;
            failAdd = true; disposed.Clear();
            Check(!MapArchive.TryRestore(data, 42, parent, out _) && disposed.Contains(lastLoaded)
                && maps.Count == 0 && RestoredContext(), "AddMap notification failure rolls back actual registration");
            failAdd = false;
            disposed.Clear();
            Check(!MapArchive.TryRestore(data, 43, parent, out _) && disposed.Contains(lastLoaded)
                && maps.Count == 0 && RestoredContext(), "wrong archive identity is rejected before registration");
            Check(!MapArchive.TryRestore("bad base64", 42, parent, out _) && RestoredContext(), "malformed archive leaves caller untouched");
            var noHeader = new XmlDocument();
            using (var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(data)), CompressionMode.Decompress)) noHeader.Load(gzip);
            noHeader.DocumentElement.RemoveChild(noHeader.DocumentElement["meta"]);
            using (var bytes = new MemoryStream())
            {
                using (var gzip = new GZipStream(bytes, CompressionLevel.Optimal, true)) noHeader.Save(gzip);
                lastLoaded = null;
                Check(!MapArchive.TryRestore(Convert.ToBase64String(bytes.ToArray()), 42, parent, out _)
                    && lastLoaded == null && RestoredContext(), "headerless snapshot is rejected before construction without borrowing caller metadata");
            }
            for (int i = 0; i < 128; i++) { var occupied = Raw<Map>(); occupied.uniqueID = 1000 + i; occupied.info = new MapInfo(); maps.Add(occupied); }
            lastLoaded = null;
            Check(!MapArchive.TryRestore(data, 42, parent, out _) && lastLoaded == null && RestoredContext(),
                "native map capacity is checked before any Map construction");
            maps.Clear();
            Console.WriteLine($"{count} archive context checks passed (production capture/unload/restore + real Scribe; map/UI fixture).");
        }
        finally
        {
            Scribe.ForceStop(); Current.Game = oldGame; Current.ProgramState = oldState;
            prefs.SetValue(null, oldPrefs);
            MapGenerator.mapBeingGenerated = oldGenerating;
            failSave = failLoad = failFinalize = failAdd = false; mainThread = true;
            harmony.UnpatchAll(harmony.Id);
        }
    }
}
