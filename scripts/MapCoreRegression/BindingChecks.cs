using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

internal static class BindingChecks
{
    internal static void Run(string game)
    {
        int count = 0;
        void Check(bool ok, string label) { count++; if (!ok) throw new Exception("Binding: " + label); }
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (var entry in new[] {
            (typeof(Verse.Map), "MapPreTick"), (typeof(Verse.Map), "MapPostTick"), (typeof(Verse.Map), "MapUpdate"),
            (typeof(Verse.Game), "DeinitAndRemoveMap"), (typeof(Verse.Thing), "DoTick"),
            (typeof(Verse.MapGenerator), "GenerateMap") })
            Check(entry.Item1.GetMethods(flags).Count(m => m.Name == entry.Item2) == 1, entry.ToString());
        Check(typeof(Verse.TickManager).GetMethod("RegisterAllTickabilityFor", flags)!.GetParameters()[0].Name == "t", "tick registration argument");
        Check(typeof(Verse.CameraDriver).GetProperty("CurrentViewRect", flags)?.GetMethod != null, "view rect getter");
        Check(typeof(Waylith.Rendering.DrawItemFactory).GetMethods(flags).Single(m => m.Name == "TryMake")
            .GetParameters().Any(p => p.Name == "queueOrder" && p.ParameterType == typeof(int)), "draw ordering argument");
        Check(typeof(Waylith.Rendering.FinalDrawItem).GetField("<SortKey>k__BackingField", flags) != null, "sort key storage");
        Check(typeof(Waylith.Rendering.MeshBatchRenderer).GetMethods(flags).Single(m => m.Name == "RecordPass")
            .GetParameters().Any(p => p.Name == "passId" && p.ParameterType == typeof(byte)), "pass argument");
        foreach (string name in new[] { "DrawBatch", "DrawBatchInstanced" })
        {
            var methods = typeof(Waylith.Rendering.MeshBatchRenderer).GetMethods(flags).Where(m => m.Name == name
                && m.GetParameters().Any(p => p.ParameterType == typeof(Waylith.Rendering.FinalDrawItem))).ToArray();
            Check(methods.Length > 0, name + " exists");
            foreach (var method in methods)
            {
                Check(method.GetParameters().Any(p => p.Name == "draw" && p.ParameterType == typeof(Waylith.Rendering.FinalDrawItem)), name + " item");
                Check(method.GetParameters().Any(p => p.Name == "frameContext" && p.ParameterType.IsByRef), name + " frame: " + method);
            }
        }
        Assembly host = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "RimWorld.dll"));
        MethodInfo capture = host.GetType("RimWorldWaylith.Rendering.MapRenderFeature", true)!.GetMethod("CaptureFrame", flags)!;
        Check(CountFieldReads(capture, typeof(Verse.MapGenerator).GetField("mapBeingGenerated")!) == 1, "one generating-map IL gate");
        Check(!typeof(RimExodus.Maps.MapTransform).Assembly.GetReferencedAssemblies().Any(a => a.Name == "DeltaV" || a.Name == "RimExodus"), "no consumer dependency");
        Console.WriteLine($"{count} actual 1.7 assembly binding checks passed; Harmony was not installed or executed.");
    }

    private static int CountFieldReads(MethodInfo method, FieldInfo field)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => unchecked((ushort)o.Value));
        byte[] bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        int count = 0;
        for (int i = 0; i < bytes.Length;)
        {
            ushort code = bytes[i++];
            if (code == 0xfe) code = (ushort)(0xfe00 | bytes[i++]);
            OpCode op = opcodes[code];
            if (op == OpCodes.Ldsfld && method.Module.ResolveField(BitConverter.ToInt32(bytes, i)) == field) count++;
            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, i),
                _ => 4
            };
        }
        return count;
    }
}
