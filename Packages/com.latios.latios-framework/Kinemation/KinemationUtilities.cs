using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;

namespace Latios.Kinemation
{
    public static class CullingUtilities
    {
        public static NativeArray<FrustumPlanes.PlanePacket4> BuildSOAPlanePackets(NativeArray<UnityEngine.Plane> cullingPlanes, ref WorldUnmanaged world)
        {
            int cullingPlaneCount = cullingPlanes.Length;
            int packetCount       = (cullingPlaneCount + 3) >> 2;
            var planes            = world.UpdateAllocator.AllocateNativeArray<FrustumPlanes.PlanePacket4>(packetCount);

            for (int i = 0; i < cullingPlaneCount; i++)
            {
                var p              = planes[i >> 2];
                p.Xs[i & 3]        = cullingPlanes[i].normal.x;
                p.Ys[i & 3]        = cullingPlanes[i].normal.y;
                p.Zs[i & 3]        = cullingPlanes[i].normal.z;
                p.Distances[i & 3] = cullingPlanes[i].distance;
                planes[i >> 2]     = p;
            }

            // Populate the remaining planes with values that are always "in"
            for (int i = cullingPlaneCount; i < 4 * packetCount; ++i)
            {
                var p       = planes[i >> 2];
                p.Xs[i & 3] = 1.0f;
                p.Ys[i & 3] = 0.0f;
                p.Zs[i & 3] = 0.0f;

                // This value was before hardcoded to 32786.0f.
                // It was causing the culling system to discard the rendering of entities having a X coordinate approximately less than -32786.
                // We could not find anything relying on this number, so the value has been increased to 1 billion
                p.Distances[i & 3] = 1e9f;

                planes[i >> 2] = p;
            }

            return planes;
        }
    }
}

