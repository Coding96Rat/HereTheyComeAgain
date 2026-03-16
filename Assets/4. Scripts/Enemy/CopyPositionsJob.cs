using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Jobs;

[BurstCompile]
public struct CopyPositionsJob : IJobParallelForTransform
{
    [WriteOnly] public NativeArray<Vector3> CurrentPositions;

    public void Execute(int index, TransformAccess transform)
    {
        CurrentPositions[index] = transform.position; // 멀티코어에서 초고속으로 복사!
    }
}