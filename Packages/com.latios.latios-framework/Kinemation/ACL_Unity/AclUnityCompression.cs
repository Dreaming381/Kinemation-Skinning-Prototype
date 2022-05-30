using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace AclUnity
{
    public static unsafe class Compression
    {
        public struct AclCompressedClipResult : IDisposable
        {
            public NativeArray<byte>.ReadOnly compressedDataToCopyFrom => compressedData.AsReadOnly();
            internal NativeArray<byte> compressedData;

            public void Dispose()
            {
                DisposeCompressedTrack(this);
            }
        }

        public struct SkeletonCompressionSettings
        {
            public short compressionLevel;
            public float maxDistanceError;
            public float sampledErrorDistanceFromBone;
            public float maxNegligibleTranslationDrift;
            public float maxNegligibleScaleDrift;
        }

        public static readonly SkeletonCompressionSettings kDefaultSettings = new SkeletonCompressionSettings
        {
            compressionLevel              = 2,
            maxDistanceError              = 0.0001f,
            sampledErrorDistanceFromBone  = 0.03f,
            maxNegligibleScaleDrift       = 0.00001f,
            maxNegligibleTranslationDrift = 0.00001f
        };

        public static AclCompressedClipResult CompressSkeletonClip(NativeArray<short>          parentIndices,
                                                                   NativeArray<Qvv>            aosClipData,
                                                                   float sampleRate,
                                                                   SkeletonCompressionSettings settings
                                                                   )
        {
            CheckParentIndicesIsValid(parentIndices);
            CheckClipDataIsValid(aosClipData, parentIndices.Length);
            CheckSampleRateIsValid(sampleRate);
            CheckSkeletonSettingsIsValid(settings);

            var alignedClipData = aosClipData.GetUnsafeReadOnlyPtr();
            if (!CollectionHelper.IsAligned(alignedClipData, 16))
            {
                alignedClipData = UnsafeUtility.Malloc(UnsafeUtility.SizeOf<Qvv>() * aosClipData.Length, math.max(UnsafeUtility.AlignOf<Qvv>(), 16), Allocator.TempJob);
                UnsafeUtility.MemCpy(alignedClipData, aosClipData.GetUnsafeReadOnlyPtr(), UnsafeUtility.SizeOf<Qvv>() * aosClipData.Length);
            }

            int   outCompressedSizeInBytes = 0;
            void* compressedClipPtr;

            var mask = AclUnityCommon.GetArch();
            if (mask.avx)
            {
                compressedClipPtr = AVX.compressSkeletonClip((short*)parentIndices.GetUnsafeReadOnlyPtr(),
                                                             (short)parentIndices.Length,
                                                             settings.compressionLevel,
                                                             (float*)alignedClipData,
                                                             aosClipData.Length,
                                                             sampleRate,
                                                             settings.maxDistanceError,
                                                             settings.sampledErrorDistanceFromBone,
                                                             settings.maxNegligibleTranslationDrift,
                                                             settings.maxNegligibleScaleDrift,
                                                             &outCompressedSizeInBytes
                                                             );
            }
            else if (mask.sse4)
            {
                compressedClipPtr = SSE4.compressSkeletonClip((short*)parentIndices.GetUnsafeReadOnlyPtr(),
                                                              (short)parentIndices.Length,
                                                              settings.compressionLevel,
                                                              (float*)alignedClipData,
                                                              aosClipData.Length,
                                                              sampleRate,
                                                              settings.maxDistanceError,
                                                              settings.sampledErrorDistanceFromBone,
                                                              settings.maxNegligibleTranslationDrift,
                                                              settings.maxNegligibleScaleDrift,
                                                              &outCompressedSizeInBytes
                                                              );
            }
            else if (mask.neon)
            {
                compressedClipPtr = Neon.compressSkeletonClip((short*)parentIndices.GetUnsafeReadOnlyPtr(),
                                                              (short)parentIndices.Length,
                                                              settings.compressionLevel,
                                                              (float*)alignedClipData,
                                                              aosClipData.Length,
                                                              sampleRate,
                                                              settings.maxDistanceError,
                                                              settings.sampledErrorDistanceFromBone,
                                                              settings.maxNegligibleTranslationDrift,
                                                              settings.maxNegligibleScaleDrift,
                                                              &outCompressedSizeInBytes
                                                              );
            }
            else
            {
                compressedClipPtr = NoExtensions.compressSkeletonClip((short*)parentIndices.GetUnsafeReadOnlyPtr(),
                                                                      (short)parentIndices.Length,
                                                                      settings.compressionLevel,
                                                                      (float*)alignedClipData,
                                                                      aosClipData.Length,
                                                                      sampleRate,
                                                                      settings.maxDistanceError,
                                                                      settings.sampledErrorDistanceFromBone,
                                                                      settings.maxNegligibleTranslationDrift,
                                                                      settings.maxNegligibleScaleDrift,
                                                                      &outCompressedSizeInBytes
                                                                      );
            }

            if (aosClipData.GetUnsafeReadOnlyPtr() != alignedClipData)
            {
                UnsafeUtility.Free(alignedClipData, Allocator.TempJob);
            }

            return new AclCompressedClipResult
            {
                compressedData = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(compressedClipPtr, outCompressedSizeInBytes, Allocator.None),
            };
        }

        public static AclCompressedClipResult CompressScalarsClip(NativeArray<float> clipData,
                                                                  NativeArray<float> maxErrorsByTrack,
                                                                  float sampleRate,
                                                                  short compressionLevel
                                                                  )
        {
            CheckErrorsByTrackIsValid(maxErrorsByTrack);
            CheckClipDataIsValid(clipData, maxErrorsByTrack.Length);
            CheckSampleRateIsValid(sampleRate);
            CheckCompressionLevelIsValid(compressionLevel);

            int   outCompressedSizeInBytes = 0;
            void* compressedClipPtr;

            var mask = AclUnityCommon.GetArch();
            if (mask.avx)
            {
                compressedClipPtr = AVX.compressScalarsClip((short)maxErrorsByTrack.Length,
                                                            compressionLevel,
                                                            (float*)clipData.GetUnsafeReadOnlyPtr(),
                                                            clipData.Length / maxErrorsByTrack.Length,
                                                            sampleRate,
                                                            (float*)maxErrorsByTrack.GetUnsafeReadOnlyPtr(),
                                                            &outCompressedSizeInBytes);
            }
            else if (mask.sse4)
            {
                compressedClipPtr = SSE4.compressScalarsClip((short)maxErrorsByTrack.Length,
                                                             compressionLevel,
                                                             (float*)clipData.GetUnsafeReadOnlyPtr(),
                                                             clipData.Length / maxErrorsByTrack.Length,
                                                             sampleRate,
                                                             (float*)maxErrorsByTrack.GetUnsafeReadOnlyPtr(),
                                                             &outCompressedSizeInBytes);
            }
            else if (mask.neon)
            {
                compressedClipPtr = Neon.compressScalarsClip((short)maxErrorsByTrack.Length,
                                                             compressionLevel,
                                                             (float*)clipData.GetUnsafeReadOnlyPtr(),
                                                             clipData.Length / maxErrorsByTrack.Length,
                                                             sampleRate,
                                                             (float*)maxErrorsByTrack.GetUnsafeReadOnlyPtr(),
                                                             &outCompressedSizeInBytes);
            }
            else
            {
                compressedClipPtr = NoExtensions.compressScalarsClip((short)maxErrorsByTrack.Length,
                                                                     compressionLevel,
                                                                     (float*)clipData.GetUnsafeReadOnlyPtr(),
                                                                     clipData.Length / maxErrorsByTrack.Length,
                                                                     sampleRate,
                                                                     (float*)maxErrorsByTrack.GetUnsafeReadOnlyPtr(),
                                                                     &outCompressedSizeInBytes);
            }

            return new AclCompressedClipResult
            {
                compressedData = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(compressedClipPtr, outCompressedSizeInBytes, Allocator.None),
            };
        }

        // Note: It shouldn't matter which DLL actually does the disposal since
        // this is a movable serializable type. So we don't have to worry about
        // Burst races in the Editor.
        internal static void DisposeCompressedTrack(AclCompressedClipResult clip)
        {
            var mask = AclUnityCommon.GetArch();
            if (mask.avx)
                AVX.disposeCompressedTracksBuffer(clip.compressedData.GetUnsafePtr());
            else if (mask.sse4)
                SSE4.disposeCompressedTracksBuffer(clip.compressedData.GetUnsafePtr());
            else if (mask.neon)
                Neon.disposeCompressedTracksBuffer(clip.compressedData.GetUnsafePtr());
            else
                NoExtensions.disposeCompressedTracksBuffer(clip.compressedData.GetUnsafePtr());
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckParentIndicesIsValid(NativeArray<short> parentIndices)
        {
            if (!parentIndices.IsCreated || parentIndices.Length == 0)
                throw new ArgumentException("parentIndices is invalid");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckClipDataIsValid(NativeArray<Qvv> aosClipData, int boneCount)
        {
            if (!aosClipData.IsCreated || aosClipData.Length == 0)
                throw new ArgumentException("aosClipData is invalid");
            if (aosClipData.Length % boneCount != 0 || aosClipData.Length < boneCount)
                throw new ArgumentException("aosClipData is not sized correctly relative to the bone count");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckErrorsByTrackIsValid(NativeArray<float> maxErrorsByTrack)
        {
            if (!maxErrorsByTrack.IsCreated || maxErrorsByTrack.Length == 0)
                throw new ArgumentException("maxErrorsByTrack is invalid");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckClipDataIsValid(NativeArray<float> clipData, int trackCount)
        {
            if (!clipData.IsCreated || clipData.Length == 0)
                throw new ArgumentException("clipData is invalid");
            if (clipData.Length % trackCount != 0 || clipData.Length < trackCount)
                throw new ArgumentException("clipData is not sized correctly relative to the track count");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckSampleRateIsValid(float sampleRate)
        {
            if (sampleRate <= math.EPSILON)
                throw new ArgumentOutOfRangeException("sampleRate is negative or too small");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckCompressionLevelIsValid(short compressionLevel)
        {
            var clampedLevel = math.clamp(compressionLevel, 0, 4);
            if (compressionLevel != clampedLevel)
                throw new ArgumentOutOfRangeException("compressionLevel must be between 0 (lowest/fastest_to_compress) and 4 (highest/slowest_to_compress)");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckSkeletonSettingsIsValid(SkeletonCompressionSettings settings)
        {
            CheckCompressionLevelIsValid(settings.compressionLevel);

            if (settings.maxDistanceError <= math.EPSILON)
                throw new ArgumentOutOfRangeException("maxDistanceError is negative or too small");
            if (settings.maxNegligibleScaleDrift <= math.EPSILON)
                throw new ArgumentOutOfRangeException("maxNegligivelScaleDrift is negative or too small");
            if (settings.maxNegligibleTranslationDrift <= math.EPSILON)
                throw new ArgumentOutOfRangeException("maxNegligibleTranslationDrift is negative or too small");
            if (settings.sampledErrorDistanceFromBone <= math.EPSILON)
                throw new ArgumentOutOfRangeException("sampledErrorDistanceFromBone is negative or too small");
        }

        static class NoExtensions
        {
#if UNITY_IPHONE
            const string dllName = "__Internal";
#else
            const string dllName = "AclUnity";
#endif
            [DllImport(dllName)]
            public static extern void* compressSkeletonClip(short* parentIndices,
                                                            short numBones,
                                                            short compressionLevel,
                                                            float* aosClipData,
                                                            int numSamples,
                                                            float sampleRate,
                                                            float maxDistanceError,
                                                            float sampledErrorDistanceFromBone,
                                                            float maxNegligibleTranslationDrift,
                                                            float maxNegligibleScaleDrift,
                                                            int*   outCompressedSizeInBytes);

            [DllImport(dllName)]
            public static extern void* compressScalarsClip(short numTracks,
                                                           short compressionLevel,
                                                           float* clipData,
                                                           int numSamples,
                                                           float sampleRate,
                                                           float* maxErrors,
                                                           int*   outCompressedSizeInBytes);

            [DllImport(dllName)]
            public static extern void* disposeCompressedTracksBuffer(void* compressedTracksBuffer);
        }

        static class AVX
        {
#if UNITY_IPHONE
            const string dllName = "__Internal";
#else
            const string dllName = "AclUnity_AVX";
#endif
            [DllImport(dllName)]
            public static extern void* compressSkeletonClip(short* parentIndices,
                                                            short numBones,
                                                            short compressionLevel,
                                                            float* aosClipData,
                                                            int numSamples,
                                                            float sampleRate,
                                                            float maxDistanceError,
                                                            float sampledErrorDistanceFromBone,
                                                            float maxNegligibleTranslationDrift,
                                                            float maxNegligibleScaleDrift,
                                                            int*   outCompressedSizeInBytes);

            [DllImport(dllName)]
            public static extern void* compressScalarsClip(short numTracks,
                                                           short compressionLevel,
                                                           float* clipData,
                                                           int numSamples,
                                                           float sampleRate,
                                                           float* maxErrors,
                                                           int*   outCompressedSizeInBytes);

            [DllImport(dllName)]
            public static extern void* disposeCompressedTracksBuffer(void* compressedTracksBuffer);
        }

        static class SSE4
        {
#if UNITY_IPHONE
            const string dllName = "__Internal";
#else
            const string dllName = "AclUnity_SSE4";
#endif
            [DllImport(dllName)]
            public static extern void* compressSkeletonClip(short* parentIndices,
                                                            short numBones,
                                                            short compressionLevel,
                                                            float* aosClipData,
                                                            int numSamples,
                                                            float sampleRate,
                                                            float maxDistanceError,
                                                            float sampledErrorDistanceFromBone,
                                                            float maxNegligibleTranslationDrift,
                                                            float maxNegligibleScaleDrift,
                                                            int*   outCompressedSizeInBytes);

            [DllImport(dllName)]
            public static extern void* compressScalarsClip(short numTracks,
                                                           short compressionLevel,
                                                           float* clipData,
                                                           int numSamples,
                                                           float sampleRate,
                                                           float* maxErrors,
                                                           int*   outCompressedSizeInBytes);

            [DllImport(dllName)]
            public static extern void* disposeCompressedTracksBuffer(void* compressedTracksBuffer);
        }

        static class Neon
        {
#if UNITY_IPHONE
            const string dllName = "__Internal";
#else
            const string dllName = "AclUnity_Neon";
#endif
            [DllImport(dllName)]
            public static extern void* compressSkeletonClip(short* parentIndices,
                                                            short numBones,
                                                            short compressionLevel,
                                                            float* aosClipData,
                                                            int numSamples,
                                                            float sampleRate,
                                                            float maxDistanceError,
                                                            float sampledErrorDistanceFromBone,
                                                            float maxNegligibleTranslationDrift,
                                                            float maxNegligibleScaleDrift,
                                                            int*   outCompressedSizeInBytes);

            [DllImport(dllName)]
            public static extern void* compressScalarsClip(short numTracks,
                                                           short compressionLevel,
                                                           float* clipData,
                                                           int numSamples,
                                                           float sampleRate,
                                                           float* maxErrors,
                                                           int*   outCompressedSizeInBytes);

            [DllImport(dllName)]
            public static extern void* disposeCompressedTracksBuffer(void* compressedTracksBuffer);
        }
    }
}

