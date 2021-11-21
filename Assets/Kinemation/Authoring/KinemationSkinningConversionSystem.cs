using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

using DeoptimizedCloneTracker = Latios.Kinemation.Authoring.HideThis.DeoptimizedCloneTracker;
using Hash128                 = Unity.Entities.Hash128;

namespace Latios.Kinemation.Authoring
{
    [UpdateBefore(typeof(SkinnedMeshRendererConversion))]
    public class KinemationSkinningConversionSystem : GameObjectConversionSystem
    {
        struct BindPoseMemory : IBufferElementData
        {
            public float4x4 bindPose;
        }

        struct ParentMemory : IBufferElementData
        {
            public short parentIndex;
        }

        static int s_computeIndexShaderProperty = Shader.PropertyToID("_ComputeMeshIndex");

        static ComponentTypes s_exposedSkeletonBoneComponentTypes = new ComponentTypes(new ComponentType[]
        {
            ComponentType.ReadWrite<BoneOwningSkeletonReference>(),
            ComponentType.ReadWrite<BoneTag>(),
            ComponentType.ReadWrite<BoneIndex>(),
            ComponentType.ReadWrite<BoneCullingIndex>(),
            ComponentType.ReadWrite<BoneBindPose>(),
            ComponentType.ReadWrite<BoneBounds>(),
            ComponentType.ReadWrite<BoneWorldBounds>(),
            ComponentType.ChunkComponent<ChunkBoneWorldBounds>(),
        });

        List<SkinnedMeshRenderer> m_exposedSkeletonRenderers   = new List<SkinnedMeshRenderer>();
        List<SkinnedMeshRenderer> m_optimizedSkeletonRenderers = new List<SkinnedMeshRenderer>();
        List<SkinnedMeshRenderer> m_noSkinRenderers            = new List<SkinnedMeshRenderer>();

        Dictionary<Mesh, BlobAssetReference<MeshSkinningBlob> > m_meshBlobs                          = new Dictionary<Mesh, BlobAssetReference<MeshSkinningBlob> >();
        Dictionary<Entity, Transform[]>                         m_exposedRoots                       = new Dictionary<Entity, Transform[]>();
        Dictionary<Transform, Entity>                           m_optimizedRoots                     = new Dictionary<Transform, Entity>();
        List<GameObject>                                        m_optimizedRootAssociatedGameObjects = new List<GameObject>();
        List<Mesh>                                              m_meshListToBlobify                  = new List<Mesh>();

        List<Matrix4x4>               m_bindposesCache          = new List<Matrix4x4>();
        List<Material>                m_materialsCache          = new List<Material>();
        List<DeoptimizedCloneTracker> m_cloneTrackersSrcCache   = new List<DeoptimizedCloneTracker>();
        List<DeoptimizedCloneTracker> m_cloneTrackersCloneCache = new List<DeoptimizedCloneTracker>();
        List<Transform>               m_transformCache          = new List<Transform>();

        bool m_isSubsceneConversion = false;

        protected override void OnUpdate()
        {
            if (HybridSkinningToggle.EnableHybrid)
                return;

            World.GetExistingSystem<SkinnedMeshRendererConversion>().Enabled = false;

            //Temporary workaround
            var tempList = new NativeList<int>(Allocator.TempJob);
            for (int i = 0; i < 5; i++)
                tempList.Add(i);
            unsafe
            {
                var result = xxHash3.Hash128(tempList.GetUnsafePtr(), tempList.Length * 4);
            }
            tempList.Dispose();

            m_exposedSkeletonRenderers.Clear();
            m_optimizedSkeletonRenderers.Clear();
            m_noSkinRenderers.Clear();
            m_meshBlobs.Clear();
            m_exposedRoots.Clear();
            m_optimizedRoots.Clear();
            m_optimizedRootAssociatedGameObjects.Clear();
            m_meshListToBlobify.Clear();

            m_isSubsceneConversion = DstEntityManager.World != World.DefaultGameObjectInjectionWorld;

            var renderMeshConversionContext = new RenderMeshConversionContext(DstEntityManager, this)
            {
                AttachToPrimaryEntityForSingleMaterial = true
            };

            Entities.ForEach((SkinnedMeshRenderer smr) =>
            {
                var mesh = smr.sharedMesh;
                DeclareAssetDependency(smr.gameObject, mesh);
                if (mesh == null)
                {
                    Debug.LogWarning($"Failed to convert Skinned Mesh Renderer on {smr.gameObject.name} because it is missing a mesh.");
                    return;
                }
                mesh.GetBindposes(m_bindposesCache);  // This clears the list which is undocumented.
                if (m_bindposesCache.Count == 0 || mesh.GetBonesPerVertex().Length == 0)  // mesh.GetBonesPerVertex uses Allocator.None which is undocumented.
                {
                    Debug.LogWarning(
                        $"The Skinned Mesh Renderer on {smr.gameObject.name} contains a mesh which does not support skinning. It will be converted as a regular Mesh Renderer instead.");
                    m_noSkinRenderers.Add(smr);
                    return;
                }
                if (!mesh.isReadable && !m_isSubsceneConversion)
                {
                    Debug.LogWarning(
                        $"The Skinned Mesh Renderer on {smr.gameObject.name} contains a mesh which is not marked for runtime reading. It will be converted as a regular Mesh Renderer instead. Mark the mesh as runtime read/write or use subscenes.");
                    m_noSkinRenderers.Add(smr);
                    return;
                }

                // Todo: GC alloc
                if (smr.bones.Length == m_bindposesCache.Count)
                {
                    m_exposedSkeletonRenderers.Add(smr);
                }
                else
                {
                    m_optimizedSkeletonRenderers.Add(smr);
                }
            });

            ConvertExposedSkeletons(renderMeshConversionContext);
            ConvertOptimizedSkeletons(renderMeshConversionContext);
            ConvertNonskinnedRenderers(renderMeshConversionContext);
            ConvertMeshes();

            renderMeshConversionContext.EndConversion();

            var query = DstEntityManager.CreateEntityQuery(typeof(BindPoseMemory));
            DstEntityManager.RemoveComponent<BindPoseMemory>(query);
            query.Dispose();
            query = DstEntityManager.CreateEntityQuery(typeof(ParentMemory));
            DstEntityManager.RemoveComponent<ParentMemory>(query);
            query.Dispose();

            query = DstEntityManager.CreateEntityQuery(ComponentType.ReadOnly<RenderMesh>(), ComponentType.ReadOnly<BindSkeletonRoot>());
            DstEntityManager.AddComponent<ComputeDeformShaderIndex>(query);
            DstEntityManager.AddComponent(                          query, ComponentType.ChunkComponent<ChunkComputeDeformMemoryMetadata>());
        }

