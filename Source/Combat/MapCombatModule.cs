using System;
using HarmonyLib;

namespace RimExodus.Maps
{
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class CombatPatchAttribute : Attribute { }

    /// <summary>Install only when a consumer requests combat. Delta-V never installs these hooks.</summary>
    public static class MapCombatModule
    {
        private static bool installed;
        private static Exception failure;
        public static void Install()
        {
            if (installed) return;
            if (failure != null) throw new InvalidOperationException("Combat installation failed; restart after correcting the cause.", failure);
            MapModule.Install();
            var harmony = new Harmony("RimExodus.Maps.Combat");
            try
            {
                InstallCombatPatches(harmony);
                CombatExtendedBridge.Register(harmony);
                Verse.LongEventHandler.ExecuteWhenFinished(() => MapModule.ReportBindings(harmony));
                installed = true;
            }
            catch (Exception error)
            {
                failure = error;
                harmony.UnpatchAll(harmony.Id);
                throw;
            }
        }

        internal static void InstallCombatPatches(Harmony harmony)
        {
            foreach (var type in typeof(MapCombatModule).Assembly.GetTypes())
                if (Attribute.IsDefined(type, typeof(CombatPatchAttribute))
                    && Attribute.IsDefined(type, typeof(HarmonyPatch)))
                    harmony.CreateClassProcessor(type).Patch();
        }
    }
}
