using Verse;

namespace RimExodus.Maps
{
    public sealed class FrameworkMod : Mod
    {
        public FrameworkMod(ModContentPack content) : base(content)
        {
            MapModule.TryInstall();
        }
    }
}
