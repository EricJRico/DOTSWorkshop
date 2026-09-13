# Moving 20,000 enemies: where the cost actually is

Unity 6000.3.22f1, URP, editor play mode, `GameObjectBaseline.unity`, 20,000 enemies unless
stated. Every number is a mean over 120 frames, read back through `ProfilerRecorder` from the
same markers the Profiler shows (`_logUpdateCost` on the Enemies object prints the table).

The question that started it: should Block A's `IJob` become `IJobParallelForTransform`? The
`IJob` version was already Bursted down to ~0.05 ms, and the copy in/copy out either side of
`Schedule` was the largest cost.

## Summary

| version | `EnemyMover.Update` | main-thread frame | draw calls |
|---|---|---|---|
| `IJobParallelForTransform`, wait in same `Update` | 0.85 ms | 7.5 ms | 2433 |
| same, wait in `LateUpdate` | 0.77 ms | 7.5 ms | 2433 |
| same, wait at top of next `Update` | 0.03 ms | 7.5 ms | 2433 |
| no GameObjects, `RenderMeshInstanced` | 0.18 ms | 2.8 ms | 53 |

The last row is the current state of the branch.

## 1. `IJobParallelForTransform` removes the copy, not the Transform

Handing the job a `TransformAccessArray` deletes both copy loops — the array is built once in
`Start`, and `Execute(int, TransformAccess)` reads and writes the Transform in place. It does not
help as much as the deleted copy suggests, because the Transform itself is the cost.

Breaking `Update` into four markers at 20,000 enemies:

| | Clear | Schedule | Complete | Collect | total |
|---|---|---|---|---|---|
| enemies parented, 1 root | 0.003 | 0.017 | 0.85 | 0.003 | 0.87 |
| enemies unparented, 20,000 roots | 0.003 | 0.60 | 0.24 | 0.003 | 0.86 |
| 8 group parents | 0.003 | 0.02 | 1.35 | 0.007 | 1.38 |
| unparented + `desiredJobCount` | 0.003 | 0.69 | 0.25 | 0.004 | 0.95 |

Rearranging the hierarchy moves the cost between `Schedule` and `Complete` and never removes it.

**Why.** A transform job is handed one range per root hierarchy — writes into a hierarchy's shared
data cannot be split across threads. `IJobParallelForTransform.cs` shows the read-write path using
`JobsUtility.GetJobRange` over arrays that native returns already sorted by root
(`GetSortedTransformAccess` / `GetSortedToUserIndex` in `TransformAccessArray.bindings.cs`). So:

- One parent → one range → the whole crowd runs on a single worker, and the main thread stands in
  `Complete` for 0.85 ms.
- One root each → all the workers, but the main thread builds 20,000 ranges in `Schedule` for
  0.60 ms.
- Grouping into 8 was worse, not better: the extra hierarchy level costs more per write than the
  8-way split saves.
- `desiredJobCount` on the `TransformAccessArray` constructor changed nothing measurable.

The per-enemy cost inside the job is not the maths. `TransformAccess.position` get and set are
`extern` native bindings (`TransformAccessBindings::GetPosition` / `SetPosition`, both
`ThrowsException = true`), so Burst cannot inline or vectorise them. The 0.05 ms `IJob` figure
already proved the arithmetic is free.

## 2. Getting the wait off the main thread

The job is scheduled at the end of `Update` and completed at the top of the *next* `Update`, so it
runs on a worker across the whole frame.

| where the wait happens | main thread |
|---|---|
| same `Update` | 0.85 ms |
| `LateUpdate`, same frame | 0.75 ms |
| next `Update`, one frame later | 0.03 ms |

`LateUpdate` recovers almost nothing, because there is only ~0.1 ms of main-thread work between
`Update` and `LateUpdate` in this scene and the job needs 0.85 ms on its one worker. Same-frame and
off-the-main-thread are mutually exclusive here: finishing inside the frame requires splitting
across workers, which requires many root hierarchies, which is the 0.6 ms `Schedule` bill.

Cost: enemy positions are one frame behind the player. Invisible on a crowd.

## 3. Deleting the Transform

The enemies are no longer GameObjects. `EnemySpawner.Spawn` returns a `NativeArray<float3>` and
never calls `Instantiate`; it reads the mesh, material and scale off the prefab once and builds one
material per colour. `EnemyMover` runs a plain `IJobParallelFor` that writes the position it
simulates and the `Matrix4x4` the renderer draws from in the same pass, and draws the crowd with
`Graphics.RenderMeshInstanced`.

| | Clear | Schedule | Complete | Collect | Draw | total |
|---|---|---|---|---|---|---|
| 20,000 enemies | 0.002 | 0.012 | 0.002 | 0.004 | 0.16 | 0.18 |

`Update` reads *higher* than the deferred transform version (0.18 vs 0.03) because `Draw` is now
inside `EnemyMover`. In the Transform versions that work still happened — it was the renderer's own
culling and draw submission for 20,000 MeshRenderers, and it is most of what the 7.5 ms
main-thread frame was. The honest comparison is the frame: **7.5 ms → 2.8 ms**.

### `RenderParams` must be built with its constructor

The first attempt rendered nothing at all, with no error. `RenderParams` is a struct whose
constructor is what fills in `renderingLayerMask = RenderingLayerMask.defaultRenderingLayerMask`
and `forceMeshLod = -1` (`Graphics.cs:180`). An object initializer on a default-constructed one
leaves the rendering layer mask at 0 and everything is culled silently.

```csharp
// wrong - culls everything, no error
var rp = new RenderParams { material = mat, worldBounds = bounds };

// right
var rp = new RenderParams(mat) { worldBounds = bounds };
```

