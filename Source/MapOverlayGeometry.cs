using System;
using System.Collections.Generic;
using UnityEngine;

namespace RimExodus.Maps
{
    // Clip in source-map space, preserving barycentric weights so every original vertex channel can be interpolated.
    // Only large, flat overlays opt into this operation. It is not a general 3D visibility/occlusion solution.
    internal static class MapOverlayGeometry
    {
        internal readonly struct Vertex
        {
            internal readonly Vector3 Position, Weights;
            internal Vertex(Vector3 position, Vector3 weights) { Position = position; Weights = weights; }
        }
        internal static List<Vertex> Clip(Vector3 a, Vector3 b, Vector3 c, IReadOnlyList<Vector2> footprint)
        {
            var vertices = new List<Vertex> { new Vertex(a, Vector3.right), new Vertex(b, Vector3.up), new Vertex(c, Vector3.forward) };
            float area = 0;
            for (int i = 0; i < footprint.Count; i++)
            { var p = footprint[i]; var q = footprint[(i + 1) % footprint.Count]; area += p.x * q.y - q.x * p.y; }
            float sign = area >= 0 ? 1 : -1;
            for (int i = 0; i < footprint.Count && vertices.Count != 0; i++)
            {
                var p = footprint[i]; var q = footprint[(i + 1) % footprint.Count];
                float Distance(Vector3 v) => sign * ((q.x - p.x) * (v.z - p.y) - (q.y - p.y) * (v.x - p.x));
                var next = new List<Vertex>();
                var previous = vertices[vertices.Count - 1];
                float pd = Distance(previous.Position);
                foreach (var vertex in vertices)
                {
                    float d = Distance(vertex.Position);
                    if ((d >= 0) != (pd >= 0))
                    {
                        float t = pd / (pd - d);
                        next.Add(new Vertex(Vector3.LerpUnclamped(previous.Position, vertex.Position, t),
                            Vector3.LerpUnclamped(previous.Weights, vertex.Weights, t)));
                    }
                    if (d >= 0) next.Add(vertex);
                    previous = vertex; pd = d;
                }
                vertices = next;
            }
            return vertices;
        }
        internal static bool IsLargeFlat(Vector3 min, Vector3 max, int mapWidth, int mapHeight) =>
            max.y - min.y < 0.01f && (max.x - min.x > mapWidth * 0.5f || max.z - min.z > mapHeight * 0.5f);
    }
}
