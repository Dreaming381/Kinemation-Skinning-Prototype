using System;
using System.Collections.Generic;
using System.Diagnostics;
using Latios.Unsafe;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;

namespace Latios.Kinemation.Systems
{
    [DisableAutoCreation]
    public partial class SkeletonMeshBindingReactiveSystem : SubSystem
    {
        EntityQuery m_newMeshesQuery;
        EntityQuery m_allMeshesQuery;
        EntityQuery m_deadMeshesQuery;
        EntityQuery m_newSkeletonsQuery;
        EntityQuery m_deadSkeletonsQuery;
        EntityQuery m_newExposedSkeletonsQuery;
        EntityQuery m_deadExposedSkeletonsQuery;

        protected override void OnCreate()
        {
            m_newMeshesQuery            = Fluent.WithAll<BindSkeletonRoot>().WithAll<MeshSkinningBlobReference>().Without<SkeletonDependent>().Build();
            m_allMeshesQuery            = Fluent.WithAll<BindSkeletonRoot>().WithAll<MeshSkinningBlobReference>().WithAll<SkeletonDependent>().Build();
            m_deadMeshesQuery           = Fluent.WithAll<SkeletonDependent>().Without<BindSkeletonRoot>().Build();
            m_newSkeletonsQuery         = Fluent.WithAll<SkeletonRootTag>(true).Without<DependentSkinnedMesh>().Build();
            m_deadSkeletonsQuery        = Fluent.WithAll<DependentSkinnedMesh>().Without<SkeletonRootTag>().Build();
            m_newExposedSkeletonsQuery  = Fluent.WithAll<SkeletonRootTag>(true).Without<DependentSkinnedMesh>().WithAll<ExposedSkeletonCullingIndex>().Build();
            m_deadExposedSkeletonsQuery = Fluent.WithAll<DependentSkinnedMesh>().Without<SkeletonRootTag>().WithAll<ExposedSkeletonCullingIndex>().Build();

            worldBlackboardEntity.AddCollectionComponent(new MeshGpuManager
            {
                blobIndexMap                                   = new NativeHashMap<MeshSkinningBlobReference, int>(128, Allocator.Persistent),
                referenceCounts                                = new NativeList<int>(Allocator.Persistent),
                verticesStarts                                 = new NativeList<int>(Allocator.Persistent),
                weightsStarts                                  = new NativeList<int>(Allocator.Persistent),
                indexFreeList                                  = new NativeList<int>(Allocator.Persistent),
                verticesGaps                                   = new NativeList<int2>(Allocator.Persistent),
                weightsGaps                                    = new NativeList<int2>(Allocator.Persistent),
                requiredVertexWeightsbufferSizesAndUploadSizes = new NativeReference<int4>(Allocator.Persistent),
                uploadCommands                                 = new NativeList<MeshGpuUploadCommand>(Allocator.Persistent)
            });

            worldBlackboardEntity.AddCollectionComponent(new ExposedCullingIndexManager
            {
                skeletonIndexMap = new NativeHashMap<Entity, int>(128, Allocator.Persistent),
                indexFreeList    = new NativeList<int>(Allocator.Persistent),
                maxIndex         = new NativeReference<int>(Allocator.Persistent)
            });
        }

