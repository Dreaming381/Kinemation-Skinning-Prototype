using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace AclUnity
{
    public static unsafe class Decompression
    {
        public enum KeyframeInterpolationMode : byte
        {
            Interpolate = 0,
            Floor = 1,
            Ceil = 2,
            Nearest = 3
        }

        // Warning: If you do not provide enough elements to outputBuffer, this may cause data corruption or even hard crash
        public static void SamplePoseAos(void* compressedTransformsClip, NativeArray<Qvv> outputBuffer, float time, KeyframeInterpolationMode keyframeInterpolationMode)
        {
            CheckCompressedClipIsValid(compressedTransformsClip);
            CheckOutputArrayIsCreated(outputBuffer);

            var mask = AclUnityCommon.GetArch();
            if (mask.avx)
            {
                AVX.samplePoseAOS(compressedTransformsClip, (float*)outputBuffer.GetUnsafePtr(), time, (byte)keyframeInterpolationMode);
            }
            else if (mask.sse4)
            {
                SSE4.samplePoseAOS(compressedTransformsClip, (float*)outputBuffer.GetUnsafePtr(), time, (byte)keyframeInterpolationMode);
            }
            else if (mask.neon)
            {
                Neon.samplePoseAOS(compressedTransformsClip, (float*)outputBuffer.GetUnsafePtr(), time, (byte)keyframeInterpolationMode);
            }
            else
            {
                NoExtensions.samplePoseAOS(compressedTransformsClip, (float*)outputBuffer.GetUnsafePtr(), time, (byte)keyframeInterpolationMode);
            }
        }

        // Warning: If you do not provide enough elements to outputBuffer, this may cause data corruption or even hard crash
        public static void SamplePoseSoa(void* compressedTransformsClip, NativeArray<float> outputBuffer, float time, KeyframeInterpolationMode keyframeInterpolationMode)
        {
            CheckCompressedClipIsValid(compressedTransformsClip);
            CheckOutputArrayIsCreated(outputBuffer);

            var mask = AclUnityCommon.GetArch();
            if (mask.avx)
            {
                AVX.samplePoseSOA(compressedTransformsClip, (float*)outputBuffer.GetUnsafePtr(), time, (byte)keyframeInterpolationMode);
            }
            else if (mask.sse4)
            {
                SSE4.samplePoseSOA(compressedTransformsClip, (float*)outputBuffer.GetUnsafePtr(), time, (byte)keyframeInterpolationMode);
            }
            else if (mask.neon)
            {
                Neon.samplePoseSOA(compressedTransformsClip, (float*)outputBuffer.GetUnsafePtr(), time, (byte)keyframeInterpolationMode);
            }
            else
            {
                NoExtensions.samplePoseSOA(compressedTransformsClip, (float*)outputBuffer.GetUnsafePtr(), time, (byte)keyframeInterpolationMode);
            }
        }

        public static Qvv SampleBone(void* compressedTransformsClip, int boneIndex, float time, KeyframeInterpolationMode keyframeInterpolationMode)
        {
            CheckCompressedClipIsValid(compressedTransformsClip);
            CheckIndexIsValid(boneIndex);

            Qvv qvv;

            var mask = AclUnityCommon.GetArch();
            if (mask.avx)
            {
                AVX.sampleBone(compressedTransformsClip, (float*)(&qvv), boneIndex, time, (byte)keyframeInterpolationMode);
            }
            else if (mask.sse4)
            {
                SSE4.sampleBone(compressedTransformsClip, (float*)(&qvv), boneIndex, time, (byte)keyframeInterpolationMode);
            }
            else if (mask.neon)
            {
                Neon.sampleBone(compressedTransformsClip, (float*)(&qvv), boneIndex, time, (byte)keyframeInterpolationMode);
            }
            else
            {
                NoExtensions.sampleBone(compressedTransformsClip, (float*)(&qvv), boneIndex, time, (byte)keyframeInterpolationMode);
            }

            return qvv;
        }

        // Warning: If you do not provide enough elements to outputBuffer, this may cause data corruption or even hard crash
        public static void SampleFloats(void* compressedFloatsClip, NativeArray<float> outputBuffer, float time, KeyframeInterpolationMode keyframeInterpolationMode)
        {
            CheckCompressedClipIsValid(compressedFloatsClip);
            CheckOutputArrayIsCreated(outputBuffer);

            var mask = AclUnityCommon.GetArch();
            if (mask.avx)
            {
                AVX.sampleFloats(compressedFloatsClip, (float*)outputBuffer.GetUnsafePtr(), time, (byte)keyframeInterpolationMode);
            }
            else if (mask.sse4)
            {
                SSE4.sampleFloats(compressedFloatsClip, (float*)outputBuffer.GetUnsafePtr(), time, (byte)keyframeInterpolationMode);
            }
            else if (mask.neon)
            {
                Neon.sampleFloats(compressedFloatsClip, (float*)outputBuffer.GetUnsafePtr(), time, (byte)keyframeInterpolationMode);
            }
            else
            {
                NoExtensions.sampleFloats(compressedFloatsClip, (float*)outputBuffer.GetUnsafePtr(), time, (byte)keyframeInterpolationMode);
            }
        }

        public static float SampleFloat(void* compressedFloatsClip, int trackIndex, float time, KeyframeInterpolationMode keyframeInterpolationMode)
        {
            CheckCompressedClipIsValid(compressedFloatsClip);
            CheckIndexIsValid(trackIndex);

            var mask = AclUnityCommon.GetArch();
            if (mask.avx)
            {
                return AVX.sampleFloat(compressedFloatsClip, trackIndex, time, (byte)keyframeInterpolationMode);
            }
            else if (mask.sse4)
            {
                return SSE4.sampleFloat(compressedFloatsClip, trackIndex, time, (byte)keyframeInterpolationMode);
            }
            else if (mask.neon)
            {
                return Neon.sampleFloat(compressedFloatsClip, trackIndex, time, (byte)keyframeInterpolationMode);
            }
            else
            {
                return NoExtensions.sampleFloat(compressedFloatsClip, trackIndex, time, (byte)keyframeInterpolationMode);
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckCompressedClipIsValid(void* compressedClip)
        {
            if (compressedClip == null)
                throw new ArgumentNullException("compressedClip is null");
            if (!CollectionHelper.IsAligned(compressedClip, 16))
                throw new ArgumentException("compressedClip is not aligned to a 16 byte boundary");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckOutputArrayIsCreated(NativeArray<Qvv> outputBuffer)
        {
            if (!outputBuffer.IsCreated || outputBuffer.Length == 0)
                throw new ArgumentException("outputBuffer is invalid");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckOutputArrayIsCreated(NativeArray<float> outputBuffer)
        {
            if (!outputBuffer.IsCreated || outputBuffer.Length == 0)
                throw new ArgumentException("outputBuffer is invalid");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckIndexIsValid(int index)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException("Bone or track index is negative");
        }

        static class NoExtensions
        {
#if UNITY_IPHONE
            const string dllName = "__Internal";
#else
            const string dllName = "AclUnity";
#endif
            [DllImport(dllName)]
            public static extern void samplePoseAOS(void* compressedTransformTracks, float* aosOutputBuffer, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern void samplePoseSOA(void* compressedTransformTracks, float* soaOutputBuffer, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern void sampleBone(void* compressedTransformTracks, float* boneQVV, int boneIndex, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern void sampleFloats(void* compressedFloatTracks, float* floatOutputBuffer, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern float sampleFloat(void* compressedFloatTracks, int trackIndex, float time, byte keyframeInterpolationMode);
        }

        static class AVX
        {
#if UNITY_IPHONE
            const string dllName = "__Internal";
#else
            const string dllName = "AclUnity_AVX";
#endif
            [DllImport(dllName)]
            public static extern void samplePoseAOS(void* compressedTransformTracks, float* aosOutputBuffer, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern void samplePoseSOA(void* compressedTransformTracks, float* soaOutputBuffer, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern void sampleBone(void* compressedTransformTracks, float* boneQVV, int boneIndex, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern void sampleFloats(void* compressedFloatTracks, float* floatOutputBuffer, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern float sampleFloat(void* compressedFloatTracks, int trackIndex, float time, byte keyframeInterpolationMode);
        }

        static class SSE4
        {
#if UNITY_IPHONE
            const string dllName = "__Internal";
#else
            const string dllName = "AclUnity_SSE4";
#endif
            [DllImport(dllName)]
            public static extern void samplePoseAOS(void* compressedTransformTracks, float* aosOutputBuffer, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern void samplePoseSOA(void* compressedTransformTracks, float* soaOutputBuffer, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern void sampleBone(void* compressedTransformTracks, float* boneQVV, int boneIndex, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern void sampleFloats(void* compressedFloatTracks, float* floatOutputBuffer, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern float sampleFloat(void* compressedFloatTracks, int trackIndex, float time, byte keyframeInterpolationMode);
        }

        static class Neon
        {
#if UNITY_IPHONE
            const string dllName = "__Internal";
#else
            const string dllName = "AclUnity_Neon";
#endif
            [DllImport(dllName)]
            public static extern void samplePoseAOS(void* compressedTransformTracks, float* aosOutputBuffer, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern void samplePoseSOA(void* compressedTransformTracks, float* soaOutputBuffer, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern void sampleBone(void* compressedTransformTracks, float* boneQVV, int boneIndex, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern void sampleFloats(void* compressedFloatTracks, float* floatOutputBuffer, float time, byte keyframeInterpolationMode);

            [DllImport(dllName)]
            public static extern float sampleFloat(void* compressedFloatTracks, int trackIndex, float time, byte keyframeInterpolationMode);
        }
    }
}

