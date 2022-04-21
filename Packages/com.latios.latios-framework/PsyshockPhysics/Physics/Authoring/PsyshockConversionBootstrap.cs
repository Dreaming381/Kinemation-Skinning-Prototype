using Latios.Psyshock.Authoring.Systems;
using Unity.Entities;

namespace Latios.Psyshock.Authoring
{
    public static class PsyshockConversionBootstrap
    {
        public static void InstallLegacyColliderConversion(World world)
        {
            if (!world.Flags.HasFlag(WorldFlags.Conversion))
                throw new System.InvalidOperationException("Psyshock Legacy Collider Conversion must be installed in a conversion world.");

            BootstrapTools.InjectSystem(typeof(LegacyColliderConversionSystem), world);
        }
    }
}