        protected override void OnUpdate()
        {
            // Todo: It may be possible to defer all structural changes to a sync point.
            // Whether or not that is worth pursuing remains to be seen.
            bool haveNewMeshes            = !m_newMeshesQuery.IsEmptyIgnoreFilter;
            bool haveDeadMeshes           = !m_deadMeshesQuery.IsEmptyIgnoreFilter;
            bool haveNewSkeletons         = !m_newSkeletonsQuery.IsEmptyIgnoreFilter;
            bool haveDeadSkeletons        = !m_deadMeshesQuery.IsEmptyIgnoreFilter;
            bool requiresStructuralChange = haveNewMeshes | haveDeadMeshes | haveNewSkeletons | haveDeadSkeletons;

            var entityHandle    = GetEntityTypeHandle();
            var rootRefHandle   = GetComponentTypeHandle<BindSkeletonRoot>(true);
            var blobRefHandle   = GetComponentTypeHandle<MeshSkinningBlobReference>(true);
            var dependentHandle = GetComponentTypeHandle<SkeletonDependent>(false);

            var meshBlobsCdfe      = GetComponentDataFromEntity<MeshSkinningBlobReference>(true);
            var boneToRootsBfe     = GetBufferFromEntity<OptimizedBoneToRoot>(true);
            var boneRefsBfe        = GetBufferFromEntity<BoneReference>(true);
            var dependentsBfe      = GetBufferFromEntity<DependentSkinnedMesh>(false);
            var optimizedBoundsBfe = GetBufferFromEntity<OptimizedBoneBounds>(false);
            var boneBoundsCdfe     = GetComponentDataFromEntity<BoneBounds>(false);

            var bindingsBlockList = new UnsafeParallelBlockList(UnsafeUtility.SizeOf<BindUnbindOperation>(), 128, Allocator.TempJob);
            var meshesBlockList   = new UnsafeParallelBlockList(UnsafeUtility.SizeOf<MeshAddRemoveOperation>(), 128, Allocator.TempJob);
            var operations        = new NativeList<BindUnbindOperation>(Allocator.TempJob);
            var startsAndCounts   = new NativeList<int2>(Allocator.TempJob);

            var lastSystemVersion   = LastSystemVersion;
            var globalSystemVersion = GlobalSystemVersion;

            if (haveNewMeshes)
            {
                Dependency = new FindNewMeshesJob
                {
                    entityHandle      = entityHandle,
                    rootRefHandle     = rootRefHandle,
                    blobRefHandle     = blobRefHandle,
                    bindingsBlockList = bindingsBlockList,
                    meshBlockList     = meshesBlockList
                }.ScheduleParallel(m_newMeshesQuery, Dependency);
            }
            if (haveDeadMeshes)
            {
                Dependency = new FindDeadMeshesJob
                {
                    entityHandle      = entityHandle,
                    depsHandle        = dependentHandle,  // Ok that we declare write here since we will delete these anyways
                    bindingsBlockList = bindingsBlockList,
                    meshBlockList     = meshesBlockList
                }.ScheduleParallel(m_deadMeshesQuery, Dependency);
            }
            Dependency = new FindChangedBindingsJob
            {
                entityHandle      = entityHandle,
                rootRefHandle     = rootRefHandle,
                blobRefHandle     = blobRefHandle,
                dependentHandle   = dependentHandle,
                bindingsBlockList = bindingsBlockList,
                meshBlockList     = meshesBlockList,
                lastSystemVersion = lastSystemVersion
            }.ScheduleParallel(m_allMeshesQuery, Dependency);

            JobHandle                                         cullingJH       = default;
            bool                                              needsCullingJob = false;
            NativeArray<ExposedSkeletonCullingIndexOperation> cullingOps      = default;
            if (haveNewSkeletons || haveDeadSkeletons)
            {
                int newExposedSkeletonsCount  = m_newExposedSkeletonsQuery.CalculateEntityCountWithoutFiltering();
                int deadExposedSkeletonsCount = m_deadExposedSkeletonsQuery.CalculateEntityCountWithoutFiltering();

                if (newExposedSkeletonsCount + deadExposedSkeletonsCount > 0)
                {
                    var newSkeletonsArray  = new NativeArray<Entity>(newExposedSkeletonsCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    var deadSkeletonsArray = new NativeArray<Entity>(deadExposedSkeletonsCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                    Dependency = new FindNewOrDeadDeadExposedSkeletonsJob
                    {
                        entityHandle   = entityHandle,
                        newOrDeadArray = newSkeletonsArray
                    }.ScheduleParallel(m_newExposedSkeletonsQuery, Dependency);
                    Dependency = new FindNewOrDeadDeadExposedSkeletonsJob
                    {
                        entityHandle   = entityHandle,
                        newOrDeadArray = deadSkeletonsArray
                    }.ScheduleParallel(m_deadExposedSkeletonsQuery, Dependency);

                    cullingOps = new NativeArray<ExposedSkeletonCullingIndexOperation>(newExposedSkeletonsCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                    var cullingManager = worldBlackboardEntity.GetCollectionComponent<ExposedCullingIndexManager>(false);

                    cullingJH = new ProcessNewAndDeadExposedSkeletonsJob
                    {
                        newExposedSkeletons  = newSkeletonsArray,
                        deadExposedSkeletons = deadSkeletonsArray,
                        cullingManager       = cullingManager,
                        operations           = cullingOps
                    }.Schedule(Dependency);
                    cullingJH = newSkeletonsArray.Dispose(cullingJH);
                    cullingJH = deadSkeletonsArray.Dispose(cullingJH);

                    needsCullingJob = newExposedSkeletonsCount > 0;
                }
            }

            // Todo: Handle meshGpuManager stuff
            var meshGpuManager = worldBlackboardEntity.GetCollectionComponent<MeshGpuManager>(false);

            var meshJH = new ProcessMeshGpuChangesJob
            {
                meshBlockList = meshesBlockList,
                manager       = meshGpuManager
            }.Schedule(Dependency);

            // We need synchronous access to this later, and we don't want to sync on the rest of the jobs in this system.
            //worldBlackboardEntity.UpdateJobDependency<MeshGpuManager>(Dependency, false);

            var sortJH = new SortBlockListJob
            {
                bindingsBlockList = bindingsBlockList,  // This is disposed here
                operations        = operations,
                startsAndCounts   = startsAndCounts
            }.Schedule(Dependency);

            if (requiresStructuralChange)
            {
                // Kick the jobs so that the sorting happens while we do structural changes.
                // Todo: Does Complete already do this?
                JobHandle.ScheduleBatchedJobs();

                CompleteDependency();

                EntityManager.RemoveComponent<SkeletonDependent>(   m_deadMeshesQuery);
                EntityManager.RemoveComponent<DependentSkinnedMesh>(m_deadSkeletonsQuery);
                EntityManager.AddComponent<SkeletonDependent>(   m_newMeshesQuery);
                EntityManager.AddComponent<DependentSkinnedMesh>(m_newSkeletonsQuery);

                rootRefHandle   = GetComponentTypeHandle<BindSkeletonRoot>(true);
                blobRefHandle   = GetComponentTypeHandle<MeshSkinningBlobReference>(true);
                dependentHandle = GetComponentTypeHandle<SkeletonDependent>(false);

                meshBlobsCdfe      = GetComponentDataFromEntity<MeshSkinningBlobReference>(true);
                boneToRootsBfe     = GetBufferFromEntity<OptimizedBoneToRoot>(true);
                boneRefsBfe        = GetBufferFromEntity<BoneReference>(true);
                dependentsBfe      = GetBufferFromEntity<DependentSkinnedMesh>(false);
                optimizedBoundsBfe = GetBufferFromEntity<OptimizedBoneBounds>(false);
                boneBoundsCdfe     = GetComponentDataFromEntity<BoneBounds>(false);
            }

            Dependency = new ProcessBindingOpsJob
            {
                operations         = operations.AsDeferredJobArray(),
                startsAndCounts    = startsAndCounts.AsDeferredJobArray(),
                meshBlobsCdfe      = meshBlobsCdfe,
                boneToRootsBfe     = boneToRootsBfe,
                boneRefsBfe        = boneRefsBfe,
                gpuManager         = meshGpuManager,
                dependentsBfe      = dependentsBfe,
                optimizedBoundsBfe = optimizedBoundsBfe,
                boneBoundsCdfe     = boneBoundsCdfe
            }.Schedule(startsAndCounts, 1, JobHandle.CombineDependencies(sortJH, meshJH, cullingJH));

            if (haveNewMeshes)
            {
                Dependency = new CleanupNewMeshesJob
                {
                    rootRefHandle       = rootRefHandle,
                    blobRefHandle       = blobRefHandle,
                    dependentHandle     = dependentHandle,
                    globalSystemVersion = globalSystemVersion
                }.ScheduleParallel(m_allMeshesQuery, Dependency);
            }

            if (needsCullingJob)
            {
                Dependency = new SetExposedSkeletonCullingIndicesJob
                {
                    boneRefsBfe              = boneRefsBfe,
                    boneCullingIndexCdfe     = GetComponentDataFromEntity<BoneCullingIndex>(false),
                    skeletonCullingIndexCdfe = GetComponentDataFromEntity<ExposedSkeletonCullingIndex>(false),
                    operations               = cullingOps
                }.ScheduleParallel(cullingOps.Length, 1, Dependency);
            }
            if (cullingOps.IsCreated)
                Dependency = cullingOps.Dispose(Dependency);

            Dependency = operations.Dispose(Dependency);
            Dependency = startsAndCounts.Dispose(Dependency);
        }

        struct BindUnbindOperation : IComparable<BindUnbindOperation>
        {
            public enum OpType : byte
            {
                Unbind = 0,
                Bind = 1,
                MeshChanged = 2
            }

            public Entity targetEntity;
            public Entity meshEntity;
            public OpType opType;

            public int CompareTo(BindUnbindOperation other)
            {
                var compare = targetEntity.CompareTo(other.targetEntity);
                if (compare == 0)
                {
                    compare = ((byte)opType).CompareTo((byte)other.opType);
                    if (compare == 0)
                        compare = meshEntity.CompareTo(other.meshEntity);
                }
                return compare;
            }
        }

        struct MeshAddRemoveOperation : IComparable<MeshAddRemoveOperation>
        {
            public BlobAssetReference<MeshSkinningBlob> blob;
            public bool                                 isAddOp;

            public int CompareTo(MeshAddRemoveOperation other)
            {
                var compare = isAddOp.CompareTo(other.isAddOp);
                if (compare == 0)
                    return blob.Value.authoredHash.CompareTo(other.blob.Value.authoredHash);
                return compare;
            }
        }

        struct ExposedSkeletonCullingIndexOperation
        {
            public Entity skeletonEntity;
            public int    index;
        }

        [BurstCompile]
        struct FindNewMeshesJob : IJobEntityBatch
        {
            [ReadOnly] public EntityTypeHandle                               entityHandle;
            [ReadOnly] public ComponentTypeHandle<BindSkeletonRoot>          rootRefHandle;
            [ReadOnly] public ComponentTypeHandle<MeshSkinningBlobReference> blobRefHandle;

            [NativeDisableUnsafePtrRestriction] public UnsafeParallelBlockList bindingsBlockList;
            [NativeDisableUnsafePtrRestriction] public UnsafeParallelBlockList meshBlockList;
            [NativeSetThreadIndex] int                                         m_NativethreadIndex;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                var entities = batchInChunk.GetNativeArray(entityHandle);
                var rootRefs = batchInChunk.GetNativeArray(rootRefHandle);
                var blobs    = batchInChunk.GetNativeArray(blobRefHandle);

                for (int i = 0; i < batchInChunk.Count; i++)
                {
                    var target = rootRefs[i].root;
                    if (target != Entity.Null)
                    {
                        bindingsBlockList.Write(new BindUnbindOperation { targetEntity = target, meshEntity = entities[i], opType = BindUnbindOperation.OpType.Bind },
                                                m_NativethreadIndex);
                        meshBlockList.Write(new MeshAddRemoveOperation { blob = blobs[i].blob, isAddOp = true }, m_NativethreadIndex);
                    }
                }
            }
        }

        [BurstCompile]
        struct FindDeadMeshesJob : IJobEntityBatch
        {
            [ReadOnly] public EntityTypeHandle                       entityHandle;
            [ReadOnly] public ComponentTypeHandle<SkeletonDependent> depsHandle;

            [NativeDisableUnsafePtrRestriction] public UnsafeParallelBlockList bindingsBlockList;
            [NativeDisableUnsafePtrRestriction] public UnsafeParallelBlockList meshBlockList;
            [NativeSetThreadIndex] int                                         m_NativethreadIndex;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                var entities = batchInChunk.GetNativeArray(entityHandle);
                var deps     = batchInChunk.GetNativeArray(depsHandle);

                for (int i = 0; i < batchInChunk.Count; i++)
                {
                    var target = deps[i].root;
                    if (target != Entity.Null)
                    {
                        bindingsBlockList.Write(new BindUnbindOperation { targetEntity = target, meshEntity = entities[i], opType = BindUnbindOperation.OpType.Unbind },
                                                m_NativethreadIndex);
                        meshBlockList.Write(new MeshAddRemoveOperation { blob = deps[i].skinningBlob, isAddOp = false }, m_NativethreadIndex);
                    }
                }
            }
        }

        [BurstCompile]
        struct FindChangedBindingsJob : IJobEntityBatch
        {
            [ReadOnly] public EntityTypeHandle                               entityHandle;
            [ReadOnly] public ComponentTypeHandle<BindSkeletonRoot>          rootRefHandle;
            [ReadOnly] public ComponentTypeHandle<MeshSkinningBlobReference> blobRefHandle;

            public ComponentTypeHandle<SkeletonDependent>                      dependentHandle;
            [NativeDisableUnsafePtrRestriction] public UnsafeParallelBlockList bindingsBlockList;
            [NativeDisableUnsafePtrRestriction] public UnsafeParallelBlockList meshBlockList;
            [NativeSetThreadIndex] int                                         m_NativethreadIndex;

            public uint lastSystemVersion;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                bool rootsChanged = batchInChunk.DidChange(rootRefHandle, lastSystemVersion);
                bool blobsChanged = batchInChunk.DidChange(blobRefHandle, lastSystemVersion);

                if (rootsChanged || blobsChanged)
                {
                    var entities = batchInChunk.GetNativeArray(entityHandle);
                    var roots    = batchInChunk.GetNativeArray(rootRefHandle);
                    var blobs    = batchInChunk.GetNativeArray(blobRefHandle);
                    var deps     = batchInChunk.GetNativeArray(dependentHandle);

                    for (int i = 0; i < batchInChunk.Count; i++)
                    {
                        bool rootNull     = roots[i].root == Entity.Null;
                        bool depsRootNull = deps[i].root == Entity.Null;
                        bool rootDiff     = roots[i].root != deps[i].root.entity;
                        bool blobNull     = blobs[i].blob != BlobAssetReference<MeshSkinningBlob>.Null;
                        bool depsBlobNull = deps[i].skinningBlob != BlobAssetReference<MeshSkinningBlob>.Null;
                        bool blobDiff     = blobs[i].blob != deps[i].skinningBlob;

                        if ((rootDiff && depsRootNull && !blobNull) || (blobDiff && depsBlobNull && !blobNull))
                        {
                            // Add binding
                            bindingsBlockList.Write(new BindUnbindOperation
                            {
                                targetEntity = roots[i].root,
                                meshEntity   = entities[i],
                                opType       = BindUnbindOperation.OpType.Bind
                            }, m_NativethreadIndex);
                            meshBlockList.Write(new MeshAddRemoveOperation { blob = blobs[i].blob, isAddOp = true }, m_NativethreadIndex);
                        }
                        else if ((rootDiff && rootNull && !depsBlobNull) || (blobDiff && blobNull && !depsBlobNull))
                        {
                            // Dead root, remove binding
                            bindingsBlockList.Write(new BindUnbindOperation
                            {
                                targetEntity = deps[i].root,
                                meshEntity   = entities[i],
                                opType       = BindUnbindOperation.OpType.Unbind
                            }, m_NativethreadIndex);
                            meshBlockList.Write(new MeshAddRemoveOperation { blob = deps[i].skinningBlob, isAddOp = false }, m_NativethreadIndex);
                        }
                        else if (rootDiff && !rootNull && !depsRootNull && !blobNull && !depsBlobNull)
                        {
                            // Switched root and maybe blob, rebind both
                            bindingsBlockList.Write(new BindUnbindOperation
                            {
                                targetEntity = deps[i].root,
                                meshEntity   = entities[i],
                                opType       = BindUnbindOperation.OpType.Unbind
                            }, m_NativethreadIndex);
                            meshBlockList.Write(new MeshAddRemoveOperation { blob = deps[i].skinningBlob, isAddOp = false }, m_NativethreadIndex);

                            bindingsBlockList.Write(new BindUnbindOperation
                            {
                                targetEntity = roots[i].root,
                                meshEntity   = entities[i],
                                opType       = BindUnbindOperation.OpType.Bind
                            }, m_NativethreadIndex);
                            meshBlockList.Write(new MeshAddRemoveOperation { blob = blobs[i].blob, isAddOp = true }, m_NativethreadIndex);
                        }
                        else if (rootDiff && !rootNull && !depsRootNull && !blobDiff && !blobNull && !depsBlobNull)
                        {
                            // Switched root only, don't touch blob
                            bindingsBlockList.Write(new BindUnbindOperation
                            {
                                targetEntity = deps[i].root,
                                meshEntity   = entities[i],
                                opType       = BindUnbindOperation.OpType.Unbind
                            }, m_NativethreadIndex);

                            bindingsBlockList.Write(new BindUnbindOperation
                            {
                                targetEntity = roots[i].root,
                                meshEntity   = entities[i],
                                opType       = BindUnbindOperation.OpType.Bind
                            }, m_NativethreadIndex);
                        }
                        else if (!rootDiff && !rootNull && !depsRootNull && blobDiff && !blobNull && !depsBlobNull)
                        {
                            // Switched blob only, update both
                            bindingsBlockList.Write(new BindUnbindOperation
                            {
                                targetEntity = deps[i].root,
                                meshEntity   = entities[i],
                                opType       = BindUnbindOperation.OpType.MeshChanged
                            }, m_NativethreadIndex);
                            meshBlockList.Write(new MeshAddRemoveOperation { blob = deps[i].skinningBlob, isAddOp = false }, m_NativethreadIndex);
                            meshBlockList.Write(new MeshAddRemoveOperation { blob                                 = blobs[i].blob, isAddOp = true },         m_NativethreadIndex);
                        }

                        if (rootDiff || blobDiff)
                            deps[i] = new SkeletonDependent { root = roots[i].root, skinningBlob = blobs[i].blob };
                    }
                }
            }
        }

