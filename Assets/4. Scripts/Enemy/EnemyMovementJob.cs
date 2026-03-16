using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Jobs;



[BurstCompile]
public struct EnemyMovementJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<Vector3> TargetPositions;
    [ReadOnly] public NativeArray<RaycastHit> RaycastHits;

    //  [추가됨] 모든 적들의 현재 위치를 담은 배열 (서로의 거리를 알기 위함)
    [ReadOnly] public NativeArray<Vector3> AllEnemyPositions;
    public NativeArray<float> YVelocities;

    // [추가됨] 애니메이션 상태를 메인 스레드로 넘겨줄 배열 (0: Idle, 1: Walk)
    public NativeArray<int> AnimStates;

    [ReadOnly] public float DeltaTime;
    [ReadOnly] public float Speed;
    [ReadOnly] public float RotationSpeed;
    [ReadOnly] public float Gravity;
    [ReadOnly] public float PivotOffset;

    //  [추가됨] 군집(밀어내기) 설정값
    [ReadOnly] public float SeparationRadius; // 밀어낼 반경 (적의 뚱뚱함 정도)
    [ReadOnly] public float SeparationWeight; // 밀어내는 힘의 세기

    public void Execute(int index, TransformAccess transform)
    {
        Vector3 currentPos = transform.position;
        Vector3 targetPos = TargetPositions[index];

        // --- 1. 타겟을 향한 기본 방향 계산 ---
        Vector3 toTarget = targetPos - currentPos;
        toTarget.y = 0f;
        Vector3 desiredDir = Vector3.zero;

        if (toTarget.sqrMagnitude > 0.001f)
        {
            desiredDir = toTarget.normalized;
        }

        // --- 2.  [핵심] 밀어내기(Separation) 연산 ---
        Vector3 separationForce = Vector3.zero;
        float sepRadiusSqr = SeparationRadius * SeparationRadius;

        // 내 주변의 다른 모든 적들을 검사합니다 (Burst 컴파일러 덕분에 이 무식한 O(N^2) 반복문이 1ms 안에 처리됩니다)
        for (int i = 0; i < AllEnemyPositions.Length; i++)
        {
            if (i == index) continue; // 자기 자신은 건너뜀

            Vector3 otherPos = AllEnemyPositions[i];
            Vector3 diff = currentPos - otherPos;
            diff.y = 0f; // 평면(XZ)에서만 밀어냅니다.

            float sqrDist = diff.sqrMagnitude;

            // 다른 적이 내 '밀어내기 반경' 안에 들어왔다면?
            if (sqrDist < sepRadiusSqr && sqrDist > 0.0001f)
            {
                float dist = Mathf.Sqrt(sqrDist);
                // 가까울수록 더 강하게 밀어냅니다.
                float pushStrength = (SeparationRadius - dist) / SeparationRadius;
                separationForce += (diff / dist) * pushStrength;
            }
        }

        // --- 3. 최종 이동 방향 결정 (목표 방향 + 밀어내는 방향) ---
        Vector3 finalDir = desiredDir + (separationForce * SeparationWeight);

        if (finalDir.sqrMagnitude > 0.001f)
        {
            finalDir.Normalize(); // 최종 방향 정규화
            currentPos += finalDir * Speed * DeltaTime; // 실제 위치 이동

            // 회전은 최종 방향을 바라보게 부드럽게 꺾어줍니다.
            Quaternion targetRotation = Quaternion.LookRotation(finalDir);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, RotationSpeed * DeltaTime);

            AnimStates[index] = 1;
        }
        else
        {
            AnimStates[index] = 0;
        }

            // --- 4. 중력 및 바닥 충돌(Grounded) 처리 (이전과 동일) ---
            RaycastHit hit = RaycastHits[index];
        float currentYVelocity = YVelocities[index];

        if (hit.colliderEntityId != 0)
        {
            float groundY = hit.point.y + PivotOffset;

            if (currentPos.y <= groundY)
            {
                currentPos.y = groundY;
                currentYVelocity = 0f;
            }
            else
            {
                currentYVelocity += Gravity * DeltaTime;
                currentPos.y += currentYVelocity * DeltaTime;
            }
        }
        else
        {
            currentYVelocity += Gravity * DeltaTime;
            currentPos.y += currentYVelocity * DeltaTime;
        }

        YVelocities[index] = currentYVelocity;
        transform.position = currentPos;
    }
}