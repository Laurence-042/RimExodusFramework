using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace RimExodus.Maps
{
    public static class MapCombatCoordinates
    {
        public struct CombatLink
        {
            public Map host, target;
            public IntVec3 offset;
        }
        public static bool Enabled => MapCombatGeometry.Enabled;
        public static bool TryGetCombatLink(Map host, Map target, out CombatLink link)
        {
            link = default;
            if (host == null || target == null || host == target
                || !MapCombatGeometry.TryGetOffset(host, target, out var offset)) return false;
            link = new CombatLink { host = host, target = target, offset = offset };
            return true;
        }
        public static IntVec3 ToUnified(IntVec3 local, in CombatLink link) => local + link.offset;
        public static IntVec3 ToTargetLocal(IntVec3 unified, in CombatLink link) => unified - link.offset;
        public static bool HasActiveSeamNeighbors(Map map)
        {
            // Disabled maps never enumerate neighbors or call the consumer's geometry.
            return MapCombatGeometry.HasNeighbors(map);
        }
        internal sealed class CastEntry { internal Map Map; internal CombatLifetime Lifetime; }
        private static ConditionalWeakTable<Verb, CastEntry> CastTargets => MapRuntime.Current.Combat.CastTargets;
        public static void MarkCrossMapCast(Verb verb, Map target)
        {
            CastTargets.Remove(verb);
            var entry = new CastEntry { Map = target };
            entry.Lifetime = MapRuntime.Current.Combat.Track(VerbCaster(verb)?.Map, target, () => { entry.Map = null; });
            CastTargets.Add(verb, entry);
        }
        internal static Map TargetMapForCastGate(Verb verb)
        {
            var target = verb.CurrentTarget.Thing?.Map;
            var source = VerbCaster(verb)?.Map;
            return target != null && source != null && source != target
                && CastTargets.TryGetValue(verb, out var marked) && marked.Map == target
                && TryGetCombatLink(source, target, out _) ? source : target;
        }
        private static readonly AccessTools.FieldRef<Verb, Thing> Caster = AccessTools.FieldRefAccess<Verb, Thing>("caster");
        public static Thing VerbCaster(Verb verb) => verb == null ? null : Caster(verb);
    }
}
