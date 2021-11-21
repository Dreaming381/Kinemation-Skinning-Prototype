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
}

