using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimExodus.Maps
{
    internal static class ArchiveMapCleanup
    {
        private static readonly FieldInfo Sections = AccessTools.Field(typeof(MapDrawer), "sections");
        private static readonly FieldInfo Global = AccessTools.Field(typeof(MapDrawer), "global");

        internal static void Dispose(Map map)
        {
            if (map.Disposed) return;
            MapDrawer drawer = map.mapDrawer;
            if (drawer == null) { map.Dispose(); return; }
            var sections = (Section[,])Sections.GetValue(drawer);
            var global = (List<MapDrawLayer>)Global.GetValue(drawer);
            if (sections != null && global != null && sections.Cast<Section>().All(section => section != null))
            {
                map.Dispose();
                return;
            }

            // MapDrawer.Dispose assumes every section and global layer was initialized.
            // A failed load may have none, or only some. Keep native Map disposal for
            // all other resources, then release only the renderer objects that exist.
            map.mapDrawer = null;
            try { map.Dispose(); }
            finally
            {
                map.mapDrawer = drawer;
                if (sections != null)
                    foreach (Section section in sections) section?.Dispose();
                Sections.SetValue(drawer, null);
                if (global != null)
                    foreach (MapDrawLayer layer in global) layer?.Dispose();
            }
        }
    }
}
