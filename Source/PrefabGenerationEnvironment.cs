using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace RimExodus.Maps
{
    // GenSteps restore prefab rooms before FinalizeInit destroys their topology.
    // Track only maps receiving prefab environment during generation; preserve the
    // latest real room values, including subsequent placements and protected rooms.
    internal sealed class PrefabGenerationEnvironment
    {
        private readonly HashSet<Map> pending = new HashSet<Map>();
        internal void Track(Map map) => pending.Add(map);
        internal void Forget(Map map) => pending.Remove(map);

        internal MapPrefabDocument CaptureBeforeRebuild(Map map)
        {
            if (!pending.Contains(map) || map.Disposed) return null;
            var snapshot = new MapPrefabDocument { size = new IntVec2(map.Size.x, map.Size.z) };
            MapPrefabEnvironment.Capture(snapshot, map, new HashSet<IntVec3>(map.AllCells));
            return snapshot;
        }

        internal static void RestoreAfterRebuild(Map map, MapPrefabDocument snapshot)
        {
            if (snapshot != null)
                MapPrefabEnvironment.Apply(snapshot, map, IntVec3.Zero, Rot4.North, null);
        }
    }

    [HarmonyPatch(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.RebuildAllRegionsAndRooms))]
    internal static class PrefabGenerationRoomsPatch
    {
        private static void Prefix(Map ___map, out MapPrefabDocument __state)
            => __state = MapRuntime.Current?.PrefabEnvironment.CaptureBeforeRebuild(___map);

        private static void Postfix(Map ___map, MapPrefabDocument __state)
            => PrefabGenerationEnvironment.RestoreAfterRebuild(___map, __state);
    }

    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
    internal static class PrefabGenerationCompletePatch
    {
        // A failed initialization must release the pending map as well.
        private static void Finalizer(Map __instance)
            => MapRuntime.Current?.PrefabEnvironment.Forget(__instance);
    }
}
