using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

// Optional installed-assembly check. Only the list of component types is a fixture:
// the real SmashTools PreCache/ClearMap and generic dictionaries execute unchanged.
static class SmashArchiveChecks
{
    static Type cache;
    static Type detachedFixture;
    static MethodInfo preCache, getDetached;
    static bool SkipTypeScan(ref List<Type> ___PriorityComponentTypes, ref List<Type> ___DetachedComponentTypes)
    {
        ___PriorityComponentTypes = new List<Type> { typeof(FixtureMapComponent) };
        ___DetachedComponentTypes = new List<Type> { detachedFixture };
        return false;
    }

    internal static void Run(string path)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(path));
        cache = assembly.GetType("SmashTools.ComponentCache", true);
        var detached = assembly.GetType("SmashTools.DetachedMapComponent", true);
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("REF.ArchiveDetachedFixture"),
            AssemblyBuilderAccess.Run).DefineDynamicModule("Fixture");
        var builder = module.DefineType("ArchiveDetachedFixture", TypeAttributes.Public, detached);
        var ctor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(Map) });
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, AccessTools.Constructor(detached, new[] { typeof(Map) })); il.Emit(OpCodes.Ret);
        Type fixture = builder.CreateType();
        detachedFixture = fixture;
        var harmony = new Harmony("REF.Regression.SmashArchive");
        harmony.Patch(cache.TypeInitializer, prefix: new HarmonyMethod(typeof(SmashArchiveChecks), nameof(SkipTypeScan)));
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(cache.TypeHandle);
        preCache = AccessTools.Method(cache, "PreCache", new[] { typeof(Map) });
        var detachedCache = assembly.GetType("SmashTools.DetachedMapComponentCache`1", true).MakeGenericType(fixture);
        getDetached = AccessTools.Method(detachedCache, "GetComponent");
        harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.AddMap)), postfix: new HarmonyMethod(preCache));
        harmony.Patch(AccessTools.Method(typeof(Map), nameof(Map.FinalizeLoading)),
            prefix: new HarmonyMethod(AccessTools.Method(cache, "PreCacheInst")) { priority = Priority.First });
        try
        {
            ArchiveContextChecks.Run();
            Console.WriteLine("Installed SmashTools cache checks passed: source removal, repeated same-ID restore, failed-finalize rollback, fresh ordinary/detached components.");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    public sealed class FixtureMapComponent : MapComponent
    {
        public FixtureMapComponent(Map map) : base(map) { }
    }

    internal static void Seed(Map map)
    {
        if (cache == null) return;
        preCache.Invoke(null, new object[] { map });
        map.components.Add(new FixtureMapComponent(map));
        _ = Ordinary(map);
    }

    static object Ordinary(Map map) => cache.GetMethod("GetCachedMapComponent").MakeGenericMethod(typeof(FixtureMapComponent))
        .Invoke(null, new object[] { map });

    internal static void AssertCache(Map map, bool present)
    {
        if (cache == null) return;
        int count = (int)cache.GetMethod("DetachedComponentCount").Invoke(null, null);
        if (count != (present ? 1 : 0)) throw new Exception("Unexpected detached cache count: " + count);
        if (!present)
        {
            if ((int)cache.GetMethod("PriorityComponentCount").Invoke(null, null) != 0)
                throw new Exception("Ordinary component cache retained unloaded map");
            return;
        }
        object component = getDetached.Invoke(null, new object[] { map });
        if (!ReferenceEquals(AccessTools.Field(component.GetType(), "map").GetValue(component), map))
            throw new Exception("Detached component refers to old map instance");
        var fresh = new FixtureMapComponent(map); map.components.Add(fresh);
        if (!ReferenceEquals(Ordinary(map), fresh)) throw new Exception("Ordinary cache returned stale component");
    }
}
