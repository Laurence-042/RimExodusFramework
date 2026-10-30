using Verse;

namespace RimExodus.Maps
{
    public sealed class FrameworkMod : Mod
    {
        public FrameworkMod(ModContentPack content) : base(content)
        {
            bool installed = MapModule.TryInstall();
            Log.Message("[REF] FrameworkMod instantiated; map services " + (installed ? "ready." : "unavailable (see installation error)."));
        }
    }
}