        [BurstCompile]
        struct FindNewOrDeadDeadExposedSkeletonsJob : IJobEntityBatchWithIndex
        {
            [ReadOnly] public EntityTypeHandle entityHandle;
            public NativeArray<Entity>         newOrDeadArray;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex, int indexOfFirstEntityInQuery)
            {
                var entities = batchInChunk.GetNativeArray(entityHandle);
                NativeArray<Entity>.Copy(entities, 0, newOrDeadArray, indexOfFirstEntityInQuery, batchInChunk.Count);
            }
        }

        [BurstCompile]
        struct SortBlockListJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public UnsafeParallelBlockList bindingsBlockList;
            public NativeList<BindUnbindOperation>                             operations;
            public NativeList<int2>                                            startsAndCounts;

            public void Execute()
            {
                var count = bindingsBlockList.Count();
                operations.ResizeUninitialized(count);
                bindingsBlockList.GetElementValues(operations.AsArray());
                operations.Sort();
                Entity  lastEntity        = Entity.Null;
                int2    nullCounts        = default;
                ref var currentStartCount = ref nullCounts;
                for (int i = 0; i < count; i++)
                {
                    if (operations[i].targetEntity != lastEntity)
                    {
                        startsAndCounts.Add(new int2(i, 1));
                        currentStartCount = ref startsAndCounts.ElementAt(startsAndCounts.Length - 1);
                    }
                    else
                        currentStartCount.y++;
                }
                bindingsBlockList.Dispose();
            }
        }

        [BurstCompile]
        struct ProcessMeshGpuChangesJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public UnsafeParallelBlockList meshBlockList;
            public MeshGpuManager                                              manager;

            public void Execute()
            {
                int count = meshBlockList.Count();
                var ops   = new NativeArray<MeshAddRemoveOperation>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                meshBlockList.GetElementValues(ops);
                ops.Sort();

                bool madeGaps = false;

                int i = 0;
                for (; i < count; i++)
                {
                    var op = ops[i];
                    if (op.isAddOp)
                        break;

                    var blob = new MeshSkinningBlobReference { blob = op.blob };
                    if (manager.blobIndexMap.TryGetValue(blob, out int meshIndex))
                    {
                        ref var refCount = ref manager.referenceCounts.ElementAt(meshIndex);
                        refCount--;
                        if (refCount == 0)
                        {
                            int vertices = op.blob.Value.verticesToSkin.Length;
                            int weights  = op.blob.Value.boneWeights.Length;
                            manager.verticesGaps.Add(new int2(manager.verticesStarts[meshIndex], vertices));
                            manager.weightsGaps.Add(new int2(manager.weightsStarts[meshIndex], weights));
                            manager.indexFreeList.Add(meshIndex);

                            if (manager.verticesStarts[meshIndex] + vertices == manager.requiredVertexWeightsbufferSizesAndUploadSizes.Value.x)
                                manager.requiredVertexWeightsbufferSizesAndUploadSizes.Value -= new int4(vertices, 0, 0, 0);
                            if (manager.weightsStarts[meshIndex] + weights == manager.requiredVertexWeightsbufferSizesAndUploadSizes.Value.y)
                                manager.requiredVertexWeightsbufferSizesAndUploadSizes.Value -= new int4(0, weights, 0, 0);

                            madeGaps = true;

                            manager.blobIndexMap.Remove(blob);

                            if (!manager.uploadCommands.IsEmpty)
                            {
                                // This only happens if this system was invoked multiple times between renders and a mesh was added and removed in that time.
                                for (int j = 0; j < manager.uploadCommands.Length; j++)
                                {
                                    if (manager.uploadCommands[j].blob == op.blob)
                                    {
                                        manager.requiredVertexWeightsbufferSizesAndUploadSizes.Value -= new int4(0,
                                                                                                                 0,
                                                                                                                 op.blob.Value.verticesToSkin.Length,
                                                                                                                 op.blob.Value.boneWeights.Length);
                                        manager.uploadCommands.RemoveAtSwapBack(j);
                                        j--;
                                    }
                                }
                            }
                        }
                    }
                }

                // coellesce gaps
                if (madeGaps)
                {
                    manager.verticesGaps.Sort(new GapSorter());
                    int dst   = 1;
                    var array = manager.verticesGaps.AsArray();
                    for (int j = 1; j < array.Length; j++)
                    {
                        array[dst] = array[j];
                        var prev   = array[dst - 1];
                        if (prev.x + prev.y == array[j].x)
                        {
                            prev.y         += array[j].x;
                            array[dst - 1]  = prev;
                        }
                        else
                            dst++;
                    }
                    manager.verticesGaps.Length = dst;

                    manager.weightsGaps.Sort(new GapSorter());
                    dst   = 1;
                    array = manager.weightsGaps.AsArray();
                    for (int j = 1; j < array.Length; j++)
                    {
                        array[dst] = array[j];
                        var prev   = array[dst - 1];
                        if (prev.x + prev.y == array[j].x)
                        {
                            prev.y         += array[j].x;
                            array[dst - 1]  = prev;
                        }
                        else
                            dst++;
                    }
                    manager.weightsGaps.Length = dst;
                }

                for (; i < count; i++)
                {
                    var op = ops[i];

                    var blob = new MeshSkinningBlobReference { blob = op.blob };
                    if (manager.blobIndexMap.TryGetValue(blob, out int meshIndex))
                    {
                        manager.referenceCounts.ElementAt(meshIndex)++;
                    }
                    else
                    {
                        int newMeshIndex;
                        if (!manager.indexFreeList.IsEmpty)
                        {
                            newMeshIndex = manager.indexFreeList[0];
                            manager.indexFreeList.RemoveAtSwapBack(0);
                        }
                        else
                        {
                            newMeshIndex = manager.referenceCounts.Length;
                            manager.referenceCounts.Add(0);
                            manager.verticesStarts.Add(0);
                            manager.weightsStarts.Add(0);
                        }

                        int verticesNeeded = op.blob.Value.verticesToSkin.Length;
                        int weightsNeeded  = op.blob.Value.boneWeights.Length;

                        int bestVerticesIndex = -1;
                        int bestVerticesCount = int.MaxValue;

                        for (int j = 0; j < manager.verticesGaps.Length; j++)
                        {
                            int vertices = manager.verticesGaps[j].y;
                            if (vertices >= verticesNeeded && vertices < bestVerticesCount)
                            {
                                bestVerticesIndex = j;
                                bestVerticesCount = vertices;
                            }
                        }

                        int verticesGpuStart;
                        if (bestVerticesIndex == -1)
                        {
                            verticesGpuStart                                              = manager.requiredVertexWeightsbufferSizesAndUploadSizes.Value.x;
                            manager.requiredVertexWeightsbufferSizesAndUploadSizes.Value += new int4(verticesNeeded, 0, 0, 0);
                        }
                        else if (bestVerticesCount == verticesNeeded)
                        {
                            verticesGpuStart = manager.verticesGaps[bestVerticesIndex].x;
                            manager.verticesGaps.RemoveAtSwapBack(bestVerticesIndex);
                        }
                        else
                        {
                            verticesGpuStart                         = manager.verticesGaps[bestVerticesIndex].x;
                            manager.verticesGaps[bestVerticesIndex] += new int2(verticesNeeded, -verticesNeeded);
                        }

                        int bestWeightsIndex = -1;
                        int bestWeightsCount = int.MaxValue;

                        for (int j = 0; j < manager.weightsGaps.Length; j++)
                        {
                            int weights = manager.weightsGaps[j].y;
                            if (weights >= weightsNeeded && weights < bestWeightsCount)
                            {
                                bestWeightsIndex = j;
                                bestWeightsCount = weights;
                            }
                        }

                        int weightsGpuStart;
                        if (bestWeightsIndex == -1)
                        {
                            weightsGpuStart                                               = manager.requiredVertexWeightsbufferSizesAndUploadSizes.Value.y;
                            manager.requiredVertexWeightsbufferSizesAndUploadSizes.Value += new int4(0, weightsNeeded, 0, 0);
                        }
                        else if (bestWeightsCount == weightsNeeded)
                        {
                            weightsGpuStart = manager.weightsGaps[bestWeightsIndex].x;
                            manager.weightsGaps.RemoveAtSwapBack(bestWeightsIndex);
                        }
                        else
                        {
                            weightsGpuStart                        = manager.weightsGaps[bestWeightsIndex].x;
                            manager.weightsGaps[bestWeightsIndex] += new int2(weightsNeeded, -weightsNeeded);
                        }

                        manager.blobIndexMap.Add(blob, newMeshIndex);
                        manager.referenceCounts[newMeshIndex] = 1;
                        manager.verticesStarts[newMeshIndex]  = verticesGpuStart;
                        manager.weightsStarts[newMeshIndex]   = weightsGpuStart;

                        manager.requiredVertexWeightsbufferSizesAndUploadSizes.Value += new int4(0, 0, op.blob.Value.verticesToSkin.Length, op.blob.Value.boneWeights.Length);
                        manager.uploadCommands.Add(new MeshGpuUploadCommand
                        {
                            blob          = op.blob,
                            verticesIndex = verticesGpuStart,
                            weightsIndex  = weightsGpuStart
                        });
                    }
                }

                meshBlockList.Dispose();
            }

            struct GapSorter : IComparer<int2>
            {
                public int Compare(int2 a, int2 b)
                {
                    return a.x.CompareTo(b.x);
                }
            }
        }

        [BurstCompile]
        struct ProcessNewAndDeadExposedSkeletonsJob : IJob
        {
            [ReadOnly] public NativeArray<Entity>                    newExposedSkeletons;
            [ReadOnly] public NativeArray<Entity>                    deadExposedSkeletons;
            public ExposedCullingIndexManager                        cullingManager;
            public NativeArray<ExposedSkeletonCullingIndexOperation> operations;

            public void Execute()
            {
                for (int i = 0; i < deadExposedSkeletons.Length; i++)
                {
                    var entity = deadExposedSkeletons[i];
                    int index  = cullingManager.skeletonIndexMap[entity];
                    cullingManager.indexFreeList.Add(index);
                    cullingManager.skeletonIndexMap.Remove(entity);
                }

                for (int i = 0; i < newExposedSkeletons.Length; i++)
                {
                    int index;
                    if (!cullingManager.indexFreeList.IsEmpty)
                    {
                        index = cullingManager.indexFreeList[0];
                        cullingManager.indexFreeList.RemoveAtSwapBack(0);
                    }
                    else
                    {
                        // Index 0 is reserved for prefabs
                        index = cullingManager.maxIndex.Value + 1;
                        cullingManager.maxIndex.Value++;
                    }
                    operations[i] = new ExposedSkeletonCullingIndexOperation
                    {
                        skeletonEntity = newExposedSkeletons[i],
                        index          = index
                    };
                }
            }
        }

        [BurstCompile]
        struct ProcessBindingOpsJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<BindUnbindOperation>                   operations;
            [ReadOnly] public NativeArray<int2>                                  startsAndCounts;
            [ReadOnly] public ComponentDataFromEntity<MeshSkinningBlobReference> meshBlobsCdfe;
            [ReadOnly] public BufferFromEntity<OptimizedBoneToRoot>              boneToRootsBfe;
            [ReadOnly] public BufferFromEntity<BoneReference>                    boneRefsBfe;
            [ReadOnly] public MeshGpuManager                                     gpuManager;

            [NativeDisableParallelForRestriction] public BufferFromEntity<DependentSkinnedMesh> dependentsBfe;
            [NativeDisableParallelForRestriction] public BufferFromEntity<OptimizedBoneBounds>  optimizedBoundsBfe;
            [NativeDisableParallelForRestriction] public ComponentDataFromEntity<BoneBounds>    boneBoundsCdfe;

            [NativeDisableContainerSafetyRestriction, NoAlias] NativeList<float> boundsCache;

            public void Execute(int index)
            {
                int2 startAndCount = startsAndCounts[index];
                var  opsArray      = operations.GetSubArray(startAndCount.x, startAndCount.y);

                Entity skeletonEntity        = opsArray[0].targetEntity;
                var    depsBuffer            = dependentsBfe[skeletonEntity];
                bool   needsFullBoundsUpdate = false;
                bool   needsAddBoundsUpdate  = false;

                // Todo: This might be really slow
                int i = 0;
                for (; i < opsArray.Length && opsArray[i].opType == BindUnbindOperation.OpType.Unbind; i++)
                {
                    for (int j = 0; j < depsBuffer.Length; j++)
                    {
                        if (depsBuffer[j].skinnedMesh == opsArray[i].meshEntity)
                        {
                            depsBuffer.RemoveAtSwapBack(j);
                            needsFullBoundsUpdate = true;
                            break;
                        }
                    }
                }

                int addStart = i;
                for (; i < opsArray.Length && opsArray[i].opType == BindUnbindOperation.OpType.Bind; i++)
                {
                    var blob     = meshBlobsCdfe[opsArray[i].meshEntity];
                    var mapIndex = gpuManager.blobIndexMap[blob];
                    depsBuffer.Add( new DependentSkinnedMesh
                    {
                        skinnedMesh       = opsArray[i].meshEntity,
                        meshVerticesStart = gpuManager.verticesStarts[mapIndex],
                        meshWeightsStart  = gpuManager.weightsStarts[mapIndex],
                        meshVerticesCount = blob.blob.Value.verticesToSkin.Length
                    });
                    needsAddBoundsUpdate = true;
                }

                int  changeStart             = i;
                bool needsChangeBoundsUpdate = i < opsArray.Length;

                if (needsFullBoundsUpdate)
                {
                    ApplyMeshBounds(skeletonEntity, opsArray, 0, opsArray.Length, true);
                }
                else if (needsAddBoundsUpdate && needsChangeBoundsUpdate)
                {
                    ApplyMeshBounds(skeletonEntity, opsArray, addStart, opsArray.Length - addStart, false);
                }
                else if (needsAddBoundsUpdate)
                {
                    ApplyMeshBounds(skeletonEntity, opsArray, addStart, changeStart - addStart, false);
                }
                else if (needsChangeBoundsUpdate)
                {
                    ApplyMeshBounds(skeletonEntity, opsArray, changeStart, opsArray.Length - changeStart, false);
                }
            }

            void ApplyMeshBounds(Entity skeletonEntity, NativeArray<BindUnbindOperation> ops, int start, int count, bool reset)
            {
                if (boneToRootsBfe.HasComponent(skeletonEntity))
                {
                    // Optimized skeleton path
                    bool needsCollapse = reset;
                    var  boundsBuffer  = optimizedBoundsBfe[skeletonEntity];
                    if (boundsBuffer.IsEmpty)
                    {
                        needsCollapse = true;
                        boundsBuffer.ResizeUninitialized(boneToRootsBfe[skeletonEntity].Length);
                    }
                    var boundsArray = boundsBuffer.Reinterpret<float>().AsNativeArray();
                    if (needsCollapse)
                    {
                        var arr = boundsBuffer.Reinterpret<float>().AsNativeArray();
                        for (int i = 0; i < arr.Length; i++)
                            arr[i] = 0f;
                    }

                    for (int i = start; i < start + count; i++)
                    {
                        var blobRef = meshBlobsCdfe[ops[i].meshEntity];
                        if (blobRef.blob.IsCreated)
                        {
                            needsCollapse      = false;
                            ref var blobBounds = ref blobRef.blob.Value.maxRadialOffsetsInBoneSpaceByBone;
                            CheckAndLogBindingMismatch(boundsArray.Length, blobBounds.Length, skeletonEntity, ops[i].meshEntity, blobRef.blob);
                            int length = math.min(boundsArray.Length, blobBounds.Length);
                            for (int j = 0; j < length; j++)
                            {
                                boundsArray[j] = math.max(boundsArray[j], blobBounds[j]);
                            }
                        }
                    }

                    if (needsCollapse)
                    {
                        // Nothing valid is bound anymore. Shrink the buffer.
                        boundsBuffer.Clear();
                    }
                }
                else
                {
                    // Exposed skeleton path
                    if (!boundsCache.IsCreated)
                    {
                        boundsCache = new NativeList<float>(Allocator.Temp);
                    }
                    var boneRefs = boneRefsBfe[skeletonEntity].Reinterpret<Entity>().AsNativeArray();
                    boundsCache.Clear();
                    boundsCache.Resize(boneRefs.Length, NativeArrayOptions.ClearMemory);
                    var boundsArray = boundsCache.AsArray();

                    for (int i = start; i < start + count; i++)
                    {
                        var blobRef = meshBlobsCdfe[ops[i].meshEntity];
                        if (blobRef.blob.IsCreated)
                        {
                            ref var blobBounds = ref blobRef.blob.Value.maxRadialOffsetsInBoneSpaceByBone;
                            CheckAndLogBindingMismatch(boundsArray.Length, blobBounds.Length, skeletonEntity, ops[i].meshEntity, blobRef.blob);
                            int length = math.min(boundsArray.Length, blobBounds.Length);
                            for (int j = 0; j < length; j++)
                            {
                                boundsArray[j] = math.max(boundsArray[j], blobBounds[j]);
                            }
                        }
                    }

                    if (reset)
                    {
                        // Overwrite the bounds
                        for (int i = 0; i < boundsArray.Length; i++)
                        {
                            boneBoundsCdfe[boneRefs[i]] = new BoneBounds { radialOffsetInBoneSpace = boundsArray[i] };
                        }
                    }
                    else
                    {
                        // Merge with new values
                        for (int i = 0; i < boundsArray.Length; i++)
                        {
                            float storedBounds          = boneBoundsCdfe[boneRefs[i]].radialOffsetInBoneSpace;
                            boneBoundsCdfe[boneRefs[i]] = new BoneBounds { radialOffsetInBoneSpace = math.max(boundsArray[i], storedBounds) };
                        }
                    }
                }
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            void CheckAndLogBindingMismatch(int skeletonCount, int meshCount, Entity skeletonEntity, Entity meshEntity, BlobAssetReference<MeshSkinningBlob> blob)
            {
                if (skeletonCount != meshCount)
                {
                    FixedString128Bytes str = blob.Value.name;
                    UnityEngine.Debug.LogWarning(
                        $"Entity {meshEntity} with skinnedMesh {str} is being bound to skeleton root {skeletonEntity} which has a different number of bones (skeleton: {skeletonCount}, mesh: {meshCount}). This may lead to incorrect behavior.");
                }
            }
        }

        [BurstCompile]
        struct CleanupNewMeshesJob : IJobEntityBatch
        {
            [ReadOnly] public ComponentTypeHandle<BindSkeletonRoot>          rootRefHandle;
            [ReadOnly] public ComponentTypeHandle<MeshSkinningBlobReference> blobRefHandle;

            public ComponentTypeHandle<SkeletonDependent> dependentHandle;

            public uint globalSystemVersion;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                // We only want chunks we made changes to.
                var chunkOrderVersion = batchInChunk.GetOrderVersion();
                if (chunkOrderVersion != globalSystemVersion)
                    return;

                var roots = batchInChunk.GetNativeArray(rootRefHandle);
                var blobs = batchInChunk.GetNativeArray(blobRefHandle);
                var deps  = batchInChunk.GetNativeArray(dependentHandle);

                for (int i = 0; i < batchInChunk.Count; i++)
                {
                    deps[i] = new SkeletonDependent { root = roots[i].root, skinningBlob = blobs[i].blob };
                }
            }
        }

        [BurstCompile]
        struct SetExposedSkeletonCullingIndicesJob : IJobFor
        {
            [ReadOnly] public NativeArray<ExposedSkeletonCullingIndexOperation>                               operations;
            [ReadOnly] public BufferFromEntity<BoneReference>                                                 boneRefsBfe;
            [NativeDisableParallelForRestriction] public ComponentDataFromEntity<ExposedSkeletonCullingIndex> skeletonCullingIndexCdfe;
            [NativeDisableParallelForRestriction] public ComponentDataFromEntity<BoneCullingIndex>            boneCullingIndexCdfe;

            public void Execute(int index)
            {
                var op    = operations[index];
                var bones = boneRefsBfe[op.skeletonEntity].AsNativeArray();
                for (int i = 0; i < bones.Length; i++)
                {
                    boneCullingIndexCdfe[bones[i].bone] = new BoneCullingIndex { cullingIndex = op.index };
                }
                skeletonCullingIndexCdfe[op.skeletonEntity] = new ExposedSkeletonCullingIndex { cullingIndex = op.index };
            }
        }
    }
}

