using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FlowFieldSystem : MonoBehaviour
{
    private GridSystem _gridSystem;
    private int _cols, _rows;

    private List<Vector3[,]> _playerFlowFields = new List<Vector3[,]>();
    private int[,] _integrationField;
    private bool _isUpdating = false;

    private static readonly Vector2Int[] neighbors = {
        Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right,
        new Vector2Int(1, 1), new Vector2Int(1, -1), new Vector2Int(-1, 1), new Vector2Int(-1, -1)
    };
    private static readonly int[] costs = { 10, 10, 10, 10, 14, 14, 14, 14 };

    private List<Vector2Int>[] _touchedCellsPerPlayer;
    private Queue<Vector2Int> _bfsQueue = new Queue<Vector2Int>(5000);

    private void Awake()
    {
        _gridSystem = FindFirstObjectByType<GridSystem>();
    }

    public void Initialize(int maxPlayers = 4)
    {
        if (_gridSystem == null) return;
        _cols = _gridSystem.Columns;
        _rows = _gridSystem.Rows;

        _integrationField = new int[_cols, _rows];
        _touchedCellsPerPlayer = new List<Vector2Int>[maxPlayers];

        for (int x = 0; x < _cols; x++)
        {
            for (int z = 0; z < _rows; z++) _integrationField[x, z] = int.MaxValue;
        }

        for (int i = 0; i < maxPlayers; i++)
        {
            _playerFlowFields.Add(new Vector3[_cols, _rows]);
            _touchedCellsPerPlayer[i] = new List<Vector2Int>(10000);
        }
    }

    public void StartUpdatingFlowFields(List<Transform> activePlayers)
    {
        if (!_isUpdating)
        {
            _isUpdating = true;
            StartCoroutine(UpdateFlowFieldsRoutine(activePlayers));
        }
    }

    private IEnumerator UpdateFlowFieldsRoutine(List<Transform> activePlayers)
    {
        while (_isUpdating)
        {
            for (int i = 0; i < activePlayers.Count; i++)
            {
                if (activePlayers[i] == null) continue;
                _gridSystem.GetGridPosition(activePlayers[i].position, out int pX, out int pZ);

                // 플레이어 1명씩 완전히 독립된 코루틴으로 계산을 위임합니다.
                yield return StartCoroutine(GenerateFlowFieldForPlayerRoutine(new Vector2Int(pX, pZ), i));
            }
            // [유저 아이디어 적용] 0.25초가 아닌 2.5초 간격으로 플레이어의 '과거 스냅샷 좌표'를 갱신
            yield return new WaitForSeconds(2.5f);
        }
    }

    // 완전히 비동기로 작동하여 렉을 유발하지 않는 기적의 길찾기 코루틴
    private IEnumerator GenerateFlowFieldForPlayerRoutine(Vector2Int targetPos, int playerIndex)
    {
        List<Vector2Int> touchedCells = _touchedCellsPerPlayer[playerIndex];

        for (int i = 0; i < touchedCells.Count; i++)
        {
            _integrationField[touchedCells[i].x, touchedCells[i].y] = int.MaxValue;
        }
        touchedCells.Clear();
        _bfsQueue.Clear();

        if (targetPos.x >= 0 && targetPos.x < _cols && targetPos.y >= 0 && targetPos.y < _rows)
        {
            _integrationField[targetPos.x, targetPos.y] = 0;
            _bfsQueue.Enqueue(targetPos);
            touchedCells.Add(targetPos);
        }

        int iterationsThisFrame = 0;

        // 1. 거리 계산 (프레임 쪼개기)
        while (_bfsQueue.Count > 0)
        {
            Vector2Int curr = _bfsQueue.Dequeue();
            int currentCost = _integrationField[curr.x, curr.y];

            for (int i = 0; i < 8; i++)
            {
                Vector2Int next = curr + neighbors[i];

                if (next.x < 0 || next.x >= _cols || next.y < 0 || next.y >= _rows) continue;
                if (_gridSystem.IsOccupied(next.x, next.y)) continue;
                if (i >= 4 && _gridSystem.IsOccupied(curr.x, next.y) && _gridSystem.IsOccupied(next.x, curr.y)) continue;

                int newCost = currentCost + costs[i];
                if (newCost < _integrationField[next.x, next.y])
                {
                    if (_integrationField[next.x, next.y] == int.MaxValue) touchedCells.Add(next);
                    _integrationField[next.x, next.y] = newCost;
                    _bfsQueue.Enqueue(next);
                }
            }

            iterationsThisFrame++;
            // 핵심 방어막: 1프레임에 3000칸 이상 계산했으면 무조건 쉬었다가 다음 프레임에 진행 (렉 방지)
            if (iterationsThisFrame > 3000)
            {
                iterationsThisFrame = 0;
                yield return null;
            }
        }

        iterationsThisFrame = 0;

        // 2. 화살표 방향 설정 (프레임 쪼개기)
        for (int j = 0; j < touchedCells.Count; j++)
        {
            Vector2Int cell = touchedCells[j];
            int bestCost = _integrationField[cell.x, cell.y];
            Vector3 bestDir = Vector3.zero;

            for (int i = 0; i < 8; i++)
            {
                Vector2Int n = cell + neighbors[i];
                if (n.x < 0 || n.x >= _cols || n.y < 0 || n.y >= _rows) continue;

                if (_integrationField[n.x, n.y] < bestCost)
                {
                    bestCost = _integrationField[n.x, n.y];
                    bestDir = new Vector3(neighbors[i].x, 0, neighbors[i].y);
                }
            }
            _playerFlowFields[playerIndex][cell.x, cell.y] = bestDir.normalized;

            iterationsThisFrame++;
            // 여기도 마찬가지로 5000칸마다 휴식
            if (iterationsThisFrame > 5000)
            {
                iterationsThisFrame = 0;
                yield return null;
            }
        }
    }

    public Vector3 GetFlowDirection(int targetIndex, Vector3 worldPos)
    {
        if (targetIndex < 0 || targetIndex >= _playerFlowFields.Count) return Vector3.zero;
        _gridSystem.GetGridPosition(worldPos, out int x, out int z);
        if (x >= 0 && x < _cols && z >= 0 && z < _rows) return _playerFlowFields[targetIndex][x, z];
        return Vector3.zero;
    }
}