using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using RimExodus.Maps;
using UnityEngine;
using Verse;
using Waylith.Rendering;

internal static class RenderDiagnosticChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new Exception(label); }
        void Set(MapRenderDiagnostics trace, string name, object value) => typeof(MapRenderDiagnostics)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(trace, value);
        var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        map.uniqueID = 41; map.info = new MapInfo { Size = new IntVec3(20, 1, 20) };
        var plan = new MapRenderPlan(map, Matrix4x4.identity, new CellRect(0, 0, 20, 20));
        MeshDrawSubmission.ClearFrameSubmissions();
        var range = MeshDrawSubmission.ActiveArena.Allocate(1, out var propertyRange);
        range[0] = DrawProperty.Float(1234, 9.5f);
        using var globals = new MapShaderState();
        int globalId = ShaderPropertyId.GetOrCreate("_RefTraceFixture");
        GlobalShaderState.SetFloat(globalId, 12.5f);
        var shader = (Waylith.Rendering.Shader)RuntimeHelpers.GetUninitializedObject(typeof(Waylith.Rendering.Shader));
        typeof(Waylith.Rendering.Shader).GetProperty("MaterialLayout")!.SetValue(shader,
            new MaterialLayout(new[] { new MaterialUniformSlot(0, MaterialUniformType.Float, "_RefTraceFixture", globalId,
                default, -1, HasAuthoredDefault: false) }, Array.Empty<MaterialTextureSlot>(), 16));
        var material = new Waylith.Rendering.Material(shader);
        material.SetFloat(1234, 5f);
        var command = new MeshDrawCommand(null, material, propertyRange, System.Numerics.Matrix4x4.Identity, 0, 3, null, 4300, 7);
        var trace = new MapRenderDiagnostics();
        trace.Observe(plan, command, command);
        Check(!trace.Active, "Disabled diagnostics does not start capture on draw");
        Check(!trace.TrySetIsolatedSource(map) && trace.ShouldSubmit(plan), "Isolation cannot run without an active capture");

        using var stream = new MemoryStream();
        Set(trace, "writer", new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true));
        Set(trace, "sampleFrame", Time.frameCount);
        Set(trace, "previousFocus", map);
        MapRenderDiagnostics.Capturing = trace;
        trace.Observe(plan, command, command);
        Set(trace, "previousFocus", null!);
        trace.Observe(plan, command, command);
        trace.Observe(null!, command, command);
        trace.StopCapture();
        string result = Encoding.UTF8.GetString(stream.ToArray());
        Check(result.Contains("|41|focus|") && result.Contains("|41|neighbor|") && result.Contains("|-1|unscoped|"),
            "Same source is identified across focus roles; unknown ownership stays explicit");
        Check(result.Contains("1234:Float]=9.5") && result.Contains("|3|7|4300/4300|"), "Properties and route captured");
        Check(result.Contains("_RefTraceFixture:Float]=12.5") && result.Contains("M[1234:Float]=5")
            && GlobalShaderState.TryGetFloat(globalId, out var global) && global == 12.5f,
            "Global names/values and material values remain distinct and are not changed by observation");
        Check(result.Contains("COUNTS commands=3 detailOmitted=0"), "Trace closes with command counts");
        Check(!trace.Active && MapRenderDiagnostics.Capturing == null, "Stop releases routing pointer and file");
        Check(command.Matrix == System.Numerics.Matrix4x4.Identity && command.RenderQueueOverride == 4300
            && MeshDrawSubmission.ActiveArena.GetProperties(command.PropertyRange)[0].FloatValue == 9.5f,
            "Observation preserves input command and arena values");

        using var limited = new MemoryStream();
        Set(trace, "writer", new StreamWriter(limited, new UTF8Encoding(false), 1024, leaveOpen: true));
        Set(trace, "sampleFrame", Time.frameCount);
        ((Dictionary<int, int>)typeof(MapRenderDiagnostics).GetField("sourceCharacters", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(trace)!)[41] = 4 * 1024 * 1024;
        trace.Observe(plan, command, command);
        trace.Observe(null!, command, command);
        trace.StopCapture();
        string limitedResult = Encoding.UTF8.GetString(limited.ToArray());
        Check(limitedResult.Contains("detailOmitted=1"), "Per-map limit explicitly reports missing details");
        Check(limitedResult.Contains("|-1|unscoped|") && limitedResult.Contains("COUNTS commands=2 detailOmitted=1"),
            "Exhausting an earlier source never suppresses a later source's draw details");

        using var expired = new MemoryStream();
        Set(trace, "writer", new StreamWriter(expired, new UTF8Encoding(false), 1024, leaveOpen: true));
        trace.UpdateIsolation(new[] { plan }); trace.TrySetIsolatedSource(map); trace.UpdateIsolation(new[] { plan });
        Set(trace, "deadline", DateTime.UtcNow.AddSeconds(-1));
        trace.BeginFrame(map, new[] { plan });
        Check(!trace.Active && trace.IsolatedSource == null && trace.Sources.Count == 0 && trace.ShouldSubmit(null!),
            "Timeout restores source filtering before accessing the camera or sampling more draws");

        using var failed = new MemoryStream();
        var badWriter = new StreamWriter(failed);
        badWriter.Dispose();
        Set(trace, "writer", badWriter); Set(trace, "sampleFrame", Time.frameCount);
        MapRenderDiagnostics.Capturing = trace;
        trace.StopCapture();
        Check(!trace.Active && MapRenderDiagnostics.Capturing == null, "I/O failure disables diagnostics without escaping into rendering");

        var other = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        other.uniqueID = 42; other.info = new MapInfo { Size = new IntVec3(20, 1, 20) };
        var otherPlan = new MapRenderPlan(other, Matrix4x4.Translate(new Vector3(21, 0, -5)), new CellRect(0, 0, 20, 20));
        using var isolation = new MemoryStream();
        Set(trace, "writer", new StreamWriter(isolation, new UTF8Encoding(false), 1024, leaveOpen: true));
        trace.UpdateIsolation(new[] { plan });
        Check(!trace.TrySetIsolatedSource(other), "Only a current render-plan source can be selected");
        trace.UpdateIsolation(new[] { plan, otherPlan });
        Check(trace.TrySetIsolatedSource(other) && trace.ShouldSubmit(plan), "Selection takes effect at the next frame boundary");
        trace.UpdateIsolation(new[] { plan, otherPlan });
        Check(!trace.ShouldSubmit(plan) && trace.ShouldSubmit(otherPlan) && trace.ShouldSubmit(null!),
            "Isolation filters only other known sources and preserves unscoped draws");
        Set(trace, "previousFocus", other); Set(trace, "sampleFrame", -1);
        Check(!trace.ShouldSubmit(plan) && trace.ShouldSubmit(otherPlan), "Focus changes and unsampled frames retain the selected source");
        var projected = MapShaderState.Freeze(command, otherPlan.Transform.Matrix);
        Check(projected.Matrix == Matrix4x4.ToNumerics(otherPlan.Transform.Matrix) * command.Matrix
            && projected.RenderQueueOverride == command.RenderQueueOverride, "Isolated source keeps the normal projection and queue");
        Set(trace, "sampleFrame", Time.frameCount);
        trace.Observe(plan, command, command, true);
        trace.UpdateIsolation(new[] { plan });
        Check(trace.IsolatedSource == null && trace.ShouldSubmit(plan), "Losing the selected render plan restores all submissions");
        trace.UpdateIsolation(new[] { plan, otherPlan }); trace.TrySetIsolatedSource(other); trace.UpdateIsolation(new[] { plan, otherPlan });
        Check(trace.TrySetIsolatedSource(null), "Explicit A/B return to normal is accepted");
        trace.UpdateIsolation(new[] { plan, otherPlan });
        Check(trace.ShouldSubmit(plan) && trace.ShouldSubmit(otherPlan), "Explicit normal mode restores both sources");
        trace.TrySetIsolatedSource(other); trace.UpdateIsolation(new[] { plan, otherPlan });
        trace.StopCapture();
        Check(trace.IsolatedSource == null && trace.Sources.Count == 0 && trace.ShouldSubmit(plan), "Stop releases maps and restores rendering");
        string isolatedResult = Encoding.UTF8.GetString(isolation.ToArray());
        Check(isolatedResult.Contains("ISOLATION") && isolatedResult.Contains("source=42") && isolatedResult.Contains("|isolated"),
            "Trace distinguishes source suppression from clipping and records mode changes");
        MeshDrawSubmission.ClearFrameSubmissions();
        Console.WriteLine($"{checks} render diagnostic observation/bounds/failure checks passed; no GPU or game started.");
    }
}
