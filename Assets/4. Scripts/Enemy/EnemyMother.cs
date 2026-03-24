using FishNet.Object;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

public class EnemyMother : NetworkBehaviour
{
    public GameObject enemyPrefab;
    public static List<Transform> ValidTargets = new List<Transform>();

    public Transform dedicatedTarget;

    [Header("스폰 설정")]
    public float startDelay = 5f;
    public float spawnInterval = 1f;
    public int enemiesPerSpawn = 5;
    public int maxActiveEnemies = 4000;
    public float spawnRadius = 20f;

    [Header("물리 설정")]
    public float separationRadius = 1.5f;
    public float separationWeight = 2.0f;
    public float globalEnemySpeed = 3f;
    public float globalRotationSpeed = 360f;
    public float cellSize = 2.0f;
    public float maxWeightTolerance = 3.0f;

    [HideInInspector]
    public Vector3[] injectedStairs;

    [Header("GPU Instancing 설정")]
    public Mesh zombieMesh;
    public Material zombieMaterial;

    public List<Enemy> Enemies;
    private Queue<Enemy> _pendingAdds = new Queue<Enemy>();
    private Queue<Enemy> _pendingRemoves = new Queue<Enemy>();

    private TransformAccessArray _transformAccessArray;
    private NativeArray<Vector3> _currentPositions;
    private NativeArray<Quaternion> _currentRotations;
    private JobHandle _movementJobHandle;
    private JobHandle _hashJobHandle;
    private NativeArray<RaycastCommand> _raycastCommands;
    private NativeArray<RaycastHit> _raycastHits;
    private NativeArray<float> _yVelocities;
    private NativeArray<int> _animStates;
    private QueryParameters _groundQueryParams;

    private NativeArray<int> _targetIndices;
    private NativeArray<Vector3> _activeTargetPositions;

    private FlowFieldSystem _ffs;
    private NativeParallelMultiHashMap<int, int> _spatialGrid;

    // GPU 렌더링 변수
    private MaterialPropertyBlock _mpb;
    private readonly int _isWalkingHash = Shader.PropertyToID("_IsWalking");
    private readonly int _timeOffsetHash = Shader.PropertyToID("_TimeOffset");
    private Matrix4x4[][] _matrixBatches;
    private float[][] _isWalkingBatches;
    private float[][] _timeOffsetBatches;

    public static void RegisterTarget(Transform target)
    {
        if (!ValidTargets.Contains(target)) ValidTargets.Add(target);
    }

    public static void UnregisterTarget(Transform target)
    {
        if (ValidTargets.Contains(target)) ValidTargets.Remove(target);
    }

    public static Transform GetClosestTarget(Vector3 searchPos)
    {
        Transform closest = null;
        float minDist = float.MaxValue;
        for (int i = 0; i < ValidTargets.Count; i++)
        {
            if (ValidTargets[i] == null || !ValidTargets[i].gameObject.activeInHierarchy) continue;
            float dist = (ValidTargets[i].position - searchPos).sqrMagnitude;
            if (dist < minDist)
            {
                minDist = dist;
                closest = ValidTargets[i];
            }
        }
        return closest;
    }

    public void AddEnemy(Enemy enemy) { _pendingAdds.Enqueue(enemy); }
    public void RemoveEnemy(Enemy enemy) { if (enemy.motherListIndex != -1) _pendingRemoves.Enqueue(enemy); }

    private void ProcessPendingChanges()
    {
        while (_pendingRemoves.Count > 0) ExecuteRemoveEnemy(_pendingRemoves.Dequeue());
        while (_pendingAdds.Count > 0) ExecuteAddEnemy(_pendingAdds.Dequeue());
    }

    private void ExecuteAddEnemy(Enemy enemy)
    {
        int newIndex = Enemies.Count;
        Enemies.Add(enemy);
        enemy.motherListIndex = newIndex;

        _transformAccessArray.Add(enemy.transform);
        _targetIndices[newIndex] = enemy.GetTargetIndex();
        _yVelocities[newIndex] = 0f;
        _animStates[newIndex] = 0;
        _currentRotations[newIndex] = enemy.transform.rotation;
    }

