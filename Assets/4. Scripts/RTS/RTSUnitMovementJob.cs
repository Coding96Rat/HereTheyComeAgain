using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// 플레이어 유닛 일괄 이동 Job. EnemyMovementJob과 동일한 구조.
/// RTSFlowFieldSystem이 목적지 기준으로 생성한 FlowField를 샘플링하여 이동.
/// </summary>
[BurstCompile]
public struct RTSUnitMovementJob : IJobParallelForTransform
{
    // ─── FlowField (RTSFlowFieldSystem.NativeFlowFields) ─────────────────────
    [ReadOnly] public NativeArray<Vector3> FlowField0;
    [ReadOnly] public NativeArray<Vector3> FlowField1;
    [ReadOnly] public NativeArray<Vector3> FlowField2;
    [ReadOnly] public NativeArray<Vector3> FlowField3;

    // ─── 유닛 데이터 ─────────────────────────────────────────────────────────
    [ReadOnly] public NativeArray<int>   OwnerIndices;       // 유닛별 playerOwnerIndex
    [ReadOnly] public NativeArray<float> Speeds;
    [ReadOnly] public NativeArray<Vector3> PlayerDestinations; // 플레이어별 목적지 (크기 4)

    public  NativeArray<float>      YVelocities;
    public  NativeArray<Quaternion> Rotations;
    // CopyPositionsJob이 이전 프레임 위치를 기록한 배열 — 분리력 계산 시 ReadOnly 참조.
    // Job 내부에서 쓰지 않으므로 데이터 레이스 없음 (EnemyMovementJob.AllEnemyPositions와 동일 역할).
    [ReadOnly] public NativeArray<Vector3> CurrentPositions;

    // ─── 분리력 (Separation) ─────────────────────────────────────────────────
    [ReadOnly] public NativeParallelMultiHashMap<int, int> SpatialGrid;
    [ReadOnly] public float SeparationRadius;
    [ReadOnly] public float SeparationWeight;
    [ReadOnly] public float CellSize;

    // ─── 지형 레이캐스트 ─────────────────────────────────────────────────────
    [ReadOnly] public NativeArray<RaycastHit> RaycastHits;

    // ─── FlowField 메타 ──────────────────────────────────────────────────────
    [ReadOnly] public int     GridCols;
    [ReadOnly] public int     GridRows;
    [ReadOnly] public float   AiCellSize;
    [ReadOnly] public Vector3 BottomLeft;
    [ReadOnly] public NativeArray<byte> CostField;

    // ─── 공통 ────────────────────────────────────────────────────────────────
    [ReadOnly] public float DeltaTime;
    [ReadOnly] public float RotationSpeed;
    [ReadOnly] public float Gravity;
    [ReadOnly] public float StoppingDistanceSqr;

