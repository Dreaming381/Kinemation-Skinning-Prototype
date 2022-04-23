using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine.Rendering;

namespace Latios.Kinemation
{
    // Meshes
    internal struct SkeletonDependent : ISystemStateComponentData
    {
        public EntityWith<SkeletonRootTag>          root;
        public BlobAssetReference<MeshSkinningBlob> skinningBlob;
    }

    [MaterialProperty("_ComputeMeshIndex", MaterialPropertyFormat.Float)]
    internal struct ComputeDeformShaderIndex : IComponentData
    {
        public uint firstVertexIndex;
    }

    // All skeletons
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

    // Exposed skeletons
    internal struct ExposedSkeletonCullingIndex : IComponentData
    {
        public int cullingIndex;
    }

    internal struct BoneCullingIndex : IComponentData
    {
        public int cullingIndex;
    }

    internal struct BoneWorldBounds : IComponentData
    {
        public AABB bounds;
    }

    internal struct ChunkBoneWorldBounds : IComponentData
    {
        public AABB chunkBounds;
    }

    // Optimized skeletons
    internal struct SkeletonWorldBounds : IComponentData
    {
        public AABB bounds;
    }

    internal struct ChunkSkeletonWorldBounds : IComponentData
    {
        public AABB chunkBounds;
    }

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
