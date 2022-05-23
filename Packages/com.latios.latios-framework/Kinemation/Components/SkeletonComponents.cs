using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Latios.Kinemation
{
    #region All Skeletons

    // Usage: Required for a skeleton to be valid.
    // Add/Write for procedural skeletons.
    public struct SkeletonRootTag : IComponentData { }

    // Usage: Typically Read Only (Add/Write for procedural skeletons)
    // This component is only used internally to point mesh bindings
    // to the skeleton root. After conversion, it is not maintained
    // internally. If you make procedural changes to the skeleton,
    // you are responsible for maintaining this component.
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

    public struct SkeletonBindingPathsBlob
    {
        // Todo: Make this a BlobArray<BlobString> once supported in Burst
        public BlobArray<BlobArray<byte> > pathsInReversedNotation;
    }

    // Usage: Typically Read Only (Add/Write for procedural skeletons)
    public struct SkeletonBindingPathsBlobReference : IComponentData
    {
        public BlobAssetReference<SkeletonBindingPathsBlob> blob;
    }

    #endregion
    #region Exposed skeleton

    // Usage: Typically Read Only (Add/Write for procedural skeletons)
    // This component is added during conversion for user convenience
    // and is written to by SkeletonMeshBindingReactiveSystem but never read.
    public struct BoneIndex : IComponentData
    {
        public short index;
    }

    // Usage: Typically Read Only (Add/Write for procedural skeletons)
    // Lives on the Skeleton Root. All LocalToWorld values will be used as
    // bone matrices for skinning purposes. The first bone is the reference
    // space for deformations and should be the skeleton root entity.
    // If creating bones from scratch, you also should call
    // CullingUtilities.GetBoneCullingComponentTypes() and add to each bone
    // in this buffer. After the components have been added, you must set the
    // BoneReferenceIsDirtyFlag to true (you may need to add that component).
    // The bones will be synchronized with the skeleton during
    // SkeletonMeshBindingReactiveSystem. You do not need to set the flag
    // if the system has not processed the skeleton at least once yet.
    //
    // WARNING: If a bone with a BoneIndex or the culling components is added
    // to multiple BoneReference buffers, there will be a data race!
    [InternalBufferCapacity(0)]
    public struct BoneReference : IBufferElementData
    {
        public EntityWith<LocalToWorld> bone;
    }

    // Usage: If a skeleton has this component and a value of true,
    // it will synchronize its skeleton with all the bones in the buffer,
    // populating the BoneIndex, removing old bones from culling, and
    // allowing new bones to report culling.
    // This happens during SkeletonMeshBindingReactiveSystem and is only
    // required if you modify the BoneReference buffer after that system
    // has ran on the skeleton entity once.
    public struct BoneReferenceIsDirtyFlag : IComponentData
    {
        public bool isDirty;
    }

    #endregion
    #region Optimized skeleton
    public struct OptimizedSkeletonHierarchyBlob
    {
        // Max of 32767 bones
        public BlobArray<short>      parentIndices;
        public BlobArray<BitField64> hasParentScaleInverseBitmask;
    }

    // Usage: Typically Read Only (Add/Write for procedural skeletons)
    public struct OptimizedSkeletonHierarchyBlobReference : IComponentData
    {
        public BlobAssetReference<OptimizedSkeletonHierarchyBlob> blob;
    }

    // Usage: Read or Write for Animations
    [InternalBufferCapacity(0)]
    public struct OptimizedBoneToRoot : IBufferElementData
    {
        public float4x4 boneToRoot;
    }

    [WriteGroup(typeof(LocalToParent))]
    public struct CopyLocalToParentFromBone : IComponentData
    {
        public short boneIndex;
    }
    #endregion
}

