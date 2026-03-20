using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

[BurstCompile]
public struct EnemyMovementJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<Vector3> TargetPositions;
    [ReadOnly] public NativeArray<RaycastHit> RaycastHits;
    [ReadOnly] public NativeArray<Vector3> AllEnemyPositions;
    [ReadOnly] public NativeParallelMultiHashMap<int, int> SpatialGrid;

    public NativeArray<float> YVelocities;
    public NativeArray<int> AnimStates;

    [ReadOnly] public float DeltaTime;
    [ReadOnly] public float Speed;
    [ReadOnly] public float RotationSpeed;
    [ReadOnly] public float Gravity;
    [ReadOnly] public float SeparationRadius;
    [ReadOnly] public float SeparationWeight;
    [ReadOnly] public float CellSize;
    [ReadOnly] public float MaxWeightTolerance;

    public void Execute(int index, TransformAccess transform)
    {
        Vector3 currentPos = transform.position;
        Vector3 targetPos = TargetPositions[index];
        float dt = DeltaTime;

        RaycastHit downHit = RaycastHits[index * 2];
        RaycastHit fwdHit = RaycastHits[index * 2 + 1];

        Vector3 toTarget = targetPos - currentPos;
        toTarget.y = 0f;
        Vector3 desiredDir = toTarget.sqrMagnitude > 0.001f ? toTarget.normalized : Vector3.forward;

        float trueGroundY = downHit.colliderEntityId != 0 ? downHit.point.y : -999f;
        float targetGroundY = trueGroundY;

        Vector3 separation = Vector3.zero;
        float myCarriedWeight = 0f;
        float sepRadiusSqr = SeparationRadius * SeparationRadius;

        for (int cx = -1; cx <= 1; cx++)
        {
            for (int cz = -1; cz <= 1; cz++)
            {
                int hash = HashPositionsJob.GetGridHash(
                    new float3(currentPos.x + cx * CellSize, 0, currentPos.z + cz * CellSize), CellSize);

                if (SpatialGrid.TryGetFirstValue(hash, out int otherIdx, out NativeParallelMultiHashMapIterator<int> it))
                {
                    int checkCount = 0;

                    do
                    {
                        if (otherIdx == index) continue;

                        checkCount++;
                        if (checkCount > 16) break;

                        Vector3 otherPos = AllEnemyPositions[otherIdx];
                        Vector3 diff = currentPos - otherPos;
                        float sqrDistXZ = diff.x * diff.x + diff.z * diff.z;

                        if (sqrDistXZ < sepRadiusSqr && math.abs(diff.y) < 1.5f && sqrDistXZ > 0.001f)
                        {
                            float pushStrength = (sepRadiusSqr - sqrDistXZ) / sepRadiusSqr;
                            separation.x += diff.x * pushStrength;
                            separation.z += diff.z * pushStrength;
                        }

                        if (sqrDistXZ < 0.8f)
                        {
                            if (otherPos.y > currentPos.y + 0.8f)
                            {
                                myCarriedWeight += 1.0f;
                            }
                            else if (otherPos.y < currentPos.y && otherPos.y > currentPos.y - 1.8f)
                            {
                                float otherHeadY = otherPos.y + 1.2f;
                                if (otherHeadY > targetGroundY) targetGroundY = otherHeadY;
                            }
                        }
                    } while (SpatialGrid.TryGetNextValue(out otherIdx, ref it));
                }
            }
        }

        if (separation.sqrMagnitude > 4.0f) separation = separation.normalized * 2.0f;

        bool isCollapsing = myCarriedWeight >= MaxWeightTolerance;
        bool isClimbingWall = false;
        float currentYVel = YVelocities[index];
        float currentSpeed = (toTarget.sqrMagnitude < 2.0f) ? 0f : Speed;

        Vector3 xzVelocity = (desiredDir * currentSpeed) + (separation * SeparationWeight);

        if (isCollapsing) targetGroundY = trueGroundY;

        // 벽 등반 및 관통 방지
        if (!isCollapsing && fwdHit.colliderEntityId != 0 && fwdHit.distance < 1.2f)
        {
            Vector3 wallNormal = fwdHit.normal;

            float dotVelocity = Vector3.Dot(xzVelocity, wallNormal);
            if (dotVelocity < 0) xzVelocity -= wallNormal * dotVelocity;

            float pushThreshold = 0.6f;
            if (fwdHit.distance < pushThreshold)
            {
                Vector3 pushOut = wallNormal * (pushThreshold - fwdHit.distance);
                pushOut.y = 0;
                currentPos += pushOut;
            }

            Vector3 slopeUpDir = Vector3.ProjectOnPlane(Vector3.up, wallNormal).normalized;
            if (slopeUpDir.y > 0.1f)
            {
                isClimbingWall = true;
                float steepness = slopeUpDir.y;
                float climbSpeed = Speed * math.lerp(0.8f, 0.2f, steepness);

                currentPos += slopeUpDir * climbSpeed * dt;
                currentYVel = 0f;
            }
        }

        // 🚨 [핵심 수정] 수직 이동 (Y축) 스무딩 로직 도입
        if (!isClimbingWall)
        {
            if (currentPos.y > targetGroundY + 0.1f) // 1. 공중에 떠 있음 -> 추락
            {
                float gravityMultiplier = isCollapsing ? 2.5f : 1.0f;
                currentYVel -= Gravity * gravityMultiplier * dt;
                currentPos.y += currentYVel * dt;

                // 추락하다 바닥에 닿음
                if (currentPos.y < targetGroundY)
                {
                    currentPos.y = targetGroundY;
                    currentYVel = 0f;
                }
            }
            else if (currentPos.y < targetGroundY - 0.1f) // 2. 타겟(동족의 머리)이 나보다 위에 있음 -> 기어오르기
            {
                // 순간이동(팝핑) 대신 부드럽게 위로 끌어올림 (Speed의 80% 속도로 기어오름)
                currentPos.y += (Speed * 0.8f) * dt;
                currentYVel = 0f;
            }
            else // 3. 바닥에 완벽히 밀착
            {
                currentPos.y = targetGroundY;
                currentYVel = 0f;
            }
        }
        else
        {
            // 절벽을 타고 있는 와중에도 동족들이 밑에서 쌓아 올려주면 부드럽게 상승
            if (targetGroundY != -999f && currentPos.y < targetGroundY - 0.1f)
            {
                currentPos.y += (Speed * 0.8f) * dt;
            }
        }

        currentPos.x += xzVelocity.x * dt;
        currentPos.z += xzVelocity.z * dt;

        if (desiredDir.sqrMagnitude > 0.001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(desiredDir);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, RotationSpeed * dt);
        }

        AnimStates[index] = (currentSpeed < 0.2f && !isClimbingWall) ? 2 : 1;
        YVelocities[index] = currentYVel;
        transform.position = currentPos;
    }
}