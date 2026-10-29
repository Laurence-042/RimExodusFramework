using RimExodus.Maps;
using UnityEngine;
using Waylith.Rendering;

static class DrawCaptureChecks
{
    public static void Run()
    {
        int checks = 0;
        void Assert(bool value, string label) { checks++; if (!value) throw new Exception(label); }
        void SameMatrix(Matrix4x4 actual, Matrix4x4 expected, string label)
        {
            foreach (var point in new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward })
                Assert((actual.MultiplyPoint3x4(point) - expected.MultiplyPoint3x4(point)).sqrMagnitude < 0.00001f, label);
        }

        // CPU-only native commands exercise the real capture and submission APIs without a GPU or game.
        var local = Matrix4x4.TRS(new Vector3(20, 1.5f, -17), Quaternion.Euler(0, 90, 0), new Vector3(2, 1, 3));
        foreach (float angle in new[] { 0f, 35f, 90f, 180f, 270f })
        {
            var pose = Matrix4x4.TRS(new Vector3(60, 0.02f, 25), Quaternion.Euler(0, angle, 0), Vector3.one);
            var source = new MeshDrawCommand(null, null, default, Matrix4x4.ToNumerics(local), 2, 3, null, 2450, 0);
            var captured = new List<MapDrawCommand>();
            MapDrawCapture.Collect(() => MeshDrawSubmission.Enqueue(in source), pose, captured.Add);
            Assert(captured.Count == 1 && !MapDrawCapture.Active && !MeshDrawSubmission.IsCapturing, "Capture scope completes");
            SameMatrix(captured[0].Matrix, pose * local, "Captured component transform: " + angle);
            var extra = Matrix4x4.Translate(new Vector3(-13, 0.03f, 4));
            var mapped = captured[0].Transformed(extra);
            SameMatrix(mapped.Matrix, extra * pose * local, "Additional command transform: " + angle);
            SameMatrix(captured[0].Transformed(pose.inverse).Matrix, local, "Return to focused coordinates: " + angle);

            MeshDrawSubmission.ClearFrameSubmissions();
            using var backend = new MapDrawBackend(false, "Offline capture regression");
            backend.BeginFrame();
            backend.BeginGroup();
            backend.Draw(mapped);
            var submitted = MeshDrawSubmission.CaptureSnapshot();
            Assert(submitted.CommandCount == 1, "Captured command reaches submission");
            var native = submitted.Commands[0];
            Assert(native.Matrix == Matrix4x4.ToNumerics(mapped.Matrix), "Submitted native matrix preserves wrapper layout");
            Assert(native.SubmeshIndex == 2 && native.PassId == 3 && native.RenderQueueOverride == 1,
                "Submission preserves submesh/pass and applies background ordering");
        }
        MeshDrawSubmission.ClearFrameSubmissions();
        Console.WriteLine($"{checks} native draw capture/transform/submission checks passed; no GPU or game started.");
    }
}
