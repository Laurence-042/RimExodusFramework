using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>
    /// LocalTargetInfo deliberately carries no Map for a cell-only target.  Targeter records the real
    /// neighbor map here while the mouse is resolved; firing, report and projectile layers then recover
    /// the same coordinate frame without inventing a fake Thing.  Entries are weakly owned by the verb or
    /// caster and are always revalidated against the active-neighbor graph before use.
    /// </summary>
    public static class MapCombatCellTargets
    {
        public sealed class Entry
        {
            public Map host;
            public Map target;
            public IntVec3 targetLocal;
            internal CombatLifetime Lifetime;
        }

        private static ConditionalWeakTable<Verb, Entry> ByVerb => MapRuntime.Current?.Combat.CellVerbs;
        private static ConditionalWeakTable<Thing, Entry> ByCaster => MapRuntime.Current?.Combat.CellCasters;
        [System.ThreadStatic] private static Verb firingVerb;

        public static void Register(Verb verb, Thing caster, Map host, Map target, IntVec3 targetLocal)
        {
            if (host == null || target == null || host == target
                || !MapCombatCoordinates.TryGetCombatLink(host, target, out var link)) return;

            var entry = new Entry
            {
                host = host,
                target = target,
                targetLocal = targetLocal
            };
            entry.Lifetime = MapRuntime.Current.Combat.Track(host, target, () => { entry.host = null; entry.target = null; });
            if (verb != null)
            {
                ByVerb.Remove(verb);
                ByVerb.Add(verb, entry);
            }
            if (caster != null)
            {
                ByCaster.Remove(caster);
                ByCaster.Add(caster, entry);
            }
        }

        public static void Clear(Verb verb, Thing caster)
        {
            if (verb != null) ByVerb?.Remove(verb);
            if (caster != null) ByCaster?.Remove(caster);
        }

        public static bool TryResolve(Verb verb, LocalTargetInfo target,
            out MapCombatCoordinates.CombatLink link, out IntVec3 unified)
        {
            link = default;
            unified = default;
            if (target.HasThing || verb == null) return false;
            var caster = MapCombatCoordinates.VerbCaster(verb);
            if (!MapCombatGeometry.IsEnabled(caster?.Map)) return false;
            Entry entry = null;
            if (!ByVerb.TryGetValue(verb, out entry) && caster != null)
                ByCaster.TryGetValue(caster, out entry);
            return TryResolveEntry(entry, caster?.Map, target.Cell, out link, out unified);
        }

        public static bool TryResolve(Thing caster, LocalTargetInfo target,
            out MapCombatCoordinates.CombatLink link, out IntVec3 unified)
        {
            link = default;
            unified = default;
            if (target.HasThing || !MapCombatGeometry.IsEnabled(caster?.Map) || !ByCaster.TryGetValue(caster, out var entry)) return false;
            return TryResolveEntry(entry, caster.Map, target.Cell, out link, out unified);
        }

        /// <summary>
        /// Single combat-level resolver for both LocalTargetInfo representations. Thing targets carry
        /// their map directly; cell targets recover it from the side table. Consumers that only need
        /// target identity and integer geometry should use this instead of branching independently.
        /// </summary>
        public static bool TryResolve(Thing caster, Verb verb, LocalTargetInfo target,
            out MapCombatCoordinates.CombatLink link, out IntVec3 unified)
        {
            link = default;
            unified = default;
            if (caster?.Map == null || !target.IsValid) return false;
            if (target.HasThing)
            {
                var targetMap = target.Thing?.MapHeld;
                if (targetMap == null || targetMap == caster.Map
                    || !MapCombatCoordinates.TryGetCombatLink(caster.Map, targetMap, out link)) return false;
                unified = target.Cell + link.offset;
                return true;
            }
            return TryResolve(verb, target, out link, out unified)
                || TryResolve(caster, target, out link, out unified);
        }

        public static Verb BeginFiring(Verb verb)
        {
            var previous = firingVerb;
            firingVerb = verb;
            return previous;
        }

        public static void EndFiring(Verb previous) => firingVerb = previous;

        public static bool TryResolveCurrent(LocalTargetInfo target,
            out MapCombatCoordinates.CombatLink link, out IntVec3 unified) =>
            TryResolve(firingVerb, target, out link, out unified);

        /// <summary>
        /// Common visual/stance projection for both target forms. Thing targets carry their Map; cell-only
        /// targets recover it from this side table. The result is always expressed in the caster's map
        /// coordinate frame and this method returns false for same-map targets.
        /// </summary>
        public static bool TryGetUnifiedTarget(Thing caster, Verb verb, LocalTargetInfo target,
            out Vector3 unified)
        {
            unified = default;
            if (caster?.Map == null || !target.IsValid) return false;
            if (!TryResolve(caster, verb, target, out var link, out var unifiedCell)) return false;
            if (target.HasThing)
            {
                var thing = target.Thing;
                unified = thing.TrueCenter() + new Vector3(link.offset.x, 0f, link.offset.z);
                return true;
            }
            unified = unifiedCell.ToVector3Shifted();
            return true;
        }

        private static bool TryResolveEntry(Entry entry, Map currentHost, IntVec3 cell,
            out MapCombatCoordinates.CombatLink link, out IntVec3 unified)
        {
            link = default;
            unified = default;
            if (entry?.host == null || entry.target == null || currentHost != entry.host
                || !MapCombatCoordinates.TryGetCombatLink(entry.host, entry.target, out link)) return false;

            // offset belongs to the live adjacency link, not to the targeting gesture. Wake/relink may
            // rebuild that link while the verb/caster weak entry survives, so never cache this derivative.
            var currentUnified = entry.targetLocal + link.offset;
            if (cell != entry.targetLocal && cell != currentUnified) return false;
            unified = currentUnified;
            return true;
        }
    }
}
