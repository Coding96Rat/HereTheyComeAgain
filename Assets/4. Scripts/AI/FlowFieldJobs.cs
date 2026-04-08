using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

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
        for (int i = 0; i < IntegrationField.Length; i++)
        {
            IntegrationField[i] = int.MaxValue;
        }

        if (TargetCell.x < 0 || TargetCell.x >= GridCols || TargetCell.y < 0 || TargetCell.y >= GridRows) return;

        NativeQueue<int2> queue = new NativeQueue<int2>(Allocator.Temp);
        int targetIdx = TargetCell.y * GridCols + TargetCell.x;
        IntegrationField[targetIdx] = 0;

        // 구조물 타겟(cost=255)이면 연결된 모든 구조물 셀을 flood-fill로 탐색해
        // 전부 integration=0으로 seed — 어느 방향에서 오는 적도 동등한 거리로 접근 가능
        if (CostField[targetIdx] == 255)
        {
            NativeQueue<int2> structureFill = new NativeQueue<int2>(Allocator.Temp);
            structureFill.Enqueue(TargetCell);

            while (structureFill.TryDequeue(out int2 curr))
            {
                queue.Enqueue(curr); // 메인 BFS에 추가 — 주변 walkable 셀로 전파
                ExpandThroughStructure(curr, 0, 1, ref structureFill);
                ExpandThroughStructure(curr, 0, -1, ref structureFill);
                ExpandThroughStructure(curr, -1, 0, ref structureFill);
                ExpandThroughStructure(curr, 1, 0, ref structureFill);
            }
            structureFill.Dispose();
        }
        else
        {
            queue.Enqueue(TargetCell);
        }

        while (queue.TryDequeue(out int2 curr))
        {
            int currentCost = IntegrationField[curr.y * GridCols + curr.x];

            ProcessNeighbor(curr, 0, 1, 10, currentCost, ref queue);   // 상
            ProcessNeighbor(curr, 0, -1, 10, currentCost, ref queue);  // 하
            ProcessNeighbor(curr, -1, 0, 10, currentCost, ref queue);  // 좌
            ProcessNeighbor(curr, 1, 0, 10, currentCost, ref queue);   // 우
            ProcessNeighbor(curr, 1, 1, 14, currentCost, ref queue);   // 우상
            ProcessNeighbor(curr, 1, -1, 14, currentCost, ref queue);  // 우하
            ProcessNeighbor(curr, -1, 1, 14, currentCost, ref queue);  // 좌상
            ProcessNeighbor(curr, -1, -1, 14, currentCost, ref queue); // 좌하
        }
        queue.Dispose();
    }

    private void ExpandThroughStructure(int2 curr, int dx, int dy, ref NativeQueue<int2> structureQueue)
    {
        int nx = curr.x + dx;
        int ny = curr.y + dy;
        if (nx < 0 || nx >= GridCols || ny < 0 || ny >= GridRows) return;
        int nIdx = ny * GridCols + nx;
        if (CostField[nIdx] != 255) return;           // 구조물 셀만 탐색
        if (IntegrationField[nIdx] != int.MaxValue) return; // 이미 방문
        IntegrationField[nIdx] = 0;
        structureQueue.Enqueue(new int2(nx, ny));
    }

    private void ProcessNeighbor(int2 curr, int dx, int dy, int moveCost, int currentCost, ref NativeQueue<int2> queue)
    {
        int nx = curr.x + dx;
        int ny = curr.y + dy;
        if (nx < 0 || nx >= GridCols || ny < 0 || ny >= GridRows) return;

        int nIdx = ny * GridCols + nx;
        byte nCostField = CostField[nIdx];
        if (nCostField == 255) return;

        if (dx != 0 && dy != 0)
        {
            if (CostField[curr.y * GridCols + nx] == 255 || CostField[ny * GridCols + curr.x] == 255) return;
        }

        // 언덕(Cost)이 높을수록 가중치를 부여해 우회 경로를 생성
        int newCost = currentCost + moveCost + (nCostField * 5);

        if (newCost < IntegrationField[nIdx])
        {
            IntegrationField[nIdx] = newCost;
            queue.Enqueue(new int2(nx, ny));
        }
    }
}

