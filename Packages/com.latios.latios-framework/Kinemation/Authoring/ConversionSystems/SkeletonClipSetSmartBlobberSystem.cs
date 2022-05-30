using System.Collections.Generic;
using Latios.Authoring;
using Latios.Authoring.Systems;
using Latios.Unsafe;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Jobs;

namespace Latios.Kinemation.Authoring
{
    public struct SkeletonClipConfig
    {
        public AnimationClip                   clip;
        public SkeletonClipCompressionSettings settings;
    }

    public struct SkeletonClipSetBakeData
    {
        public Animator             animator;
        public SkeletonClipConfig[] clips;
    }

    public struct SkeletonClipCompressionSettings
    {
        public short compressionLevel;
        public float maxDistanceError;
        public float sampledErrorDistanceFromBone;
        public float maxNegligibleTranslationDrift;
        public float maxNegligibleScaleDrift;

        public static readonly SkeletonClipCompressionSettings kDefaultSettings = new SkeletonClipCompressionSettings
        {
            compressionLevel              = 2,
            maxDistanceError              = 0.0001f,
            sampledErrorDistanceFromBone  = 0.03f,
            maxNegligibleScaleDrift       = 0.00001f,
            maxNegligibleTranslationDrift = 0.00001f
        };
    }

    public static class AudioClipBlobberAPIExtensions
    {
        public static SmartBlobberHandle<SkeletonClipSetBlob> CreateBlob(this GameObjectConversionSystem conversionSystem,
                                                                         GameObject gameObject,
                                                                         SkeletonClipSetBakeData bakeData)
        {
            return conversionSystem.World.GetExistingSystem<Systems.SkeletonClipSetSmartBlobberSystem>().AddToConvert(gameObject, bakeData);
        }

        public static SmartBlobberHandleUntyped CreateBlobUntyped(this GameObjectConversionSystem conversionSystem,
                                                                  GameObject gameObject,
                                                                  SkeletonClipSetBakeData bakeData)
        {
            return conversionSystem.World.GetExistingSystem<Systems.SkeletonClipSetSmartBlobberSystem>().AddToConvertUntyped(gameObject, bakeData);
        }
    }
}

namespace Latios.Kinemation.Authoring.Systems
{
    [ConverterVersion("Latios", 1)]
    [DisableAutoCreation]
    public class SkeletonClipSetSmartBlobberSystem : SmartBlobberConversionSystem<SkeletonClipSetBlob, SkeletonClipSetBakeData, SkeletonClipSetConverter>
    {
        Dictionary<Animator, SkeletonConversionContext> animatorToContextLookup = new Dictionary<Animator, SkeletonConversionContext>();

        protected override void GatherInputs()
        {
            animatorToContextLookup.Clear();
            Entities.ForEach((Animator animator, SkeletonConversionContext context) => animatorToContextLookup.Add(animator, context));
        }

        protected override bool Filter(in SkeletonClipSetBakeData input, GameObject gameObject, out SkeletonClipSetConverter converter)
        {
            converter = default;

            if (input.clips == null || input.animator == null)
                return false;
            foreach (var clip in input.clips)
            {
                if (clip.clip == null)
                {
                    Debug.LogError($"Kinemation failed to convert clip set on animator {input.animator.gameObject.name} due to one fo the clips being null.");
                    return false;
                }
                DeclareAssetDependency(gameObject, clip.clip);
            }

            if (!animatorToContextLookup.TryGetValue(input.animator, out SkeletonConversionContext context))
            {
                Debug.LogError($"Kinemation failed to convert clip set on animator {input.animator.gameObject.name} because the passed-in animator is not marked for conversion.");
                return false;
            }

            // Todo: Need to fix this for squash and stretch on optimized hierarchies.
            if (context.authoring != null && context.authoring.bindingMode != BindingMode.ConversionTime)
            {
                Debug.LogError(
                    $"Conversion of animation clips is not currently supported for a BindingMode other than ConversionTime. If you need this feature, let me know! Failed to convert clip set on animator {input.animator.gameObject.name}");
                return false;
            }

            var allocator                    = World.UpdateAllocator.ToAllocator;
            converter.parents                = new UnsafeList<int>(context.skeleton.Length, allocator);
            converter.hasParentScaleInverses = new UnsafeList<bool>(context.skeleton.Length, allocator);
            converter.parents.Resize(context.skeleton.Length);
            converter.hasParentScaleInverses.Resize(context.skeleton.Length);
            for (int i = 0; i < context.skeleton.Length; i++)
            {
                converter.parents[i]                = context.skeleton[i].parentIndex;
                converter.hasParentScaleInverses[i] = context.skeleton[i].ignoreParentScale;
            }

            var shadowHierarchy      = BuildHierarchyFromShadow(context);
            converter.clipsToConvert = new UnsafeList<SkeletonClipSetConverter.SkeletonClipConversionData>(input.clips.Length, allocator);
            converter.clipsToConvert.Resize(input.clips.Length);
            int targetClip = 0;
            foreach (var clip in input.clips)
            {
                converter.clipsToConvert[targetClip] = new SkeletonClipSetConverter.SkeletonClipConversionData
                {
                    clipName               = clip.clip.name,
                    sampleRate             = clip.clip.frameRate,
                    settings               = clip.settings,
                    sampledLocalTransforms = SampleClip(shadowHierarchy, clip.clip, allocator)
                };
            }
            shadowHierarchy.Dispose();

            return true;
        }

        Queue<Transform> m_breadthQueeue = new Queue<Transform>();

