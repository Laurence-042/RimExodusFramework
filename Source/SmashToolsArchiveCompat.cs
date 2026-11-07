using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimExodus.Maps
{
    // Archive unloading intentionally bypasses MapDeiniter.Deinit, where SmashTools
    // normally removes both ordinary and detached map-component caches.
    internal static class SmashToolsArchiveCompat
    {
        private static readonly MethodInfo clearMap = ResolveClearMap();

        private static MethodInfo ResolveClearMap()
        {
            try
            {
                Type type = AccessTools.TypeByName("SmashTools.ComponentCache");
                if (type == null) return null;
                MethodInfo method = AccessTools.DeclaredMethod(type, "ClearMap", new[] { typeof(Map) });
                if (method == null || !method.IsStatic || method.ReturnType != typeof(void))
                    throw new MissingMethodException("SmashTools.ComponentCache.ClearMap(Map)");
                return method;
            }
            catch (Exception error)
            {
                Log.Warning("[RimExodus.Maps] SmashTools archive cache compatibility unavailable: " + error);
                return null;
            }
        }

        internal static void ClearMap(Map map)
        {
            if (clearMap == null) return;
            try { clearMap.Invoke(null, new object[] { map }); }
            catch (Exception error)
            {
                Log.ErrorOnce("[RimExodus.Maps] SmashTools archived-map cache cleanup failed: " + error, 192738451);
            }
        }
    }
}
