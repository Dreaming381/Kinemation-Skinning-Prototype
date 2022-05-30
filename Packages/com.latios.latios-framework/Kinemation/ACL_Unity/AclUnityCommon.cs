using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Mathematics;

namespace AclUnity
{
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct Qvv
    {
        [FieldOffset(0)] public quaternion rotation;
        [FieldOffset(16)] public float4    translation;
        [FieldOffset(32)] public float4    scale;
    }

    public struct SemanticVersion
    {
        public short major;
        public short minor;
        public short patch;

        public bool IsValid => major > 0 || minor > 0 || patch > 0;
        public bool IsUnrecognized => major == -1 && minor == -1 && patch == -1;
    }

    [BurstCompile]
    public static class AclUnityCommon
    {
        public static SemanticVersion GetVersion()
        {
            var mask = GetArch();
            int version;
            if (mask.avx)
                version = AVX.getVersion();
            else if (mask.sse4)
                version = SSE4.getVersion();
            else if (mask.neon)
                version = Neon.getVersion();
            else
            {
                //UnityEngine.Debug.Log("Fetched without AVX");
                version = NoExtensions.getVersion();
            }

            if (version == -1)
                return new SemanticVersion { major = -1, minor = -1, patch = -1 };

            short patch                        = (short)(version & 0x3ff);
            short minor                        = (short)((version >> 10) & 0x3ff);
            short major                        = (short)((version >> 20) & 0x3ff);
            return new SemanticVersion { major = major, minor = minor, patch = patch };
        }

        public static string GetPluginName()
        {
            var mask = GetArch();
            if (mask.avx)
                return dllNameAVX;
            if (mask.sse4)
                return dllNameSSE4;
            if (mask.neon)
                return dllNameNeon;
            return dllName;
        }

        [BurstCompile(CompileSynchronously = true)]
        internal struct ArchitectureMask
        {
            internal byte m_avx;
            internal byte m_sse4;
            internal byte m_neon;

            public bool avx => m_avx != 0;
            public bool sse4 => m_sse4 != 0;
            public bool neon => m_neon != 0;
        }

        [BurstCompile]
        private static void GetArch(ref ArchitectureMask mask)
        {
            if (X86.Avx2.IsAvx2Supported)
                mask.m_avx = 1;
            else if (X86.Sse4_2.IsSse42Supported && X86.Popcnt.IsPopcntSupported)
                mask.m_sse4 = 1;
            else if (Arm.Neon.IsNeonSupported)
                mask.m_neon = 1;
        }

        internal static ArchitectureMask GetArch()
        {
            ArchitectureMask mask = default;
            GetArch(ref mask);
            return mask;
        }

#if UNITY_IPHONE
        private const string dllName     = "__Internal";
        private const string dllNameAVX  = "__Internal";
        private const string dllNameSSE4 = "__Internal";
        private const string dllNameNeon = "__Internal";
#else
        private const string dllName     = "AclUnity";
        private const string dllNameAVX  = "AclUnity_AVX";
        private const string dllNameSSE4 = "AclUnity_SSE4";
        private const string dllNameNeon = "AclUnity_Neon";
#endif
        static class NoExtensions
        {
            [DllImport(dllName)]
            public static extern int getVersion();
        }

        static class AVX
        {
            [DllImport(dllNameAVX)]
            public static extern int getVersion();
        }

        static class SSE4
        {
            [DllImport(dllNameSSE4)]
            public static extern int getVersion();
        }

        static class Neon
        {
            [DllImport(dllNameNeon)]
            public static extern int getVersion();
        }
    }
}

