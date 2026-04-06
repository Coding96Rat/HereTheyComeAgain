using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

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
    public bool showObstacleBlocks = true;
    public bool showFlowArrows = true;
    [Range(0, 3)] public int debugPlayerIndex = 0;

    [SerializeField, HideInInspector] private byte[] _savedCostField;
    [SerializeField, HideInInspector] private int _savedCols, _savedRows;
    [SerializeField, HideInInspector] private Vector3 _savedBottomLeft;

    private int _cols, _rows;
    private Vector3 _bottomLeft;

    private NativeArray<byte> _costField;
    private NativeArray<int>[] _integrationFields;
    public NativeArray<Vector3>[] NativeFlowFields;

    private JobHandle[] _perPlayerHandles;
    private bool _isInitialized = false;
    private int _lastScheduledFrame = -1;
    private JobHandle _combinedFfsHandleThisFrame;

    // P1/P6: 플레이어별 마지막 FlowField 계산 셀 추적 — 같은 셀이면 BFS 생략
    private int2[] _lastPlayerCells;

    // 💡 [에러 원천 차단] 모든 Mother의 읽기 작업을 추적하는 마스터 핸들
    private JobHandle _globalReadersHandle;

    // FlowFieldSystem.cs 의 Awake 함수만 아래로 교체하세요.
    private void Awake()
    {
        _gridSystem = FindFirstObjectByType<GridSystem>();
        Initialize(4); // 💡 서버/클라이언트 무관하게 씬이 켜지면 무조건 배열을 할당하여 크래시 원천 차단
    }

    private void OnDestroy()
    {
        // 파괴되기 전, 읽고 있는 모든 Job이 끝날 때까지 무조건 대기
        _globalReadersHandle.Complete();

        if (_perPlayerHandles != null)
        {
            for (int i = 0; i < _perPlayerHandles.Length; i++) _perPlayerHandles[i].Complete();
        }

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
        if (_isInitialized) return;

        if (_gridSystem == null) _gridSystem = FindFirstObjectByType<GridSystem>();

        if (_savedCostField == null || _savedCostField.Length == 0)
        {
            Debug.Log("[FFS] 맵 데이터가 없어 런타임에 새로 굽습니다.");
            BakeInEditor();
        }

        _cols = _savedCols;
        _rows = _savedRows;
        _bottomLeft = _savedBottomLeft;

        int totalCells = _cols * _rows;
        _costField = new NativeArray<byte>(totalCells, Allocator.Persistent);
        _integrationFields = new NativeArray<int>[maxPlayers];
        NativeFlowFields = new NativeArray<Vector3>[maxPlayers];

        _perPlayerHandles = new JobHandle[maxPlayers];

        // P1/P6: 셀 추적 배열 초기화 — int.MinValue는 "아직 계산 안 함" 센티넬 값
        _lastPlayerCells = new int2[maxPlayers];
        for (int i = 0; i < maxPlayers; i++)
        {
            _lastPlayerCells[i] = new int2(int.MinValue, int.MinValue);
            _integrationFields[i] = new NativeArray<int>(totalCells, Allocator.Persistent);
            NativeFlowFields[i] = new NativeArray<Vector3>(totalCells, Allocator.Persistent);
        }

        if (_savedCostField != null && _savedCostField.Length == totalCells)
        {
            _costField.CopyFrom(_savedCostField);
        }

        _isInitialized = true;
    }

    // 💡 [핵심 연동 함수] 각 Mother가 자신의 Move Job 핸들을 여기로 넘겨서 등록합니다.
    public void RegisterReader(JobHandle readerHandle)
    {
        _globalReadersHandle = JobHandle.CombineDependencies(_globalReadersHandle, readerHandle);
    }

    public void UpdateDynamicObstacleRegion(Vector3 centerPosition, float radius)
    {
        if (!_costField.IsCreated) return;

        _globalReadersHandle.Complete();
        if (_perPlayerHandles != null)
        {
            for (int i = 0; i < _perPlayerHandles.Length; i++) _perPlayerHandles[i].Complete();
        }

        int minX = Mathf.Max(0, Mathf.FloorToInt((centerPosition.x - radius - _bottomLeft.x) / aiCellSize));
        int maxX = Mathf.Min(_cols - 1, Mathf.FloorToInt((centerPosition.x + radius - _bottomLeft.x) / aiCellSize));
        int minZ = Mathf.Max(0, Mathf.FloorToInt((centerPosition.z - radius - _bottomLeft.z) / aiCellSize));
        int maxZ = Mathf.Min(_rows - 1, Mathf.FloorToInt((centerPosition.z + radius - _bottomLeft.z) / aiCellSize));

        float rayHeight = centerPosition.y + 50f;

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                int flatIndex = z * _cols + x;
                Vector3 cellCenter = _bottomLeft + new Vector3(x * aiCellSize + (aiCellSize / 2f), 0, z * aiCellSize + (aiCellSize / 2f));
                Vector3 rayOrigin = new Vector3(cellCenter.x, rayHeight, cellCenter.z);

                byte newCost = 255;

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
                _costField[flatIndex] = newCost;
            }
        }

        // 장애물 변경 → 모든 플레이어 셀 캐시 무효화 (다음 프레임에 FlowField 강제 재계산)
        if (_lastPlayerCells != null)
            for (int i = 0; i < _lastPlayerCells.Length; i++)
                _lastPlayerCells[i] = new int2(int.MinValue, int.MinValue);
    }

    public JobHandle ScheduleFlowFieldJobs(JobHandle dependency, List<Transform> targetPlayers)
    {
        if (!_isInitialized || targetPlayers == null || targetPlayers.Count == 0 || !_costField.IsCreated) return dependency;

        if (Time.frameCount == _lastScheduledFrame)
        {
            return JobHandle.CombineDependencies(dependency, _combinedFfsHandleThisFrame);
        }

        // 💡 [에러 해결의 핵심 방어선] 이전 프레임에서 FlowField를 읽고 있는 "모든 Mother"의 Job이 끝날 때까지 대기합니다!
        _globalReadersHandle.Complete();
        _globalReadersHandle = default;

        _lastScheduledFrame = Time.frameCount;
        int playerCount = Mathf.Min(targetPlayers.Count, _perPlayerHandles.Length);
        JobHandle finalCombinedHandle = dependency;

        for (int i = 0; i < playerCount; i++)
        {
            if (targetPlayers[i] == null || !targetPlayers[i].gameObject.activeInHierarchy) continue;

            Vector3 pPos = targetPlayers[i].position;
            int pX = Mathf.FloorToInt((pPos.x - _bottomLeft.x) / aiCellSize);
            int pZ = Mathf.FloorToInt((pPos.z - _bottomLeft.z) / aiCellSize);

            // P1/P6: 플레이어가 같은 FlowField 셀 안에 있으면 BFS 재계산 생략
            // — IntegrationFieldJob(단일 스레드 BFS)이 매 프레임 불필요하게 실행되는 병목 제거
            if (_lastPlayerCells[i].x == pX && _lastPlayerCells[i].y == pZ)
                continue;

            _lastPlayerCells[i] = new int2(pX, pZ);

            if (_perPlayerHandles != null) _perPlayerHandles[i].Complete();

            IntegrationFieldJob intJob = new IntegrationFieldJob
            {
                CostField = _costField,
                IntegrationField = _integrationFields[i],
                GridCols = _cols,
                GridRows = _rows,
                TargetCell = new int2(pX, pZ)
            };

            JobHandle intHandle = intJob.Schedule(dependency);

            VectorFieldJob vecJob = new VectorFieldJob
            {
                IntegrationField = _integrationFields[i],
                CostField = _costField,
                FlowField = NativeFlowFields[i],
                GridCols = _cols,
                GridRows = _rows,
                BottomLeft = _bottomLeft,
                AiCellSize = aiCellSize,
                TargetPos = pPos
            };

            JobHandle vecHandle = vecJob.Schedule(_cols * _rows, 64, intHandle);

            _perPlayerHandles[i] = vecHandle;
            finalCombinedHandle = JobHandle.CombineDependencies(finalCombinedHandle, vecHandle);
        }

        _combinedFfsHandleThisFrame = finalCombinedHandle;
        return finalCombinedHandle;
    }

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
                    bool isObstacleLayer = (obstacleLayer.value & (1 << hit.transform.gameObject.layer)) > 0;
                    float slopeAngle = Vector3.Angle(Vector3.up, hit.normal);

                    if (isObstacleLayer) { _savedCostField[flatIndex] = 255; continue; }

                    byte cost = 1;
                    if (slopeAngle > maxWalkableSlope) cost = 150;
                    else if (slopeAngle > maxWalkableSlope * 0.5f) cost = (byte)(1 + (slopeAngle / maxWalkableSlope) * 30);
                    else cost = (Physics.CheckSphere(hit.point, 1.2f, obstacleLayer)) ? (byte)5 : (byte)1;

                    _savedCostField[flatIndex] = cost;
                }
            }
        }
    }

    private void OnDrawGizmos()
    {
        if (_savedCols == 0 || _savedRows == 0) return;
        Vector3 centerOffset = new Vector3(aiCellSize / 2f, 0, aiCellSize / 2f);
        Vector3 cubeSize = new Vector3(aiCellSize, 0.2f, aiCellSize);

        if (showObstacleBlocks && _savedCostField != null && _savedCostField.Length > 0)
        {
            // 플레이 중에는 저장된 Bake 데이터만 표시 (런타임 동적 마킹 셀 제외)
            // → PlacedStructure가 cost=255로 마킹한 셀이 빨간색으로 덮이는 현상 방지
            for (int x = 0; x < _savedCols; x++)
            {
                for (int z = 0; z < _savedRows; z++)
                {
                    int flatIndex = z * _savedCols + x;
                    byte cost = _savedCostField[flatIndex]; // 항상 baked 데이터 사용

                    if (cost == 255) { Gizmos.color = new Color(1f, 0f, 0f, 0.5f); Gizmos.DrawCube(_savedBottomLeft + new Vector3(x * aiCellSize, 0, z * aiCellSize) + centerOffset, cubeSize); }
                    else if (cost == 5) { Gizmos.color = new Color(1f, 1f, 0f, 0.3f); Gizmos.DrawCube(_savedBottomLeft + new Vector3(x * aiCellSize, 0, z * aiCellSize) + centerOffset, cubeSize); }
                }
            }
        }
        if (showFlowArrows && Application.isPlaying && NativeFlowFields != null)
        {
            if (debugPlayerIndex < 0 || debugPlayerIndex >= NativeFlowFields.Length || !NativeFlowFields[debugPlayerIndex].IsCreated) return;
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
                        Vector3 startPos = _bottomLeft + new Vector3(x * aiCellSize, 3.0f, z * aiCellSize) + centerOffset;
                        Vector3 endPos = startPos + (dir * aiCellSize * 0.8f);
                        Gizmos.DrawLine(startPos, endPos); Gizmos.DrawSphere(endPos, 0.3f);
                    }
                }
            }
        }
    }

    public int GridCols => _cols;
    public int GridRows => _rows;
    public Vector3 BottomLeft => _bottomLeft;

    // ─── PlacedStructure / EnemyMovementJob 연동용 공개 API ──────────────────

    /// <summary>
    /// EnemyMovementJob에 ReadOnly로 넘기기 위한 CostField 노출.
    /// </summary>
    public NativeArray<byte> CostField => _costField;

    /// <summary>
    /// 특정 플랫 인덱스의 비용을 직접 쓴다 (PlacedStructure 등록/해제용).
    /// Job이 읽고 있을 수 있으므로 완료 대기 후 작성.
    /// </summary>
    public bool TrySetCellCost(int flatIdx, byte cost)
    {
        if (!_costField.IsCreated || flatIdx < 0 || flatIdx >= _costField.Length) return false;
        _globalReadersHandle.Complete();
        if (_perPlayerHandles != null)
            for (int i = 0; i < _perPlayerHandles.Length; i++) _perPlayerHandles[i].Complete();
        _costField[flatIdx] = cost;
        // FlowField 강제 재계산
        if (_lastPlayerCells != null)
            for (int i = 0; i < _lastPlayerCells.Length; i++)
                _lastPlayerCells[i] = new int2(int.MinValue, int.MinValue);
        return true;
    }

    public int2 WorldToCell(Vector3 worldPos) => new int2(
        Mathf.FloorToInt((worldPos.x - _bottomLeft.x) / aiCellSize),
        Mathf.FloorToInt((worldPos.z - _bottomLeft.z) / aiCellSize));

    public int CellToFlat(int2 cell) => cell.y * _cols + cell.x;
    public bool IsCellValid(int2 cell) => cell.x >= 0 && cell.x < _cols && cell.y >= 0 && cell.y < _rows;
}