using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimExodus.Maps
{
    public sealed class MapRenderOptions
    {
        public bool DynamicThings = true;
        public bool Effects = true;
        public bool Overlays;
        public bool EdgeClippers;
        public Func<MapDrawLayer, bool> LayerFilter;
        public Func<MapDrawCommand, bool> CommandFilter;
        public Action<Map, Action<MapDrawCommand>> CustomDraw;
        /// <summary>Optional convex source-local footprint for large planar overlays. Keep this list immutable.</summary>
        public IReadOnlyList<Vector2> OverlayFootprint;
    }

    /// <summary>Freezes the map, display transform and view used for both drawing and picking.</summary>
    public sealed class MapRenderPlan
    {
        /// <summary>True only inside the original frame update of a registered neighboring view.</summary>
        public static bool IsNativeView => MapRenderScope.NeighborActive;
        public readonly Map Source;
        public readonly MapTransform Transform;
        public readonly CellRect LocalView;
        private readonly MapRenderOptions options;
        internal IReadOnlyList<Vector2> OverlayFootprint => options.OverlayFootprint;
        [ThreadStatic] private static MapRenderPlan drawing;

        public MapRenderPlan(Map source, Matrix4x4 transform, CellRect displayView, MapRenderOptions options = null)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Transform = new MapTransform(transform);
            LocalView = Transform.LocalView(displayView).ClipInsideMap(source);
            this.options = options ?? new MapRenderOptions();
        }
        public bool TryPick(Vector3 point, out IntVec3 cell)
        {
            cell = Transform.CellAt(point);
            return !Source.Disposed && cell.InBounds(Source);
        }
        /// <summary>Selective capture path. Rendering.SetProvider uses original frame callbacks instead;
        /// only OverlayFootprint applies there, not the selective filters or CustomDraw.</summary>
        public void Collect(Action<MapDrawCommand> output)
        {
            if (Source.Disposed) return;
            if (drawing != null) throw new InvalidOperationException("Nested map rendering.");
            drawing = this;
            try
            {
                if (options.CustomDraw != null)
                {
                    options.CustomDraw(Source, command =>
                    {
                        command = command.Transformed(Transform.Matrix);
                        if (options.CommandFilter?.Invoke(command) != false) output(command);
                    });
                    return;
                }
                MapDrawCapture.Collect(() =>
                {
                    // Display maintenance only. Never invoke MapUpdate or change CurrentMap here.
                    Source.mapDrawer.MapMeshDrawerUpdate_First();
                    Source.mapDrawer.DrawMapMesh();
                    if (options.DynamicThings) Source.dynamicDrawManager.DrawDynamicThings();
                    if (options.EdgeClippers) MapEdgeClipDrawer.DrawClippers(Source);
                    if (options.Effects)
                    {
                        Source.gameConditionManager.GameConditionManagerDraw(Source);
                        Source.temporaryThingDrawer.Draw();
                        Source.flecks.FleckManagerDraw();
                    }
                    if (options.Overlays)
                    {
                        Source.designationManager.DrawDesignations();
                        Source.overlayDrawer.DrawAllOverlays();
                    }
                }, Transform.Matrix, command =>
                {
                    if (options.CommandFilter?.Invoke(command) != false) output(command);
                });
            }
            finally { drawing = null; }
        }

        [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect), MethodType.Getter)]
        private static class ViewPatch
        {
            private static bool Prefix(ref CellRect __result)
            {
                if (drawing == null) return true;
                __result = drawing.LocalView;
                return false;
            }
        }
        [HarmonyPatch]
        private static class LayerPatch
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(MapDrawLayer).Assembly.GetTypes()
                .Where(t => typeof(MapDrawLayer).IsAssignableFrom(t))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(m => m.Name == nameof(MapDrawLayer.DrawLayer) && !m.IsAbstract && m.GetMethodBody() != null);
            private static bool Prefix(MapDrawLayer __instance) => drawing?.options.LayerFilter?.Invoke(__instance) != false;
        }
    }
}
