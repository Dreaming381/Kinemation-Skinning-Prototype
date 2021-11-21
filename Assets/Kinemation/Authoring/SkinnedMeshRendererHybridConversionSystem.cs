using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Latios.Kinemation.Authoring
{
    [UpdateAfter(typeof(Unity.Rendering.SkinnedMeshRendererConversion))]
    public class SkinnedMeshRendererHybridConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            if (!HybridSkinningToggle.EnableHybrid)
                return;

            Entities.ForEach((SkinnedMeshRenderer smr) =>
            {
                var entity = GetPrimaryEntity(smr);
                var root   = GetPrimaryEntity(smr.rootBone);
                DstEntityManager.AddComponentData(entity,
                                                  new BindSkeletonRoot { root = root });

                var bindPoses = smr.sharedMesh.bindposes;
                if (bindPoses.Length == smr.bones.Length)
                {
                    int i = 0;
                    foreach (var bone in smr.bones)
                    {
                        var e                                                            = GetPrimaryEntity(bone);
                        DstEntityManager.AddComponentData(e, new BoneBindPose { bindPose = bindPoses[i] });
                        DstEntityManager.AddComponentData(e, new BoneIndex { index       = i });
                        i++;
                    }
                    if (!DstEntityManager.HasComponent<BoneReference>(root))
                    {
                        var bones = DstEntityManager.AddBuffer<BoneReference>(root);
                        bones.ResizeUninitialized(smr.bones.Length);
                        for (i = 0; i < bones.Length; i++)
                        {
                            bones[i] = new BoneReference { bone = GetPrimaryEntity(smr.bones[i]) };
                        }
                    }
                }
                else
                {
                    Debug.Log("Converting optimized SkinnedMeshRenderer");

                    BlobBuilder blobBuilder = new BlobBuilder(Allocator.Temp);
                    ref var     blobRoot    = ref blobBuilder.ConstructRoot<OptimizedBindSkeletonBlob>();

                    foreach (var exp in smr.transform.root.GetComponentsInChildren<HideThis.DeoptimizedCloneTracker>())
                    {
                        Debug.Log($"smr root has transform: {exp.gameObject.name} with id: {exp.trackerId}");
                    }

                    List<HideThis.DeoptimizedCloneTracker> exposedtransforms = new List<HideThis.DeoptimizedCloneTracker>();
                    int                                    id                = 0;
                    foreach (var tf in smr.transform.root.GetComponentsInChildren<Transform>())
                    {
                        Debug.Log($"Adding id {id} to transform: {tf.gameObject}");
                        if (tf.gameObject.GetComponent<HideThis.DeoptimizedCloneTracker>() != null)
                        {
                            var dup = tf.gameObject.GetComponent<HideThis.DeoptimizedCloneTracker>();
                            Debug.Log($"Transform {tf.gameObject} already has id {dup.trackerId}");
                            continue;
                        }
                        var exp       = tf.gameObject.AddComponent<HideThis.DeoptimizedCloneTracker>();
                        exp.trackerId = id++;
                        exposedtransforms.Add(exp);
                    }

                    var clone = GameObject.Instantiate(smr.transform.root.gameObject);
                    //var cloneAnimator = clone.AddComponent<Animator>();

                    AnimatorUtility.DeoptimizeTransformHierarchy(clone);

                    var smrBones = clone.GetComponentInChildren<SkinnedMeshRenderer>().bones;

                    Debug.Log(
                        $"Poses: {bindPoses.Length}, bones in clone: {smrBones.Length}, originalTransforms: {exposedtransforms.Count}, cloneTransforms: {clone.GetComponentsInChildren<Transform>().Length}, has hierarchy: {/*cloneAnimator.hasTransformHierarchy*/ false}");

                    foreach (var exp in clone.GetComponentsInChildren<HideThis.DeoptimizedCloneTracker>())
                    {
                        Debug.Log($"Clone has transform: {exp.gameObject.name} with id: {exp.trackerId}");
                    }

                    var bindposesBlob = blobBuilder.Allocate(ref blobRoot.bindPoses, smrBones.Length);
                    var parentIndices = blobBuilder.Allocate(ref blobRoot.parentIndices, smrBones.Length);

                    for (short i = 0; i < smrBones.Length; i++)
                    {
                        bindposesBlob[i] = bindPoses[i];
                        parentIndices[i] = -1;
                        for (short j = 0; j < smrBones.Length; j++)
                        {
                            if (smrBones[j] == smrBones[i].parent)
                            {
                                if (j > i)
                                    Debug.Log("Parent has greater bone index than child.");
                                parentIndices[i] = j;
                                break;
                            }
                        }

                        if (smrBones[i].GetComponent<HideThis.DeoptimizedCloneTracker>() != null)
                        {
                            var magicId = smrBones[i].GetComponent<HideThis.DeoptimizedCloneTracker>().trackerId;
                            foreach (var originalExposed in exposedtransforms)
                            {
                                var exposedBoneEntity = GetPrimaryEntity(originalExposed);
                                if (exposedBoneEntity != root && originalExposed.trackerId == magicId)
                                {
                                    Debug.Log(
                                        $"Found exposed bone - clone bone name: {smrBones[i].gameObject.name}, magicId: {magicId}, originalExposed: {originalExposed.gameObject.name}");
                                    DstEntityManager.AddComponentData(exposedBoneEntity, new CopyLocalToWorldFromBone { boneIndex       = i });
                                    DstEntityManager.AddComponentData(exposedBoneEntity, new BoneOwningSkeletonReference { skeletonRoot = root });
                                }
                            }
                        }
                    }

                    foreach (var exp in smr.transform.root.GetComponentsInChildren<HideThis.DeoptimizedCloneTracker>())
                    {
                        Object.DestroyImmediate(exp, true);
                    }

                    var blobReference = blobBuilder.CreateBlobAssetReference<OptimizedBindSkeletonBlob>(Allocator.Persistent);
                    BlobAssetStore.AddUniqueBlobAsset(ref blobReference);
                    DstEntityManager.AddComponentData(root, new OptimizedBindSkeletonBlobReference { blob = blobReference });
                    var boneToRoots                                                                       = DstEntityManager.AddBuffer<OptimizedBoneToRoot>(
                        root).Reinterpret<float4x4>();
                    boneToRoots.ResizeUninitialized(smrBones.Length);

                    for (int i = 0; i < smrBones.Length; i++)
                    {
                        boneToRoots[i] = float4x4.TRS(smrBones[i].localPosition, smrBones[i].localRotation, smrBones[i].localScale);
                    }

                    GameObject.DestroyImmediate(clone);
                }
            });
        }
    }
}

