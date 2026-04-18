using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 플레이어 유닛 전용 FlowField.
/// FlowFieldSystem의 CostField(장애물 정보)를 공유하고,
/// 플레이어별 이동 목적지에 대한 FlowField를 관리한다.
/// EnemyMother가 NativeFlowFields를 읽는 것처럼, RTSUnitMovementJob이 이를 읽는다.
/// </summary>
public class RTSFlowFieldSystem : MonoBehaviour
{
    public static RTSFlowFieldSystem Instance { get; private set; }

    private const int MaxPlayers = 4;

    // RTSUnitMovementJob이 ReadOnly로 직접 접근 (EnemyMother.NativeFlowFields와 동일 패턴)
    public NativeArray<Vector3>[] NativeFlowFields { get; private set; }

    private NativeArray<int>[] _integrationFields;
    private JobHandle[] _jobHandles;
    private JobHandle _globalReadersHandle;

    private int2[] _lastDestinationCells;

    private bool _isInitialized;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        TryInitialize();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        DisposeAll();
    }

    // ─── 초기화 ──────────────────────────────────────────────────────────────

    public void TryInitialize()
    {
        if (_isInitialized) return;
        if (FlowFieldSystem.Instance == null) return;

        int totalCells = FlowFieldSystem.Instance.GridCols * FlowFieldSystem.Instance.GridRows;
        if (totalCells == 0) return;

        _integrationFields = new NativeArray<int>[MaxPlayers];
        NativeFlowFields   = new NativeArray<Vector3>[MaxPlayers];
        _jobHandles        = new JobHandle[MaxPlayers];
        _lastDestinationCells = new int2[MaxPlayers];

        for (int i = 0; i < MaxPlayers; i++)
        {
            _integrationFields[i] = new NativeArray<int>(totalCells, Allocator.Persistent);
            NativeFlowFields[i]   = new NativeArray<Vector3>(totalCells, Allocator.Persistent);
            _lastDestinationCells[i] = new int2(int.MinValue, int.MinValue);
        }

        _isInitialized = true;
    }

    private void DisposeAll()
    {
        if (!_isInitialized) return;

        _globalReadersHandle.Complete();
        for (int i = 0; i < MaxPlayers; i++)
        {
            _jobHandles[i].Complete();
            if (_integrationFields[i].IsCreated) _integrationFields[i].Dispose();
            if (NativeFlowFields[i].IsCreated)   NativeFlowFields[i].Dispose();
        }
        _isInitialized = false;
    }

    // ─── 공개 API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// RTSUnitMother에서 매 프레임 호출.
    /// 목적지가 바뀐 playerIndex에 대해서만 FlowField를 재계산한다.
    /// EnemyMother의 ScheduleFlowFieldJobs 호출 패턴과 동일.
    /// </summary>
    public JobHandle ScheduleFlowFieldJobs(JobHandle dependency, Vector3[] destinations, bool[] dirty)
    {
        if (!_isInitialized) TryInitialize();
        if (!_isInitialized) return dependency;
        if (FlowFieldSystem.Instance == null || !FlowFieldSystem.Instance.CostField.IsCreated) return dependency;

        _globalReadersHandle.Complete();
        _globalReadersHandle = default;

        JobHandle combined = dependency;

        for (int i = 0; i < MaxPlayers; i++)
        {
            if (!dirty[i]) continue;

            int2 destCell = FlowFieldSystem.Instance.WorldToCell(destinations[i]);
            if (!FlowFieldSystem.Instance.IsCellValid(destCell)) continue;

            // 같은 셀이면 재계산 생략
            if (_lastDestinationCells[i].x == destCell.x && _lastDestinationCells[i].y == destCell.y) continue;

            _lastDestinationCells[i] = destCell;

            _jobHandles[i].Complete();

            IntegrationFieldJob intJob = new IntegrationFieldJob
            {
                CostField        = FlowFieldSystem.Instance.CostField,
                IntegrationField = _integrationFields[i],
                GridCols         = FlowFieldSystem.Instance.GridCols,
                GridRows         = FlowFieldSystem.Instance.GridRows,
                TargetCell       = destCell
            };
            JobHandle intHandle = intJob.Schedule(dependency);

            VectorFieldJob vecJob = new VectorFieldJob
            {
                IntegrationField = _integrationFields[i],
                CostField        = FlowFieldSystem.Instance.CostField,
                FlowField        = NativeFlowFields[i],
                GridCols         = FlowFieldSystem.Instance.GridCols,
                GridRows         = FlowFieldSystem.Instance.GridRows,
                BottomLeft       = FlowFieldSystem.Instance.BottomLeft,
                AiCellSize       = FlowFieldSystem.Instance.aiCellSize,
                TargetPos        = destinations[i]
            };

            _jobHandles[i] = vecJob.Schedule(
                FlowFieldSystem.Instance.GridCols * FlowFieldSystem.Instance.GridRows, 64, intHandle);

            combined = JobHandle.CombineDependencies(combined, _jobHandles[i]);
        }

        // CostField를 읽는 Job 핸들을 FlowFieldSystem에 등록.
        // TrySetCellCost()가 쓰기 전에 이 Job들도 대기하도록 보장.
        FlowFieldSystem.Instance.RegisterReader(combined);

        return combined;
    }

    /// <summary>RTSUnitMovementJob이 읽기 완료 후 핸들 등록 (FlowFieldSystem.RegisterReader와 동일).</summary>
    public void RegisterReader(JobHandle readerHandle)
    {
        _globalReadersHandle = JobHandle.CombineDependencies(_globalReadersHandle, readerHandle);
    }

    /// <summary>장애물 변경 시 모든 캐시 무효화 (TileMap 변경 연동용).</summary>
    public void InvalidateAllCaches()
    {
        if (_lastDestinationCells == null) return;
        for (int i = 0; i < MaxPlayers; i++)
            _lastDestinationCells[i] = new int2(int.MinValue, int.MinValue);
    }
}
