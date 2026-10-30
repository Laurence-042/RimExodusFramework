using System;
using System.Collections.Generic;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>A real cell, never a projected Thing or a cell in a synthetic game map.</summary>
    public readonly struct MapCellAddress
    {
        public readonly Map Map;
        public readonly IntVec3 Cell;
        public bool IsValid => Map != null;
        public MapCellAddress(Map map, IntVec3 cell) { Map = map; Cell = cell; }
    }

    /// <summary>Exact lattice translation: local + OriginInReference = reference.
    /// Ownership is supplied by the consumer; rectangular bounds alone are insufficient.</summary>
    public sealed class MapCellPlacement
    {
        public Map Map { get; }
        public IntVec3 OriginInReference { get; }
        private readonly Func<IntVec3, bool> ownsCell;
        public MapCellPlacement(Map map, IntVec3 originInReference, Func<IntVec3, bool> ownsCell)
        {
            Map = map ?? throw new ArgumentNullException(nameof(map));
            if (originInReference.y != 0) throw new ArgumentException("Cell spaces are horizontal integer translations.");
            OriginInReference = originInReference;
            this.ownsCell = ownsCell ?? throw new ArgumentNullException(nameof(ownsCell));
        }
        internal bool Owns(IntVec3 local) => !Map.Disposed && local.InBounds(Map) && ownsCell(local);
    }

    /// <summary>A short-lived, main-thread query frame. Capture again after topology or availability changes.
    /// Overlapping ownership is resolved in provider order. Projection does not imply ownership.</summary>
    public sealed class MapCellSpace
    {
        private readonly MapCellPlacement[] placements;
        public Map Reference { get; }
        public IReadOnlyList<MapCellPlacement> Placements { get; }
        public MapCellSpace(Map reference, IEnumerable<MapCellPlacement> placements)
        {
            Reference = reference ?? throw new ArgumentNullException(nameof(reference));
            var list = new List<MapCellPlacement>(placements ?? throw new ArgumentNullException(nameof(placements)));
            var seen = new HashSet<Map>();
            bool hasReference = false;
            foreach (var entry in list)
            {
                if (entry == null || !seen.Add(entry.Map)) throw new ArgumentException("Placements must contain unique maps.");
                if (entry.Map != reference) continue;
                if (entry.OriginInReference != IntVec3.Zero) throw new ArgumentException("Reference origin must be zero.");
                hasReference = true;
            }
            if (!hasReference) throw new ArgumentException("The reference map must be included.");
            this.placements = list.ToArray();
            Placements = Array.AsReadOnly(this.placements);
        }
        public bool TryProject(Map source, IntVec3 sourceLocal, out IntVec3 referenceCell)
        {
            foreach (var entry in placements)
                if (entry.Map == source && !source.Disposed)
                {
                    referenceCell = sourceLocal + entry.OriginInReference;
                    return true;
                }
            referenceCell = IntVec3.Invalid;
            return false;
        }
        public bool TryResolve(IntVec3 referenceCell, out MapCellAddress address)
        {
            foreach (var entry in placements)
            {
                var local = referenceCell - entry.OriginInReference;
                if (!entry.Owns(local)) continue;
                address = new MapCellAddress(entry.Map, local);
                return true;
            }
            address = default;
            return false;
        }
        public MapCellGrid CaptureGrid(CellRect referenceBounds, int maxCells = 262144)
            => new MapCellGrid(this, referenceBounds, maxCells);
    }

    /// <summary>Dense bounded address snapshot for algorithms that directly index arrays.
    /// Unresolved cells stay invalid; consumers must choose an explicit unavailable-cell policy.</summary>
    public sealed class MapCellGrid
    {
        private readonly MapCellAddress[] cells;
        public CellRect Bounds { get; }
        public int Count => cells.Length;
        public MapCellAddress this[int index] => cells[index];
        internal MapCellGrid(MapCellSpace space, CellRect bounds, int maxCells)
        {
            long count = (long)bounds.Width * bounds.Height;
            if (bounds.Width <= 0 || bounds.Height <= 0 || maxCells <= 0 || count > maxCells)
                throw new ArgumentOutOfRangeException(nameof(bounds), "Query grid exceeds its explicit cell budget.");
            Bounds = bounds;
            cells = new MapCellAddress[(int)count];
            for (int i = 0; i < cells.Length; i++) space.TryResolve(ReferenceCell(i), out cells[i]);
        }
        public IntVec3 ReferenceCell(int index)
        {
            if ((uint)index >= (uint)cells.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return new IntVec3(Bounds.minX + index % Bounds.Width, 0, Bounds.minZ + index / Bounds.Width);
        }
        public IntVec3 ToGrid(IntVec3 referenceCell) => new IntVec3(referenceCell.x - Bounds.minX, 0, referenceCell.z - Bounds.minZ);
        public bool TryResolve(IntVec3 referenceCell, out MapCellAddress address)
        {
            if (!Bounds.Contains(referenceCell)) { address = default; return false; }
            address = cells[(referenceCell.z - Bounds.minZ) * Bounds.Width + referenceCell.x - Bounds.minX];
            return address.IsValid;
        }
    }

    /// <summary>No automatic tick, global current-map override, or implicit connection to rendering/combat.</summary>
    public sealed class MapSpatialQueries
    {
        private readonly Dictionary<string, Func<Map, MapCellSpace>> providers = new Dictionary<string, Func<Map, MapCellSpace>>();
        public void SetProvider(string owner, Func<Map, MapCellSpace> provider)
        {
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("An owner is required.", nameof(owner));
            if (provider == null) providers.Remove(owner); else providers[owner] = provider;
        }
        public bool TryCapture(string owner, Map reference, out MapCellSpace space)
        {
            space = null;
            if (reference == null || reference.Disposed || !providers.TryGetValue(owner, out var provider)) return false;
            space = provider(reference);
            if (space != null && space.Reference != reference) throw new InvalidOperationException("Provider returned the wrong reference frame.");
            return space != null;
        }
    }
}
