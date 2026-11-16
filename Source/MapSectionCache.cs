using System;
using System.Collections.Generic;

namespace RimExodus.Maps
{
    /// <summary>Caller-owned cache of rebuildable section snapshots for one map.</summary>
    public sealed class MapSectionCache<T> : IDisposable where T : class, IDisposable
    {
        private readonly Dictionary<int, T> sections = new Dictionary<int, T>();
        private readonly HashSet<int> dirty = new HashSet<int>();

        private static int Key(int x, int z) => x | (z << 16);

        public void MarkDirty(int x, int z) => dirty.Add(Key(x, z));

        public bool TryGet(int x, int z, out T snapshot)
        {
            int key = Key(x, z);
            if (!dirty.Contains(key)) return sections.TryGetValue(key, out snapshot);
            snapshot = null;
            return false;
        }

        public T Rebuild(int x, int z, Func<T> build)
        {
            int key = Key(x, z);
            dirty.Remove(key);
            if (sections.TryGetValue(key, out T old))
            {
                sections.Remove(key);
                old.Dispose();
            }

            T snapshot = build() ?? throw new InvalidOperationException("Section builder returned no snapshot.");
            sections.Add(key, snapshot);
            return snapshot;
        }

        public void Dispose() => Clear();

        public void Clear()
        {
            foreach (T snapshot in sections.Values) snapshot.Dispose();
            sections.Clear();
            dirty.Clear();
        }
    }
}