        void ConvertExposedSkeletons(RenderMeshConversionContext renderMeshConversionContext)
        {
            foreach (var smr in m_exposedSkeletonRenderers)
            {
                var rootEntity = smr.rootBone != null? TryGetPrimaryEntity(smr.rootBone) : Entity.Null;
                if (rootEntity == Entity.Null)
                {
                    // Todo: Perform a search for the root-most bone in the bone list.
                    Debug.LogError($"A root bone was not found on the exposed skeleton for {smr.gameObject.name}. If you encounter this error, please report it!");
                    m_noSkinRenderers.Add(smr);
                    continue;
                }

                var mesh = smr.sharedMesh;
                mesh.GetBindposes(m_bindposesCache);

                if (m_exposedRoots.ContainsKey(rootEntity))
                {
                    var checks = CheckBindposes(rootEntity, smr);

                    if (checks.x == false)
                    {
                        // Todo: Find bone superset and rewrite all mesh bone weights.
                        m_noSkinRenderers.Add(smr);
                        continue;
                    }
                }
                else
                {
                    // Todo: Alloc
                    var bones = smr.bones;
                    m_exposedRoots.Add(rootEntity, bones);
                    AddBindposeMemoryFromCache(rootEntity);
                }

                smr.GetSharedMaterials(m_materialsCache);

                renderMeshConversionContext.Convert(smr, mesh, m_materialsCache, smr.transform);

                // Todo: Get rid of bounds
                RenderBounds     bounds        = new RenderBounds { Value = smr.localBounds.ToAABB() };
                BindSkeletonRoot rootReference                            = new BindSkeletonRoot { root = rootEntity };
                foreach (var entity in GetEntities(smr))
                {
                    if (DstEntityManager.HasComponent<RenderMesh>(entity))
                    {
                        DstEntityManager.AddComponentData(entity, bounds);
                        DstEntityManager.AddComponentData(entity, rootReference);
                        DstEntityManager.AddComponent<SkinningRenderCullingFlags>(entity);
                    }
                }

                if (!m_meshBlobs.ContainsKey(mesh))
                {
                    m_meshBlobs.Add(mesh, default);
                }
            }

            NativeList<BoneReference>  boneRefCache  = new NativeList<BoneReference>(Allocator.Temp);
            NativeList<BindPoseMemory> bindPoseCache = new NativeList<BindPoseMemory>(Allocator.Temp);

            foreach (var rootArrayPair in m_exposedRoots)
            {
                var rootEntity     = rootArrayPair.Key;
                var bonesGO        = rootArrayPair.Value;
                var bindposeBuffer = DstEntityManager.GetBuffer<BindPoseMemory>(rootEntity);
                bindPoseCache.Clear();
                bindPoseCache.AddRange(bindposeBuffer.AsNativeArray());

                boneRefCache.Clear();
                short i = 0;
                foreach (var boneTF in bonesGO)
                {
                    var boneEntity = GetPrimaryEntity(boneTF);
                    DstEntityManager.AddComponents(boneEntity, s_exposedSkeletonBoneComponentTypes);
                    DstEntityManager.SetComponentData(boneEntity, new BoneOwningSkeletonReference { skeletonRoot = rootEntity });
                    DstEntityManager.SetComponentData(boneEntity, new BoneBindPose { bindPose                    = bindPoseCache[i].bindPose });
                    DstEntityManager.SetComponentData(boneEntity, new BoneIndex { index                          = i++ });

                    boneRefCache.Add(new BoneReference { bone = boneEntity });
                }

                DstEntityManager.AddComponent<SkeletonRootTag>(                rootEntity);
                DstEntityManager.AddComponent<ExposedSkeletonCullingIndex>(    rootEntity);
                DstEntityManager.AddComponent<PerFrameSkeletonBufferMetadata>( rootEntity);
                var boneBuffer = DstEntityManager.AddBuffer<BoneReference>(rootEntity);
                boneBuffer.CopyFrom(boneRefCache);
            }
        }

