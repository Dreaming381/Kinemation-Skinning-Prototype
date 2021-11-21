using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Latios.Kinemation.Systems
{
    public class BeginPerFrameMeshSkinningBuffersUploadSystem : SubSystem
    {
        UnityEngine.ComputeShader m_verticesUploadShader;
        UnityEngine.ComputeShader m_bytesUploadShader;

        protected override void OnCreate()
        {
            worldBlackboardEntity.AddCollectionComponent(new ComputeBufferManager
            {
                pool = new ComputeBufferTrackingPool(),
            }, true);

            worldBlackboardEntity.AddCollectionComponent(new MeshGpuUploadBuffers());

            m_verticesUploadShader = UnityEngine.Resources.Load<UnityEngine.ComputeShader>("UploadVertices");
            m_bytesUploadShader    = UnityEngine.Resources.Load<UnityEngine.ComputeShader>("UploadBytes");
        }

        protected override void OnUpdate()
        {
            var meshGpuManager = worldBlackboardEntity.GetCollectionComponent<MeshGpuManager>(false, out var jobToComplete);
            var bufferManager  = worldBlackboardEntity.GetCollectionComponent<ComputeBufferManager>();
            var uploadBuffers  = worldBlackboardEntity.GetCollectionComponent<MeshGpuUploadBuffers>(false, out var oldUploadBuffersJob);
            oldUploadBuffersJob.Complete();

            uploadBuffers.Dispatch();

            bufferManager.pool.Update();
            worldBlackboardEntity.UpdateJobDependency<ComputeBufferManager>(default, false);
            jobToComplete.Complete();

            var uploadCount              = meshGpuManager.uploadCommands.Length;
            var requiredSizes            = meshGpuManager.requiredVertexWeightsbufferSizesAndUploadSizes.Value;
            var verticesBuffer           = bufferManager.pool.GetMeshVerticesBuffer(requiredSizes.x);
            var weightsBuffer            = bufferManager.pool.GetMeshWeightsBuffer(requiredSizes.y);
            var verticesUploadBuffer     = bufferManager.pool.GetMeshVerticesUploadBuffer(requiredSizes.z);
            var weightsUploadBuffer      = bufferManager.pool.GetMeshWeightsUploadBuffer(requiredSizes.w);
            var uploadVerticesMetaBuffer = bufferManager.pool.GetUploadMetaBuffer(uploadCount);
            var uploadWeightsMetaBuffer  = bufferManager.pool.GetUploadMetaBuffer(uploadCount);
            var mappedVertices           = verticesUploadBuffer.BeginWrite<VertexToSkin>(0, requiredSizes.z);
            var mappedWeights            = weightsUploadBuffer.BeginWrite<BoneWeightLinkedList>(0, requiredSizes.w);  // Unity uses T for sizing so we don't need to *2 here.
            var mappedVerticesMeta       = uploadVerticesMetaBuffer.BeginWrite<uint3>(0, uploadCount);
            var mappedWeightsMeta        = uploadWeightsMetaBuffer.BeginWrite<uint3>(0, uploadCount);

            var verticesSums = new NativeArray<int>(meshGpuManager.uploadCommands.Length, Allocator.TempJob);
            var weightsSums  = new NativeArray<int>(meshGpuManager.uploadCommands.Length, Allocator.TempJob);
            var jhv          = new PrefixSumVerticesCountsJob
            {
                commands = meshGpuManager.uploadCommands,
                sums     = verticesSums
            }.Schedule();
            var jhw = new PrefixSumWeightsCountsJob
            {
                commands = meshGpuManager.uploadCommands,
                sums     = weightsSums
            }.Schedule();
            jhv = new UploadMeshesVerticesJob
            {
                commands       = meshGpuManager.uploadCommands,
                prefixSums     = verticesSums,
                mappedVertices = mappedVertices,
                mappedMeta     = mappedVerticesMeta
            }.ScheduleParallel(uploadCount, 1, jhv);
            jhw = new UploadMeshesWeightsJob
            {
                commands      = meshGpuManager.uploadCommands,
                prefixSums    = weightsSums,
                mappedWeights = mappedWeights,
                mappedMeta    = mappedWeightsMeta
            }.ScheduleParallel(uploadCount, 1, jhw);

            Dependency = JobHandle.CombineDependencies(jhv, jhw);
            worldBlackboardEntity.SetCollectionComponentAndDisposeOld(new MeshGpuUploadBuffers
            {
                verticesBuffer                     = verticesBuffer,
                weightsBuffer                      = weightsBuffer,
                verticesUploadBuffer               = verticesUploadBuffer,
                verticesUploadMetaBuffer           = uploadVerticesMetaBuffer,
                weightsUploadBuffer                = weightsUploadBuffer,
                weightsUploadMetaBuffer            = uploadWeightsMetaBuffer,
                verticesUploadBufferWriteCount     = requiredSizes.z,
                weightsUploadBufferWriteCount      = requiredSizes.w,
                verticesUploadMetaBufferWriteCount = uploadCount,
                weightsUploadMetaBufferWriteCount  = uploadCount,
                needsCommitment                    = true,
                uploadVerticesShader               = m_verticesUploadShader,
                uploadBytesShader                  = m_bytesUploadShader
            });
            worldBlackboardEntity.UpdateJobDependency<MeshGpuUploadBuffers>(Dependency, false);

            Dependency = new ClearCommandsJob { commands = meshGpuManager.uploadCommands }.Schedule(Dependency);
            Dependency                                   = verticesSums.Dispose(Dependency);
            Dependency                                   = weightsSums.Dispose(Dependency);
        }

        [BurstCompile]
        struct PrefixSumVerticesCountsJob : IJob
        {
            [ReadOnly] public NativeArray<MeshGpuUploadCommand> commands;
            public NativeArray<int>                             sums;

            public void Execute()
            {
                int s = 0;
                for (int i = 0; i < commands.Length; i++)
                {
                    sums[i]  = 0;
                    s       += commands[i].blob.Value.verticesToSkin.Length;
                }
            }
        }

        [BurstCompile]
        struct PrefixSumWeightsCountsJob : IJob
        {
            [ReadOnly] public NativeArray<MeshGpuUploadCommand> commands;
            public NativeArray<int>                             sums;

            public void Execute()
            {
                int s = 0;
                for (int i = 0; i < commands.Length; i++)
                {
                    sums[i]  = 0;
                    s       += commands[i].blob.Value.boneWeights.Length;
                }
            }
        }

        [BurstCompile]
        struct UploadMeshesVerticesJob : IJobFor
        {
            [ReadOnly] public NativeArray<MeshGpuUploadCommand> commands;
            [ReadOnly] public NativeArray<int>                  prefixSums;
            public NativeArray<VertexToSkin>                    mappedVertices;
            public NativeArray<uint3>                           mappedMeta;

            public unsafe void Execute(int index)
            {
                int size          = commands[index].blob.Value.verticesToSkin.Length;
                mappedMeta[index] = (uint3) new int3(prefixSums[index], commands[index].verticesIndex, size);
                var blobData      = commands[index].blob.Value.verticesToSkin.GetUnsafePtr();
                var subArray      = mappedVertices.GetSubArray(prefixSums[index], size);
                UnsafeUtility.MemCpy(subArray.GetUnsafePtr(), blobData, size * sizeof(VertexToSkin));
            }
        }

        [BurstCompile]
        struct UploadMeshesWeightsJob : IJobFor
        {
            [ReadOnly] public NativeArray<MeshGpuUploadCommand> commands;
            [ReadOnly] public NativeArray<int>                  prefixSums;
            public NativeArray<BoneWeightLinkedList>            mappedWeights;
            public NativeArray<uint3>                           mappedMeta;

            public unsafe void Execute(int index)
            {
                int size          = commands[index].blob.Value.boneWeights.Length;
                mappedMeta[index] = (uint3) new int3(prefixSums[index], commands[index].weightsIndex, size);
                var blobData      = commands[index].blob.Value.boneWeights.GetUnsafePtr();
                var subArray      = mappedWeights.GetSubArray(prefixSums[index], size);
                UnsafeUtility.MemCpy(subArray.GetUnsafePtr(), blobData, size * sizeof(BoneWeightLinkedList));
            }
        }

        [BurstCompile]
        struct ClearCommandsJob : IJob
        {
            public NativeList<MeshGpuUploadCommand> commands;

            public void Execute()
            {
                commands.Clear();
            }
        }
    }
}

