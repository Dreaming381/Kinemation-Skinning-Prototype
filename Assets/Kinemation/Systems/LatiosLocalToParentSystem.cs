using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Latios.UnityReplacements
{
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateAfter(typeof(EndFrameLocalToParentSystem))]
    [UpdateBefore(typeof(EndFrameWorldToLocalSystem))]
    public unsafe class LatiosLocalToParentSystem3 : SubSystem
    {
        EntityQuery     m_childWithParentDependencyQuery;
        EntityQueryMask m_childWithParentDependencyMask;

        EntityQuery m_childQuery;
        EntityQuery m_childMissingDepthQuery;

        const int kMaxDepthIterations = 16;

        protected override void OnCreate()
        {
            m_childWithParentDependencyQuery = Fluent.WithAll<LocalToWorld>(false).WithAll<LocalToParent>(true).WithAll<Parent>(true).UseWriteGroups().Build();
            m_childWithParentDependencyMask  = m_childWithParentDependencyQuery.GetEntityQueryMask();
            m_childQuery                     = Fluent.WithAll<Parent>(true).WithAll<Depth>(true).Build();
            m_childMissingDepthQuery         = Fluent.WithAll<Parent>(true).Without<Depth>().Build();
        }

        protected override void OnUpdate()
        {
            if (!m_childMissingDepthQuery.IsEmptyIgnoreFilter)
            {
                var depthTypes = new ComponentTypes(typeof(Depth), ComponentType.ChunkComponent<ChunkDepthMask>());
                EntityManager.AddComponent(m_childMissingDepthQuery, depthTypes);
            }

            var parentHandle              = GetComponentTypeHandle<Parent>(true);
            var parentCdfe                = GetComponentDataFromEntity<Parent>(true);
            var childHandle               = GetBufferTypeHandle<Child>(true);
            var childBfe                  = GetBufferFromEntity<Child>(true);
            var depthWriteHandle          = GetComponentTypeHandle<Depth>(false);
            var depthReadHandle           = GetComponentTypeHandle<Depth>(true);
            var depthCdfe                 = GetComponentDataFromEntity<Depth>(false);
            var chunkDepthMaskWriteHandle = GetComponentTypeHandle<ChunkDepthMask>(false);
            var chunkDepthMaskReadHandle  = GetComponentTypeHandle<ChunkDepthMask>(true);
            var ltwWriteHandle            = GetComponentTypeHandle<LocalToWorld>(false);
            var ltwReadHandle             = GetComponentTypeHandle<LocalToWorld>(true);
            var ltpHandle                 = GetComponentTypeHandle<LocalToParent>(true);
            var entityHandle              = GetEntityTypeHandle();
            var ltwWriteCdfe              = GetComponentDataFromEntity<LocalToWorld>(false);
            var ltwReadCdfe               = GetComponentDataFromEntity<LocalToWorld>(true);
            var ltpCdfe                   = GetComponentDataFromEntity<LocalToParent>(true);

            uint lastSystemVersion = LastSystemVersion;

            var blockLists = new NativeArray<UnsafeParallelBlockList>(kMaxDepthIterations, Allocator.TempJob);
            for (int i = 0; i < kMaxDepthIterations; i++)
            {
                blockLists[i] = new UnsafeParallelBlockList(sizeof(ArchetypeChunk), 64, Allocator.TempJob);
            }
            var chunkList       = new NativeList<ArchetypeChunk>(Allocator.TempJob);
            var needsUpdateList = new NativeList<bool>(Allocator.TempJob);

            Dependency = new PatchDepthsJob
            {
                parentHandle      = parentHandle,
                parentCdfe        = parentCdfe,
                childHandle       = childHandle,
                childBfe          = childBfe,
                depthCdfe         = depthCdfe,
                depthHandle       = depthWriteHandle,
                lastSystemVersion = lastSystemVersion
            }.ScheduleParallel(m_childQuery, 1, Dependency);

            Dependency = new PatchChunkDepthMasksJob
            {
                depthHandle          = depthReadHandle,
                chunkDepthMaskHandle = chunkDepthMaskWriteHandle,
                lastSystemVersion    = lastSystemVersion
            }.ScheduleParallel(m_childQuery, 1, Dependency);

            Dependency = new ScatterChunksToDepthsJob
            {
                chunkDepthMaskHandle = chunkDepthMaskReadHandle,
                blockLists           = blockLists
            }.ScheduleParallel(m_childQuery, 1, Dependency);

            for (int i = 0; i < kMaxDepthIterations; i++)
            {
                Dependency = new ConvertBlockListToArrayJob
                {
                    chunkList       = chunkList,
                    needsUpdateList = needsUpdateList,
                    blockLists      = blockLists,
                    depthLevel      = i
                }.Schedule(Dependency);

                Dependency = new CheckIfMatricesShouldUpdateForSingleDepthLevelJob
                {
                    chunkList         = chunkList.AsDeferredJobArray(),
                    needsUpdateList   = needsUpdateList.AsDeferredJobArray(),
                    depth             = i,
                    depthHandle       = depthReadHandle,
                    entityHandle      = entityHandle,
                    lastSystemVersion = lastSystemVersion,
                    ltpHandle         = ltpHandle,
                    ltwCdfe           = ltwReadCdfe,
                    parentHandle      = parentHandle,
                    shouldUpdateMask  = m_childWithParentDependencyMask
                }.Schedule(chunkList, 1, Dependency);

                Dependency = new UpdateMatricesOfSingleDepthLevelJob
                {
                    chunkList       = chunkList.AsDeferredJobArray(),
                    needsUpdateList = needsUpdateList.AsDeferredJobArray(),
                    depth           = i,
                    depthHandle     = depthReadHandle,
                    ltpHandle       = ltpHandle,
                    ltwCdfe         = ltwReadCdfe,
                    ltwHandle       = ltwWriteHandle,
                    parentHandle    = parentHandle,
                }.Schedule(chunkList, 1, Dependency);
            }

            Dependency = new UpdateMatricesOfDeepChildrenJob
            {
                chunkList         = chunkList.AsDeferredJobArray(),
                childBfe          = childBfe,
                childHandle       = childHandle,
                depthHandle       = depthReadHandle,
                depthLevel        = kMaxDepthIterations - 1,
                lastSystemVersion = lastSystemVersion,
                ltwCdfe           = ltwWriteCdfe,
                ltpCdfe           = ltpCdfe,
                ltwHandle         = ltwReadHandle,
                ltwWriteGroupMask = m_childWithParentDependencyMask,
                parentCdfe        = parentCdfe
            }.Schedule(chunkList, 1, Dependency);

            Dependency = blockLists.Dispose(Dependency);
            Dependency = chunkList.Dispose(Dependency);
            Dependency = needsUpdateList.Dispose(Dependency);
        }

        struct Depth : IComponentData
        {
            public byte depth;
        }

        struct ChunkDepthMask : IComponentData
        {
            public BitField32 chunkDepthMask;
        }

        [BurstCompile]
        struct PatchDepthsJob : IJobEntityBatch
        {
            [ReadOnly] public ComponentTypeHandle<Parent>                                   parentHandle;
            [ReadOnly] public ComponentDataFromEntity<Parent>                               parentCdfe;
            [ReadOnly] public BufferTypeHandle<Child>                                       childHandle;
            [ReadOnly] public BufferFromEntity<Child>                                       childBfe;
            [NativeDisableContainerSafetyRestriction] public ComponentDataFromEntity<Depth> depthCdfe;
            public ComponentTypeHandle<Depth>                                               depthHandle;

            public uint lastSystemVersion;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                if (!batchInChunk.DidChange(parentHandle, lastSystemVersion))
                    return;

                var parents = batchInChunk.GetNativeArray(parentHandle);

                BufferAccessor<Child> childAccess         = default;
                bool                  hasChildrenToUpdate = batchInChunk.Has(childHandle);
                if (hasChildrenToUpdate)
                    childAccess           = batchInChunk.GetBufferAccessor(childHandle);
                NativeArray<Depth> depths = default;

                for (int i = 0; i < batchInChunk.Count; i++)
                {
                    if (IsDepthChangeRoot(parents[i].Value, out var depth))
                    {
                        if (!depths.IsCreated)
                            depths = batchInChunk.GetNativeArray(depthHandle);

                        var startDepth = new Depth { depth = depth };
                        depths[i]                          = startDepth;
                        startDepth.depth++;

                        if (hasChildrenToUpdate)
                        {
                            foreach (var child in childAccess[i])
                            {
                                WriteDepthAndRecurse(child.Value, startDepth);
                            }
                        }
                    }
                }
            }

            bool IsDepthChangeRoot(Entity parent, out byte depth)
            {
                var current = parent;
                depth       = 0;
                while (parentCdfe.HasComponent(current))
                {
                    if (parentCdfe.DidChange(current, lastSystemVersion))
                    {
                        return false;
                    }
                    depth++;
                    current = parentCdfe[current].Value;
                }
                return true;
            }

            void WriteDepthAndRecurse(Entity child, Depth depth)
            {
                depthCdfe[child] = depth;
                depth.depth++;
                if (childBfe.HasComponent(child))
                {
                    foreach (var c in childBfe[child])
                    {
                        WriteDepthAndRecurse(c.Value, depth);
                    }
                }
            }
        }

        [BurstCompile]
        struct PatchChunkDepthMasksJob : IJobEntityBatch
        {
            [ReadOnly] public ComponentTypeHandle<Depth> depthHandle;
            public ComponentTypeHandle<ChunkDepthMask>   chunkDepthMaskHandle;
            public uint                                  lastSystemVersion;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                if (batchInChunk.DidChange(depthHandle, lastSystemVersion) || batchInChunk.DidOrderChange(lastSystemVersion))
                {
                    BitField32 depthMask = default;
                    var        depths    = batchInChunk.GetNativeArray(depthHandle);
                    for (int i = 0; i < batchInChunk.Count; i++)
                    {
                        var clampDepth = math.min(kMaxDepthIterations, depths[i].depth);
                        depthMask.SetBits(clampDepth, true);
                    }

                    batchInChunk.SetChunkComponentData(chunkDepthMaskHandle, new ChunkDepthMask { chunkDepthMask = depthMask });
                }
            }
        }

        [BurstCompile]
        struct ScatterChunksToDepthsJob : IJobEntityBatch
        {
            [ReadOnly] public ComponentTypeHandle<ChunkDepthMask> chunkDepthMaskHandle;

            [NativeDisableParallelForRestriction, NativeDisableUnsafePtrRestriction] public NativeArray<UnsafeParallelBlockList> blockLists;

            [NativeSetThreadIndex]
            public int m_NativeThreadIndex;

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                var mask = batchInChunk.GetChunkComponentData(chunkDepthMaskHandle).chunkDepthMask;

                for (int i = 0; i < kMaxDepthIterations; i++)
                {
                    if (mask.IsSet(i))
                    {
                        blockLists[i].Write(batchInChunk, m_NativeThreadIndex);
                    }
                }
            }
        }

        // Todo: Make wide for each depth level?
        [BurstCompile]
        struct ConvertBlockListToArrayJob : IJob
        {
            public NativeList<ArchetypeChunk>                                               chunkList;
            public NativeList<bool>                                                         needsUpdateList;
            [NativeDisableUnsafePtrRestriction] public NativeArray<UnsafeParallelBlockList> blockLists;
            public int                                                                      depthLevel;

            public void Execute()
            {
                chunkList.Clear();
                needsUpdateList.Clear();
                int count = blockLists[depthLevel].Count();
                chunkList.ResizeUninitialized(count);
                needsUpdateList.ResizeUninitialized(count);
                blockLists[depthLevel].GetElementValues(chunkList.AsArray());
                blockLists[depthLevel].Dispose();
            }
        }

        [BurstCompile]
        struct CheckIfMatricesShouldUpdateForSingleDepthLevelJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<ArchetypeChunk> chunkList;

            [ReadOnly] public ComponentTypeHandle<LocalToParent>    ltpHandle;
            [ReadOnly] public ComponentTypeHandle<Parent>           parentHandle;
            [ReadOnly] public ComponentTypeHandle<Depth>            depthHandle;
            [ReadOnly] public EntityTypeHandle                      entityHandle;
            [ReadOnly] public ComponentDataFromEntity<LocalToWorld> ltwCdfe;

            public NativeArray<bool> needsUpdateList;

            public EntityQueryMask shouldUpdateMask;
            public int             depth;
            public uint            lastSystemVersion;

            public void Execute(int index)
            {
                var chunk = chunkList[index];

                if (!shouldUpdateMask.Matches(chunk.GetNativeArray(entityHandle)[0]))
                {
                    needsUpdateList[index] = false;
                    return;
                }

                var parents = chunk.GetNativeArray(parentHandle);
                var depths  = chunk.GetNativeArray(depthHandle);

                if (chunk.DidChange(parentHandle, lastSystemVersion) || chunk.DidChange(ltpHandle, lastSystemVersion))
                {
                    // Fast path. No need to check for changes on parent.
                    needsUpdateList[index] = true;
                }
                else
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (depth == depths[i].depth)
                        {
                            var parent = parents[i].Value;
                            if (ltwCdfe.DidChange(parent, lastSystemVersion))
                            {
                                needsUpdateList[index] = true;
                                return;
                            }
                        }
                    }
                    needsUpdateList[index] = false;
                }
            }
        }

        [BurstCompile]
        struct UpdateMatricesOfSingleDepthLevelJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<ArchetypeChunk>                                      chunkList;
            [NativeDisableContainerSafetyRestriction] public ComponentTypeHandle<LocalToWorld> ltwHandle;
            [ReadOnly] public NativeArray<bool>                                                needsUpdateList;

            [ReadOnly] public ComponentTypeHandle<LocalToParent>    ltpHandle;
            [ReadOnly] public ComponentTypeHandle<Parent>           parentHandle;
            [ReadOnly] public ComponentTypeHandle<Depth>            depthHandle;
            [ReadOnly] public ComponentDataFromEntity<LocalToWorld> ltwCdfe;

            public int depth;

            public void Execute(int index)
            {
                if (!needsUpdateList[index])
                    return;
                var chunk   = chunkList[index];
                var parents = chunk.GetNativeArray(parentHandle);
                var depths  = chunk.GetNativeArray(depthHandle);
                var ltps    = chunk.GetNativeArray(ltpHandle);

                // Fast path. No need to check for changes on parent since it already happened.
                NativeArray<LocalToWorld> ltws = chunk.GetNativeArray(ltwHandle);

                for (int i = 0; i < chunk.Count; i++)
                {
                    if (depth == depths[i].depth)
                    {
                        ltws[i] = new LocalToWorld { Value = math.mul(ltwCdfe[parents[i].Value].Value, ltps[i].Value) };
                    }
                }
            }
        }

        [BurstCompile]
        struct UpdateMatricesOfDeepChildrenJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<ArchetypeChunk>            chunkList;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld>      ltwHandle;
            [ReadOnly] public ComponentTypeHandle<Depth>             depthHandle;
            [ReadOnly] public BufferTypeHandle<Child>                childHandle;
            [ReadOnly] public BufferFromEntity<Child>                childBfe;
            [ReadOnly] public ComponentDataFromEntity<LocalToParent> ltpCdfe;
            [ReadOnly] public ComponentDataFromEntity<Parent>        parentCdfe;
            [ReadOnly] public EntityQueryMask                        ltwWriteGroupMask;
            public uint                                              lastSystemVersion;
            public int                                               depthLevel;

            [NativeDisableContainerSafetyRestriction]
            public ComponentDataFromEntity<LocalToWorld> ltwCdfe;

            void ChildLocalToWorld(ref float4x4 parentLocalToWorld, Entity entity, bool updateChildrenTransform, Entity parent, ref bool parentLtwValid)
            {
                updateChildrenTransform = updateChildrenTransform || ltpCdfe.DidChange(entity, lastSystemVersion) || parentCdfe.DidChange(entity,
                                                                                                                                          lastSystemVersion);

                float4x4 localToWorldMatrix = default;
                bool     ltwIsValid         = false;

                bool isDependent = ltwWriteGroupMask.Matches(entity);
                if (updateChildrenTransform && isDependent)
                {
                    if (!parentLtwValid)
                    {
                        parentLocalToWorld = ltwCdfe[parent].Value;
                        parentLtwValid     = true;
                    }
                    var localToParent  = ltpCdfe[entity];
                    localToWorldMatrix = math.mul(parentLocalToWorld, localToParent.Value);
                    ltwIsValid         = true;
                    ltwCdfe[entity]    = new LocalToWorld { Value = localToWorldMatrix };
                }
                else if (!isDependent)  //This entity has a component with the WriteGroup(LocalToWorld)
                {
                    updateChildrenTransform = updateChildrenTransform || ltwCdfe.DidChange(entity, lastSystemVersion);
                }
                if (childBfe.HasComponent(entity))
                {
                    var children = childBfe[entity];
                    for (int i = 0; i < children.Length; i++)
                    {
                        ChildLocalToWorld(ref localToWorldMatrix, children[i].Value, updateChildrenTransform, entity, ref ltwIsValid);
                    }
                }
            }

            public void Execute(int index)
            {
                var  batchInChunk            = chunkList[index];
                bool updateChildrenTransform =
                    batchInChunk.DidChange<LocalToWorld>(ltwHandle, lastSystemVersion) ||
                    batchInChunk.DidChange<Child>(childHandle, lastSystemVersion);

                var  chunkLocalToWorld = batchInChunk.GetNativeArray(ltwHandle);
                var  depths            = batchInChunk.GetNativeArray(depthHandle);
                var  chunkChildren     = batchInChunk.GetBufferAccessor(childHandle);
                bool ltwIsValid        = true;
                for (int i = 0; i < batchInChunk.Count; i++)
                {
                    if (depths[i].depth == depthLevel)
                    {
                        var localToWorldMatrix = chunkLocalToWorld[i].Value;
                        var children           = chunkChildren[i];
                        for (int j = 0; j < children.Length; j++)
                        {
                            ChildLocalToWorld(ref localToWorldMatrix, children[j].Value, updateChildrenTransform, Entity.Null, ref ltwIsValid);
                        }
                    }
                }
            }
        }
    }

    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateAfter(typeof(EndFrameLocalToParentSystem))]
    [UpdateBefore(typeof(EndFrameWorldToLocalSystem))]
    //[UpdateBefore(typeof(EndFrameLocalToParentSystem))]
    public class LocalToParentSystem2 : JobComponentSystem
    {
        private EntityQuery     m_RootsQuery;
        private EntityQueryMask m_LocalToWorldWriteGroupMask;
        private EntityQuery     m_ChildrenQuery;

        // LocalToWorld = Parent.LocalToWorld * LocalToParent
        [BurstCompile]
        struct UpdateHierarchy : IJobEntityBatch
        {
            [ReadOnly] public ComponentTypeHandle<LocalToWorld>      LocalToWorldTypeHandle;
            [ReadOnly] public BufferTypeHandle<Child>                ChildTypeHandle;
            [ReadOnly] public BufferFromEntity<Child>                ChildFromEntity;
            [ReadOnly] public ComponentDataFromEntity<LocalToParent> LocalToParentFromEntity;
            [ReadOnly] public EntityQueryMask                        LocalToWorldWriteGroupMask;
            public uint                                              LastSystemVersion;

            [NativeDisableContainerSafetyRestriction]
            public ComponentDataFromEntity<LocalToWorld> LocalToWorldFromEntity;

            void ChildLocalToWorld(ref float4x4 parentLocalToWorld, Entity entity, bool updateChildrenTransform, Entity parent, ref bool parentLtwValid)
            {
                updateChildrenTransform = updateChildrenTransform || LocalToParentFromEntity.DidChange(entity, LastSystemVersion);

                float4x4 localToWorldMatrix = default;
                bool     ltwIsValid         = false;

                bool isDependent = LocalToWorldWriteGroupMask.Matches(entity);
                if (updateChildrenTransform && isDependent)
                {
                    if (!parentLtwValid)
                    {
                        parentLocalToWorld = LocalToWorldFromEntity[parent].Value;
                        parentLtwValid     = true;
                    }
                    var localToParent              = LocalToParentFromEntity[entity];
                    localToWorldMatrix             = math.mul(parentLocalToWorld, localToParent.Value);
                    ltwIsValid                     = true;
                    LocalToWorldFromEntity[entity] = new LocalToWorld { Value = localToWorldMatrix };
                }
                else if (!isDependent)  //This entity has a component with the WriteGroup(LocalToWorld)
                {
                    updateChildrenTransform = updateChildrenTransform || LocalToWorldFromEntity.DidChange(entity, LastSystemVersion);
                }
                if (ChildFromEntity.HasComponent(entity))
                {
                    var children = ChildFromEntity[entity];
                    for (int i = 0; i < children.Length; i++)
                    {
                        ChildLocalToWorld(ref localToWorldMatrix, children[i].Value, updateChildrenTransform, entity, ref ltwIsValid);
                    }
                }
            }

            public void Execute(ArchetypeChunk batchInChunk, int batchIndex)
            {
                bool updateChildrenTransform =
                    batchInChunk.DidChange<LocalToWorld>(LocalToWorldTypeHandle, LastSystemVersion) ||
                    batchInChunk.DidChange<Child>(ChildTypeHandle, LastSystemVersion);

                var  chunkLocalToWorld = batchInChunk.GetNativeArray(LocalToWorldTypeHandle);
                var  chunkChildren     = batchInChunk.GetBufferAccessor(ChildTypeHandle);
                bool ltwIsValid        = true;
                for (int i = 0; i < batchInChunk.Count; i++)
                {
                    var localToWorldMatrix = chunkLocalToWorld[i].Value;
                    var children           = chunkChildren[i];
                    for (int j = 0; j < children.Length; j++)
                    {
                        ChildLocalToWorld(ref localToWorldMatrix, children[j].Value, updateChildrenTransform, Entity.Null, ref ltwIsValid);
                    }
                }
            }
        }

        protected override void OnCreate()
        {
            m_RootsQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadOnly<Child>()
                },
                None = new ComponentType[]
                {
                    typeof(Parent)
                },
                Options = EntityQueryOptions.FilterWriteGroup
            });

            m_ChildrenQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    typeof(LocalToWorld),
                    ComponentType.ReadOnly<LocalToParent>(),
                    ComponentType.ReadOnly<Parent>()
                },
                Options = EntityQueryOptions.FilterWriteGroup
            });
            m_LocalToWorldWriteGroupMask = EntityManager.GetEntityQueryMask(m_ChildrenQuery);
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var localToWorldType        = GetComponentTypeHandle<LocalToWorld>(true);
            var childType               = GetBufferTypeHandle<Child>(true);
            var childFromEntity         = GetBufferFromEntity<Child>(true);
            var localToParentFromEntity = GetComponentDataFromEntity<LocalToParent>(true);
            var localToWorldFromEntity  = GetComponentDataFromEntity<LocalToWorld>();

            var updateHierarchyJob = new UpdateHierarchy
            {
                LocalToWorldTypeHandle     = localToWorldType,
                ChildTypeHandle            = childType,
                ChildFromEntity            = childFromEntity,
                LocalToParentFromEntity    = localToParentFromEntity,
                LocalToWorldFromEntity     = localToWorldFromEntity,
                LocalToWorldWriteGroupMask = m_LocalToWorldWriteGroupMask,
                LastSystemVersion          = LastSystemVersion
            };
            inputDeps = updateHierarchyJob.ScheduleParallel(m_RootsQuery, 1, inputDeps);
            return inputDeps;
        }
    }
}

