using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

[BurstCompile]
public struct HashPositionsJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<Vector3> Positions;

    // ¿Ã∏ß ∫Ø∞Êµ !
    public NativeParallelMultiHashMap<int, int>.ParallelWriter SpatialGrid;

    [ReadOnly] public float CellSize;

    public void Execute(int index)
    {
        float3 pos = Positions[index];
        int hash = GetGridHash(pos, CellSize);
        SpatialGrid.Add(hash, index);
    }

    public static int GetGridHash(float3 pos, float cellSize)
    {
        int x = (int)math.floor(pos.x / cellSize);
        int z = (int)math.floor(pos.z / cellSize);
        return (x * 73856093) ^ (z * 83492791);
    }
}