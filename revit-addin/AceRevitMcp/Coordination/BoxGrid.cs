using System;
using System.Collections.Generic;

namespace AceRevitMcp.Coordination
{
    /// <summary>
    /// A uniform 3D grid of axis-aligned boxes (plain numbers, no Revit types) for fast "which boxes touch this box"
    /// queries. The clash test fills one grid per model and test with the B elements' boxes (in host coordinates),
    /// then asks it for candidates of every A solid: one pass over each model instead of one collector per A solid.
    /// Very large boxes (slabs, long walls) are kept in a separate list that every query checks, so they don't fill
    /// thousands of cells.
    /// </summary>
    internal sealed class BoxGrid
    {
        private readonly double _cell;
        private readonly int _maxCellsPerBox;
        private readonly List<double[]> _boxes = new List<double[]>();   // minX, minY, minZ, maxX, maxY, maxZ
        private readonly Dictionary<(int, int, int), List<int>> _cells = new Dictionary<(int, int, int), List<int>>();
        private readonly List<int> _large = new List<int>();
        private int[] _seen = Array.Empty<int>();
        private int _stamp;

        /// <param name="cell">Cell size in model units (feet in Revit).</param>
        /// <param name="maxCellsPerBox">Boxes spanning more cells than this go to the list every query checks.</param>
        public BoxGrid(double cell, int maxCellsPerBox = 512)
        {
            if (cell <= 0) throw new ArgumentOutOfRangeException(nameof(cell));
            _cell = cell;
            _maxCellsPerBox = maxCellsPerBox;
        }

        public int Count => _boxes.Count;

        /// <summary>Adds a box and returns its index (the order of adding).</summary>
        public int Add(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            var i = _boxes.Count;
            _boxes.Add(new[] { minX, minY, minZ, maxX, maxY, maxZ });
            var (x0, y0, z0) = CellOf(minX, minY, minZ);
            var (x1, y1, z1) = CellOf(maxX, maxY, maxZ);
            var cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (cells > _maxCellsPerBox) { _large.Add(i); return i; }
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                    for (var z = z0; z <= z1; z++)
                    {
                        if (!_cells.TryGetValue((x, y, z), out var list)) _cells[(x, y, z)] = list = new List<int>();
                        list.Add(i);
                    }
            return i;
        }

        /// <summary>Indexes of the boxes that intersect (or touch) the given box, each once, in no particular order.</summary>
        public List<int> Query(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            var result = new List<int>();
            if (_seen.Length < _boxes.Count) _seen = new int[Math.Max(_boxes.Count, _seen.Length * 2)];
            if (++_stamp == int.MaxValue) { Array.Clear(_seen, 0, _seen.Length); _stamp = 1; }

            void Consider(int i)
            {
                if (_seen[i] == _stamp) return;
                _seen[i] = _stamp;
                var b = _boxes[i];
                if (b[0] <= maxX && b[3] >= minX && b[1] <= maxY && b[4] >= minY && b[2] <= maxZ && b[5] >= minZ) result.Add(i);
            }

            foreach (var i in _large) Consider(i);
            var (x0, y0, z0) = CellOf(minX, minY, minZ);
            var (x1, y1, z1) = CellOf(maxX, maxY, maxZ);
            var cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (cells > _cells.Count)
            {
                // A query box larger than the filled part of the grid: walking the filled cells is cheaper.
                foreach (var kv in _cells)
                {
                    var (x, y, z) = kv.Key;
                    if (x < x0 || x > x1 || y < y0 || y > y1 || z < z0 || z > z1) continue;
                    foreach (var i in kv.Value) Consider(i);
                }
                return result;
            }
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                    for (var z = z0; z <= z1; z++)
                        if (_cells.TryGetValue((x, y, z), out var list))
                            foreach (var i in list) Consider(i);
            return result;
        }

        private (int, int, int) CellOf(double x, double y, double z) =>
            ((int)Math.Floor(x / _cell), (int)Math.Floor(y / _cell), (int)Math.Floor(z / _cell));
    }
}
