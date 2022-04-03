using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Dragons
{
    [DisallowMultipleComponent]
    public class DancerReferenceGroupHybridAuthoring : MonoBehaviour
    {
        public GameObject dancerGoPrefab;

        [HideInInspector] public int bonesPerReference;
    }

    public class DancerReferenceGroupHybrid : IComponentData
    {
        public GameObject dancerGoPrefab;
        public int        bonesPerReference;
    }

    public class SpawnerDotsHybridConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((DancerReferenceGroupHybridAuthoring hybrid) =>
            {
                DstEntityManager.AddComponentObject(GetPrimaryEntity(hybrid), new DancerReferenceGroupHybrid
                {
                    dancerGoPrefab    = hybrid.dancerGoPrefab,
                    bonesPerReference = hybrid.bonesPerReference
                });
            });
        }
    }
}

