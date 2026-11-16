using RimExodus.Maps;
using UnityEngine;
using Verse;

internal static class SectionMeshBatchChecks
{
    private sealed class Snapshot : IDisposable
    {
        internal int Releases { get; private set; }
        public void Dispose() => Releases++;
    }

    internal static void Run()
    {
        int checks = 0;
        void Check(bool pass, string reason)
        {
            if (!pass) throw new InvalidOperationException(reason);
            checks++;
        }

        var map = (Map)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Map));
        var section = (Section)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Section));
        typeof(Section).GetField(nameof(Section.map))!.SetValue(section, map);
        // Match the native rendering fixture's CPU-only shader setup.
        var nativeShader = (Waylith.Rendering.Shader)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(Waylith.Rendering.Shader));
        typeof(Waylith.Rendering.Shader).GetProperty("MaterialLayout")!.SetValue(nativeShader,
            new Waylith.Rendering.MaterialLayout(Array.Empty<Waylith.Rendering.MaterialUniformSlot>(),
                Array.Empty<Waylith.Rendering.MaterialTextureSlot>(), 0));
        Shader shader = Shader.FromEngineShader("REF section regression", nativeShader);
        var first = new Material(shader);
        var second = new Material(shader);
        var batches = new SectionMeshBatches(section, 2);
        var origin = new Vector3(15.5f, 0.125f, 16.5f);
        var tile = new Vector2(15, 0);
        batches.AddPlane(0, first, origin, tile, 0.0625f);
        batches.AddPlane(0, second, origin, tile, 0.0625f);
        batches.AddPlane(0, first, origin + Vector3.right, Vector2.zero, 0.0625f);
        batches.AddPlane(1, first, origin, tile, 0.0625f);
        batches.Complete();
        var deck = batches.Batches(0);
        var cover = batches.Batches(1);
        Check(deck.Count == 2 && cover.Count == 1, "Batches are grouped by surface and actual material");
        Check(ReferenceEquals(deck[0].material, first) && ReferenceEquals(deck[1].material, second)
            && ReferenceEquals(cover[0].material, first) && !ReferenceEquals(deck[0].mesh, cover[0].mesh),
            "Material and surface order follows first appearance");
        Check(deck[0].verts.Count == 8 && deck[1].verts.Count == 4 && cover[0].verts.Count == 4,
            "A repeated material appends to the existing batch");
        Check(deck[0].verts[0].Equals(new Vector3(15f, 0.125f, 16f))
            && deck[0].verts[4].Equals(new Vector3(16f, 0.125f, 16f)),
            "Positions retain the section-local cell offsets");
        Check(deck[0].uvs[0].Equals(new Vector3(0.9375f, 0f, 0f))
            && deck[0].uvs[2].Equals(new Vector3(1f, 0.0625f, 0f))
            && deck[0].uvs[4].Equals(Vector3.zero), "UVs preserve the tile boundary and wrap");
        Check(deck[0].normals.All(n => n.Equals(Vector3.up)) && deck[0].normals.Count == 8,
            "Both planes retain their normals");
        Check(deck[0].tris.SequenceEqual(new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }),
            "Plane indices preserve order and apply the batch offset");
        Check(deck[0].finalized && deck[0].mesh.vertexCount == 8 && cover[0].finalized,
            "Completed batches materialize the uploaded mesh data");
        batches.Dispose();
        batches.Dispose();
        Check(deck.Count == 0 && cover.Count == 0, "Release clears both surface batches");

        var cache = new MapSectionCache<Snapshot>();
        var otherMap = new MapSectionCache<Snapshot>();
        var old = cache.Rebuild(1, 1, () => new Snapshot());
        var neighbor = cache.Rebuild(2, 1, () => new Snapshot());
        var other = otherMap.Rebuild(1, 1, () => new Snapshot());
        Check(cache.TryGet(1, 1, out var current) && current == old, "A visible clean section reuses its snapshot");
        cache.MarkDirty(1, 1);
        cache.MarkDirty(1, 1);
        Check(!cache.TryGet(1, 1, out _) && old.Releases == 0, "A dirty section waits for demand before disposal");
        Check(cache.TryGet(2, 1, out var unchanged) && unchanged == neighbor
            && otherMap.TryGet(1, 1, out var elsewhere) && elsewhere == other, "Other sections and maps stay clean");
        var next = cache.Rebuild(1, 1, () => new Snapshot());
        Check(cache.TryGet(1, 1, out current) && current == next && old.Releases == 1,
            "Coalesced dirtiness leads to one replacement");
        cache.MarkDirty(1, 1);
        var failure = new ArgumentException("Synthetic section build failure");
        try { cache.Rebuild(1, 1, () => throw failure); }
        catch (ArgumentException error) when (ReferenceEquals(error, failure)) { }
        Check(!cache.TryGet(1, 1, out _) && next.Releases == 1, "Failed rebuild leaves no stale geometry to draw");
        var recovered = cache.Rebuild(1, 1, () => new Snapshot());
        cache.MarkDirty(3, 3);
        cache.Dispose();
        cache.Dispose();
        Check(neighbor.Releases == 1 && recovered.Releases == 1 && other.Releases == 0
            && !cache.TryGet(1, 1, out _), "Map release disposes only that map's cached snapshots once");
        otherMap.Dispose();
        Check(other.Releases == 1, "An independent map owns and releases its own resources");

        Console.WriteLine($"{checks} section mesh and cache checks passed.");
    }
}
