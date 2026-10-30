#if !RIMWORLD_1_7
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RimExodus.Maps
{
    // Unity has no public mesh mutation version. Refill pooled meshes instead of caching stale mod geometry.
    internal sealed class MapOverlayClipper
    {
        private readonly List<Mesh> meshes = new List<Mesh>();
        private int cursor;
        internal void BeginFrame() => cursor = 0;
        internal void Dispose()
        {
            foreach (var mesh in meshes) Object.Destroy(mesh);
            meshes.Clear(); cursor = 0;
        }
        internal bool Clip(ref Mesh mesh, Matrix4x4 matrix, ref int submesh, MapRenderPlan plan)
        {
            var footprint = plan.OverlayFootprint;
            if (mesh == null || !mesh.isReadable || footprint == null || footprint.Count < 3
                || submesh < 0 || submesh >= mesh.subMeshCount || mesh.GetTopology(submesh) != MeshTopology.Triangles
                || mesh.HasVertexAttribute(VertexAttribute.BlendIndices) || mesh.HasVertexAttribute(VertexAttribute.BlendWeight)) return true;
            var bounds = mesh.bounds;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
            for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
            {
                var p = matrix.MultiplyPoint3x4(new Vector3(x == 0 ? bounds.min.x : bounds.max.x,
                    y == 0 ? bounds.min.y : bounds.max.y, z == 0 ? bounds.min.z : bounds.max.z));
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
            if (!MapOverlayGeometry.IsLargeFlat(min, max, plan.Source.Size.x, plan.Source.Size.z)) return true;
            var positions = mesh.vertices;
            var indices = mesh.GetIndices(submesh);
            var vertices = new List<(int a, int b, int c, Vector3 weights)>();
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                var polygon = MapOverlayGeometry.Clip(matrix.MultiplyPoint3x4(positions[a]),
                    matrix.MultiplyPoint3x4(positions[b]), matrix.MultiplyPoint3x4(positions[c]), footprint);
                for (int k = 1; k + 1 < polygon.Count; k++)
                {
                    vertices.Add((a, b, c, polygon[0].Weights));
                    vertices.Add((a, b, c, polygon[k].Weights));
                    vertices.Add((a, b, c, polygon[k + 1].Weights));
                }
            }
            if (vertices.Count == 0) return false;
            if (cursor == meshes.Count) meshes.Add(new Mesh { name = "REF clipped planar overlay" });
            var output = meshes[cursor++];
            output.Clear();
            output.indexFormat = IndexFormat.UInt32;
            Vector3[] Interpolate(Vector3[] data)
            {
                var result = new Vector3[vertices.Count];
                for (int i = 0; i < result.Length; i++)
                { var v = vertices[i]; result[i] = data[v.a] * v.weights.x + data[v.b] * v.weights.y + data[v.c] * v.weights.z; }
                return result;
            }
            List<Vector4> Interpolate4(IList<Vector4> data)
            {
                var result = new List<Vector4>(vertices.Count);
                foreach (var v in vertices) result.Add(data[v.a] * v.weights.x + data[v.b] * v.weights.y + data[v.c] * v.weights.z);
                return result;
            }
            output.vertices = Interpolate(positions);
            var normals = mesh.normals;
            if (normals.Length == positions.Length) output.normals = Interpolate(normals);
            var tangents = mesh.tangents;
            if (tangents.Length == positions.Length) output.SetTangents(Interpolate4(tangents));
            var colors = mesh.colors;
            if (colors.Length == positions.Length)
            {
                var result = new List<Color>(vertices.Count);
                foreach (var v in vertices) result.Add(colors[v.a] * v.weights.x + colors[v.b] * v.weights.y + colors[v.c] * v.weights.z);
                output.SetColors(result);
            }
            for (int channel = 0; channel < 8; channel++)
            {
                var uv = new List<Vector4>(); mesh.GetUVs(channel, uv);
                if (uv.Count == positions.Length) output.SetUVs(channel, Interpolate4(uv));
            }
            var triangles = new int[vertices.Count];
            for (int i = 0; i < triangles.Length; i++) triangles[i] = i;
            output.SetTriangles(triangles, 0);
            mesh = output; submesh = 0;
            return true;
        }
    }
}
#endif
