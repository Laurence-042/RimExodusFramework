using System.Runtime.CompilerServices;
using HarmonyLib;
using RimExodus.Maps;
using UnityEngine;
using Verse;

static class ArchiveCleanupChecks
{
    static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    static bool SkipEngine() => false;
    sealed class DisposableComponent : MapComponent, IDisposable
    {
        internal int calls;
        internal DisposableComponent(Map map) : base(map) { }
        public void Dispose() => calls++;
    }

    internal static void Run()
    {
        var harmony = new Harmony("REF.Regression.ArchiveCleanup");
        harmony.Patch(AccessTools.Method(typeof(Resources), nameof(Resources.UnloadUnusedAssets)),
            prefix: new HarmonyMethod(typeof(ArchiveCleanupChecks), nameof(SkipEngine)));
        harmony.Patch(AccessTools.Method(typeof(MapGenerator), nameof(MapGenerator.ClearDebugMode)),
            prefix: new HarmonyMethod(typeof(ArchiveCleanupChecks), nameof(SkipEngine)));
        int count = 0;
        try
        {
            foreach (int stage in new[] { 0, 1, 2, 3 })
            {
                var map = Raw<Map>(); map.info = new MapInfo { Size = new IntVec3(4, 1, 4) };
                var comp = new DisposableComponent(map); map.components = new List<MapComponent> { comp };
                map.mapDrawer = new MapDrawer(map);
                var section = Raw<Section>();
                AccessTools.Field(typeof(Section), "layers").SetValue(section, new List<SectionLayer>());
                AccessTools.Field(typeof(Section), "dynamic").SetValue(section, new List<SectionLayer_Dynamic>());
                if (stage > 0)
                {
                    var sections = new Section[1, stage == 2 ? 2 : 1];
                    if (stage >= 2) sections[0, 0] = section;
                    AccessTools.Field(typeof(MapDrawer), "sections").SetValue(map.mapDrawer, sections);
                }
                if (stage >= 2) AccessTools.Field(typeof(MapDrawer), "global").SetValue(map.mapDrawer, new List<MapDrawLayer>());
                ArchiveMapCleanup.Dispose(map);
                ArchiveMapCleanup.Dispose(map);
                if (!map.Disposed || comp.calls != 1) throw new Exception("Partial map disposal stage " + stage);
                count++;
            }
            Console.WriteLine($"{count} archive cleanup checks passed (real Map/MapDrawer/Section disposal; engine unload stubbed).");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}
