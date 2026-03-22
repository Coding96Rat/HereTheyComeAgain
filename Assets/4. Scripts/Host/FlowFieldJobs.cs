using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// 1. 통합 필드(거리 지도)를 그리는 초고속 BFS 연산 Job
[BurstCompile]
public struct IntegrationFieldJob : IJob
{
    [ReadOnly] public NativeArray<byte> CostField;
    public NativeArray<int> IntegrationField;

    [ReadOnly] public int GridCols;
    [ReadOnly] public int GridRows;
    [ReadOnly] public int2 TargetCell;

    public void Execute()
    {
        // 1. 전체 지도 초기화 (병렬 처리가 필요 없을 만큼 Burst에서 순식간에 끝남)
        for (int i = 0; i < IntegrationField.Length; i++)
        {
            IntegrationField[i] = int.MaxValue;
        }

        // 타겟이 맵 밖에 있으면 종료
        if (TargetCell.x < 0 || TargetCell.x >= GridCols || TargetCell.y < 0 || TargetCell.y >= GridRows) return;

        // [핵심] C# Queue 대신 메모리 할당 없는 NativeQueue 사용
        NativeQueue<int2> queue = new NativeQueue<int2>(Allocator.Temp);

        int targetIdx = TargetCell.y * GridCols + TargetCell.x;
        IntegrationField[targetIdx] = 0;
        queue.Enqueue(TargetCell);

        // 8방향 이웃 탐색 세팅
        NativeArray<int2> neighbors = new NativeArray<int2>(8, Allocator.Temp);
        neighbors[0] = new int2(0, 1);   // 상
        neighbors[1] = new int2(0, -1);  // 하
        neighbors[2] = new int2(-1, 0);  // 좌
        neighbors[3] = new int2(1, 0);   // 우
        neighbors[4] = new int2(1, 1);   // 우상
        neighbors[5] = new int2(1, -1);  // 우하
        neighbors[6] = new int2(-1, 1);  // 좌상
        neighbors[7] = new int2(-1, -1); // 좌하

        NativeArray<int> costs = new NativeArray<int>(8, Allocator.Temp);
        costs[0] = 10; costs[1] = 10; costs[2] = 10; costs[3] = 10;
        costs[4] = 14; costs[5] = 14; costs[6] = 14; costs[7] = 14;

        while (queue.TryDequeue(out int2 curr))
        {
            int currIdx = curr.y * GridCols + curr.x;
            int currentCost = IntegrationField[currIdx];

            for (int i = 0; i < 8; i++)
            {
                int2 n = curr + neighbors[i];
                if (n.x < 0 || n.x >= GridCols || n.y < 0 || n.y >= GridRows) continue;

                int nIdx = n.y * GridCols + n.x;
                byte nCostField = CostField[nIdx];
                if (nCostField == 255) continue; // 장애물 통과 불가

                // 대각선 이동 시 모서리 뚫기 방지
                if (i >= 4)
                {
                    if (CostField[curr.y * GridCols + n.x] == 255 || CostField[n.y * GridCols + curr.x] == 255)
                        continue;
                }

                // [버그 수정] 기존 코드에선 CostField(벽 근처 가중치)를 더하지 않아 벽에 비비는 현상이 있었습니다.
                // 이제 nCostField 값을 더해 벽에서 떨어져서 걷도록 유도합니다!
                int newCost = currentCost + costs[i] + (nCostField * 5);

                if (newCost < IntegrationField[nIdx])
                {
                    IntegrationField[nIdx] = newCost;
                    queue.Enqueue(n);
                }
            }
        }

        queue.Dispose();
        neighbors.Dispose();
        costs.Dispose();
    }
}

// 💡 2. 8방향의 한계를 부수고 타겟을 향해 완벽한 곡선 화살표를 꽂아넣는 Job
[BurstCompile]
public struct VectorFieldJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<int> IntegrationField;
    [ReadOnly] public NativeArray<byte> CostField;
    [WriteOnly] public NativeArray<Vector3> FlowField;

    [ReadOnly] public int GridCols;
    [ReadOnly] public int GridRows;

    // 💡 [추가됨] 완벽한 직선 방향을 구하기 위한 맵 정보와 타겟 위치
    [ReadOnly] public Vector3 BottomLeft;
    [ReadOnly] public float AiCellSize;
    [ReadOnly] public Vector3 TargetPos;

    public void Execute(int index)
    {
        if (CostField[index] == 255)
        {
            FlowField[index] = Vector3.zero;
            return;
        }

        int cx = index % GridCols;
        int cz = index / GridCols;
        int currentCost = IntegrationField[index];

        Vector3 bfsDir = Vector3.zero;

        // 1. BFS 기반의 기본 우회 방향 계산 (기존 로직)
        AddDirection(cx, cz, 0, 1, currentCost, ref bfsDir);
        AddDirection(cx, cz, 0, -1, currentCost, ref bfsDir);
        AddDirection(cx, cz, -1, 0, currentCost, ref bfsDir);
        AddDirection(cx, cz, 1, 0, currentCost, ref bfsDir);
        AddDirection(cx, cz, 1, 1, currentCost, ref bfsDir);
        AddDirection(cx, cz, 1, -1, currentCost, ref bfsDir);
        AddDirection(cx, cz, -1, 1, currentCost, ref bfsDir);
        AddDirection(cx, cz, -1, -1, currentCost, ref bfsDir);

        bfsDir = math.lengthsq(bfsDir) > 0.001f ? (Vector3)math.normalize(bfsDir) : Vector3.zero;

        // 💡 2. [핵심] 유클리드 직선 방향 융합 (Euclidean Blending)
        if (bfsDir.sqrMagnitude > 0.001f)
        {
            // 현재 타일의 실제 월드 좌표 계산
            Vector3 cellWorldPos = BottomLeft + new Vector3(cx * AiCellSize + (AiCellSize / 2f), 0, cz * AiCellSize + (AiCellSize / 2f));

            // 타겟을 향하는 완벽한 직선 방향
            Vector3 exactDir = TargetPos - cellWorldPos;
            exactDir.y = 0;
            exactDir = math.normalize(exactDir);

            // BFS가 가리키는 방향과 실제 직선 방향의 유사도(각도) 검사
            float dot = Vector3.Dot(bfsDir, exactDir);

            // 두 방향이 엇비슷하다면 (약 72도 이내 = 앞에 가로막는 큰 벽이 없는 평지 상태)
            if (dot > 0.3f)
            {
                // 각도가 일치할수록 강하게 직선 방향으로 화살표를 꺾어줌 (8방향 고속도로 파괴)
                float blendWeight = math.pow(dot, 2.0f);
                bfsDir = Vector3.Lerp(bfsDir, exactDir, blendWeight * 0.85f);
                bfsDir = math.normalize(bfsDir);
            }
        }

        FlowField[index] = bfsDir;
    }

    private void AddDirection(int cx, int cz, int dx, int dz, int currentCost, ref Vector3 blendedDir)
    {
        int nx = cx + dx;
        int nz = cz + dz;

        if (nx < 0 || nx >= GridCols || nz < 0 || nz >= GridRows) return;

        int nIdx = nz * GridCols + nx;
        if (CostField[nIdx] == 255) return;

        int nCost = IntegrationField[nIdx];

        if (nCost < currentCost)
        {
            float weight = currentCost - nCost;
            Vector3 dir = new Vector3(dx, 0, dz);
            blendedDir += (Vector3)math.normalize(dir) * weight;
        }
    }
}
