using System;
using Latios;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.Jobs;

namespace Dragons
{
    public struct DancerDots : IComponentData
    {
        public int   referenceDancerIndexA;
        public int   referenceDancerIndexB;
        public float weightA;
    }

    public struct QuaternionCache : IComponentData
    {
        public quaternion lastQuaternionA;
        public quaternion lastQuaternionB;
        public float      maxRadsA;
        public float      maxRadsB;
        public int        warmup;
    }

    public struct QuaternionCacheElement : IBufferElementData
    {
        public quaternion lastQuaternionA;
        public quaternion lastQuaternionB;
        public quaternion lastRotation;
        public float      maxRadsA;
        public float      maxRadsB;
        public int        warmup;
    }

    public struct SpawnerDots : IComponentData
    {
        public Entity dancerPrefab;
        public int    referencesToSpawn;
        public int    rows;
        public int    columns;
        public float  interval;
    }

    public struct DancerFootCorrector : IComponentData
    {
        public EntityWith<LocalToWorld> leftFoot;
        public EntityWith<LocalToWorld> rightFoot;
        public float                    offset;
    }

    public struct DancerFootCache : IComponentData
    {
        public float3 lastStepPosition;
        public bool   lastStepWasLeft;
    }

    public struct DancerReferenceGroupMember : ISharedComponentData
    {
        public Entity dancerReferenceEntity;
    }

    public struct DancerReferenceGroupTransformsTag : IComponentData { }

    public struct DancerReferenceGroupTransforms : ICollectionComponent
    {
        public TransformAccessArray transforms;

        public Type AssociatedComponentType => typeof(DancerReferenceGroupTransformsTag);

        public JobHandle Dispose(JobHandle inputDeps)
        {
            inputDeps.Complete();
            transforms.Dispose();
            return default;
        }
    }
}