        // Todo: Exposed and exported bones should always have a mapping in the skeleton definition
        // and consequently the shadow skeleton can track them. Optimized bones can't have their names
        // altered between import and deoptimization, so the optimized bone subtree underneath an exposed
        // bone can use path matching. The only failure case is if an optimized bone's path gets altered.
        // In that case, it might be best to log a warning and assign the skeleton definition's transform
        // to all samples for that bone.
        TransformAccessArray BuildHierarchyFromShadow(SkeletonConversionContext context)
        {
            var boneCount = context.skeleton.Length;
            var result    = new TransformAccessArray(boneCount);
            m_breadthQueeue.Clear();

            var root = context.shadowHierarchy.transform;
            m_breadthQueeue.Enqueue(root);
            while (m_breadthQueeue.Count > 0)
            {
                var bone = m_breadthQueeue.Dequeue();
                result.Add(bone);
                for (int i = 0; i < bone.childCount; i++)
                {
                    m_breadthQueeue.Enqueue(bone.GetChild(i));
                }
            }

            return result;
        }

        unsafe UnsafeList<BoneTransform> SampleClip(TransformAccessArray shadowHierarchy, AnimationClip clip, Allocator allocator)
        {
            int requiredSamples    = Mathf.CeilToInt(clip.frameRate * clip.length);
            int requiredTransforms = requiredSamples * shadowHierarchy.length;
            var result             = new UnsafeList<BoneTransform>(requiredTransforms, allocator);
            result.Resize(requiredTransforms);
            var boneTransforms = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<BoneTransform>(result.Ptr, requiredTransforms, Allocator.None);

            var oldWrapMode = clip.wrapMode;
            clip.wrapMode   = WrapMode.Clamp;
            var   root      = shadowHierarchy[0].gameObject;
            float timestep  = math.rcp(clip.frameRate);
            var   job       = new CaptureSampledBonesJob
            {
                boneTransforms = boneTransforms,
                samplesPerBone = requiredSamples,
                currentSample  = 0
            };

            for (int i = 0; i < requiredSamples; i++)
            {
                clip.SampleAnimation(root, timestep * i);
                job.currentSample = i;
                job.RunReadOnly(shadowHierarchy);
            }

            clip.wrapMode = oldWrapMode;

            return result;
        }

        [BurstCompile]
        struct CaptureSampledBonesJob : IJobParallelForTransform
        {
            public NativeArray<BoneTransform> boneTransforms;
            public int                        samplesPerBone;
            public int                        currentSample;

            public void Execute(int index, TransformAccess transform)
            {
                int target             = index * samplesPerBone + currentSample;
                boneTransforms[target] = new BoneTransform(transform.localRotation, transform.localPosition, transform.localScale);
            }
        }
    }

    public struct SkeletonClipSetConverter : ISmartBlobberSimpleBuilder<SkeletonClipSetBlob>
    {
        internal struct SkeletonClipConversionData
        {
            public UnsafeList<BoneTransform>       sampledLocalTransforms;
            public FixedString128Bytes             clipName;
            public SkeletonClipCompressionSettings settings;
            public float                           sampleRate;
        }

        internal UnsafeList<SkeletonClipConversionData> clipsToConvert;
        internal UnsafeList<int>                        parents;
        public UnsafeList<bool>                         hasParentScaleInverses;

        public unsafe BlobAssetReference<SkeletonClipSetBlob> BuildBlob()
        {
            var     builder = new BlobBuilder(Allocator.Temp);
            ref var root    = ref builder.ConstructRoot<SkeletonClipSetBlob>();
            root.boneCount  = (short)parents.Length;
            var blobClips   = builder.Allocate(ref root.clips, clipsToConvert.Length);

            // Step 1: Patch parent hierarchy for ACL
            var parentIndices = new NativeArray<short>(parents.Length, Allocator.Temp);
            for (short i = 0; i < parents.Length; i++)
            {
                short index = (short)parents[i];
                if (index < 0)
                    index = i;
                if (hasParentScaleInverses[i])
                    index        *= -1;
                parentIndices[i]  = index;
            }

            int targetClip = 0;
            foreach (var clip in clipsToConvert)
            {
                // Step 2: Convert settings
                var aclSettings = new AclUnity.Compression.SkeletonCompressionSettings
                {
                    compressionLevel              = clip.settings.compressionLevel,
                    maxDistanceError              = clip.settings.maxDistanceError,
                    maxNegligibleScaleDrift       = clip.settings.maxNegligibleScaleDrift,
                    maxNegligibleTranslationDrift = clip.settings.maxNegligibleTranslationDrift,
                    sampledErrorDistanceFromBone  = clip.settings.sampledErrorDistanceFromBone
                };

                // Step 3: Encode bone samples into QVV array
                var qvvArray = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<AclUnity.Qvv>(clip.sampledLocalTransforms.Ptr,
                                                                                                       clip.sampledLocalTransforms.Length,
                                                                                                       Allocator.None);

                // Step 4: Compress
                var compressedClip = AclUnity.Compression.CompressSkeletonClip(parentIndices, qvvArray, clip.sampleRate, aclSettings);

                // Step 5: Build blob clip
                blobClips[targetClip]          = default;
                blobClips[targetClip].name     = clip.clipName;
                blobClips[targetClip].duration = clip.sampleRate * (qvvArray.Length / parents.Length);
                var compressedData             = builder.Allocate(ref blobClips[targetClip].compressedClipDataAligned16, compressedClip.compressedDataToCopyFrom.Length, 16);
                UnsafeUtility.MemCpy(compressedData.GetUnsafePtr(), compressedClip.compressedDataToCopyFrom.GetUnsafeReadOnlyPtr(), compressedClip.compressedDataToCopyFrom.Length);

                // Step 6: Dispose blob
                compressedClip.Dispose();

                targetClip++;
            }

            return builder.CreateBlobAssetReference<SkeletonClipSetBlob>(Allocator.Persistent);
        }
    }
}

