using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimExodus.Maps
{
    public sealed class MapGenerationCoordinator
    {
        private string owner;
        private Action drain;
        public Map GeneratingMap { get; private set; }
        public bool IsBusy => owner != null;
        public bool IsOwnedBy(string requestedOwner) => owner != null && owner == requestedOwner;
        public void Acquire(string requestedOwner, Action drainSynchronously)
        {
            if (owner != null || MapGenerator.mapBeingGenerated != null)
                throw new InvalidOperationException("Map generation static state is already leased.");
            if (drainSynchronously == null) throw new ArgumentNullException(nameof(drainSynchronously));
            owner = requestedOwner ?? throw new ArgumentNullException(nameof(requestedOwner));
            drain = drainSynchronously;
        }
        public void Bind(string requestedOwner, Map map)
        {
            if (owner != requestedOwner) throw new InvalidOperationException("Map generation owner mismatch.");
            GeneratingMap = map;
        }
        public void Release(string requestedOwner)
        {
            if (owner == null) return;
            if (owner != requestedOwner) throw new InvalidOperationException("Map generation owner mismatch.");
            owner = null; drain = null; GeneratingMap = null;
        }
        public void Drain()
        {
            if (owner == null) return;
            drain();
            if (owner != null) throw new InvalidOperationException("Map generation did not release its lease.");
        }
    }
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    internal static class GenerationLeasePatch
    {
        private static void Prefix() => MapRuntime.Current?.Generation.Drain();
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
