using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace Workshop
{
    /// <summary>
    /// Owns the swarm state and runs one frame of it. No GameObjects, no Transforms: positions
    /// live only in native arrays and go straight to the GPU as instance data, because writing
    /// 50,000 Transforms is a cost the simulation should never pay.
    ///
    /// Shape of a frame, all Burst, all on worker threads:
    ///   Predict -> Bounds -> GridSetup -> Hash -> Scan -> Scatter -> Separate x N -> Finalize
    /// One grid build per frame, reused by every separation pass (Macklin's structure).
    /// </summary>
    public class BoidSolver
    {
        /// <summary>Row length of the cached neighbour list. MaxNeighbours is clamped to this.</summary>
        public const int MaxNeighbourStride = 64;

        public static readonly ProfilerMarker ScheduleMarker = new ProfilerMarker("Boid.Schedule");
        public static readonly ProfilerMarker CompleteMarker = new ProfilerMarker("Boid.Complete");

        readonly BoidSettings _settings;

        NativeArray<float2> _position;
        NativeArray<float2> _velocity;
        NativeArray<float2> _predicted;
        NativeArray<float2> _scratch;        // ping-pong target for the Jacobi passes
        NativeArray<float2> _sortPosition;
        NativeArray<float2> _sortPredicted;
        NativeArray<float2> _sortVelocity;
        NativeArray<float2> _contactNormal;
        NativeArray<int> _neighbourCount;
        NativeArray<int> _neighbours;
        NativeArray<float4> _instances;
        NativeArray<int> _cellOf;
        NativeArray<int> _cellCount;
        NativeArray<int> _offset;
        NativeArray<float4> _partialBounds;
        NativeArray<GridInfo> _info;
        NativeArray<int> _stats;

        int _count;
        int _slices;
        bool _allocated;

        public int Count => _count;
        public NativeArray<float2> Positions => _position;
        public NativeArray<float2> Velocities => _velocity;
        public NativeArray<float4> Instances => _instances;
        public GridInfo Grid => _info.IsCreated ? _info[0] : default;

        /// <summary>Pairs closer than the constraint the solver targets. The number to watch.</summary>
        public int OverlapPairs => _stats.IsCreated ? _stats[0] : 0;
        /// <summary>Pairs whose centres are closer than 2*Radius, i.e. bodies genuinely overlapping.</summary>
        public int BodyOverlapPairs => _stats.IsCreated ? _stats[10] : 0;
        /// <summary>Agents involved in at least one such pair.</summary>
        public int OverlapAgents => _stats.IsCreated ? _stats[1] : 0;
        /// <summary>Deepest penetration this frame, as a fraction of the solve diameter.</summary>
        public float WorstPenetration => _stats.IsCreated ? _stats[2] * 1e-6f : 0f;
        /// <summary>Mean penetration over the failing pairs, as a fraction of the solve diameter.</summary>
        public float MeanPenetration => _stats.IsCreated && _stats[0] > 0 ? _stats[3] * 1e-4f / _stats[0] : 0f;

        /// <summary>Penetration histogram: &lt;0.1%, 0.1-1%, 1-5%, 5-10%, 10-25%, &gt;25% of body diameter.</summary>
        public int PenetrationBand(int band) => _stats.IsCreated ? _stats[4 + band] : 0;

        public BoidSolver(BoidSettings settings)
        {
            _settings = settings;
        }

        public void Allocate(int count, float spawnMin, float spawnMax, int seed)
        {
            Release();
            _count = count;

            var maxCells = math.max(65536, count * _settings.CellsPerAgent);
            _slices = math.max(1, math.min(64, count / 2048));

            _position = new NativeArray<float2>(count, Allocator.Persistent);
            _velocity = new NativeArray<float2>(count, Allocator.Persistent);
            _predicted = new NativeArray<float2>(count, Allocator.Persistent);
            _scratch = new NativeArray<float2>(count, Allocator.Persistent);
            _sortPosition = new NativeArray<float2>(count, Allocator.Persistent);
            _sortPredicted = new NativeArray<float2>(count, Allocator.Persistent);
            _sortVelocity = new NativeArray<float2>(count, Allocator.Persistent);
            _contactNormal = new NativeArray<float2>(count, Allocator.Persistent);
            _neighbourCount = new NativeArray<int>(count, Allocator.Persistent);
            // Fixed stride, so MaxNeighbours can be retuned at runtime without reallocating -
            // and, more to the point, without GatherJob writing past the end of a shorter array.
            _neighbours = new NativeArray<int>(count * MaxNeighbourStride, Allocator.Persistent);
            _instances = new NativeArray<float4>(count, Allocator.Persistent);
            _cellOf = new NativeArray<int>(count, Allocator.Persistent);
            _cellCount = new NativeArray<int>(maxCells, Allocator.Persistent);
            _offset = new NativeArray<int>(maxCells + 1, Allocator.Persistent);
            _partialBounds = new NativeArray<float4>(_slices, Allocator.Persistent);
            _info = new NativeArray<GridInfo>(1, Allocator.Persistent);
            _stats = new NativeArray<int>(
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.ThreadIndexCount * OverlapJob.Stride,
                Allocator.Persistent);

            var rng = new Unity.Mathematics.Random((uint)seed | 1u);
            for (var i = 0; i < count; i++)
            {
                var angle = rng.NextFloat(0f, math.PI * 2f);
                var radius = math.lerp(spawnMin, spawnMax, math.sqrt(rng.NextFloat()));
                _position[i] = new float2(math.cos(angle), math.sin(angle)) * radius;
            }
            _allocated = true;
        }

        public JobHandle Schedule(float2 target, float deltaTime)
        {
            if (!_allocated || _count == 0) return default;
            var s = _settings;
            var batch = s.BatchSize;
            var stride = (_count + _slices - 1) / _slices;

            ScheduleMarker.Begin();

            var handle = default(JobHandle);
            var substeps = math.max(1, s.Substeps);
            var sdt = deltaTime / substeps;
            var reach = s.Radius + s.PlayerRadius;

            for (var sub = 0; sub < substeps; sub++)
            {
                handle = new SteerJob
                {
                    Position = _position, Velocity = _velocity, Predicted = _predicted,
                    Target = target, Speed = s.Speed, Blend = s.SteerBlend, DeltaTime = sdt
                }.Schedule(_count, batch, handle);

                handle = new BoundsJob
                {
                    Position = _predicted, Partial = _partialBounds, Stride = stride
                }.Schedule(_slices, 1, handle);

                handle = new GridSetupJob
                {
                    Partial = _partialBounds, Info = _info,
                    // Cell = the GATHER radius, not the solve radius: a 3x3 scan only reaches one
                    // cell out, so the skin has to be inside the cell or the gather misses it.
                    CellSize = s.CacheNeighbours
                        ? s.CollisionDiameter * (1f + s.GatherSkin)
                        : s.CollisionDiameter,
                    MaxCells = _cellCount.Length, Count = _count
                }.Schedule(handle);

                handle = new HashJob
                {
                    Position = _predicted, Info = _info, CellOf = _cellOf, CellCount = _cellCount
                }.Schedule(_count, batch, handle);

                handle = new ScanJob
                {
                    CellCount = _cellCount, Offset = _offset, Info = _info, Count = _count
                }.Schedule(handle);

                // Sort the agents into cell order. From here on the sorted arrays ARE the arrays:
                // swapping them in costs nothing and removes every indirection from the inner loop.
                // All three streams move together or Finalize would difference two different agents.
                handle = new ScatterJob
                {
                    CellOf = _cellOf, Position = _position, Predicted = _predicted, Velocity = _velocity,
                    Offset = _offset,
                    SortedPosition = _sortPosition, SortedPredicted = _sortPredicted,
                    SortedVelocity = _sortVelocity
                }.Schedule(_count, batch, handle);

                Swap(ref _position, ref _sortPosition);
                Swap(ref _predicted, ref _sortPredicted);
                Swap(ref _velocity, ref _sortVelocity);

                // Gather neighbours, then iterate over the cached list. Measured: the grid walk
                // was 78% of every old separation pass (3.81 ms of 5.5 ms), so re-walking it for
                // all 8 iterations was the whole cost. Re-gathering every GatherEvery passes
                // trades a little of that back for contacts that form mid-solve.
                var gatherEvery = math.max(1, s.GatherEvery);

                for (var it = 0; it < s.Iterations; it++)
                {
                    if (s.CacheNeighbours)
                    {
                        if (it % gatherEvery == 0)
                        {
                            handle = new GatherJob
                            {
                                Predicted = _predicted, Offset = _offset, Info = _info,
                                Neighbours = _neighbours, NeighbourCount = _neighbourCount,
                                GatherDiameter = s.CollisionDiameter * (1f + s.GatherSkin),
                                MaxNeighbours = math.min(s.MaxNeighbours, MaxNeighbourStride),
                                Stride = MaxNeighbourStride
                            }.Schedule(_count, batch, handle);
                        }

                        handle = new SeparateCachedJob
                        {
                            Predicted = _predicted, Neighbours = _neighbours,
                            NeighbourCount = _neighbourCount,
                            Result = _scratch, ContactNormal = _contactNormal,
                            Diameter = s.CollisionDiameter, Omega = s.Omega,
                            Stride = MaxNeighbourStride,
                            PlayerPosition = target, PlayerReach = reach
                        }.Schedule(_count, batch, handle);
                    }
                    else if (s.SeparateVariant == 2)
                    {
                        handle = new SeparateSimdJob
                        {
                            Predicted = _predicted, Offset = _offset, Info = _info,
                            Result = _scratch, ContactNormal = _contactNormal,
                            NeighbourCount = _neighbourCount,
                            Diameter = s.CollisionDiameter, Omega = s.Omega,
                            MaxNeighbours = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1),
                            PlayerPosition = target, PlayerReach = reach
                        }.Schedule(_count, batch, handle);
                    }
                    else if (s.SeparateVariant == 1)
                    {
                        handle = new SeparateCompactJob
                        {
                            Predicted = _predicted, Offset = _offset, Info = _info,
                            Result = _scratch, ContactNormal = _contactNormal,
                            NeighbourCount = _neighbourCount,
                            Diameter = s.CollisionDiameter, Omega = s.Omega,
                            MaxNeighbours = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1),
                            PlayerPosition = target, PlayerReach = reach
                        }.Schedule(_count, batch, handle);
                    }
                    else
                    {
                        handle = new SeparateJob
                        {
                            Predicted = _predicted, Offset = _offset, Info = _info,
                            Result = _scratch, ContactNormal = _contactNormal,
                            NeighbourCount = _neighbourCount,
                            Diameter = s.CollisionDiameter, Omega = s.Omega,
                            MaxNeighbours = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1),
                            PlayerPosition = target, PlayerReach = reach
                        }.Schedule(_count, batch, handle);
                    }
                    Swap(ref _predicted, ref _scratch);

                    // Ablation variants run IN ADDITION to the real solve, writing to a throwaway
                    // buffer, so the crowd state they measure is the real one.
                    switch (s.AblationStage)
                    {
                        case 1:
                            handle = new AblateStage1
                            { Predicted = _predicted, Sink = _sortPosition }
                                .Schedule(_count, batch, handle);
                            break;
                        case 2:
                            handle = new AblateStage2
                            { Predicted = _predicted, Offset = _offset, Info = _info, Sink = _sortPosition }
                                .Schedule(_count, batch, handle);
                            break;
                        case 3:
                            handle = new AblateStage3
                            { Predicted = _predicted, Offset = _offset, Info = _info, Sink = _sortPosition }
                                .Schedule(_count, batch, handle);
                            break;
                        case 4:
                            handle = new AblateStage4
                            { Predicted = _predicted, Offset = _offset, Info = _info, Sink = _sortPosition }
                                .Schedule(_count, batch, handle);
                            break;
                        case 5:
                            handle = new AblateStage5
                            { Predicted = _predicted, Offset = _offset, Info = _info, Sink = _sortPosition }
                                .Schedule(_count, batch, handle);
                            break;
                        case 6:
                            handle = new AblateStage6
                            {
                                Predicted = _predicted, Offset = _offset, Info = _info,
                                Sink = _sortPosition, Diameter = s.CollisionDiameter
                            }.Schedule(_count, batch, handle);
                            break;
                        case 7:
                            handle = new AblateStage7
                            {
                                Predicted = _predicted, Offset = _offset, Info = _info,
                                Sink = _sortPosition, Diameter = s.CollisionDiameter
                            }.Schedule(_count, batch, handle);
                            break;
                        case 8:
                            handle = new AblateStage8
                            {
                                Predicted = _predicted, Offset = _offset, Info = _info,
                                Sink = _sortPosition, Diameter = s.CollisionDiameter
                            }.Schedule(_count, batch, handle);
                            break;
                        case 9:
                            handle = new AblateStage9
                            {
                                Predicted = _predicted, Offset = _offset, Info = _info,
                                Sink = _sortPosition, Diameter = s.CollisionDiameter
                            }.Schedule(_count, batch, handle);
                            break;
                    }
                }

                handle = new FinalizeJob
                {
                    Position = _position, Velocity = _velocity, Predicted = _predicted,
                    ContactNormal = _contactNormal, NeighbourCount = _neighbourCount,
                    Instances = _instances, Target = target,
                    InvDeltaTime = 1f / sdt, MaxSpeed = s.MaxSpeed,
                    TangentialSlide = s.TangentialSlide,
                    WriteInstances = sub == substeps - 1,
                    CrowdFree = s.CrowdFree, CrowdFull = s.CrowdFull
                }.Schedule(_count, batch, handle);
            }

            if (s.CheckOverlap)
            {
                // Re-grid the SOLVED positions and count real interpenetration. It needs its own
                // grid because the solve moved every agent after the simulation grid was built,
                // so that grid no longer describes where the agents actually are. The sort
                // buffers and cell arrays are all free by this point, so this costs one grid
                // build and one scan - no extra memory.
                handle = new BoundsJob
                {
                    Position = _position, Partial = _partialBounds, Stride = stride
                }.Schedule(_slices, 1, handle);

                handle = new GridSetupJob
                {
                    Partial = _partialBounds, Info = _info,
                    CellSize = s.CollisionDiameter, MaxCells = _cellCount.Length, Count = _count
                }.Schedule(handle);

                handle = new HashJob
                {
                    Position = _position, Info = _info, CellOf = _cellOf, CellCount = _cellCount
                }.Schedule(_count, batch, handle);

                handle = new ScanJob
                {
                    CellCount = _cellCount, Offset = _offset, Info = _info, Count = _count
                }.Schedule(handle);

                handle = new ScatterJob
                {
                    CellOf = _cellOf, Position = _position, Predicted = _predicted, Velocity = _velocity,
                    Offset = _offset,
                    SortedPosition = _sortPosition, SortedPredicted = _sortPredicted,
                    SortedVelocity = _sortVelocity
                }.Schedule(_count, batch, handle);

                Swap(ref _position, ref _sortPosition);
                Swap(ref _predicted, ref _sortPredicted);
                Swap(ref _velocity, ref _sortVelocity);

                handle = new ClearStatsJob { Stats = _stats }.Schedule(handle);

                handle = new OverlapJob
                {
                    Position = _position, Offset = _offset, Info = _info,
                    SolveDiameter = s.CollisionDiameter, BodyDiameter = s.Radius * 2f,
                    Stats = _stats
                }.Schedule(_count, batch, handle);

                handle = new ReduceStatsJob
                {
                    Stats = _stats,
                    Threads = Unity.Jobs.LowLevel.Unsafe.JobsUtility.ThreadIndexCount
                }.Schedule(handle);
            }

            ScheduleMarker.End();
            return handle;
        }

        /// <summary>
        /// Time one separation variant on the crowd state as it stands right now. Call it only
        /// after the frame's handle has completed: at that point _predicted holds the solved
        /// positions and _offset/_info describe exactly those positions, because the overlap
        /// check re-grids and re-sorts them. Every rep therefore reads IDENTICAL input, which is
        /// the only way an A/B on a moving crowd means anything - a variant measured on its own
        /// run is measured on a different crowd.
        ///
        /// Results go to _scratch, which the next frame overwrites before anything reads it.
        /// Returns wall milliseconds per dispatch. The all-threads number the profiler reports is
        /// this times the number of executing threads (workers + main).
        /// </summary>
        public double TimeSeparate(int variant, float2 target, int reps)
        {
            if (!_allocated || _count == 0 || reps <= 0) return 0d;
            var s = _settings;
            var reach = s.Radius + s.PlayerRadius;
            var batch = s.BatchSize;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var r = 0; r < reps; r++)
            {
                JobHandle h;
                if (variant == 2)
                {
                    h = new SeparateSimdJob
                    {
                        Predicted = _predicted, Offset = _offset, Info = _info,
                        Result = _scratch, ContactNormal = _contactNormal,
                        NeighbourCount = _neighbourCount,
                        Diameter = s.CollisionDiameter, Omega = s.Omega,
                        MaxNeighbours = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1),
                        PlayerPosition = target, PlayerReach = reach
                    }.Schedule(_count, batch);
                }
                else if (variant == 1)
                {
                    h = new SeparateCompactJob
                    {
                        Predicted = _predicted, Offset = _offset, Info = _info,
                        Result = _scratch, ContactNormal = _contactNormal,
                        NeighbourCount = _neighbourCount,
                        Diameter = s.CollisionDiameter, Omega = s.Omega,
                        MaxNeighbours = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1),
                        PlayerPosition = target, PlayerReach = reach
                    }.Schedule(_count, batch);
                }
                else
                {
                    h = new SeparateJob
                    {
                        Predicted = _predicted, Offset = _offset, Info = _info,
                        Result = _scratch, ContactNormal = _contactNormal,
                        NeighbourCount = _neighbourCount,
                        Diameter = s.CollisionDiameter, Omega = s.Omega,
                        MaxNeighbours = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1),
                        PlayerPosition = target, PlayerReach = reach
                    }.Schedule(_count, batch);
                }
                h.Complete();
            }
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds / reps;
        }

        /// <summary>
        /// Same stopwatch, pointed at one ablation stage, so the stage table can be rebuilt on the
        /// current crowd without opening the profiler. Stages 2..8 all scan the same candidates,
        /// so the difference between two of them is the cost of the piece that was added.
        /// </summary>
        public double TimeAblation(int stage, int reps)
        {
            if (!_allocated || _count == 0 || reps <= 0) return 0d;
            var batch = _settings.BatchSize;
            var dia = _settings.CollisionDiameter;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var r = 0; r < reps; r++)
            {
                JobHandle h;
                switch (stage)
                {
                    case 1:
                        h = new AblateStage1 { Predicted = _predicted, Sink = _scratch }
                            .Schedule(_count, batch);
                        break;
                    case 2:
                        h = new AblateStage2 { Predicted = _predicted, Offset = _offset, Info = _info, Sink = _scratch }
                            .Schedule(_count, batch);
                        break;
                    case 3:
                        h = new AblateStage3 { Predicted = _predicted, Offset = _offset, Info = _info, Sink = _scratch }
                            .Schedule(_count, batch);
                        break;
                    case 4:
                        h = new AblateStage4 { Predicted = _predicted, Offset = _offset, Info = _info, Sink = _scratch }
                            .Schedule(_count, batch);
                        break;
                    case 5:
                        h = new AblateStage5 { Predicted = _predicted, Offset = _offset, Info = _info, Sink = _scratch }
                            .Schedule(_count, batch);
                        break;
                    case 6:
                        h = new AblateStage6 { Predicted = _predicted, Offset = _offset, Info = _info, Sink = _scratch, Diameter = dia }
                            .Schedule(_count, batch);
                        break;
                    case 7:
                        h = new AblateStage7 { Predicted = _predicted, Offset = _offset, Info = _info, Sink = _scratch, Diameter = dia }
                            .Schedule(_count, batch);
                        break;
                    case 8:
                        h = new AblateStage8 { Predicted = _predicted, Offset = _offset, Info = _info, Sink = _scratch, Diameter = dia }
                            .Schedule(_count, batch);
                        break;
                    case 9:
                        h = new AblateStage9 { Predicted = _predicted, Offset = _offset, Info = _info, Sink = _scratch, Diameter = dia }
                            .Schedule(_count, batch);
                        break;
                    default:
                        return 0d;
                }
                h.Complete();
            }
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds / reps;
        }

        /// <summary>
        /// Every separation variant over the same crowd state, plus the largest position
        /// disagreement each one has with SeparateJob. The timing is worthless without that
        /// check: a variant that skips neighbours is trivially faster. Runs them interleaved so a
        /// thermal or scheduler drift over the run hits all of them.
        /// </summary>
        public void CompareSeparate(float2 target, int reps, double[] ms, float[] delta)
        {
            var variants = ms.Length;
            for (var v = 0; v < variants; v++) { ms[v] = 0d; delta[v] = 0f; }
            if (!_allocated || _count == 0) return;

            // Warm the caches and let Burst's first-call overhead land outside the measurement.
            for (var v = 0; v < variants; v++) TimeSeparate(v, target, 2);

            var reference = new NativeArray<float2>(_count, Allocator.Temp);
            for (var r = 0; r < reps; r++)
            {
                for (var v = 0; v < variants; v++)
                {
                    ms[v] += TimeSeparate(v, target, 1);
                    if (v == 0) reference.CopyFrom(_scratch);
                    else
                        for (var i = 0; i < _count; i++)
                            delta[v] = math.max(delta[v], math.length(reference[i] - _scratch[i]));
                }
            }
            reference.Dispose();
            for (var v = 0; v < variants; v++) ms[v] /= reps;
        }

        static void Swap(ref NativeArray<float2> a, ref NativeArray<float2> b)
        {
            (a, b) = (b, a);
        }

        public void Release()
        {
            if (!_allocated) return;
            _position.Dispose();
            _velocity.Dispose();
            _predicted.Dispose();
            _scratch.Dispose();
            _sortPosition.Dispose();
            _sortPredicted.Dispose();
            _sortVelocity.Dispose();
            _contactNormal.Dispose();
            _neighbourCount.Dispose();
            _neighbours.Dispose();
            _instances.Dispose();
            _cellOf.Dispose();
            _cellCount.Dispose();
            _offset.Dispose();
            _partialBounds.Dispose();
            _info.Dispose();
            _stats.Dispose();
            _allocated = false;
            _count = 0;
        }
    }
}
