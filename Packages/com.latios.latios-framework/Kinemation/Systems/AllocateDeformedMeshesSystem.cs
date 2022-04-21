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
    [DisableAutoCreation]
    public partial class AllocateDeformedMeshesSystem : SubSystem
    {
        EntityQuery m_metaQuery;
        EntityQuery m_query;

        protected override void OnCreate()
        {
            m_metaQuery = Fluent.WithAll<ChunkHeader>(true).WithAll<ChunkComputeDeformMemoryMetadata>().Build();
            m_query     = Fluent.WithAll<ComputeDeformShaderIndex>().WithAll<ChunkComputeDeformMemoryMetadata>(true, true).Build();

            worldBlackboardEntity.AddComponent<MaxRequiredDeformVertices>();
        }

        protected override void OnUpdate()
        {
            var metaHandle    = GetComponentTypeHandle<ChunkComputeDeformMemoryMetadata>();
            var indicesHandle = GetComponentTypeHandle<ComputeDeformShaderIndex>();

            Dependency = new ChunkPrefixSumJob
            {
                metaHandle                    = metaHandle,
                maxRequiredDeformVerticesCdfe = GetComponentDataFromEntity<MaxRequiredDeformVertices>(),
                worldBlackboardEntity         = worldBlackboardEntity
            }.Schedule(m_metaQuery, Dependency);

            Dependency = new AssignComputeDeformMeshOffsetsJob
            {
                metaHandle    = metaHandle,
                indicesHandle = indicesHandle
            }.ScheduleParallel(m_query, Dependency);
        }

        // Schedule single
        [BurstCompile]
        struct ChunkPrefixSumJob : IJobEntityBatch
        {
            public ComponentTypeHandle<ChunkComputeDeformMemoryMetadata> metaHandle;
            public ComponentDataFromEntity<MaxRequiredDeformVertices>    maxRequiredDeformVerticesCdfe;
            public Entity                                                worldBlackboardEntity;
            int                                                          prefixSum;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                if (batchIndex == 0)
                    prefixSum = 0;

                var metadata = batchInChunk.GetNativeArray(metaHandle);

                for (int i = 0; i < batchInChunk.Count; i++)
                {
                    var datum = metadata[i];

                    datum.vertexStartPrefixSum  = prefixSum;
                    prefixSum                  += datum.entitiesInChunk * datum.verticesPerMesh;
                    metadata[i]                 = datum;
                }
                maxRequiredDeformVerticesCdfe[worldBlackboardEntity] = new MaxRequiredDeformVertices { verticesCount = prefixSum };
            }
        }

        [BurstCompile]
        struct AssignComputeDeformMeshOffsetsJob : IJobEntityBatch
        {
            [ReadOnly] public ComponentTypeHandle<ChunkComputeDeformMemoryMetadata> metaHandle;

            public ComponentTypeHandle<ComputeDeformShaderIndex> indicesHandle;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                var metadata  = batchInChunk.GetChunkComponentData(metaHandle);
                var indices   = batchInChunk.GetNativeArray(indicesHandle).Reinterpret<uint>();
                int prefixSum = metadata.vertexStartPrefixSum;

                for (int i = 0; i < batchInChunk.Count; i++)
                {
                    indices[i]  = (uint)prefixSum;
                    prefixSum  += metadata.verticesPerMesh;
                }
            }
        }
    }
}

