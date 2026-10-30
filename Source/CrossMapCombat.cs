using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus.Maps
{
    internal sealed class CombatLifetime
    {
        private Action end;
        internal CombatLifetime(Action end) { this.end = end; }
        internal void Invalidate() { var action = end; end = null; action?.Invoke(); }
    }
    public readonly struct MapSightSegment
    {
        public readonly Map Map;
        public readonly IntVec3 Start, End;
        public readonly bool SkipFirstCell;
        public MapSightSegment(Map map, IntVec3 start, IntVec3 end, bool skipFirstCell = false)
        { Map = map; Start = start; End = end; SkipFirstCell = skipFirstCell; }
    }
    public readonly struct MapTarget
    {
        public readonly Map Map;
        public readonly Thing Thing;
        public readonly IntVec3 Cell;
        public MapTarget(Map map, IntVec3 cell) { Map = map; Cell = cell; Thing = null; }
        public MapTarget(Thing thing) { Thing = thing; Map = thing?.Map; Cell = thing?.Position ?? IntVec3.Invalid; }
        public bool Valid => Map != null && !Map.Disposed && Cell.InBounds(Map)
            && (Thing == null || !Thing.Destroyed && Thing.Spawned && Thing.Map == Map);
        public Vector3 Center => Thing?.TrueCenter() ?? Cell.ToVector3Shifted();
    }

    public sealed class MapCombatDomain
    {
        internal readonly string Owner;
        internal readonly Func<Map, Map, bool> Allows;
        internal readonly Func<Map, Vector3, Map, Vector3> Convert;
        public bool Enabled { get; set; }
        // The domain owns ray partitioning; the DLL runs vanilla LOS on each local segment.
        public Func<Map, Vector3, MapTarget, IEnumerable<MapSightSegment>> SightSegments { get; set; }
        public MapCombatDomain(string owner, Func<Map, Map, bool> allows,
            Func<Map, Vector3, Map, Vector3> convert, bool enabled = false)
        {
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("A combat owner is required.", nameof(owner));
            Owner = owner; Allows = allows ?? throw new ArgumentNullException(nameof(allows));
            Convert = convert ?? throw new ArgumentNullException(nameof(convert)); Enabled = enabled;
        }
    }

    /// <summary>Combat permission and map lifetime, independent of the caller's trajectory/physics.</summary>
    public sealed class CrossMapCombat
    {
        internal readonly ConditionalWeakTable<Verb, MapCombatCellTargets.Entry> CellVerbs = new ConditionalWeakTable<Verb, MapCombatCellTargets.Entry>();
        internal readonly ConditionalWeakTable<Thing, MapCombatCellTargets.Entry> CellCasters = new ConditionalWeakTable<Thing, MapCombatCellTargets.Entry>();
        internal readonly ConditionalWeakTable<Verb, MapCombatCoordinates.CastEntry> CastTargets = new ConditionalWeakTable<Verb, MapCombatCoordinates.CastEntry>();
        internal readonly ConditionalWeakTable<Projectile, CombatLifetime> Projectiles = new ConditionalWeakTable<Projectile, CombatLifetime>();
        internal readonly ConditionalWeakTable<Thing, CombatExtendedBridge.ProjectileTargetState> CeProjectiles = new ConditionalWeakTable<Thing, CombatExtendedBridge.ProjectileTargetState>();
        private readonly Dictionary<Map, int> inUse = new Dictionary<Map, int>();
        private readonly Dictionary<Map, MapCombatPolicy> policies = new Dictionary<Map, MapCombatPolicy>();
        private readonly Dictionary<Map, List<System.WeakReference<CombatLifetime>>> lifetimes = new Dictionary<Map, List<System.WeakReference<CombatLifetime>>>();
        internal CombatLifetime Track(Map source, Map target, Action end)
        {
            var lifetime = new CombatLifetime(end);
            TrackOnMap(source, lifetime);
            if (target != source) TrackOnMap(target, lifetime);
            return lifetime;
        }
        private void TrackOnMap(Map map, CombatLifetime lifetime)
        {
            if (map == null) return;
            if (!lifetimes.TryGetValue(map, out var list)) lifetimes[map] = list = new List<System.WeakReference<CombatLifetime>>();
            if (list.Count >= 128) list.RemoveAll(w => !w.TryGetTarget(out _));
            list.Add(new System.WeakReference<CombatLifetime>(lifetime));
        }
        public void Invalidate(Map map)
        {
            if (map == null) return;
            if (lifetimes.TryGetValue(map, out var list))
            {
                lifetimes.Remove(map);
                foreach (var weak in list) if (weak.TryGetTarget(out var lifetime)) lifetime.Invalidate();
            }
            inUse.Remove(map);
        }
        public void InvalidateOwner(string owner)
        {
            foreach (var pair in policies) if (pair.Value.Owner == owner) Invalidate(pair.Key);
        }
        internal void InvalidatePolicy(MapCombatPolicy policy)
        {
            foreach (var pair in policies) if (pair.Value == policy) Invalidate(pair.Key);
        }
        public bool HasEnabledMaps => policies.Count != 0;
        public bool DiagnosticLogging { get; set; }
        internal MapCombatPolicy Policy(Map map) => map != null && !map.Disposed
            && policies.TryGetValue(map, out var p) && p.Enabled && p.Available?.Invoke(map) != false ? p : null;
        public void Enable(Map map, MapCombatPolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            MapCombatModule.Install();
            MapRuntime.Current.Register(map, policy.Owner);
            if (policies.TryGetValue(map, out var previous) && previous != policy) Invalidate(map);
            policies[map] = policy;
        }
        public void Disable(Map map)
        {
            if (map == null) return;
            policies.Remove(map);
            Invalidate(map);
        }
        internal void Forget(Map map) => Disable(map);
        public bool IsInUse(Map map) => map != null && inUse.ContainsKey(map);
        public CombatLease Begin(MapCombatDomain domain, Map source, MapTarget target)
        {
            if (domain == null || !domain.Enabled || source == null || source.Disposed || !target.Valid
                || !domain.Allows(source, target.Map)) return null;
            MapRuntime.Current.Register(source, domain.Owner);
            MapRuntime.Current.Register(target.Map, domain.Owner);
            return new CombatLease(this, domain, source, target);
        }
        private void Retain(Map map)
        {
            inUse.TryGetValue(map, out int count); inUse[map] = count + 1;
        }
        private void Release(Map map)
        {
            if (!inUse.TryGetValue(map, out int count)) return;
            if (count == 1) inUse.Remove(map); else inUse[map] = count - 1;
        }
        public sealed class CombatLease : IDisposable
        {
            private readonly CrossMapCombat service;
            private readonly MapCombatDomain domain;
            private readonly Map source;
            private readonly MapTarget target;
            private bool disposed;
            private readonly CombatLifetime lifetime;
            internal CombatLease(CrossMapCombat service, MapCombatDomain domain, Map source, MapTarget target)
            {
                this.service = service; this.domain = domain; this.source = source; this.target = target;
                service.Retain(source);
                if (source != target.Map) service.Retain(target.Map);
                lifetime = service.Track(source, target.Map, Dispose);
            }
            public bool Valid => !disposed && domain.Enabled && !source.Disposed && target.Valid
                && domain.Allows(source, target.Map);
            public Vector3 TargetInSource => domain.Convert(target.Map, target.Center, source);
            public bool InRange(Vector3 origin, float minRange, float maxRange)
            {
                if (!Valid) return false;
                float distance = (TargetInSource - origin).MagnitudeHorizontal();
                return distance >= minRange && distance <= maxRange;
            }
            public DamageWorker.DamageResult ApplyDamage(DamageInfo damage)
            {
                if (!Valid || target.Thing == null) throw new InvalidOperationException("Combat target is no longer valid.");
                return target.Thing.TakeDamage(damage);
            }
            public bool HasLineOfSight(Vector3 origin)
            {
                if (!Valid) return false;
                if (domain.SightSegments == null) throw new InvalidOperationException("The combat domain must supply its ray segments.");
                var segments = domain.SightSegments(source, origin, target)?.ToList();
                if (segments == null || segments.Count == 0 || segments[0].Map != source
                    || segments[segments.Count - 1].Map != target.Map) return false;
                foreach (var segment in segments)
                    if (segment.Map == null || segment.Map.Disposed || !segment.Start.InBounds(segment.Map)
                        || !segment.End.InBounds(segment.Map) || !domain.Allows(source, segment.Map)
                        || !GenSight.LineOfSight(segment.Start, segment.End, segment.Map, segment.SkipFirstCell)) return false;
                return true;
            }
            public float CoverBlockChance(Vector3 origin)
            {
                if (!Valid) throw new InvalidOperationException("Combat target is no longer valid.");
                Vector3 converted = domain.Convert(source, origin, target.Map);
                var cell = new IntVec3(Mathf.FloorToInt(converted.x), 0, Mathf.FloorToInt(converted.z));
                LocalTargetInfo local = target.Thing != null ? new LocalTargetInfo(target.Thing) : new LocalTargetInfo(target.Cell);
                return CoverUtility.CalculateOverallBlockChance(local, cell, target.Map);
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true; service.Release(source);
                if (source != target.Map) service.Release(target.Map);
                lifetime?.Invalidate();
            }
        }
    }
}
