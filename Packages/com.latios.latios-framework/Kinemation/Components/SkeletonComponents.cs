using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Latios.Kinemation
{
    #region All Skeletons
    public struct SkeletonRootTag : IComponentData { }

    // Usage: Typically Read Only (Add/Write for procedural skeletons)
    public struct BoneOwningSkeletonReference : IComponentData
    {
        public EntityWith<SkeletonRootTag> skeletonRoot;
    }

    // Usage: Typically Read Only
    public struct ChunkPerCameraSkeletonCullingMask : IComponentData
    {
        public BitField64 lower;
        public BitField64 upper;
    }

    #endregion
    #region Exposed skeleton
    public struct BoneTag : IComponentData { }

    // Usage: Typically Read Only (Add/Write for procedural skeletons)
    public struct BoneIndex : IComponentData
    {
        public int index;
    }

    // Usage: Typically Read Only (Add/Write for procedural skeletons)
    public struct BoneBindPose : IComponentData
    {
        public float4x4 bindPose;
    }

    // Usage: Typically Read Only (Add/Write for procedural skeletons)
    public struct BoneBounds : IComponentData
    {
        public float radialOffsetInBoneSpace;
    }

    // Usage: Typically Read Only (Add/Write for procedural skeletons)
    // Lives on Skeleton Root
    public struct BoneReference : IBufferElementData
    {
        public EntityWith<BoneTag> bone;
    }

    #endregion
    #region Optimized skeleton
    public struct OptimizedBindSkeletonBlob
    {
        public BlobArray<float4x4> bindPoses;

        // Max of 32767 bones
        public BlobArray<short> parentIndices;
    }

    // Usage: Typically Read Only (Add/Write for procedural skeletons)
    public struct OptimizedBindSkeletonBlobReference : IComponentData
    {
        public BlobAssetReference<OptimizedBindSkeletonBlob> blob;
    }

    // Usage: Read or Write for Animations
    public struct OptimizedBoneToRoot : IBufferElementData
    {
        public float4x4 boneToRoot;
    }

    // Usage: Typically Read Only (Add/Write for procedural skeletons)
    // The length of this should be 0 when no meshes are bound.
    public struct OptimizedBoneBounds : IBufferElementData
    {
        public float radialOffsetInBoneSpace;
    }

    // Todo: Need LocalToParent version.
    [WriteGroup(typeof(Unity.Transforms.LocalToWorld))]
    public struct CopyLocalToWorldFromBone : IComponentData
    {
        public short boneIndex;
    }
    #endregion
}

