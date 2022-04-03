using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Latios.Kinemation.Systems
{
    public partial class ClearPerFrameSkinningDataSystem : SubSystem
    {
        EntityQuery m_skeletonQuery;
        EntityQuery m_meshQuery;

        protected override void OnCreate()
        {
            m_skeletonQuery = Fluent.WithAll<PerFrameSkeletonBufferMetadata>().Build();
            m_meshQuery     = Fluent.WithAll<SkinningRenderCullingFlags>(false).Build();

            worldBlackboardEntity.AddCollectionComponent(new BoneMatricesPerFrameBuffersManager
            {
                boneMatricesBuffers = new System.Collections.Generic.List<UnityEngine.ComputeBuffer>()
            });
        }

        protected override void OnUpdate()
        {
            Dependency = new ClearSkinnedThisFrameJob
            {
                handle            = GetComponentTypeHandle<PerFrameSkeletonBufferMetadata>(false),
                lastSystemVersion = LastSystemVersion
            }.ScheduleParallel(m_skeletonQuery, Dependency);

            Dependency = new ClearSkinningRenderCullingFlagsJob
            {
                handle            = GetComponentTypeHandle<SkinningRenderCullingFlags>(false),
                lastSystemVersion = LastSystemVersion
            }.ScheduleParallel(m_meshQuery, Dependency);

            worldBlackboardEntity.GetCollectionComponent<BoneMatricesPerFrameBuffersManager>().boneMatricesBuffers.Clear();
        }

        [BurstCompile]
        struct ClearSkinnedThisFrameJob : IJobEntityBatch
        {
            public ComponentTypeHandle<PerFrameSkeletonBufferMetadata> handle;
            public uint                                                lastSystemVersion;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                if (batchInChunk.DidChange(handle, lastSystemVersion))
                {
                    var marks = batchInChunk.GetNativeArray(handle);
                    for (int i = 0; i < batchInChunk.Count; i++)
                    {
                        marks[i] = new PerFrameSkeletonBufferMetadata { bufferId = -1, startIndexInBuffer = -1 };
                    }
                }
            }
        }

        [BurstCompile]
        struct ClearSkinningRenderCullingFlagsJob : IJobEntityBatch
        {
            public ComponentTypeHandle<SkinningRenderCullingFlags> handle;
            public uint                                            lastSystemVersion;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                if (batchInChunk.DidChange(handle, lastSystemVersion))
                {
                    var marks = batchInChunk.GetNativeArray(handle).Reinterpret<byte>();
                    for (int i = 0; i < batchInChunk.Count; i++)
                    {
                        marks[i] &= (~(SkinningRenderCullingFlags.renderThisCamera | SkinningRenderCullingFlags.renderThisFrame)) & 0xff;
                    }
                }
            }
        }
    }
}

