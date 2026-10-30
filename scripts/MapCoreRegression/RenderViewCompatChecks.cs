using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using RimExodus.Maps;
using UnityEngine;
using Verse;

internal static class RenderViewCompatChecks
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static CellRect ViewQuery() => CellRect.Empty;
    public static void CameraPostfix(ref CellRect __result) => __result = CellRect.FromLimits(38, 57, 249, 249);

    // Simulate the two independent IsoRim query entrances. Do not call BoxVisible from MeshVisible:
    // the reference implementation evaluates the planes itself in each entrance.
    private static class CullFixture
    {
        internal static Matrix4x4 LastMatrix;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool BoxVisible(Vector3 min, Vector3 max) => min.x <= 5 && max.x >= -5 && min.z <= 5 && max.z >= -5;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool MeshVisible(Mesh mesh, Matrix4x4 m)
        { LastMatrix = m; var p = m.MultiplyPoint3x4(Vector3.zero); return Math.Abs(p.x) <= 5 && Math.Abs(p.z) <= 5; }
    }
    private static class DriftFixture
    {
        public static bool BoxVisible(Vector3 min, Vector3 max) => true;
        public static bool MeshVisible(Mesh mesh) => true;
    }

    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        var scopeField = AccessTools.Field(typeof(MapRenderScope), "current");
        object oldScope = scopeField.GetValue(null);
        var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        map.info = new MapInfo { Size = new IntVec3(250, 1, 250) };
        var transform = Matrix4x4.Translate(new Vector3(138, 0, 161));
        var view = new MapRenderPlan(map, transform, CellRect.FromLimits(138, 161, 348, 367));
        void Scope(MapRenderPlan plan)
        {
            if (plan == null) { scopeField.SetValue(null, null); return; }
            var scope = RuntimeHelpers.GetUninitializedObject(typeof(MapRenderScope));
            AccessTools.Field(typeof(MapRenderScope), "Plan").SetValue(scope, plan);
            scopeField.SetValue(null, scope);
        }
        try
        {
            var target = AccessTools.Method(typeof(RenderViewCompatChecks), nameof(ViewQuery));
            var viewHook = AccessTools.Method(typeof(MapRenderScope).GetNestedType("ViewPatch", BindingFlags.NonPublic), "Postfix");
            foreach (bool refFirst in new[] { true, false })
            {
                var hRef = new Harmony("REF.Regression.ViewScope");
                var hCamera = new Harmony("REF.Regression.Camera");
                try
                {
                    void Ref() => hRef.Patch(target, postfix: new HarmonyMethod(viewHook));
                    void Camera() => hCamera.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(RenderViewCompatChecks), nameof(CameraPostfix))));
                    if (refFirst) { Ref(); Camera(); } else { Camera(); Ref(); }
                    Scope(view);
                    Check(ViewQuery() == view.LocalView, "Source-local view survives camera postfix in either registration order");
                    Scope(null);
                    Check(ViewQuery() == CellRect.FromLimits(38, 57, 249, 249), "Camera mod owns the view outside the map scope");
                }
                finally { hRef.UnpatchAll(hRef.Id); hCamera.UnpatchAll(hCamera.Id); }
            }

            var hCull = new Harmony("REF.Regression.IsoRimCulling");
            try
            {
                foreach (Type missing in new Type[] { null, typeof(DriftFixture) })
                {
                    bool rejected = false;
                    try { IsoRimCullingCompat.Bind(hCull, missing!); } catch (MissingMethodException) { rejected = true; }
                    Check(rejected && !hCull.GetPatchedMethods().Any(), "Missing or drifted API installs no partial hooks");
                }
                IsoRimCullingCompat.Bind(hCull, typeof(CullFixture));
                Check(hCull.GetPatchedMethods().Count() == 2, "Both known culling entrances bind");
                var min = new Vector3(-140, 0, -163); var max = new Vector3(-136, 1, -159);
                var model = Matrix4x4.Translate(new Vector3(-138, 0, -161));
                for (int i = 0; i < 3; i++)
                {
                    Scope(null);
                    Check(!CullFixture.BoxVisible(min, max) && !CullFixture.MeshVisible(null!, model), "No scope leaves source coordinates alone");
                    Scope(view);
                    Check(CullFixture.BoxVisible(min, max) && CullFixture.MeshVisible(null!, model), "A translated source is tested in display space");
                    Check(min.x == -140 && max.x == -136 && model.m03 == -138 && CullFixture.LastMatrix == transform * model,
                        "No mutation of caller values or repeated/double projection");
                    Scope(new MapRenderPlan(map, Matrix4x4.identity, CellRect.WholeMap(map)));
                    Check(!CullFixture.BoxVisible(min, max) && !CullFixture.MeshVisible(null!, model), "Focused identity scope leaves original culling unchanged");
                }
                foreach (float angle in new[] { 0f, 35f, 90f, 180f, 270f })
                {
                    var rotation = Matrix4x4.TRS(new Vector3(12, 3, -8), Quaternion.Euler(0, angle, 0), new Vector3(2, 0.5f, 3));
                    var mapping = new MapTransform(rotation);
                    var lo = new Vector3(-2, -1, -4); var hi = new Vector3(3, 5, 2);
                    mapping.BoundsToDisplay(lo, hi, out var actualLo, out var actualHi);
                    Vector3 expectedLo = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                    Vector3 expectedHi = -expectedLo;
                    foreach (float x in new[] { lo.x, hi.x })
                    foreach (float y in new[] { lo.y, hi.y })
                    foreach (float z in new[] { lo.z, hi.z })
                    { var p = mapping.ToDisplay(new Vector3(x, y, z)); expectedLo = Vector3.Min(expectedLo, p); expectedHi = Vector3.Max(expectedHi, p); }
                    Check((actualLo - expectedLo).sqrMagnitude < 1e-7f && (actualHi - expectedHi).sqrMagnitude < 1e-7f,
                        "Projected AABB matches eight-corner oracle for rotation and nonuniform scale");
                    Scope(new MapRenderPlan(map, rotation, CellRect.WholeMap(map)));
                    CullFixture.MeshVisible(null!, model);
                    Check(CullFixture.LastMatrix == rotation * model, "Culling model uses left multiplication for rotated views");
                }
            }
            finally { hCull.UnpatchAll(hCull.Id); Scope(null); }

            // Optional repository reference. Bind the real methods and exercise its real BoxVisible
            // with recorded camera planes, without starting View3D, any GPU resources or the game.
            string isoPath = Path.GetFullPath("../_references/IsoRim-1.7.installed/1.7/Assemblies/IsoRim.dll");
            if (File.Exists(isoPath))
            {
                var iso = AssemblyLoadContext.Default.LoadFromAssemblyPath(isoPath);
                var cull = iso.GetType("IsoRim.Cull", true)!;
                var hReal = new Harmony("REF.Regression.IsoRimReference");
                var planes = (Vector4[])AccessTools.Field(cull, "planes").GetValue(null)!;
                var saved = (Vector4[])planes.Clone();
                try
                {
                    var x = new Vector4(0.7071067f, 4.470348E-11f, -0.7071068f, 13.43507f) * 0.009375f;
                    var y = new Vector4(0.4086071f, 0.002448413f, 0.408607f, -167.9375f) * -0.01666667f;
                    var w = new Vector4(0, 0, 0, 1);
                    planes[0] = w - x; planes[1] = w + x; planes[2] = w - y; planes[3] = w + y; planes[4] = w;
                    var query = (Func<Vector3, Vector3, bool>)AccessTools.Method(cull, "BoxVisible", new[] { typeof(Vector3), typeof(Vector3) })
                        .CreateDelegate(typeof(Func<Vector3, Vector3, bool>));
                    var lo = new Vector3(0, 0, 68); var hi = new Vector3(17, 1, 85);
                    Scope(null);
                    Check(!query(lo, hi), "Actual IsoRim rejects the reported local block in the wrong coordinate frame");
                    IsoRimCullingCompat.Bind(hReal, cull);
                    Check(hReal.GetPatchedMethods().Count() == 2, "Production hooks bind both actual reference methods");
                    Scope(view);
                    Check(query(lo, hi), "Actual IsoRim accepts that same block after REF display projection");
                    Scope(null);
                    Check(!query(lo, hi), "Leaving scope restores the actual reference's original behavior");
                }
                finally { Array.Copy(saved, planes, saved.Length); hReal.UnpatchAll(hReal.Id); }
            }
            else Console.WriteLine("IsoRim reference DLL unavailable; real-mod checks skipped (synthetic checks ran).");
        }
        finally { scopeField.SetValue(null, oldScope); }
        Console.WriteLine($"{checks} view-order/culling compatibility checks passed; no GPU or game started.");
    }
}
