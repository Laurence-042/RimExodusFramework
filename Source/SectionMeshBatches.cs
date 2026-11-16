using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>Owns the static plane meshes for one section, grouped by surface and material.</summary>
    public sealed class SectionMeshBatches : IDisposable
    {
        private readonly SectionLayer_Terrain[] surfaces;

        private static class Plane
        {
            internal static readonly Vector3[] Vertices = MeshPool.plane10.vertices;
            internal static readonly Vector3[] Normals = MeshPool.plane10.normals;
            internal static readonly int[] Triangles = MeshPool.plane10.triangles;
        }

        public SectionMeshBatches(Section section, int surfaceCount)
        {
            surfaces = new SectionLayer_Terrain[surfaceCount];
            for (int i = 0; i < surfaces.Length; i++) surfaces[i] = new SectionLayer_Terrain(section);
        }

        public IReadOnlyList<LayerSubMesh> Batches(int surface) => surfaces[surface].subMeshes;

        /// <summary>Appends a unit plane with UVs specified in unscaled tile coordinates.</summary>
        public void AddPlane(int surface, Material material, Vector3 center, Vector2 uvTile, float uvScale)
        {
            AppendPlane(surfaces[surface].GetSubMesh(material), center, uvTile, uvScale,
                Plane.Vertices, Plane.Normals, Plane.Triangles);
        }

        public void Complete()
        {
            const MeshParts parts = MeshParts.Verts | MeshParts.Tris | MeshParts.UVs1 | MeshParts.Normals;
            foreach (SectionLayer_Terrain surface in surfaces)
            foreach (LayerSubMesh batch in surface.subMeshes)
                batch.FinalizeMesh(parts);
        }

        public void Dispose()
        {
            foreach (SectionLayer_Terrain surface in surfaces) surface.Dispose();
        }

        public static Vector3 PlaneUv(Vector2 uvTile, float scale, Vector3 vertex)
            => new Vector3((uvTile.x + 0.5f + vertex.x) * scale,
                (uvTile.y + 0.5f + vertex.z) * scale, 0f);

        internal static void AppendPlane(LayerSubMesh batch, Vector3 center, Vector2 uvTile, float uvScale,
            Vector3[] vertices, Vector3[] normals, int[] triangles)
        {
            int offset = batch.verts.Count;
            foreach (Vector3 vertex in vertices)
            {
                batch.verts.Add(center + vertex);
                batch.uvs.Add(PlaneUv(uvTile, uvScale, vertex));
            }
            if (normals.Length == vertices.Length) batch.normals.AddRange(normals);
            foreach (int index in triangles) batch.tris.Add(offset + index);
        }
    }
}
