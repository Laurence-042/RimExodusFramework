#if RIMWORLD_1_7
using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>IsoRim's private frustum is in display space. Convert its query inputs, never
    /// its global planes or the real camera. No consumer/map-category dependency.</summary>
    internal static class IsoRimCullingCompat
    {
        private static bool installed;
        internal static void Register(Harmony harmony)
        {
            if (installed) return;
            try
            {
                if (!ModsConfig.IsActive("Peren.IsoRim17")) return;
                Bind(harmony, AccessTools.TypeByName("IsoRim.Cull"));
                installed = true;
                Log.Message("[RimExodus.Maps] IsoRim compat: bound BoxVisible and MeshVisible; native-view culling inputs projected to display space.");
            }
            catch (Exception error)
            {
                Log.Warning("[RimExodus.Maps] IsoRim culling compatibility unavailable: " + error);
            }
        }

        internal static void Bind(Harmony harmony, Type type)
        {
            var box = type == null ? null : AccessTools.DeclaredMethod(type, "BoxVisible", new[] { typeof(Vector3), typeof(Vector3) });
            var mesh = type == null ? null : AccessTools.DeclaredMethod(type, "MeshVisible", new[] { typeof(Mesh), typeof(Matrix4x4) });
            if (box == null || mesh == null || !box.IsStatic || !mesh.IsStatic
                || box.ReturnType != typeof(bool) || mesh.ReturnType != typeof(bool))
                throw new MissingMethodException("IsoRim.Cull.BoxVisible(Vector3,Vector3) / MeshVisible(Mesh,Matrix4x4)");
            var boxHook = AccessTools.Method(typeof(IsoRimCullingCompat), nameof(BeforeBox));
            var meshHook = AccessTools.Method(typeof(IsoRimCullingCompat), nameof(BeforeMesh));
            try
            {
                harmony.Patch(box, prefix: new HarmonyMethod(boxHook) { priority = Priority.Last });
                harmony.Patch(mesh, prefix: new HarmonyMethod(meshHook) { priority = Priority.Last });
            }
            catch
            {
                // Roll back only our hooks, including a hook installed before the second binding failed.
                foreach (var pair in new[] { (target: box, hook: boxHook), (target: mesh, hook: meshHook) })
                    try { harmony.Unpatch(pair.target, pair.hook); }
                    catch (Exception cleanup) { Log.Warning("[RimExodus.Maps] IsoRim culling rollback failed: " + cleanup); }
                throw;
            }
        }

        private static void BeforeBox(ref Vector3 __0, ref Vector3 __1)
        {
            var view = MapRenderPlan.CurrentNativeView;
            if (view == null || view.Transform.Matrix == Matrix4x4.identity) return;
            // BoxVisible handles sections and whole moving things. Its arguments are passed by value:
            // changing these locals cannot modify IsoRim's cached source-local boxes.
            view.Transform.BoundsToDisplay(__0, __1, out __0, out __1);
        }

        private static void BeforeMesh(ref Matrix4x4 __1)
        {
            var view = MapRenderPlan.CurrentNativeView;
            if (view == null || view.Transform.Matrix == Matrix4x4.identity) return;
            // MeshVisible evaluates planes itself and does not call BoxVisible. Project exactly once.
            // Only the visibility query receives this value; the eventual draw follows normal REF projection.
            __1 = view.Transform.Matrix * __1;
        }
    }
}
#endif
