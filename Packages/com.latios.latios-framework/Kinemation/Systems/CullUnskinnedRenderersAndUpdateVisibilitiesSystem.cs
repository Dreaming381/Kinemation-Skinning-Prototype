using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine.Rendering;

// Unlike the LOD system, Unity's culling implementation is much more sensible.
// There's a couple of oddities that I have corrected, such as using Intersect2NoPartial in opportune locations
// and using Temp memory for the ThreadLocalIndexLists.
// But otherwise, most modifications are for shoehorning the skinning flags.
namespace Latios.Kinemation.Systems
{
    [DisableAutoCreation]
    public partial class CullUnskinnedRenderersAndUpdateVisibilitiesSystem : SubSystem
    {
        EntityQuery m_query;

        protected override void OnCreate()
        {
            m_query = Fluent.WithAll<ChunkWorldRenderBounds>(true).WithAll<HybridChunkInfo>(false).WithAll<ChunkHeader>(true).Build();
        }

        protected unsafe override void OnUpdate()
        {
            var flagsClearSystemVersion = worldBlackboardEntity.GetComponentData<PerCameraClearFlagsSystemChangeVersion>();
            var brgCullingContext       = worldBlackboardEntity.GetCollectionComponent<BrgCullingContext>();

            var planePackets = FrustumPlanes.BuildSOAPlanePackets(brgCullingContext.cullingContext.cullingPlanes, Allocator.TempJob);

            Dependency = new ZeroVisibleCountsJob
            {
                Batches = brgCullingContext.cullingContext.batchVisibility
            }.ScheduleParallel(brgCullingContext.cullingContext.batchVisibility.Length, 16, Dependency);

            Dependency = new SimpleCullingJob
            {
                flagsHandle                      = GetComponentTypeHandle<SkinningRenderCullingFlags>(true),
                computeDeformMetadataHandle      = GetComponentTypeHandle<ChunkComputeDeformMemoryMetadata>(false),
                flagsClearSystemVersion          = flagsClearSystemVersion.capturedWorldSystemVersion,
                Planes                           = planePackets,
                InternalToExternalRemappingTable = brgCullingContext.internalToExternalMappingIds,
                BoundsComponent                  = GetComponentTypeHandle<WorldRenderBounds>(true),
                HybridChunkInfo                  = GetComponentTypeHandle<HybridChunkInfo>(false),
                ChunkHeader                      = GetComponentTypeHandle<ChunkHeader>(true),
                ChunkWorldRenderBounds           = GetComponentTypeHandle<ChunkWorldRenderBounds>(true),
                IndexList                        = brgCullingContext.cullingContext.visibleIndices,
                Batches                          = brgCullingContext.cullingContext.batchVisibility,
            }.ScheduleParallel(m_query, Dependency);
        }

        [BurstCompile]
        unsafe struct ZeroVisibleCountsJob : IJobFor
        {
            public NativeArray<BatchVisibility> Batches;

            public void Execute(int index)
            {
                // Can't set individual fields of structs inside NativeArray, so do it via raw pointer
                ((BatchVisibility*)Batches.GetUnsafePtr())[index].visibleCount = 0;
            }
        }

        [BurstCompile]
        unsafe struct SimpleCullingJob : IJobEntityBatch
        {
            [ReadOnly] public ComponentTypeHandle<SkinningRenderCullingFlags> flagsHandle;
            public ComponentTypeHandle<ChunkComputeDeformMemoryMetadata>      computeDeformMetadataHandle;
            public uint                                                       flagsClearSystemVersion;

            [DeallocateOnJobCompletion][ReadOnly] public NativeArray<FrustumPlanes.PlanePacket4> Planes;
            [ReadOnly] public NativeArray<int>                                                   InternalToExternalRemappingTable;

            [ReadOnly] public ComponentTypeHandle<WorldRenderBounds>      BoundsComponent;
            public ComponentTypeHandle<HybridChunkInfo>                   HybridChunkInfo;
            [ReadOnly] public ComponentTypeHandle<ChunkHeader>            ChunkHeader;
            [ReadOnly] public ComponentTypeHandle<ChunkWorldRenderBounds> ChunkWorldRenderBounds;

