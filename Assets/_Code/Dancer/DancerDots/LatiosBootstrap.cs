using System;
using System.Collections.Generic;
using Latios;
using Latios.Authoring;
using Unity.Entities;

namespace Dragons
{
    public class LatiosConversionBootstrap : ICustomConversionBootstrap
    {
        public bool InitializeConversion(World conversionWorldWithGroupsAndMappingSystems, CustomConversionSettings settings, ref List<Type> filteredSystems)
        {
            var defaultGroup = conversionWorldWithGroupsAndMappingSystems.GetExistingSystem<GameObjectConversionGroup>();
            BootstrapTools.InjectSystems(filteredSystems, conversionWorldWithGroupsAndMappingSystems, defaultGroup);

            Latios.Psyshock.Authoring.PsyshockConversionBootstrap.InstallLegacyColliderConversion(conversionWorldWithGroupsAndMappingSystems);
            Latios.Kinemation.Authoring.KinemationConversionBootstrap.InstallKinemationConversion(conversionWorldWithGroupsAndMappingSystems);
            return true;
        }
    }

    public class LatiosBootstrap : ICustomBootstrap
    {
        public unsafe bool Initialize(string defaultWorldName)
        {
            var world                             = new LatiosWorld(defaultWorldName);
            World.DefaultGameObjectInjectionWorld = world;
            world.useExplicitSystemOrdering       = true;

            var systems = new List<Type>(DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default));

            BootstrapTools.InjectUnitySystems(systems, world, world.simulationSystemGroup);
            BootstrapTools.InjectRootSuperSystems(systems, world, world.simulationSystemGroup);

            CoreBootstrap.InstallExtremeTransforms(world);
            Latios.Kinemation.KinemationBootstrap.InstallKinemation(world);

            world.initializationSystemGroup.SortSystems();
            world.simulationSystemGroup.SortSystems();
            world.presentationSystemGroup.SortSystems();

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
            GetOrCreateAndAddSystem<TestConvexSystem>();
            GetOrCreateAndAddSystem<SampleDancersExposedSystem>();
            GetOrCreateAndAddSystem<SampleDancersOptimizedSystem>();
            GetOrCreateAndAddSystem<Unity.Transforms.TransformSystemGroup>();
            GetOrCreateAndAddSystem<TempFixExportedTransformsSystem>();
            GetOrCreateAndAddSystem<CorrectDancerFeetSystem>();
        }
    }
}

