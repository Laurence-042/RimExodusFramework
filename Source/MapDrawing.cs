using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace RimExodus.Maps
{
    public readonly struct MapDrawCommand
    {
        public readonly Mesh Mesh;
        public readonly Matrix4x4 Matrix;
        public readonly Material Material;
        public readonly int Submesh;
        public readonly MaterialPropertyBlock Properties;
        public readonly int Layer;
#if RIMWORLD_1_7
        internal readonly Waylith.Rendering.MeshDrawCommand Native;
        internal readonly bool IsNative;
        internal MapDrawCommand(Waylith.Rendering.MeshDrawCommand command)
        {
            Native = command; IsNative = true;
            Mesh = null; Material = null; Submesh = command.SubmeshIndex; Properties = null; Layer = command.Layer;
            var m = command.Matrix;
            // UnityStubs.ToNumerics preserves the column-vector layout; it does not transpose.
            Matrix = new Matrix4x4 { m00 = m.M11, m01 = m.M12, m02 = m.M13, m03 = m.M14,
                m10 = m.M21, m11 = m.M22, m12 = m.M23, m13 = m.M24,
                m20 = m.M31, m21 = m.M32, m22 = m.M33, m23 = m.M34,
                m30 = m.M41, m31 = m.M42, m32 = m.M43, m33 = m.M44 };
        }
#endif
        public MapDrawCommand(Mesh mesh, Matrix4x4 matrix, Material material, int submesh = 0,
            MaterialPropertyBlock properties = null, int layer = 0)
        {
            Mesh = mesh; Matrix = matrix; Material = material; Submesh = submesh; Properties = properties; Layer = layer;
#if RIMWORLD_1_7
            Native = default; IsNative = false;
#endif
        }
        public int RenderQueue
        {
            get
            {
#if RIMWORLD_1_7
                if (IsNative) return Native.RenderQueueOverride > 0 ? Native.RenderQueueOverride : Native.Material.RenderQueue;
#endif
                return Material.renderQueue;
            }
        }
        public bool UsesMaterial(Material material)
        {
#if RIMWORLD_1_7
            if (IsNative) return material != null && ReferenceEquals(Native.Material, material.engineMaterial);
#endif
            return Material == material;
        }
        public MapDrawCommand Transformed(Matrix4x4 transform)
        {
#if RIMWORLD_1_7
            if (IsNative)
            {
                var c = Native;
                return new MapDrawCommand(new Waylith.Rendering.MeshDrawCommand(c.Mesh, c.Material, c.PropertyRange,
                    Matrix4x4.ToNumerics(transform) * c.Matrix, c.SubmeshIndex, c.PassId, c.Instancing,
                    c.RenderQueueOverride, c.Layer, c.Storage));
            }
#endif
            return new MapDrawCommand(Mesh, transform * Matrix, Material, Submesh, Properties, Layer);
        }
    }

    public static class MapDrawCapture
    {
        [ThreadStatic] private static Action<MapDrawCommand> collector;
        [ThreadStatic] private static Matrix4x4 transform;
        public static bool Active => collector != null;
        public static void Collect(Action draw, Matrix4x4 matrix, Action<MapDrawCommand> collect)
        {
            if (collector != null) throw new InvalidOperationException("Nested map draw capture.");
            collector = collect; transform = matrix;
            try
            {
#if RIMWORLD_1_7
                var commands = new List<Waylith.Rendering.MeshDrawCommand>();
                using (Waylith.Rendering.MeshDrawSubmission.BeginCapture(commands)) draw();
                foreach (var c in commands)
                {
                    var mapped = new Waylith.Rendering.MeshDrawCommand(c.Mesh, c.Material, c.PropertyRange,
                        Matrix4x4.ToNumerics(matrix) * c.Matrix, c.SubmeshIndex, c.PassId, c.Instancing,
                        c.RenderQueueOverride, c.Layer, c.Storage);
                    collect(new MapDrawCommand(mapped));
                }
#else
                draw();
#endif
            }
            finally { collector = null; transform = default; }
        }
#if !RIMWORLD_1_7
        [HarmonyPatch]
        private static class CapturePatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh),
                new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int), typeof(Camera), typeof(int),
                    typeof(MaterialPropertyBlock), typeof(ShadowCastingMode), typeof(bool), typeof(Transform),
                    typeof(LightProbeUsage), typeof(LightProbeProxyVolume) });
            private static bool Prefix(Mesh __0, Matrix4x4 __1, Material __2, int __3, int __5, MaterialPropertyBlock __6)
            {
                if (collector == null) return true;
                collector(new MapDrawCommand(__0, transform * __1, __2, __5, __6, __3));
                return false;
            }
        }
#endif
    }

    /// <summary>One ordered depth group per map/surface, retaining native blending inside that group.</summary>
    public sealed class MapDrawBackend : IDisposable
    {
        private readonly bool foreground;
        private readonly string name;
        private int group;
#if !RIMWORLD_1_7
        private CommandBuffer buffer;
        private Camera camera;
#endif
        public MapDrawBackend(bool foreground, string name) { this.foreground = foreground; this.name = name; }
        public void Attach(Camera target)
        {
#if !RIMWORLD_1_7
            if (camera == target && buffer != null) return;
            Dispose(); camera = target; buffer = new CommandBuffer { name = name };
            camera.AddCommandBuffer(foreground ? CameraEvent.AfterForwardAlpha : CameraEvent.BeforeForwardOpaque, buffer);
#endif
        }
        public void BeginFrame()
        {
            group = 0;
#if !RIMWORLD_1_7
            buffer?.Clear();
#endif
        }
        public void BeginGroup()
        {
            group++;
#if !RIMWORLD_1_7
            buffer?.ClearRenderTarget(true, false, Color.clear, 1f);
#endif
        }
        public void Draw(MapDrawCommand command)
        {
#if RIMWORLD_1_7
            if (!command.IsNative)
            {
                MapDrawCapture.Collect(() => Graphics.DrawMesh(command.Mesh, command.Matrix, command.Material,
                    command.Layer, null, command.Submesh, command.Properties), Matrix4x4.identity, SubmitNative);
            }
            else SubmitNative(command);
#else
            buffer.DrawMesh(command.Mesh, command.Matrix, command.Material, command.Submesh, -1, command.Properties);
#endif
        }
#if RIMWORLD_1_7
        private void SubmitNative(MapDrawCommand command)
        {
            var c = command.Native;
            int layer = WaylithMapLayers.Encode(group, foreground);
            var tagged = new Waylith.Rendering.MeshDrawCommand(c.Mesh, c.Material, c.PropertyRange, c.Matrix,
                c.SubmeshIndex, c.PassId, c.Instancing, foreground ? 8190 : 1, layer, c.Storage);
            Waylith.Rendering.MeshDrawSubmission.Enqueue(in tagged);
        }
#endif
        public void Finish()
        {
#if !RIMWORLD_1_7
            buffer?.ClearRenderTarget(true, false, Color.clear, 1f);
#endif
        }
        public void Dispose()
        {
#if !RIMWORLD_1_7
            if (camera != null && buffer != null)
                camera.RemoveCommandBuffer(foreground ? CameraEvent.AfterForwardAlpha : CameraEvent.BeforeForwardOpaque, buffer);
            buffer?.Release(); buffer = null; camera = null;
#endif
            group = 0;
        }
    }
}
