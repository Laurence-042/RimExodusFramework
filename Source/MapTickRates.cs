using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace RimExodus.Maps
{
    public sealed class MapTickRates
    {
        private sealed class State
        {
            public string Owner;
            public int Percent;
            public Func<Thing, bool> FullSpeed;
            public bool SuppressUpdate;
        }
        private readonly Dictionary<Map, State> states = new Dictionary<Map, State>();
        private readonly Dictionary<Map, int> pins = new Dictionary<Map, int>();

        // Use opportunity numbers, not ticks modulo N: rare/long buckets must not alias the rate.
        public static bool Allows(int opportunity, int percent)
        {
            if (percent <= 0) return false;
            if (percent >= 100) return true;
            long n = opportunity;
            return Floor(n * percent, 100) != Floor((n - 1) * percent, 100);
        }
        private static long Floor(long n, long d) => n >= 0 ? n / d : (n - d + 1) / d;

        public void Set(Map map, string owner, int percent, Func<Thing, bool> fullSpeed = null,
            bool suppressUpdateWhenStopped = true)
        {
            if (percent < 0 || percent > 100) throw new ArgumentOutOfRangeException(nameof(percent));
            MapRuntime.Current.Register(map, owner);
            bool wasStopped = IsStopped(map);
            states[map] = new State { Owner = owner, Percent = percent, FullSpeed = fullSpeed,
                SuppressUpdate = suppressUpdateWhenStopped };
            bool stopped = IsStopped(map);
            if (stopped)
            {
                Find.TickManager?.RemoveAllFromMap(map);
                if (!wasStopped)
                {
                    map.weatherManager?.EndAllSustainers();
                    Find.SoundRoot?.sustainerManager?.EndAllInMap(map);
                }
            }
            else if (wasStopped) Reregister(map);
        }

        public bool IsStopped(Map map) => map != null && states.TryGetValue(map, out State s)
            && s.Percent == 0 && s.FullSpeed == null && !pins.ContainsKey(map);
        public bool Contains(Map map) => map != null && states.ContainsKey(map);
        public bool AllowsMap(Map map, bool preTick)
        {
            if (pins.ContainsKey(map)) return true;
            if (MapRuntime.Current.StoppedByPolicy(map)) return false;
            if (!states.TryGetValue(map, out State state)) return true;
            int tick = Find.TickManager.TicksGame + (preTick ? (DebugSettings.fastEcology ? 2000 : 1) : 0);
            return Allows(tick, state.Percent);
        }
        public bool AllowsUpdate(Map map) => pins.ContainsKey(map) || !MapRuntime.Current.StoppedByPolicy(map)
            && (!states.TryGetValue(map, out State state) || state.Percent != 0 || !state.SuppressUpdate);
        public bool AllowsThing(Thing thing)
        {
            Map map = thing.Map;
            if (map != null && pins.ContainsKey(map)) return true;
            if (MapRuntime.Current.StoppedByPolicy(map)) return false;
            if (map == null || !states.TryGetValue(map, out State state)) return true;
            if (state.FullSpeed?.Invoke(thing) == true) return true;
            int interval = thing.def.tickerType == TickerType.Rare ? 250 : thing.def.tickerType == TickerType.Long ? 2000 : 1;
            return Allows(Find.TickManager.TicksGame / interval, state.Percent);
        }
        public void Release(Map map, string owner)
        {
            if (map == null || !states.TryGetValue(map, out State state)) return;
            if (state.Owner != owner) throw new InvalidOperationException("Only the registered owner may release a map rate.");
            bool stopped = IsStopped(map);
            states.Remove(map);
            if (stopped && !map.Disposed) Reregister(map);
        }
        public void Forget(Map map) { if (map != null) { states.Remove(map); pins.Remove(map); } }
        internal void Pin(Map map)
        {
            bool stopped = IsStopped(map);
            pins.TryGetValue(map, out int count); pins[map] = count + 1;
            if (stopped) Reregister(map);
        }
        internal void Unpin(Map map)
        {
            if (!pins.TryGetValue(map, out int count)) return;
            if (count == 1) pins.Remove(map); else pins[map] = count - 1;
            if (!map.Disposed && IsStopped(map)) Find.TickManager?.RemoveAllFromMap(map);
        }
        public void ReleaseOwner(string owner)
        {
            foreach (Map map in new List<Map>(states.Keys))
                if (states[map].Owner == owner) Release(map, owner);
        }
        private static void Reregister(Map map)
        {
            TickManager ticks = Find.TickManager;
            ticks?.RemoveAllFromMap(map);
            foreach (Thing thing in map.spawnedThings)
                if (thing != null && !thing.Destroyed && thing.Spawned && thing.Map == map)
                    ticks?.RegisterAllTickabilityFor(thing);
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
    internal static class MapPreTickPatch
    {
        private static bool Prefix(Map __instance) => MapRuntime.Current?.TickRates.AllowsMap(__instance, true) != false;
    }
    [HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
    internal static class MapPostTickPatch
    {
        private static bool Prefix(Map __instance) => MapRuntime.Current?.TickRates.AllowsMap(__instance, false) != false;
    }
    [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
    internal static class MapUpdatePatch
    {
        private static bool Prefix(Map __instance) => MapRuntime.Current?.TickRates.AllowsUpdate(__instance) != false;
    }
    [HarmonyPatch(typeof(Thing), nameof(Thing.DoTick))]
    internal static class ThingTickPatch
    {
        private static bool Prefix(Thing __instance) => MapRuntime.Current?.TickRates.AllowsThing(__instance) != false;
    }
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.RegisterAllTickabilityFor))]
    internal static class ThingRegistrationPatch
    {
        private static bool Prefix(Thing t) => MapRuntime.Current?.TickRates.IsStopped(t.Map) != true;
    }
}
