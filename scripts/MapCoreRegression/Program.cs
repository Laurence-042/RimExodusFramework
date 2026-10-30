using System.Reflection;
using System.Runtime.Loader;

string game = Environment.GetEnvironmentVariable("RIMWORLD_DIR") ?? @"D:\SteamLibrary\steamapps\common\RimWorld";
AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(game, name.Name + ".dll"))
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, name.Name + ".dll")) : null;
AssemblyLoadContext.Default.Resolving += (_, name) => name.Name == "0Harmony"
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Environment.GetEnvironmentVariable("HARMONY_PATH")
        ?? @"D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll") : null;
if (args.Length > 1 && args[0] == "api")
{
    foreach (string file in Directory.GetFiles(game, "*.dll").Where(f => Path.GetFileName(f).StartsWith("Brutal.") || Path.GetFileName(f) == "Waylith.dll" || Path.GetFileName(f) == "RimWorld.dll"))
    {
        try
        {
            Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
            foreach (Type type in assembly.GetTypes().Where(t => t.FullName.Contains(args[1])))
            {
                Console.WriteLine(type.FullName);
                foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                    if (args.Length < 3 || member.Name.Contains(args[2])) Console.WriteLine("  " + member);
            }
        }
        catch (BadImageFormatException) { }
        catch (ReflectionTypeLoadException ex) { Console.WriteLine(ex.LoaderExceptions.First().Message); }
    }
    return;
}
Checks.Run();
BindingChecks.Run(game);
CorePatchInstallationChecks.Run();
RenderGateChecks.Run(game);
DrawCaptureChecks.Run();

static class Checks
{
    public static void Run()
    {
        int checks = 0;
        void Assert(bool value, string label) { checks++; if (!value) throw new Exception(label); }
        foreach (int rate in new[] { 0, 1, 25, 50, 75, 99, 100 })
        {
            int count = Enumerable.Range(1, 10000).Count(n => RimExodus.Maps.MapTickRates.Allows(n, rate));
            Assert(count == rate * 100, "Exact rate: " + rate);
            foreach (int bucket in new[] { 1, 250, 2000 })
                Assert(Enumerable.Range(1, 100).Count(n => RimExodus.Maps.MapTickRates.Allows((n * bucket) / bucket, rate)) == rate,
                    "Bucket fairness: " + bucket);
        }
        foreach (float angle in new[] { 0f, 35f, 90f, 180f, 270f, 359f })
        {
            var matrix = UnityEngine.Matrix4x4.TRS(new UnityEngine.Vector3(12, 0, -31),
                UnityEngine.Quaternion.Euler(0, angle, 0), UnityEngine.Vector3.one);
            var transform = new RimExodus.Maps.MapTransform(matrix);
            var point = new UnityEngine.Vector3(-0.25f, 0, 17.5f);
            Assert((transform.ToLocal(transform.ToDisplay(point)) - point).sqrMagnitude < 0.0001f, "Coordinate roundtrip: " + angle);
            var view = new Verse.CellRect(-40, -30, 100, 80);
            var local = transform.LocalView(view);
            foreach (int x in new[] { view.minX, view.maxX })
                foreach (int z in new[] { view.minZ, view.maxZ })
                    Assert(local.Contains(transform.CellAt(new UnityEngine.Vector3(x, 0, z))), "Conservative rotated view: " + angle);
        }
        var identity = new RimExodus.Maps.MapTransform(UnityEngine.Matrix4x4.identity);
        Assert(identity.CellAt(new UnityEngine.Vector3(-0.1f, 0, -0.1f)).x == -1, "Negative cell floors");
        foreach (var bad in new[] { UnityEngine.Matrix4x4.zero,
            UnityEngine.Matrix4x4.Translate(new UnityEngine.Vector3(float.NaN, 0, 0)),
            new UnityEngine.Matrix4x4 { m00 = 1, m11 = 1, m22 = 1, m33 = 2 } })
        {
            bool rejected = false;
            try { _ = new RimExodus.Maps.MapTransform(bad); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Invalid matrix rejected");
        }
        Console.WriteLine($"{checks} checks passed. Pure scheduling/coordinate tests; no game or Scribe runtime started.");
    }
}
