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
        public unsafe bool Initialize(string defaultWorldName)
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

            world.Unmanaged.ResolveSystemState(world.Unmanaged.GetExistingUnmanagedSystem<Unity.Transforms.LocalToParentSystem>().Handle)->Enabled = false;
            world.Unmanaged.ResolveSystemState(world.Unmanaged.GetExistingUnmanagedSystem<Unity.Transforms.ParentSystem>().Handle)->Enabled        = false;

            //BootstrapTools.InjectSystem(typeof(ImprovedParentSystem),        world);
            //BootstrapTools.InjectSystem(typeof(ImprovedLocalToParentSystem), world);
            BootstrapTools.InjectSystem(typeof(ExtremeParentSystem),        world);
            BootstrapTools.InjectSystem(typeof(ExtremeChildDepthsSystem),   world);
            BootstrapTools.InjectSystem(typeof(ExtremeLocalToParentSystem), world);

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
            //GetOrCreateAndAddSystem<PokeDancerRootsSystem>();
            GetOrCreateAndAddSystem<SampleDancersExposedSystem>();
            GetOrCreateAndAddSystem<SampleDancersOptimizedSystem>();
            GetOrCreateAndAddSystem<Unity.Transforms.TransformSystemGroup>();
            GetOrCreateAndAddSystem<TempFixExportedTransformsSystem>();
            GetOrCreateAndAddSystem<CorrectDancerFeetSystem>();
        }
    }
}

