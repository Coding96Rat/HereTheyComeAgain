using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;

public class FlowFieldSystem : MonoBehaviour
{
    [Header("References")]
    private GridSystem _gridSystem;

    [Header("AI Flow Field Settings")]
    public float aiCellSize = 2f;

    [Header("Physics Scan Settings (NavMesh 대체)")]
    public LayerMask walkableLayer;
    public LayerMask obstacleLayer;
    public float maxWalkableSlope = 45f;

    [Header("Debug & Gizmos")]
    public bool showObstacleBlocks = true; // 에디터에서 벽/장애물 붉은색 표시
    public bool showFlowArrows = true;     // 플레이 시 화살표 표시 (렉 유발 가능하므로 디버그용으로만 켜세요)
    [Range(0, 3)] public int debugPlayerIndex = 0; // 화살표를 볼 플레이어 인덱스

    // 💡 [에디터 저장용 데이터] NativeArray는 에디터에서 날아가기 때문에 일반 배열로 저장합니다.
    [SerializeField, HideInInspector] private byte[] _savedCostField;
    [SerializeField, HideInInspector] private int _savedCols, _savedRows;
    [SerializeField, HideInInspector] private Vector3 _savedBottomLeft;

    private int _cols, _rows;
    private Vector3 _bottomLeft;

    private NativeArray<byte> _costField;
    private NativeArray<int>[] _integrationFields;
    public NativeArray<Vector3>[] NativeFlowFields;

    private List<Transform> _targetPlayers;

    private void Awake()
    {
        _gridSystem = FindFirstObjectByType<GridSystem>();
    }

    private void OnDestroy()
    {
        if (_costField.IsCreated) _costField.Dispose();

        if (NativeFlowFields != null)
        {
            for (int i = 0; i < NativeFlowFields.Length; i++)
            {
                if (NativeFlowFields[i].IsCreated) NativeFlowFields[i].Dispose();
                if (_integrationFields != null && _integrationFields[i].IsCreated) _integrationFields[i].Dispose();
            }
        }
    }

    public void Initialize(int maxPlayers = 4)
    {
        if (_gridSystem == null) _gridSystem = FindFirstObjectByType<GridSystem>();

        // 💡 에디터에서 구워둔 데이터가 있으면 그대로 로드합니다 (로딩 시간 0초!)
        if (_savedCostField != null && _savedCostField.Length > 0)
        {
            _cols = _savedCols;
            _rows = _savedRows;
            _bottomLeft = _savedBottomLeft;
        }
        else
        {
            float mapWidth = _gridSystem.Columns * _gridSystem.CellSize;
            float mapHeight = _gridSystem.Rows * _gridSystem.CellSize;
            _bottomLeft = _gridSystem.GetBottomLeft();
            _cols = Mathf.CeilToInt(mapWidth / aiCellSize);
            _rows = Mathf.CeilToInt(mapHeight / aiCellSize);
        }

        int totalCells = _cols * _rows;

        _costField = new NativeArray<byte>(totalCells, Allocator.Persistent);
        _integrationFields = new NativeArray<int>[maxPlayers];
        NativeFlowFields = new NativeArray<Vector3>[maxPlayers];

        for (int i = 0; i < maxPlayers; i++)
        {
            _integrationFields[i] = new NativeArray<int>(totalCells, Allocator.Persistent);
            NativeFlowFields[i] = new NativeArray<Vector3>(totalCells, Allocator.Persistent);
        }

        // 구워둔 데이터를 NativeArray로 초고속 복사
        if (_savedCostField != null && _savedCostField.Length == totalCells)
        {
            _costField.CopyFrom(_savedCostField);
        }
        else
        {
            // 구워둔 게 없으면 게임 시작할 때 강제로 한 번 굽습니다.
            BakeInEditor();
            _costField.CopyFrom(_savedCostField);
        }
    }

