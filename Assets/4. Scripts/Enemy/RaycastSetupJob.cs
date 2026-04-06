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
    // 건물·벽 레이어를 포함한 전방 감지 파라미터 (EnemyMother._wallDetectionLayer 기반)
    [ReadOnly] public QueryParameters ForwardQueryParams;

    public void Execute(int index, TransformAccess transform)
    {
        Vector3 pos = transform.position;

        // 하향 레이캐스트 — 지형 Y 추적
        Vector3 downOrigin = pos;
        downOrigin.y += 15.0f;
        Commands[index * 2] = new RaycastCommand(downOrigin, Vector3.down, DownQueryParams, 25.0f);

        // 전방 레이캐스트 — 건물·벽 감지 후 속도 슬라이딩 (EnemyMovementJob 5번 단계)
        Vector3 forwardOrigin = pos;
        forwardOrigin.y += 0.3f;
        Commands[index * 2 + 1] = new RaycastCommand(forwardOrigin, transform.rotation * Vector3.forward, ForwardQueryParams, 1.5f);
    }
}
