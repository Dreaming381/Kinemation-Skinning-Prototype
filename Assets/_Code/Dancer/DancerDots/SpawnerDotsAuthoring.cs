using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Dragons
{
    [DisallowMultipleComponent]
    public class SpawnerDotsAuthoring : MonoBehaviour, IConvertGameObjectToEntity, IDeclareReferencedPrefabs
    {
        public GameObject dancerPrefab;
        public int        referencesToSpawn;
        public int        rows;
        public int        columns;
        public float      interval;

        public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new SpawnerDots
            {
                dancerPrefab      = conversionSystem.GetPrimaryEntity(dancerPrefab),
                referencesToSpawn = referencesToSpawn,
                rows              = rows,
                columns           = columns,
                interval          = interval
            });
        }

        public void DeclareReferencedPrefabs(List<GameObject> referencedPrefabs)
        {
            referencedPrefabs.Add(dancerPrefab);
        }
    }
}

