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

    // P2: 렌더링 데이터 빌드용 NativeArray — BuildRenderDataJob이 Worker Thread에서 채움
    private NativeArray<Matrix4x4> _matricesBuffer;
    private NativeArray<float>     _isWalkingBuffer;
    private NativeArray<float>     _timeOffsetBuffer;

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

        // 최종 방어선: 큐 경합으로 인해 NativeArray 한계를 초과하는 경우 차단
        if (newIndex >= maxActiveEnemies)
        {
            enemy.motherListIndex = -1;
            Destroy(enemy.gameObject);
            return;
        }

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
        if (_matricesBuffer.IsCreated)   _matricesBuffer.Dispose();
        if (_isWalkingBuffer.IsCreated)  _isWalkingBuffer.Dispose();
        if (_timeOffsetBuffer.IsCreated) _timeOffsetBuffer.Dispose();
    }

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();

        // [수정 1] 씬 전환 후 stale Transform 정리 — null이거나 비활성 오브젝트 제거
        // P5: RemoveAll(람다) → 수동 루프로 교체하여 람다 캡처 GC Alloc 제거
        for (int i = ValidTargets.Count - 1; i >= 0; i--)
            if (ValidTargets[i] == null || !ValidTargets[i].gameObject.activeInHierarchy)
                ValidTargets.RemoveAt(i);

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

        // P2: 렌더링 빌드 버퍼 (Worker Thread에서 TRS 계산 결과를 받는 저장소)
        _matricesBuffer  = new NativeArray<Matrix4x4>(maxActiveEnemies, Allocator.Persistent);
        _isWalkingBuffer = new NativeArray<float>(maxActiveEnemies, Allocator.Persistent);
        _timeOffsetBuffer = new NativeArray<float>(maxActiveEnemies, Allocator.Persistent);

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

        // 2. 확정된 결과로 변경 처리
        ProcessPendingChanges();

        // P2: TRS 행렬 + 애니메이션 데이터를 Worker Thread에서 병렬 계산
        // (기존 Main Thread에서 Matrix4x4.TRS × N 반복 → Worker Thread 병렬화)
        if (Enemies.Count > 0)
        {
            new BuildRenderDataJob
            {
                Positions       = _currentPositions,
                Rotations       = _currentRotations,
                AnimStates      = _animStates,
                Matrices        = _matricesBuffer,
                IsWalkingValues = _isWalkingBuffer,
                TimeOffsets     = _timeOffsetBuffer
            }.Schedule(Enemies.Count, 64).Complete();
        }
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

        // 6. P3: 격 프레임 레이캐스트 — 짝수 프레임만 실행 (매 프레임 8000회 → 격 프레임 8000회)
        // 홀수 프레임은 직전 프레임의 _raycastHits를 그대로 재사용 (1프레임 지연, 60fps에서 무시 가능)
        JobHandle combinedHandle = _hashJobHandle;
        if (Time.frameCount % 2 == 0)
        {
            RaycastSetupJob setupJob = new RaycastSetupJob
            {
                Commands        = _raycastCommands,
                DownQueryParams = _groundQueryParams
            };
            // 활성 적 수만큼만 Raycast 처리 — 전체 배열(최대 8000) 대신 실제 사용 슬라이스만 전달
            int activeRayCount      = Enemies.Count * 2;
            JobHandle setupHandle   = setupJob.Schedule(_transformAccessArray, copyHandle);
            JobHandle raycastHandle = RaycastCommand.ScheduleBatch(
                _raycastCommands.GetSubArray(0, activeRayCount),
                _raycastHits.GetSubArray(0, activeRayCount),
                32, setupHandle);
            combinedHandle = JobHandle.CombineDependencies(_hashJobHandle, raycastHandle);
        }

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
            int start      = b * 1023;
            int batchCount = Mathf.Min(1023, count - start);

            // P2: BuildRenderDataJob이 채운 NativeArray → managed array 고속 복사
            // CopyTo(T[])는 길이 완전 일치 필요 → 명시적 length를 지정하는 정적 Copy 오버로드 사용
            NativeArray<Matrix4x4>.Copy(_matricesBuffer,  start, _matrixBatches[b],    0, batchCount);
            NativeArray<float>.Copy    (_isWalkingBuffer, start, _isWalkingBatches[b], 0, batchCount);
            NativeArray<float>.Copy    (_timeOffsetBuffer,start, _timeOffsetBatches[b],0, batchCount);

            _mpb.SetFloatArray(_isWalkingHash,  _isWalkingBatches[b]);
            _mpb.SetFloatArray(_timeOffsetHash, _timeOffsetBatches[b]);

            Graphics.DrawMeshInstanced(zombieMesh, 0, zombieMaterial, _matrixBatches[b], batchCount, _mpb);
        }
    }

    private IEnumerator SpawnWaveRoutine()
    {
        yield return new WaitForSeconds(startDelay);

        // WaitForSeconds를 매 루프마다 new 하지 않도록 캐시 (GC Alloc 방지)
        WaitForSeconds interval = new WaitForSeconds(spawnInterval);

        // maxActiveEnemies에 도달할 때까지 spawnInterval마다 enemiesPerSpawn씩 지속 소환
        // _pendingAdds.Count 포함: 아직 미처리된 대기 적까지 합산해야 실제 한계 초과를 방지
        while (Enemies.Count + _pendingAdds.Count < maxActiveEnemies)
        {
            int randomSeed = UnityEngine.Random.Range(0, 999999);

            // 서버(호스트 포함)는 여기서 직접 스폰
            StartCoroutine(SpawnEnemyLocal(randomSeed, enemiesPerSpawn));

            // 클라이언트들에게만 RPC 전송 (RunLocally = false)
            RpcStartSpawnWave(randomSeed, enemiesPerSpawn);

            yield return interval;
        }
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
            // _pendingAdds까지 합산해 한계 초과 여부 판단 (Enemies.Count만 보면 큐 경합 발생)
            if (Enemies.Count + _pendingAdds.Count >= maxActiveEnemies) break;

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