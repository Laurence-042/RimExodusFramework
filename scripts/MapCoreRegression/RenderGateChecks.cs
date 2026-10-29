using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;
using Verse;

internal static class RenderGateChecks
{
    internal static void Run(string game)
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var commonType = typeof(RimExodus.Maps.MapModule).Assembly.GetType("RimExodus.Maps.GeneratingRenderGatePatch", true)!;
        var common = commonType.GetMethod("Transpiler", flags)!;
#if RIMEXODUS_COMPOSITION
        var exodus = typeof(RimExodus.SeamlessWaylithMapRenderCompat).GetMethod("Transpiler", flags)!;
#endif
        var original = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "RimWorld.dll"))
            .GetType("RimWorldWaylith.Rendering.MapRenderFeature", true)!.GetMethod("CaptureFrame")!;
        List<CodeInstruction> Original() => PatchProcessor.GetOriginalInstructions(original);
        List<CodeInstruction> Apply(MethodInfo method, List<CodeInstruction> il)
            => ((IEnumerable<CodeInstruction>)method.Invoke(null, new object[] { il })!).ToList();
        bool Calls(CodeInstruction code, Type type) => code.opcode == OpCodes.Call
            && code.operand is MethodInfo m && m.DeclaringType == type;
#if RIMEXODUS_COMPOSITION
        void CheckChain(List<CodeInstruction> il, string order)
        {
            int foreign = il.FindIndex(c => Calls(c, exodus.DeclaringType!));
            Check(foreign >= 0, order + ": preserve RimExodus gate");
            Check(Calls(il[foreign + 1], commonType), order + ": filter the existing result");
            Check(il.Count(c => Calls(c, commonType)) == 1, order + ": one common filter");
        }
        // Link the actual sibling source read-only, rather than mocking its strict one-match transpiler.
        CheckChain(Apply(common, Apply(exodus, Original())), "RimExodus then common");
        CheckChain(Apply(exodus, Apply(common, Original())), "common then RimExodus");
#endif
        var alone = Apply(common, Original());
        var field = typeof(MapGenerator).GetField(nameof(MapGenerator.mapBeingGenerated))!;
        int load = alone.FindIndex(c => c.opcode == OpCodes.Ldsfld && Equals(c.operand, field));
        Check(load >= 0 && Calls(alone[load + 1], commonType), "standalone preserves the vanilla field read");
#if RIMEXODUS_COMPOSITION
        Check(Apply(exodus, Original()).Count(c => Calls(c, exodus.DeclaringType!)) == 1, "RimExodus alone unchanged");
#endif
        foreach (var invalid in new[] { new List<CodeInstruction> { new(OpCodes.Nop) },
            new List<CodeInstruction> { new(OpCodes.Ldsfld, field), new(OpCodes.Ldsfld, field) } })
        {
            bool rejected = false;
            try { Apply(common, invalid); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "unknown or ambiguous IL must still be rejected");
        }
        var failure = typeof(RimExodus.Maps.MapModule).GetField("installationFailure", flags)!;
        object previousFailure = failure.GetValue(null);
        try
        {
            failure.SetValue(null, new InvalidOperationException("Injected failed-install state"));
            Check(!RimExodus.Maps.MapModule.TryInstall(), "failed installation is unavailable without throwing");
            Check(!RimExodus.Maps.MapModule.TryInstall(), "repeated query does not retry failed patches");
        }
        finally { failure.SetValue(null, previousFailure); }
        Console.WriteLine($"{checks} render-gate composition checks passed using actual game IL (sibling composition included when available). No game launched.");
    }
}

// Only satisfy the source-linked file's unused runtime members. Tests invoke its real Transpiler, not Register/Gate.
namespace RimExodus
{
    internal static class SeamlessTileRenderer { internal static bool IsWaylithRuntime => true; }
    internal static class IncrementalMapGenerator { internal static bool IsGenerating(Map map) => false; }
}
