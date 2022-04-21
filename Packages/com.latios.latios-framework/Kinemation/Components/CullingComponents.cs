using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace Latios.Kinemation
{
    // Usage: Read or Write
    // This is a chunk component and also a WriteGroup target.
    // To iterate these, you must include ChunkHeader in your query.
    // Every mesh entity has one of these as a chunk component,
    // with a max of 128 mesh instances per chunk (all of the same RenderMesh).
    // A true value for a bit will cause the mesh at that index to be rendered
    // by the current camera. This must happen inside the KinemationCullingSuperSystem.
    public struct ChunkPerCameraCullingMask : IComponentData
    {
        public BitField64 lower;
        public BitField64 upper;
    }

    // Usage: Read Only (No exceptions!)
    // This is marked WriteGroup to ensure normal unskinned meshes can use write group filtering.
    // Include this if you choose to use WriteGroup filtering yourself.
    [WriteGroup(typeof(ChunkPerCameraCullingMask))]
    public struct ChunkPerFrameCullingMask : IComponentData
    {
        internal BitField64 lower;
        internal BitField64 upper;
    }

    // Usage: Read Only (No exceptions!)
    // This is public such that you can include it in queries when using WriteGroup filtering yourself.
    [WriteGroup(typeof(ChunkPerCameraCullingMask))]
    public struct ChunkComputeDeformMemoryMetadata : IComponentData
    {
        internal int vertexStartPrefixSum;
        internal int verticesPerMesh;
        internal int entitiesInChunk;
    }

    // Usage: Read Only (No exceptions!)
    // This lives on the worldBlackboardEntity and is set on the main thread for each camera.
    // For SIMD culling, use Kinemation.CullingUtilities and Unity.Rendering.FrustumPlanes.
    public struct CullingPlane : IBufferElementData
    {
        public UnityEngine.Plane plane;
    }

    // Usage: Read Only (No exceptions!)
    // This lives on the worldBlackboardEntity and is set on the main thread for each camera.
    public struct CullingContext : IComponentData
    {
        public LODParameters lodParameters;
        public float4x4      cullingMatrix;
        public float         nearPlane;
        public int           cullIndexThisFrame;
    }
}

