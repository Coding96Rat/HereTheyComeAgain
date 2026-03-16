using System.Collections.Generic;
using UnityEngine;

public class GridSystem : MonoBehaviour
{
    [Header("Grid Settings")]
    [SerializeField] private Vector3 _middlePoint = new Vector3(0, 0, 0); // 중앙 기점으로 변경
    [SerializeField] private int _rows = 1500;
    [SerializeField] private int _columns = 1500;
    [SerializeField] private int _cellSize = 1;

    [Header("Bake Settings")]
    [Tooltip("벽으로 인식할 레이어를 선택하세요 (예: Wall)")]
    public LayerMask obstacleLayer;

    [Header("Debug Settings")]
    public bool showGizmos = true;

    public Vector3 MiddlePoint => _middlePoint;
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

        for (int x = 0; x < _columns; x++)
        {
            for (int z = 0; z < _rows; z++)
            {
                Vector3 cellCenter = GetWorldPosition(x, z) + new Vector3(_cellSize / 2f, 0, _cellSize / 2f);
                Vector3 rayStart = cellCenter + Vector3.up * 10f;

                if (Physics.Raycast(rayStart, Vector3.down, 20f, obstacleLayer))
                {
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

    // 내부적으로 캐싱 용도로 좌측 하단 좌표를 계산해서 리턴합니다.
    public Vector3 GetBottomLeft()
    {
        float halfWidth = (_columns * _cellSize) / 2f;
        float halfHeight = (_rows * _cellSize) / 2f;
        return _middlePoint - new Vector3(halfWidth, 0, halfHeight);
    }

    // 중앙 기점(MiddlePoint)을 기준으로 한 월드 좌표 계산
    public Vector3 GetWorldPosition(int x, int z)
    {
        return new Vector3(x, 0, z) * _cellSize + GetBottomLeft();
    }

    // 월드 좌표를 다시 배열의 x, z 인덱스로 변환
    public void GetGridPosition(Vector3 worldPosition, out int x, out int z)
    {
        Vector3 bl = GetBottomLeft();
        x = Mathf.FloorToInt((worldPosition.x - bl.x) / _cellSize);
        z = Mathf.FloorToInt((worldPosition.z - bl.z) / _cellSize);
    }

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

        return true;
    }

    private void OnDrawGizmosSelected()
    {
        if (!showGizmos) return;

        // 개별 그리드를 그리지 않고, 중앙 기점을 바탕으로 형성된 전체 직사각형 외곽선만 표시
        Gizmos.color = Color.cyan; // 눈에 잘 띄도록 색상 변경
        Vector3 bl = GetBottomLeft();
        Vector3 tl = bl + new Vector3(0, 0, _rows * _cellSize);
        Vector3 br = bl + new Vector3(_columns * _cellSize, 0, 0);
        Vector3 tr = bl + new Vector3(_columns * _cellSize, 0, _rows * _cellSize);

        Gizmos.DrawLine(bl, tl);
        Gizmos.DrawLine(tl, tr);
        Gizmos.DrawLine(tr, br);
        Gizmos.DrawLine(br, bl);
    }
}