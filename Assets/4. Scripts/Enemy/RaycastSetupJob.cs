using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Jobs;

[BurstCompile]
public struct RaycastSetupJob : IJobParallelForTransform
{
    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<RaycastCommand> Commands;

    [ReadOnly] public QueryParameters DownQueryParams;

    public void Execute(int index, TransformAccess transform)
    {
        Vector3 pos = transform.position;

        // 💡 아래로 쏘는 레이: 너무 높으면(3m) 동굴/다리 천장을 뚫고 올라갑니다. 머리 살짝 위(2m)로 낮춤.
        Vector3 downOrigin = pos;
        downOrigin.y += 2.0f;
        Commands[index * 2] = new RaycastCommand(downOrigin, Vector3.down, DownQueryParams, 4.0f);

        // 💡 전방 레이: 배꼽 높이(1m)면 작은 계단이나 경사면을 아예 못 봅니다. 무릎 아래(0.3f)에서 발사.
        Vector3 forwardOrigin = pos;
        forwardOrigin.y += 0.3f;
        Commands[index * 2 + 1] = new RaycastCommand(forwardOrigin, transform.rotation * Vector3.forward, DownQueryParams, 1.5f);
    }
}