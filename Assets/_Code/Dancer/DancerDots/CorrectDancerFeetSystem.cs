using Latios;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Dragons
{
    public class CorrectDancerFeetSystem : SubSystem
    {
        protected override void OnUpdate()
        {
            var rotCdfe = GetComponentDataFromEntity<Rotation>();

            Entities.ForEach((ref Translation trans, ref DancerFootCache cache, in DancerFootCorrector dfc) =>
            {
                var hl = GetComponent<LocalToWorld>(dfc.leftFoot).Position;
                var hr = GetComponent<LocalToWorld>(dfc.rightFoot).Position;

                float hmin     = math.min(hl.y, hr.y) - dfc.offset;
                trans.Value.y -= hmin;

                if ((hl.y < hr.y) && cache.lastStepWasLeft)
                {
                    // correct left foot
                    var rot                = rotCdfe[dfc.leftFoot];
                    rot.Value              = quaternion.LookRotationSafe(math.forward(rot.Value), math.up());
                    rotCdfe[dfc.leftFoot]  = rot;
                    trans.Value.xz        -= (hl - cache.lastStepPosition).xz;
                    //cache.lastStepPosition = hl;
                }
                else if ((hl.y >= hr.y) && !cache.lastStepWasLeft)
                {
                    // correct right foot
                    var rot                 = rotCdfe[dfc.rightFoot];
                    rot.Value               = quaternion.LookRotationSafe(math.forward(rot.Value), math.up());
                    rotCdfe[dfc.rightFoot]  = rot;
                    trans.Value.xz         -= (hr - cache.lastStepPosition).xz;
                    //cache.lastStepPosition  = hr;
                }
                else if (hl.y < hr.y)
                {
                    cache.lastStepPosition = hl;
                    cache.lastStepWasLeft  = true;
                }
                else
                {
                    cache.lastStepPosition = hr;
                    cache.lastStepWasLeft  = false;
                }
            }).WithNativeDisableParallelForRestriction(rotCdfe).ScheduleParallel();
        }
    }
}

