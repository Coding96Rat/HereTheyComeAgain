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
    // [ReadOnly] public QueryParameters FwdQueryParams; // 만약 쓰신다면 주석 해제

    public void Execute(int index, TransformAccess transform)
    {
        Vector3 pos = transform.position;
        Vector3 fwd = transform.rotation * Vector3.forward;

        // 1. 아래로 쏘는 레이캐스트
        Vector3 downOrigin = pos;
        downOrigin.y += 1.0f;
        Commands[index * 2] = new RaycastCommand(downOrigin, Vector3.down, DownQueryParams, 3.0f);

        // 💡 [핵심 수정] 2. 앞으로 쏘는 레이캐스트
        Vector3 forwardOrigin = pos;
        forwardOrigin.y += 0.5f; // 배꼽 높이

        // 좀비의 중심보다 약간 뒤에서(-0.3f) 쏴야, 벽에 완전히 밀착해도 앞면을 정확히 감지합니다.
        forwardOrigin -= fwd * 0.3f;

        // 뒤에서 쐈으므로 길이를 그만큼 길게(1.5f) 늘려줍니다.
        // FwdQueryParams를 쓰신다면 DownQueryParams 대신 교체해주세요.
        Commands[index * 2 + 1] = new RaycastCommand(forwardOrigin, fwd, DownQueryParams, 1.5f);
    }
}