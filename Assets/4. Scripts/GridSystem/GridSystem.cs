using System.Collections.Generic;
using UnityEngine;

public class GridSystem : MonoBehaviour
{
    [Header("Grid Settings")]
    [SerializeField] private Vector3 _leftBottomLocation = new Vector3(0, 0, 0);
    [SerializeField] private int _rows = 1500;
    [SerializeField] private int _columns = 1500;
    [SerializeField] private int _cellSize = 1;

    [Header("Bake Settings")]
    [Tooltip("벽으로 인식할 레이어를 선택하세요 (예: Wall)")]
    public LayerMask obstacleLayer;

    [Header("Debug Settings")]
    public bool showGizmos = true;

    public Vector3 LeftBottomLocation => _leftBottomLocation;
    public int Rows => _rows;
    public int Columns => _columns;
    public int CellSize => _cellSize;

    // 핵심 최적화: 225만 개의 클래스를 저장하는 대신, 점거된 칸의 '인덱스 번호'만 가볍게 저장합니다.
    [HideInInspector]
    [SerializeField] private List<int> _occupiedIndices = new List<int>();

    // 게임 실행(Play) 중에만 생성되는 초경량 2차원 메모리 맵
    private bool[,] _runtimeGrid;

    void Awake()
    {
        InitializeRuntimeGrid();
    }

    private void InitializeRuntimeGrid()
    {
        _runtimeGrid = new bool[_columns, _rows];

        if (_occupiedIndices != null)
        {
            foreach (int index in _occupiedIndices)
            {
                int x = index % _columns;
                int z = index / _columns;
                if (x >= 0 && x < _columns && z >= 0 && z < _rows)
                {
                    _runtimeGrid[x, z] = true;
                }
            }
        }
    }

    public void BakeGrid()
    {
        _occupiedIndices = new List<int>();
        int hitCount = 0;

        // 베이킹할 때는 어쩔 수 없이 225만 번 검사해야 하지만, 에디터에서 단 한 번만 실행되므로 문제없습니다.
        for (int x = 0; x < _columns; x++)
        {
            for (int z = 0; z < _rows; z++)
            {
                Vector3 cellCenter = GetWorldPosition(x, z) + new Vector3(_cellSize / 2f, 0, _cellSize / 2f);
                Vector3 rayStart = cellCenter + Vector3.up * 10f;

                if (Physics.Raycast(rayStart, Vector3.down, 20f, obstacleLayer))
                {
                    // 2차원 좌표를 1차원 인덱스로 압축하여 저장
                    _occupiedIndices.Add(z * _columns + x);
                    hitCount++;
                }
            }
        }
        Debug.Log($"[GridSystem] 맵 스캔 완료. 총 {_columns * _rows}칸 중 {hitCount}칸이 벽으로 인식되어 저장되었습니다.");
    }

    public void ClearGrid()
    {
        _occupiedIndices.Clear();
        _runtimeGrid = null;
        Debug.Log("[GridSystem] 모든 점거 데이터가 삭제되었습니다.");
    }

    public Vector3 GetWorldPosition(int x, int z)
    {
        return new Vector3(x, 0, z) * _cellSize + _leftBottomLocation;
    }

    public void GetGridPosition(Vector3 worldPosition, out int x, out int z)
    {
        x = Mathf.FloorToInt((worldPosition.x - _leftBottomLocation.x) / _cellSize);
        z = Mathf.FloorToInt((worldPosition.z - _leftBottomLocation.z) / _cellSize);
    }

    // 건축 시스템에서 이 자리에 건물을 지을 수 있는지 초고속으로 확인하는 함수
    public bool IsOccupied(int x, int z)
    {
        if (_runtimeGrid == null && !Application.isPlaying)
        {
            InitializeRuntimeGrid();
        }

        if (x >= 0 && z >= 0 && x < _columns && z < _rows)
        {
            return _runtimeGrid[x, z];
        }

        return true; // 맵 밖으로 벗어났다면 무조건 건축 불가(점거됨) 처리
    }

    //// 하위 호환성을 위해 남겨둔 함수 (이후 로직에서는 IsOccupied 사용을 권장합니다)
    //public GridNode GetNode(int x, int z)
    //{
    //    if (x >= 0 && z >= 0 && x < _columns && z < _rows)
    //    {
    //        GridNode node = new GridNode(x, z);
    //        node.IsOccupied = IsOccupied(x, z);
    //        return node;
    //    }
    //    return null;
    //}

    private void OnDrawGizmosSelected()
    {
        if (!showGizmos || _occupiedIndices == null) return;

        // 225만 번 루프 대신, 저장된 극소수의 인덱스 배열만 빠르게 순회하여 기즈모를 그립니다. (버벅임 완벽 해소)
        Gizmos.color = new Color(1, 0, 0, 0.5f);
        foreach (int index in _occupiedIndices)
        {
            int x = index % _columns;
            int z = index / _columns;

            Vector3 p0 = GetWorldPosition(x, z);
            Vector3 center = p0 + new Vector3(_cellSize / 2f, 0.1f, _cellSize / 2f);
            Gizmos.DrawCube(center, new Vector3(_cellSize, 0.2f, _cellSize));
        }

        // 전체 맵의 바깥쪽 테두리만 하얀 선으로 표시합니다.
        Gizmos.color = Color.white;
        Vector3 bl = GetWorldPosition(0, 0);
        Vector3 tl = GetWorldPosition(0, _rows);
        Vector3 br = GetWorldPosition(_columns, 0);
        Vector3 tr = GetWorldPosition(_columns, _rows);

        Gizmos.DrawLine(bl, tl);
        Gizmos.DrawLine(tl, tr);
        Gizmos.DrawLine(tr, br);
        Gizmos.DrawLine(br, bl);
    }
}