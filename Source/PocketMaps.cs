using System;
using RimWorld.Planet;
using Verse;

namespace RimExodus.Maps
{
    public static class PocketMaps
    {
        public static Map Create(IntVec3 size, PocketMapParent parent, MapGeneratorDef generator, Map sourceMap)
        {
            if (parent == null || parent.Destroyed || parent.HasMap) throw new ArgumentException("A new pocket parent is required.");
            if (sourceMap?.Disposed == true) throw new ArgumentException("Pocket source map is disposed.");
            parent.Tile = PlanetTile.Invalid;
            parent.sourceMap = sourceMap;
            parent.mapGenerator = generator;
            try
            {
                Map map = MapGenerator.GenerateMap(size, parent, generator, null, null, isPocketMap: true);
                Register(parent, map);
                return map;
            }
            catch
            {
                if (parent.HasMap) Current.Game.DeinitAndRemoveMap(parent.Map, false);
                Find.World.pocketMaps.Remove(parent);
                throw;
            }
        }
        public static void Register(PocketMapParent parent, Map map)
        {
            if (map == null || map.Disposed || map.Parent != parent || parent.Tile.Valid)
                throw new InvalidOperationException("Pocket map registration has an invalid map, parent or tile.");
            if (!Find.World.pocketMaps.Contains(parent)) Find.World.pocketMaps.Add(parent);
        }
    }
}
