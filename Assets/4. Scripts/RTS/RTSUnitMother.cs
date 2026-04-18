using System.Collections;
using System.Collections.Generic;
using FishNet;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

/// <summary>
/// 플레이어 유닛 전체를 EnemyMother와 동일한 방식으로 관리.
/// TransformAccessArray + NativeArray + Burst Job으로 유닛 이동을 일괄 처리한다.
///
/// 네트워크: 이동 목적지를 SyncVar로 동기화 → 모든 클라이언트가 동일한 FlowField로
/// 결정론적 이동 계산 (EnemyMother의 RPC 동기화 패턴과 동일).
/// </summary>
public class RTSUnitMother : NetworkBehaviour
{
    public static RTSUnitMother Instance { get; private set; }

    [Header("Unit Prefab")]
    public GameObject unitPrefab;

    [Header("Initial Spawn")]
    [SerializeField] private int _unitsPerPlayer = 5;
    [SerializeField] private float _unitSpawnRadius = 3f;
    [Tooltip("플레이어 인덱스별 유닛 초기 스폰 중심 (0=Host, 1=Client1, ...)")]
    [SerializeField] private Vector3[] _playerSpawnCenters = new Vector3[4];

    [Header("Physics")]
    [SerializeField] private LayerMask _groundLayer;
    public float separationRadius = 0.8f;
    public float separationWeight = 2.0f;
    public float rotationSpeed    = 540f;
    public float cellSize         = 2f;

    [Header("Stopping Distance")]
    public float stoppingDistance = 0.4f;

    // ─── 동기화: 플레이어별 이동 목적지 (FishNet 4 — SyncVar<T> 타입) ─────────
    private readonly SyncVar<Vector3> _dest0 = new();
    private readonly SyncVar<Vector3> _dest1 = new();
    private readonly SyncVar<Vector3> _dest2 = new();
    private readonly SyncVar<Vector3> _dest3 = new();

    // ─── 유닛 리스트 ─────────────────────────────────────────────────────────
    public List<RTSUnit> Units { get; private set; } = new();

    private Queue<RTSUnit> _pendingAdds    = new();
    private Queue<RTSUnit> _pendingRemoves = new();

    // ─── NativeArray / Job 인프라 ─────────────────────────────────────────────
    private const int MaxUnits = 256;

    private TransformAccessArray _transformAccessArray;
    private NativeArray<float>      _yVelocities;
    private NativeArray<Vector3>    _currentPositions;
    private NativeArray<Quaternion> _rotations;
    private NativeArray<float>      _speeds;
    private NativeArray<int>        _ownerIndices;
    private NativeArray<Vector3>    _playerDestinations;  // 크기 4

    private NativeParallelMultiHashMap<int, int> _spatialGrid;
    private NativeArray<RaycastCommand> _raycastCommands;
    private NativeArray<RaycastHit>     _raycastHits;
    private QueryParameters             _groundQueryParams;

    private JobHandle _movementJobHandle;
    private JobHandle _hashJobHandle;

    // 로컬 목적지 캐시 (NativeArray 업데이트용)
    private readonly Vector3[] _destinations = new Vector3[4];
    private readonly bool[]    _dirtyFlags   = new bool[4];

    private RTSFlowFieldSystem _rtsFlowField;