        void ConvertOptimizedSkeletons(RenderMeshConversionContext renderMeshConversionContext)
        {
            NativeList<short>                        parentCache     = new NativeList<short>(Allocator.Temp);
            NativeList<Entity>                       entityCache     = new NativeList<Entity>(Allocator.Temp);
            NativeList<OptimizedBlobComputationData> blobComputeData = new NativeList<OptimizedBlobComputationData>(Allocator.TempJob);

            foreach (var smr in m_optimizedSkeletonRenderers)
            {
                // Todo: Adding components to prefabs is dirty and may cause problems. Find a better way to map things.
                var rootTF = smr.transform.root;
                var mesh   = smr.sharedMesh;
                mesh.GetBindposes(m_bindposesCache);

                Entity rootEntity = Entity.Null;

                if (m_optimizedRoots.ContainsKey(rootTF))
                {
                    rootEntity = m_optimizedRoots[rootTF];

                    var checks = CheckBindposes(rootEntity, smr);

                    if (checks.x == false)
                    {
                        // Todo: Find bone superset and rewrite all mesh bone weights.
                        m_noSkinRenderers.Add(smr);
                        continue;
                    }
                }
                else
                {
                    var animator = rootTF.GetComponentInChildren<Animator>();
                    if (animator == null)
                    {
                        Debug.LogWarning(
                            $"An animator was not found on the hierarchy {rootTF.gameObject.name} containing Skinned Mesh Renderer of {smr.gameObject.name}. Because this is an optimized hierarchy, the skeleton could not be extracted, and the Skinned Mesh Renderer will be converted as a Mesh Renderer instead.");
                        m_noSkinRenderers.Add(smr);
                        continue;
                    }

                    m_cloneTrackersSrcCache.Clear();
                    DeoptimizedCloneTracker smrTracker = null;
                    rootTF.GetComponentsInChildren(m_transformCache);
                    int trackerCounter = 0;
                    foreach (var tf in m_transformCache)
                    {
                        DeoptimizedCloneTracker tracker;
                        if (tf.gameObject.GetComponent<DeoptimizedCloneTracker>() != null)
                            tracker = tf.gameObject.GetComponent<DeoptimizedCloneTracker>();
                        else
                            tracker       = tf.gameObject.AddComponent<DeoptimizedCloneTracker>();
                        tracker.trackerId = trackerCounter++;
                        m_cloneTrackersSrcCache.Add(tracker);
                        if (tracker.transform == smr.transform)
                            smrTracker = tracker;
                    }

                    var clone = GameObject.Instantiate(smr.transform.root.gameObject);
                    AnimatorUtility.DeoptimizeTransformHierarchy(clone);
                    clone.GetComponentsInChildren(m_cloneTrackersCloneCache);
                    SkinnedMeshRenderer cloneSmr = null;
                    foreach (var tracker in m_cloneTrackersCloneCache)
                    {
                        if (tracker.trackerId == smrTracker.trackerId)
                        {
                            cloneSmr = tracker.GetComponent<SkinnedMeshRenderer>();
                            break;
                        }
                    }

                    // Todo: Alloc
                    var cloneBones = cloneSmr.bones;
                    if (cloneBones.Length != m_bindposesCache.Count)
                    {
                        Debug.LogError(
                            $"Failed to extract skeleton from hierarchy {rootTF.gameObject.name} for Skinned Mesh Renderer of {smr.gameObject.name}. If you are seeing this error, please report it!");
                        GameObject.DestroyImmediate(clone);
                        foreach (var tracker in m_cloneTrackersSrcCache)
                            Object.DestroyImmediate(tracker, true);
                    }

                    parentCache.Clear();
                    parentCache.ResizeUninitialized(cloneBones.Length);
                    entityCache.Clear();
                    for (short i = 0; i < cloneBones.Length; i++)
                    {
                        parentCache[i] = -1;
                        for (short j = 0; j < cloneBones.Length; j++)
                        {
                            if (cloneBones[j] == cloneBones[i].parent)
                            {
                                if (j > i)
                                    Debug.LogError(
                                        $"The bones in hierarchy {rootTF.gameObject.name} for Skinned Mesh Renderer of {smr.gameObject.name} are not in depth-first order. If you are seeing this error, please report it!");
                                parentCache[i] = j;
                                break;
                            }
                        }

                        var tracker = cloneBones[i].GetComponent<DeoptimizedCloneTracker>();
                        if (tracker != null)
                        {
                            foreach (var originalExposed in m_cloneTrackersSrcCache)
                            {
                                var exposedBoneEntity = GetPrimaryEntity(originalExposed);
                                if (originalExposed.trackerId == tracker.trackerId)
                                {
                                    if (smr.rootBone == originalExposed.transform || cloneSmr.rootBone == tracker.transform)
                                    {
                                        rootEntity = exposedBoneEntity;
                                        m_optimizedRootAssociatedGameObjects.Add(originalExposed.gameObject);
                                    }
                                    else
                                    {
                                        entityCache.Add(exposedBoneEntity);
                                        DstEntityManager.AddComponentData(exposedBoneEntity, new CopyLocalToWorldFromBone { boneIndex = i });
                                    }
                                }
                            }
                        }
                    }

                    if (rootEntity == Entity.Null)
                    {
                        rootEntity = GetPrimaryEntity(animator);
                        m_optimizedRootAssociatedGameObjects.Add(animator.gameObject);
                    }
                    blobComputeData.Add(new OptimizedBlobComputationData { entity = rootEntity });

                    DstEntityManager.AddComponent<SkeletonRootTag>(rootEntity);
                    DstEntityManager.AddBuffer<OptimizedBoneBounds>(rootEntity);  // Don't resize until something gets bound to it
                    DstEntityManager.AddComponent<SkeletonWorldBounds>(rootEntity);
                    DstEntityManager.AddChunkComponentData<ChunkSkeletonWorldBounds>(rootEntity);
                    DstEntityManager.AddComponent<PerFrameSkeletonBufferMetadata>(rootEntity);
                    var btrBuffer = DstEntityManager.AddBuffer<OptimizedBoneToRoot>(rootEntity).Reinterpret<float4x4>();
                    btrBuffer.ResizeUninitialized(cloneBones.Length);

                    float4x4 rootInverse = cloneBones[0].worldToLocalMatrix;
                    for (int i = 0; i < btrBuffer.Length; i++)
                    {
                        btrBuffer[i] = math.mul(rootInverse, cloneBones[i].localToWorldMatrix);
                    }

                    var parentBuffer = DstEntityManager.AddBuffer<ParentMemory>(rootEntity).Reinterpret<short>();
                    parentBuffer.CopyFrom(parentCache);

                    AddBindposeMemoryFromCache(rootEntity);

                    foreach (var e in entityCache)
                    {
                        DstEntityManager.AddComponentData(e, new BoneOwningSkeletonReference { skeletonRoot = rootEntity });
                    }

                    // Write to map
                    m_optimizedRoots.Add(rootTF, rootEntity);

                    // Teardown
                    GameObject.DestroyImmediate(clone);
                    foreach (var tracker in m_cloneTrackersSrcCache)
                        Object.DestroyImmediate(tracker, true);
                }

                smr.GetSharedMaterials(m_materialsCache);

                renderMeshConversionContext.Convert(smr, mesh, m_materialsCache, smr.transform);

                // Todo: Get rid of bounds
                RenderBounds     bounds        = new RenderBounds { Value = smr.localBounds.ToAABB() };
                BindSkeletonRoot rootReference                            = new BindSkeletonRoot { root = rootEntity };
                foreach (var entity in GetEntities(smr))
                {
                    if (DstEntityManager.HasComponent<RenderMesh>(entity))
                    {
                        DstEntityManager.AddComponentData(entity, bounds);
                        DstEntityManager.AddComponentData(entity, rootReference);
                        DstEntityManager.AddComponent<SkinningRenderCullingFlags>(entity);
                    }
                }

                if (!m_meshBlobs.ContainsKey(mesh))
                {
                    m_meshBlobs.Add(mesh, default);
                }
            }

            var leechSystem  = DstEntityManager.World.GetOrCreateSystem<LeechSystem>();
            var bindposesBfe = leechSystem.GetBufferFromEntity<BindPoseMemory>(true);
            var parentBfe    = leechSystem.GetBufferFromEntity<ParentMemory>(true);
            new ComputeOptimizedSkeletonBlobHashsJob
            {
                array       = blobComputeData,
                bindposeBfe = bindposesBfe,
                parentBfe   = parentBfe
            }.ScheduleParallel(blobComputeData.Length, 1, default).Complete();

            var computationContext =
                new BlobAssetComputationContext<OptimizedBlobComputationData, OptimizedBindSkeletonBlob>(BlobAssetStore, blobComputeData.Length, Allocator.Temp);
            for (int i = 0; i < blobComputeData.Length; i++)
            {
                computationContext.AssociateBlobAssetWithUnityObject(blobComputeData[i].hash, m_optimizedRootAssociatedGameObjects[i]);
                if (computationContext.NeedToComputeBlobAsset(blobComputeData[i].hash))
                    computationContext.AddBlobAssetToCompute(blobComputeData[i].hash, blobComputeData[i]);
            }

            var needCompute = computationContext.GetSettings(Allocator.TempJob);
            new ComputeOptimizedSkeletonBlobsJob
            {
                array       = needCompute,
                bindposeBfe = bindposesBfe,
                parentBfe   = parentBfe
            }.ScheduleParallel(needCompute.Length, 1, default).Complete();
            foreach (var c in needCompute)
                computationContext.AddComputedBlobAsset(c.hash, c.blob.blob);

            for (int i = 0; i < blobComputeData.Length; i++)
            {
                computationContext.GetBlobAsset(blobComputeData[i].hash, out var blob);
                DstEntityManager.AddComponentData(blobComputeData[i].entity, new OptimizedBindSkeletonBlobReference { blob = blob });
            }
            needCompute.Dispose();
            computationContext.Dispose();
            blobComputeData.Dispose();
        }

