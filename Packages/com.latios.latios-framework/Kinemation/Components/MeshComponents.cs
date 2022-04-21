using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Latios.Kinemation
{
    public struct BindSkeletonRoot : IComponentData
    {
        public EntityWith<SkeletonRootTag> root;
    }

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
        public BlobArray<float>                maxRadialOffsetsInBoneSpaceByBone;
        public BlobArray<VertexToSkin>         verticesToSkin;
        public BlobArray<BoneWeightLinkedList> boneWeights;
        public BlobArray<uint>                 boneWeightBatchStarts;
        public Hash128                         authoredHash;
        public FixedString128Bytes             name;

        public override int GetHashCode() => authoredHash.GetHashCode();
    }

    public struct MeshSkinningBlobReference : IComponentData, IEquatable<MeshSkinningBlobReference>
    {
        public BlobAssetReference<MeshSkinningBlob> blob;

        public bool Equals(MeshSkinningBlobReference other) => blob == other.blob;
        public override int GetHashCode() => blob.Value.GetHashCode();
    }
}

