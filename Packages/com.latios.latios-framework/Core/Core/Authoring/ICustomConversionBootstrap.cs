using System.Collections.Generic;
using System.Reflection;
using static Unity.Entities.GameObjectConversionUtility;
using Unity.Entities;

namespace Latios.Authoring
{
    public struct CustomConversionSettings
    {
        public World           destinationWorld;
        public Hash128         sceneGUID;
        public string          debugConversionName;
        public ConversionFlags conversionFlags;
#if UNITY_EDITOR
        public UnityEditor.GUID buildConfigurationGUID;
        public Unity.Build.BuildConfiguration buildConfiguration;
        public UnityEditor.AssetImporters.AssetImportContext assetImportContext;
        public UnityEngine.GameObject prefabRoot;
#endif
    }

    public interface ICustomConversionBootstrap
    {
        // Return true if this function created all the conversion world systems.
        // Return false if Unity should create them from the settings after this function returns.
        // The list of filtered systems initially contains the systems Unity would have otherwise added.
        // Modify or replace the list to change the systems to add if returning false.
        // If returning true, the result of any modification is ignored.
        // The top-level ComponentSystemGroups already exist when this function is called.
        bool InitializeConversion(World conversionWorldWithGroupsAndMappingSystems, CustomConversionSettings settings, ref List<System.Type> filteredSystems);
    }

#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    internal static class ConversionBootstrapUtilities
    {
        static ConversionBootstrapUtilities()
        {
            RegisterConversionWorldAction();
        }

        static bool m_isRegistered = false;

        internal static void RegisterConversionWorldAction()
        {
            if (!m_isRegistered)
            {
                m_isRegistered                                                = true;
                Unity.Entities.Exposed.WorldExposedExtensions.OnWorldCreated += InitializeConversionWorld;
            }
        }

        static void InitializeConversionWorld(World conversionWorldWithoutSystems)
        {
            if (!conversionWorldWithoutSystems.Flags.HasFlag(WorldFlags.Conversion))
                return;

            conversionWorldWithoutSystems.GetOrCreateSystem<CustomConversionBootstrapDetectorSystem>();
        }

        [DisableAutoCreation]
        class CustomConversionBootstrapDetectorSystem : ComponentSystem
        {
            bool bootstrapRan = false;

            protected override void OnCreate()
            {
                Unity.Entities.Exposed.WorldExposedExtensions.OnSystemCreated += CreateBootstrapSystem;
            }

            protected override void OnDestroy()
            {
                if (!bootstrapRan)
                    Unity.Entities.Exposed.WorldExposedExtensions.OnSystemCreated -= CreateBootstrapSystem;
            }

            protected override void OnUpdate()
            {
            }

            void CreateBootstrapSystem(World world, ComponentSystemBase system)
            {
                if (world != World)
                    return;

                if (system == this)
                    return;

                bootstrapRan                                                   = true;
                Unity.Entities.Exposed.WorldExposedExtensions.OnSystemCreated -= CreateBootstrapSystem;

                world.CreateSystem<CustomConversionBootstrapSystem>();
            }
        }

        [DisableAutoCreation]
        class CustomConversionBootstrapSystem : GameObjectConversionSystem
        {
            protected override void OnCreate()
            {
                base.OnCreate();

                IEnumerable<System.Type> bootstrapTypes;
#if UNITY_EDITOR
                bootstrapTypes = UnityEditor.TypeCache.GetTypesDerivedFrom(typeof(ICustomConversionBootstrap));
#else

                var types = new List<System.Type>();
                var type  = typeof(ICustomConversionBootstrap);
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!BootstrapTools.IsAssemblyReferencingLatios(assembly))
                        continue;

                    try
                    {
                        var assemblyTypes = assembly.GetTypes();
                        foreach (var t in assemblyTypes)
                        {
                            if (type.IsAssignableFrom(t))
                                types.Add(t);
                        }
                    }
                    catch (ReflectionTypeLoadException e)
                    {
                        foreach (var t in e.Types)
                        {
                            if (t != null && type.IsAssignableFrom(t))
                                types.Add(t);
                        }

                        UnityEngine.Debug.LogWarning($"ConversionWorldBootstrap failed loading assembly: {(assembly.IsDynamic ? assembly.ToString() : assembly.Location)}");
                    }
                }

                bootstrapTypes = types;
#endif

                System.Type selectedType = null;

                foreach (var bootType in bootstrapTypes)
                {
                    if (bootType.IsAbstract || bootType.ContainsGenericParameters)
                        continue;

                    if (selectedType == null)
                        selectedType = bootType;
                    else if (selectedType.IsAssignableFrom(bootType))
                        selectedType = bootType;
                    else if (!bootType.IsAssignableFrom(selectedType))
                        UnityEngine.Debug.LogError("Multiple custom ICustomConversionBootstrap exist in the project, ignoring " + bootType);
                }
                if (selectedType == null)
                    return;

                ICustomConversionBootstrap bootstrap = System.Activator.CreateInstance(selectedType) as ICustomConversionBootstrap;

                var settingsProperty   = GetType().GetProperty("Settings", BindingFlags.Instance | BindingFlags.NonPublic);
                var settingsObject     = settingsProperty.GetValue(this);
                var settings           = settingsObject as GameObjectConversionSettings;
                var settingsType       = settings.GetType();
                var conversionAssembly = settingsType.Assembly;

                var incremental    = World.GetOrCreateSystem(conversionAssembly.GetType("Unity.Entities.ConversionSetupGroup")) as ComponentSystemGroup;
                var declareConvert = World.GetOrCreateSystem<GameObjectDeclareReferencedObjectsGroup>();
                var earlyConvert   = World.GetOrCreateSystem<GameObjectBeforeConversionGroup>();
                var convert        = World.GetOrCreateSystem<GameObjectConversionGroup>();
                var lateConvert    = World.GetOrCreateSystem<GameObjectAfterConversionGroup>();

                var export = settings.SupportsExporting ? World.GetOrCreateSystem<GameObjectExportGroup>() : null;

                {
                    // for various reasons, this system needs to be present before any other system initializes
                    var system = World.GetOrCreateSystem(conversionAssembly.GetType("Unity.Entities.IncrementalChangesSystem"));
                    incremental.AddSystemToUpdateList(system);
                }

                var baseSystemTypes    = settings.Systems ?? DefaultWorldInitialization.GetAllSystems(settings.FilterFlags);
                var filteredSystemsSet = new HashSet<System.Type>();
                foreach (var system in baseSystemTypes)
                    filteredSystemsSet.Add(system);
                foreach (var system in settings.ExtraSystems)
                    filteredSystemsSet.Add(system);

                // Todo: Assuming the filter flags are not set to All so we don't have to remove the [DisableAutoCreation] systems we just created.
                var filteredSystems = new List<System.Type>(filteredSystemsSet);

                CustomConversionSettings customSettings = new CustomConversionSettings
                {
                    destinationWorld    = settings.DestinationWorld,
                    sceneGUID           = settings.SceneGUID,
                    debugConversionName = settings.DebugConversionName,
                    conversionFlags     = settings.ConversionFlags,

#if UNITY_EDITOR
                    buildConfigurationGUID = settings.BuildConfigurationGUID,
                    buildConfiguration     = settings.BuildConfiguration,
                    assetImportContext     = settings.AssetImportContext,
                    prefabRoot             = settings.PrefabRoot
#endif
                };

                bool createdSystems = bootstrap.InitializeConversion(World, customSettings, ref filteredSystems);

                if (createdSystems)
                {
                    settings.Systems = new List<System.Type>();
                }
                else
                {
                    settings.Systems = filteredSystems;
                }
                settings.ExtraSystems            = System.Array.Empty<System.Type>();
                settings.ConversionWorldCreated += OnConversionWorldCreationFinished;
                m_settings                       = settings;
                m_ranCleanup                     = false;

                foreach (var system in World.Systems)
                {
                    if (!system.Enabled)
                        m_disableSet.Add(system);
                }
            }

            protected override void OnUpdate()
            {
            }

            GameObjectConversionSettings m_settings;
            bool                         m_ranCleanup = true;
            HashSet<ComponentSystemBase> m_disableSet = new HashSet<ComponentSystemBase>();

            protected override void OnDestroy()
            {
                if (!m_ranCleanup)
                    m_settings.ConversionWorldCreated -= OnConversionWorldCreationFinished;
                m_ranCleanup                           = true;
            }

            void OnConversionWorldCreationFinished(World world)
            {
                if (world == World)
                {
                    m_settings.ConversionWorldCreated -= OnConversionWorldCreationFinished;
                    m_ranCleanup                       = true;

                    if (m_disableSet.Count == 0)
                        return;

                    foreach (var system in World.Systems)
                    {
                        if (m_disableSet.Contains(system))
                            system.Enabled = false;
                    }
                }
            }
        }
    }
}

