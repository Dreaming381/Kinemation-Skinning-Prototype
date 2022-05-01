using Latios;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Dragons
{
    public partial class PokeDancerRootsSystem : SubSystem
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

    public partial class TestConvexSystem : SubSystem
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((in Translation t, in Rotation r, in Latios.Psyshock.Collider c) =>
            {
                Latios.Psyshock.PhysicsDebug.DrawCollider(c, new RigidTransform(r.Value, t.Value), UnityEngine.Color.red);
            }).Schedule();
        }
    }

    public partial class ChangeColorsSystem : SubSystem
    {
        protected override void OnUpdate()
        {
            float time = math.frac((float)Time.ElapsedTime);

            Entities.ForEach((ref Unity.Rendering.URPMaterialPropertyBaseColor color) =>
            {
                color.Value.x = time;
            }).ScheduleParallel();
        }
    }
}

