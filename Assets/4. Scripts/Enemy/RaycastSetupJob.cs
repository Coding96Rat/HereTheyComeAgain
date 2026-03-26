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

    // RaycastSetupJob.cs
    public void Execute(int index, TransformAccess transform)
    {
        Vector3 pos = transform.position;

        // 💡 [수정] 머리 위 훨씬 높은 곳에서부터 깊게 쏩니다! (기존 2.0f -> 15.0f / 거리 10.0f -> 50.0f)
        // 좀비가 맵 아래로 약간 파고들었더라도, 15m 위에서 쏘면 지형 위에서 쏘는 판정이 되어 다시 지형 위로 끌어올려집니다.
        Vector3 downOrigin = pos;
        downOrigin.y += 15.0f;
        Commands[index * 2] = new RaycastCommand(downOrigin, Vector3.down, DownQueryParams, 25.0f);

        Vector3 forwardOrigin = pos;
        forwardOrigin.y += 0.3f;
        Commands[index * 2 + 1] = new RaycastCommand(forwardOrigin, transform.rotation * Vector3.forward, DownQueryParams, 1.5f);
    }
}