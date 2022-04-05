using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Latios.Kinemation.Systems
{
    [DisableAutoCreation]
    public partial class ClearPerCameraFlagsSystem : SubSystem
    {
        EntityQuery m_query;

        protected override void OnCreate()
        {
            m_query = Fluent.WithAll<SkinningRenderCullingFlags>(false).Build();
            worldBlackboardEntity.AddComponent<PerCameraClearFlagsSystemChangeVersion>();
        }

        protected override void OnUpdate()
        {
            worldBlackboardEntity.SetComponentData(new PerCameraClearFlagsSystemChangeVersion { capturedWorldSystemVersion = GlobalSystemVersion });
            Dependency                                                                                                     = new ClearJob
            {
                handle            = GetComponentTypeHandle<SkinningRenderCullingFlags>(false),
                lastSystemVersion = LastSystemVersion
            }.ScheduleParallel(m_query, Dependency);
        }

        [BurstCompile]
        struct ClearJob : IJobEntityBatch
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
                        marks[i] &= (~SkinningRenderCullingFlags.renderThisCamera) & 0xff;
                    }
                }
            }
        }
    }
}

