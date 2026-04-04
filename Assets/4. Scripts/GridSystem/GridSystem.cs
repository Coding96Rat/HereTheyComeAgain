using System.Collections.Generic;
using UnityEngine;

public class GridSystem : MonoBehaviour
{
    [Header("Grid Settings")]
    [SerializeField] private Vector3 _middlePoint = new Vector3(0, 0, 0); // �߾� �������� ����
    [SerializeField] private int _rows = 1500;
    [SerializeField] private int _columns = 1500;
    [SerializeField] private int _cellSize = 1;

    [Header("Bake Settings")]
    [Tooltip("������ �ν��� ���̾ �����ϼ��� (��: Wall)")]
    public LayerMask obstacleLayer;

    [Header("Debug Settings")]
    public bool showGizmos = true;

    public static GridSystem Instance { get; private set; }

    public Vector3 MiddlePoint => _middlePoint;
    public int Rows => _rows;
    public int Columns => _columns;
    public int CellSize => _cellSize;

    // �ٽ� ����ȭ: 225�� ���� Ŭ������ �����ϴ� ���, ���ŵ� ĭ�� '�ε��� ��ȣ'�� ������ �����մϴ�.
    [HideInInspector]
    [SerializeField] private List<int> _occupiedIndices = new List<int>();

    // ���� ����(Play) �߿��� �����Ǵ� �ʰ淮 2���� �޸� ��
    private bool[,] _runtimeGrid;

    void Awake()
    {
        Instance = this;
        InitializeRuntimeGrid();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
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
        Debug.Log($"[GridSystem] �� ��ĵ �Ϸ�. �� {_columns * _rows}ĭ �� {hitCount}ĭ�� ������ �νĵǾ� ����Ǿ����ϴ�.");
    }

    public void ClearGrid()
    {
        _occupiedIndices.Clear();
        _runtimeGrid = null;
        Debug.Log("[GridSystem] ��� ���� �����Ͱ� �����Ǿ����ϴ�.");
    }

    // ���������� ĳ�� �뵵�� ���� �ϴ� ��ǥ�� ����ؼ� �����մϴ�.
    public Vector3 GetBottomLeft()
    {
        float halfWidth = (_columns * _cellSize) / 2f;
        float halfHeight = (_rows * _cellSize) / 2f;
        return _middlePoint - new Vector3(halfWidth, 0, halfHeight);
    }

    // �߾� ����(MiddlePoint)�� �������� �� ���� ��ǥ ���
    public Vector3 GetWorldPosition(int x, int z)
    {
        return new Vector3(x, 0, z) * _cellSize + GetBottomLeft();
    }

    // ���� ��ǥ�� �ٽ� �迭�� x, z �ε����� ��ȯ
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

    /// <summary>
    /// 런타임에 특정 셀의 점유 상태를 변경한다.
    /// BuildingSystem의 SyncList 변경 시 호출하거나, 지역 클라이언트 즉시 반영에 사용.
    /// </summary>
    public void SetOccupied(int x, int z, bool occupied)
    {
        if (_runtimeGrid == null) InitializeRuntimeGrid();
        if (x >= 0 && z >= 0 && x < _columns && z < _rows)
            _runtimeGrid[x, z] = occupied;
    }

    private void OnDrawGizmosSelected()
    {
        if (!showGizmos) return;

        // ���� �׸��带 �׸��� �ʰ�, �߾� ������ �������� ������ ��ü ���簢�� �ܰ����� ǥ��
        Gizmos.color = Color.cyan; // ���� �� �絵�� ���� ����
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