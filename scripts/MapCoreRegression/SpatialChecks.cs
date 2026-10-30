using RimExodus.Maps;
using Verse;

internal static class SpatialChecks
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }
        Map Make() { var map = new Map(); map.info.Size = new IntVec3(8, 1, 8); return map; }
        var a = Make(); var b = Make(); var c = Make();
        var space = new MapCellSpace(a, new[] {
            new MapCellPlacement(a, IntVec3.Zero, cell => cell.x < 6),
            new MapCellPlacement(b, new IntVec3(6, 0, -2), cell => cell.x < 6) });
        Check(space.TryProject(b, new IntVec3(2, 0, 3), out var projected) && projected == new IntVec3(8, 0, 1), "Explicit frame translation");
        Check(space.TryResolve(projected, out var address) && address.Map == b && address.Cell == new IntVec3(2, 0, 3), "Owner round trip");
        Check(space.TryResolve(new IntVec3(6, 0, 0), out address) && address.Map == b, "Void in source rectangle does not own a cell");
        Check(!space.TryResolve(new IntVec3(-1, 0, 0), out _), "Unavailable remains unresolved");
        Check(!space.TryProject(c, IntVec3.Zero, out _), "Unregistered map cannot project");
        var grid = space.CaptureGrid(new CellRect(-2, -3, 17, 13));
        for (int i = 0; i < grid.Count; i++)
        {
            var point = grid.ReferenceCell(i);
            bool resolved = space.TryResolve(point, out var original);
            Check(grid.TryResolve(point, out address) == resolved && address.Map == original.Map && address.Cell == original.Cell, "Address snapshot matches live query");
        }
        bool rejected = false;
        try { space.CaptureGrid(new CellRect(0, 0, 1000, 1000), 100); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "Explicit memory budget enforced");
        var service = new MapSpatialQueries();
        Check(!service.TryCapture("test", a, out _), "No provider is inert");
        service.SetProvider("test", map => map == a ? space : null);
        Check(service.TryCapture("test", a, out _), "Provider available");
        service.SetProvider("test", null);
        Check(!service.TryCapture("test", a, out _), "Unregistration is immediate");
        var clipper = typeof(MapRuntime).Assembly.GetType("RimExodus.Maps.SurveillanceMapClippersCompat")!;
        var hook = HarmonyLib.AccessTools.Method(clipper, "AfterClippers");
        a.generatorDef = new MapGeneratorDef { disableMapClippers = true };
        var args = new object[] { a, true };
        hook.Invoke(null, args);
        Check(!(bool)args[1], "SS cannot cover explicit map background opt-out");
        a.generatorDef.disableMapClippers = false;
        args[1] = true; hook.Invoke(null, args);
        Check((bool)args[1], "SS ordinary-map clipper behavior retained");
        var ss = new HarmonyLib.Harmony("REF.Tests.SSClipper");
        var repair = new HarmonyLib.Harmony("REF.Tests.BackgroundRepair");
        var ssPostfix = HarmonyLib.AccessTools.Method(typeof(SpatialChecks), nameof(ForceClippers));
        var bind = HarmonyLib.AccessTools.Method(clipper, "Bind");
        bool missingRejected = false;
        try { bind.Invoke(null, new object[] { repair, ssPostfix }); }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidOperationException) { missingRejected = true; }
        Check(missingRejected && !repair.GetPatchedMethods().Any(), "Missing installed SS hook cannot silently bind repair");
        try
        {
            ss.Patch(HarmonyLib.AccessTools.PropertyGetter(typeof(Map), nameof(Map.DrawMapClippers)), postfix: new HarmonyLib.HarmonyMethod(ssPostfix));
            bind.Invoke(null, new object[] { repair, ssPostfix });
            a.generatorDef.disableMapClippers = true;
            Check(!a.DrawMapClippers, "Real Harmony ordering restores disabled clippers after SS override");
            a.generatorDef.disableMapClippers = false;
            Check(a.DrawMapClippers, "Real getter retains SS ordinary-map result");
        }
        finally { repair.UnpatchAll(repair.Id); ss.UnpatchAll(ss.Id); }
        Console.WriteLine($"{checks} cell-space and background contract checks passed.");
    }
    private static void ForceClippers(ref bool __result) { __result = true; }
}
