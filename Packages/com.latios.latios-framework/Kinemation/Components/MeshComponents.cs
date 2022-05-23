using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Latios.Kinemation
{
    // Usage: Add/Write this component to true whenever binding needs to occur.
    // This will become an Enabled component tag in 1.0.
    // Binding must occur whenever the presence or value of any of the following changes:
    // - BindSkeletonRoot
    // - MeshSkinningBlobReference
    // - MeshBindingPathsBlobReference
    // - OverrideSkinningBoneIndex
    // - ShaderEffectRadialBounds
    // - LockToSkeletonRootTag
    // An initial attempt at binding will be made when the SkeletonMeshBindingReactiveSystem
    // first processes a mesh entity, even without this flag component.
    // However, if the flag component is present and set to false at this time, no binding
    // attempt will be made.
    public struct NeedsBindingFlag : IComponentData
    {
        public bool needsBinding;
    }

    // Usage: Add/Write to attach a skinned mesh to a skeleton
    [MaximumChunkCapacity(128)]
    public struct BindSkeletonRoot : IComponentData
    {
        public EntityWith<SkeletonRootTag> root;
    }

    // Usage: Typically Read Only (Add/Write for procedural meshes)
    public struct MeshSkinningBlobReference : IComponentData
    {
        public BlobAssetReference<MeshSkinningBlob> blob;
    }

    // Usage: Typically Read Only (Add/Write for procedural meshes)
    public struct MeshBindingPathsBlobReference : IComponentData
    {
        public BlobAssetReference<MeshBindingPathsBlob> blob;
    }

    // Usage: Add/Write to create a custom mapping from mesh bone indices to
    // skeleton bone indices. The length of this buffer must match the length
    // of MeshSkinningBlob.bindPoses.
    public struct OverrideSkinningBoneIndex : IBufferElementData
    {
        public short boneOffset;
    }

    // Usage: Add/Write to apply additional bounds inflation for a mesh
    // that modifies vertex positions in its shader (aside from skinning)
    public struct ShaderEffectRadialBounds : IComponentData
    {
        public float radialBounds;
    }

    // Usage: Add to share a skin from another entity.
    // This is useful when RenderMeshes share a mesh but have different materials.
    public struct ShareSkinFromEntity : IComponentData
    {
        public EntityWith<MeshSkinningBlobReference> sourceSkinnedEntity;
    }

    // Usage: Query to get entity where all children are failed bindings.
    // Currently the transform system (including framework variants) will
    // crash if a Parent has a Null entity value.
    // For performance reasons, all bound entities have Parent and LocalToParent
    // components added even if the bindings failed. So entities with failed
    // bindings are parented to a singleton entity with this tag instead.
    public struct FailedBindingsRootTag : IComponentData { }

    #region BlobData
    public struct BoneWeightLinkedList
    {
        public float weight;
        public uint  next10Lds7Bone15;
    }

    public struct VertexToSkin
    {
        public float3 position;
        public float3 normal;
        public float3 tangent;
    }

    public struct MeshSkinningBlob
    {
        public BlobArray<float4x4>             bindPoses;
        public BlobArray<float>                maxRadialOffsetsInBoneSpaceByBone;
        public BlobArray<VertexToSkin>         verticesToSkin;
        public BlobArray<BoneWeightLinkedList> boneWeights;
        public BlobArray<uint>                 boneWeightBatchStarts;
        public FixedString128Bytes             name;
    }

    public struct MeshBindingPathsBlob
    {
        // Todo: Make this a BlobArray<BlobString> once supported in Burst
        public BlobArray<BlobArray<byte> > pathsInReversedNotation;
    }
    #endregion
}

