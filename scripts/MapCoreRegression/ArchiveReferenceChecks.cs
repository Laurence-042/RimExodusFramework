using System.Runtime.CompilerServices;
using HarmonyLib;
using RimExodus.Maps;
using RimWorld;
using RimWorld.Planet;
using Verse;

static class ArchiveReferenceChecks
{
    static readonly List<string> messages = new();
    static bool Capture(string text) { messages.Add(text); return false; }
    static bool Skip() => false;
    static bool Ideology(ref bool __result) { __result = true; return false; }
    static bool FixtureType(string typeName, ref Type __result)
    {
        if (typeName != typeof(References).FullName) return true;
        __result = typeof(References); return false;
    }
    static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    static void Set(object obj, string field, object value) => AccessTools.Field(obj.GetType(), field).SetValue(obj, value);

    sealed class WorldHolder : WorldObject, IThingHolder
    {
        internal readonly ThingOwner<Thing> contents;
        public WorldHolder() { contents = new ThingOwner<Thing>(this); }
        public ThingOwner GetDirectlyHeldThings() => contents;
        public void GetChildHolders(List<IThingHolder> result) { }
    }

    public sealed class References : IExposable
    {
        public Battle battle;
        public LogEntry combat, play;
        public Precept precept;
        public DrugPolicy drug;
        public FoodPolicy food;
        public ApparelPolicy outfit;
        public ReadingPolicy reading;
        public Tale tale;
        public Thing remoteItem;
        public void ExposeData()
        {
            Scribe_References.Look(ref battle, "battleActive");
            Scribe_References.Look(ref combat, "combatLogEntry");
            Scribe_References.Look(ref play, "playLogEntry");
            Scribe_References.Look(ref precept, "sourcePrecept");
            Scribe_References.Look(ref drug, "curAssignedDrugs");
            Scribe_References.Look(ref food, "curRestriction");
            Scribe_References.Look(ref outfit, "curOutfit");
            Scribe_References.Look(ref reading, "curAssignment");
            Scribe_References.Look(ref tale, "tale");
            Scribe_References.Look(ref remoteItem, "remoteItem");
        }
    }

