using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

/// <summary>
/// P2 최적화: Main Thread에서 처리하던 Matrix4x4.TRS × N 연산을
/// Worker Thread에서 병렬로 계산한 뒤 결과를 NativeArray에 저장합니다.
/// DrawZombiesGPU()는 NativeArray → managed array 복사(memcpy)만 수행합니다.
/// </summary>
[BurstCompile]
public struct BuildRenderDataJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<Vector3>     Positions;
    [ReadOnly] public NativeArray<Quaternion>  Rotations;
    [ReadOnly] public NativeArray<int>         AnimStates;

    [WriteOnly] public NativeArray<Matrix4x4> Matrices;
    [WriteOnly] public NativeArray<float>     IsWalkingValues;
    [WriteOnly] public NativeArray<float>     TimeOffsets;

    public void Execute(int i)
    {
        Matrices[i]        = Matrix4x4.TRS(Positions[i], Rotations[i], Vector3.one);
        IsWalkingValues[i] = (AnimStates[i] == 1) ? 1f : 0f;
        TimeOffsets[i]     = (i * 0.123f) % 2f;
    }
}
