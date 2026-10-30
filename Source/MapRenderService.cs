using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>A consumer supplies visible source maps, never simulation or camera-update callbacks.</summary>
    public sealed class MapRenderService
    {
        private readonly Dictionary<string, Action<Map, List<MapRenderPlan>>> providers =
            new Dictionary<string, Action<Map, List<MapRenderPlan>>>();
        private readonly Dictionary<Map, MapRenderPlan> views = new Dictionary<Map, MapRenderPlan>();
        private readonly List<MapRenderPlan> scratch = new List<MapRenderPlan>();
        private Map host;
        private int frame = -1;
#if RIMWORLD_1_7
        private readonly Dictionary<Map, MapOverlayClipper> clippers = new Dictionary<Map, MapOverlayClipper>();
        internal Waylith.Rendering.MeshDrawCommand Clip(in Waylith.Rendering.MeshDrawCommand command, MapRenderPlan plan)
        {
            if (!clippers.TryGetValue(plan.Source, out var clipper))
                clippers.Add(plan.Source, clipper = new MapOverlayClipper());
            return clipper.Clip(command, plan);
        }
#endif
#if !RIMWORLD_1_7
        private Camera clearCamera;
        internal readonly MapOverlayClipper UnityClipper = new MapOverlayClipper();
        private CommandBuffer clearBuffer;
        private readonly List<Material> materials = new List<Material>();
        private int materialCursor;
        internal Material MaterialCopy(Material source)
        {
            if (materialCursor == materials.Count) materials.Add(new Material(source));
            var copy = materials[materialCursor++];
            copy.shader = source.shader;
            copy.CopyPropertiesFromMaterial(source);
            return copy;
        }
#endif
        /// <summary>Called before map updates each frame. Supply source-local to display plans;
        /// include the focused map with identity to clip its overlays too. Null unregisters the provider.
        /// Native callbacks ignore selective Collect options except OverlayFootprint.</summary>
        public void SetProvider(string owner, Action<Map, List<MapRenderPlan>> provider)
        {
            if (provider == null) providers.Remove(owner);
            else providers[owner] = provider;
        }
        internal void BeginFrame()
        {
            views.Clear();
#if !RIMWORLD_1_7
            materialCursor = 0;
            UnityClipper.BeginFrame();
#endif
            host = Find.CurrentMap;
            frame = Time.frameCount;
            if (host == null || !WorldRendererUtility.DrawingMap || LongEventHandler.ShouldWaitForEvent)
            { Clear(); return; }
#if RIMWORLD_1_7
            foreach (var clipper in clippers.Values) clipper.Prune();
#endif
            foreach (var provider in providers)
            {
                scratch.Clear();
                try
                {
                    provider.Value(host, scratch);
                    foreach (var plan in scratch)
                        if (plan != null && !plan.Source.Disposed)
                            views[plan.Source] = plan;
                }
                catch (Exception error)
                {
                    Log.ErrorOnce("[RimExodus.Maps] Render provider " + provider.Key + " failed: " + error,
                        provider.Key.GetHashCode() ^ 0x524546);
                }
            }
            scratch.Clear();
#if !RIMWORLD_1_7
            if (views.Count == 0) { ReleaseClear(); return; }
            if (clearCamera != Find.Camera)
            {
                ReleaseClear();
                clearCamera = Find.Camera;
                if (clearCamera != null)
                {
                    clearBuffer = new CommandBuffer { name = "REF native neighbor frame clear" };
                    clearBuffer.ClearRenderTarget(true, true, Color.clear);
                    clearCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, clearBuffer);
                }
            }
#endif
        }
        internal MapRenderPlan GetView(Map map)
        {
            if (frame != Time.frameCount || host != Find.CurrentMap || map == null || map.Disposed
                || MapRuntime.Current?.StoppedByPolicy(map) == true) return null;
            return views.TryGetValue(map, out var view) ? view : null;
        }
        internal void Forget(Map map)
        {
            views.Remove(map);
#if RIMWORLD_1_7
            if (clippers.TryGetValue(map, out var clipper)) { clipper.Dispose(); clippers.Remove(map); }
#endif
        }
        public void Clear()
        {
            views.Clear(); scratch.Clear(); host = null; frame = -1;
#if RIMWORLD_1_7
            foreach (var clipper in clippers.Values) clipper.Dispose();
            clippers.Clear();
#endif
#if !RIMWORLD_1_7
            ReleaseClear();
            UnityClipper.Dispose();
            foreach (var material in materials) UnityEngine.Object.Destroy(material);
            materials.Clear(); materialCursor = 0;
#endif
        }
