using Unity.Burst;
using Unity.Collections;
using Unity.Deformations;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;

namespace Latios.Kinemation.Systems
{
    public class HybridSkinMatrixSystem : SubSystem
    {
        public override bool ShouldUpdateSystem()
        {
            return HybridSkinningToggle.EnableHybrid;
        }

        protected override void OnUpdate()
        {
            var brBfe         = GetBufferFromEntity<BoneReference>(true);
            var boneToRootBfe = GetBufferFromEntity<OptimizedBoneToRoot>(false);

            Entities.ForEach((Entity entity, ref DynamicBuffer<SkinMatrix> skinMatrices, in BindSkeletonRoot root) =>
            {
                if (brBfe.HasComponent(root.root))
                {
                    var bones       = brBfe[root.root];
                    var rootToWorld = GetComponent<LocalToWorld>(root.root);
                    var worldToRoot = math.inverse(rootToWorld.Value);

                    for (int i = 0; i < bones.Length; i++)
                    {
                        var bindPose    = GetComponent<BoneBindPose>(bones[i].bone);
                        var boneToWorld = GetComponent<LocalToWorld>(bones[i].bone).Value;
                        var boneToRoot  = math.mul(worldToRoot, boneToWorld);
                        var skinMat     = math.mul(boneToRoot, bindPose.bindPose);
                        skinMatrices[i] = new SkinMatrix { Value = new float3x4(skinMat.c0.xyz, skinMat.c1.xyz, skinMat.c2.xyz, skinMat.c3.xyz) };
                    }
                }
                else if (boneToRootBfe.HasComponent(root.root))
                {
                    var cache = boneToRootBfe[root.root].Reinterpret<float4x4>();

                    if (skinMatrices.Length < cache.Length)
                        skinMatrices.ResizeUninitialized(cache.Length);

                    ref var blob = ref GetComponent<OptimizedBindSkeletonBlobReference>(root.root).blob.Value;
                    for (int i = 0; i < cache.Length; i++)
                    {
                        var skinMat     = math.mul(cache[i], blob.bindPoses[i]);
                        skinMatrices[i] = new SkinMatrix { Value = new float3x4(skinMat.c0.xyz, skinMat.c1.xyz, skinMat.c2.xyz, skinMat.c3.xyz) };
                    }
                }
            }).WithReadOnly(brBfe).WithNativeDisableParallelForRestriction(boneToRootBfe).ScheduleParallel();
        }
    }
}