Every use in Unity's own source (`PreviewRenderUtility.cs:501`) and all three renderers in OVERRUN
(`BloodCardRenderer.cs:414`, `BloodBurstRenderer.cs:179`, `DamageNumberRenderer.cs:180`) use the
constructor form. `worldBounds` must also be set — nothing computes it once there are no renderers
in the scene, and an empty bounds culls the batch.

## 4. Why 53 draw calls, and how to get to 1

`RenderMeshInstanced` does not draw 20,000 instances in one call. Measured:

| enemies | draw calls |
|---|---|
| 2,000 | 16 |
| 10,000 | 32 |
| 20,000 | 53 |

That fits `12 + n/500` exactly. The 12 is the rest of the scene; every 500 enemies costs one more
call, regardless of how many materials the crowd is split into.

**Why 500.** Classic GPU instancing puts per-instance data in a *constant buffer*. Each instance
needs `unity_ObjectToWorld` and `unity_WorldToObject` — 64 bytes each, 128 total — and a constant
buffer is capped at 64 KB, so 512 instances fit, minus a couple of reserved slots.
`RenderMeshInstanced` chunks the array to fit and issues one call per chunk. No argument you pass
it changes this; the limit is the buffer the data travels in.

### The fix: `RenderMeshIndirect` with a `GraphicsBuffer`

Move the per-instance data out of the constant buffer and into a `StructuredBuffer`, which has no
such cap, and issue one indirect command. This is the path `BloodCardRenderer` and
`DamageNumberRenderer` already take in OVERRUN.

**Not yet measured.** Everything above this heading was run; this section is the mechanism and the
code shape, and should be measured the same way once written.

C# side:

```csharp
// once
_instances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, InstanceStride);
_commands  = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1,
                                GraphicsBuffer.IndirectDrawIndexedArgs.size);
_commandData = new GraphicsBuffer.IndirectDrawIndexedArgs[1];
_mpb = new MaterialPropertyBlock();

// per frame
_commandData[0] = new GraphicsBuffer.IndirectDrawIndexedArgs
{
    indexCountPerInstance = _mesh.GetIndexCount(0),
    instanceCount         = (uint)count,
    startIndex            = 0,
    baseVertexIndex       = 0,
    startInstance         = 0,
};
_commands.SetData(_commandData);

_mpb.SetBuffer(IdInstances, _instances);

var rp = new RenderParams(_material) { worldBounds = _bounds, matProps = _mpb };
Graphics.RenderMeshIndirect(rp, _mesh, _commands, 1);
```

The instance struct carries whatever the shader needs. For this crowd that is a position and a
colour index, which is 16 bytes instead of the 128 a pair of matrices costs — the enemies never
rotate, so there is no reason to ship a matrix at all:

```csharp
struct EnemyInstance   // InstanceStride = 16
{
    public float3 Position;
    public uint   Colour;
}
```

Writing it: the move job already computes the position, so it writes `NativeArray<EnemyInstance>`
in place of `NativeArray<Matrix4x4>`, and the array goes to the GPU with
`_instances.SetData(_instanceArray)`. That is one upload of 320 KB a frame instead of four
submissions of 1.28 MB of matrices. Better still, allocate the `GraphicsBuffer` with
`GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination` and use
`BeginWrite`/`EndWrite` so the job writes straight into mapped memory with no copy at all.

**Colour without a second draw.** Putting `Colour` in the instance data is what collapses the four
material draws into one: the shader indexes a small `float4 _Colours[4]` (or a second, tiny
`StructuredBuffer`) by `inst.Colour`. The crowd no longer needs to be sorted by colour at spawn, and
`MaterialCounts` / the per-material `RenderParams[]` in `EnemyMover` go away.

Shader side — the parts that are easy to get wrong, taken from `BloodCard.shader`:

```hlsl
#pragma target 4.5
#pragma multi_compile_instancing

#define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
#include "UnityIndirect.cginc"

StructuredBuffer<EnemyInstance> _Instances;

struct Attributes
{
    uint vertexID   : SV_VertexID;
    uint instanceID : SV_InstanceID;
};

Varyings Vert(Attributes input)
{
    InitIndirectDrawArgs(0);
    uint vertexID   = GetIndirectVertexID(input.vertexID);
    uint instanceID = GetIndirectInstanceID(input.instanceID);

    EnemyInstance inst = _Instances[instanceID];
    ...
}
```

`InitIndirectDrawArgs` and the two `GetIndirect*` accessors are not optional:
`GetIndirectInstanceID` corrects `SV_InstanceID` for `startInstance`, which Vulkan includes in the
semantic and D3D does not — skip it and the crowd is fine on desktop D3D and wrong everywhere else.
`SV_InstanceID` is declared as an explicit field rather than via
`UNITY_VERTEX_INPUT_INSTANCE_ID` because that macro expands to nothing in the variant where no
instancing keyword is set, and the raw id is needed in every variant.

Expected result: **1 draw call for all 20,000 enemies**, and the main thread submits one command
with no per-instance payload. The `Draw` marker should fall from 0.16 ms to near nothing.

## What this means for the workshop

Block A ends on a real number: with GameObjects, 20,000 enemies cost ~0.86 ms of main thread that
no job arrangement removes, because the cost is the Transform and the hierarchy around it, not the
loop. Deferring the wait a frame hides it; deleting the Transform removes it.

The shape the crowd ends up in — positions in a flat array, a parallel job over them, a renderer
reading a buffer — is the shape ECS hands you by default. Entities Graphics does not use
`RenderMeshInstanced` or `RenderMeshIndirect` at all; it registers a `BatchRendererGroup` with its
own culling callback (`EntitiesGraphicsSystem.cs:1010`) and has no per-frame main-thread draw
submission. Doing it by hand in Block A is the bridge to that.
