using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimExodus.Maps
{
    public interface IRegionSelection
    {
        IEnumerable<IntVec3> Cells(Map map);
    }
    public sealed class CellSelection : IRegionSelection
    {
        private readonly IntVec3[] cells;
        public CellSelection(IEnumerable<IntVec3> cells) { this.cells = cells.ToArray(); }
        public IEnumerable<IntVec3> Cells(Map map) => cells;
    }
    public sealed class AreaSelection : IRegionSelection
    {
        private readonly Func<Map, Area> area;
        public AreaSelection(Func<Map, Area> area) { this.area = area ?? throw new ArgumentNullException(nameof(area)); }
        public IEnumerable<IntVec3> Cells(Map map) => area(map).ActiveCells;
        public static AreaSelection Home => new AreaSelection(map => map.areaManager.Home);
    }
    public sealed class WholeMapSelection : IRegionSelection
    {
        public IEnumerable<IntVec3> Cells(Map map) => map.AllCells;
    }
}
