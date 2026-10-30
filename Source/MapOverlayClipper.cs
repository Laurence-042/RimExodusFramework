#if RIMWORLD_1_7
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Waylith.Graphics.Meshes;
using Waylith.Rendering;
using NativeMesh = Waylith.Graphics.Meshes.Mesh;

namespace RimExodus.Maps
{
    internal sealed class MapOverlayClipper : IDisposable
    {
        private sealed class Entry
        {
            internal long Version;
            internal NativeMesh Mesh;
            internal int Used;
            internal bool Changed;
        }
        private readonly Dictionary<(NativeMesh, int, System.Numerics.Matrix4x4), Entry> entries =
            new Dictionary<(NativeMesh, int, System.Numerics.Matrix4x4), Entry>();
        private readonly List<(NativeMesh, int, System.Numerics.Matrix4x4)> stale = new List<(NativeMesh, int, System.Numerics.Matrix4x4)>();
        private IReadOnlyList<Vector2> footprint;
        internal MeshDrawCommand Clip(in MeshDrawCommand command, MapRenderPlan plan)
        {
            if (plan.OverlayFootprint == null || plan.OverlayFootprint.Count < 3 || command.Mesh == null || command.Instancing != null)
                return command;
            // Reject moving pawns/flecks and small sections before allocating cache entries or reading vertex arrays.
            var bounds = command.Mesh.Bounds;
            var m = new MapDrawCommand(command).Matrix;
            var size = bounds.Size;
            var span = new Vector3(Math.Abs(m.m00) * size.X + Math.Abs(m.m01) * size.Y + Math.Abs(m.m02) * size.Z,
                Math.Abs(m.m10) * size.X + Math.Abs(m.m11) * size.Y + Math.Abs(m.m12) * size.Z,
                Math.Abs(m.m20) * size.X + Math.Abs(m.m21) * size.Y + Math.Abs(m.m22) * size.Z);
            if (bounds.IsEmpty || !MapOverlayGeometry.IsLargeFlat(Vector3.zero, span, plan.Source.Size.x, plan.Source.Size.z))
                return command;
            if (!ReferenceEquals(footprint, plan.OverlayFootprint)) { Dispose(); footprint = plan.OverlayFootprint; }
            var key = (command.Mesh, command.SubmeshIndex, command.Matrix);
            if (!entries.TryGetValue(key, out var entry) || entry.Version != command.Mesh.Version)
            {
                entry?.Mesh?.Dispose();
                entry = new Entry { Version = command.Mesh.Version };
                entry.Mesh = Build(command, plan, out entry.Changed);
                entries[key] = entry;
            }
            entry.Used = Time.frameCount;
            if (!entry.Changed) return command;
            return new MeshDrawCommand(entry.Mesh, command.Material, command.PropertyRange,
                command.Matrix, 0, command.PassId, command.Instancing, command.RenderQueueOverride, command.Layer, command.Storage);
        }
        internal void Prune()
        {
            stale.Clear();
            foreach (var item in entries) if (Time.frameCount - item.Value.Used > 120) stale.Add(item.Key);
            foreach (var key in stale) { entries[key].Mesh?.Dispose(); entries.Remove(key); }
            stale.Clear();
        }
        public void Dispose()
        {
            foreach (var entry in entries.Values) entry.Mesh?.Dispose();
            entries.Clear(); footprint = null;
        }
        private NativeMesh Build(in MeshDrawCommand command, MapRenderPlan plan, out bool changed)
        {
            changed = false;
            var source = command.Mesh.CpuStorage;
            if (!command.Mesh.IsReadable || command.SubmeshIndex < 0 || command.SubmeshIndex >= source.SubMeshes.Length
                || !source.Layout.TryGetAttribute(VertexAttributeSemantic.Position, out var position)
                || position.Format != VertexAttributeFormat.Float32 || position.Dimension != 3) return null;
            var sub = source.SubMeshes[command.SubmeshIndex];
            if (sub.Topology != Waylith.Graphics.Meshes.MeshTopology.Triangles) return null;
            // Keep unknown packed/integer channels intact by declining to alter such meshes.
            foreach (var attr in source.Layout.Attributes)
                if (attr.Format != VertexAttributeFormat.Float32 && attr.Format != VertexAttributeFormat.UNorm8) return null;
            var positions = MeshArrays.GetPositions(source);
            var matrix = new MapDrawCommand(command).Matrix;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
            foreach (var pos in positions)
            {
                var point = matrix.MultiplyPoint3x4(new Vector3(pos.X, pos.Y, pos.Z));
                min = Vector3.Min(min, point); max = Vector3.Max(max, point);
            }
            if (!MapOverlayGeometry.IsLargeFlat(min, max, plan.Source.Size.x, plan.Source.Size.z)) return null;
            var vertices = new List<(int a, int b, int c, Vector3 weights)>();
            int Index(int at) => source.IndexFormat == MeshIndexFormat.UInt16
                ? MemoryMarshal.Read<ushort>(source.GetIndexBuffer().AsSpan().Slice(at * 2)) + sub.BaseVertex
                : (int)MemoryMarshal.Read<uint>(source.GetIndexBuffer().AsSpan().Slice(at * 4)) + sub.BaseVertex;
            Vector3 Point(int at) { var p = positions[at]; return matrix.MultiplyPoint3x4(new Vector3(p.X, p.Y, p.Z)); }
            for (int i = sub.IndexStart; i + 2 < sub.IndexStart + sub.IndexCount; i += 3)
            {
                int a = Index(i), b = Index(i + 1), c = Index(i + 2);
                var polygon = MapOverlayGeometry.Clip(Point(a), Point(b), Point(c), footprint);
                for (int k = 1; k + 1 < polygon.Count; k++)
                {
                    vertices.Add((a, b, c, polygon[0].Weights));
                    vertices.Add((a, b, c, polygon[k].Weights));
                    vertices.Add((a, b, c, polygon[k + 1].Weights));
                }
            }
            changed = true;
            if (vertices.Count == 0) return null;
            var output = new CpuMeshData();
            output.SetVertexLayout(source.Layout, vertices.Count);
            foreach (var attr in source.Layout.Attributes)
            {
                int stride = source.Layout.GetStreamStride(attr.Stream);
                var destination = output.GetVertexStream(attr.Stream).AsSpan();
                for (int i = 0; i < vertices.Count; i++)
                {
                    var v = vertices[i];
                    for (int component = 0; component < attr.Dimension; component++)
                    {
                        int size = attr.Format == VertexAttributeFormat.Float32 ? 4 : 1;
                        int offset = attr.OffsetBytes + component * size;
                        float Read(int vertex) => size == 4
                            ? MemoryMarshal.Read<float>(source.GetVertexStream(attr.Stream).AsSpan().Slice(vertex * stride + offset))
                            : source.GetVertexStream(attr.Stream).AsSpan()[vertex * stride + offset];
                        float value = Read(v.a) * v.weights.x + Read(v.b) * v.weights.y + Read(v.c) * v.weights.z;
                        if (size == 4) MemoryMarshal.Write(destination.Slice(i * stride + offset), in value);
                        else destination[i * stride + offset] = (byte)Mathf.Clamp(Mathf.RoundToInt(value), 0, 255);
                    }
                }
            }
            output.SetIndexBufferParams(vertices.Count, MeshIndexFormat.UInt32);
            var outputIndices = MemoryMarshal.Cast<byte, uint>(output.GetIndexBuffer().AsSpan());
            for (int i = 0; i < vertices.Count; i++) outputIndices[i] = (uint)i;
            output.SetSubMeshCount(1);
            output.SetSubMesh(0, new SubMesh(0, vertices.Count));
            // The constructor requests upload immediately; bounds must already be complete.
            MeshArrays.RecalculateBounds(output);
            return new NativeMesh(output) { DebugName = "REF clipped planar overlay" };
        }
    }
}
#endif
