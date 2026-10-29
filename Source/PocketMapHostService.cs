using System;
using RimWorld.Planet;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>定制口袋地图对公共 sourceMap 宿主的最小依赖面。</summary>
    public interface IPocketMapHostProvider
    {
        Map GetOrCreateHostMap();
    }

    /// <summary>
    /// 无 tile 公共宿主的可抽取实现。宿主仅承载原版 PocketMap sourceMap 契约，不是坐标锚。
    /// </summary>
    public sealed class PocketMapHostService : IPocketMapHostProvider
    {
        private readonly Func<PocketMapParent, bool> isHost;
        private readonly Func<PocketMapParent> createHost;
        private readonly MapGeneratorDef generator;
        private readonly IntVec3 size;
        private Map cached;

        public Map Current => cached;

        public PocketMapHostService(Func<PocketMapParent, bool> isHost,
            Func<PocketMapParent> createHost, MapGeneratorDef generator, IntVec3 size)
        {
            this.isHost = isHost ?? throw new ArgumentNullException(nameof(isHost));
            this.createHost = createHost ?? throw new ArgumentNullException(nameof(createHost));
            this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
            this.size = size;
        }

        public Map GetOrCreateHostMap()
        {
            if (cached != null && !cached.Disposed && cached.Parent is PocketMapParent current && isHost(current)
                && Find.Maps.Contains(cached)) return cached;

            foreach (PocketMapParent parent in Find.World.pocketMaps)
            {
                if (isHost(parent) && parent.HasMap)
                {
                    cached = parent.Map;
                    return cached;
                }
            }

            PocketMapParent host = createHost();
            cached = PocketMaps.Create(size, host, generator, null);
            return cached;
        }

        public void Reset()
        {
            cached = null;
        }
    }
}
