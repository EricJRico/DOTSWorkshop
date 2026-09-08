using Unity.Mathematics;

namespace Workshop
{
    /// <summary>Cell indexing, shared by the jobs so the hash and the scan cannot disagree.</summary>
    public static class BoidGrid
    {
        public static int CellIndex(float2 p, GridInfo g)
        {
            // Clamped into the padded interior, so a scan reaching g.Pad - 1 cells out from any
            // occupied cell stays inside the array without a per-candidate bounds test.
            var cx = math.clamp((int)((p.x - g.Min.x) * g.InvCell), g.Pad, g.Cols - 1 - g.Pad);
            var cy = math.clamp((int)((p.y - g.Min.y) * g.InvCell), g.Pad, g.Rows - 1 - g.Pad);
            return cy * g.Cols + cx;
        }
    }
}
