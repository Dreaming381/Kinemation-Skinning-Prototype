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
    public class AddMissingMasksSystem : SubSystem
    {
        EntityQuery m_query;

        protected override void OnCreate()
        {
            m_query = Fluent.WithAll<RenderMesh>(true).Without<ChunkPerFrameCullingMask>(true).IncludePrefabs().IncludeDisabled().Build();
        }

        protected override void OnUpdate()
        {
            var types = new ComponentTypes(ComponentType.ChunkComponent<ChunkPerCameraCullingMask>(),
                                           ComponentType.ChunkComponent<ChunkPerFrameCullingMask>(),
                                           ComponentType.ChunkComponent<ChunkMaterialPropertyDirtyMask>());
            EntityManager.AddComponent(m_query, types);
        }
    }
}