        void ConvertNonskinnedRenderers(RenderMeshConversionContext renderMeshConversionContext)
        {
            foreach (var smr in m_noSkinRenderers)
            {
                smr.GetSharedMaterials(m_materialsCache);

                renderMeshConversionContext.Convert(smr, smr.sharedMesh, m_materialsCache, smr.transform);
                RenderBounds bounds = new RenderBounds { Value = smr.localBounds.ToAABB() };
                foreach (var entity in GetEntities(smr))
                {
                    if (DstEntityManager.HasComponent<RenderMesh>(entity))
                        DstEntityManager.AddComponentData(entity, bounds);
                }
            }
        }

        void ConvertMeshes()
        {
            int count                       = m_meshBlobs.Count;
            var bindPoseStartIndicesPerMesh = new NativeArray<int>(count, Allocator.TempJob);
            var bindPosesPerMesh            = new NativeArray<short>(count, Allocator.TempJob);
            var bindPoses                   = new NativeList<float4x4>(Allocator.TempJob);
            var names                       = new NativeArray<FixedString128>(count, Allocator.TempJob);
            int startCounter                = 0;
            foreach(var mesh in m_meshBlobs.Keys)
            {
                m_meshListToBlobify.Add(mesh);
                mesh.GetBindposes(m_bindposesCache);
                names[startCounter]                         = mesh.name;
                bindPosesPerMesh[startCounter]              = (short)m_bindposesCache.Count;
                bindPoseStartIndicesPerMesh[startCounter++] = bindPoses.Length;
                foreach (var bp in m_bindposesCache)
                    bindPoses.Add(bp);
            }

            var meshDataArray   = Mesh.AcquireReadOnlyMeshData(m_meshListToBlobify);
            var computationData = new NativeArray<MeshSkinningComputationData>(count, Allocator.TempJob);

            new ComputeMeshSkinningHashes
            {
                meshes                      = meshDataArray,
                bindPosesPerMesh            = bindPosesPerMesh,
                bindPoseStartIndicesPerMesh = bindPoseStartIndicesPerMesh,
                bindPoses                   = bindPoses,
                meshNames                   = names,
                dataArray                   = computationData
            }.ScheduleParallel(count, 1, default).Complete();

            var computationContext = new BlobAssetComputationContext<MeshSkinningComputationData, MeshSkinningBlob>(BlobAssetStore, count, Allocator.Temp);
            for (int i = 0; i < count; i++)
            {
                computationContext.AssociateBlobAssetWithUnityObject(computationData[i].hash, m_meshListToBlobify[i]);
                if (computationContext.NeedToComputeBlobAsset(computationData[i].hash))
                    computationContext.AddBlobAssetToCompute(computationData[i].hash, computationData[i]);
            }

            var needsCompute            = computationContext.GetSettings(Allocator.TempJob);
            var verticesStartsPerMesh   = new NativeArray<int>(needsCompute.Length, Allocator.TempJob);
            var boneWeightStartsPerMesh = new NativeArray<int>(needsCompute.Length, Allocator.TempJob);
            int runningVertexCount      = 0;
            int runningWeightCount      = 0;
            for (int i = 0; i < needsCompute.Length; i++)
            {
                verticesStartsPerMesh[i]    = runningVertexCount;
                boneWeightStartsPerMesh[i]  = runningWeightCount;
                var mesh                    = m_meshListToBlobify[computationData[i].meshIndex];
                runningVertexCount         += mesh.vertexCount;
                runningWeightCount         += mesh.GetAllBoneWeights().Length;
            }
            var boneWeightCountsPerVertex = new NativeArray<byte>(runningVertexCount, Allocator.TempJob);
            var boneWeights               = new NativeArray<BoneWeight1>(runningWeightCount, Allocator.TempJob);
            for (int i = 0; i < needsCompute.Length; i++)
            {
                var mesh = m_meshListToBlobify[computationData[i].meshIndex];
                var src  = mesh.GetBonesPerVertex();
                NativeArray<byte>.Copy(src, 0, boneWeightCountsPerVertex, verticesStartsPerMesh[i], src.Length);
                var src2 = mesh.GetAllBoneWeights();
                NativeArray<BoneWeight1>.Copy(src2, 0, boneWeights, boneWeightStartsPerMesh[i], src2.Length);
            }

            new ComputeMeshSkinningBlobs
            {
                meshes                      = meshDataArray,
                verticesStartsPerMesh       = verticesStartsPerMesh,
                boneWeightStartsPerMesh     = boneWeightStartsPerMesh,
                boneWeightCountsPerVertex   = boneWeightCountsPerVertex,
                boneWeights                 = boneWeights,
                bindPosesPerMesh            = bindPosesPerMesh,
                bindPoseStartIndicesPerMesh = bindPoseStartIndicesPerMesh,
                bindPoses                   = bindPoses,
                meshNames                   = names,
                dataArray                   = needsCompute
            }.ScheduleParallel(needsCompute.Length, 1, default).Complete();

            foreach (var c in needsCompute)
            {
                computationContext.AddComputedBlobAsset(c.hash, c.blobReference);
            }

            for (int i = 0; i < count; i++)
            {
                computationContext.GetBlobAsset(computationData[i].hash, out var blob);
                m_meshBlobs[m_meshListToBlobify[i]] = blob;
            }

            meshDataArray.Dispose();
            verticesStartsPerMesh.Dispose();
            boneWeightStartsPerMesh.Dispose();
            boneWeightCountsPerVertex.Dispose();
            boneWeights.Dispose();
            bindPosesPerMesh.Dispose();
            bindPoseStartIndicesPerMesh.Dispose();
            bindPoses.Dispose();
            names.Dispose();
            needsCompute.Dispose();
            computationData.Dispose();
            computationContext.Dispose();

            foreach (var smr in m_exposedSkeletonRenderers)
            {
                var blob     = m_meshBlobs[smr.sharedMesh];
                var entities = GetEntities(smr);
                foreach (var e in entities)
                {
                    if (DstEntityManager.HasComponent<RenderMesh>(e))
                    {
                        DstEntityManager.AddComponentData(e, new MeshSkinningBlobReference { blob = blob });
                    }
                }
            }

            foreach (var smr in m_optimizedSkeletonRenderers)
            {
                var blob     = m_meshBlobs[smr.sharedMesh];
                var entities = GetEntities(smr);
                foreach (var e in entities)
                {
                    if (DstEntityManager.HasComponent<RenderMesh>(e))
                    {
                        DstEntityManager.AddComponentData(e, new MeshSkinningBlobReference { blob = blob });
                    }
                }
            }
        }

