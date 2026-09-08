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

        /// <summary>Colour buckets allocated for the radius path: (MaxRadius + 1)^2.</summary>
        public const int MaxColours =
            (SeparateColouredRJob.MaxRadius + 1) * (SeparateColouredRJob.MaxRadius + 1);

        public static readonly ProfilerMarker ScheduleMarker = new ProfilerMarker("Boid.Schedule");
        public static readonly ProfilerMarker CompleteMarker = new ProfilerMarker("Boid.Complete");

        readonly BoidSettings _settings;

        /// <summary>
        /// Debug switches, set by the owner each frame. They live here rather than on the
        /// BoidSettings asset because they are measurement, not tuning, and because a
        /// ScriptableObject edited at runtime keeps the change after play mode exits.
        /// </summary>
        public bool CheckOverlap;
        public int AblationStage;
        /// <summary>
        /// Scan radius for SeparateVariant 4. The cell is the collision diameter divided by this
        /// and the scan reaches this many cells, so the reach is one diameter at any radius; what
        /// changes is the swept area and the (R+1)^2 colours it takes to keep the in-place write
        /// safe. Lives here rather than on BoidSettings for the same reason CheckOverlap does:
        /// it is a measurement until it wins, and a field on the solver dies with play mode.
        /// </summary>
        public int ScanRadius = 1;

        /// <summary>
        /// Multiplier on the cell size for the radius path, on top of the divide by ScanRadius.
        /// The scan still reaches ScanRadius cells, so reach = diameter * this: at 1 it is the
        /// tight grid, above 1 it over-scans and no contact is missed either way.
        ///
        /// This is the knob the cycle split actually points at. The candidate walk costs ~24 cycles
        /// per ROW-RUN ENTRY against ~6 per candidate, and there are 2R+1 entries per AGENT. A
        /// bigger cell holds more agents - 1.23 per cell at scale 1, and it goes as the square - so
        /// the run bounds, which are hoisted once per cell, amortise over more of them. It buys
        /// that by scanning scale^2 more candidates, so the two terms fight and the minimum is a
        /// measurement.
        /// </summary>
        public float CellScale = 1f;

        /// <summary>
        /// Count how far the separation solve moved each agent this frame, into
        /// <see cref="MotionBand"/>. Verification, like CheckOverlap - nothing in the sim reads it.
        /// It exists to size the sleeping idea before building it: skipping agents that cannot move
        /// is only worth it if most of them cannot move WHILE THE CROWD IS FLOWING.
        /// </summary>
        public bool MeasureMotion;

        NativeArray<float2> _position;
        NativeArray<float2> _velocity;
        NativeArray<float2> _predicted;
        NativeArray<float2> _scratch;        // ping-pong target for the Jacobi passes
        NativeArray<float2> _sortPosition;
        NativeArray<float2> _sortPredicted;
        NativeArray<float2> _sortVelocity;
        NativeArray<float2> _contactNormal;
        NativeArray<int> _neighbourCount;
        NativeArray<int> _gatherCount;
        NativeArray<int> _candidateCount;
        NativeArray<int> _neighbours;
        NativeArray<float4> _instances;
        NativeArray<int> _cellOf;
        NativeArray<int> _cellCount;
        NativeArray<int> _offset;
        NativeArray<float4> _partialBounds;
        NativeArray<GridInfo> _info;
        NativeArray<int> _stats;
        NativeArray<int> _motion;
        NativeArray<float2> _preSolve;
        // SoA position streams and a hit counter, for the SIMD walk experiment only. The +8 slack
        // is what makes the masked over-read past a row run legal to LOAD; the mask is what makes
        // it legal to USE.
        NativeArray<float> _predX, _predY;
        NativeArray<int> _hitCount;
        NativeList<int> _colour0, _colour1, _colour2, _colour3;
        /// <summary>Colour buckets for the radius-parameterised path, (MaxRadius+1)^2 of them.</summary>
        NativeList<int>[] _colourR;

        /// <summary>AVX2 + FMA, answered by Burst rather than by managed code. See CpuFeatureJob.</summary>
        bool _simdSupported;

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

        /// <summary>
        /// Agents whose whole separation solve moved them less than 0.01%, 0.1%, 1%, 5% and more
        /// than 5% of the solve diameter. Valid only with <see cref="MeasureMotion"/> on.
        /// </summary>
        public int MotionBand(int band) => _motion.IsCreated ? _motion[band] : 0;

        public BoidSolver(BoidSettings settings)
        {
            _settings = settings;
        }

        public void Allocate(int count, float spawnMin, float spawnMax, int seed)
        {
            Release();
            _count = count;

            // Halving the cell to scan 5x5 quadruples the cell count, so the budget has to hold
            // the largest radius that will be measured or GridSetupJob grows the cell instead -
            // which silently turns a radius-3 run back into a radius-1 run with extra colours.
            var baseCells = math.max(65536, count * _settings.CellsPerAgent);
            var maxCells = baseCells * SeparateColouredRJob.MaxRadius * SeparateColouredRJob.MaxRadius;
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
            _gatherCount = new NativeArray<int>(count, Allocator.Persistent);
            _candidateCount = new NativeArray<int>(count, Allocator.Persistent);
            // Fixed stride, so MaxNeighbours can be retuned at runtime without reallocating -
            // and, more to the point, without GatherJob writing past the end of a shorter array.
            _neighbours = new NativeArray<int>(count * MaxNeighbourStride, Allocator.Persistent);
            _instances = new NativeArray<float4>(count, Allocator.Persistent);
            _cellOf = new NativeArray<int>(count, Allocator.Persistent);
            _cellCount = new NativeArray<int>(maxCells, Allocator.Persistent);
            _offset = new NativeArray<int>(maxCells + 1, Allocator.Persistent);
            _partialBounds = new NativeArray<float4>(_slices, Allocator.Persistent);
            _info = new NativeArray<GridInfo>(1, Allocator.Persistent);
            var colourCap = math.max(1024, baseCells / 4);
            _colour0 = new NativeList<int>(colourCap, Allocator.Persistent);
            _colour1 = new NativeList<int>(colourCap, Allocator.Persistent);
            _colour2 = new NativeList<int>(colourCap, Allocator.Persistent);
            _colour3 = new NativeList<int>(colourCap, Allocator.Persistent);
            // Sized small and left to grow once: a colour holds cells/(R+1)^2, and which radius is
            // running is not known here.
            _colourR = new NativeList<int>[MaxColours];
            for (var c = 0; c < MaxColours; c++)
                _colourR[c] = new NativeList<int>(8192, Allocator.Persistent);
            _stats = new NativeArray<int>(
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.ThreadIndexCount * OverlapJob.Stride,
                Allocator.Persistent);
            _motion = new NativeArray<int>(
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.ThreadIndexCount * MotionStatsJob.Stride,
                Allocator.Persistent);
            _preSolve = new NativeArray<float2>(count, Allocator.Persistent);
            _predX = new NativeArray<float>(count + 8, Allocator.Persistent);
            _predY = new NativeArray<float>(count + 8, Allocator.Persistent);
            _hitCount = new NativeArray<int>(count, Allocator.Persistent);

            using (var probe = new NativeArray<bool>(1, Allocator.TempJob))
            {
                new CpuFeatureJob { Supported = probe }.Schedule().Complete();
                _simdSupported = probe[0];
            }

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

            // 4 = parameterised radius, 5 = + rsqrt instead of sqrt-then-divide,
            // 6 = + phase 1 hands phase 2 the delta and r2 it already had. All three share the
            // grid, the colouring and the dispatch shape, so a sweep row isolates one change.
            var variant = s.SeparateVariant;
            // Variant 8 is hand-written AVX2 + FMA. On a machine without them the intrinsics
            // would trap, so fall back to variant 5 - same geometry, same colouring, just scalar.
            // Checked here on the main thread rather than inside the job, so the fallback picks a
            // different job rather than branching in the inner loop.
            if (variant == 8 && !_simdSupported) variant = 5;

            var radiusPath = !s.CacheNeighbours && variant >= 4 && variant <= 8;
            // Variant 8 works on float streams. The pipeline stays AoS: deinterleave once before
            // the passes, run all of them in place on the streams, interleave back for Finalize.
            var soaPath = !s.CacheNeighbours && variant == 8;
            var radius = radiusPath ? math.clamp(ScanRadius, 1, SeparateColouredRJob.MaxRadius) : 1;
            var spacing = radius + 1;
            var colours = spacing * spacing;

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
                    // On the radius path the cell is the diameter divided by R instead, because
                    // the scan reaches R cells - the product, and so the reach, is unchanged.
                    CellSize = s.CacheNeighbours
                        ? s.CollisionDiameter * (1f + s.GatherSkin)
                        : s.CollisionDiameter * (radiusPath ? math.max(0.25f, CellScale) : 1f) / radius,
                    MaxCells = _cellCount.Length, Count = _count, Pad = radius + 1
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

                // Snapshot AFTER the sort so the indices match the ones the solve will write, and
                // before any separation pass, so what is measured is the solve correction alone -
                // not the steering, which moves every agent whether it is jammed or not.
                if (MeasureMotion)
                {
                    handle = new ClearMotionJob { Stats = _motion }.Schedule(handle);
                    handle = new CopyJob
                    {
                        Source = _predicted, Destination = _preSolve
                    }.Schedule(_count, batch, handle);
                }

                var coloured = !s.CacheNeighbours && s.SeparateVariant == 3;
                if (radiusPath)
                {
                    // One job per colour, all in parallel off the same grid: a colour owns a fixed
                    // 1/(R+1)^2 stride of the cells, and at R=3 there are nine times as many cells
                    // to walk as at R=1, which is too much for the single-threaded version.
                    var buckets = new NativeArray<JobHandle>(colours, Allocator.Temp);
                    for (var c = 0; c < colours; c++)
                        buckets[c] = new ColourCellsRJob
                        {
                            Offset = _offset, Info = _info, Cells = _colourR[c],
                            Colour = c, Spacing = spacing
                        }.Schedule(handle);
                    handle = JobHandle.CombineDependencies(buckets);
                    buckets.Dispose();
                }
                else if (coloured)
                {
                    // Once per grid build, not once per pass: the buckets only change when the
                    // sort changes.
                    handle = new ColourCellsJob
                    {
                        Offset = _offset, Info = _info,
                        Colour0 = _colour0, Colour1 = _colour1,
                        Colour2 = _colour2, Colour3 = _colour3
                    }.Schedule(handle);
                }

                if (soaPath)
                {
                    handle = new DeinterleaveJob
                    {
                        Source = _predicted, X = _predX, Y = _predY
                    }.Schedule(_count, batch, handle);
                }

                for (var it = 0; it < s.Iterations; it++)
                {
                    if (s.CacheNeighbours)
                    {
                        if (it % gatherEvery == 0)
                        {
                            handle = new GatherJob
                            {
                                Predicted = _predicted, Offset = _offset, Info = _info,
                                Neighbours = _neighbours, GatherCount = _gatherCount,
                                GatherDiameter = s.CollisionDiameter * (1f + s.GatherSkin),
                                MaxNeighbours = math.min(s.MaxNeighbours, MaxNeighbourStride),
                                Stride = MaxNeighbourStride
                            }.Schedule(_count, batch, handle);
                        }

                        handle = new SeparateCachedJob
                        {
                            Predicted = _predicted, Neighbours = _neighbours,
                            GatherCount = _gatherCount,
                            Result = _scratch, ContactNormal = _contactNormal,
                            NeighbourCount = _neighbourCount,
                            Diameter = s.CollisionDiameter, Omega = s.Omega,
                            MaxNeighbours = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1),
                            Stride = MaxNeighbourStride,
                            PlayerPosition = target, PlayerReach = reach
                        }.Schedule(_count, batch, handle);
                    }
                    else if (radiusPath)
                    {
                        // (R+1)^2 dispatches instead of 4, each depending on the last, because the
                        // ordering IS the Gauss-Seidel step. This chain is the cost the smaller
                        // candidate list has to beat.
                        var cb = math.max(1, s.ColourBatch);
                        var maxN = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1);
                        var minDiv = math.max(1, s.MinDivisor);
                        for (var c = 0; c < colours; c++)
                        {
                            var cells = _colourR[c];
                            if (variant == 8)
                                handle = new SeparateSoaMaskedJob
                                {
                                    Cells = cells.AsDeferredJobArray(),
                                    PredX = _predX, PredY = _predY,
                                    Offset = _offset, Info = _info,
                                    ContactNormal = _contactNormal, NeighbourCount = _neighbourCount,
                                    Diameter = s.CollisionDiameter, Omega = s.Omega,
                                    MinDivisor = minDiv, ScanRadius = radius,
                                    PlayerPosition = target, PlayerReach = reach
                                }.Schedule(cells, cb, handle);
                            else if (variant == 7)
                                handle = new SeparateColouredSplitJob
                                {
                                    Cells = cells.AsDeferredJobArray(),
                                    Predicted = _predicted, Offset = _offset, Info = _info,
                                    ContactNormal = _contactNormal, NeighbourCount = _neighbourCount,
                                    Diameter = s.CollisionDiameter, Omega = s.Omega,
                                    MaxNeighbours = maxN, MinDivisor = minDiv, ScanRadius = radius,
                                    PlayerPosition = target, PlayerReach = reach
                                }.Schedule(cells, cb, handle);
                            else if (variant == 6)
                                handle = new SeparateColouredFusedJob
                                {
                                    Cells = cells.AsDeferredJobArray(),
                                    Predicted = _predicted, Offset = _offset, Info = _info,
                                    ContactNormal = _contactNormal, NeighbourCount = _neighbourCount,
                                    Diameter = s.CollisionDiameter, Omega = s.Omega,
                                    MaxNeighbours = maxN, MinDivisor = minDiv, ScanRadius = radius,
                                    PlayerPosition = target, PlayerReach = reach
                                }.Schedule(cells, cb, handle);
                            else if (variant == 5)
                                handle = new SeparateColouredRsqrtJob
                                {
                                    Cells = cells.AsDeferredJobArray(),
                                    Predicted = _predicted, Offset = _offset, Info = _info,
                                    ContactNormal = _contactNormal, NeighbourCount = _neighbourCount,
                                    Diameter = s.CollisionDiameter, Omega = s.Omega,
                                    MaxNeighbours = maxN, MinDivisor = minDiv, ScanRadius = radius,
                                    PlayerPosition = target, PlayerReach = reach
                                }.Schedule(cells, cb, handle);
                            else
                                handle = new SeparateColouredRJob
                                {
                                    Cells = cells.AsDeferredJobArray(),
                                    Predicted = _predicted, Offset = _offset, Info = _info,
                                    ContactNormal = _contactNormal, NeighbourCount = _neighbourCount,
                                    Diameter = s.CollisionDiameter, Omega = s.Omega,
                                    MaxNeighbours = maxN, MinDivisor = minDiv, ScanRadius = radius,
                                    PlayerPosition = target, PlayerReach = reach
                                }.Schedule(cells, cb, handle);
                        }
                    }
                    else if (coloured)
                    {
                        // Four dispatches, one per colour, each writing in place. They must run in
                        // order - that ordering IS the Gauss-Seidel step - so each depends on the
                        // previous, and the chain is the whole point rather than a missed
                        // parallelisation.
                        for (var c = 0; c < 4; c++)
                        {
                            var cells = c == 0 ? _colour0 : c == 1 ? _colour1 : c == 2 ? _colour2 : _colour3;
                            handle = new SeparateColouredJob
                            {
                                Cells = cells.AsDeferredJobArray(),
                                Predicted = _predicted, Offset = _offset, Info = _info,
                                ContactNormal = _contactNormal, NeighbourCount = _neighbourCount,
                                Diameter = s.CollisionDiameter, Omega = s.Omega,
                                MaxNeighbours = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1),
                                MinDivisor = math.max(1, s.MinDivisor),
                                PlayerPosition = target, PlayerReach = reach
                            }.Schedule(cells, math.max(1, s.ColourBatch), handle);
                        }
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
                    // The coloured jobs correct in place, so there is no second buffer to swap.
                    if (!coloured && !radiusPath) Swap(ref _predicted, ref _scratch);

                    // Ablation variants run IN ADDITION to the real solve, writing to a throwaway
                    // buffer, so the crowd state they measure is the real one.
                    switch (AblationStage)
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

                if (soaPath)
                {
                    handle = new InterleaveJob
                    {
                        X = _predX, Y = _predY, Destination = _predicted
                    }.Schedule(_count, batch, handle);
                }

                if (MeasureMotion)
                {
                    handle = new MotionStatsJob
                    {
                        Before = _preSolve, After = _predicted,
                        Diameter = s.CollisionDiameter, Stats = _motion
                    }.Schedule(_count, batch, handle);
                    handle = new ReduceMotionJob
                    {
                        Stats = _motion,
                        Threads = Unity.Jobs.LowLevel.Unsafe.JobsUtility.ThreadIndexCount
                    }.Schedule(handle);
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

            if (CheckOverlap)
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
                    // The check is its own 3x3 scan at the solve diameter whatever the solver did,
                    // so the quality number stays comparable across radii.
                    CellSize = s.CollisionDiameter, MaxCells = _cellCount.Length, Count = _count,
                    Pad = 2
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

        /// <summary>
        /// Time the non-separation work in the frame. The separation job has an ablation harness
        /// and an A/B timer; nothing else ever had either, so its cost was only ever inferred by
        /// subtracting the pass slope from the total.
        ///
        /// The grid chain is timed as ONE unit rather than job by job, because Hash, Scan and
        /// Scatter are only idempotent as a cycle: Hash fills CellCount, Scan turns it into Offset
        /// AND zeroes CellCount for next time, and Scatter decrements Offset down to the cell
        /// starts. Timing Scatter on its own in a loop decrements Offset once per rep and it walks
        /// off the front of the array - which is exactly what the first version of this did.
        ///
        /// Everything here writes to buffers the next frame overwrites, and the chain leaves
        /// CellCount zeroed and Offset in the layout the solver expects, so running it mid-play
        /// does not disturb the simulation.
        /// </summary>
        public void TimeStages(double[] ms, int reps)
        {
            for (var i = 0; i < ms.Length; i++) ms[i] = 0d;
            if (!_allocated || _count == 0 || reps <= 0) return;

            var s = _settings;
            var batch = s.BatchSize;
            var stride = (_count + _slices - 1) / _slices;
            var watch = new System.Diagnostics.Stopwatch();

            // 0 Steer. Writes the SORT buffers, not the live velocity - running the real one six
            // times would integrate the crowd six extra frames.
            watch.Restart();
            for (var r = 0; r < reps; r++)
                new SteerJob
                {
                    Position = _position, Velocity = _sortVelocity, Predicted = _sortPredicted,
                    Target = float2.zero, Speed = s.Speed, Blend = s.SteerBlend, DeltaTime = 1f / 60f
                }.Schedule(_count, batch).Complete();
            ms[0] = watch.Elapsed.TotalMilliseconds / reps;

            // 1 Whole grid build: bounds reduce, sizing, hash, prefix scan, counting-sort scatter.
            watch.Restart();
            for (var r = 0; r < reps; r++) GridChain(stride, batch).Complete();
            ms[1] = watch.Elapsed.TotalMilliseconds / reps;

            // 2 Colour bucketing, coloured path only.
            watch.Restart();
            for (var r = 0; r < reps; r++)
                new ColourCellsJob
                {
                    Offset = _offset, Info = _info,
                    Colour0 = _colour0, Colour1 = _colour1, Colour2 = _colour2, Colour3 = _colour3
                }.Schedule().Complete();
            ms[2] = watch.Elapsed.TotalMilliseconds / reps;

            // 3 Finalize.
            watch.Restart();
            for (var r = 0; r < reps; r++)
                new FinalizeJob
                {
                    Position = _sortPosition, Velocity = _sortVelocity, Predicted = _predicted,
                    ContactNormal = _contactNormal, NeighbourCount = _neighbourCount,
                    Instances = _instances, Target = float2.zero, InvDeltaTime = 60f,
                    MaxSpeed = s.MaxSpeed, TangentialSlide = s.TangentialSlide,
                    WriteInstances = true, CrowdFree = s.CrowdFree, CrowdFull = s.CrowdFull
                }.Schedule(_count, batch).Complete();
            ms[3] = watch.Elapsed.TotalMilliseconds / reps;

            // 4 The verification check: a SECOND full grid build plus the overlap scan and reduce.
            // This is the number that matters most here, because none of it would ship.
            watch.Restart();
            for (var r = 0; r < reps; r++)
            {
                var h = GridChain(stride, batch);
                h = new ClearStatsJob { Stats = _stats }.Schedule(h);
                h = new OverlapJob
                {
                    Position = _position, Offset = _offset, Info = _info,
                    SolveDiameter = s.CollisionDiameter, BodyDiameter = s.Radius * 2f, Stats = _stats
                }.Schedule(_count, batch, h);
                new ReduceStatsJob
                {
                    Stats = _stats, Threads = Unity.Jobs.LowLevel.Unsafe.JobsUtility.ThreadIndexCount
                }.Schedule(h).Complete();
            }
            ms[4] = watch.Elapsed.TotalMilliseconds / reps;
        }

        /// <summary>
        /// One full grid build against the live positions, writing the sort buffers. Self
        /// contained and repeatable: Scan zeroes CellCount behind itself and Scatter consumes the
        /// Offset that Scan just wrote, so the arrays end where they started.
        /// </summary>
        JobHandle GridChain(int stride, int batch)
        {
            return GridChain(stride, batch, _settings.CollisionDiameter, 2);
        }

        JobHandle GridChain(int stride, int batch, float cellSize, int pad)
        {
            var handle = new BoundsJob
            {
                Position = _position, Partial = _partialBounds, Stride = stride
            }.Schedule(_slices, 1);

            handle = new GridSetupJob
            {
                Partial = _partialBounds, Info = _info, CellSize = cellSize,
                MaxCells = _cellCount.Length, Count = _count, Pad = pad
            }.Schedule(handle);

            handle = new HashJob
            {
                Position = _position, Info = _info, CellOf = _cellOf, CellCount = _cellCount
            }.Schedule(_count, batch, handle);

            handle = new ScanJob
            {
                CellCount = _cellCount, Offset = _offset, Info = _info, Count = _count
            }.Schedule(handle);

            return new ScatterJob
            {
                CellOf = _cellOf, Position = _position, Predicted = _predicted, Velocity = _velocity,
                Offset = _offset, SortedPosition = _sortPosition,
                SortedPredicted = _sortPredicted, SortedVelocity = _sortVelocity
            }.Schedule(_count, batch, handle);
        }

        /// <summary>
        /// Cost breakdown of the job that actually ships. Every stage is scheduled over all four
        /// colour lists exactly as the real job is, so the four dispatches and their barriers are
        /// inside every number and the differences isolate the loop body rather than the
        /// scheduling. Stage 0 is the full job for reference.
        ///
        /// Stage 3 also fills _candidateCount, so the candidates-per-agent figure that every
        /// per-candidate estimate has been guessing at becomes a measurement.
        /// </summary>
        public double TimeColoured(int stage, int reps)
        {
            if (!_allocated || _count == 0 || reps <= 0) return 0d;
            var s = _settings;
            var batch = math.max(1, s.ColourBatch);
            var reach = s.Radius + s.PlayerRadius;
            var dia = s.CollisionDiameter;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var r = 0; r < reps; r++)
            {
                var h = default(JobHandle);
                for (var c = 0; c < 4; c++)
                {
                    var cells = c == 0 ? _colour0 : c == 1 ? _colour1 : c == 2 ? _colour2 : _colour3;
                    var arr = cells.AsDeferredJobArray();
                    switch (stage)
                    {
                        case 1:
                            h = new ColouredAblate1
                            { Cells = arr, Offset = _offset, Info = _info, Predicted = _predicted, Sink = _scratch }
                                .Schedule(cells, batch, h);
                            break;
                        case 2:
                            h = new ColouredAblate2
                            { Cells = arr, Offset = _offset, Info = _info, Predicted = _predicted, Sink = _scratch }
                                .Schedule(cells, batch, h);
                            break;
                        case 3:
                            h = new ColouredAblate3
                            {
                                Cells = arr, Offset = _offset, Info = _info, Predicted = _predicted,
                                Sink = _scratch, Candidates = _candidateCount
                            }.Schedule(cells, batch, h);
                            break;
                        case 4:
                            h = new ColouredAblate4
                            { Cells = arr, Offset = _offset, Info = _info, Predicted = _predicted, Sink = _scratch }
                                .Schedule(cells, batch, h);
                            break;
                        case 5:
                            h = new ColouredAblate5
                            { Cells = arr, Offset = _offset, Info = _info, Predicted = _predicted, Sink = _scratch }
                                .Schedule(cells, batch, h);
                            break;
                        case 6:
                            h = new ColouredAblate6
                            {
                                Cells = arr, Offset = _offset, Info = _info, Predicted = _predicted,
                                Sink = _scratch, Diameter = dia
                            }.Schedule(cells, batch, h);
                            break;
                        default:
                            // The real job, but writing the scratch buffer so the timing loop does
                            // not advance the simulation reps times over.
                            h = new SeparateColouredJob
                            {
                                Cells = arr, Predicted = _scratch, Offset = _offset, Info = _info,
                                ContactNormal = _contactNormal, NeighbourCount = _neighbourCount,
                                Diameter = dia, Omega = s.Omega,
                                MaxNeighbours = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1),
                                MinDivisor = math.max(1, s.MinDivisor),
                                PlayerPosition = float2.zero, PlayerReach = reach
                            }.Schedule(cells, batch, h);
                            break;
                    }
                }
                h.Complete();
            }
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds / reps;
        }


        /// <summary>
        /// The radius experiment, measured rather than argued: build the grid at cell = diameter/R,
        /// colour it at spacing R+1, then time the candidate walk alone and the whole separation
        /// pass over exactly that grid.
        ///
        /// The walk timing is the point. C3 - C2 said the walk costs ~6.5 cycles per candidate for
        /// an increment, a compare, a branch and an add, which is three times what those four
        /// instructions can possibly cost. The reason is that it is not a per-candidate cost: the
        /// walk is entered once per ROW RUN, 2R+1 times per agent, and each entry pays a loop
        /// setup, the vectoriser's trip-count guard and a mispredicted exit on a run that only
        /// holds ~4 candidates. Measuring at two radii separates the two, because R=2 cuts
        /// candidates ~31% while raising row runs from 3 to 5:
        ///
        ///   walk(R) = runs(R) * Fixed + candidates(R) * PerCandidate
        ///
        /// Two radii, two unknowns. If Fixed dominates, halving the cell cannot win however few
        /// candidates it leaves, and the whole lead is dead on the first two rows of the sweep.
        ///
        /// Self contained: it builds its own grid off the live positions and swaps the sorted
        /// arrays in, exactly as a frame does, so the crowd is reordered but not moved. The next
        /// frame rebuilds all of it.
        /// </summary>
        public void MeasureRadius(int radius, int reps, out double walkMs, out double fullMs,
                                  out double rsqrtMs, out double fusedMs, out double simdWalkMs,
                                  out double candidates, out double simdCandidates,
                                  out int cells, out int workItems)
        {
            walkMs = 0d; fullMs = 0d; rsqrtMs = 0d; fusedMs = 0d; simdWalkMs = 0d;
            candidates = 0d; simdCandidates = 0d; cells = 0; workItems = 0;
            if (!_allocated || _count == 0 || reps <= 0) return;

            var s = _settings;
            var r = math.clamp(radius, 1, SeparateColouredRJob.MaxRadius);
            var spacing = r + 1;
            var colours = spacing * spacing;
            var batch = math.max(1, s.ColourBatch);
            var stride = (_count + _slices - 1) / _slices;
            var reach = s.Radius + s.PlayerRadius;

            GridChain(stride, s.BatchSize, s.CollisionDiameter / r, r + 1).Complete();
            Swap(ref _position, ref _sortPosition);
            Swap(ref _predicted, ref _sortPredicted);
            Swap(ref _velocity, ref _sortVelocity);

            var buckets = new NativeArray<JobHandle>(colours, Allocator.Temp);
            for (var c = 0; c < colours; c++)
                buckets[c] = new ColourCellsRJob
                {
                    Offset = _offset, Info = _info, Cells = _colourR[c],
                    Colour = c, Spacing = spacing
                }.Schedule();
            JobHandle.CombineDependencies(buckets).Complete();
            buckets.Dispose();

            cells = _info[0].Cells;
            for (var c = 0; c < colours; c++) workItems += _colourR[c].Length;

            // Deinterleave once, outside every timed loop - this experiment is about whether the
            // 8-wide block beats the scalar walk, not about what the split costs to build.
            new DeinterleaveJob
            {
                Source = _predicted, X = _predX, Y = _predY
            }.Schedule(_count, s.BatchSize).Complete();

            JobHandle SimdWalk()
            {
                var h = default(JobHandle);
                for (var c = 0; c < colours; c++)
                    h = new ColouredWalkSimdJob
                    {
                        Cells = _colourR[c].AsDeferredJobArray(), Offset = _offset, Info = _info,
                        PredX = _predX, PredY = _predY,
                        Sink = _scratch, Candidates = _candidateCount, Hits = _hitCount,
                        Normals = _contactNormal,
                        Diameter = s.CollisionDiameter, Omega = s.Omega, ScanRadius = r
                    }.Schedule(_colourR[c], batch, h);
                return h;
            }

            JobHandle Walk()
            {
                var h = default(JobHandle);
                for (var c = 0; c < colours; c++)
                    h = new ColouredWalkRJob
                    {
                        Cells = _colourR[c].AsDeferredJobArray(), Offset = _offset, Info = _info,
                        Predicted = _predicted, Sink = _scratch, Candidates = _candidateCount,
                        ScanRadius = r
                    }.Schedule(_colourR[c], batch, h);
                return h;
            }

            JobHandle Full()
            {
                var h = default(JobHandle);
                for (var c = 0; c < colours; c++)
                    h = new SeparateColouredRJob
                    {
                        Cells = _colourR[c].AsDeferredJobArray(),
                        // Writes the scratch buffer, so timing it does not advance the crowd.
                        Predicted = _scratch, Offset = _offset, Info = _info,
                        ContactNormal = _contactNormal, NeighbourCount = _neighbourCount,
                        Diameter = s.CollisionDiameter, Omega = s.Omega,
                        MaxNeighbours = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1),
                        MinDivisor = math.max(1, s.MinDivisor), ScanRadius = r,
                        PlayerPosition = float2.zero, PlayerReach = reach
                    }.Schedule(_colourR[c], batch, h);
                return h;
            }

            JobHandle Rsqrt()
            {
                var h = default(JobHandle);
                for (var c = 0; c < colours; c++)
                    h = new SeparateColouredRsqrtJob
                    {
                        Cells = _colourR[c].AsDeferredJobArray(),
                        Predicted = _scratch, Offset = _offset, Info = _info,
                        ContactNormal = _contactNormal, NeighbourCount = _neighbourCount,
                        Diameter = s.CollisionDiameter, Omega = s.Omega,
                        MaxNeighbours = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1),
                        MinDivisor = math.max(1, s.MinDivisor), ScanRadius = r,
                        PlayerPosition = float2.zero, PlayerReach = reach
                    }.Schedule(_colourR[c], batch, h);
                return h;
            }

            JobHandle Fused()
            {
                var h = default(JobHandle);
                for (var c = 0; c < colours; c++)
                    h = new SeparateColouredFusedJob
                    {
                        Cells = _colourR[c].AsDeferredJobArray(),
                        Predicted = _scratch, Offset = _offset, Info = _info,
                        ContactNormal = _contactNormal, NeighbourCount = _neighbourCount,
                        Diameter = s.CollisionDiameter, Omega = s.Omega,
                        MaxNeighbours = math.min(s.MaxNeighbours, SeparateCompactJob.Cap - 1),
                        MinDivisor = math.max(1, s.MinDivisor), ScanRadius = r,
                        PlayerPosition = float2.zero, PlayerReach = reach
                    }.Schedule(_colourR[c], batch, h);
                return h;
            }

            // Warm, so Burst's first call and the cold caches land outside the timing.
            for (var w = 0; w < 2; w++)
            {
                Walk().Complete(); Full().Complete(); Rsqrt().Complete(); Fused().Complete();
                SimdWalk().Complete();
            }

            // Validate the mask BEFORE trusting any timing: the SIMD walk counts in-range,
            // non-self lanes, which must land on the same number the scalar walk reports.
            SimdWalk().Complete();
            simdCandidates = MeanCandidates();

            // Interleaved rather than three separate loops, so a thermal or scheduler drift over
            // the run lands on all three instead of on whichever went last.
            var watch = new System.Diagnostics.Stopwatch();
            for (var q = 0; q < reps; q++)
            {
                watch.Restart(); Walk().Complete(); walkMs += watch.Elapsed.TotalMilliseconds;
                watch.Restart(); Full().Complete(); fullMs += watch.Elapsed.TotalMilliseconds;
                watch.Restart(); Rsqrt().Complete(); rsqrtMs += watch.Elapsed.TotalMilliseconds;
                watch.Restart(); Fused().Complete(); fusedMs += watch.Elapsed.TotalMilliseconds;
                watch.Restart(); SimdWalk().Complete(); simdWalkMs += watch.Elapsed.TotalMilliseconds;
            }
            walkMs /= reps; fullMs /= reps; rsqrtMs /= reps; fusedMs /= reps; simdWalkMs /= reps;

            Walk().Complete();
            candidates = MeanCandidates();
        }

        /// <summary>Mean candidates scanned per agent, valid after TimeColoured(3, ...).</summary>
        public double MeanCandidates()
        {
            if (!_candidateCount.IsCreated || _count == 0) return 0d;
            var total = 0L;
            for (var i = 0; i < _count; i++) total += _candidateCount[i];
            return (double)total / _count;
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
            _gatherCount.Dispose();
            _candidateCount.Dispose();
            _neighbours.Dispose();
            _instances.Dispose();
            _cellOf.Dispose();
            _cellCount.Dispose();
            _offset.Dispose();
            _partialBounds.Dispose();
            _info.Dispose();
            _stats.Dispose();
            _motion.Dispose();
            _preSolve.Dispose();
            _predX.Dispose();
            _predY.Dispose();
            _hitCount.Dispose();
            _colour0.Dispose();
            _colour1.Dispose();
            _colour2.Dispose();
            _colour3.Dispose();
            for (var c = 0; c < _colourR.Length; c++) _colourR[c].Dispose();
            _allocated = false;
            _count = 0;
        }
    }
}