    public void Execute(int index, TransformAccess transform)
    {
        Vector3 currentPos = transform.position;
        int ownerIdx = OwnerIndices[index];

        Vector3 destination = (ownerIdx >= 0 && ownerIdx < PlayerDestinations.Length)
            ? PlayerDestinations[ownerIdx]
            : currentPos;

        Vector3 toTarget = destination - currentPos;
        toTarget.y = 0f;
        float distSqr = toTarget.sqrMagnitude;

        float dt = DeltaTime;

        // ── 1. 목적지 도착 → 정지 ─────────────────────────────────────────────
        if (distSqr <= StoppingDistanceSqr)
        {
            ApplyGravityOnly(index, ref currentPos, dt);
            Rotations[index] = transform.rotation;
            transform.position = currentPos;
            return;
        }

        // ── 2. FlowField 방향 샘플링 ──────────────────────────────────────────
        Vector3 desiredDir = Vector3.zero;

        if (distSqr < 2.25f)
        {
            desiredDir = math.normalize(toTarget);
        }
        else
        {
            int cx = (int)math.floor((currentPos.x - BottomLeft.x) / AiCellSize);
            int cz = (int)math.floor((currentPos.z - BottomLeft.z) / AiCellSize);

            if (cx >= 0 && cx < GridCols && cz >= 0 && cz < GridRows)
            {
                int flatIdx = cz * GridCols + cx;
                Vector3 flowDir = SampleFlowField(ownerIdx, flatIdx);

                desiredDir = flowDir.sqrMagnitude > 0.01f ? flowDir : (Vector3)math.normalize(toTarget);
            }
            else
            {
                desiredDir = math.normalize(toTarget);
            }
        }

        // ── 3. 분리력 ─────────────────────────────────────────────────────────
        Vector3 separation = ComputeSeparation(index, currentPos);
        if (separation.sqrMagnitude > 4f) separation = (Vector3)math.normalize(separation) * 2f;

        float currentSpeed = Speeds[index];
        Vector3 xzVelocity = desiredDir * currentSpeed + separation * SeparationWeight;

        // ── 4. CostField 벽 차단 (cost==255) ─────────────────────────────────
        if (CostField.IsCreated && GridCols > 0)
        {
            int nx = (int)math.floor((currentPos.x + xzVelocity.x * dt - BottomLeft.x) / AiCellSize);
            int nz = (int)math.floor((currentPos.z - BottomLeft.z) / AiCellSize);
            if (nx >= 0 && nx < GridCols && nz >= 0 && nz < GridRows && CostField[nz * GridCols + nx] == 255)
                xzVelocity.x = 0f;

            int nx2 = (int)math.floor((currentPos.x - BottomLeft.x) / AiCellSize);
            int nz2 = (int)math.floor((currentPos.z + xzVelocity.z * dt - BottomLeft.z) / AiCellSize);
            if (nx2 >= 0 && nx2 < GridCols && nz2 >= 0 && nz2 < GridRows && CostField[nz2 * GridCols + nx2] == 255)
                xzVelocity.z = 0f;
        }

        // ── 5. Y축 물리 ───────────────────────────────────────────────────────
        RaycastHit downHit = RaycastHits[index * 2];   // RaycastSetupJob: [i*2]=down, [i*2+1]=forward
        float trueGroundY = downHit.colliderEntityId != 0 ? downHit.point.y : -999f;
        float currentYVel = YVelocities[index];

        if (trueGroundY != -999f)
        {
            float yDiff = trueGroundY - currentPos.y;
            if (yDiff > 0.1f)
            {
                currentPos.y += math.min(yDiff, currentSpeed * 1.5f * dt);
                currentYVel = 0f;
            }
            else if (yDiff < -0.1f)
            {
                currentYVel -= Gravity * dt;
                float nextY = currentPos.y + currentYVel * dt;
                if (nextY < trueGroundY) { currentPos.y = trueGroundY; currentYVel = 0f; }
                else currentPos.y = nextY;
            }
            else
            {
                currentPos.y = trueGroundY;
                currentYVel = 0f;
            }
        }
        else
        {
            currentYVel -= Gravity * dt;
            currentPos.y += currentYVel * dt;
        }

        // ── 6. XZ 이동 적용 ───────────────────────────────────────────────────
        currentPos.x += xzVelocity.x * dt;
        currentPos.z += xzVelocity.z * dt;

        // ── 7. 회전 ───────────────────────────────────────────────────────────
        if (desiredDir.sqrMagnitude > 0.001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(desiredDir);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, RotationSpeed * dt);
        }

        YVelocities[index]  = currentYVel;
        Rotations[index]    = transform.rotation;
        transform.position  = currentPos;   // CurrentPositions는 다음 프레임 CopyPositionsJob이 갱신
    }

    // ─── 헬퍼 ─────────────────────────────────────────────────────────────────

    private Vector3 SampleFlowField(int ownerIdx, int flatIdx)
    {
        if (ownerIdx == 0 && FlowField0.IsCreated) return FlowField0[flatIdx];
        if (ownerIdx == 1 && FlowField1.IsCreated) return FlowField1[flatIdx];
        if (ownerIdx == 2 && FlowField2.IsCreated) return FlowField2[flatIdx];
        if (ownerIdx == 3 && FlowField3.IsCreated) return FlowField3[flatIdx];
        return Vector3.zero;
    }

    private Vector3 ComputeSeparation(int index, Vector3 currentPos)
    {
        Vector3 separation = Vector3.zero;
        float sepRadSqr = SeparationRadius * SeparationRadius;

        for (int cx = -1; cx <= 1; cx++)
        {
            for (int cz = -1; cz <= 1; cz++)
            {
                int hash = HashPositionsJob.GetGridHash(
                    new float3(currentPos.x + cx * CellSize, 0f, currentPos.z + cz * CellSize), CellSize);

                if (!SpatialGrid.TryGetFirstValue(hash, out int otherIdx, out var it)) continue;

                int checks = 0;
                do
                {
                    if (otherIdx == index) continue;
                    if (++checks > 12) break;

                    Vector3 diff = currentPos - CurrentPositions[otherIdx];
                    float sqrXZ = diff.x * diff.x + diff.z * diff.z;

                    if (sqrXZ < sepRadSqr && math.abs(diff.y) < 1.5f && sqrXZ > 0.001f)
                    {
                        float strength = (sepRadSqr - sqrXZ) / sepRadSqr;
                        separation.x += diff.x * strength;
                        separation.z += diff.z * strength;
                    }
                }
                while (SpatialGrid.TryGetNextValue(out otherIdx, ref it));
            }
        }

        return separation;
    }

    private void ApplyGravityOnly(int index, ref Vector3 pos, float dt)
    {
        RaycastHit hit = RaycastHits[index * 2];
        float groundY = hit.colliderEntityId != 0 ? hit.point.y : -999f;
        float yVel = YVelocities[index];

        if (groundY != -999f)
        {
            pos.y = groundY;
            yVel = 0f;
        }
        else
        {
            yVel -= Gravity * dt;
            pos.y += yVel * dt;
        }

        YVelocities[index] = yVel;
    }
}
