using UnityEngine;

public class GridSystem : MonoBehaviour
{
    [Header("Grid Settings")]
    [SerializeField] private Vector3 _leftBottomLocation = new Vector3(0, 0, 0);
    [SerializeField] private int _rows = 10;
    [SerializeField] private int _columns = 10;
    [SerializeField] private int _cellSize = 1;

    //  외부에서 값을 읽어갈 수 있도록 프로퍼티 추가
    public Vector3 LeftBottomLocation => _leftBottomLocation;
    public int Rows => _rows;
    public int Columns => _columns;
    public int CellSize => _cellSize;
    

    // 실제 오브젝트 대신 데이터를 들고 있는 2차원 배열
    private GridNode[,] _gridArray;

    void Start()
    {
        GenerateGrid();
    }

    void GenerateGrid()
    {
        // 메모리 상에 2차원 배열 할당
        _gridArray = new GridNode[_columns, _rows];

        for (int x =0; x <_columns; x++)
        {
            for(int z = 0; z < _rows; z++)
            {
                // 오브젝트 생성 없이 데이터만 배열에 채워 넣기
                _gridArray[x, z] = new GridNode(x, z);
            }
        }
    }

    // 핵심 기능 1 : 그리드 좌표를 월드 좌표로 변환
    public Vector3 GetWorldPosition(int x, int z)
    {
        return new Vector3(x, 0, z) * _cellSize + _leftBottomLocation;
    }

    // 핵심 기능 2 : 월드 좌표를 그리드 좌표로 변환
    public void GetGridPosition(Vector3 worldPosition, out int x, out int z)
    {
        // 시작점을 빼주고 셀 크기로 나눈 뒤 내림(Floor) 처리하여 정확한 칸의 인덱스를 구합니다.
        x = Mathf.FloorToInt((worldPosition.x - _leftBottomLocation.x) / _cellSize);
        z = Mathf.FloorToInt((worldPosition.z - _leftBottomLocation.z) / _cellSize);
    }

    // 에디터에서 그리드가 어떻게 깔렸는지 시각적으로 확인하기 위한 기즈모 그리기
    private void OnDrawGizmos()
    {
        if (!Application.isPlaying && _gridArray == null) return;

        Gizmos.color = Color.white;
        for (int x = 0; x < _columns; x++)
        {
            for (int z = 0; z < _rows; z++)
            {
                // 각 칸의 테두리를 그립니다. 실제 오브젝트가 아니라 에디터에서만 보이는 선입니다.
                Vector3 p0 = GetWorldPosition(x, z);
                Vector3 p1 = GetWorldPosition(x, z + 1);
                Vector3 p2 = GetWorldPosition(x + 1, z);

                Gizmos.DrawLine(p0, p1);
                Gizmos.DrawLine(p0, p2);
            }
        }
        // 가장자리 닫기
        Gizmos.DrawLine(GetWorldPosition(0, _rows), GetWorldPosition(_columns, _rows));
        Gizmos.DrawLine(GetWorldPosition(_columns, 0), GetWorldPosition(_columns, _rows));
    }
}