    // =========================================================================
    // 💡 1. 에디터 굽기 기능 (우클릭 메뉴에서 실행)
    // =========================================================================
    [ContextMenu("Bake FFS In Editor (물리 스캔)")]
    public void BakeInEditor()
    {
        if (_gridSystem == null) _gridSystem = FindFirstObjectByType<GridSystem>();
        if (_gridSystem == null) { Debug.LogError("GridSystem을 찾을 수 없습니다."); return; }

        float mapWidth = _gridSystem.Columns * _gridSystem.CellSize;
        float mapHeight = _gridSystem.Rows * _gridSystem.CellSize;
        _savedBottomLeft = _gridSystem.GetBottomLeft();
        _savedCols = Mathf.CeilToInt(mapWidth / aiCellSize);
        _savedRows = Mathf.CeilToInt(mapHeight / aiCellSize);

        int totalCells = _savedCols * _savedRows;
        _savedCostField = new byte[totalCells];

        float rayHeight = _gridSystem.MiddlePoint.y + 100f;

        for (int x = 0; x < _savedCols; x++)
        {
            for (int z = 0; z < _savedRows; z++)
            {
                int flatIndex = z * _savedCols + x;
                Vector3 cellCenter = _savedBottomLeft + new Vector3(x * aiCellSize + (aiCellSize / 2f), 0, z * aiCellSize + (aiCellSize / 2f));
                Vector3 rayOrigin = new Vector3(cellCenter.x, rayHeight, cellCenter.z);

                if (Physics.SphereCast(rayOrigin, aiCellSize * 0.45f, Vector3.down, out RaycastHit hit, 200f, walkableLayer | obstacleLayer))
                {
                    // 💡 수정: 장애물 레이어에 닿았거나 경사가 설정값(45도)보다 크면 무조건 벽(255)
                    float slopeAngle = Vector3.Angle(Vector3.up, hit.normal);
                    bool isObstacleLayer = (obstacleLayer.value & (1 << hit.transform.gameObject.layer)) > 0;

                    if (isObstacleLayer || slopeAngle > maxWalkableSlope)
                    {
                        _savedCostField[flatIndex] = 255;
                        continue;
                    }

                    // 💡 수정: 지형이 너무 급격히 변하는 구간(절벽 끝 등)도 벽으로 간주하여 추락 방지
                    if (Physics.Raycast(hit.point + Vector3.up * 0.1f, Vector3.down, 0.5f) == false)
                    {
                        _savedCostField[flatIndex] = 255;
                        continue;
                    }

                    _savedCostField[flatIndex] = (Physics.CheckSphere(hit.point, 1.2f, obstacleLayer)) ? (byte)5 : (byte)1;
                }
            }
        }
        Debug.Log($"[FFS] 맵 스캔 완료! ({totalCells}칸 구워짐)");
    }