    internal static void Run()
    {
        int count = 0;
        void Check(bool value, string label) { count++; if (!value) throw new Exception(label); }
        var originalGame = Current.Game;
        var prefs = AccessTools.Field(typeof(Prefs), "data");
        var originalPrefs = prefs.GetValue(null);
        var harmony = new Harmony("REF.Regression.ArchiveReferences");
        using var locked = Log.LockMessages();
        // This fixture has no enabled DLC or filesystem-backed game configuration.
        harmony.Patch(typeof(ModsConfig).TypeInitializer,
            prefix: new HarmonyMethod(typeof(ArchiveReferenceChecks), nameof(Skip)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(ModsConfig), nameof(ModsConfig.IdeologyActive)),
            prefix: new HarmonyMethod(typeof(ArchiveReferenceChecks), nameof(Ideology)));
        // The fixture assembly is not a loaded Mod; only supply its wrapper type.
        harmony.Patch(AccessTools.Method(typeof(GenTypes), nameof(GenTypes.GetTypeInAnyAssembly)),
            prefix: new HarmonyMethod(typeof(ArchiveReferenceChecks), nameof(FixtureType)));
        foreach (string method in new[] { "Error", "Warning" })
            harmony.Patch(AccessTools.Method(typeof(Log), method, new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(ArchiveReferenceChecks), nameof(Capture)));
        string path = Path.Combine(Path.GetTempPath(), "ref-archive-refs-" + Guid.NewGuid() + ".xml");
        try
        {
            var settings = Raw<PrefsData>(); settings.devMode = true; prefs.SetValue(null, settings);
            var game = Raw<Game>(); var world = Raw<World>();
            Set(game, "maps", new List<Map>()); Set(game, "worldInt", world);
            game.questManager = new QuestManager();
            game.battleLog = new BattleLog(); game.playLog = new PlayLog();
            game.drugPolicyDatabase = Raw<DrugPolicyDatabase>(); Set(game.drugPolicyDatabase, "policies", new List<DrugPolicy>());
            game.foodRestrictionDatabase = Raw<FoodRestrictionDatabase>(); Set(game.foodRestrictionDatabase, "foodRestrictions", new List<FoodPolicy>());
            game.outfitDatabase = Raw<OutfitDatabase>(); Set(game.outfitDatabase, "outfits", new List<ApparelPolicy>());
            game.readingPolicyDatabase = Raw<ReadingPolicyDatabase>(); Set(game.readingPolicyDatabase, "readingPolicies", new List<ReadingPolicy>());
            game.taleManager = new TaleManager();
            world.factionManager = new FactionManager(); world.worldObjects = new WorldObjectsHolder();
            world.worldPawns = new WorldPawns(); world.pocketMaps = new List<PocketMapParent>();
            world.ideoManager = new IdeoManager();
            Current.Game = game;
            var battle = new Battle(); Set(battle, "loadID", 24);
            var entry = Raw<BattleLogEntry_RangedFire>(); Set(entry, "logID", 25); Set(entry, "ticksAbs", 100);
            var playEntry = Raw<PlayLogEntry_Interaction>(); Set(playEntry, "logID", 26); Set(playEntry, "ticksAbs", 101);
            battle.Entries.Add(entry); game.battleLog.Battles.Add(battle); game.playLog.AllEntries.Add(playEntry);
            var parent = Raw<PocketMapParent>(); parent.ID = 27; world.pocketMaps.Add(parent);
            var faction = Raw<Faction>(); faction.loadID = 28; world.factionManager.AllFactionsListForReading.Add(faction);
            var ideo = new Ideo(); Set(ideo, "id", 29); world.ideoManager.IdeosListForReading.Add(ideo);
            var precept = new Precept_ThingStyle(); Set(precept, "ID", 30); ideo.PreceptsListForReading.Add(precept);
            var drug = new DrugPolicy { id = 31 }; game.drugPolicyDatabase.AllPolicies.Add(drug);
            var food = new FoodPolicy { id = 32 }; game.foodRestrictionDatabase.AllFoodRestrictions.Add(food);
            var outfit = new ApparelPolicy { id = 33 }; game.outfitDatabase.AllOutfits.Add(outfit);
            var reading = new ReadingPolicy { id = 34 }; game.readingPolicyDatabase.AllReadingPolicies.Add(reading);
            var tale = new Tale { id = 35 }; game.taleManager.AllTalesListForReading.Add(tale);
            var holder = new WorldHolder { ID = 36 }; world.worldObjects.AllWorldObjects.Add(holder);
            var remoteItem = new Thing { def = new ThingDef { defName = "HeldFixture", category = ThingCategory.Item }, thingIDNumber = 37 };
            ((List<Thing>)AccessTools.Field(typeof(ThingOwner<Thing>), "innerList").GetValue(holder.contents)).Add(remoteItem);
            var refs = MapArchive.ExternalReferences(null).ToArray();
            Check(refs.Contains(battle) && refs.Contains(entry) && refs.Contains(playEntry)
                && refs.Contains(parent) && refs.Contains(faction), "archive source includes global battle/play logs and parent/faction");
            Check(new ILoadReferenceable[] { precept, drug, food, outfit, reading, tale }.All(refs.Contains),
                "archive includes native style precepts, pawn policies and art tales");
            Check(refs.Contains(remoteItem), "world-object held items are included in external references");

            messages.Clear();
            Scribe.saver.InitSaving(path, "archiveReferenceTest");
            foreach (var value in refs) Scribe.saver.loadIDsErrorsChecker.RegisterReferenced(value, "external");
            // The same object may be reached twice; a deeply saved object must not be registered twice.
            Scribe.saver.loadIDsErrorsChecker.RegisterDeepSaved(faction, "internal");
            MapArchive.RegisterSavingExternalReferences(refs.Concat(refs), new[] { faction });
            Scribe.saver.FinalizeSaving();
            Check(messages.Count == 0, "known external references produce no false missing/duplicate warnings");

            var unknown = new Battle(); Set(unknown, "loadID", 99);
            Scribe.saver.InitSaving(path, "archiveReferenceTest");
            Scribe.saver.loadIDsErrorsChecker.RegisterReferenced(unknown, "unknown");
            MapArchive.RegisterSavingExternalReferences(refs, Array.Empty<ILoadReferenceable>());
            Scribe.saver.FinalizeSaving();
            Check(messages.Any(m => m.Contains("Battle_99") && m.Contains("not deep-saved")), "unknown external ID still warns");

            messages.Clear();
            for (int repeat = 0; repeat < 2; repeat++)
            {
                Scribe.loader.InitLoading(path);
                MapArchive.RegisterExternalReferences(null);
                var directory = (LoadedObjectDirectory)AccessTools.Field(typeof(CrossRefHandler), "loadedObjectDirectory")
                    .GetValue(Scribe.loader.crossRefs);
                Check(ReferenceEquals(directory.ObjectWithLoadID<Battle>(battle.GetUniqueLoadID()), battle), "battle resolves to current game's instance");
                Check(ReferenceEquals(directory.ObjectWithLoadID<LogEntry>(entry.GetUniqueLoadID()), entry)
                    && ReferenceEquals(directory.ObjectWithLoadID<LogEntry>(playEntry.GetUniqueLoadID()), playEntry), "combat and play entries resolve without copies");
                Check(messages.Count == 0, "external directory registration has no duplicate errors");
                Scribe.ForceStop();
                Scribe.loader.InitLoading(path);
                bool collision = false;
                try { MapArchive.RegisterExternalReferences(null, new HashSet<string> { battle.GetUniqueLoadID() }); }
                catch (InvalidDataException) { collision = true; }
                Check(collision, "external/internal identity collision rejects restoration");
                Scribe.ForceStop();
                messages.Clear();
            }
            Check(Scribe.mode == LoadSaveMode.Inactive, "reference registration leaves no active loader");
            var referenceSet = new References { battle = battle, combat = entry, play = playEntry,
                precept = precept, drug = drug, food = food, outfit = outfit, reading = reading, tale = tale, remoteItem = remoteItem };
            Scribe.saver.InitSaving(path, "archiveReferenceTest");
            Scribe_Deep.Look(ref referenceSet, "references");
            MapArchive.RegisterSavingExternalReferences(refs, Array.Empty<ILoadReferenceable>());
            Scribe.saver.FinalizeSaving();
            for (int repeat = 0; repeat < 2; repeat++)
            {
                References restored = null;
                Scribe.loader.InitLoading(path);
                Scribe_Deep.Look(ref restored, "references");
                MapArchive.RegisterExternalReferences(null);
                Scribe.loader.FinalizeLoading();
                Check(ReferenceEquals(restored.battle, battle) && ReferenceEquals(restored.combat, entry)
                    && ReferenceEquals(restored.play, playEntry), "Scribe cross-reference roundtrip retains external instances");
                Check(ReferenceEquals(restored.precept, precept) && ReferenceEquals(restored.drug, drug)
                    && ReferenceEquals(restored.food, food) && ReferenceEquals(restored.outfit, outfit)
                    && ReferenceEquals(restored.reading, reading) && ReferenceEquals(restored.tale, tale),
                    "Scribe roundtrip retains original style, policy and tale instances");
                Check(ReferenceEquals(restored.remoteItem, remoteItem), "Scribe resolves a world-holder item to its current instance");
                Check(messages.Count == 0, "Scribe roundtrip has no missing references: " + string.Join("\n", messages));
            }
            Console.WriteLine($"{count} archive reference checks passed (real 1.7 Scribe checker/directory; no map lifecycle).");
        }
        finally
        {
            Scribe.ForceStop(); Current.Game = originalGame; prefs.SetValue(null, originalPrefs);
            harmony.UnpatchAll(harmony.Id); File.Delete(path);
        }
    }
}