        // x = lengths match, y = matrices match
        bool2 CheckBindposes(Entity entity, SkinnedMeshRenderer smr)
        {
            var bindposeBuffer = DstEntityManager.GetBuffer<BindPoseMemory>(entity);
            smr.sharedMesh.GetBindposes(m_bindposesCache);
            if (m_bindposesCache.Count != bindposeBuffer.Length)
            {
                Debug.LogError(
                    $"The Skinned Mesh Renderer of {smr.gameObject.name} has a different number of bones than another Skinned Mesh Renderer in the same hierarchy. If you encounter this error, please report it!");
                return false;
            }
            for (int i = 0; i < bindposeBuffer.Length; i++)
            {
                float4x4 bindpose = m_bindposesCache[i];
                if (!bindposeBuffer[i].Equals(bindpose))
                {
                    Debug.LogError(
                        $"The Skinned Mesh Renderer of {smr.gameObject.name} has different bindposes than another Skinned Mesh Renderer in the same hierarchy. If you encounter this error, please report it!");
                    return new bool2(true, false);
                }
            }

            return true;
        }

        void AddBindposeMemoryFromCache(Entity entity)
        {
            var buffer = DstEntityManager.AddBuffer<BindPoseMemory>(entity).Reinterpret<float4x4>();
            buffer.ResizeUninitialized(m_bindposesCache.Count);
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = m_bindposesCache[i];
            }
        }

