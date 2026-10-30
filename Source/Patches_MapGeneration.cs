using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
namespace RimExodus.Maps
{
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    internal static class GenerationLeasePatch
    {
        [HarmonyPriority(Priority.First + 100)]
        private static void Prefix() => MapRuntime.Current?.Generation.Drain();
    }
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    internal static class SynchronousGenerationPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(out MapGenerationCoordinator __state)
        {
            __state = null;
            var coordinator = MapRuntime.Current?.Generation;
            coordinator?.EnterExternal();
            __state = coordinator;
        }
        private static Exception Finalizer(Exception __exception, MapGenerationCoordinator __state)
        { __state?.ExitExternal(); return __exception; }
    }

    internal static class MapPreviewGenerationPatch
    {
        private static bool registered, failed;
        internal static void Register(Harmony harmony)
        {
            if (registered || failed) return;
            try
            {
                var type = AccessTools.TypeByName("MapPreview.MapPreviewGenerator");
                if (type == null) return; // Not loaded yet: a later safe registration call may retry.
                var request = AccessTools.TypeByName("MapPreview.MapPreviewRequest");
                var result = AccessTools.TypeByName("MapPreview.MapPreviewResult");
                var method = request == null || result == null ? null
                    : AccessTools.Method(type, "GeneratePreview", new[] { request, result });
                if (method == null) throw new MissingMethodException("MapPreviewGenerator.GeneratePreview(request, result)");
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(MapPreviewGenerationPatch), nameof(Prefix)),
                    finalizer: new HarmonyMethod(typeof(MapPreviewGenerationPatch), nameof(Finalizer)));
                registered = true;
                Log.Message("[REF] Bound MapPreview generation coordination before preview pause/static preparation.");
            }
            catch (Exception error)
            {
                failed = true;
                Log.Warning("[REF] MapPreview generation coordination unavailable: " + error);
            }
        }
        private static void Prefix(out MapGenerationCoordinator __state)
        {
            __state = null;
            var coordinator = MapRuntime.Current?.Generation;
            coordinator?.EnterExternal();
            __state = coordinator;
        }
        private static Exception Finalizer(Exception __exception, MapGenerationCoordinator __state)
        { __state?.ExitExternal(); return __exception; }
    }
#if RIMWORLD_1_7
    [HarmonyPatch]
    internal static class GeneratingRenderGatePatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(
            AccessTools.TypeByName("RimWorldWaylith.Rendering.MapRenderFeature"), "CaptureFrame")
            ?? throw new MissingMethodException("MapRenderFeature.CaptureFrame");
        private static Map Gate(Map generating)
        {
            return generating != null && generating != Find.CurrentMap
                && MapRuntime.Current?.Generation.GeneratingMap == generating ? null : generating;
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            foreach (CodeInstruction code in instructions)
            {
                bool vanilla = code.opcode == OpCodes.Ldsfld && code.operand is FieldInfo field
                    && field.DeclaringType == typeof(MapGenerator) && field.Name == nameof(MapGenerator.mapBeingGenerated);
                bool exodus = code.opcode == OpCodes.Call && code.operand is MethodInfo method
                    && method.DeclaringType?.FullName == "RimExodus.SeamlessWaylithMapRenderCompat"
                    && method.Name == "MapBeingGeneratedForRenderGate" && method.IsStatic
                    && method.ReturnType == typeof(Map) && method.GetParameters().Length == 0;
                yield return code;
                if (vanilla || exodus)
                {
                    // Keep the original producer so RimExodus can still replace its field read if it runs later.
                    // If it already ran, consume its result: null must stay null for its own generation policy.
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GeneratingRenderGatePatch), nameof(Gate)));
                    count++;
                }
            }
            if (count != 1) throw new InvalidOperationException($"Expected one generating-map render gate; found {count}.");
        }
    }
#endif
}
