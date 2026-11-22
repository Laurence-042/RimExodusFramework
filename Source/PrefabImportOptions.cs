using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimExodus.Maps
{
    public enum MissingPrefabDef
    {
        Error,
        Skip,
        Replace
    }

    /// <summary>Optional recovery rules for importing a region template.</summary>
    public sealed class PrefabImportOptions
    {
        public bool SkipMissingThings;
        public bool SkipMissingFilterDefs;
        public bool SkipThingsOutOfBounds;
        public bool SkipMissingTerrainColors;
        public MissingPrefabDef MissingStuff;
        public ThingDef DefaultStuff;
        public bool UseVanillaDefaultStuff;
        public MissingPrefabDef MissingTerrain;
        public TerrainDef DefaultTerrain;
        public MissingPrefabDef MissingFoundation;
        public TerrainDef DefaultFoundation;
        public MissingPrefabDef MissingRoof;
        public RoofDef DefaultRoof;
    }

    internal sealed class PrefabImportChange
    {
        internal readonly string layer;
        internal readonly string original;
        internal readonly string replacement;
        internal readonly string location;

        internal PrefabImportChange(string layer, string original, string replacement, string location)
        {
            this.layer = layer;
            this.original = original;
            this.replacement = replacement;
            this.location = location;
        }

        internal static void Warn(MapPrefabDocument doc, List<PrefabImportChange> changes)
        {
            if (changes.Count == 0) return;
            var details = changes.GroupBy(change => (change.layer, change.original, change.replacement))
                .Select(group =>
                {
                    var (layer, original, replacement) = group.Key;
                    string action = replacement == null ? "skipped" : $"replaced with '{replacement}'";
                    string positions = string.Join(", ", group.Select(change => change.location).Distinct());
                    return $"  {layer} '{original}' {action} ({group.Count()}) at {positions}";
                });
            Log.Warning($"[REF] Prefab import '{doc.ImportSource}' adjusted {changes.Count} entries.\n"
                + string.Join("\n", details));
        }
    }
}
