using Latios;
using Latios.Kinemation;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.Jobs;

namespace Dragons
{
    public partial class SampleDancersOptimizedSystem : SubSystem
    {
        protected override void OnUpdate()
        {
            var backup = Dependency;
            Dependency = default;

            Entities.ForEach((Entity entity, int entityInQueryIndex, DancerReferenceGroupHybrid group) =>
            {
                if (entityInQueryIndex == 0)
                    Dependency = backup;

                DoSampling(entity, group);
            }).WithoutBurst().Run();
        }

        void DoSampling(Entity entity, DancerReferenceGroupHybrid group)
        {
            var memberScd           = new DancerReferenceGroupMember { dancerReferenceEntity = entity };
            int boneCount                                                                    = group.bonesPerReference;
            var transformCollection                                                          = EntityManager.GetCollectionComponent<DancerReferenceGroupTransforms>(entity, true);
            int transformCount                                                               = transformCollection.transforms.length;

            var translations = new NativeArray<float3>(transformCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var rotations    = new NativeArray<quaternion>(transformCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            Dependency = new FetchTransformsJob { translations = translations, rotations = rotations }.ScheduleReadOnly(transformCollection.transforms, 1, Dependency);

            float dt = Time.DeltaTime;

            if (HybridSkinningToggle.EnableBlending)
            {
                Entities.ForEach((ref Translation trans, ref Rotation rot, ref DynamicBuffer<QuaternionCacheElement> cacheBuffer,
                                  ref DynamicBuffer<OptimizedBoneToRoot> boneToRoots,
                                  in DancerDots dd, in OptimizedSkeletonHierarchyBlobReference blobRef) =>
                {
                    for (int i = 0; i < boneToRoots.Length; i++)
                    {
                        int ia = dd.referenceDancerIndexA * boneCount + i;
                        int ib = dd.referenceDancerIndexB * boneCount + i;
                        var ta = translations[ia];
                        var tb = translations[ib];
                        var ra = rotations[ia];
                        var rb = rotations[ib];

                        var t = math.lerp(tb, ta, dd.weightA);

                        var cache = cacheBuffer[i];
                        var r     = cache.lastRotation;

                        if (cache.warmup < 1)
                        {
                            r = math.nlerp(rb, ra, dd.weightA);
                            cache.warmup++;
                        }
                        else
                        {
                            var diffA = math.mul(ra, math.inverse(cache.lastQuaternionA));
                            var diffB = math.mul(rb, math.inverse(cache.lastQuaternionB));

                            float angleA       = math.acos(math.forward(diffA).z);
                            float angleB       = math.acos(math.forward(diffB).z);
                            cache.maxRadsA     = math.max(cache.maxRadsA, angleA / dt);
                            cache.maxRadsB     = math.max(cache.maxRadsB, angleB / dt);
                            float allowedAngle = math.min(math.PI / 2, math.max(cache.maxRadsA, cache.maxRadsB)) * dt;
                            var   target       = math.slerp(rb, ra, dd.weightA);
                            var   targetDelta  = math.mul(target, math.inverse(r));
                            float targetAngle  = math.acos(math.forward(targetDelta).z);
                            r                  = math.slerp(r, target, math.saturate(allowedAngle / targetAngle));
                        }
                        cache.lastQuaternionA = ra;
                        cache.lastQuaternionB = rb;
                        cache.lastRotation    = r;

                        cacheBuffer[i] = cache;

                        var trs         = float4x4.TRS(t, r, 1f);
                        var parentIndex = blobRef.blob.Value.parentIndices[i];
                        if (parentIndex > 0)
                        {
                            trs = math.mul(boneToRoots[parentIndex].boneToRoot, trs);
                        }
                        boneToRoots[i] = new OptimizedBoneToRoot { boneToRoot = trs };

                        if (i == 0)
                        {
                            //trans.Value    = t;
                            //rot.Value      = r;
                            boneToRoots[i] = new OptimizedBoneToRoot { boneToRoot = float4x4.identity };
                        }
                    }
                }).WithReadOnly(translations).WithReadOnly(rotations).WithName("BlendPoses").ScheduleParallel();
            }
            else
            {
                Entities.ForEach((ref Translation trans, ref Rotation rot,
                                  ref DynamicBuffer<OptimizedBoneToRoot> boneToRoots,
                                  in DancerDots dd, in OptimizedSkeletonHierarchyBlobReference blobRef) =>
                {
                    for (int i = 0; i < boneToRoots.Length; i++)
                    {
                        int ia = dd.referenceDancerIndexA * boneCount + i;
                        int ib = dd.referenceDancerIndexB * boneCount + i;
                        var ta = translations[ia];
                        var tb = translations[ib];
                        var ra = rotations[ia];
                        var rb = rotations[ib];

                        var trs         = float4x4.TRS(ta, ra, 1f);
                        var parentIndex = blobRef.blob.Value.parentIndices[i];
                        if (parentIndex > 0)
                        {
                            trs = math.mul(boneToRoots[parentIndex].boneToRoot, trs);
                        }

                        boneToRoots[i] = new OptimizedBoneToRoot { boneToRoot = trs };

                        if (i == 0)
                        {
                            //trans.Value    = ta;
                            //rot.Value      = ra;
                            boneToRoots[i] = new OptimizedBoneToRoot { boneToRoot = float4x4.identity };
                        }
                    }
                }).WithReadOnly(translations).WithReadOnly(rotations).WithName("CopyPoses2").ScheduleParallel();
            }

            Dependency = translations.Dispose(Dependency);
            Dependency = rotations.Dispose(Dependency);
        }

        [BurstCompile]
        struct FetchTransformsJob : IJobParallelForTransform
        {
            public NativeArray<float3>     translations;
            public NativeArray<quaternion> rotations;

            public void Execute(int index, TransformAccess transform)
            {
                translations[index] = transform.localPosition;
                rotations[index]    = transform.localRotation;
            }
        }
    }
}

