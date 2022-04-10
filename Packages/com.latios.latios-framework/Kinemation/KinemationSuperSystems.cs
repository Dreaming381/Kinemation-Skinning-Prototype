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

            GetOrCreateAndAddSystem<UpdateSkinnedLODsSystem>();
            GetOrCreateAndAddSystem<ClearPerCameraFlagsSystem>();
            GetOrCreateAndAddSystem<SkeletonFrustumCullingAndSkinningDispatchSystem>();
            GetOrCreateAndAddSystem<UpdateUnskinnedLODsSystem>();
            GetOrCreateAndAddSystem<CullUnskinnedRenderersAndUpdateVisibilitiesSystem>();
        }
    }

    [UpdateInGroup(typeof(UpdatePresentationSystemGroup))]
    [UpdateAfter(typeof(RenderBoundsUpdateSystem))]
    public class KinemationSuperSystem : RootSuperSystem
    {
        protected override void CreateSystems()
        {
            GetOrCreateAndAddSystem<SkeletonBoundsUpdateSystem>();
            GetOrCreateAndAddSystem<SkinnedMeshChunkBoundsUpdateSystem>();
            GetOrCreateAndAddSystem<UpdateChunkComputeDeformMetadataSystem>();
            GetOrCreateAndAddSystem<AllocateDeformedMeshesSystem>();
            GetOrCreateAndAddSystem<ClearPerFrameSkinningDataSystem>();
            GetOrCreateAndAddSystem<BeginPerFrameMeshSkinningBuffersUploadSystem>();
        }
    }

    [UpdateInGroup(typeof(StructuralChangePresentationSystemGroup))]
    public class KinemationRenderSyncPointSuperSystem : RootSuperSystem
    {
        protected override void CreateSystems()
        {
            GetOrCreateAndAddSystem<SkeletonMeshBindingReactiveSystem>();
        }
    }

    [UpdateInGroup(typeof(Latios.Systems.LatiosWorldSyncGroup))]
    public class KinemationFrameSyncPointSuperSystem : RootSuperSystem
    {
        protected override void CreateSystems()
        {
            GetOrCreateAndAddSystem<SkeletonMeshBindingReactiveSystem>();
        }
    }
}

