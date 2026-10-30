using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>SS unconditionally enables rectangular clippers. Respect the generator's explicit
    /// background contract whenever generatorDef.disableMapClippers is true, regardless of the
    /// map's creator or category. Otherwise this adapter leaves the getter result unchanged.</summary>
    internal static class SurveillanceMapClippersCompat
    {
        private static bool installed;
        internal static void Register(Harmony harmony)
        {
            if (installed) return;
            MethodInfo target = null;
            var hook = AccessTools.Method(typeof(SurveillanceMapClippersCompat), nameof(AfterClippers));
            try
            {
                if (!ModsConfig.IsActive("ferny.SurveillanceShift")) return;
                var type = AccessTools.TypeByName("SurveillanceShift.Map_DrawMapClippers_Patch");
                var ss = type == null ? null : AccessTools.DeclaredMethod(type, "Postfix", new[] { typeof(bool).MakeByRefType() });
                if (ss == null || ss.ReturnType != typeof(void)) throw new MissingMethodException("SS DrawMapClippers.Postfix(ref bool)");
                target = AccessTools.PropertyGetter(typeof(Map), nameof(Map.DrawMapClippers));
                Bind(harmony, ss);
                installed = true;
                Log.Message("[RimExodus.Maps] SS compat: bound DrawMapClippers; explicit generator background opt-out preserved.");
            }
            catch (Exception error)
            {
                if (target != null)
                    try { harmony.Unpatch(target, hook); }
                    catch (Exception cleanup) { Log.Warning("[RimExodus.Maps] SS clipper rollback failed: " + cleanup); }
                Log.Warning("[RimExodus.Maps] SS map clipper compatibility unavailable: " + error);
            }
        }
        internal static void Bind(Harmony harmony, MethodInfo ss)
        {
            var target = AccessTools.PropertyGetter(typeof(Map), nameof(Map.DrawMapClippers));
            var owners = Harmony.GetPatchInfo(target)?.Postfixes.Where(p => p.PatchMethod == ss).Select(p => p.owner).Distinct().ToArray();
            if (owners == null || owners.Length == 0) throw new InvalidOperationException("SS clipper postfix is not installed.");
            harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(SurveillanceMapClippersCompat), nameof(AfterClippers)))
                { priority = Priority.Last, after = owners });
        }
        private static void AfterClippers(Map __instance, ref bool __result)
        {
            if (__instance.generatorDef?.disableMapClippers == true) __result = false;
        }
    }
}