        struct OptimizedBlobComputationData
        {
            public Entity                             entity;
            public Hash128                            hash;
            public OptimizedBindSkeletonBlobReference blob;
        }

        [BurstCompile]
        struct ComputeOptimizedSkeletonBlobHashsJob : IJobFor
        {
            public NativeArray<OptimizedBlobComputationData>   array;
            [ReadOnly] public BufferFromEntity<BindPoseMemory> bindposeBfe;
            [ReadOnly] public BufferFromEntity<ParentMemory>   parentBfe;

            public unsafe void Execute(int index)
            {
                var data           = array[index];
                var entity         = data.entity;
                var bindposeBuffer = bindposeBfe[entity];
                var parentBuffer   = parentBfe[entity];
                var hashLow        = xxHash3.Hash64(bindposeBuffer.GetUnsafeReadOnlyPtr(), sizeof(BindPoseMemory) * bindposeBuffer.Length);
                var hashHigh       = xxHash3.Hash64(parentBuffer.GetUnsafeReadOnlyPtr(), sizeof(ParentMemory) * parentBuffer.Length);
                data.hash          = new Hash128(hashLow.x, hashLow.y, hashHigh.x, hashHigh.y);
                array[index]       = data;
            }
        }

        [BurstCompile]
        struct ComputeOptimizedSkeletonBlobsJob : IJobFor
        {
            public NativeArray<OptimizedBlobComputationData>   array;
            [ReadOnly] public BufferFromEntity<BindPoseMemory> bindposeBfe;
            [ReadOnly] public BufferFromEntity<ParentMemory>   parentBfe;

