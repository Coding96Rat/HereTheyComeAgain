using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Jobs;

[BurstCompile]
public struct EnemyMovementJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<Vector3> TargetPositions;
    [ReadOnly] public NativeArray<RaycastHit> RaycastHits;
    [ReadOnly] public NativeArray<Vector3> AllEnemyPositions;
    [ReadOnly] public NativeArray<Vector3> StairPositions;

    public NativeArray<float> YVelocities;
    public NativeArray<int> AnimStates;

    [ReadOnly] public float DeltaTime;
    [ReadOnly] public float Speed;
    [ReadOnly] public float RotationSpeed;
    [ReadOnly] public float Gravity;
    [ReadOnly] public float PivotOffset;
    [ReadOnly] public float SeparationRadius;
    [ReadOnly] public float SeparationWeight;
    [ReadOnly] public float SlopeThreshold;

    public void Execute(int index, TransformAccess transform)
    {
        Vector3 currentPos = transform.position;
        Vector3 finalTargetPos = TargetPositions[index];
        RaycastHit hit = RaycastHits[index];

        Vector3 toPlayerFlat = finalTargetPos - currentPos;
        toPlayerFlat.y = 0f;
        float distToPlayerFlatSqr = toPlayerFlat.sqrMagnitude;

        bool hasStairs = StairPositions.Length > 0;

        // [수정 2] 계단을 버리고 플레이어에게 꺾는 높이 기준을 0.5m -> 0.1m로 초정밀화!
        bool targetIsHigh = (finalTargetPos.y - currentPos.y > 0.1f);

        Vector3 activeTarget = finalTargetPos;
        bool isTargetingPlayer = true;

        if (targetIsHigh && hasStairs)
        {
            float minDistToStair = float.MaxValue;
            Vector3 closestStair = currentPos;

            for (int s = 0; s < StairPositions.Length; s++)
            {
                float d = (StairPositions[s] - currentPos).sqrMagnitude;
                if (d < minDistToStair)
                {
                    minDistToStair = d;
                    closestStair = StairPositions[s];
                }
            }

            // [수정 2] 계단 꼭대기 마커에 완벽하게 도달할 때(sqr 0.25 = 0.5m 반경)까지 시선 고정!
            if (minDistToStair > 0.25f)
            {
                activeTarget = closestStair;
                isTargetingPlayer = false;
            }
        }

        Vector3 desiredDir = activeTarget - currentPos;
        desiredDir.y = 0f;
        if (desiredDir.sqrMagnitude > 0.001f) desiredDir.Normalize();

        bool isSteep = false;
        float groundY = currentPos.y;
        float heightDiff = 0f;

        if (hit.colliderEntityId != 0)
        {
            groundY = hit.point.y + PivotOffset;
            heightDiff = groundY - currentPos.y;

            if (hit.normal.y < SlopeThreshold)
            {
                isSteep = true;
            }
        }

        // [수정 1] "계단이 없는데 플레이어가 위에 있거나", "플레이어 발밑 8m 이내로 도달"했다면 무식하게 뭉치기 모드 온!
        bool shouldClump = isTargetingPlayer && (distToPlayerFlatSqr < 64.0f || (!hasStairs && targetIsHigh));

        Vector3 separationForce = Vector3.zero;
        float sepRadiusSqr = SeparationRadius * SeparationRadius;

        for (int i = 0; i < AllEnemyPositions.Length; i++)
        {
            if (i == index) continue;

            // [초극강 최적화 1] 곱셈(sqrMagnitude)을 하기 전, 단순 뺄셈으로 거리를 구합니다.
            float diffX = currentPos.x - AllEnemyPositions[i].x;
            float diffZ = currentPos.z - AllEnemyPositions[i].z;

            // [초극강 최적화 2] Bounding Box 컷(Cut)! 
            // 가로 또는 세로 거리가 SeparationRadius(예: 1.5m)보다 멀다면, 
            // 무거운 곱셈 연산을 하지 않고 즉시 다음 좀비로 넘어갑니다! (전체 연산의 99% 스킵)
            if (diffX > SeparationRadius || diffX < -SeparationRadius ||
                diffZ > SeparationRadius || diffZ < -SeparationRadius)
            {
                continue;
            }

            // 위 관문을 통과한 '진짜 코앞에 있는 좀비'들만 거리를 계산합니다.
            float sqrDist = diffX * diffX + diffZ * diffZ;

            if (sqrDist < sepRadiusSqr && sqrDist > 0.0001f)
            {
                float pushStrength = (sepRadiusSqr - sqrDist) / sepRadiusSqr;
                separationForce.x += diffX * pushStrength;
                separationForce.z += diffZ * pushStrength;
            }
        }

        if (separationForce.sqrMagnitude > 4.0f)
        {
            separationForce = separationForce.normalized * 2.0f;
        }

        float currentSepWeight = shouldClump ? SeparationWeight * 0.2f : SeparationWeight;
        Vector3 finalDir = desiredDir + (separationForce * currentSepWeight);

        if (finalDir.sqrMagnitude > 0.001f)
        {
            finalDir.Normalize();

            if (isSteep)
            {
                Vector3 flatNormal = new Vector3(hit.normal.x, 0, hit.normal.z).normalized;
                float dot = Vector3.Dot(finalDir, flatNormal);

                if (dot < 0)
                {
                    finalDir -= flatNormal * dot;
                }

                if (shouldClump)
                {
                    // 우회 따위 하지 않고 플레이어를 향해 겹치면서 좀비 탑 쌓기!
                    currentPos += finalDir.normalized * (Speed * 0.01f) * DeltaTime;
                }
                else
                {
                    // 일반적인 장애물을 만났을 때는 부드럽게 우회 (Tangent Flow)
                    float randomBias = (index % 2 == 0) ? 1f : -1f;
                    Vector3 tangent1 = new Vector3(flatNormal.z, 0, -flatNormal.x);
                    Vector3 tangent2 = new Vector3(-flatNormal.z, 0, flatNormal.x);

                    float d1 = Vector3.Dot(tangent1, desiredDir);
                    float d2 = Vector3.Dot(tangent2, desiredDir);

                    Vector3 bestTangent = (d1 > d2) ? tangent1 : tangent2;

                    if (Mathf.Abs(d1 - d2) < 0.1f)
                    {
                        bestTangent = (randomBias > 0) ? tangent1 : tangent2;
                    }

                    finalDir = (finalDir + bestTangent * 1.5f).normalized;
                    currentPos += flatNormal * (Speed * 0.1f) * DeltaTime;
                    currentPos += finalDir * (Speed * 0.7f) * DeltaTime;
                }
            }
            else
            {
                // 평지 및 오르막길 자연스러운 등반
                float slopeDot = finalDir.x * hit.normal.x + finalDir.y * hit.normal.y + finalDir.z * hit.normal.z;
                Vector3 moveDir = new Vector3(
                    finalDir.x - hit.normal.x * slopeDot,
                    finalDir.y - hit.normal.y * slopeDot,
                    finalDir.z - hit.normal.z * slopeDot
                );

                if (moveDir.sqrMagnitude > 0.001f)
                {
                    moveDir.Normalize();
                    currentPos += moveDir * Speed * DeltaTime;
                }
                else
                {
                    currentPos += finalDir * Speed * DeltaTime;
                }
            }

            if (desiredDir.sqrMagnitude > 0.001f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(desiredDir);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, RotationSpeed * DeltaTime);
            }

            AnimStates[index] = 1;
        }
        else
        {
            AnimStates[index] = 0;
        }

        // [수정 3] 추락 스냅 최적화: 1.5m 텔레포트 삭제, 진짜 중력 적용!
        float currentYVelocity = YVelocities[index];

        if (hit.colliderEntityId != 0)
        {
            if (!isSteep)
            {
                // 허용치를 1.5m -> 0.25m로 확 줄였습니다. 내리막길은 붙어서 가고, 절벽은 즉시 떨어집니다!
                if (currentPos.y <= groundY + 0.25f && currentPos.y >= groundY - 1.5f)
                {
                    currentPos.y = groundY;
                    currentYVelocity = 0f;
                }
                else
                {
                    // 공중 체공 중 (진짜 추락!)
                    currentYVelocity += Gravity * DeltaTime;
                    currentPos.y += currentYVelocity * DeltaTime;

                    // 떨어지다가 정확히 바닥을 뚫는 순간에만 안전하게 스냅 착지!
                    if (currentPos.y <= groundY)
                    {
                        currentPos.y = groundY;
                        currentYVelocity = 0f;
                    }
                }
            }
            else
            {
                // 벽에 붙었을 때도 발밑이 0.5m 이상 비어있으면 얄짤없이 자유낙하!
                if (heightDiff < -0.5f)
                {
                    currentYVelocity += Gravity * DeltaTime;
                    currentPos.y += currentYVelocity * DeltaTime;

                    if (currentPos.y <= groundY)
                    {
                        currentPos.y = groundY;
                        currentYVelocity = 0f;
                    }
                }
                else
                {
                    currentYVelocity = 0f;
                }
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