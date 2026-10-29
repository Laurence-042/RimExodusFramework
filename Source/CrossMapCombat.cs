using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus.Maps
{
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
        private readonly Dictionary<Map, int> inUse = new Dictionary<Map, int>();
        internal void Forget(Map map) => inUse.Remove(map);
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
            MapRuntime.Current.TickRates.Pin(map);
        }
        private void Release(Map map)
        {
            if (!inUse.TryGetValue(map, out int count)) return;
            if (count == 1) inUse.Remove(map); else inUse[map] = count - 1;
            MapRuntime.Current.TickRates.Unpin(map);
        }
        public sealed class CombatLease : IDisposable
        {
            private readonly CrossMapCombat service;
            private readonly MapCombatDomain domain;
            private readonly Map source;
            private readonly MapTarget target;
            private bool disposed;
            internal CombatLease(CrossMapCombat service, MapCombatDomain domain, Map source, MapTarget target)
            {
                this.service = service; this.domain = domain; this.source = source; this.target = target;
                service.Retain(source);
                if (source != target.Map) service.Retain(target.Map);
            }
            // Closing the domain blocks new leases, but already authorized flights may finish.
            public bool Valid => !disposed && !source.Disposed && target.Valid;
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
            }
        }
    }
}
