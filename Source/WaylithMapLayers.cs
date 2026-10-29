#if RIMWORLD_1_7
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Brutal.VulkanApi;
using HarmonyLib;
using Waylith.Rendering;
using UnityStubs.Rendering;

namespace RimExodus.Maps
{
    internal static class WaylithMapLayers
    {
        private const int Marker = unchecked((int)0xD7000000);
        internal static int Encode(int group, bool foreground)
        {
            if (group < 1 || group >= 0x100000) throw new InvalidOperationException("Too many map draw groups.");
            return Marker | (foreground ? 0x100000 : 0) | group;
        }
        private static bool Tagged(int layer) => (layer & unchecked((int)0xFF000000)) == Marker;
        [ThreadStatic] private static bool recording;
        [ThreadStatic] private static int previous;

        // Store ordering on the immutable render-frame items, so main/render threads never share a mutable group table.
        [HarmonyPatch(typeof(DrawItemFactory), "TryMake")]
        private static class OrderingPatch
        {
            private static readonly FieldInfo SortKey = AccessTools.Field(typeof(FinalDrawItem), "<SortKey>k__BackingField");
            private static void Postfix(FinalDrawItem __result, int queueOrder)
            {
                if (__result == null || !Tagged(__result.Layer)) return;
                ulong group = (uint)(__result.Layer & 0xFFFFF);
                bool foreground = (__result.Layer & 0x100000) != 0;
                SortKey.SetValue(__result, new DrawSortKey(foreground ? ulong.MaxValue - 0x100000 + group : group, (ulong)queueOrder));
            }
        }
        [HarmonyPatch(typeof(MeshBatchRenderer), nameof(MeshBatchRenderer.RecordPass))]
        private static class PassPatch
        {
            private static void Prefix(byte passId, out (bool, int) __state)
            { __state = (recording, previous); recording = passId == LayerPassRouter.Resolve(0); previous = 0; }
            private static void Finalizer((bool, int) __state) { recording = __state.Item1; previous = __state.Item2; }
        }
        [HarmonyPatch]
        private static class DrawPatch
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(MeshBatchRenderer)
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(m => (m.Name == "DrawBatch" || m.Name == "DrawBatchInstanced")
                    && m.GetParameters().Any(p => p.ParameterType == typeof(FinalDrawItem)));
            private static unsafe void Prefix(in FrameContext frameContext, FinalDrawItem draw)
            {
                if (!recording) return;
                int group = Tagged(draw.Layer) ? draw.Layer : 0;
                if (previous == group) return;
                previous = group;
                var attachment = new VkClearAttachment { AspectMask = VkImageAspectFlags.DepthBit,
                    ClearValue = new VkClearDepthStencilValue { Depth = 0f, Stencil = 0 } };
                var rect = new VkClearRect { Rect = new VkRect2D(frameContext.Extent), BaseArrayLayer = 0, LayerCount = 1 };
                var command = frameContext.CommandBuffer;
                command.PInvoke.vkCmdClearAttachments.Invoke(command.Handle, 1, &attachment, 1, &rect);
            }
        }
    }
}
#endif