            [NativeDisableParallelForRestriction] public NativeArray<int>             IndexList;
            [NativeDisableParallelForRestriction] public NativeArray<BatchVisibility> Batches;
            public const uint                                                         kMaxEntitiesPerChunk = 128;
            [NativeDisableContainerSafetyRestriction] NativeArray<int>                ThreadLocalIndexLists;

#pragma warning disable 649
            [NativeSetThreadIndex] public int ThreadIndex;
#pragma warning restore 649

            public void Execute(ArchetypeChunk archetypeChunk, int chunkIndex)
            {
                var hybridChunkInfoArray = archetypeChunk.GetNativeArray(HybridChunkInfo);
                var chunkHeaderArray     = archetypeChunk.GetNativeArray(ChunkHeader);
                var chunkBoundsArray     = archetypeChunk.GetNativeArray(ChunkWorldRenderBounds);

                NativeArray<ChunkComputeDeformMemoryMetadata> computeDeformMetadataArray = default;
                bool                                          hasSkinning                = archetypeChunk.Has(computeDeformMetadataHandle);
                if (hasSkinning)
                {
                    computeDeformMetadataArray = archetypeChunk.GetNativeArray(computeDeformMetadataHandle);
                }

                if (!ThreadLocalIndexLists.IsCreated)
                {
                    ThreadLocalIndexLists = new NativeArray<int>((int)kMaxEntitiesPerChunk, Allocator.Temp);
                }

                for (var entityIndex = 0; entityIndex < archetypeChunk.Count; entityIndex++)
                {
                    var hybridChunkInfo = hybridChunkInfoArray[entityIndex];
                    if (!hybridChunkInfo.Valid)
                        continue;

                    var chunkHeader = chunkHeaderArray[entityIndex];
                    var chunkBounds = chunkBoundsArray[entityIndex];

                    int internalBatchIndex = hybridChunkInfo.InternalIndex;
                    int externalBatchIndex = InternalToExternalRemappingTable[internalBatchIndex];

                    int chunkOutputOffset    = 0;
                    int instanceOutputOffset = 0;

                    ref var chunkCullingData = ref hybridChunkInfo.CullingData;

                    var pBatch = ((BatchVisibility*)Batches.GetUnsafePtr()) + externalBatchIndex;

                    int batchOutputOffset      = pBatch->offset;
                    int processedInstanceCount = chunkCullingData.BatchOffset;

                    var chunkInstanceCount    = chunkHeader.ArchetypeChunk.Count;
                    var chunkEntityLodEnabled = chunkCullingData.InstanceLodEnableds;
                    var anyLodEnabled         = (chunkEntityLodEnabled.Enabled[0] | chunkEntityLodEnabled.Enabled[1]) != 0;

                    if (hasSkinning)
                        anyLodEnabled &= chunkHeader.ArchetypeChunk.DidChange(flagsHandle, flagsClearSystemVersion);

                    if (anyLodEnabled)
                    {
                        var perInstanceCull = 0 != (chunkCullingData.Flags & HybridChunkCullingData.kFlagInstanceCulling);

                        var chunkIn = perInstanceCull ?
                                      FrustumPlanes.Intersect2(Planes, chunkBounds.Value) :
                                      FrustumPlanes.Intersect2NoPartial(Planes, chunkBounds.Value);

                        if (chunkIn == FrustumPlanes.IntersectResult.Partial || hasSkinning)
                        {
                            // Output to the scratch area first, then atomic allocate space for the correct amount of instances,
                            // and finally memcpy from the scratch to the real output.
                            int* scratch = ((int*)ThreadLocalIndexLists.GetUnsafePtr());

                            var chunk = chunkHeader.ArchetypeChunk;

                            if (hasSkinning)
                            {
                                var skinningFlags         = chunk.GetNativeArray(flagsHandle);
                                var computeDeformMetadata = computeDeformMetadataArray[entityIndex];

                                for (int j = 0; j < 2; j++)
                                {
                                    var        lodWord            = chunkEntityLodEnabled.Enabled[j];
                                    BitField64 renderedThisCamera = default;

                                    while (lodWord != 0)
                                    {
                                        var bitIndex   = math.tzcnt(lodWord);
                                        var finalIndex = (j << 6) + bitIndex;

                                        scratch[instanceOutputOffset] = processedInstanceCount + finalIndex;

                                        bool renderThis = (skinningFlags[finalIndex].flags & SkinningRenderCullingFlags.renderThisCamera) != 0;
                                        renderedThisCamera.SetBits(bitIndex, renderThis);
                                        int advance           = math.select(0, 1, renderThis);
                                        instanceOutputOffset += advance;

                                        lodWord ^= 1ul << bitIndex;
                                    }
                                    if (j == 0)
                                    {
                                        computeDeformMetadata.lastFrameRenderedMaskLower.Value |= renderedThisCamera.Value;
                                    }
                                    else
                                    {
                                        computeDeformMetadata.lastFrameRenderedMaskUpper.Value |= renderedThisCamera.Value;
                                    }
                                }
                                computeDeformMetadataArray[entityIndex] = computeDeformMetadata;
                            }
                            else
                            {
                                var chunkInstanceBounds = chunk.GetNativeArray(BoundsComponent);

                                for (int j = 0; j < 2; j++)
                                {
                                    var lodWord = chunkEntityLodEnabled.Enabled[j];

                                    while (lodWord != 0)
                                    {
                                        var bitIndex   = math.tzcnt(lodWord);
                                        var finalIndex = (j << 6) + bitIndex;

                                        scratch[instanceOutputOffset] = processedInstanceCount + finalIndex;

                                        int advance = FrustumPlanes.Intersect2(Planes, chunkInstanceBounds[finalIndex].Value) !=
                                                      FrustumPlanes.IntersectResult.Out ?
                                                      1 :
                                                      0;
                                        instanceOutputOffset += advance;

                                        lodWord ^= 1ul << bitIndex;
                                    }
                                }
                            }

                            int chunkOutputCount = instanceOutputOffset;

                            if (chunkOutputCount > 0)
                            {
                                chunkOutputOffset = System.Threading.Interlocked.Add(
                                    ref pBatch->visibleCount, chunkOutputCount) - chunkOutputCount;

                                chunkOutputOffset += batchOutputOffset;

                                var pVisibleIndices = ((int*)IndexList.GetUnsafePtr()) + chunkOutputOffset;

                                UnsafeUtility.MemCpy(
                                    pVisibleIndices,
                                    scratch,
                                    chunkOutputCount * sizeof(int));
                            }
                        }
                        else if (chunkIn == FrustumPlanes.IntersectResult.In)
                        {
                            // Since the chunk is fully in, we can easily count how many instances we will output
                            int chunkOutputCount = math.countbits(chunkEntityLodEnabled.Enabled[0]) +
                                                   math.countbits(chunkEntityLodEnabled.Enabled[1]);

                            chunkOutputOffset = System.Threading.Interlocked.Add(
                                ref pBatch->visibleCount, chunkOutputCount) - chunkOutputCount;

                            chunkOutputOffset += batchOutputOffset;

                            for (int j = 0; j < 2; j++)
                            {
                                var lodWord = chunkEntityLodEnabled.Enabled[j];

                                while (lodWord != 0)
                                {
                                    var bitIndex                                        = math.tzcnt(lodWord);
                                    var finalIndex                                      = (j << 6) + bitIndex;
                                    IndexList[chunkOutputOffset + instanceOutputOffset] =
                                        processedInstanceCount + finalIndex;

                                    instanceOutputOffset += 1;
                                    lodWord              ^= 1ul << bitIndex;
                                }
                            }
                        }
                    }
                    chunkCullingData.StartIndex       = (short)chunkOutputOffset;
                    chunkCullingData.Visible          = (short)instanceOutputOffset;
                    hybridChunkInfoArray[entityIndex] = hybridChunkInfo;
                }
            }
        }
    }
}