#if !RIMWORLD_1_7
        private void ReleaseClear()
        {
            if (clearCamera != null && clearBuffer != null)
                clearCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, clearBuffer);
            clearBuffer?.Release(); clearBuffer = null; clearCamera = null;
        }
#endif
        [HarmonyPatch(typeof(Game), nameof(Game.UpdatePlay))]
        private static class FramePatch
        {
            private static void Prefix() => MapRuntime.Current?.Rendering.BeginFrame();
        }

        [HarmonyPatch(typeof(Verse.Current), nameof(Verse.Current.Game), MethodType.Setter)]
        private static class GameChangePatch
        {
            private static void Prefix() => MapRuntime.Current?.Rendering.Clear();
        }

        // The original MapUpdate runs ONCE, including third-party prefixes/postfixes and component updates.
        // No CameraDriver.Update replay, CurrentMap setter, extra tick, or separate component-update loop.
        [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
        private static class UpdatePatch
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Map __instance, bool __runOriginal, out MapRenderScope __state)
            {
                __state = null;
                if (!__runOriginal || MapRenderScope.Active) return;
                var view = MapRuntime.Current?.Rendering.GetView(__instance);
                if (view != null) __state = new MapRenderScope(view);
            }
            private static void Postfix(bool __runOriginal, MapRenderScope __state)
            {
                // MapInterface only calls these draw callbacks on the real focus map. Neighbors need one call.
                if (__runOriginal && __state != null && __state.IsNeighbor)
                    MapComponentUtility.MapComponentOnDraw(__state.Plan.Source);
            }
            private static Exception Finalizer(Exception __exception, MapRenderScope __state)
            {
                __state?.Dispose();
                return __exception;
            }
        }

        // Focused-map draw callbacks run later in MapInterface, outside MapUpdate. Apply the same footprint there.
        [HarmonyPatch(typeof(MapComponentUtility), nameof(MapComponentUtility.MapComponentOnDraw))]
        private static class ComponentDrawPatch
        {
            private static void Prefix(Map map, out MapRenderScope __state)
            {
                __state = null;
                if (MapRenderScope.Active) return;
                var view = MapRuntime.Current?.Rendering.GetView(map);
                if (view != null) __state = new MapRenderScope(view);
            }
            private static Exception Finalizer(Exception __exception, MapRenderScope __state)
            { __state?.Dispose(); return __exception; }
        }
    }

    /// <summary>Scoped query overrides. Never changes the actual game focus, Thing positions or simulation ownership.</summary>
    internal sealed class MapRenderScope : IDisposable
    {
        [ThreadStatic] private static MapRenderScope current;
        internal static bool Active => current != null;
        internal static MapRenderPlan Current => current?.Plan;
        internal static bool NeighborActive => current?.IsNeighbor == true;
        internal readonly MapRenderPlan Plan;
        internal readonly bool IsNeighbor;
        private readonly Color lightColor;
        private readonly MapShaderState shaders;
        private bool disposed;
        internal MapRenderScope(MapRenderPlan plan)
        {
            if (current != null) throw new InvalidOperationException("Nested native map rendering.");
            Plan = plan;
            IsNeighbor = plan.Source != Verse.Current.Game.CurrentMap;
            if (IsNeighbor)
            {
                lightColor = MatBases.LightOverlay.color;
                shaders = new MapShaderState();
            }
            current = this;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { shaders?.Dispose(); }
            finally
            {
                try { if (IsNeighbor) MatBases.LightOverlay.color = lightColor; }
                finally { current = null; }
            }
        }
        [HarmonyPatch(typeof(Find), nameof(Find.CurrentMap), MethodType.Getter)]
        private static class CurrentMapPatch
        {
            private static void Postfix(ref Map __result)
            { if (current != null) __result = current.Plan.Source; }
        }
        [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect), MethodType.Getter)]
        private static class ViewPatch
        {
            // Camera mods first compute the display-space view. Inside this scope the consumer
            // has already projected that view into Source; a later normal postfix must not undo it.
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ref CellRect __result)
            { if (current != null) __result = current.Plan.LocalView; }
        }
    }
}
