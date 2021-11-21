using Latios;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Dragons
{
    public class PokeDancerRootsSystem : SubSystem
    {
        protected override void OnUpdate()
        {
            float time = (float)Time.ElapsedTime;
            float dt   = Time.DeltaTime;
            Entities.WithNone<Parent>().WithAll<Child>().ForEach((ref Translation translation) =>
            {
                translation.Value.x += math.sin(time) * dt;
            }).ScheduleParallel();
        }
    }
}

