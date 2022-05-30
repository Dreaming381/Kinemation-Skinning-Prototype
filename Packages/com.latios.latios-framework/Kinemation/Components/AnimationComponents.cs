using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace Latios.Kinemation
{
    public struct BoneTransform
    {
        private quaternion m_rotation;
        private float4     m_translation;
        private float4     m_scale;

        public quaternion rotation { get => m_rotation; set => m_rotation              = value; }
        public float3 translation { get => m_translation.xyz; set => m_translation.xyz = value; }
        public float3 scale { get => m_scale.xyz; set => m_scale.xyz                   = value; }

        public BoneTransform(quaternion rotation, float3 translation, float3 scale)
        {
            m_rotation    = rotation;
            m_translation = new float4(translation, 0f);
            m_scale       = new float4(scale, 1f);
        }

        internal unsafe BoneTransform(AclUnity.Qvv qvv)
        {
            m_rotation    = qvv.rotation;
            m_translation = qvv.translation;
            m_scale       = qvv.scale;
        }
    }

    public enum KeyframeInterpolationMode : byte
    {
        Interpolate = 0,
        Floor = 1,
        Ceil = 2,
        Nearest = 3
    }

    public struct SkeletonClipSetBlob
    {
        public short                   boneCount;
        public BlobArray<SkeletonClip> clips;
    }

    public struct SkeletonClip
    {
        internal BlobArray<byte>   compressedClipDataAligned16;
        public float               duration;
        public FixedString128Bytes name;

        public unsafe BoneTransform SampleBone(int boneIndex, float time, KeyframeInterpolationMode keyframeInterpolationMode = KeyframeInterpolationMode.Interpolate)
        {
            var   mode         = (AclUnity.Decompression.KeyframeInterpolationMode)keyframeInterpolationMode;
            float wrappedTime  = math.fmod(time, duration);
            wrappedTime       += math.select(0f, duration, wrappedTime < 0f);

            var qvv = AclUnity.Decompression.SampleBone(compressedClipDataAligned16.GetUnsafePtr(), boneIndex, wrappedTime, mode);
            return new BoneTransform(qvv);
        }

        // Todo: SamplePose functions.
        // The reason these aren't originally supported is that they need a buffer to write to.
        // Since users will likely want to blend and stuff, that requires lots of temporary buffers
        // which will overflow Allocator.Temp.
        // The solution is a custom allocator that is rewindable per threadIndex.
        // But this was out of scope for the initial release of 0.5.
    }
}

