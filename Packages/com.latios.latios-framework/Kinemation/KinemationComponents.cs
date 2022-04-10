using System;
using System.Collections.Generic;
using Latios.Psyshock;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine.Rendering;

namespace Latios.Kinemation
{
    #region Meshes

    // This is a WriteGroup target.
    public struct ChunkPerCameraCullingMask : IComponentData
    {
        public BitField64 lower;
        public BitField64 upper;
    }

    // Warning: Do not write to this component!
    // This is marked WriteGroup to ensure normal unskinned meshes can use write group filtering.
    // Include this if you choose to use WriteGroup filtering yourself.
    [WriteGroup(typeof(ChunkPerCameraCullingMask))]
    public struct ChunkPerFrameCullingMask : IComponentData
    {
        public BitField64 lower;
        public BitField64 upper;
    }

    public struct BindSkeletonRoot : IComponentData
    {
        public EntityWith<SkeletonRootTag> root;
    }

    internal struct SkeletonDependent : ISystemStateComponentData
    {
        public EntityWith<SkeletonRootTag>          root;
        public BlobAssetReference<MeshSkinningBlob> skinningBlob;
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

    [MaterialProperty("_ComputeMeshIndex", MaterialPropertyFormat.Float)]
    internal struct ComputeDeformShaderIndex : IComponentData
    {
        public uint firstVertexIndex;
    }

    [WriteGroup(typeof(ChunkPerCameraCullingMask))]
    internal struct ChunkComputeDeformMemoryMetadata : IComponentData
    {
        public int vertexStartPrefixSum;
        public int verticesPerMesh;
        public int entitiesInChunk;
    }
    #endregion
    #region All Skeletons
    public struct SkeletonRootTag : IComponentData { }

    public struct BoneOwningSkeletonReference : IComponentData
    {
        public EntityWith<SkeletonRootTag> skeletonRoot;
    }

    public struct ChunkPerCameraSkeletonCullingMask : IComponentData
    {
        public BitField64 lower;
        public BitField64 upper;
    }

    // This is system state to prevent copies on instantiate
    internal struct DependentSkinnedMesh : ISystemStateBufferElementData
    {
        public EntityWith<SkeletonDependent> skinnedMesh;
        public int                           meshVerticesStart;
        public int                           meshVerticesCount;  // Todo: Replace with blob for mesh splitting?
        public int                           meshWeightsStart;
    }

    [MaximumChunkCapacity(128)]
    internal struct PerFrameSkeletonBufferMetadata : IComponentData
    {
        public int bufferId;
        public int startIndexInBuffer;
    }

    #endregion
    #region Exposed skeleton
    public struct BoneTag : IComponentData { }

    public struct BoneIndex : IComponentData
    {
        public int index;
    }

    public struct BoneBindPose : IComponentData
    {
        public float4x4 bindPose;
    }

    public struct BoneBounds : IComponentData
    {
        public float radialOffsetInBoneSpace;
    }

    internal struct ExposedSkeletonCullingIndex : IComponentData
    {
        public int cullingIndex;
    }

    internal struct BoneCullingIndex : IComponentData
    {
        public int cullingIndex;
    }

    public struct BoneReference : IBufferElementData
    {
        public EntityWith<BoneTag> bone;
    }

    internal struct BoneWorldBounds : IComponentData
    {
        public AABB bounds;
    }

    internal struct ChunkBoneWorldBounds : IComponentData
    {
        public AABB chunkBounds;
    }
    #endregion
    #region Optimized skeleton
    public struct OptimizedBindSkeletonBlob
    {
        public BlobArray<float4x4> bindPoses;

        // Max of 32767 bones
        public BlobArray<short> parentIndices;
    }

    public struct OptimizedBindSkeletonBlobReference : IComponentData
    {
        public BlobAssetReference<OptimizedBindSkeletonBlob> blob;
    }

    public struct OptimizedBoneToRoot : IBufferElementData
    {
        public float4x4 boneToRoot;
    }

    // The length of this is 0 when no meshes are bound.
    public struct OptimizedBoneBounds : IBufferElementData
    {
        public float radialOffsetInBoneSpace;
    }

