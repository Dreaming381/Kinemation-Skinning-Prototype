﻿using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Latios
{
    public unsafe struct UnsafeParallelBlockList : INativeDisposable
    {
        public readonly int  m_elementSize;
        private readonly int m_blockSize;
        private readonly int m_elementsPerBlock;
        private Allocator    m_allocator;

        [NativeDisableUnsafePtrRestriction] private PerThreadBlockList* m_perThreadBlockLists;

        public UnsafeParallelBlockList(int elementSize, int elementsPerBlock, Allocator allocator)
        {
            m_elementSize      = elementSize;
            m_elementsPerBlock = elementsPerBlock;
            m_blockSize        = elementSize * elementsPerBlock;
            m_allocator        = allocator;

            m_perThreadBlockLists = (PerThreadBlockList*)UnsafeUtility.Malloc(64 * JobsUtility.MaxJobThreadCount, 64, allocator);
            for (int i = 0; i < JobsUtility.MaxJobThreadCount; i++)
            {
                m_perThreadBlockLists[i] = default;
                m_perThreadBlockLists[i].nextWriteAddress++;
            }
        }

        //The thread index is passed in because otherwise job reflection can't inject it through a pointer.
        public void Write<T>(T value, int threadIndex) where T : unmanaged
        {
            var blockList = m_perThreadBlockLists + threadIndex;
            if (blockList->nextWriteAddress > blockList->lastByteAddressInBlock)
            {
                if (blockList->elementCount == 0)
                {
                    blockList->blocks = new UnsafeList(m_allocator);
                    blockList->blocks.SetCapacity<BlockPtr>(10);
                }
                BlockPtr newBlockPtr = new BlockPtr
                {
                    ptr = (byte*)UnsafeUtility.Malloc(m_blockSize, UnsafeUtility.AlignOf<T>(), m_allocator)
                };
                blockList->nextWriteAddress       = newBlockPtr.ptr;
                blockList->lastByteAddressInBlock = newBlockPtr.ptr + m_blockSize - 1;
                blockList->blocks.Add(newBlockPtr);
            }

            UnsafeUtility.CopyStructureToPtr(ref value, blockList->nextWriteAddress);
            blockList->nextWriteAddress += m_elementSize;
            blockList->elementCount++;
        }

        public void* Allocate(int threadIndex)
        {
            var blockList = m_perThreadBlockLists + threadIndex;
            if (blockList->nextWriteAddress > blockList->lastByteAddressInBlock)
            {
                if (blockList->elementCount == 0)
                {
                    blockList->blocks = new UnsafeList(m_allocator);
                    blockList->blocks.SetCapacity<BlockPtr>(10);
                }
                BlockPtr newBlockPtr = new BlockPtr
                {
                    ptr = (byte*)UnsafeUtility.Malloc(m_blockSize, UnsafeUtility.AlignOf<byte>(), m_allocator)
                };
                blockList->nextWriteAddress       = newBlockPtr.ptr;
                blockList->lastByteAddressInBlock = newBlockPtr.ptr + m_blockSize - 1;
                blockList->blocks.Add(newBlockPtr);
            }

            var result                   = blockList->nextWriteAddress;
            blockList->nextWriteAddress += m_elementSize;
            blockList->elementCount++;
            return result;
        }

        public int Count()
        {
            int result = 0;
            for (int i = 0; i < JobsUtility.MaxJobThreadCount; i++)
            {
                result += m_perThreadBlockLists[i].elementCount;
            }
            return result;
        }

        public struct ElementPtr
        {
            public byte* ptr;
        }

        public void GetElementPtrs(NativeArray<ElementPtr> ptrs)
        {
            int dst = 0;

            for (int threadBlockId = 0; threadBlockId < JobsUtility.MaxJobThreadCount; threadBlockId++)
            {
                var blockList = m_perThreadBlockLists + threadBlockId;
                if (blockList->elementCount > 0)
                {
                    int src = 0;
                    for (int blockId = 0; blockId < blockList->blocks.Length - 1; blockId++)
                    {
                        var address = ((BlockPtr*)blockList->blocks.Ptr)[blockId].ptr;
                        for (int i = 0; i < m_elementsPerBlock; i++)
                        {
                            ptrs[dst] = new ElementPtr { ptr  = address };
                            address                          += m_elementSize;
                            src++;
                            dst++;
                        }
                    }
                    {
                        var address = ((BlockPtr*)blockList->blocks.Ptr)[blockList->blocks.Length - 1].ptr;
                        for (int i = src; i < blockList->elementCount; i++)
                        {
                            ptrs[dst] = new ElementPtr { ptr  = address };
                            address                          += m_elementSize;
                            dst++;
                        }
                    }
                }
            }
        }

        public void GetElementValues<T>(NativeArray<T> values) where T : struct
        {
            int dst = 0;

            for (int threadBlockId = 0; threadBlockId < JobsUtility.MaxJobThreadCount; threadBlockId++)
            {
                var blockList = m_perThreadBlockLists + threadBlockId;
                if (blockList->elementCount > 0)
                {
                    int src = 0;
                    CheckBlockCountMatchesCount(blockList->elementCount, blockList->blocks.Length);
                    for (int blockId = 0; blockId < blockList->blocks.Length - 1; blockId++)
                    {
                        var address = ((BlockPtr*)blockList->blocks.Ptr)[blockId].ptr;
                        for (int i = 0; i < m_elementsPerBlock; i++)
                        {
                            UnsafeUtility.CopyPtrToStructure(address, out T temp);
                            values[dst]  = temp;
                            address     += m_elementSize;
                            src++;
                            dst++;
                        }
                    }
                    {
                        var address = ((BlockPtr*)blockList->blocks.Ptr)[blockList->blocks.Length - 1].ptr;
                        for (int i = src; i < blockList->elementCount; i++)
                        {
                            UnsafeUtility.CopyPtrToStructure(address, out T temp);
                            values[dst]  = temp;
                            address     += m_elementSize;
                            dst++;
                        }
                    }
                }
            }
        }

        //This catches race conditions if I accidentally pass in 0 for thread index in the parallel writer because copy and paste.
        [BurstDiscard]
        void CheckBlockCountMatchesCount(int count, int blockCount)
        {
            int expectedBlocks = count / m_elementsPerBlock;
            if (count % m_elementsPerBlock > 0)
                expectedBlocks++;
            if (blockCount != expectedBlocks)
                throw new System.InvalidOperationException($"Block count: {blockCount} does not match element count: {count}");
        }

        [BurstCompile]
        struct DisposeJob : IJob
        {
            public UnsafeParallelBlockList upbl;

            public void Execute()
            {
                upbl.Dispose();
            }
        }

        public JobHandle Dispose(JobHandle inputDeps)
        {
            var jh = new DisposeJob { upbl = this }.Schedule(inputDeps);
            m_perThreadBlockLists          = null;
            return jh;
        }

        public void Dispose()
        {
            for (int i = 0; i < JobsUtility.MaxJobThreadCount; i++)
            {
                if (m_perThreadBlockLists[i].elementCount > 0)
                {
                    for (int j = 0; j < m_perThreadBlockLists[i].blocks.Length; j++)
                    {
                        BlockPtr* blockPtrArray = (BlockPtr*)m_perThreadBlockLists[i].blocks.Ptr;
                        UnsafeUtility.Free(blockPtrArray[j].ptr, m_allocator);
                    }
                    m_perThreadBlockLists[i].blocks.Dispose();
                }
            }
            UnsafeUtility.Free(m_perThreadBlockLists, m_allocator);
        }

        private struct BlockPtr
        {
            public byte* ptr;
        }

        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct PerThreadBlockList
        {
            public UnsafeList blocks;
            public byte*      nextWriteAddress;
            public byte*      lastByteAddressInBlock;
            public int        elementCount;
        }

        public Enumerator GetEnumerator(int nativeThreadIndex)
        {
            return new Enumerator(m_perThreadBlockLists + nativeThreadIndex, m_elementSize, m_elementsPerBlock);
        }

        public struct Enumerator
        {
            private PerThreadBlockList* m_perThreadBlockList;
            private byte*               m_readAddress;
            private byte*               m_lastByteAddressInBlock;
            private int                 m_blockIndex;
            private int                 m_elementSize;
            private int                 m_elementsPerBlock;

            internal Enumerator(void* perThreadBlockList, int elementSize, int elementsPerBlock)
            {
                m_perThreadBlockList = (PerThreadBlockList*)perThreadBlockList;
                m_readAddress        = null;
                m_readAddress++;
                m_lastByteAddressInBlock = null;
                m_blockIndex             = -1;
                m_elementSize            = elementSize;
                m_elementsPerBlock       = elementsPerBlock;
            }

            public bool MoveNext()
            {
                m_readAddress += m_elementSize;
                if (m_readAddress > m_lastByteAddressInBlock)
                {
                    m_blockIndex++;
                    if (m_blockIndex >= m_perThreadBlockList->blocks.Length)
                        return false;

                    int elementsInBlock      = math.min(m_elementsPerBlock, m_perThreadBlockList->elementCount - m_blockIndex * m_elementsPerBlock);
                    var blocks               = (BlockPtr*)m_perThreadBlockList->blocks.Ptr;
                    m_lastByteAddressInBlock = blocks[m_blockIndex].ptr + elementsInBlock * m_elementSize - 1;
                    m_readAddress            = blocks[m_blockIndex].ptr;
                }
                return true;
            }

            public T GetCurrent<T>() where T : struct
            {
                UnsafeUtility.CopyPtrToStructure(m_readAddress, out T t);
                return t;
            }
        }
    }

    // Schedule for 128 iterations
    [BurstCompile]
    struct ExampleReadJob : IJobFor
    {
        [NativeDisableUnsafePtrRestriction] public UnsafeParallelBlockList blockList;

        public void Execute(int index)
        {
            var enumerator = blockList.GetEnumerator(index);

            while (enumerator.MoveNext())
            {
                int number = enumerator.GetCurrent<int>();

                if (number == 381)
                    UnityEngine.Debug.Log("You found me!");
                else if (number == 380)
                    UnityEngine.Debug.Log("Where?");
            }
        }
    }
}