            public unsafe void Execute(int index)
            {
                var data           = array[index];
                var entity         = data.entity;
                var bindposeBuffer = bindposeBfe[entity];
                var parentBuffer   = parentBfe[entity];

                var     builder = new BlobBuilder(Allocator.Temp);
                ref var root    = ref builder.ConstructRoot<OptimizedBindSkeletonBlob>();
                builder.ConstructFromNativeArray(ref root.parentIndices, parentBuffer.Reinterpret<short>().AsNativeArray());
                builder.ConstructFromNativeArray(ref root.bindPoses,     bindposeBuffer.Reinterpret<float4x4>().AsNativeArray());
                data.blob.blob = builder.CreateBlobAssetReference<OptimizedBindSkeletonBlob>(Allocator.Persistent);

                array[index] = data;
            }
        }

        struct MeshSkinningComputationData
        {
            public int                                  meshIndex;
            public Hash128                              hash;
            public BlobAssetReference<MeshSkinningBlob> blobReference;
        }

        [BurstCompile]
        struct ComputeMeshSkinningHashes : IJobFor
        {
            [ReadOnly] public Mesh.MeshDataArray          meshes;
            [ReadOnly] public NativeArray<short>          bindPosesPerMesh;
            [ReadOnly] public NativeArray<int>            bindPoseStartIndicesPerMesh;
            [ReadOnly] public NativeArray<float4x4>       bindPoses;
            [ReadOnly] public NativeArray<FixedString128> meshNames;

            public NativeArray<MeshSkinningComputationData> dataArray;

            public unsafe void Execute(int index)
            {
                var mesh       = meshes[index];
                var data       = dataArray[index];
                data.meshIndex = index;

                var bindPoseSubArray = bindPoses.GetSubArray(bindPoseStartIndicesPerMesh[index], bindPosesPerMesh[index]);
                var hashHigh         = xxHash3.Hash64(meshNames[index]);
                hashHigh             = xxHash3.Hash64(bindPoseSubArray.GetUnsafeReadOnlyPtr(), sizeof(float4x4) * bindPoseSubArray.Length, ((ulong)hashHigh.y << 32) | hashHigh.x);

                var hashLow = hashHigh;
                for (int i = 0; i < mesh.vertexBufferCount; i++)
                {
                    var buffer = mesh.GetVertexData<byte>(i);
                    hashLow    = xxHash3.Hash64(buffer.GetUnsafeReadOnlyPtr(), buffer.Length, ((ulong)hashLow.y << 32) | hashLow.x);
                }
                data.hash        = new Hash128(hashLow.x, hashLow.y, hashHigh.x, hashHigh.y);
                dataArray[index] = data;
            }
        }

        [BurstCompile]
        struct ComputeMeshSkinningBlobs : IJobFor
        {
            [ReadOnly] public Mesh.MeshDataArray          meshes;
            [ReadOnly] public NativeArray<int>            verticesStartsPerMesh;  //IJobFor index
            [ReadOnly] public NativeArray<int>            boneWeightStartsPerMesh;  //IJobFor index
            [ReadOnly] public NativeArray<byte>           boneWeightCountsPerVertex;
            [ReadOnly] public NativeArray<BoneWeight1>    boneWeights;
            [ReadOnly] public NativeArray<short>          bindPosesPerMesh;  //dataArray index
            [ReadOnly] public NativeArray<int>            bindPoseStartIndicesPerMesh;  //dataArray index
            [ReadOnly] public NativeArray<float4x4>       bindPoses;
            [ReadOnly] public NativeArray<FixedString128> meshNames;  //dataArray index

            [NativeDisableContainerSafetyRestriction] NativeList<Vector3> vector3Cache;
            [NativeDisableContainerSafetyRestriction] NativeList<Vector4> vector4Cache;

            public NativeArray<MeshSkinningComputationData> dataArray;

