using Unity.Mathematics;

namespace Workshop
{
    /// <summary>Cell indexing, shared by the jobs so the hash and the scan cannot disagree.</summary>
    public static class BoidGrid
    {
        public static int CellIndex(float2 p, GridInfo g)
        {
            var cx = math.clamp((int)((p.x - g.Min.x) * g.InvCell), 1, g.Cols - 2);
            var cy = math.clamp((int)((p.y - g.Min.y) * g.InvCell), 1, g.Rows - 2);
            return cy * g.Cols + cx;
        }
    }
}
