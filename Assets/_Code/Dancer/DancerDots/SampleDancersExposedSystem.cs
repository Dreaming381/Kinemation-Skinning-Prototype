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
    public partial class SampleDancersExposedSystem : SubSystem
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
                Entities.ForEach((ref Translation trans, ref Rotation rot, ref QuaternionCache cache, in DancerDots dd, in BoneIndex boneIndex) =>
                {
                    int ia = dd.referenceDancerIndexA * boneCount + boneIndex.index;
                    int ib = dd.referenceDancerIndexB * boneCount + boneIndex.index;
                    var ta = translations[ia];
                    var tb = translations[ib];
                    var ra = rotations[ia];
                    var rb = rotations[ib];

                    trans.Value = math.lerp(tb, ta, dd.weightA);

                    if (cache.warmup < 2)
                    {
                        rot.Value = math.nlerp(rb, ra, dd.weightA);
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
                        var   targetDelta  = math.mul(target, math.inverse(rot.Value));
                        float targetAngle  = math.acos(math.forward(targetDelta).z);
                        rot.Value          = math.slerp(rot.Value, target, math.saturate(allowedAngle / targetAngle));
                    }
                    cache.lastQuaternionA = ra;
                    cache.lastQuaternionB = rb;
                }).WithReadOnly(translations).WithReadOnly(rotations).WithName("BlendBone").ScheduleParallel();
            }
            else
            {
                Entities.ForEach((ref Translation trans, ref Rotation rot, ref QuaternionCache cache, in DancerDots dd, in BoneIndex boneIndex) =>
                {
                    int ia = dd.referenceDancerIndexA * boneCount + boneIndex.index;
                    int ib = dd.referenceDancerIndexB * boneCount + boneIndex.index;
                    var ta = translations[ia];
                    var tb = translations[ib];
                    var ra = rotations[ia];
                    var rb = rotations[ib];

                    trans.Value = ta;
                    rot.Value   = ra;
                }).WithReadOnly(translations).WithReadOnly(rotations).WithName("CopyBone").ScheduleParallel();
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

