using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>All mutable map service state belongs to this game, never to a process-wide registry.</summary>
    public sealed class MapRuntime : GameComponent
    {
        public readonly MapTickRates TickRates = new MapTickRates();
        public readonly MapGenerationCoordinator Generation = new MapGenerationCoordinator();
        public readonly CrossMapCombat Combat = new CrossMapCombat();
        public readonly MapRenderService Rendering = new MapRenderService();
        public readonly MapSpatialQueries Spatial = new MapSpatialQueries();
        internal readonly PrefabGenerationEnvironment PrefabEnvironment = new PrefabGenerationEnvironment();
        private readonly Dictionary<Map, string> owners = new Dictionary<Map, string>();
        private readonly Dictionary<Map, int> generations = new Dictionary<Map, int>();
        private readonly Dictionary<string, Func<Map, bool>> stopPolicies = new Dictionary<string, Func<Map, bool>>();
        private int nextGeneration;
        public MapRuntime(Game game) { }
        public override void GameComponentTick() => TickRates.ApplyPositionChanges();
        public static MapRuntime Current => Verse.Current.Game?.GetComponent<MapRuntime>();
        public void SetStopPolicy(string owner, Func<Map, bool> policy) => stopPolicies[owner] = policy;
        public bool StoppedByPolicy(Map map)
        {
            if (map == null) return false;
            foreach (Func<Map, bool> policy in stopPolicies.Values) if (policy(map)) return true;
            return false;
        }

        public int Register(Map map, string owner)
        {
            if (map == null || map.Disposed) throw new ArgumentException("A live map is required.", nameof(map));
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("An owner is required.", nameof(owner));
            if (owners.TryGetValue(map, out string existing) && existing != owner)
                throw new InvalidOperationException($"Map {map.uniqueID} is already owned by {existing}.");
            owners[map] = owner;
            if (!generations.ContainsKey(map)) generations[map] = checked(++nextGeneration);
            return generations[map];
        }

        public bool IsCurrent(Map map, int generation) => map != null && !map.Disposed
            && owners.ContainsKey(map) && generations.TryGetValue(map, out int value) && value == generation;

        public void Forget(Map map)
        {
            if (map == null) return;
            owners.Remove(map);
            generations.Remove(map);
            TickRates.Forget(map);
            Combat.Forget(map);
            Rendering.Forget(map);
            PrefabEnvironment.Forget(map);
        }
    }

    public static class MapModule
    {
        private static bool installed;
        private static Exception installationFailure;
        private static readonly object InstallationLock = new object();
        public static bool TryInstall()
        {
            lock (InstallationLock)
            {
                if (installationFailure != null) return false;
                try { Install(); return true; }
                catch (Exception error)
                {
                    Log.Error($"[RimExodus.Maps] Module installation failed and will not be retried until restart. "
                        + $"The shared backend is unavailable; consumers must not use its services. {error}");
                    return false;
                }
            }
        }
        public static void Install()
        {
            lock (InstallationLock)
            {
                if (installed) return;
                if (installationFailure != null)
                    throw new InvalidOperationException("Map module installation previously failed; restart after correcting the cause.", installationFailure);
                var harmony = new Harmony("RimExodus.Maps");
                try
                {
                    InstallCorePatches(harmony);
                    LongEventHandler.ExecuteWhenFinished(() => MapPreviewGenerationPatch.Register(harmony));
                    LongEventHandler.ExecuteWhenFinished(() => SurveillanceMapClippersCompat.Register(harmony));
#if RIMWORLD_1_7
                    LongEventHandler.ExecuteWhenFinished(() => IsoRimCullingCompat.Register(harmony));
#endif
                    LongEventHandler.ExecuteWhenFinished(() => ReportBindings(harmony));
                    installed = true;
                }
                catch (Exception error)
                {
                    installationFailure = error;
                    try { harmony.UnpatchAll(harmony.Id); }
                    catch (Exception cleanup) { throw new AggregateException("Map module installation and rollback failed.", error, cleanup); }
                    throw;
                }
            }
        }
        internal static void InstallCorePatches(Harmony harmony)
        {
            // ClassProcessor also recognizes convention-named Prefix/Finalizer methods on
            // unannotated helpers. Soft compatibility hooks must reach their manual Register path.
            foreach (var type in typeof(MapModule).Assembly.GetTypes())
                if (Attribute.IsDefined(type, typeof(HarmonyPatch))
                    && !Attribute.IsDefined(type, typeof(CombatPatchAttribute)))
                    harmony.CreateClassProcessor(type).Patch();
        }

        internal static void ReportBindings(Harmony harmony)
        {
            int count = 0;
            foreach (var method in harmony.GetPatchedMethods())
            {
                count++;
                Log.Message($"[REF:{harmony.Id}] patched: {method.DeclaringType?.FullName}::{method.Name}");
            }
            Log.Message($"[REF:{harmony.Id}] GetPatchedMethods: {count} methods.");
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.DeinitAndRemoveMap))]
    internal static class MapRemovedPatch
    {
        private static void Postfix(Map map) => MapRuntime.Current?.Forget(map);
    }
}
