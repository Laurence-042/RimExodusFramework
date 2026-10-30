using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
#if RIMWORLD_1_7
using System.Threading;
using Waylith.Rendering;
#endif

namespace RimExodus.Maps
{
    // This isolates engine shader state, not arbitrary static fields owned by other mods.
    internal sealed class MapShaderState : IDisposable
    {
#if RIMWORLD_1_7
        private static readonly Type Globals = typeof(GlobalShaderState);
        private static readonly ReaderWriterLockSlim Sync = (ReaderWriterLockSlim)AccessTools.Field(Globals, "Sync").GetValue(null);
        private static readonly FieldInfo Version = AccessTools.Field(Globals, "version");
        private static readonly FieldInfo Registration = AccessTools.Field(Globals, "registrationVersion");
        private readonly Dictionary<int, float> floats;
        private readonly Dictionary<int, System.Numerics.Vector4> vectors;
        private readonly Dictionary<int, System.Numerics.Matrix4x4> matrices;
        private readonly Dictionary<int, Waylith.Rendering.Texture> textures;
        private static class Table<T>
        {
            internal static readonly Dictionary<string, Dictionary<int, T>> Cached = new Dictionary<string, Dictionary<int, T>>();
        }
        private static Dictionary<int, T> Values<T>(string name) =>
            Table<T>.Cached.TryGetValue(name, out var table) ? table :
                Table<T>.Cached[name] = (Dictionary<int, T>)AccessTools.Field(Globals, name).GetValue(null);
        internal MapShaderState()
        {
            Sync.EnterReadLock();
            try
            {
                floats = new Dictionary<int, float>(Values<float>("Floats"));
                vectors = new Dictionary<int, System.Numerics.Vector4>(Values<System.Numerics.Vector4>("Vectors"));
                matrices = new Dictionary<int, System.Numerics.Matrix4x4>(Values<System.Numerics.Matrix4x4>("Matrices"));
                textures = new Dictionary<int, Waylith.Rendering.Texture>(Values<Waylith.Rendering.Texture>("Textures"));
            }
            finally { Sync.ExitReadLock(); }
        }
        private static void Restore<T>(string name, Dictionary<int, T> saved)
        {
            var target = Values<T>(name);
            target.Clear();
            foreach (var item in saved) target.Add(item.Key, item.Value);
        }
        public void Dispose()
        {
            Sync.EnterWriteLock();
            try
            {
                Restore("Floats", floats); Restore("Vectors", vectors);
                Restore("Matrices", matrices); Restore("Textures", textures);
                Version.SetValue(null, (long)Version.GetValue(null) + 1);
                Registration.SetValue(null, (long)Registration.GetValue(null) + 1);
            }
            finally { Sync.ExitWriteLock(); }
        }

        // Copy material values and only the globals actually consumed by this shader. Original MPB values win.
        // Capturing all global names as overrides would wrongly replace authored shader defaults and materials.
        internal static MeshDrawCommand Freeze(in MeshDrawCommand command, Matrix4x4 transform)
        {
            if (command.Material == null) return command;
            var values = new Dictionary<int, DrawProperty>();
            var layout = command.Material.Shader.MaterialLayout;
            foreach (var slot in layout.UniformSlots)
            {
                if (slot.HasAuthoredDefault) continue;
                int id = slot.PropertyId;
                switch (slot.Type)
                {
                    case MaterialUniformType.Float:
                        values[id] = DrawProperty.Float(id, GlobalShaderState.TryGetFloat(id, out var f) ? f : slot.DefaultValue.Float);
                        break;
                    case MaterialUniformType.Matrix3x3:
                    case MaterialUniformType.Matrix4x4:
                        values[id] = DrawProperty.Matrix(id, GlobalShaderState.TryGetMatrix(id, out var m) ? m : slot.DefaultValue.Matrix4x4);
                        break;
                    case MaterialUniformType.Vector2:
                    case MaterialUniformType.Vector3:
                    case MaterialUniformType.Vector4:
                    case MaterialUniformType.TextureST:
                    case MaterialUniformType.TexelSize:
                        values[id] = DrawProperty.Vector(id, GlobalShaderState.TryGetVector(id, out var v) ? v : slot.DefaultValue.Vector4);
                        break;
                }
            }
            foreach (var slot in layout.TextureSlots)
                if (slot.Kind != MaterialTextureKind.Sampler)
                {
                    GlobalShaderState.TryGetTexture(slot.PropertyId, out var tex);
                    values[slot.PropertyId] = DrawProperty.Texture(slot.PropertyId, tex);
                }
            foreach (var property in command.Material.PropertiesForReading) values[property.PropertyId] = property;
            foreach (var property in MeshDrawSubmission.ActiveArena.GetProperties(command.PropertyRange))
                values[property.PropertyId] = property;
            var rangeValues = MeshDrawSubmission.ActiveArena.Allocate(values.Count, out var range);
            int index = 0;
            foreach (var property in values.Values) rangeValues[index++] = property;
            return new MeshDrawCommand(command.Mesh, command.Material, range,
                Matrix4x4.ToNumerics(transform) * command.Matrix, command.SubmeshIndex, command.PassId,
                command.Instancing, command.RenderQueueOverride > 0 ? command.RenderQueueOverride : command.Material.RenderQueue,
                command.Layer, command.Storage);
        }
        [HarmonyPatch]
        private static class SubmissionPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(MeshDrawSubmission), nameof(MeshDrawSubmission.Enqueue),
                new[] { typeof(MeshDrawCommand).MakeByRefType() });
            private static bool Prefix(ref MeshDrawCommand command)
            {
                var plan = MapRenderScope.Current;
                if (plan == null) return true;
                command = MapRuntime.Current.Rendering.Clip(command, plan);
                if (command.Mesh == null) return false;
                command = Freeze(command, plan.Transform.Matrix);
                return true;
            }
        }
