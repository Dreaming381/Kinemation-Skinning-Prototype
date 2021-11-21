using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

// This system doesn't actually allocate the compute buffer.
// Doing so now would introduce a sync point.
// This system just calculates the required size and distributes instance shader properties.
namespace Latios.Kinemation.Systems
{
    public class AllocateDeformedMeshesSystem : SubSystem
    {
        EntityQuery m_metaQuery;
        EntityQuery m_query;

        protected override void OnCreate()
        {
            m_metaQuery = Fluent.WithAll<ChunkHeader>(true).WithAll<ChunkComputeDeformMemoryMetadata>().Build();
            m_query     = Fluent.WithAll<ComputeDeformShaderIndex>().WithAll<ChunkComputeDeformMemoryMetadata>(true, true).Build();

            worldBlackboardEntity.AddCollectionComponent(new LastFrameRenderedNotRenderedVertices
            {
                renderedNotRenderedCounts = new NativeReference<int2>(Allocator.Persistent)
            });
        }

        protected override void OnUpdate()
        {
            if (HybridSkinningToggle.EnableHybrid)
                return;

            World.GetExistingSystem<Unity.Rendering.DeformationsInPresentation>().Enabled = false;

            var context       = worldBlackboardEntity.GetCollectionComponent<LastFrameRenderedNotRenderedVertices>();
            var metaHandle    = GetComponentTypeHandle<ChunkComputeDeformMemoryMetadata>();
            var indicesHandle = GetComponentTypeHandle<ComputeDeformShaderIndex>();

            Dependency = new ChunkPrefixSumJob
            {
                metaHandle = metaHandle,
                prefixSums = context.renderedNotRenderedCounts
            }.Schedule(m_metaQuery, Dependency);

            Dependency = new AssignComputeDeformMeshOffsetsJob
            {
                metaHandle    = metaHandle,
                prefixSums    = context.renderedNotRenderedCounts,
                indicesHandle = indicesHandle
            }.ScheduleParallel(m_query, 1, Dependency);
        }

        // Schedule single
        [BurstCompile]
        struct ChunkPrefixSumJob : IJobEntityBatch
        {
            public ComponentTypeHandle<ChunkComputeDeformMemoryMetadata> metaHandle;
            public NativeReference<int2>                                 prefixSums;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                if (batchIndex == 0)
                    prefixSums.Value = default;

                var metadata = batchInChunk.GetNativeArray(metaHandle);

                for (int i = 0; i < batchInChunk.Count; i++)
                {
                    var datum       = metadata[i];
                    int rendered    = datum.lastFrameRenderedMaskLower.CountBits() + datum.lastFrameRenderedMaskUpper.CountBits();
                    int notRendered = datum.entitiesInChunk - rendered;

                    datum.prefixSumRendered     = prefixSums.Value.x;
                    datum.prefixSumNotRendered  = prefixSums.Value.y;
                    prefixSums.Value           += new int2(rendered, notRendered) * datum.verticesPerMesh;
                    metadata[i]                 = datum;
                }
            }
        }

        [BurstCompile]
        struct AssignComputeDeformMeshOffsetsJob : IJobEntityBatch
        {
            public ComponentTypeHandle<ChunkComputeDeformMemoryMetadata> metaHandle;
            [ReadOnly] public NativeReference<int2>                      prefixSums;

            public ComponentTypeHandle<ComputeDeformShaderIndex> indicesHandle;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                var metadata = batchInChunk.GetChunkComponentData(metaHandle);
                var indices  = batchInChunk.GetNativeArray(indicesHandle).Reinterpret<uint>();

                int rendered    = metadata.prefixSumRendered;
                int notRendered = metadata.prefixSumNotRendered + prefixSums.Value.x;

                for (int i = 0; i < math.min(batchInChunk.Count, 64); i++)
                {
                    if (metadata.lastFrameRenderedMaskLower.IsSet(i))
                    {
                        indices[i]  = (uint)rendered;
                        rendered   += metadata.verticesPerMesh;
                    }
                    else
                    {
                        indices[i]   = (uint)notRendered;
                        notRendered += metadata.verticesPerMesh;
                    }
                }
                for (int i = 0; i < batchInChunk.Count - 64; i++)
                {
                    if (metadata.lastFrameRenderedMaskUpper.IsSet(i))
                    {
                        indices[i + 64]  = (uint)rendered;
                        rendered        += metadata.verticesPerMesh;
                    }
                    else
                    {
                        indices[i + 64]  = (uint)notRendered;
                        notRendered     += metadata.verticesPerMesh;
                    }
                }

                metadata.lastFrameRenderedMaskLower.Clear();
                metadata.lastFrameRenderedMaskUpper.Clear();

                batchInChunk.SetChunkComponentData(metaHandle, metadata);
            }
        }
    }
}

