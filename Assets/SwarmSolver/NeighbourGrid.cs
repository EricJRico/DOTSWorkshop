using Unity.Collections;
using Unity.Mathematics;

namespace Workshop
{
    /// <summary>
    /// A fixed cell grid over the arena, rebuilt every frame, so each enemy only looks at
    /// the enemies in its own cell and the eight around it. Provided. Allocates once.
    /// </summary>
    public struct NeighbourGrid
    {
        public float2 Min;
        public float CellSize;
        public int Cols;
        public int Rows;

        // Counting-sort layout: enemies in cell c are Sorted[CellStart[c] .. CellStart[c + 1]).
        public NativeArray<int> CellStart;
        public NativeArray<int> Sorted;
        NativeArray<int> _cellCount;
        NativeArray<int> _cellOf;

        public NeighbourGrid(float2 min, float2 max, float cellSize, int capacity)
        {
            Min = min - cellSize * 2f;
            CellSize = cellSize;
            var size = (max - min) + cellSize * 4f;
            Cols = (int)math.ceil(size.x / cellSize);
            Rows = (int)math.ceil(size.y / cellSize);
            var cells = Cols * Rows;
            CellStart = new NativeArray<int>(cells + 1, Allocator.Persistent);
            Sorted = new NativeArray<int>(capacity, Allocator.Persistent);
            _cellCount = new NativeArray<int>(cells, Allocator.Persistent);
            _cellOf = new NativeArray<int>(capacity, Allocator.Persistent);
        }

        public void Dispose()
        {
            CellStart.Dispose();
            Sorted.Dispose();
            _cellCount.Dispose();
            _cellOf.Dispose();
        }

        public int2 CellOf(float3 p)
        {
            var cx = math.clamp((int)((p.x - Min.x) / CellSize), 0, Cols - 1);
            var cy = math.clamp((int)((p.z - Min.y) / CellSize), 0, Rows - 1);
            return new int2(cx, cy);
        }

        public void Build(NativeArray<float3> positions)
        {
            for (var c = 0; c < _cellCount.Length; c++) _cellCount[c] = 0;

            for (var i = 0; i < positions.Length; i++)
            {
                var cell = CellOf(positions[i]);
                var c = cell.y * Cols + cell.x;
                _cellOf[i] = c;
                _cellCount[c]++;
            }

            var running = 0;
            for (var c = 0; c < _cellCount.Length; c++)
            {
                CellStart[c] = running;
                running += _cellCount[c];
            }
            CellStart[_cellCount.Length] = running;

            for (var c = 0; c < _cellCount.Length; c++) _cellCount[c] = 0;
            for (var i = 0; i < positions.Length; i++)
            {
                var c = _cellOf[i];
                Sorted[CellStart[c] + _cellCount[c]] = i;
                _cellCount[c]++;
            }
        }
    }
}
