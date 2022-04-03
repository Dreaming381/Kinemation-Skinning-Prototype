using System;
using System.Collections.Generic;
using Latios;
using Latios.Systems;
using Unity.Collections;
using Unity.Entities;

namespace Dragons
{
    public class LatiosBootstrap : ICustomBootstrap
    {
        public bool Initialize(string defaultWorldName)
        {
            var world                             = new LatiosWorld(defaultWorldName);
            World.DefaultGameObjectInjectionWorld = world;
            world.useExplicitSystemOrdering       = true;

            var initializationSystemGroup = world.initializationSystemGroup;
            var simulationSystemGroup     = world.simulationSystemGroup;
            var presentationSystemGroup   = world.presentationSystemGroup;
            var systems                   = new List<Type>(DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default));

            systems.RemoveSwapBack(typeof(LatiosInitializationSystemGroup));
            systems.RemoveSwapBack(typeof(LatiosSimulationSystemGroup));
            systems.RemoveSwapBack(typeof(LatiosPresentationSystemGroup));
            systems.RemoveSwapBack(typeof(InitializationSystemGroup));
            systems.RemoveSwapBack(typeof(SimulationSystemGroup));
            systems.RemoveSwapBack(typeof(PresentationSystemGroup));

            BootstrapTools.InjectUnitySystems(systems, world, simulationSystemGroup);
            BootstrapTools.InjectRootSuperSystems(systems, world, simulationSystemGroup);

            initializationSystemGroup.SortSystems();
            simulationSystemGroup.SortSystems();
            presentationSystemGroup.SortSystems();

            //world.GetExistingSystem<Unity.Transforms.LocalToParentSystem>().Enabled  = false;
            //world.GetExistingSystem<Latios.UnityReplacements.LocalToParentSystem2>().Enabled = false;

            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);
            return true;
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(Unity.Transforms.TransformSystemGroup))]
    public class DancerSuperSystem : RootSuperSystem
    {
        protected override void CreateSystems()
        {
            GetOrCreateAndAddSystem<SpawnAndBuildReferencesSystem>();
            GetOrCreateAndAddSystem<Latios.Kinemation.Systems.SkeletonMeshBindingReactiveSystem>();
            //GetOrCreateAndAddSystem<PokeDancerRootsSystem>();
            GetOrCreateAndAddSystem<SampleDancersExposedSystem>();
            GetOrCreateAndAddSystem<SampleDancersOptimizedSystem>();
            GetOrCreateAndAddSystem<Unity.Transforms.TransformSystemGroup>();
            GetOrCreateAndAddSystem<TempFixExportedTransformsSystem>();
            GetOrCreateAndAddSystem<CorrectDancerFeetSystem>();
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(Unity.Transforms.TransformSystemGroup))]
    public class KinemationSuperSystem : RootSuperSystem
    {
        protected override void CreateSystems()
        {
            GetOrCreateAndAddSystem<Latios.Kinemation.Systems.HybridSkinMatrixSystem>();
            GetOrCreateAndAddSystem<Latios.Kinemation.Systems.SkeletonBoundsUpdateSystem>();
            GetOrCreateAndAddSystem<Latios.Kinemation.Systems.UpdateChunkComputeDeformMetadataSystem>();
            GetOrCreateAndAddSystem<Latios.Kinemation.Systems.AllocateDeformedMeshesSystem>();
            GetOrCreateAndAddSystem<Latios.Kinemation.Systems.ClearPerFrameSkinningDataSystem>();
            GetOrCreateAndAddSystem<Latios.Kinemation.Systems.BeginPerFrameMeshSkinningBuffersUploadSystem>();
        }
    }
}