    private void ExecuteRemoveEnemy(Enemy enemy)
    {
        int removeIndex = enemy.motherListIndex;
        if (removeIndex < 0 || removeIndex >= Enemies.Count || Enemies[removeIndex] != enemy) return;

        int lastIndex = Enemies.Count - 1;

        if (removeIndex == lastIndex)
        {
            Enemies.RemoveAt(lastIndex);
            _transformAccessArray.RemoveAtSwapBack(lastIndex);
        }
        else
        {
            Enemy lastEnemy = Enemies[lastIndex];
            Enemies[removeIndex] = lastEnemy;
            lastEnemy.motherListIndex = removeIndex;
            Enemies.RemoveAt(lastIndex);

            _transformAccessArray.RemoveAtSwapBack(removeIndex);
            _targetIndices[removeIndex] = _targetIndices[lastIndex];
            _yVelocities[removeIndex] = _yVelocities[lastIndex];
            _animStates[removeIndex] = _animStates[lastIndex];
            _currentPositions[removeIndex] = _currentPositions[lastIndex];
            _currentRotations[removeIndex] = _currentRotations[lastIndex];
        }
        enemy.motherListIndex = -1;
    }

    private void Awake()
    {
        // 모니터 주사율에 맞춰 GPU 폭주 방지
        Application.targetFrameRate = 60;
        Enemies = new List<Enemy>(maxActiveEnemies);
    }

    public override void OnStopNetwork()
    {
        base.OnStopNetwork();
        // 네트워크에서 분리될 때 Job 완전 정리
        _movementJobHandle.Complete();
        _hashJobHandle.Complete();
    }

    private void OnDestroy()
    {
        _movementJobHandle.Complete();
        _hashJobHandle.Complete();
        if (_transformAccessArray.isCreated) _transformAccessArray.Dispose();
        if (_raycastCommands.IsCreated) _raycastCommands.Dispose();
        if (_raycastHits.IsCreated) _raycastHits.Dispose();
        if (_yVelocities.IsCreated) _yVelocities.Dispose();
        if (_currentPositions.IsCreated) _currentPositions.Dispose();
        if (_currentRotations.IsCreated) _currentRotations.Dispose();
        if (_animStates.IsCreated) _animStates.Dispose();
        if (_targetIndices.IsCreated) _targetIndices.Dispose();
        if (_activeTargetPositions.IsCreated) _activeTargetPositions.Dispose();
        if (_spatialGrid.IsCreated) _spatialGrid.Dispose();
    }

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();

        // [수정 1] 씬 전환 후 stale Transform 정리 — null이거나 비활성 오브젝트 제거
        ValidTargets.RemoveAll(t => t == null || !t.gameObject.activeInHierarchy);

        _transformAccessArray = new TransformAccessArray(maxActiveEnemies);
        _raycastCommands = new NativeArray<RaycastCommand>(maxActiveEnemies * 2, Allocator.Persistent);
        _raycastHits = new NativeArray<RaycastHit>(maxActiveEnemies * 2, Allocator.Persistent);
        _yVelocities = new NativeArray<float>(maxActiveEnemies, Allocator.Persistent);
        _currentPositions = new NativeArray<Vector3>(maxActiveEnemies, Allocator.Persistent);
        _currentRotations = new NativeArray<Quaternion>(maxActiveEnemies, Allocator.Persistent);
        _animStates = new NativeArray<int>(maxActiveEnemies, Allocator.Persistent);
        _spatialGrid = new NativeParallelMultiHashMap<int, int>(maxActiveEnemies, Allocator.Persistent);
        _targetIndices = new NativeArray<int>(maxActiveEnemies, Allocator.Persistent);
        _activeTargetPositions = new NativeArray<Vector3>(4, Allocator.Persistent);

        _groundQueryParams = new QueryParameters(LayerMask.GetMask("Ground"), false, QueryTriggerInteraction.Ignore, false);
        _mpb = new MaterialPropertyBlock();

        int maxBatches = Mathf.CeilToInt((float)maxActiveEnemies / 1023f) + 1;
        _matrixBatches = new Matrix4x4[maxBatches][];
        _isWalkingBatches = new float[maxBatches][];
        _timeOffsetBatches = new float[maxBatches][];