    internal struct SkeletonWorldBounds : IComponentData
    {
        public AABB bounds;
    }

    internal struct ChunkSkeletonWorldBounds : IComponentData
    {
        public AABB chunkBounds;
    }

    [WriteGroup(typeof(Unity.Transforms.LocalToWorld))]
    public struct CopyLocalToWorldFromBone : IComponentData
    {
        public short boneIndex;
    }
    #endregion
    #region Blackboard
    internal struct LastFrameRenderedNotRenderedVerticesTag : IComponentData { }

    internal struct MeshGpuManagerTag : IComponentData { }

    internal struct MeshGpuUploadCommand
    {
        public BlobAssetReference<MeshSkinningBlob> blob;
        public int                                  verticesIndex;
        public int                                  weightsIndex;
    }

    internal struct MeshGpuManager : ICollectionComponent
    {
        // Todo: Bug in BlobAssetReference.GetHashCode makes it not Burst-compatible.
        // So we need to wrap it and override the GetHashCode function.
        // Maybe we could provide an IHasher version of NativeHashMap similar to STL?
        public NativeHashMap<MeshSkinningBlobReference, int> blobIndexMap;

        public NativeList<int> referenceCounts;
        public NativeList<int> verticesStarts;
        public NativeList<int> weightsStarts;

        public NativeList<int>  indexFreeList;
        public NativeList<int2> verticesGaps;
        public NativeList<int2> weightsGaps;

        public NativeList<MeshGpuUploadCommand> uploadCommands;
        public NativeReference<int4>            requiredVertexWeightsbufferSizesAndUploadSizes;  // vertex buffer, weight buffer, vertex upload, weight upload

        public Type AssociatedComponentType => typeof(MeshGpuManagerTag);

        public JobHandle Dispose(JobHandle inputDeps)
        {
            inputDeps = blobIndexMap.Dispose(inputDeps);
            inputDeps = referenceCounts.Dispose(inputDeps);
            inputDeps = verticesStarts.Dispose(inputDeps);
            inputDeps = weightsStarts.Dispose(inputDeps);
            inputDeps = indexFreeList.Dispose(inputDeps);
            inputDeps = verticesGaps.Dispose(inputDeps);
            inputDeps = weightsGaps.Dispose(inputDeps);
            inputDeps = uploadCommands.Dispose(inputDeps);
            inputDeps = requiredVertexWeightsbufferSizesAndUploadSizes.Dispose(inputDeps);
            return inputDeps;
        }
    }

    // Todo: Combine with above once ref collections are supported
    internal struct MeshGpuUploadBuffers : ICollectionComponent
    {
        // Not owned by this
        public UnityEngine.ComputeBuffer verticesBuffer;
        public UnityEngine.ComputeBuffer weightsBuffer;
        public UnityEngine.ComputeBuffer verticesUploadBuffer;
        public UnityEngine.ComputeBuffer weightsUploadBuffer;
        public UnityEngine.ComputeBuffer verticesUploadMetaBuffer;
        public UnityEngine.ComputeBuffer weightsUploadMetaBuffer;
        public int                       verticesUploadBufferWriteCount;
        public int                       weightsUploadBufferWriteCount;
        public int                       verticesUploadMetaBufferWriteCount;
        public int                       weightsUploadMetaBufferWriteCount;
        public bool                      needsCommitment;

        public UnityEngine.ComputeShader uploadVerticesShader;
        public UnityEngine.ComputeShader uploadBytesShader;

        public Type AssociatedComponentType => typeof(MeshGpuManagerTag);

        public JobHandle Dispose(JobHandle inputDeps) => inputDeps;

