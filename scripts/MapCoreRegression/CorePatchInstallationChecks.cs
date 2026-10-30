using System.Reflection;
using HarmonyLib;
using RimExodus.Maps;

internal static class CorePatchInstallationChecks
{
    internal static void Run()
    {
        var harmony = new Harmony("REF.Regression.CoreInstallation");
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        try
        {
            // Execute the production installation loop against the actual game/Harmony assemblies.
            // This used to submit the targetless MapPreview helper and fail before startup completed.
            typeof(MapModule).GetMethod("InstallCorePatches", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { harmony });
            var patched = harmony.GetPatchedMethods().ToArray();
            Check(patched.Contains(AccessTools.Method(typeof(Verse.MapGenerator), "GenerateMap")), "generation hooks installed");
            Check(patched.Contains(AccessTools.Method(typeof(Verse.Map), "MapPreTick")), "tick hooks installed");
            Check(patched.Contains(AccessTools.Method(typeof(Verse.Game), "DeinitAndRemoveMap")), "cleanup hook installed");
            var callbacks = patched.Select(Harmony.GetPatchInfo).SelectMany(p =>
                p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers))
                .Where(p => p.owner == harmony.Id).Select(p => p.PatchMethod).ToArray();
            Check(!callbacks.Any(m => m.DeclaringType?.Name == "MapPreviewGenerationPatch"), "manual preview hook excluded from automatic installation");
            Check(!callbacks.Any(m => m.DeclaringType!.GetCustomAttributes(false)
                .Any(a => a.GetType().Name == "CombatPatchAttribute")), "combat remains opt-in");
            Console.WriteLine($"{checks} core installation checks passed; Harmony bound {patched.Length} actual game methods. No game launched.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}
