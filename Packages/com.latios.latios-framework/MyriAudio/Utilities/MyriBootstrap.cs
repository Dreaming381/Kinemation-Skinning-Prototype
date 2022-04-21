using Unity.Entities;

namespace Latios.Myri
{
    public static class MyriBootstrap
    {
        public static void InstallMyri(World world)
        {
            if (world.Flags.HasFlag(WorldFlags.Conversion))
                throw new System.InvalidOperationException("Cannot install Myri runtime in a conversion world.");

            BootstrapTools.InjectSystem(typeof(Systems.AudioSystem), world);
        }
    }
}