        for (int i = 0; i < maxBatches; i++)
        {
            _matrixBatches[i] = new Matrix4x4[1023];
            _isWalkingBatches[i] = new float[1023];
            _timeOffsetBatches[i] = new float[1023];
        }

        // [수정 2] FFS 참조만 가져옴. Initialize는 SpawnTransformHandler에서 이미 호출하므로 중복 호출 안 함.
        _ffs = FindFirstObjectByType<FlowFieldSystem>();

        if (IsServerInitialized) StartCoroutine(SpawnWaveRoutine());
    }

    private void Update()
    {
        // 1. 이전 프레임 Job 완료 대기 → 결과 확정
        _movementJobHandle.Complete();
        _hashJobHandle.Complete();

        // 2. 확정된 결과로 변경 처리 및 렌더링
        ProcessPendingChanges();
        DrawZombiesGPU();

        if (Enemies.Count == 0) return;

        // 3. 타겟 위치 갱신
        for (int i = 0; i < ValidTargets.Count && i < 4; i++)
        {
            if (ValidTargets[i] != null && ValidTargets[i].gameObject.activeInHierarchy)
                _activeTargetPositions[i] = ValidTargets[i].position;
        }

        _spatialGrid.Clear();

        // 4. Transform 복사 Job
        CopyPositionsJob copyJob = new CopyPositionsJob { CurrentPositions = _currentPositions };
        JobHandle copyHandle = copyJob.Schedule(_transformAccessArray);

        // 5. 공간 해시 Job
        HashPositionsJob hashJob = new HashPositionsJob
        {
            Positions = _currentPositions,
            SpatialGrid = _spatialGrid.AsParallelWriter(),
            CellSize = cellSize
        };
        _hashJobHandle = hashJob.Schedule(Enemies.Count, 64, copyHandle);

        // 6. 레이캐스트 셋업 Job
        RaycastSetupJob setupJob = new RaycastSetupJob
        {
            Commands = _raycastCommands,
            DownQueryParams = _groundQueryParams
        };
        JobHandle setupHandle = setupJob.Schedule(_transformAccessArray, copyHandle);

        // 수정 후 (전체 배열 사용, setupHandle 의존성 올바르게 연결)
        JobHandle raycastHandle = RaycastCommand.ScheduleBatch(
            _raycastCommands, _raycastHits, 32, setupHandle);

        JobHandle combinedHandle = JobHandle.CombineDependencies(_hashJobHandle, raycastHandle);

        // 7. FlowField Job (FFS가 있을 때만)
        if (_ffs != null)
        {
            JobHandle ffsHandle = _ffs.ScheduleFlowFieldJobs(default, ValidTargets);
            combinedHandle = JobHandle.CombineDependencies(combinedHandle, ffsHandle);
        }

        NativeArray<Vector3> ff0 = default;
        NativeArray<Vector3> ff1 = default;
        NativeArray<Vector3> ff2 = default;
        NativeArray<Vector3> ff3 = default;

        if (_ffs != null && _ffs.NativeFlowFields != null)
        {
            if (_ffs.NativeFlowFields.Length > 0) ff0 = _ffs.NativeFlowFields[0];
            if (_ffs.NativeFlowFields.Length > 1) ff1 = _ffs.NativeFlowFields[1];
            if (_ffs.NativeFlowFields.Length > 2) ff2 = _ffs.NativeFlowFields[2];
            if (_ffs.NativeFlowFields.Length > 3) ff3 = _ffs.NativeFlowFields[3];
        }

        // 8. 이동 Job — [수정 4] ElapsedTime 전달 추가
        EnemyMovementJob moveJob = new EnemyMovementJob
        {
            ActiveTargetPositions = _activeTargetPositions,
            TargetIndices = _targetIndices,
            RaycastHits = _raycastHits,
            AllEnemyPositions = _currentPositions,
            SpatialGrid = _spatialGrid,
            YVelocities = _yVelocities,
            AnimStates = _animStates,
            Rotations = _currentRotations,
            ElapsedTime = Time.time,        // [수정 4] 누락된 ElapsedTime 전달 — sway 애니메이션 정상 동작
            DeltaTime = Time.deltaTime,
            Speed = globalEnemySpeed,
            RotationSpeed = globalRotationSpeed,
            Gravity = 9.81f,
            SeparationRadius = separationRadius,
            SeparationWeight = separationWeight,
            CellSize = cellSize,
            MaxWeightTolerance = maxWeightTolerance,
            FlowField0 = ff0,
            FlowField1 = ff1,
            FlowField2 = ff2,
            FlowField3 = ff3,
            GridCols = _ffs != null ? _ffs.GridCols : 0,
            GridRows = _ffs != null ? _ffs.GridRows : 0,
            AiCellSize = _ffs != null ? _ffs.aiCellSize : 2f,
            BottomLeft = _ffs != null ? _ffs.BottomLeft : Vector3.zero
        };

        _movementJobHandle = moveJob.Schedule(_transformAccessArray, combinedHandle);

        if (_ffs != null) _ffs.RegisterReader(_movementJobHandle);

        JobHandle.ScheduleBatchedJobs();
    }

    private void DrawZombiesGPU()
    {
        int count = Enemies.Count;
        if (count == 0 || zombieMesh == null || zombieMaterial == null) return;

        int batches = Mathf.CeilToInt((float)count / 1023f);

        for (int b = 0; b < batches; b++)
        {
            int batchCount = Mathf.Min(1023, count - (b * 1023));

            for (int i = 0; i < batchCount; i++)
            {
                int globalIdx = b * 1023 + i;
                _matrixBatches[b][i] = Matrix4x4.TRS(_currentPositions[globalIdx], _currentRotations[globalIdx], Vector3.one);
                _isWalkingBatches[b][i] = (_animStates[globalIdx] == 1) ? 1f : 0f;
                _timeOffsetBatches[b][i] = (globalIdx * 0.123f) % 2f;
            }

            _mpb.SetFloatArray(_isWalkingHash, _isWalkingBatches[b]);
            _mpb.SetFloatArray(_timeOffsetHash, _timeOffsetBatches[b]);

            Graphics.DrawMeshInstanced(zombieMesh, 0, zombieMaterial, _matrixBatches[b], batchCount, _mpb);
        }
    }

    private IEnumerator SpawnWaveRoutine()
    {
        yield return new WaitForSeconds(startDelay);
        int randomSeed = UnityEngine.Random.Range(0, 999999);

        // 서버(호스트 포함)는 여기서 직접 스폰
        StartCoroutine(SpawnEnemyLocal(randomSeed, enemiesPerSpawn));

        // 클라이언트들에게만 RPC 전송 (RunLocally = false)
        RpcStartSpawnWave(randomSeed, enemiesPerSpawn);
    }

    // RunLocally = false → 서버는 위에서 이미 스폰했으므로 재실행 안 함
    [ObserversRpc(RunLocally = false)]
    private void RpcStartSpawnWave(int seed, int spawnCount)
    {
        // 클라이언트호스트는 IsServerInitialized = true 이므로 건너뜀
        // 서버에서 이미 SpawnEnemyLocal을 직접 호출했기 때문
        if (IsServerInitialized) return;

        StartCoroutine(SpawnEnemyLocal(seed, spawnCount));
    }

    private IEnumerator SpawnEnemyLocal(int seed, int spawnCount)
    {
        UnityEngine.Random.InitState(seed);
        WaitForSeconds wait01 = new WaitForSeconds(0.01f);

        for (int i = 0; i < spawnCount; i++)
        {
            if (Enemies.Count >= maxActiveEnemies) break;

            Vector3 spawnPos = GetSpawnPointFromMom();
            GameObject newEnemyObj = Instantiate(enemyPrefab, spawnPos, Quaternion.identity);

            if (newEnemyObj.TryGetComponent(out Enemy simpleEnemy))
            {
                Transform target = dedicatedTarget != null && dedicatedTarget.gameObject.activeInHierarchy
                    ? dedicatedTarget
                    : GetClosestTarget(transform.position);
                simpleEnemy.InitializeEnemy(this, target);
                AddEnemy(simpleEnemy);
            }

            yield return wait01;
        }
    }

    private Vector3 GetSpawnPointFromMom()
    {
        Vector2 randomCircle = UnityEngine.Random.insideUnitCircle * spawnRadius;
        return transform.position + new Vector3(randomCircle.x, 1, randomCircle.y);
    }
}