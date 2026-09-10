using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Jobs;

namespace Workshop
{
    // Weiss et al. 2017, Algorithm 1, one job per line group. Equal-mass discs on XZ.
    // The player is a fixed disc of infinite mass (paper 4.7: obstacles).

    /// <summary>Lines 1-5: blend velocity toward the preferred velocity, predict x*.</summary>
    [BurstCompile]
    public struct PredictJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Position;
        [ReadOnly] public NativeArray<float3> Velocity;
        [WriteOnly] public NativeArray<float3> Predicted;
        public float3 Target;
        public float Speed;
        public float Blend;
        public float DeltaTime;

        public void Execute(int i)
        {
            var toTarget = Target - Position[i];
            toTarget.y = 0f;
            var preferred = math.lengthsq(toTarget) > 1e-8f
                ? math.normalize(toTarget) * Speed
                : float3.zero;
            var blended = (1f - Blend) * Velocity[i] + Blend * preferred;   // eq. 1
            Predicted[i] = Position[i] + blended * DeltaTime;
        }
    }

    /// <summary>Lines 6-8: bucket positions into a grid.</summary>
    [BurstCompile]
    public struct BuildGridJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Positions;
        public NeighbourGrid Grid;

        public void Execute()
        {
            Grid.Build(Positions);
        }
    }

    /// <summary>
    /// Lines 9-15 and 16-21: frictional contact (Weiss 4.2, Macklin 6.1), accumulated
    /// Jacobi style. Reads the array named Solve (x for stability, x* for the solver).
    /// </summary>
    [BurstCompile]
    public struct ContactJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Solve;      // positions being corrected
        [ReadOnly] public NativeArray<float3> Start;      // x^n, for the friction term
        [ReadOnly] public NeighbourGrid Grid;
        [WriteOnly] public NativeArray<float3> Delta;
        [WriteOnly] public NativeArray<int> Count;
        public float Diameter;
        public float Radius;
        public float3 PlayerPosition;
        public float PlayerRadius;
        public float StaticFriction;
        public float KineticFriction;
        public bool Friction;

        public void Execute(int i)
        {
            var xi = Solve[i];
            var sum = float3.zero;
            var n = 0;
            var cell = Grid.CellOf(xi);

            for (var dy = -1; dy <= 1; dy++)
            {
                var y = cell.y + dy;
                if (y < 0 || y >= Grid.Rows) continue;
                for (var dx = -1; dx <= 1; dx++)
                {
                    var x = cell.x + dx;
                    if (x < 0 || x >= Grid.Cols) continue;
                    var c = y * Grid.Cols + x;
                    var end = Grid.CellStart[c + 1];
                    for (var k = Grid.CellStart[c]; k < end; k++)
                    {
                        var j = Grid.Sorted[k];
                        if (j == i) continue;
                        var xj = Solve[j];
                        var d = xi - xj;
                        d.y = 0f;
                        var distSq = math.lengthsq(d);
                        if (distSq >= Diameter * Diameter) continue;

                        var dist = math.sqrt(distSq);
                        var nrm = dist > 1e-6f ? d / dist : new float3(1f, 0f, 0f);
                        var C = dist - Diameter;                          // eq. 2, < 0
                        sum += -0.5f * C * nrm;                            // half each (equal mass)

                        if (Friction)
                        {
                            var rel = (xi - Start[i]) - (xj - Start[j]);   // Macklin eq. 23
                            var tangent = rel - math.dot(rel, nrm) * nrm;
                            var depth = -C;
                            var tLen = math.length(tangent);
                            if (tLen < StaticFriction * depth)
                                sum += -0.5f * tangent;                    // static: cancel it
                            else if (tLen > 1e-6f)
                                sum += -0.5f * tangent * math.min(KineticFriction * depth / tLen, 1f);
                        }
                        n++;
                    }
                }
            }

            // Player: infinite mass, agent takes the whole correction (paper 4.7).
            var toPlayer = xi - PlayerPosition;
            toPlayer.y = 0f;
            var reach = Radius + PlayerRadius;
            var dp = math.lengthsq(toPlayer);
            if (dp < reach * reach)
            {
                var dist = math.sqrt(dp);
                var nrm = dist > 1e-6f ? toPlayer / dist : new float3(1f, 0f, 0f);
                var C = dist - reach;
                sum += -C * nrm;
                if (Friction)
                {
                    var rel = xi - Start[i];
                    var tangent = rel - math.dot(rel, nrm) * nrm;
                    var depth = -C;
                    var tLen = math.length(tangent);
                    if (tLen < StaticFriction * depth) sum += -tangent;
                    else if (tLen > 1e-6f) sum += -tangent * math.min(KineticFriction * depth / tLen, 1f);
                }
                n++;
            }

            Delta[i] = sum;
            Count[i] = n;
        }
    }

    /// <summary>Apply the averaged delta (Macklin eq. 13) to one or two arrays. No walls: enemies
    /// spawn outside the view and converge on the player, so nothing pens them in.</summary>
    [BurstCompile]
    public struct ApplyJob : IJobParallelFor
    {
        public NativeArray<float3> Predicted;
        [NativeDisableParallelForRestriction] public NativeArray<float3> Position;
        [ReadOnly] public NativeArray<float3> Delta;
        [ReadOnly] public NativeArray<int> Count;
        public float Omega;
        public bool AlsoPosition;   // stability pass: lines 12-13 update both x and x*

        public void Execute(int i)
        {
            var n = Count[i];
            var delta = n > 0 ? Delta[i] * (Omega / n) : float3.zero;

            var p = Predicted[i] + delta;
            p.y = 0f;
            Predicted[i] = p;

            if (AlsoPosition)
            {
                var q = Position[i] + delta;
                q.y = 0f;
                Position[i] = q;
            }
        }
    }

    /// <summary>Line 23: v = (x* - x) / dt.</summary>
    [BurstCompile]
    public struct VelocityJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Position;
        [ReadOnly] public NativeArray<float3> Predicted;
        [WriteOnly] public NativeArray<float3> Velocity;
        public float DeltaTime;

        public void Execute(int i)
        {
            Velocity[i] = (Predicted[i] - Position[i]) / DeltaTime;
        }
    }

    /// <summary>
    /// Line 24: XSPH viscosity, eq. 3, with the Poly6 kernel. Standard XSPH form
    /// v_i += c * sum_j (v_j - v_i) W(|x_i - x_j|, h). Reads over the wide grid.
    /// </summary>
    [BurstCompile]
    public struct XsphJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> Predicted;
        [ReadOnly] public NativeArray<float3> Velocity;
        [ReadOnly] public NeighbourGrid Grid;
        [WriteOnly] public NativeArray<float3> Smoothed;
        public float H;
        public float C;

        public void Execute(int i)
        {
            var xi = Predicted[i];
            var vi = Velocity[i];
            var sum = float3.zero;
            var h2 = H * H;
            var poly6 = 315f / (64f * math.PI * math.pow(H, 9f));
            var cell = Grid.CellOf(xi);

            for (var dy = -1; dy <= 1; dy++)
            {
                var y = cell.y + dy;
                if (y < 0 || y >= Grid.Rows) continue;
                for (var dx = -1; dx <= 1; dx++)
                {
                    var x = cell.x + dx;
                    if (x < 0 || x >= Grid.Cols) continue;
                    var c = y * Grid.Cols + x;
                    var end = Grid.CellStart[c + 1];
                    for (var k = Grid.CellStart[c]; k < end; k++)
                    {
                        var j = Grid.Sorted[k];
                        if (j == i) continue;
                        var d = xi - Predicted[j];
                        d.y = 0f;
                        var r2 = math.lengthsq(d);
                        if (r2 >= h2) continue;
                        var q = h2 - r2;
                        var w = poly6 * q * q * q;
                        sum += (Velocity[j] - vi) * w;
                    }
                }
            }

            Smoothed[i] = vi + C * sum;
        }
    }

    /// <summary>Lines 25-26: clamp speed and acceleration, commit x = x*.</summary>
    [BurstCompile]
    public struct CommitJob : IJobParallelFor
    {
        public NativeArray<float3> Position;
        public NativeArray<float3> Velocity;          // previous velocity in, new out
        [ReadOnly] public NativeArray<float3> Smoothed;
        [ReadOnly] public NativeArray<float3> Predicted;
        public float MaxSpeed;
        public float MaxAcceleration;
        public float DeltaTime;

        public void Execute(int i)
        {
            var v = Smoothed[i];
            var prev = Velocity[i];

            var dv = v - prev;
            var maxDv = MaxAcceleration * DeltaTime;
            var dvLen = math.length(dv);
            if (dvLen > maxDv) v = prev + dv * (maxDv / dvLen);

            var speed = math.length(v);
            if (speed > MaxSpeed) v *= MaxSpeed / speed;

            Velocity[i] = v;
            Position[i] = Predicted[i];
        }
    }

    [BurstCompile]
    public struct WriteTransformsJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<float3> Position;

        public void Execute(int i, TransformAccess transform)
        {
            transform.position = Position[i];
        }
    }
}
