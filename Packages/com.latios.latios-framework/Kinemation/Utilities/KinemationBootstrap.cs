using Latios.Kinemation.Systems;
using Unity.Entities;
using Unity.Rendering;

namespace Latios.Kinemation
{
    public static class KinemationBootstrap
    {
        public static void InstallKinemation(World world)
        {
            var unityRenderer = world.GetExistingSystem<HybridRendererSystem>();
            if (unityRenderer != null)
                unityRenderer.Enabled = false;
            var unitySkinning         = world.GetExistingSystem<DeformationsInPresentation>();
            if (unitySkinning != null)
                unitySkinning.Enabled = false;

            BootstrapTools.InjectSystem(typeof(KinemationRenderUpdateSuperSystem),    world);
            BootstrapTools.InjectSystem(typeof(KinemationRenderSyncPointSuperSystem), world);
            BootstrapTools.InjectSystem(typeof(KinemationFrameSyncPointSuperSystem),  world);
            BootstrapTools.InjectSystem(typeof(LatiosHybridRendererSystem),           world);
        }
    }
}

