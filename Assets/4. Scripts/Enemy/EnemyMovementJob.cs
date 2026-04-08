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
    [ReadOnly] public NativeArray<float> Speeds;
    [ReadOnly] public float RotationSpeed;
    [ReadOnly] public float Gravity;
    [ReadOnly] public float SeparationRadius;
    [ReadOnly] public float SeparationWeight;
    [ReadOnly] public float CellSize;
    [ReadOnly] public NativeArray<Vector3> FlowField0;
    [ReadOnly] public NativeArray<Vector3> FlowField1;
    [ReadOnly] public NativeArray<Vector3> FlowField2;
    [ReadOnly] public NativeArray<Vector3> FlowField3;
    [ReadOnly] public int GridCols;
    [ReadOnly] public int GridRows;
    [ReadOnly] public float AiCellSize;
    [ReadOnly] public Vector3 BottomLeft;

    [ReadOnly] public NativeArray<byte> CostField;

    [ReadOnly] public NativeArray<Vector3> StructurePositions;
    [ReadOnly] public NativeArray<Vector3> StructureExtents; // AABB 사각형 크기 데이터
    [ReadOnly] public int StructureCount;
    [ReadOnly] public float StructureDetectRangeSqr;

    [ReadOnly] public NativeArray<byte> SuppressStructureDetection;

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

        // ── 1. FlowField 기반 이동 방향 결정 ──────────────────────────────────
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

                // 💡 [버그 2 해결] 플레이어 타겟팅 중 254(구조물)를 밟으면 Escape 무시 후 직진
                if (CostField.IsCreated && CostField[flatIdx] == 254 && SuppressStructureDetection[index] == 1)
                {
                    desiredDir = toTarget.normalized;
                }
                else if (flowDir.sqrMagnitude > 0.01f) desiredDir = flowDir;
                else if (distSqr > 0.001f) desiredDir = toTarget.normalized;
            }
            else
            {
                if (distSqr > 0.001f) desiredDir = toTarget.normalized;
            }
        }

        // ── 2. 주변 플레이어 구조물 감지 → 가장 가까운 구조물 우선 타겟 ──
        if (StructureCount > 0 && distSqr > 2.25f && SuppressStructureDetection[index] == 0)
        {
            float nearestSqr = StructureDetectRangeSqr;
            int nearestIdx = -1;

            for (int s = 0; s < StructureCount; s++)
            {
                Vector3 sp = StructurePositions[s];
                float sdx = sp.x - currentPos.x;
                float sdz = sp.z - currentPos.z;
                float ssqr = sdx * sdx + sdz * sdz;

                if (ssqr >= nearestSqr) continue;

                float dotFwd = sdx * toTarget.x + sdz * toTarget.z;
                if (dotFwd <= 0f) continue;

                nearestSqr = ssqr;
                nearestIdx = s;
            }

            if (nearestIdx >= 0)
            {
                Vector3 sp = StructurePositions[nearestIdx];
                Vector3 toStruct = new Vector3(sp.x - currentPos.x, 0, sp.z - currentPos.z);
                if (toStruct.sqrMagnitude > 0.001f)
                    desiredDir = (Vector3)math.normalize(toStruct);
            }
        }

        // ── 3. 방향 흔들림(sway) ─────────────────────────────────────────────
        if (desiredDir.sqrMagnitude > 0.001f && distSqr > 10.0f)
        {
            Vector3 rightVector = new Vector3(desiredDir.z, 0, -desiredDir.x);
            float sway = math.sin(ElapsedTime * 2.0f + index * 7.13f) * 0.1f;
            desiredDir += rightVector * sway;
            desiredDir = (Vector3)math.normalize(desiredDir);
        }

        float trueGroundY = downHit.colliderEntityId != 0 ? downHit.point.y : -999f;
        float targetGroundY = trueGroundY;

        // ── 4. 분리력 ─────────────────────────────────────────────────────────
        Vector3 separation = Vector3.zero;
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
                    } while (SpatialGrid.TryGetNextValue(out otherIdx, ref it));
                }
            }
        }

        if (separation.sqrMagnitude > 4.0f) separation = separation.normalized * 2.0f;

        float currentYVel = YVelocities[index];
        float stopDistSqr = 2.0f;
        float currentSpeed = (distSqr < stopDistSqr) ? 0f : Speeds[index];

        Vector3 xzVelocity = (desiredDir * currentSpeed) + (separation * SeparationWeight);

        if (distSqr > 0.1f && distSqr < 9.0f)
        {
            float dist = math.sqrt(distSqr);
            Vector3 radial = new Vector3(toTarget.x / dist, 0f, toTarget.z / dist);
            Vector3 tangentCW = new Vector3(radial.z, 0f, -radial.x);
            Vector3 tangentCCW = new Vector3(-radial.z, 0f, radial.x);
            float cwDot = separation.x * tangentCW.x + separation.z * tangentCW.z;
            float ccwDot = separation.x * tangentCCW.x + separation.z * tangentCCW.z;
            Vector3 chosenTangent = (cwDot >= ccwDot) ? tangentCW : tangentCCW;
            float tangentStrength = math.max(0f, 1f - dist / 3.0f) * Speeds[index] * 0.8f;
            xzVelocity.x += chosenTangent.x * tangentStrength;
            xzVelocity.z += chosenTangent.z * tangentStrength;
        }

        // ── 5. 전방 레이캐스트 슬라이딩 ──────────────────────────────────────
        const float stopDist = 0.65f;
        const float pushDist = 0.20f;
        if (fwdHit.colliderEntityId != 0 && fwdHit.distance < stopDist)
        {
            if (fwdHit.normal.y < 0.25f)
            {
                float dotVelocity = Vector3.Dot(xzVelocity, fwdHit.normal);
                if (dotVelocity < 0) xzVelocity -= fwdHit.normal * dotVelocity;

                if (fwdHit.distance < pushDist)
                {
                    Vector3 pushOut = fwdHit.normal * (pushDist - fwdHit.distance);
                    pushOut.y = 0;
                    currentPos += pushOut;
                }
            }
        }

        // ── 6. Y축 물리 (오르막 / 내리막 / 추락) ────────────────────────────
        if (targetGroundY != -999f)
        {
            float yDiff = targetGroundY - currentPos.y;

            if (yDiff > 0.1f)
            {
                currentPos.y += math.min(yDiff, Speeds[index] * 1.5f * dt);
                currentYVel = 0f;
            }
            else if (yDiff < -0.1f)
            {
                currentYVel -= Gravity * dt;
                float nextY = currentPos.y + currentYVel * dt;
                if (nextY < targetGroundY) { currentPos.y = targetGroundY; currentYVel = 0f; }
                else currentPos.y = nextY;
            }
            else
            {
                currentPos.y = targetGroundY;
                currentYVel = 0f;
            }
        }
        else
        {
            currentYVel -= Gravity * dt;
            currentPos.y += currentYVel * dt;
        }

        if (targetGroundY != -999f && currentPos.y < targetGroundY)
            currentPos.y = targetGroundY;

        // ── 7. CostField cost==255 절대 벽 차단 ─────────────────────────────
        if (CostField.IsCreated && GridCols > 0)
        {
            float dtx = xzVelocity.x * dt;
            float dtz = xzVelocity.z * dt;

            int nx = (int)math.floor((currentPos.x + dtx - BottomLeft.x) / AiCellSize);
            int nz = (int)math.floor((currentPos.z - BottomLeft.z) / AiCellSize);

            if (nx >= 0 && nx < GridCols && nz >= 0 && nz < GridRows && CostField[nz * GridCols + nx] == 255)
                xzVelocity.x = 0f;

            int nx2 = (int)math.floor((currentPos.x - BottomLeft.x) / AiCellSize);
            int nz2 = (int)math.floor((currentPos.z + dtz - BottomLeft.z) / AiCellSize);
            if (nx2 >= 0 && nx2 < GridCols && nz2 >= 0 && nz2 < GridRows && CostField[nz2 * GridCols + nx2] == 255)
                xzVelocity.z = 0f;
        }

        // ── 7.5. 구조물 표면 정밀 정지 (AABB 사각형 고속 연산) ─────────────────────────
        if (StructureCount > 0)
        {
            float nextX = currentPos.x + xzVelocity.x * dt;
            float nextZ = currentPos.z + xzVelocity.z * dt;
            float zombieRadius = 0.35f;

            for (int s = 0; s < StructureCount; s++)
            {
                Vector3 sp = StructurePositions[s];
                Vector3 extents = StructureExtents[s];

                float dx = math.abs(currentPos.x - sp.x);
                float dz = math.abs(currentPos.z - sp.z);
                if (dx > extents.x + AiCellSize || dz > extents.z + AiCellSize) continue;

                float distX = math.max(0f, math.abs(nextX - sp.x) - (extents.x + zombieRadius));
                float distZ = math.max(0f, math.abs(nextZ - sp.z) - (extents.z + zombieRadius));

                if (distX <= 0.001f && distZ <= 0.001f)
                {
                    float overlapX = dx - extents.x;
                    float overlapZ = dz - extents.z;

                    if (overlapX > overlapZ) xzVelocity.x = 0f;
                    else xzVelocity.z = 0f;
                }
            }
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
        Rotations[index] = transform.rotation;
        transform.position = currentPos;
    }
}