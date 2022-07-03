using System.Collections.Generic;
using Latios;
using Latios.Kinemation;
using Random = UnityEngine.Random;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Jobs;

namespace Dragons
{
    public partial class SpawnAndBuildReferencesSystem : SubSystem
    {
        struct ColorInitializedTag : IComponentData { }
        struct DancerInitializedTag : IComponentData { }

        Rng         m_rng = new Rng("SpawnAndBuildReferencesSystem");
        EntityQuery m_spawnerQuery;
        EntityQuery m_dancerColorQuery;
        EntityQuery m_dancerChildQuery;

        protected override void OnUpdate()
        {
            var icb                = new InstantiateCommandBuffer<Translation, Rotation, DancerDots>(Allocator.TempJob);
            var icbp               = icb.AsParallelWriter();
            var dcb                = new DestroyCommandBuffer(Allocator.TempJob);
            var renderersInPrefabs = new NativeList<Entity>(Allocator.TempJob);

            Entities.ForEach((Entity entity, DancerReferenceGroupHybrid hybrid, in SpawnerDots spawner) =>
            {
                var smr = hybrid.dancerGoPrefab.GetComponentInChildren<SkinnedMeshRenderer>();
                if (EntityManager.HasComponent<BoneReference>(spawner.dancerPrefab))
                    hybrid.bonesPerReference = EntityManager.GetBuffer<BoneReference>(spawner.dancerPrefab, true).Length;
                else
                    hybrid.bonesPerReference = EntityManager.GetBuffer<OptimizedBoneToRoot>(spawner.dancerPrefab, true).Length;

                var transformArray = new DancerReferenceGroupTransforms
                {
                    transforms = new TransformAccessArray(hybrid.bonesPerReference * spawner.referencesToSpawn)
                };

                for (int i = 0; i < spawner.referencesToSpawn; i++)
                {
                    var newGo  = GameObject.Instantiate(hybrid.dancerGoPrefab);
                    var newSmr = newGo.GetComponentInChildren<SkinnedMeshRenderer>();
                    AddTransformsInSkeletonOrder(transformArray.transforms, newGo, EntityManager.GetComponentData<SkeletonBindingPathsBlobReference>(spawner.dancerPrefab));
                    newSmr.enabled = false;
                    InitializeReference(newGo);
                }
                EntityManager.AddCollectionComponent(entity, transformArray);

                var prefab = EntityManager.Instantiate(spawner.dancerPrefab);
                foreach (var child in EntityManager.GetBuffer<LinkedEntityGroup>(prefab))
                {
                    if (EntityManager.HasComponent<RenderMesh>(child.Value))
                    {
                        renderersInPrefabs.Add(child.Value);
                    }
                }
                EntityManager.AddSharedComponentData(prefab, new DancerReferenceGroupMember { dancerReferenceEntity = entity });
                var linkedGroup                                                                                     =
                    new NativeArray<Entity>(EntityManager.GetBuffer<LinkedEntityGroup>(prefab).Reinterpret<Entity>().AsNativeArray(), Allocator.Temp);
                bool isOptimized = false;
                foreach (var e in linkedGroup)
                {
                    EntityManager.AddSharedComponentData(e, new DancerReferenceGroupMember { dancerReferenceEntity = entity });
                    if (EntityManager.HasComponent<OptimizedSkeletonHierarchyBlobReference>(e))
                    {
                        isOptimized = true;
                        var cache   = EntityManager.AddBuffer<QuaternionCacheElement>(e);
                        cache.ResizeUninitialized(hybrid.bonesPerReference);
                        for (int i = 0; i < hybrid.bonesPerReference; i++)
                            cache[i] = default;
                        EntityManager.AddComponent<DancerDots>(e);
                    }
                }
                if (!isOptimized)
                {
                    EntityManager.AddComponent<DancerDots>(     linkedGroup);
                    EntityManager.AddComponent<QuaternionCache>(linkedGroup);
                    EntityManager.AddComponent<QuaternionCache>(prefab);
                }

                Dependency = new InstantiateJob
                {
                    icb           = icbp,
                    patchedPrefab = prefab,
                    rng           = m_rng.Shuffle(),
                    spawner       = spawner
                }.ScheduleParallel(spawner.columns, 1, Dependency);
                dcb.Add(prefab);
            }).WithStoreEntityQueryInField(ref m_spawnerQuery).WithStructuralChanges().Run();
            CompleteDependency();

            EntityManager.AddComponent<URPMaterialPropertyBaseColor>(renderersInPrefabs);
            renderersInPrefabs.Dispose();
            icb.Playback(EntityManager);
            icb.Dispose();
            dcb.Playback(EntityManager);
            dcb.Dispose();

            var rng = m_rng.Shuffle();

            var dancerCdfe = GetComponentDataFromEntity<DancerDots>(true);
            Entities.WithNone<DancerInitializedTag>().WithNativeDisableContainerSafetyRestriction(dancerCdfe).ForEach((ref DancerDots dd, in Parent parent) =>
            {
                var p = parent.Value;
                while (HasComponent<Parent>(p))
                    p = GetComponent<Parent>(p).Value;
                dd    = dancerCdfe[p];
            }).WithStoreEntityQueryInField(ref m_dancerChildQuery).ScheduleParallel();

            Entities.WithNone<ColorInitializedTag>().ForEach((int entityInQueryIndex, ref URPMaterialPropertyBaseColor color) =>
            {
                var random  = rng.GetSequence(entityInQueryIndex);
                var hsv     = random.NextFloat3(new float3(0f, 0.6f, 0.8f), new float3(1f, 0.8f, 1f));
                var rgb     = Color.HSVToRGB(hsv.x, hsv.y, hsv.z);
                color.Value = new float4(rgb.r, rgb.g, rgb.b, 1f);
            }).WithStoreEntityQueryInField(ref m_dancerColorQuery).ScheduleParallel();
            CompleteDependency();
            EntityManager.AddComponent<ColorInitializedTag>(m_dancerColorQuery);
            EntityManager.RemoveComponent<SpawnerDots>(m_spawnerQuery);
            EntityManager.AddComponent<DancerInitializedTag>(m_dancerChildQuery);
        }

