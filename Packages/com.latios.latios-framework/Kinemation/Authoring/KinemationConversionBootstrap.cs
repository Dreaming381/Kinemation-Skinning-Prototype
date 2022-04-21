using Unity.Entities;
using Unity.Rendering;

namespace Latios.Kinemation.Authoring
{
    public static class KinemationConversionBootstrap
    {
        public static void InstallKinemationConversion(World world)
        {
            if (!world.Flags.HasFlag(WorldFlags.Conversion))
                throw new System.InvalidOperationException("Kinemation Conversion must be installed in a conversion world.");

            var builtinConversionSystem = world.GetExistingSystem<SkinnedMeshRendererConversion>();
            if (builtinConversionSystem != null)
                builtinConversionSystem.Enabled = false;

            BootstrapTools.InjectSystem(typeof(KinemationSkinningConversionSystem), world);
        }
    }
}

