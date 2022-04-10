using Latios.Systems;
using Unity.Entities;
using Unity.Transforms;

namespace Latios
{
    public static unsafe class CoreBootstrap
    {
        public static void InstallSceneManager(World world)
        {
            // Todo:
        }

        public static void InstallImprovedTransforms(World world)
        {
            if (world.GetExistingSystem<ExtremeLocalToParentSystem>() != null)
            {
                throw new System.InvalidOperationException("Cannot install Improved Transforms when Extreme Transforms are already installed.");
            }

            var unmanaged = world.Unmanaged;

            try
            {
                unmanaged.ResolveSystemState(unmanaged.GetExistingUnmanagedSystem<LocalToParentSystem>().Handle)->Enabled = false;
            }
            catch (System.InvalidOperationException)
            {
                // Failed to find the unmanaged system
            }
            try
            {
                unmanaged.ResolveSystemState(unmanaged.GetExistingUnmanagedSystem<ParentSystem>().Handle)->Enabled = false;
            }
            catch (System.InvalidOperationException)
            {
                // Failed to find the unmanaged system
            }

            BootstrapTools.InjectSystem(typeof(ImprovedParentSystem),        world);
            BootstrapTools.InjectSystem(typeof(ImprovedLocalToParentSystem), world);
        }

        public static void InstallExtremeTransforms(World world)
        {
            var unmanaged = world.Unmanaged;

            bool caughtException = false;

            try
            {
                unmanaged.GetExistingUnmanagedSystem<ImprovedParentSystem>();
            }
            catch (System.InvalidOperationException)
            {
                // Failed to find the unmanaged system
                caughtException = true;
            }

            if (!caughtException)
                throw new System.InvalidOperationException("Cannot install Extreme Transforms when Improved Transforms are already installed");

            try
            {
                unmanaged.ResolveSystemState(unmanaged.GetExistingUnmanagedSystem<LocalToParentSystem>().Handle)->Enabled = false;
            }
            catch (System.InvalidOperationException)
            {
                // Failed to find the unmanaged system
            }
            try
            {
                unmanaged.ResolveSystemState(unmanaged.GetExistingUnmanagedSystem<ParentSystem>().Handle)->Enabled = false;
            }
            catch (System.InvalidOperationException)
            {
                // Failed to find the unmanaged system
            }

            BootstrapTools.InjectSystem(typeof(ExtremeParentSystem),        world);
            BootstrapTools.InjectSystem(typeof(ExtremeChildDepthsSystem),   world);
            BootstrapTools.InjectSystem(typeof(ExtremeLocalToParentSystem), world);
        }
    }
}