        List<Transform> m_transformsCache = new List<Transform>();

        unsafe void AddTransformsInSkeletonOrder(TransformAccessArray transformArray, GameObject go, SkeletonBindingPathsBlobReference blobRef)
        {
            ref var            paths  = ref blobRef.blob.Value.pathsInReversedNotation;
            FixedString64Bytes goName = default;

            // Assume the roots match even though their names are different.
            transformArray.Add(go.transform);
            go.GetComponentsInChildren(m_transformsCache);

            for (int i = 1; i < paths.Length; i++)
            {
                bool found = false;
                foreach (Transform t in m_transformsCache)
                {
                    goName = t.gameObject.name;
                    if (UnsafeUtility.MemCmp(goName.GetUnsafePtr(), paths[i].GetUnsafePtr(), math.min(goName.Length, 12)) == 0)
                    {
                        transformArray.Add(t);
                        found = true;
                        break;
                    }
                }
                if (found == false)
                {
                    FixedString4096Bytes missingPath = default;
                    missingPath.Append((byte*)paths[i].GetUnsafePtr(), paths[i].Length);
                    UnityEngine.Debug.LogWarning($"Failed to find mapping for GO dancer reference. Name: {missingPath}");
                }
            }
        }

        void InitializeReference(GameObject go)
        {
            var dancer = go.GetComponent<Puppet.Dancer>();

            dancer.footDistance *= Random.Range(0.8f, 2.0f);
            //dancer.stepFrequency *= Random.Range(0.4f, 1.6f);
            dancer.stepHeight *= Random.Range(0.75f, 1.25f);
            dancer.stepAngle  *= Random.Range(0.75f, 1.25f);

            dancer.hipHeight        *= Random.Range(0.75f, 1.25f);
            dancer.hipPositionNoise *= Random.Range(0.75f, 1.25f);
            dancer.hipRotationNoise *= Random.Range(0.75f, 1.25f);

            dancer.spineBend           = Random.Range(4.0f, -16.0f);
            dancer.spineRotationNoise *= Random.Range(0.75f, 1.25f);

            dancer.handPositionNoise *= Random.Range(0.5f, 2.0f);
            dancer.handPosition      += Random.insideUnitSphere * 0.25f;

            dancer.headMove *= Random.Range(0.2f, 2.8f);
            //dancer.noiseFrequency *= Random.Range(0.4f, 1.8f);
            dancer.randomSeed = (uint)Random.Range(0, 0xffffff);
        }

        [BurstCompile]
        struct InstantiateJob : IJobFor
        {
            public InstantiateCommandBuffer<Translation, Rotation, DancerDots>.ParallelWriter icb;
            public SpawnerDots                                                                spawner;
            public Entity                                                                     patchedPrefab;
            public Rng                                                                        rng;

            public void Execute(int c)
            {
                var random = rng.GetSequence(c);
                var x      = spawner.interval * (c - spawner.columns * 0.5f + 0.5f);
                for (int r = 0; r < spawner.rows; r++)
                {
                    var y = spawner.interval * (r - spawner.rows * 0.5f + 0.5f);

                    var trans = new Translation { Value = new float3(x, 0f, y) };
                    var rot                             = new Rotation { Value = quaternion.AxisAngle(math.up(), random.NextFloat(0f, 2f * math.PI)) };
                    //rot                                 = new Rotation { Value = new float4(0f, 1f, 0f, 0f) };
                    var dancer = new DancerDots
                    {
                        referenceDancerIndexA = random.NextInt(0, spawner.referencesToSpawn),
                        referenceDancerIndexB = random.NextInt(0, spawner.referencesToSpawn),
                        weightA               = random.NextFloat(0.0f, 1f)
                    };
                    icb.Add(patchedPrefab, trans, rot, dancer, c);
                }
            }
        }
    }
}

