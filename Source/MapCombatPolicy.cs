using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimExodus.Maps
{
    public readonly struct MapCombatCell
    {
        public readonly Map Map;
        public readonly IntVec3 Cell;
        public MapCombatCell(Map map, IntVec3 cell) { Map = map; Cell = cell; }
    }
    public struct MapCombatNeighbor
    {
        public Map map;
        public IntVec3 offset;
    }
    /// <summary>Consumer geometry and availability. Translation is target-local to source-local.</summary>
    public sealed class MapCombatPolicy
    {
        public readonly string Owner;
        private bool enabled = true;
        public bool Enabled
        {
            get => enabled;
            set
            {
                if (enabled == value) return;
                enabled = value;
                if (!value) MapRuntime.Current?.Combat.InvalidatePolicy(this);
            }
        }
        public Func<Map, bool> Available;
        public Func<Map, Map, IntVec3?> Offset;
        public Func<Map, IntVec3, MapCombatCell?> CellOwner;
        public Action<Map, List<MapCombatNeighbor>> Neighbors;
        public Func<Map, bool> HasNeighbors;
        public Func<Pawn, Map, bool> CanApproach;
        public Func<Pawn, bool> SuppressTargetSearch;
        public Func<Projectile, bool> IgnoreProjectile;
        public Func<bool> DiagnosticLogging;
        public MapCombatPolicy(string owner) { Owner = owner ?? throw new ArgumentNullException(nameof(owner)); }
    }

    public static class MapCombatGeometry
    {
        public static bool Enabled => MapRuntime.Current?.Combat.HasEnabledMaps == true;
        public static bool IsEnabled(Map map) => MapRuntime.Current?.Combat.Policy(map) != null;
        internal static bool DebugEnabled => MapRuntime.Current?.Combat.DiagnosticLogging == true;
        public static bool HasNeighbors(Map map)
        {
            var policy = MapRuntime.Current?.Combat.Policy(map);
            return policy?.HasNeighbors?.Invoke(map) == true;
        }
        internal static bool CanApproach(Pawn pawn, Map target)
            => MapRuntime.Current?.Combat.Policy(pawn?.Map)?.CanApproach?.Invoke(pawn, target) == true;
        internal static bool SuppressTargetSearch(Pawn pawn)
            => MapRuntime.Current?.Combat.Policy(pawn?.Map)?.SuppressTargetSearch?.Invoke(pawn) == true;
        public static bool TryGetOffset(Map source, Map target, out IntVec3 offset)
        {
            offset = default;
            var policy = MapRuntime.Current?.Combat.Policy(source);
            if (policy == null || MapRuntime.Current.Combat.Policy(target) != policy) return false;
            var value = policy.Offset?.Invoke(source, target);
            if (!value.HasValue) return false;
            offset = value.Value;
            return true;
        }
        public static bool TryGetOwnerNeighbor(Map source, IntVec3 cell, out Map map, out IntVec3 local)
        {
            map = null; local = default;
            var policy = MapRuntime.Current?.Combat.Policy(source);
            if (policy == null) return false;
            var owner = policy.CellOwner?.Invoke(source, cell);
            if (!owner.HasValue || owner.Value.Map == source || MapRuntime.Current.Combat.Policy(owner.Value.Map) != policy) return false;
            map = owner.Value.Map; local = owner.Value.Cell;
            return local.InBounds(map);
        }
        public static void PopulateNeighbors(Map source, List<MapCombatNeighbor> output)
        {
            var policy = MapRuntime.Current?.Combat.Policy(source);
            if (policy == null) return;
            int start = output.Count;
            policy.Neighbors?.Invoke(source, output);
            for (int i = output.Count - 1; i >= start; i--)
                if (MapRuntime.Current.Combat.Policy(output[i].map) != policy) output.RemoveAt(i);
        }
        internal static bool IgnoreProjectile(Projectile projectile)
            => MapRuntime.Current?.Combat.Policy(projectile.Map)?.IgnoreProjectile?.Invoke(projectile) == true;
        internal static bool TryProject(Map source, IntVec3 local, Map destination, out IntVec3 projected)
        {
            projected = local;
            if (source == destination) return true;
            if (!TryGetOffset(destination, source, out var offset)) return false;
            projected += offset;
            return true;
        }
    }
}
