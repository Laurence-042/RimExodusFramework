using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>Local cell samples with shared values, not a serialized Room identity or topology.</summary>
    public sealed class PrefabEnvironmentEntry
    {
        public float temperature = float.NaN;
        public float vacuum = float.NaN;
        public List<IntVec3> cells = new List<IntVec3>();
    }

    internal static class MapPrefabEnvironment
    {
        internal static void Validate(MapPrefabDocument doc)
        {
            var seen = new HashSet<IntVec3>();
            var mask = new HashSet<IntVec3>(doc.mask);
            if (doc.environment == null) throw new InvalidDataException("Missing prefab environment list.");
            foreach (PrefabEnvironmentEntry entry in doc.environment)
            {
                if (entry == null || float.IsNaN(entry.temperature) || float.IsInfinity(entry.temperature)
                    || entry.temperature < -273.15f || entry.temperature > 1000f
                    || float.IsNaN(entry.vacuum) || float.IsInfinity(entry.vacuum)
                    || entry.vacuum < -0.1f || entry.vacuum > 1f || entry.cells == null || entry.cells.Count == 0)
                    throw new InvalidDataException("Invalid prefab environment: require temperature [-273.15,1000], vacuum [-0.1,1] and cells.");
                foreach (IntVec3 cell in entry.cells)
                    if (cell.y != 0 || cell.x < 0 || cell.z < 0 || cell.x >= doc.size.x || cell.z >= doc.size.z
                        || mask.Count > 0 && !mask.Contains(cell) || !seen.Add(cell))
                        throw new InvalidDataException($"Invalid, duplicate or unselected prefab environment cell: {cell}.");
            }
        }

        internal static void Capture(MapPrefabDocument doc, Map map, IEnumerable<IntVec3> cells)
        {
            WithRooms(map, () =>
            {
                var entries = new Dictionary<Room, PrefabEnvironmentEntry>();
                foreach (IntVec3 cell in cells.OrderBy(c => c.z).ThenBy(c => c.x))
                {
                    Room room = cell.GetRoom(map);
                    if (room == null || room.UsesOutdoorTemperature) continue;
                    if (!entries.TryGetValue(room, out PrefabEnvironmentEntry entry))
                    {
                        entry = new PrefabEnvironmentEntry { temperature = room.Temperature, vacuum = room.Vacuum };
                        entries.Add(room, entry);
                        doc.environment.Add(entry);
                    }
                    entry.cells.Add(cell - doc.sourceOrigin);
                }
            });
        }

        internal static void Apply(MapPrefabDocument doc, Map map, IntVec3 root, Rot4 rotation,
            Func<Map, IntVec3, bool> protectCell)
        {
            if (doc.environment.Count == 0) return;
            WithRooms(map, () =>
            {
                var samples = new Dictionary<Room, (double temperature, double vacuum, int count)>();
                foreach (PrefabEnvironmentEntry entry in doc.environment)
                    foreach (IntVec3 local in entry.cells)
                    {
                        IntVec3 cell = root + PrefabUtility.GetAdjustedLocalPosition(local, rotation);
                        Room room = cell.GetRoom(map);
                        if (room == null || room.UsesOutdoorTemperature) continue;
                        samples.TryGetValue(room, out var sum);
                        samples[room] = (sum.temperature + entry.temperature, sum.vacuum + entry.vacuum, sum.count + 1);
                    }
                foreach (var pair in samples)
                {
                    Room room = pair.Key;
                    // Room state cannot protect just one cell: leave the entire room unchanged.
                    if (protectCell != null && room.Cells.Any(c => protectCell(map, c))) continue;
                    var sum = pair.Value;
                    int remaining = Math.Max(0, room.CellCount - sum.count);
                    int total = sum.count + remaining;
                    room.Temperature = (float)((sum.temperature + room.Temperature * remaining) / total);
                    room.Vacuum = room.ExposedToSpace ? 1f : (float)((sum.vacuum + room.Vacuum * remaining) / total);
                }
            });
        }

        private static void WithRooms(Map map, Action action)
        {
            bool enabled = map.regionAndRoomUpdater.Enabled;
            try
            {
                map.regionAndRoomUpdater.Enabled = true;
                // Incremental rebuild preserves the existing native temperature/vacuum transfer.
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                action();
            }
            finally { map.regionAndRoomUpdater.Enabled = enabled; }
        }
    }
}