    // ─── 생명주기 ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        StartCoroutine(SpawnInitialUnitsCoroutine());
    }

    private IEnumerator SpawnInitialUnitsCoroutine()
    {
        // FlowFieldSystem 등 씬 초기화 대기
        yield return new WaitForSeconds(0.5f);

        // 현재 연결된 모든 클라이언트에 대해 유닛 스폰
        // FishNet 4: ServerManager.Clients = Dictionary<int, NetworkConnection>
        foreach (var kvp in InstanceFinder.ServerManager.Clients)
        {
            int playerIndex = kvp.Key; // ClientId = playerIndex
            SpawnUnitsForPlayer(playerIndex);
        }
    }

    private void SpawnUnitsForPlayer(int playerIndex)
    {
        Vector3 center = (playerIndex >= 0 && playerIndex < _playerSpawnCenters.Length)
            ? _playerSpawnCenters[playerIndex]
            : Vector3.zero;

        for (int i = 0; i < _unitsPerPlayer; i++)
        {
            Vector2 r = UnityEngine.Random.insideUnitCircle * _unitSpawnRadius;
            Vector3 pos = center + new Vector3(r.x, 0f, r.y);
            RpcSpawnUnit(playerIndex, pos);
        }
    }

    // RunLocally = true → 서버 자신도 실행, 클라이언트도 동일하게 실행
    // 결정론적 위치를 서버가 계산 후 전달 → EnemyMother.RpcStartSpawnWave와 동일 패턴
    [ObserversRpc(RunLocally = true)]
    private void RpcSpawnUnit(int playerIndex, Vector3 position)
    {
        if (unitPrefab == null) return;

        GameObject obj = Instantiate(unitPrefab, position, Quaternion.identity);
        if (obj.TryGetComponent(out RTSUnit unit))
        {
            unit.Initialize(this, playerIndex);
            AddUnit(unit);
        }
    }

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();

        _dest0.OnChange += OnDest0Changed;
        _dest1.OnChange += OnDest1Changed;
        _dest2.OnChange += OnDest2Changed;
        _dest3.OnChange += OnDest3Changed;

        _transformAccessArray = new TransformAccessArray(MaxUnits);
        _yVelocities          = new NativeArray<float>(MaxUnits, Allocator.Persistent);
        _currentPositions     = new NativeArray<Vector3>(MaxUnits, Allocator.Persistent);
        _rotations            = new NativeArray<Quaternion>(MaxUnits, Allocator.Persistent);
        _speeds               = new NativeArray<float>(MaxUnits, Allocator.Persistent);
        _ownerIndices         = new NativeArray<int>(MaxUnits, Allocator.Persistent);
        _playerDestinations   = new NativeArray<Vector3>(4, Allocator.Persistent);
        _spatialGrid          = new NativeParallelMultiHashMap<int, int>(MaxUnits, Allocator.Persistent);
        // RaycastSetupJob은 유닛당 2개 (down + forward) 기록
        _raycastCommands      = new NativeArray<RaycastCommand>(MaxUnits * 2, Allocator.Persistent);
        _raycastHits          = new NativeArray<RaycastHit>(MaxUnits * 2, Allocator.Persistent);

        _groundQueryParams = new QueryParameters(
            _groundLayer.value != 0 ? _groundLayer.value : LayerMask.GetMask("Ground"),
            false, QueryTriggerInteraction.Ignore, false);

        _rtsFlowField = FindFirstObjectByType<RTSFlowFieldSystem>();
        _rtsFlowField?.TryInitialize();
    }

    public override void OnStopNetwork()
    {
        base.OnStopNetwork();

        _dest0.OnChange -= OnDest0Changed;
        _dest1.OnChange -= OnDest1Changed;
        _dest2.OnChange -= OnDest2Changed;
        _dest3.OnChange -= OnDest3Changed;

        DisposeAll();
    }

    private void OnDestroy()
    {
        DisposeAll();
    }

    private void DisposeAll()
    {
        _movementJobHandle.Complete();
        _hashJobHandle.Complete();

        if (_transformAccessArray.isCreated) _transformAccessArray.Dispose();
        if (_yVelocities.IsCreated)          _yVelocities.Dispose();
        if (_currentPositions.IsCreated)     _currentPositions.Dispose();
        if (_rotations.IsCreated)            _rotations.Dispose();
        if (_speeds.IsCreated)               _speeds.Dispose();
        if (_ownerIndices.IsCreated)         _ownerIndices.Dispose();
        if (_playerDestinations.IsCreated)   _playerDestinations.Dispose();
        if (_spatialGrid.IsCreated)          _spatialGrid.Dispose();
        if (_raycastCommands.IsCreated)      _raycastCommands.Dispose();
        if (_raycastHits.IsCreated)          _raycastHits.Dispose();
    }

    // ─── 유닛 등록 / 해제 ────────────────────────────────────────────────────

    public void AddUnit(RTSUnit unit) => _pendingAdds.Enqueue(unit);

    public void RemoveUnit(RTSUnit unit)
    {
        if (unit.unitListIndex != -1) _pendingRemoves.Enqueue(unit);
    }

    private void ProcessPendingChanges()
    {
        while (_pendingRemoves.Count > 0) ExecuteRemove(_pendingRemoves.Dequeue());
        while (_pendingAdds.Count > 0)    ExecuteAdd(_pendingAdds.Dequeue());
    }

    private void ExecuteAdd(RTSUnit unit)
    {
        if (Units.Count >= MaxUnits) return;

        int idx = Units.Count;
        Units.Add(unit);
        unit.unitListIndex = idx;

        _transformAccessArray.Add(unit.transform);
        _currentPositions[idx] = unit.transform.position;
        _rotations[idx]        = unit.transform.rotation;
        _yVelocities[idx]      = 0f;
        _speeds[idx]           = unit.speed;
        _ownerIndices[idx]     = unit.playerOwnerIndex;
    }

    private void ExecuteRemove(RTSUnit unit)
    {
        int removeIdx = unit.unitListIndex;
        if (removeIdx < 0 || removeIdx >= Units.Count || Units[removeIdx] != unit) return;

        int lastIdx = Units.Count - 1;

        if (removeIdx == lastIdx)
        {
            Units.RemoveAt(lastIdx);
            _transformAccessArray.RemoveAtSwapBack(lastIdx);
        }
        else
        {
            RTSUnit last = Units[lastIdx];
            Units[removeIdx] = last;
            last.unitListIndex = removeIdx;
            Units.RemoveAt(lastIdx);

            _transformAccessArray.RemoveAtSwapBack(removeIdx);
            _currentPositions[removeIdx] = _currentPositions[lastIdx];
            _rotations[removeIdx]        = _rotations[lastIdx];
            _yVelocities[removeIdx]      = _yVelocities[lastIdx];
            _speeds[removeIdx]           = _speeds[lastIdx];
            _ownerIndices[removeIdx]     = _ownerIndices[lastIdx];
        }
        unit.unitListIndex = -1;
        unit.gameObject.SetActive(false);
    }

    // ─── 이동 명령 ───────────────────────────────────────────────────────────

    /// <summary>소유 클라이언트가 호출. 서버에서 SyncVar 갱신 → 전 클라이언트 동기화.</summary>
    [ServerRpc(RequireOwnership = false)]
    public void CmdIssueMoveCommand(int playerIndex, Vector3 destination)
    {
        switch (playerIndex)
        {
            case 0: _dest0.Value = destination; break;
            case 1: _dest1.Value = destination; break;
            case 2: _dest2.Value = destination; break;
            case 3: _dest3.Value = destination; break;
        }
    }

    // SyncVar 변경 Hook → 로컬 캐시 및 dirty 플래그 갱신
    private void OnDest0Changed(Vector3 prev, Vector3 next, bool asServer) { _destinations[0] = next; _dirtyFlags[0] = true; }
    private void OnDest1Changed(Vector3 prev, Vector3 next, bool asServer) { _destinations[1] = next; _dirtyFlags[1] = true; }
    private void OnDest2Changed(Vector3 prev, Vector3 next, bool asServer) { _destinations[2] = next; _dirtyFlags[2] = true; }
    private void OnDest3Changed(Vector3 prev, Vector3 next, bool asServer) { _destinations[3] = next; _dirtyFlags[3] = true; }
    // FishNet 4: SyncVar<T>.OnChange delegate requires exact signature above — (T prev, T next, bool asServer)

    // ─── Update — 적 EnemyMother.Update()와 동일 구조 ─────────────────────────

    private void Update()
    {
        _movementJobHandle.Complete();
        _hashJobHandle.Complete();

        ProcessPendingChanges();

        if (Units.Count == 0) return;

        // NativeArray에 플레이어 목적지 복사 (변경된 것만)
        for (int i = 0; i < 4; i++)
        {
            if (_dirtyFlags[i])
                _playerDestinations[i] = _destinations[i];
        }

        // ── Hash Job ──────────────────────────────────────────────────────────
        _spatialGrid.Clear();

        CopyPositionsJob copyJob = new CopyPositionsJob { CurrentPositions = _currentPositions };
        JobHandle copyHandle = copyJob.Schedule(_transformAccessArray);

        HashPositionsJob hashJob = new HashPositionsJob
        {
            Positions  = _currentPositions,
            SpatialGrid = _spatialGrid.AsParallelWriter(),
            CellSize   = cellSize
        };
        _hashJobHandle = hashJob.Schedule(Units.Count, 64, copyHandle);

        // ── Ground Raycast (2프레임에 1회) ───────────────────────────────────
        JobHandle combinedHandle = _hashJobHandle;
        if (Time.frameCount % 2 == 0)
        {
            RaycastSetupJob setupJob = new RaycastSetupJob
            {
                Commands          = _raycastCommands,
                DownQueryParams   = _groundQueryParams,
                ForwardQueryParams = _groundQueryParams
            };
            JobHandle setupHandle   = setupJob.Schedule(_transformAccessArray, copyHandle);
            int activeRayCount = Units.Count * 2;
            JobHandle raycastHandle = RaycastCommand.ScheduleBatch(
                _raycastCommands.GetSubArray(0, activeRayCount),
                _raycastHits.GetSubArray(0, activeRayCount),
                32, setupHandle);
            combinedHandle = JobHandle.CombineDependencies(_hashJobHandle, raycastHandle);
        }

        // ── RTSFlowField Jobs ─────────────────────────────────────────────────
        JobHandle ffsHandle = default;
        if (_rtsFlowField != null)
        {
            ffsHandle = _rtsFlowField.ScheduleFlowFieldJobs(default, _destinations, _dirtyFlags);
            combinedHandle = JobHandle.CombineDependencies(combinedHandle, ffsHandle);
        }

        // dirty 플래그 리셋
        for (int i = 0; i < 4; i++) _dirtyFlags[i] = false;

        // ── Unit Movement Job ─────────────────────────────────────────────────
        NativeArray<Vector3> ff0 = default, ff1 = default, ff2 = default, ff3 = default;
        if (_rtsFlowField?.NativeFlowFields != null)
        {
            var ffs = _rtsFlowField.NativeFlowFields;
            if (ffs.Length > 0) ff0 = ffs[0];
            if (ffs.Length > 1) ff1 = ffs[1];
            if (ffs.Length > 2) ff2 = ffs[2];
            if (ffs.Length > 3) ff3 = ffs[3];
        }

        FlowFieldSystem ffsRef = FlowFieldSystem.Instance;
        RTSUnitMovementJob moveJob = new RTSUnitMovementJob
        {
            FlowField0 = ff0,
            FlowField1 = ff1,
            FlowField2 = ff2,
            FlowField3 = ff3,

            OwnerIndices       = _ownerIndices,
            Speeds             = _speeds,
            PlayerDestinations = _playerDestinations,

            YVelocities      = _yVelocities,
            CurrentPositions = _currentPositions,
            Rotations        = _rotations,

            SpatialGrid       = _spatialGrid,
            SeparationRadius  = separationRadius,
            SeparationWeight  = separationWeight,
            CellSize          = cellSize,

            RaycastHits = _raycastHits,

            GridCols   = ffsRef != null ? ffsRef.GridCols : 0,
            GridRows   = ffsRef != null ? ffsRef.GridRows : 0,
            AiCellSize = ffsRef != null ? ffsRef.aiCellSize : 2f,
            BottomLeft = ffsRef != null ? ffsRef.BottomLeft : Vector3.zero,
            CostField  = (ffsRef != null && ffsRef.CostField.IsCreated) ? ffsRef.CostField : default,

            DeltaTime          = Time.deltaTime,
            RotationSpeed      = rotationSpeed,
            Gravity            = 9.81f,
            StoppingDistanceSqr = stoppingDistance * stoppingDistance
        };

        _movementJobHandle = moveJob.Schedule(_transformAccessArray, combinedHandle);

        if (_rtsFlowField != null) _rtsFlowField.RegisterReader(_movementJobHandle);
        // CostField를 FlowFieldSystem에서 빌려쓰므로 FlowFieldSystem에도 등록 필수
        FlowFieldSystem.Instance?.RegisterReader(_movementJobHandle);

        JobHandle.ScheduleBatchedJobs();
    }

    // ─── 피격 감지 (EnemyMother.GetEnemyHitByRay 동일 패턴) ──────────────────

    /// <summary>적이 근접 공격 시 RTSUnitMother에서 피격 유닛 탐색.</summary>
    public RTSUnit GetUnitNearPosition(Vector3 pos, float radiusSqr)
    {
        for (int i = 0; i < Units.Count; i++)
        {
            Vector3 up = _currentPositions[i];
            float dx = up.x - pos.x, dz = up.z - pos.z;
            if (dx * dx + dz * dz < radiusSqr) return Units[i];
        }
        return null;
    }
}