[BurstCompile]
public struct VectorFieldJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<int> IntegrationField;
    [ReadOnly] public NativeArray<byte> CostField;
    [WriteOnly] public NativeArray<Vector3> FlowField;

    [ReadOnly] public int GridCols;
    [ReadOnly] public int GridRows;

    [ReadOnly] public Vector3 BottomLeft;
    [ReadOnly] public float AiCellSize;
    [ReadOnly] public Vector3 TargetPos;

    public void Execute(int index)
    {
        int cx = index % GridCols;
        int cz = index / GridCols;
        int currentCost = IntegrationField[index];
        Vector3 bfsDir = Vector3.zero;

        // 벽 탈출 구명줄 로직
        if (CostField[index] == 255 || currentCost == int.MaxValue)
        {
            int minCost = int.MaxValue;
            AddEscapeDirection(cx, cz, 0, 1, ref minCost, ref bfsDir);
            AddEscapeDirection(cx, cz, 0, -1, ref minCost, ref bfsDir);
            AddEscapeDirection(cx, cz, -1, 0, ref minCost, ref bfsDir);
            AddEscapeDirection(cx, cz, 1, 0, ref minCost, ref bfsDir);

            FlowField[index] = math.lengthsq(bfsDir) > 0.001f ? (Vector3)math.normalize(bfsDir) : Vector3.zero;
            return;
        }

        AddDirection(cx, cz, 0, 1, currentCost, ref bfsDir);
        AddDirection(cx, cz, 0, -1, currentCost, ref bfsDir);
        AddDirection(cx, cz, -1, 0, currentCost, ref bfsDir);
        AddDirection(cx, cz, 1, 0, currentCost, ref bfsDir);
        AddDirection(cx, cz, 1, 1, currentCost, ref bfsDir);
        AddDirection(cx, cz, 1, -1, currentCost, ref bfsDir);
        AddDirection(cx, cz, -1, 1, currentCost, ref bfsDir);
        AddDirection(cx, cz, -1, -1, currentCost, ref bfsDir);

        bfsDir = math.lengthsq(bfsDir) > 0.001f ? (Vector3)math.normalize(bfsDir) : Vector3.zero;

        // 💡 [복구됨] 언덕 돌진 방지 및 평지 직진 융합 로직
        if (bfsDir.sqrMagnitude > 0.001f)
        {
            Vector3 cellWorldPos = BottomLeft + new Vector3(cx * AiCellSize + (AiCellSize / 2f), 0, cz * AiCellSize + (AiCellSize / 2f));

            Vector3 exactDir = TargetPos - cellWorldPos;
            exactDir.y = 0;
            exactDir = math.normalize(exactDir);

            float dot = Vector3.Dot(bfsDir, exactDir);

            // Cost가 10 미만인 "평지"에서만 타겟을 향해 직선 블렌딩 허용
            // 언덕에서는 블렌딩을 끄고 순수하게 우회 화살표만 따름
            if (dot > 0.3f && CostField[index] < 10)
            {
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

    private void AddEscapeDirection(int cx, int cz, int dx, int dz, ref int minCost, ref Vector3 escapeDir)
    {
        int nx = cx + dx;
        int nz = cz + dz;
        if (nx < 0 || nx >= GridCols || nz < 0 || nz >= GridRows) return;

        int nIdx = nz * GridCols + nx;
        if (CostField[nIdx] != 255 && IntegrationField[nIdx] < minCost)
        {
            minCost = IntegrationField[nIdx];
            escapeDir = new Vector3(dx, 0, dz);
        }
    }
}