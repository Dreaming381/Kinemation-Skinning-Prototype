using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine.Rendering;

namespace Latios.Kinemation.Systems
{
    [DisableAutoCreation]
    internal class KinemationCullingSuperSystem : SuperSystem
    {
        protected override void CreateSystems()
        {
            worldBlackboardEntity.AddCollectionComponent(new BrgCullingContext());

            EnableSystemSorting = false;

            GetOrCreateAndAddSystem<FrustumCullBonesSystem>();
            GetOrCreateAndAddSystem<FrustumCullSkeletonsSystem>();
            GetOrCreateAndAddSystem<UpdateLODsSystem>();
            GetOrCreateAndAddSystem<FrustumCullSkinnedEntitiesSystem>();
            GetOrCreateAndAddSystem<FrustumCullUnskinnedEntitiesSystem>();
            GetOrCreateAndAddSystem<SkinningDispatchSystem>();
            GetOrCreateAndAddSystem<UpdateVisibilitiesSystem>();
        }
    }

    [UpdateInGroup(typeof(UpdatePresentationSystemGroup))]
    [UpdateAfter(typeof(RenderBoundsUpdateSystem))]
    public class KinemationSuperSystem : RootSuperSystem
    {
        protected override void CreateSystems()
        {
            GetOrCreateAndAddSystem<UpdateSkeletonBoundsSystem>();
            GetOrCreateAndAddSystem<ClearPerFrameCullingMasksSystem>();
            GetOrCreateAndAddSystem<UpdateSkinnedMeshChunkBoundsSystem>();
            GetOrCreateAndAddSystem<UpdateChunkComputeDeformMetadataSystem>();
            GetOrCreateAndAddSystem<AllocateDeformedMeshesSystem>();
            GetOrCreateAndAddSystem<ResetPerFrameSkinningMetadataJob>();
            GetOrCreateAndAddSystem<BeginPerFrameMeshSkinningBuffersUploadSystem>();
        }
    }

    [UpdateInGroup(typeof(StructuralChangePresentationSystemGroup))]
    public class KinemationRenderSyncPointSuperSystem : RootSuperSystem
    {
        protected override void CreateSystems()
        {
            GetOrCreateAndAddSystem<AddMissingCullingMaskSystem>();
            GetOrCreateAndAddSystem<SkeletonMeshBindingReactiveSystem>();
        }
    }

    [UpdateInGroup(typeof(Latios.Systems.LatiosWorldSyncGroup))]
    public class KinemationFrameSyncPointSuperSystem : RootSuperSystem
    {
        protected override void CreateSystems()
        {
            GetOrCreateAndAddSystem<AddMissingCullingMaskSystem>();
            GetOrCreateAndAddSystem<SkeletonMeshBindingReactiveSystem>();
        }
    }
}