        public void Dispatch()
        {
            if (!needsCommitment)
                return;

            verticesUploadBuffer.EndWrite<VertexToSkin>(verticesUploadBufferWriteCount);
            verticesUploadMetaBuffer.EndWrite<uint3>(verticesUploadMetaBufferWriteCount);
            weightsUploadBuffer.EndWrite<BoneWeightLinkedList>(weightsUploadBufferWriteCount);
            weightsUploadMetaBuffer.EndWrite<uint3>(weightsUploadMetaBufferWriteCount);

            uploadVerticesShader.SetBuffer(0, "_dst",  verticesBuffer);
            uploadVerticesShader.SetBuffer(0, "_src",  verticesUploadBuffer);
            uploadVerticesShader.SetBuffer(0, "_meta", verticesUploadMetaBuffer);

            for (int dispatchesRemaining = verticesUploadMetaBufferWriteCount, offset = 0; dispatchesRemaining > 0;)
            {
                int dispatchCount = math.min(dispatchesRemaining, 65535);
                uploadVerticesShader.SetInt("_startOffset", offset);
                uploadVerticesShader.Dispatch(0, dispatchCount, 1, 1);
                offset              += dispatchCount;
                dispatchesRemaining -= dispatchCount;
            }

            uploadBytesShader.SetBuffer(0, "_dst",  weightsBuffer);
            uploadBytesShader.SetBuffer(0, "_src",  weightsUploadBuffer);
            uploadBytesShader.SetBuffer(0, "_meta", weightsUploadMetaBuffer);
            uploadBytesShader.SetInt("_elementSizeInBytes", 8);

            for (int dispatchesRemaining = weightsUploadMetaBufferWriteCount, offset = 0; dispatchesRemaining > 0;)
            {
                int dispatchCount = math.min(dispatchesRemaining, 65535);
                uploadBytesShader.SetInt("_startOffset", offset);
                uploadBytesShader.Dispatch(0, dispatchCount, 1, 1);
                offset              += dispatchCount;
                dispatchesRemaining -= dispatchCount;
            }

            needsCommitment = false;
        }
    }

    internal struct ExposedCullingIndexManagerTag : IComponentData { }

    internal struct ExposedCullingIndexManager : ICollectionComponent
    {
        public NativeHashMap<Entity, int> skeletonIndexMap;
        public NativeReference<int>       maxIndex;
        public NativeList<int>            indexFreeList;

        public Type AssociatedComponentType => typeof(ExposedCullingIndexManagerTag);

        public JobHandle Dispose(JobHandle inputDeps)
        {
            inputDeps = skeletonIndexMap.Dispose(inputDeps);
            inputDeps = maxIndex.Dispose(inputDeps);
            inputDeps = indexFreeList.Dispose(inputDeps);
            return inputDeps;
        }
    }

    internal struct ComputeBufferManagerTag : IComponentData { }

    internal struct ComputeBufferManager : ICollectionComponent
    {
        public ComputeBufferTrackingPool pool;

        public Type AssociatedComponentType => typeof(ComputeBufferManagerTag);

        public JobHandle Dispose(JobHandle inputDeps)
        {
            pool.Dispose();
            return inputDeps;
        }
    }

    public struct CullingPlane : IBufferElementData
    {
        public UnityEngine.Plane plane;
    }

    public struct CullingContext : IComponentData
    {
        public LODParameters lodParameters;
        public float4x4      cullingMatrix;
        public float         nearPlane;
        public int           cullIndexThisFrame;
    }

    internal struct BrgCullingContextTag : IComponentData { }

    internal unsafe struct BrgCullingContext : ICollectionComponent
    {
        public BatchCullingContext cullingContext;
        public NativeArray<int>    internalToExternalMappingIds;

        public Type AssociatedComponentType => typeof(BrgCullingContextTag);

        // We don't own this data
        public JobHandle Dispose(JobHandle inputDeps)
        {
            return inputDeps;
        }
    }

    internal struct BoneMatricesPerFrameBuffersManagerTag : IComponentData { }

    internal struct BoneMatricesPerFrameBuffersManager : ICollectionComponent
    {
        public List<UnityEngine.ComputeBuffer> boneMatricesBuffers;

        public Type AssociatedComponentType => typeof(BoneMatricesPerFrameBuffersManagerTag);

        // We don't own the buffers.
        public JobHandle Dispose(JobHandle inputDeps)
        {
            return inputDeps;
        }
    }

    internal struct MaxRequiredDeformVertices : IComponentData
    {
        public int verticesCount;
    }
    #endregion
}

