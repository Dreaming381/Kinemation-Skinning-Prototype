using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Dragons
{
    [DisallowMultipleComponent]
    public class DancerDotsAuthoring : MonoBehaviour, IConvertGameObjectToEntity
    {
        public Transform leftFoot;
        public Transform rightFoot;
        public float     offset;

        public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            if (leftFoot != null && rightFoot != null)
            {
                //var smrs = GetComponentsInChildren<SkinnedMeshRenderer>();

                //foreach (var smr in smrs)
                //{
                //    var e = conversionSystem.GetPrimaryEntity(smr);
                dstManager.AddComponentData(entity, new DancerFootCorrector
                {
                    leftFoot  = conversionSystem.GetPrimaryEntity(leftFoot),
                    rightFoot = conversionSystem.GetPrimaryEntity(rightFoot),
                    offset    = offset
                });
                dstManager.AddComponent<DancerFootCache>(entity);
                //}
            }
        }
    }
}