    // =========================================================================
    // 💡 2. 동적 장애물 부분 업데이트 기능 (런타임용)
    // =========================================================================
    /// <summary>
    /// 게임 도중 문이 열리거나 바리케이드가 쳐지면 호출하세요!
    /// ffs.UpdateDynamicObstacleRegion(door.position, 5f);
    /// </summary>
    public void UpdateDynamicObstacleRegion(Vector3 centerPosition, float radius)
    {
        if (!_costField.IsCreated) return;

        // 반경 내에 포함되는 그리드 인덱스 범위 계산 (불필요한 전체 루프 방지)
        int minX = Mathf.Max(0, Mathf.FloorToInt((centerPosition.x - radius - _bottomLeft.x) / aiCellSize));
        int maxX = Mathf.Min(_cols - 1, Mathf.FloorToInt((centerPosition.x + radius - _bottomLeft.x) / aiCellSize));
        int minZ = Mathf.Max(0, Mathf.FloorToInt((centerPosition.z - radius - _bottomLeft.z) / aiCellSize));
        int maxZ = Mathf.Min(_rows - 1, Mathf.FloorToInt((centerPosition.z + radius - _bottomLeft.z) / aiCellSize));

        float rayHeight = centerPosition.y + 50f;

        // 딱 변화가 일어난 n x n 칸만 다시 레이저를 쏴서 업데이트합니다. (프레임 저하 0%)
        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                int flatIndex = z * _cols + x;
                Vector3 cellCenter = _bottomLeft + new Vector3(x * aiCellSize + (aiCellSize / 2f), 0, z * aiCellSize + (aiCellSize / 2f));
                Vector3 rayOrigin = new Vector3(cellCenter.x, rayHeight, cellCenter.z);

                byte newCost = 255; // 기본값 막힘

                if (Physics.SphereCast(rayOrigin, aiCellSize * 0.4f, Vector3.down, out RaycastHit hit, 100f, walkableLayer | obstacleLayer))
                {
                    if ((obstacleLayer.value & (1 << hit.transform.gameObject.layer)) == 0)
                    {
                        float slopeAngle = Vector3.Angle(Vector3.up, hit.normal);
                        if (slopeAngle <= maxWalkableSlope)
                        {
                            if (Physics.CheckSphere(hit.point, 1.5f, obstacleLayer)) newCost = 5;
                            else newCost = 1;
                        }
                    }
                }

                // NativeArray에 즉시 적용 (다음 프레임부터 좀비들이 알아서 피해감)
                _costField[flatIndex] = newCost;
            }
        }
    }

    // =========================================================================
    // 3. 런타임 업데이트 (Job System) - 이전과 동일
    // =========================================================================
    public void StartUpdatingFlowFields(List<Transform> activePlayers)
    {
        _targetPlayers = activePlayers;
    }

    private void Update()
    {
        if (_targetPlayers == null || _targetPlayers.Count == 0 || !_costField.IsCreated) return;

        int playerCount = Mathf.Min(_targetPlayers.Count, 4);
        NativeArray<JobHandle> vectorHandles = new NativeArray<JobHandle>(playerCount, Allocator.Temp);

        for (int i = 0; i < playerCount; i++)
        {
            if (_targetPlayers[i] == null || !_targetPlayers[i].gameObject.activeInHierarchy) continue;

            Vector3 pPos = _targetPlayers[i].position;
            int pX = Mathf.FloorToInt((pPos.x - _bottomLeft.x) / aiCellSize);
            int pZ = Mathf.FloorToInt((pPos.z - _bottomLeft.z) / aiCellSize);

            IntegrationFieldJob intJob = new IntegrationFieldJob
            {
                CostField = _costField,
                IntegrationField = _integrationFields[i],
                GridCols = _cols,
                GridRows = _rows,
                TargetCell = new Unity.Mathematics.int2(pX, pZ)
            };
            JobHandle intHandle = intJob.Schedule();

            VectorFieldJob vecJob = new VectorFieldJob
            {
                IntegrationField = _integrationFields[i],
                CostField = _costField,
                FlowField = NativeFlowFields[i],
                GridCols = _cols,
                GridRows = _rows,

                BottomLeft = _bottomLeft,
                AiCellSize = aiCellSize,
                TargetPos = _targetPlayers[i].position
            };
            vectorHandles[i] = vecJob.Schedule(_cols * _rows, 64, intHandle);
        }

        JobHandle.CompleteAll(vectorHandles);
        vectorHandles.Dispose();
    }

    // =========================================================================
    // 💡 4. 기즈모(Gizmos) 시각화 - 에디터 및 런타임에서 눈으로 확인
    // =========================================================================
    // 💡 Selected를 지워서 항상 보이게 만듭니다!
    private void OnDrawGizmos()
    {
        if (_savedCols == 0 || _savedRows == 0) return;

        Vector3 centerOffset = new Vector3(aiCellSize / 2f, 0, aiCellSize / 2f);
        Vector3 cubeSize = new Vector3(aiCellSize, 0.2f, aiCellSize);

        // 1. 에디터/플레이 상태 상관없이 장애물(벽) 표시
        if (showObstacleBlocks && _savedCostField != null && _savedCostField.Length > 0)
        {
            bool useRuntime = Application.isPlaying && _costField.IsCreated;

            for (int x = 0; x < _savedCols; x++)
            {
                for (int z = 0; z < _savedRows; z++)
                {
                    int flatIndex = z * _savedCols + x;
                    byte cost = useRuntime ? _costField[flatIndex] : _savedCostField[flatIndex];

                    if (cost == 255)
                    {
                        Gizmos.color = new Color(1f, 0f, 0f, 0.5f);
                        Vector3 pos = _savedBottomLeft + new Vector3(x * aiCellSize, 0, z * aiCellSize) + centerOffset;
                        Gizmos.DrawCube(pos, cubeSize);
                    }
                    else if (cost == 5)
                    {
                        Gizmos.color = new Color(1f, 1f, 0f, 0.3f);
                        Vector3 pos = _savedBottomLeft + new Vector3(x * aiCellSize, 0, z * aiCellSize) + centerOffset;
                        Gizmos.DrawCube(pos, cubeSize);
                    }
                }
            }
        }

        // 2. 플레이 상태일 때 실시간 화살표 표시
        if (showFlowArrows && Application.isPlaying && NativeFlowFields != null)
        {
            if (debugPlayerIndex < 0 || debugPlayerIndex >= NativeFlowFields.Length) return;
            if (!NativeFlowFields[debugPlayerIndex].IsCreated) return;

            // 💡 선을 더 잘 보이게 마젠타(분홍)색으로 변경!
            Gizmos.color = Color.magenta;

            for (int x = 0; x < _cols; x++)
            {
                for (int z = 0; z < _rows; z++)
                {
                    int flatIndex = z * _cols + x;
                    if (_costField[flatIndex] == 255) continue;

                    Vector3 dir = NativeFlowFields[debugPlayerIndex][flatIndex];
                    if (dir.sqrMagnitude > 0.1f)
                    {
                        // 💡 Y축을 3.0f로 확 띄워서 지형에 절대 파묻히지 않게 만듭니다!
                        Vector3 startPos = _bottomLeft + new Vector3(x * aiCellSize, 3.0f, z * aiCellSize) + centerOffset;
                        Vector3 endPos = startPos + (dir * aiCellSize * 0.8f);

                        Gizmos.DrawLine(startPos, endPos);
                        // 💡 구슬 크기도 살짝 키움
                        Gizmos.DrawSphere(endPos, 0.3f);
                    }
                }
            }
        }
    }

    public int GridCols => _cols;
    public int GridRows => _rows;
    public Vector3 BottomLeft => _bottomLeft;
}