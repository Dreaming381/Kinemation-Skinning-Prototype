## Kinemation Skinning Prototype

This is a Unity DOTS project built upon
[Puppet-Test](https://github.com/keijiro/PuppetTest) for the purpose of
developing a new skinning solution known as Kinemation as part of the Latios
Framework.

This prototype is **NOT** ready for production.

The procedural animation is still done with 10 reference game objects, but these
are used to drive 10k entity dancers.

The latest master commit uses DOTS 0.50 and Unity 2020.3.30f1.

## How to Test

The project contains two important scenes: *Party DOTS* and *Part DOTS
Optimized*. The former uses an entity per bone in each dancer. The latter uses
dynamic buffers to represent the bones.

Each scene contains a *Toggle* game object with two checkboxes. If *Enable
Hybrid* is checked, skinning and rendering is handled completely by the Hybrid
Renderer using compute deformations. If it is unchecked, skinning and rendering
is handled by the Kinemation solution, which uses a different compute
deformation design. If *Enable Blending* is checked, each dancer entity will
blend randomly between two of the ten reference game object-based procedural
dancers. This can be quite expensive. If unchecked, each entity dancer will copy
the pose of one of the reference procedural dancers.

### Important

Do not modify the toggle checkboxes while in play mode. The controls affect
conversion.

## Design and Goals

Kinemation was designed primarily with two goals in mind:

-   Provide an API and backend systems for driving skeletal animation from
    normal ECS systems (no animation graph required).
-   Improve GPU performance by sacrificing some CPU performance.

In Kinemation, skinning is driven by skeletons, and there are two types:

-   Exposed – Each bone is an Entity
    -   Intuitive for programmers and saves development time
    -   Much more viable with DOTS than with game objects
    -   Automatic hierarchy updates via Transform System
    -   Great for procedurally-generated data
-   Optimized – Bones are stored in packed Dynamic Buffers
    -   Faster on CPU than Exposed
    -   Better data locality when animation logic is simple
    -   Hierarchy updates dispatched manually
    -   Exported bone support
    -   Great for fully authored data

### Differences from Unity’s Animation and Hybrid Renderer Packages

For optimized hierarchies (what the animation package always uses), Unity stores
the bind poses and hierarchy information in dynamic buffers, while Kinemation
stores this information in blob assets.

Unity requires meshes be marked as Read-Write enabled at runtime. Kinemation
only requires read-write access at conversion time, as it stores skinning data
in a blob asset using a custom compute-optimized data structure.

Unity handles bone hierarchies in root space, and reparents all skinned meshes
to the root. This matches the Skinned Mesh Renderer behavior of game objects.
Kinemation operates in skeletal reference space and leaves the hierarchy as is.

Unity uses `Immutable` compute buffers and creates a compute shader dispatch for
each unique mesh. Kinemation uses `SubUpdate` compute buffers for per-frame data
and a single compute shader dispatch can process many different meshes at once.

### Unity Exclusive Features

Unity’s solution currently supports Blend Shapes. These are not currently
supported in Kinemation but will be in a future release. Similarly, Unity
supports 4-bone vertex skinning. Kinemation is compute-only.

Unity has occlusion-culling. Kinemation disables this for the entire project.
More research is required to make it work correctly with Kinemation.

Lastly, Unity has fully animation clip sampling and blending system with
authoring tools and workflows. This prototype version does not have anything
like that. The first release version will have simple code-driven animation clip
support. Unlike Unity, runtime conversion will be supported (assuming that’s
still a thing by then).

### Kinemation Exclusive Features

Kinemation has true frustum culling support for skeletons. This means that
animated meshes will have proper bounds updated every frame. In addition, only
meshes that pass culling and LOD selection will be skinned on the GPU. Note that
frustum culling is per-skeleton rather than per-mesh.

Kinemation has a rebinding system which allows for fast and intuitive binding
and rebinding of meshes to skeletons at runtime as long as the mesh was designed
for the same number of bones as the skeleton. This is useful for things like
character customizations where all customizations are skinned to the same base
skeleton in the DCC package.

Kinemation does not require any bones to be exposed during conversion of an
optimized skeleton.

## How It Works

Kinemation has five primary entity archetypes:

-   Skinned RenderMesh
-   Exposed Bone
-   Exposed Skeleton
-   Exported Bone
-   Optimized Skeleton

The data associated with these archetypes are designed to minimize random memory
accesses where possible.

It is also worth noting that Kinemation makes use of several features of the
Latios Framework. Also, the current systems are not all placed in the most
correct locations in the player loop for simplicity of prototyping. However, now
that the design is mostly complete, this section will typically call out the
intended correct locations for these systems.

### Conversion

Currently, a single conversion system is responsible for generating all
components and blobs. Each Skinned Mesh Renderer gets classified as belonging to
an exposed skeleton, an optimized skeleton, or an invalid skeleton. Conversion
then typically involves converting the entire skeletons and all skinned meshes
using it at once. The shared meshes get converted separately to blobs.

For optimized skeletons, the bone hierarchy data needs to be extracted. A
temporary Game Object gets instantiated and
`AnimatorUtility.DeoptimizeGameObjectHierarchy()` exposes all the underlying
transforms as `GameObjects` for analysis. Currently, a hack component exists for
identifying exposed bones, and this solution likely won’t work for prefabs
referenced in subscenes.

### Binding Reactive System

`SkeletonMeshBindingReactiveSystem` is the first system to process converted
entities at runtime. As its name suggests, this is a reactive system. It
generates a sync point when necessary and reacts to new and deleted skeletons as
well as new and deleted meshes. It also reacts to rebindings via changes to the
`BindSkeletonRoot` component, although this does not incur a sync point.
Existence and bindings are assumed to be relatively persistent, so this system
tries to perform many expensive random-access operations in order to avoid such
operations in per-frame updates.

One of these operations is skinning source data allocation inside the two mesh
compute buffers. One compute buffer stores the mesh vertices, while the other
stores the weights. New and dead mesh blobs get collected into an
`UnsafeParallelBlockList` and an `IJob` processes this during the sync point.
This job maintains a `MeshGpuManager` collection component which handles all the
occupied and free ranges of meshes as well as reference counts. It also
generates an upload command list for a future system.

The second operation is exposed skeleton culling index linking. Each unique
exposed skeleton gets assigned an index which needs to be synchronized with all
of its exposed bones. These indices will be used to index bit arrays for
optimally passing culling information from bones to the skeleton, avoiding
expensive cache-thrashing random accesses.

The third operation is the actual binding changes between meshes and skeletons.
These are also recorded in an `UnsafeParallelBlockList`. The recorded operations
are sorted and batched during the sync point. Then after the sync point, a job
runs through all skeletons and updates all data related to the bindings.
Skeletons keep a list of bound mesh entities in the dynamic buffers. They also
cache references to GPU memory for the mesh skinning source data. And lastly,
each mesh blob contains bounding spheres for the mesh for each bone. The
skeletons use this to bake bounding spheres for all bound meshes for each bone.
In the case of exposed skeletons, this also gets forwarded to all the individual
bones via random access.

This system is set to run in the presentation structural changes system group.
However, it can be run multiple times per frame at different locations if moving
its sync point is beneficial.

### Optimized Skeleton Bone Export

Optimized skeletons have the ability to export bone transforms to external
entities. Currently, this export is done using a `LocalToWorld` `[WriteGroup]`
override. However, this will be replaced to use `LocalToParent` for the official
release. The change will remove the need for a system like
`TempFixExportedTransformsSystem` to be scheduled so precariously around the
`TransformSystemGroup` when exported bones have children and skeletons have
parents.

### Skeleton and Bone Bounds Update

`SkeletonBoundsUpdateSystem` is responsible for updating both optimized skeleton
and exposed bone bounds. Optimized skeletons calculate a world-space AABB for
the entire skeleton hierarchy. Exposed bones calculate a world-space AABB for
each bone individually. Both archetypes update a chunk bounds component when
necessary. Change and order version filtering are both used.

### Compute Deform Buffer Allocation

Kinemation makes use of the same Shader variables and skinning buffer format as
Unity. That means that any shader graph shader using the Compute Deformation
node or any custom shader compatible with Hybrid Renderer’s compute deformation
skinning will be compatible with Kinemation. Each `RenderMesh` gets a material
instance property with an index into a compute deformation vertices buffer. This
buffer reserves memory for all deformed vertices for all deforming `RenderMesh`
instances in the ECS World. A downside to this is that this buffer gets huge and
wasteful. More research is needed to resolve this.

Because the buffer memory is valuable, indices are reassigned every frame to
reduce fragmentation. `ChunkComputeDeformMemoryMetadata` maintains state for
this process. `UpdateChunkComputeDeformMetadataSystem` looks for changed meshes
or structural changes and caches vertex counts and chunk counts in the chunk
component. These results are used by `AllocateDeformedMeshesSystem` which
iterates the meta-chunks in a single-threaded job and computes prefix sums at
the chunk level. Iterating over meta-chunks like this is incredibly fast and
effectively removes the performance penalty of a single-threaded prefix sum.
Once the prefix sums are computed, a parallel job distributes unique vertices to
the individual entities.

It is worth noting the buffer index assignment assigns lower indices to meshes
rendered the previous frame. This was an experiment and currently serves no
practical purpose.

### Per Frame Setup

The Hybrid Renderer uses the `BatchRendererGroup` API to provide drawing
information into the engine. This API invokes a culling callback each time
culling needs to occur. Culling occurs multiple times per frame, usually while
the Scriptable Render Pipeline code is executing. When profiling play mode in
the editor, three callbacks happen per frame. One is for the main camera, one is
for shadows, and one is for the scene view.

Kinemation dispatches skinning during these culling callbacks in order to avoid
skinning invisible meshes. Skinning only has to happen once per visible mesh
across all culling callbacks, so Kinemation caches skinning state in components
on both skeleton and mesh entities. This data needs to be reset each frame prior
to culling callbacks. `ClearPerFrameSkinningDataSystem` performs this.

Skeletons cache which bone matrix buffer and bone offsets they used when first
skinning a batch of meshes. This is useful when different mesh LODs bound to the
same skeleton are selected during different culling callbacks.

Meshes store three Booleans in a byte-sized component. One of these is the
selected LOD. This never gets cleared and is kept up-to-date in the LOD
selection system. The second is whether the mesh should be rendered for a given
callback. This is cleared for every callback and also at the beginning of the
frame. The third flag specifies whether the mesh was rendered at least once
during the frame by a previous callback. When this flag is set, the mesh has had
a skinning dispatch for the frame.

### Source Mesh Data Upload

All Compute Buffers are pooled using a type called `ComputeBufferTrackingPool`.
A Collection Component wraps this and exists on the `worldBlackboardEntity`.
`BeginPerFrameMeshSkinningBuffersUploadSystem` is responsible for creating this.
It updates the pool using async readback fences to ensure `SubUpdate` buffers
get recycled properly. It then reserves mesh buffer sizes and populates the
source mesh upload buffers in jobs. However, it does not submit the buffers, but
rather leaves that state in a `MeshGpuUploadBuffers` collection component for
the next user to finish and dispatch. Usually, the buffers writes are committed
and the upload compute shaders are dispatched during the first culling callback.
However, if no culling callback is performed, this system will perform these
steps at the beginning of the next frame to ensure the GPU stays in sync with
the CPU allocation management.

### Latios Hybrid Renderer Replacement

Unfortunately, Unity’s Hybrid Renderer does not support custom culling. This is
something they are working on, but `Kinemation` couldn’t wait. The solution is
to replace Unity’s Hybrid Renderer with a custom one using a different culling
callback.

This replacement does several things differently. First, it forces all batch
bounds to the max extents (1e9f) for any chunks containing skinned meshes.

Second, it creates the `KinemationCullingSuperSystem`.

And third, in the culling callback, it places all the callback arguments into a
collection component and then invokes `Update()` on the
`KinemationCullingSuperSystem`. Once the update finishes, it returns the
collection component’s `JobHandle`.

### Skeleton LODs Update

Inside the culling callback, the first system to run is
`UpdateSkinnedLODsSystem`. This system runs what is essentially the LOD update
job from Unity’s Hybrid Renderer. However, there are a few modifications.

First, the LODs have to be propagated to the per-mesh-entity flags so skeleton
culling can easily look them up via entity in a job.

Second, Unity’s implementation had some issues managing state and change filters
using `EntityQuery` dependencies. But with a proper system, this could be
corrected. Admittedly, these fixes haven’t really been tested yet. But the
opportunity is real.

### Per Camera Setup

The second system to run is `ClearPerCameraFlagsSystem`. And by this point, it
should be pretty obvious what this system does. It clears the per-camera flag on
the byte-sized component attached to skinned meshes.

What is less obvious is that it also assigns its `GlobalSystemVersion` to an ICD
on the `worldBlackboardEntity`. This allows future systems to detect changes to
the flags relative to this system.

### Frustum Culling and Skinning

`SkeletonFrustumCullingAndSkinningDispatchSystem` should probably be split into
multiple systems. But for now, it exists as a mega system and does many things.

First, it performs culling on exposed bones. Bones which are visible mark a bit
in a per-thread `UnsafeBitArray` associated with their exposed skeleton culling
index, which was set up by the binding reactive system. For 10k skeletons, each
thread has 1.3 kB of bits to potentially mark. This easily fits in L1 cache
making these random-access marks extremely cheap. And that’s important, because
these marks have to happen for each bone. For a skeleton that is visible, nearly
all bones in it will have to mark their visibility.

After this job, the `UnsafeBitArrays` get collapsed into one using bitwise-OR
operations.

The third job is `CullAndCollectMeshMetadataJob`. This job identifies visible
skeletons and queues up skinning metadata commands. For exposed skeletons, it
uses the `UnsafeBitArray` to determine visibility. For optimized skeletons, it
performs frustum culling directly on the optimized skeleton bounds. For each
exposed skeleton, it looks up all mesh flags and checks their state. Meshes yet
to be skinned have the skeleton index in chunk and the mesh dynamic buffer index
stored in a `NativeStream` along with the mesh’s Compute Deform material
property index. For all visible meshes with enabled LODs, flags are updated to
mark the mesh as visible per frame and per camera. The job also stores counts
relative to the number of meshes requiring skinning as well as which bone matrix
buffer to use.

The fourth and fifth jobs prefix sum the counts so that a metadata compute
buffer can be populated with the correct offsets in parallel.

The sixth job is `WriteBuffersJob`, and it calculates the skin matrices and
writes them to a compute buffer. It also writes the metadata buffer for
specifying offsets to the skeletons and meshes the compute shader needs to
index. All of this data has either been calculated via the prefix sums or was
cached in the skeleton. For exposed skeletons, this job pays the cost of
random-accessing the `LocalToWorld` of all the individual bones, but it only
does so for skeletons that require skinning.

While all these jobs are running, the main thread stays busy juggling collection
components, dispatching mesh uploads, and priming the compute skinning shader.
Finally, it force-completes all the jobs so that it can end the writes to the
compute buffers and dispatch the compute skinning shader. It finally binds the
deformed data to the global buffer.

### Unskinned LODs Update

Because skinning requires force-completing jobs, unskinned LODs update in a
separate system after skinning dispatch. This is the same as the skinning LOD
update, except there are no skinning-specific flags to update.

### Batch Renderer Group Visibilities

The last system to run is `CullUnskinnedRenderersAndUpdateVisibilitiesSystem`.
This system does the frustum culling logic that the Hybrid Renderer normally
does. However, it only does this check for the unskinned meshes. The Hybrid
Renderer updates the batch renderer group visibilities in the frustum culling
job, so this job does the same. However, for skinned meshes, instead of chunk
bounds and world bounds test, this job uses the change version since the flag
clearing and then checks the flags for chunks that changed.

One other aspect to this system is that it uses temp memory allocated in the job
for scratch indices rather than allocate all the memory prior to the job. This
is a little faster.

### The Batch Skinning Shader

With all the systems discussed, there’s only one piece left in the puzzle, and
that is the compute shader. This thing is weird, but fast. It takes advantage of
two aspects common to most GPUs.

First, many GPUs have dedicated on-chip memory which maps to HLSL’s
`groupshared` variables. In DX11, this memory is required to be 32 kB per thread
group. That is enough to fit 682 bone matrices. So as an optimization, each
skeleton is assigned a threadgroup. Skeletons with less than 683 bones will
store the matrices in `groupshared` memory at the beginning of the shader and
then run a group barrier. After this, the thread group processes all meshes
bound to the skeleton, using the cached matrices for fast lookups. This
optimization is especially fast on AMD and Nvidia GPUs. But modern Adreno
(Qualcomm) GPUs, PowerVR GPUs (older iOS devices), and Apple designed GPUs
(modern iOS and Apple Silicon) all have this hardware. Even very recent intel
integrated graphics adopted this on-chip memory in their GEN11 architecture.
That just leaves ARM Mali and older Intel integrated GPUs where this
optimization doesn’t work. But that can be detected at runtime so Kinemation can
use a slightly different shader for those cases. This isn’t implemented yet in
the prototype. One other caveat is that the threadgroup size is set to 1024
threads, which is optimal for desktop GPUs but might be too high for mobile.
Fortunately, the shader can be easily tweaked to work with smaller thread
groups.

The second optimization is with how bone weights are stored. Traditionally, all
bone weights for a given vertex are stored adjacent in memory. A second buffer
specifies the starts and counts for the weights. There are two problems with
this. The first is that we need two buffers just for weights. The second is that
this thrashes GPU caches. Unlike CPU caches, the GPU caches are designed for
throughput and coalesce reads and writes across multiple GPU threads. In other
words, data locality is emphasized and temporal locality is not really a thing.
A batch of threads in a thread group work in lockstep for evaluating the first
weight, and then the second, and then the third, and so on. So naturally, the
GPU could benefit if all the first weights for a batch of threads were packed
together, and then the second, and so on. And so that is how Kinemation stores
the weights. It steals the high bits of the bone index to encode linked-list
offsets to the next weights for a given vertex. It also uses a negative weight
to signal that it is the last weight. And batches of vertices are prefixed with
batch linked list to help threads along. Much of this logic relies on a shared
threadgroup scalar processor which many GPUs also have and get further speedups
from. This is known as “Scalarization”.

My personal GPU is an R9 390, which is very similar to the GPU in the PS4 Pro.
One thing about these GCN GPU architectures is that to get good GPU utilization
with max `groupshared` memory usage, only 32 vector registers (VGPRs) can be
used. Unlike Unity’s compute shader which uses 36, this one uses between 26 and
28\. To be fair, Unity uses smaller threadgroup sizes where this doesn’t matter
as much. But still, achieving high utilization is possible, even when processing
multiple meshes in a single threadgroup.

One last thing, each bone weight has 7 unused bits that I don’t know what to do
with. Any ideas?

## Performance

Regarding performance, while I made a strong effort to keep CPU costs down as
much as possible, my goal was to improve GPU performance.

### GPU Performance

The benchmark only involves one skinned mesh per skeleton with that mesh being
shared by all entities. This is best-case-scenario for the Hybrid Renderer
skinning. Yet despite this, between 1k and 10k dancers, the simulation will
become GPU-bound and the main thread will block on compute buffer uploads. At
10k dancers, frames take between 25 and 30 milliseconds on my system.

But with Kinemation, the frustum culling significantly reduces the GPU load. 10k
dancers are CPU-bound running well-above 60 FPS. And Windows reports GPU
utilization at only 50% compared to the 100% with the Hybrid Renderer. The
Hybrid Renderer does perform frustum culling on the mesh instances, so this
difference is entirely from compute skinning.

I also tested a slightly modified simulation where frustum culling was disabled
to compare raw skinning performance. Kinemation skinning is 1.5x faster.

### CPU Performance

This is where things get tricky. When not GPU-bound, Kinemation performs worse.
But that’s not because the algorithms are slower. It is because Kinemation is
doing a lot more work per frame. It is doing bounds updates for all the bones in
the skeleton, which adds up to 220k bones. It also endures a significant
constant overhead in the culling callbacks since it runs multiple systems in
there.

One possible optimization may be to replace the exposed bone culling algorithm
to use bounding spheres instead of bounding boxes.

Another optimization would be running more main-thread logic in Burst.

Lastly, dynamic buffer capacities were left at default values. These should
probably be tuned to reflect common use cases.

In any case, if you have ideas to further improve performance, please reach out
to me!

## What’s Next?

There is quite a bit of work remaining before Kinemation is ready for release.
Here are the planned changes:

-   Basic animation clip support
-   Skinning shader variants for up to 32k bones
-   Skinning shader variants for specific GPUs
-   Full renaming pass to improve on API consistency
-   Properly ordered systems and an installation mechanism
-   Removal of the Hybrid Renderer skinning mode
-   Support for more prefab configurations
-   SubScene and optimization support
-   Exported bones using `LocalToParent` instead of `LocalToWorld`
-   Project configuration validation
-   Documentation

## Licenses

Kinemation and Latios Framework are licensed under the Unity Companion License.

Everything else including all assets in Assets/_Code and Assets/Scenes and
Prefabs is [CC0](https://creativecommons.org/publicdomain/zero/1.0/).
