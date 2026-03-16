using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Jobs;

[BurstCompile]
public struct RaycastSetupJob : IJobParallelForTransform
{
    [WriteOnly] public NativeArray<RaycastCommand> Commands;
    [ReadOnly] public QueryParameters QueryParams;

    public void Execute(int index, TransformAccess transform)
    {
        // 캡슐의 머리 꼭대기(위로 2만큼 띄운 곳)에서 아래로 쏩니다.
        Vector3 origin = transform.position;
        origin.y += 2.0f;

        // 20f 거리만큼 바닥을 향해 레이캐스트 세팅
        Commands[index] = new RaycastCommand(origin, Vector3.down, QueryParams, 20f);
    }
}