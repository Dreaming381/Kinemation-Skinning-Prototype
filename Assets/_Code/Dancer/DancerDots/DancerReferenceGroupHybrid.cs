using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Dragons
{
    [DisallowMultipleComponent]
    public class DancerReferenceGroupHybrid : MonoBehaviour
    {
        public GameObject dancerGoPrefab;

        [HideInInspector] public int bonesPerReference;
    }

    public class SpawnerDotsHybridConversionSystem : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((DancerReferenceGroupHybrid hybrid) =>
            {
                AddHybridComponent(hybrid);
            });
        }
    }
}