#else
        [ThreadStatic] private static MapShaderState active;
        private readonly Dictionary<int, Action> restores = new Dictionary<int, Action>();
        private readonly Dictionary<int, Action<Material>> bindings = new Dictionary<int, Action<Material>>();
        private static Material FreezeMaterial(Material material)
        {
            var copy = MapRuntime.Current.Rendering.MaterialCopy(material);
            if (active != null)
                foreach (var bind in active.bindings)
                    if (!copy.HasProperty(bind.Key)) bind.Value(copy);
            return copy;
        }
        internal MapShaderState() { active = this; }
        private void Save(int id, Action restore, Action<Material> bind)
        { if (!restores.ContainsKey(id)) restores.Add(id, restore); bindings[id] = bind; }
        public void Dispose()
        {
            active = null;
            foreach (var restore in restores.Values) restore();
        }
        [HarmonyPatch(typeof(Shader), nameof(Shader.SetGlobalFloat), new[] { typeof(int), typeof(float) })]
        private static class FloatPatch
        {
            private static void Prefix(int nameID, float value)
            { if (active == null) return; float old = Shader.GetGlobalFloat(nameID);
                active.Save(nameID, () => Shader.SetGlobalFloat(nameID, old), m => m.SetFloat(nameID, value)); }
        }
        [HarmonyPatch(typeof(Shader), nameof(Shader.SetGlobalVector), new[] { typeof(int), typeof(Vector4) })]
        private static class VectorPatch
        {
            private static void Prefix(int nameID, Vector4 value)
            { if (active == null) return; var old = Shader.GetGlobalVector(nameID);
                active.Save(nameID, () => Shader.SetGlobalVector(nameID, old), m => m.SetVector(nameID, value)); }
        }
        [HarmonyPatch(typeof(Shader), nameof(Shader.SetGlobalColor), new[] { typeof(int), typeof(Color) })]
        private static class ColorPatch
        {
            private static void Prefix(int nameID, Color value)
            { if (active == null) return; var old = Shader.GetGlobalColor(nameID);
                active.Save(nameID, () => Shader.SetGlobalColor(nameID, old), m => m.SetColor(nameID, value)); }
        }
        [HarmonyPatch(typeof(Shader), nameof(Shader.SetGlobalTexture), new[] { typeof(int), typeof(Texture) })]
        private static class TexturePatch
        {
            private static void Prefix(int nameID, Texture value)
            { if (active == null) return; var old = Shader.GetGlobalTexture(nameID);
                active.Save(nameID, () => Shader.SetGlobalTexture(nameID, old), m => m.SetTexture(nameID, value)); }
        }
        [HarmonyPatch(typeof(Shader), nameof(Shader.SetGlobalMatrix), new[] { typeof(int), typeof(Matrix4x4) })]
        private static class MatrixPatch
        {
            private static void Prefix(int nameID, Matrix4x4 value)
            { if (active == null) return; var old = Shader.GetGlobalMatrix(nameID);
                active.Save(nameID, () => Shader.SetGlobalMatrix(nameID, old), m => m.SetMatrix(nameID, value)); }
        }
        [HarmonyPatch]
        private static class SubmissionPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh),
                new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int), typeof(Camera), typeof(int),
                    typeof(MaterialPropertyBlock), typeof(UnityEngine.Rendering.ShadowCastingMode), typeof(bool), typeof(Transform),
                    typeof(UnityEngine.Rendering.LightProbeUsage), typeof(LightProbeProxyVolume) });
            private static bool Prefix(ref Mesh __0, ref Matrix4x4 __1, ref Material __2, ref int __5)
            {
                var plan = MapRenderScope.Current;
                if (plan == null || __2 == null) return true;
                if (!MapRuntime.Current.Rendering.UnityClipper.Clip(ref __0, __1, ref __5, plan)) return false;
                __1 = plan.Transform.Matrix * __1;
                __2 = FreezeMaterial(__2);
                return true;
            }
        }
        [HarmonyPatch]
        private static class InstancedSubmissionPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMeshInstanced),
                new[] { typeof(Mesh), typeof(int), typeof(Material), typeof(Matrix4x4[]), typeof(int),
                    typeof(MaterialPropertyBlock), typeof(UnityEngine.Rendering.ShadowCastingMode), typeof(bool), typeof(int),
                    typeof(Camera), typeof(UnityEngine.Rendering.LightProbeUsage), typeof(LightProbeProxyVolume) });
            private static void Prefix(ref Material __2, ref Matrix4x4[] __3, int __4)
            {
                var plan = MapRenderScope.Current;
                if (plan == null || __2 == null || __3 == null || __4 < 0 || __4 > __3.Length) return;
                // Never mutate the caller's persistent instance matrices.
                var transformed = new Matrix4x4[__4];
                for (int i = 0; i < __4; i++) transformed[i] = plan.Transform.Matrix * __3[i];
                __3 = transformed;
                __2 = FreezeMaterial(__2);
            }
        }
#endif
    }
}
