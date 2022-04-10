using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;

namespace Latios.Kinemation.Systems
{
    [DisableAutoCreation]
    public class AddMissingCullingMaskSystem : SubSystem
    {
        EntityQuery m_query;

        protected override void OnCreate()
        {
            m_query = Fluent.WithAll<RenderMesh>(true).Without<ChunkPerCameraCullingMask>(true).Without<ChunkPerFrameCullingMask>(true).IncludePrefabs().IncludeDisabled().Build();
        }

        protected override void OnUpdate()
        {
            var types = new ComponentTypes(ComponentType.ChunkComponent<ChunkPerCameraCullingMask>(), ComponentType.ChunkComponent<ChunkPerFrameCullingMask>());
            EntityManager.AddComponent(m_query, types);
        }
    }
}