            public unsafe void Execute(int index)
            {
                if (!vector3Cache.IsCreated)
                {
                    vector3Cache = new NativeList<Vector3>(Allocator.Temp);
                    vector4Cache = new NativeList<Vector4>(Allocator.Temp);
                }

                var builder = new BlobBuilder(Allocator.Temp);

                ref var blobRoot = ref builder.ConstructRoot<MeshSkinningBlob>();
                var     data     = dataArray[index];
                var     mesh     = meshes[data.meshIndex];

                //builder.AllocateFixedString(ref blobRoot.name, meshNames[data.meshIndex]);
                blobRoot.name = meshNames[data.meshIndex];

                blobRoot.authoredHash = data.hash;

                var verticesToSkin = (VertexToSkin*)builder.Allocate(ref blobRoot.verticesToSkin, mesh.vertexCount).GetUnsafePtr();
                vector3Cache.ResizeUninitialized(mesh.vertexCount);
                mesh.GetVertices(vector3Cache);
                var t = vector3Cache.AsArray().Reinterpret<float3>();
                for (int i = 0; i < mesh.vertexCount; i++)
                {
                    verticesToSkin[i].position = t[i];
                }
                mesh.GetNormals(vector3Cache);
                for (int i = 0; i < mesh.vertexCount; i++)
                {
                    verticesToSkin[i].normal = t[i];
                }
                vector4Cache.ResizeUninitialized(mesh.vertexCount);
                mesh.GetTangents(vector4Cache);
                var tt = vector4Cache.AsArray().Reinterpret<float4>();
                for (int i = 0; i < mesh.vertexCount; i++)
                {
                    verticesToSkin[i].tangent = tt[i].xyz;
                }

                var maxRadialOffsets = builder.Allocate(ref blobRoot.maxRadialOffsetsInBoneSpaceByBone, bindPosesPerMesh[data.meshIndex]);
                for (int i = 0; i < maxRadialOffsets.Length; i++)
                    maxRadialOffsets[i] = 0;

                int boneWeightBatches = mesh.vertexCount / 1024;
                if (mesh.vertexCount % 1024 != 0)
                    boneWeightBatches++;
                var boneWeightStarts      = builder.Allocate(ref blobRoot.boneWeightBatchStarts, boneWeightBatches);
                int verticesStart         = verticesStartsPerMesh[index];
                var weightCountsPerVertex = boneWeightCountsPerVertex.GetSubArray(verticesStart, verticesStart + mesh.vertexCount);
                int weightsStart          = boneWeightStartsPerMesh[index];
                int weightsCount          = boneWeights.Length - weightsStart;
                if (boneWeightStartsPerMesh.Length > index + 1)
                {
                    weightsCount = boneWeightStartsPerMesh[index + 1] - weightsStart;
                }
                var boneWeightsSrc = boneWeights.GetSubArray(weightsStart, weightsCount);
                var boneWeightsDst = builder.Allocate(ref blobRoot.boneWeights, weightsCount + boneWeightBatches);
                var meshBindPoses  = bindPoses.GetSubArray(bindPoseStartIndicesPerMesh[data.meshIndex], bindPosesPerMesh[data.meshIndex]);

                var weightStartsPerCache    = stackalloc int[1024];
                int weightStartsBatchOffset = 0;

                int dst = 0;
                for (int batchIndex = 0; batchIndex < boneWeightBatches; batchIndex++)
                {
                    int batchHeaderIndex = dst;
                    dst++;
                    int verticesInBatch = math.min(1024, mesh.vertexCount - batchIndex * 1024);
                    int batchOffset     = batchIndex * 1024;
                    int threadsAlive    = verticesInBatch;

                    int weightStartCounter = 0;
                    for (int i = 0; i < verticesInBatch; i++)
                    {
                        weightStartsPerCache[i]  = weightStartCounter;
                        weightStartCounter      += weightCountsPerVertex[batchOffset + i];
                    }

                    for (int weightRound = 1; threadsAlive > 0; weightRound++)
                    {
                        for (int i = 0; i < verticesInBatch; i++)
                        {
                            int weightsForThisVertex = weightCountsPerVertex[batchOffset + i];
                            if (weightsForThisVertex < weightRound)
                                continue;
                            bool retireThisRound = weightsForThisVertex == weightRound;
                            var  srcWeight       = boneWeightsSrc[weightStartsPerCache[i] + weightStartsBatchOffset + weightRound - 1];
                            var  dstWeight       = new BoneWeightLinkedList
                            {
                                weight           = math.select(srcWeight.weight, -srcWeight.weight, retireThisRound),
                                next10Lds7Bone15 = (((uint)threadsAlive - 1) << 22) | (uint)(srcWeight.boneIndex & 0x8fff)
                            };

                            boneWeightsDst[dst] = dstWeight;
                            dst++;

                            float3 boneSpacePosition              = math.transform(meshBindPoses[srcWeight.boneIndex], verticesToSkin[i].position);
                            maxRadialOffsets[srcWeight.boneIndex] = math.max(maxRadialOffsets[srcWeight.boneIndex], math.length(boneSpacePosition));

                            if (retireThisRound)
                                threadsAlive--;
                        }
                    }

                    weightStartsBatchOffset += weightStartCounter;

                    boneWeightsDst[batchHeaderIndex] = new BoneWeightLinkedList
                    {
                        weight           = math.asfloat(0xbb000000 | (uint)batchIndex),
                        next10Lds7Bone15 = (uint)weightStartCounter + 1
                    };
                    boneWeightStarts[batchIndex] = (uint)batchHeaderIndex;
                }

                data.blobReference = builder.CreateBlobAssetReference<MeshSkinningBlob>(Allocator.Persistent);
                dataArray[index]   = data;
            }
        }
    }

    class LeechSystem : SystemBase
    {
        protected override void OnCreate()
        {
            Enabled = false;
        }

        protected override void OnUpdate()
        {
            Enabled = false;
        }
    }
}

