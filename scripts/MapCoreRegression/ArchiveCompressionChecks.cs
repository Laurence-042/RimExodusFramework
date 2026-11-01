using System.Runtime.CompilerServices;
using System.Xml;
using HarmonyLib;
using RimExodus.Maps;
using Verse;
using Verse.AI;
using Verse.AI.Group;

static class ArchiveCompressionChecks
{
    static readonly List<string> errors = new();
    static bool Capture(string text) { errors.Add(text); return false; }
    static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    static void Set(object obj, string field, object value) => AccessTools.Field(obj.GetType(), field).SetValue(obj, value);

    internal static void Run()
    {
        int count = 0;
        void Check(bool value, string label) { count++; if (!value) throw new Exception(label); }
        var originalGame = Current.Game;
        var harmony = new Harmony("REF.Regression.ArchiveCompression");
        harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }),
            prefix: new HarmonyMethod(typeof(ArchiveCompressionChecks), nameof(Capture)));
        harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.ErrorOnce), new[] { typeof(string), typeof(int) }),
            prefix: new HarmonyMethod(typeof(ArchiveCompressionChecks), nameof(Capture)));
        using var locked = Log.LockMessages();
        string path = Path.Combine(Path.GetTempPath(), "ref-compression-" + Guid.NewGuid() + ".xml");
        try
        {
            var game = Raw<Game>();
            var map = Raw<Map>();
            Set(game, "maps", new List<Map> { map }); Current.Game = game;
            map.info = new MapInfo { Size = new IntVec3(4, 1, 4) };
            map.cellIndices = new CellIndices(map);
            map.edificeGrid = new EdificeGrid(map);
            map.thingGrid = new ThingGrid(map);
            map.listerThings = new ListerThings(ListerThingsUse.Global);
            map.designationManager = Raw<DesignationManager>();
            var designations = Raw<DefMap<DesignationDef, List<Designation>>>();
            Set(designations, "values", Enumerable.Range(0, DefDatabase<DesignationDef>.DefCount).Select(_ => new List<Designation>()).ToList());
            Set(map.designationManager, "designationsByDef", designations);
            map.reservationManager = new ReservationManager(map);
            map.mapPawns = new MapPawns(map);
            map.lordManager = Raw<LordManager>(); map.lordManager.lords = new List<Lord>();
            Thing Make(int id, bool compressible, bool saveable = true)
            {
                var thing = Raw<Thing>();
                thing.def = new ThingDef { defName = "ArchiveFixture" + id, category = ThingCategory.Item,
                    isSaveable = saveable, saveCompressible = compressible, useHitPoints = false, shortHash = (ushort)id };
                thing.thingIDNumber = id;
                thing.SetPositionDirect(new IntVec3(id % 4, 0, id / 4));
                Set(thing, "mapIndexOrState", (sbyte)0);
                map.listerThings.AllThings.Add(thing);
                map.thingGrid.ThingsListAt(thing.Position).Add(thing);
                return thing;
            }
            Thing compressed = Make(1, true), referenced = Make(2, true), full = Make(3, false), transient = Make(4, false, false);
            var reservations = new List<ReservationManager.Reservation>
            { new(null, null, 1, 1, referenced, null) };
            Set(map.reservationManager, "reservations", reservations);

            // Reproduce the old post-save call with no Map compressor installed.
            errors.Clear();
            Check(referenced.IsSaveCompressible(), "old post-save predicate wrongly treats reserved thing as compressible");
            Check(errors.Any(e => e.Contains("no maps with compressor")), "old post-save predicate reproduces reported error");
            errors.Clear();
            Scribe.saver.InitSaving(path, "compressionFixture");
            var required = MapArchive.RequiredSavedThingIds(map);
            Check(required.SetEquals(new[] { referenced.ThingID, full.ThingID }), "native reference-aware manifest keeps reserved/full things only");
            Check(map.compressor == null && errors.Count == 0, "manifest restores null compressor without error");

            // Match vanilla's actual compression pass, then validate after it releases the context.
            map.compressor = new MapFileCompressor(map);
            map.compressor.BuildCompressedString();
            Check(required.SetEquals(map.listerThings.AllThings.Where(t => t.def.isSaveable && !t.IsSaveCompressible()).Select(t => t.ThingID)),
                "manifest matches native writer classification");
            map.compressor.ExposeData();
            map.compressor = null;
            Scribe.saver.FinalizeSaving();
            var document = new XmlDocument();
            document.LoadXml($"<archivedMap><map><things><thing><id>{referenced.ThingID}</id></thing><thing><id>{full.ThingID}</id></thing></things></map></archivedMap>");
            MapArchive.ValidateSavedThingIds(document, required);
            Check(errors.Count == 0 && Scribe.mode == LoadSaveMode.Inactive, "post-save verification needs no compression context");
            foreach (string missing in required)
            {
                var broken = (XmlDocument)document.CloneNode(true);
                XmlNode node = broken.SelectSingleNode($"//thing[id='{missing}']"); node.ParentNode.RemoveChild(node);
                bool rejected = false;
                try { MapArchive.ValidateSavedThingIds(broken, required); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "missing required ID is rejected: " + missing);
            }
            var previous = new MapFileCompressor(map);
            map.compressor = previous;
            Check(required.SetEquals(MapArchive.RequiredSavedThingIds(map)) && ReferenceEquals(map.compressor, previous),
                "manifest preserves previous compressor");
            map.listerThings.AllThings.Add(null);
            bool failed = false;
            try { MapArchive.RequiredSavedThingIds(map); }
            catch (NullReferenceException) { failed = true; }
            Check(failed && ReferenceEquals(map.compressor, previous), "failed manifest restores previous compressor");
            map.listerThings.AllThings.Remove(null); map.compressor = null;
            reservations.Clear();
            Check(MapArchive.RequiredSavedThingIds(map).SetEquals(new[] { full.ThingID }), "next capture rebuilds references without stale state");
            Check(errors.Count == 0, "all fixed paths avoid compression-context errors");
            Console.WriteLine($"{count} archive compression checks passed (real 1.7 compression rules; no full map lifecycle).");
        }
        finally
        {
            Scribe.ForceStop(); Current.Game = originalGame;
            harmony.UnpatchAll(harmony.Id); File.Delete(path);
        }
    }
}
