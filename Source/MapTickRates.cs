using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;
using Verse.AI;

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
            public bool CompensateMovement;
            public readonly HashSet<Thing> Suspended = new HashSet<Thing>();
            public readonly int[] Opportunity = { int.MinValue, int.MinValue, int.MinValue };
            public readonly bool[] Decision = new bool[3];

            public bool AllowsOpportunity(int opportunity, int bucket, int phase)
            {
                if (Opportunity[bucket] != opportunity)
                {
                    Opportunity[bucket] = opportunity;
                    Decision[bucket] = Allows(opportunity + phase, Percent);
                }
                return Decision[bucket];
            }
        }
        private readonly Dictionary<Map, State> states = new Dictionary<Map, State>();
        private readonly Dictionary<Map, int> pins = new Dictionary<Map, int>();
        private readonly HashSet<Thing> changedThings = new HashSet<Thing>();

        // Use opportunity numbers, not ticks modulo N: rare/long buckets must not alias the rate.
        public static bool Allows(int opportunity, int percent)
            => MapRateSchedule.Allows(opportunity, percent);

        public void Set(Map map, string owner, int percent, Func<Thing, bool> fullSpeed = null,
            bool suppressUpdateWhenStopped = true)
            => Set(map, owner, percent, fullSpeed, suppressUpdateWhenStopped, false);

        public void Set(Map map, string owner, int percent, Func<Thing, bool> fullSpeed,
            bool suppressUpdateWhenStopped, bool compensateMovement)
        {
            if (percent < 0 || percent > 100) throw new ArgumentOutOfRangeException(nameof(percent));
            MapRuntime.Current.Register(map, owner);
            states.TryGetValue(map, out State previous);
            if (previous != null && previous.Percent == percent && previous.FullSpeed == fullSpeed
                && previous.SuppressUpdate == suppressUpdateWhenStopped && previous.CompensateMovement == compensateMovement) return;
            states[map] = new State { Owner = owner, Percent = percent, FullSpeed = fullSpeed,
                SuppressUpdate = suppressUpdateWhenStopped, CompensateMovement = compensateMovement };
            // Rebuild once at the consumer's safe boundary, including partial 0% registration.
            if (percent == 0 || previous?.Percent == 0) RefreshRegistration(map);
            if (percent == 0 && previous?.Percent != 0)
            {
                map.weatherManager?.EndAllSustainers();
                Find.SoundRoot?.sustainerManager?.EndAllInMap(map);
            }
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
            return state.AllowsOpportunity(tick, 0, map.uniqueID);
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
            int interval = thing is IThingHolder ? 1
                : thing.def.tickerType == TickerType.Rare ? 250 : thing.def.tickerType == TickerType.Long ? 2000 : 1;
            return state.AllowsOpportunity(Find.TickManager.TicksGame / interval,
                interval == 1 ? 0 : interval == 250 ? 1 : 2, map.uniqueID);
        }
        public float MovementMultiplier(Thing thing)
        {
            Map map = thing?.Map;
            if (map == null || !states.TryGetValue(map, out State state) || !state.CompensateMovement
                || state.Percent <= 0 || pins.ContainsKey(map) || state.FullSpeed?.Invoke(thing) == true) return 1f;
            return 100f / state.Percent;
        }
        public void Release(Map map, string owner) => Release(map, owner, true);

        public void Release(Map map, string owner, bool restoreTicks)
        {
            if (map == null || !states.TryGetValue(map, out State state)) return;
            if (state.Owner != owner) throw new InvalidOperationException("Only the registered owner may release a map rate.");
            bool stopped = state.Percent == 0;
            states.Remove(map);
            if (stopped && restoreTicks && !map.Disposed) RefreshRegistration(map);
        }
        public void Forget(Map map) { if (map != null) { states.Remove(map); pins.Remove(map); } }
        internal void Pin(Map map)
        {
            bool stopped = IsStopped(map);
            pins.TryGetValue(map, out int count); pins[map] = count + 1;
            if (stopped) RefreshRegistration(map);
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
        /// <summary>Call at a safe tick boundary when the full-speed region changes.</summary>
        public void RefreshRegistration(Map map)
        {
            if (map == null || map.Disposed) return;
            if (states.TryGetValue(map, out State state)) state.Suspended.Clear();
            TickManager ticks = Find.TickManager;
            ticks?.RemoveAllFromMap(map);
            foreach (Thing thing in map.spawnedThings)
                if (thing != null && !thing.Destroyed && thing.Spawned && thing.Map == map)
                    if (thing is IThingHolder || thing.def.tickerType != TickerType.Never)
                        ticks?.RegisterAllTickabilityFor(thing);
        }

        internal bool AllowsRegistration(Thing thing)
        {
            Map map = thing?.Map;
            if (map == null || !states.TryGetValue(map, out State state)) return true;
            if (!(thing is IThingHolder) && thing.def.tickerType == TickerType.Never) return true;
            bool allow = pins.ContainsKey(map) || state.Percent != 0 || state.FullSpeed?.Invoke(thing) == true;
            if (allow) state.Suspended.Remove(thing); else state.Suspended.Add(thing);
            return allow;
        }
        public void NotifyPositionChanged(Thing thing)
        {
            if (thing != null && !(thing is IThingHolder) && thing.def.tickerType == TickerType.Never) return;
            if (thing?.Map != null && states.TryGetValue(thing.Map, out State state) && state.Percent == 0)
                changedThings.Add(thing);
        }
        internal void NotifyDespawn(Thing thing)
        {
            changedThings.Remove(thing);
            if (thing.Map != null && states.TryGetValue(thing.Map, out State state)) state.Suspended.Remove(thing);
        }
        internal void ApplyPositionChanges()
        {
            foreach (Thing thing in changedThings)
            {
                Map map = thing.Map;
                if (map == null || map.Disposed || !states.TryGetValue(map, out State state) || state.Percent != 0) continue;
                bool allow = pins.ContainsKey(map) || state.FullSpeed?.Invoke(thing) == true;
                if (allow && state.Suspended.Remove(thing)) Find.TickManager.RegisterAllTickabilityFor(thing);
                else if (!allow && state.Suspended.Add(thing)) Find.TickManager.DeRegisterAllTickabilityFor(thing);
            }
            changedThings.Clear();
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
        private static bool Prefix(Thing t) => MapRuntime.Current?.TickRates.AllowsRegistration(t) != false;
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.Position), MethodType.Setter)]
    internal static class ThingPositionRatePatch
    {
        private static void Postfix(Thing __instance) => MapRuntime.Current?.TickRates.NotifyPositionChanged(__instance);
    }
    [HarmonyPatch(typeof(Thing), nameof(Thing.SetPositionDirect))]
    internal static class ThingDirectPositionRatePatch
    {
        private static void Postfix(Thing __instance) => MapRuntime.Current?.TickRates.NotifyPositionChanged(__instance);
    }
    [HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
    internal static class ThingDespawnRatePatch
    {
        private static void Prefix(Thing __instance) => MapRuntime.Current?.TickRates.NotifyDespawn(__instance);
    }
    [HarmonyPatch(typeof(Pawn_PathFollower), "CostToPayThisTick")]
    internal static class PawnMovementRatePatch
    {
        private static void Postfix(Pawn ___pawn, ref float __result)
            => __result *= MapRuntime.Current?.TickRates.MovementMultiplier(___pawn) ?? 1f;
    }
}
