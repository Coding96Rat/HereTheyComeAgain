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
    public NativeArray<Quaternion> Rotations;
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

                if (flowDir.sqrMagnitude > 0.01f) desiredDir = flowDir;
                else desiredDir = distSqr > 0.001f ? toTarget.normalized : Vector3.forward;
            }
            else
            {
                desiredDir = distSqr > 0.001f ? toTarget.normalized : Vector3.forward;
            }
        }

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
            if (fwdHit.normal.y < 0.25f)
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

        // 💡 [복구됨] 완벽한 추락 방지 및 오르막/내리막 물리 연산
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

                // 다음 프레임 위치가 지형보다 낮아지면 지형 높이에 강제 고정
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
            // 바닥을 못 찾았을 때 공중부양 방지 (절벽 추락)
            currentYVel -= Gravity * dt;
            currentPos.y += currentYVel * dt;
        }

        // 최종 방어선: 어떤 이유로든 현재 Y가 땅보다 낮아지면 즉시 복구
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
        Rotations[index] = transform.rotation; // 💡 계산된 최종 회전값을 배열에 담아 메인 스레드로 넘깁니다
        transform.position = currentPos;
    }
}