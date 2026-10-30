using RimExodus.Maps;
using UnityEngine;
using Waylith.Rendering;
using Waylith.Graphics.Meshes;
using System.Runtime.InteropServices;

internal static class NativeRenderChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        var footprint = new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) };
        var a = new Vector3(-10, 3, -10); var b = new Vector3(30, 3, -10); var c = new Vector3(-10, 3, 30);
        foreach (var boundary in new[] { footprint, footprint.Reverse().ToArray() })
        {
            var result = MapOverlayGeometry.Clip(a, b, c, boundary);
            Check(result.Count >= 4, "Clipping retains footprint regardless of winding");
            float area = 0;
            for (int i = 0; i < result.Count; i++)
            {
                var v = result[i]; var next = result[(i + 1) % result.Count];
                Check(v.Position.x >= -0.0001f && v.Position.x <= 10.0001f && v.Position.z >= -0.0001f && v.Position.z <= 10.0001f,
                    "No overlay vertex outside footprint");
                Check((a * v.Weights.x + b * v.Weights.y + c * v.Weights.z - v.Position).sqrMagnitude < 0.0001f,
                    "Barycentric interpolation preserves source UV/position correspondence");
                Check(Math.Abs(v.Weights.x + v.Weights.y + v.Weights.z - 1) < 0.0001f, "Interpolation is affine");
                area += v.Position.x * next.Position.z - next.Position.x * v.Position.z;
            }
            Check(Math.Abs(Math.Abs(area) / 2 - 100) < 0.001f, "Clipped triangles cover footprint area");
        }
        Check(MapOverlayGeometry.Clip(a, a + Vector3.right, a + Vector3.forward, footprint).Count == 0, "Outside overlay omitted");
        Check(!MapOverlayGeometry.IsLargeFlat(Vector3.zero, new Vector3(100, 5, 100), 100, 100), "3D geometry untouched");
        Check(!MapOverlayGeometry.IsLargeFlat(Vector3.zero, new Vector3(5, 0, 5), 100, 100), "Small geometry untouched");

        var map = (Verse.Map)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Verse.Map));
        map.info = new Verse.MapInfo { Size = new Verse.IntVec3(20, 1, 20) };
        var plan = new MapRenderPlan(map, Matrix4x4.identity, new Verse.CellRect(0, 0, 20, 20),
            new MapRenderOptions { OverlayFootprint = footprint });
        var cpu = new CpuMeshData();
        cpu.SetVertexLayout(new VertexLayout(new[] {
            new VertexAttribute(VertexAttributeSemantic.Position, VertexAttributeFormat.Float32, 3, 0, 0),
            new VertexAttribute(VertexAttributeSemantic.TexCoord0, VertexAttributeFormat.Float32, 2, 0, 12) }), 4);
        // Unreferenced/off-plane vertices must not disqualify the selected planar submesh.
        float[] vertexData = { -10, 3, -10, 0, 0, 30, 3, -10, 1, 0, -10, 3, 30, 0, 1, 0, 80, 0, 0, 0 };
        vertexData.CopyTo(MemoryMarshal.Cast<byte, float>(cpu.GetVertexStream(0).AsSpan()));
        cpu.SetIndexBufferParams(3, MeshIndexFormat.UInt32);
        var indices = MemoryMarshal.Cast<byte, uint>(cpu.GetIndexBuffer().AsSpan());
        indices[0] = 0; indices[1] = 1; indices[2] = 2;
        cpu.SetSubMeshCount(1); cpu.SetSubMesh(0, new SubMesh(0, 3));
        using (var mesh = new Waylith.Graphics.Meshes.Mesh(cpu))
        using (var clipper = new MapOverlayClipper())
        {
            mesh.RecalculateBounds();
            var draw = new MeshDrawCommand(mesh, null, default, System.Numerics.Matrix4x4.Identity, 0, 3, null, 4300, 7);
            var clipped = clipper.Clip(draw, plan);
            Check(clipped.Mesh != null && clipped.Mesh != mesh, "Real native mesh clipped");
            Check(clipped.Layer == 7 && clipped.PassId == 3 && clipped.RenderQueueOverride == 4300, "Clipping preserves draw route");
            var data = clipped.Mesh!.CpuStorage;
            var output = MemoryMarshal.Cast<byte, float>(data.GetVertexStream(0).AsSpan());
            for (int i = 0; i < data.VertexCount; i++)
            {
                Check(Math.Abs(output[i * 5 + 3] - (output[i * 5] + 10) / 40) < 0.0001f, "Clipped U agrees with source position");
                Check(Math.Abs(output[i * 5 + 4] - (output[i * 5 + 2] + 10) / 40) < 0.0001f, "Clipped V agrees with source position");
            }
            Check(ReferenceEquals(clipper.Clip(draw, plan).Mesh, clipped.Mesh), "Unchanged mesh reuses clipped cache");
            mesh.MarkCpuStorageChanged();
            Check(!ReferenceEquals(clipper.Clip(draw, plan).Mesh, clipped.Mesh), "Mesh version change rebuilds clipping");
            foreach (float padding in new[] { 1f, 10f, 200f })
            {
                clipper.Dispose(); // Exercise classification instead of a previous cached result.
                mesh.Bounds = new MeshBounds(-10, 3 - padding / 2, -10, 30, 3 + padding / 2, 30);
                var padded = clipper.Clip(draw, plan);
                Check(padded.Mesh != null && padded.Mesh != mesh, "Padded culling bounds do not hide a planar overlay: " + padding);
                Check(ReferenceEquals(clipper.Clip(draw, plan).Mesh, padded.Mesh), "Padded overlay uses normal geometry cache");
            }
            var sourceData = MemoryMarshal.Cast<byte, float>(cpu.GetVertexStream(0).AsSpan());
            sourceData[1] = 8;
            mesh.MarkCpuStorageChanged();
            Check(ReferenceEquals(clipper.Clip(draw, plan).Mesh, mesh), "Actual non-planar geometry remains untouched with large bounds");
            sourceData[1] = 3;
            sourceData[5] = -9; sourceData[12] = -9;
            mesh.MarkCpuStorageChanged();
            Check(ReferenceEquals(clipper.Clip(draw, plan).Mesh, mesh), "Padded bounds cannot turn a small triangle into a large overlay");
        }

        int inherited = ShaderPropertyId.GetOrCreate("_RefTestInherited"), authored = ShaderPropertyId.GetOrCreate("_RefTestAuthored"),
            materialId = ShaderPropertyId.GetOrCreate("_RefTestMaterial"), overrideId = ShaderPropertyId.GetOrCreate("_RefTestOverride"),
            introduced = ShaderPropertyId.GetOrCreate("_RefTestIntroduced"), missing = ShaderPropertyId.GetOrCreate("_RefTestMissing");
        var slots = new[] { inherited, authored, materialId, overrideId, missing }.Select((id, index) =>
            new MaterialUniformSlot(index * 4, MaterialUniformType.Float, "test" + index, id,
                default, -1, HasAuthoredDefault: id == authored)).ToArray();
        // Supply layout metadata only: the real Shader constructor boots the game-wide shader registry/GPU assets.
        var shader = (Waylith.Rendering.Shader)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Waylith.Rendering.Shader));
        typeof(Waylith.Rendering.Shader).GetProperty("MaterialLayout")!.SetValue(shader,
            new MaterialLayout(slots, Array.Empty<MaterialTextureSlot>(), 32));
        var material = new Waylith.Rendering.Material(shader);
        material.RenderQueue = 4300;
        material.SetFloat(materialId, 23); material.SetFloat(overrideId, 24);
        GlobalShaderState.SetFloat(inherited, 1);
        MeshDrawSubmission.ClearFrameSubmissions();
        var sourceValues = MeshDrawSubmission.ActiveArena.Allocate(1, out var sourceRange);
        sourceValues[0] = DrawProperty.Float(overrideId, 35);
        MeshDrawCommand frozen = default;
        var transform = Matrix4x4.TRS(new Vector3(20, 0, -15), Quaternion.Euler(0, 90, 0), Vector3.one);
        var local = Matrix4x4.Translate(new Vector3(4, 2, 7));
        try
        {
            using var scope = new MapShaderState();
            GlobalShaderState.SetFloat(inherited, 12); GlobalShaderState.SetFloat(authored, 13);
            GlobalShaderState.SetFloat(materialId, 14); GlobalShaderState.SetFloat(overrideId, 15);
            GlobalShaderState.SetFloat(introduced, 99);
            var command = new MeshDrawCommand(null, material, sourceRange, Matrix4x4.ToNumerics(local), 2, 3, null, 0, 7);
            frozen = MapShaderState.Freeze(command, transform);
            throw new InvalidOperationException("Simulated mod draw failure");
        }
        catch (InvalidOperationException) { }
        Check(GlobalShaderState.TryGetFloat(inherited, out var restored) && restored == 1, "Original shader global restored after exception");
        Check(!GlobalShaderState.TryGetFloat(introduced, out _), "New global removed after exception");
        var values = MeshDrawSubmission.ActiveArena.GetProperties(frozen.PropertyRange).ToArray().ToDictionary(v => v.PropertyId);
        Check(values[inherited].FloatValue == 12, "Neighbor globals survive deferred submission");
        Check(values[missing].FloatValue == 0, "Absent globals freeze shader fallback instead of inheriting a later map's value");
        Check(!values.ContainsKey(authored), "Shader authored defaults take precedence over globals");
        Check(values[materialId].FloatValue == 23 && values[overrideId].FloatValue == 35, "Material and property block precedence preserved");
        Check(frozen.RenderQueueOverride == 4300 && frozen.PassId == 3 && frozen.Layer == 7 && frozen.SubmeshIndex == 2,
            "Native render queue, pass, layer and submesh preserved");
        Check(frozen.Matrix == Matrix4x4.ToNumerics(transform * local), "Native command rotates and translates in display space");
        material.SetFloat(materialId, 100);
        Check(values[materialId].FloatValue == 23, "Later shared material writes do not change submitted neighbor values");
        MeshDrawSubmission.ClearFrameSubmissions();
        Console.WriteLine($"{checks} native rendering isolation/ordering/overlay geometry checks passed; no GPU or game started.");
    }
}
