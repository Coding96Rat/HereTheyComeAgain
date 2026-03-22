using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

[BurstCompile]
public struct EnemyMovementJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<Vector3> ActiveTargetPositions;
    [ReadOnly] public NativeArray<int> TargetIndices;
    [ReadOnly] public NativeArray<RaycastHit> RaycastHits;
    [ReadOnly] public NativeArray<Vector3> AllEnemyPositions;
    [ReadOnly] public NativeParallelMultiHashMap<int, int> SpatialGrid;

    public NativeArray<float> YVelocities;
    public NativeArray<int> AnimStates;

    [ReadOnly] public float ElapsedTime;
    [ReadOnly] public float DeltaTime;
    [ReadOnly] public float Speed;
    [ReadOnly] public float RotationSpeed;
    [ReadOnly] public float Gravity;
    [ReadOnly] public float SeparationRadius;
    [ReadOnly] public float SeparationWeight;
    [ReadOnly] public float CellSize;
    [ReadOnly] public float MaxWeightTolerance;

    [ReadOnly] public NativeArray<Vector3> FlowField0;
    [ReadOnly] public NativeArray<Vector3> FlowField1;
    [ReadOnly] public NativeArray<Vector3> FlowField2;
    [ReadOnly] public NativeArray<Vector3> FlowField3;
    [ReadOnly] public int GridCols;
    [ReadOnly] public int GridRows;
    [ReadOnly] public float AiCellSize;
    [ReadOnly] public Vector3 BottomLeft;

    public void Execute(int index, TransformAccess transform)
    {
        Vector3 currentPos = transform.position;
        int tIdx = TargetIndices[index];

        Vector3 targetPos = (tIdx >= 0 && tIdx < ActiveTargetPositions.Length) ? ActiveTargetPositions[tIdx] : currentPos;

        float dt = DeltaTime;

        RaycastHit downHit = RaycastHits[index * 2];
        RaycastHit fwdHit = RaycastHits[index * 2 + 1];

        Vector3 toTarget = targetPos - currentPos;
        toTarget.y = 0f;
        float distSqr = toTarget.sqrMagnitude;

        Vector3 desiredDir = Vector3.forward;

        // 💡 [수정됨] 타겟과의 거리가 약 1.5m(2.25f) 이내일 때만 FFS를 무시하고 타겟에게 달려듭니다.
        // 기존 25.0f(5m)에서 대폭 줄여, 먼 거리에서는 무조건 FFS 화살표를 따르도록 강제합니다.
        if (distSqr < 2.25f && distSqr > 0.001f)
        {
            desiredDir = toTarget.normalized;
        }
        else
        {
            int x = (int)math.floor((currentPos.x - BottomLeft.x) / AiCellSize);
            int z = (int)math.floor((currentPos.z - BottomLeft.z) / AiCellSize);

            if (x >= 0 && x < GridCols && z >= 0 && z < GridRows)
            {
                int flatIdx = z * GridCols + x;
                Vector3 flowDir = Vector3.zero;

                if (tIdx == 0 && FlowField0.IsCreated) flowDir = FlowField0[flatIdx];
                else if (tIdx == 1 && FlowField1.IsCreated) flowDir = FlowField1[flatIdx];
                else if (tIdx == 2 && FlowField2.IsCreated) flowDir = FlowField2[flatIdx];
                else if (tIdx == 3 && FlowField3.IsCreated) flowDir = FlowField3[flatIdx];

                // 💡 [수정됨] 내부적인 각도 계산(Blending)을 모두 제거하고, 
                // 오직 FlowFieldJobs가 만들어준 완벽한 화살표(flowDir)에 100% 복종합니다.
                if (flowDir.sqrMagnitude > 0.01f) desiredDir = flowDir;
                else desiredDir = distSqr > 0.001f ? toTarget.normalized : Vector3.forward;
            }
            else
            {
                desiredDir = distSqr > 0.001f ? toTarget.normalized : Vector3.forward;
            }
        }

        // 💡 [수정됨] 군집 퍼트리기 (Swarm Sway) 강도 대폭 축소
        // 곱하는 값을 0.5f에서 0.1f로 줄여서, 화살표 궤도를 이탈하지 않는 선에서만 살짝 꿈틀거리게 만듭니다.
        if (desiredDir.sqrMagnitude > 0.001f && distSqr > 10.0f)
        {
            Vector3 rightVector = new Vector3(desiredDir.z, 0, -desiredDir.x);
            float sway = math.sin(ElapsedTime * 2.0f + index * 7.13f) * 0.1f;
            desiredDir += rightVector * sway;
            desiredDir = (Vector3)math.normalize(desiredDir);
        }

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
                            if (otherPos.y > currentPos.y + 0.8f) myCarriedWeight += 1.0f;
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
        float currentYVel = YVelocities[index];
        float currentSpeed = (distSqr < 2.0f) ? 0f : Speed;

        Vector3 xzVelocity = (desiredDir * currentSpeed) + (separation * SeparationWeight);
        if (isCollapsing) targetGroundY = trueGroundY;

        if (!isCollapsing && fwdHit.colliderEntityId != 0 && fwdHit.distance < 1.2f)
        {
            if (fwdHit.normal.y < 0.6f)
            {
                float dotVelocity = Vector3.Dot(xzVelocity, fwdHit.normal);
                if (dotVelocity < 0) xzVelocity -= fwdHit.normal * dotVelocity;

                float pushThreshold = 0.6f;
                if (fwdHit.distance < pushThreshold)
                {
                    Vector3 pushOut = fwdHit.normal * (pushThreshold - fwdHit.distance);
                    pushOut.y = 0;
                    currentPos += pushOut;
                }
            }
        }

        if (targetGroundY != -999f)
        {
            float yDiff = targetGroundY - currentPos.y;

            if (yDiff > 0.1f) // 오르막
            {
                currentPos.y += math.min(yDiff, Speed * 1.5f * dt);
                currentYVel = 0f;
            }
            else if (yDiff < -0.1f) // 내리막 또는 추락
            {
                currentYVel -= Gravity * (isCollapsing ? 2.5f : 1.0f) * dt;
                float nextY = currentPos.y + currentYVel * dt;

                // 💡 [추락 방지 핵심]: 다음 프레임 위치가 지형보다 낮아지면 강제로 지형 높이에 고정
                if (nextY < targetGroundY)
                {
                    currentPos.y = targetGroundY;
                    currentYVel = 0f;
                }
                else
                {
                    currentPos.y = nextY;
                }
            }
            else // 평지
            {
                currentPos.y = targetGroundY;
                currentYVel = 0f;
            }
        }
        else
        {
            // 💡 [세이프티]: 레이캐스트가 바닥을 못 찾았을 때(타일 경계 등) 
            // 갑자기 추락하지 않고 이전 프레임 높이를 유지하며 재탐색 유도
            currentYVel = 0f;
        }

        // 💡 [최종 방어선]: 만약 어떤 이유로든 현재 Y가 targetGroundY보다 낮아지면 즉시 복구
        if (targetGroundY != -999f && currentPos.y < targetGroundY)
        {
            currentPos.y = targetGroundY;
        }

        currentPos.x += xzVelocity.x * dt;
        currentPos.z += xzVelocity.z * dt;

        if (desiredDir.sqrMagnitude > 0.001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(desiredDir);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, RotationSpeed * dt);
        }

        AnimStates[index] = (currentSpeed < 0.2f) ? 2 : 1;
        YVelocities[index] = currentYVel;
        transform.position = currentPos;
    }
}