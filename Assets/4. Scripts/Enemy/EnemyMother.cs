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
    public static readonly List<EnemyMother> AllMothers = new List<EnemyMother>();

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
    public float globalRotationSpeed = 360f;
    public float cellSize = 2.0f;

    // SpawnTransformHandler가 Spawn 직전에 설정 — StageHandler 설정 인덱스와 대응
    [HideInInspector] public int motherIndex = -1;

    [HideInInspector]
    public Vector3[] injectedStairs;

    // GPU Instancing용 메시/머티리얼 — enemyPrefab의 EnemyDataSO에서 자동 캐싱
    private Mesh _enemyMesh;
    private Material _enemyMaterial;

    public List<Enemy> Enemies;
    private Queue<Enemy> _pendingAdds = new Queue<Enemy>();
    private Queue<Enemy> _pendingRemoves = new Queue<Enemy>();
    private Queue<Enemy> _pool = new Queue<Enemy>();

    // 스폰 코루틴 WaitForSeconds 재사용 캐시 (호출마다 new 방지)
    private WaitForSeconds _spawnStepWait;

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
    private NativeArray<float> _speeds;

    // NativeArray 할당 크기 — OnStartNetwork에서 고정됨 (Configure로 변경 불가)
    private int _nativeCapacity;

    private FlowFieldSystem _ffs;
    private NativeParallelMultiHashMap<int, int> _spatialGrid;

    // P2: 렌더링 데이터 빌드용 NativeArray — BuildRenderDataJob이 Worker Thread에서 채움
    private NativeArray<Matrix4x4> _matricesBuffer;
    private NativeArray<float>     _isWalkingBuffer;
    private NativeArray<float>     _timeOffsetBuffer;

    // 구조물 감지용 — EnemyMovementJob에 NativeArray로 전달 (매 프레임 갱신)
    private NativeArray<Vector3> _structurePositions;    // 최대 64개 구조물 위치
    private NativeArray<float>   _structureHalfExtents; // 구조물별 콜라이더 반지름 (정밀 정지용)
    private int _activeStructureCount;
    private const int MaxStructures = 64;

    // 전방 레이캐스트 파라미터 — 건물·벽 레이어 포함 (Inspector에서 _wallDetectionLayer 설정)
    [SerializeField] private LayerMask _wallDetectionLayer;
    private QueryParameters _forwardQueryParams;

    // ─── 동적 타겟 슬롯 (구조물 전용, 인덱스 4~35) ───────────────────────────
    // _activeTargetPositions[0..3] = 플레이어, [4..4+MaxDynamicTargets-1] = 구조물/기타
    private const int MaxDynamicTargets = 32;
    private readonly Dictionary<Transform, int> _dynamicSlotMap = new Dictionary<Transform, int>();
    private readonly bool[] _dynamicSlotInUse = new bool[MaxDynamicTargets];

    // ─── 스태거드 AI 틱 — 200개/프레임씩 처리 ────────────────────────────────
    private int _aiTickCursor;
    private const int AiTickBatchSize = 200;

    // ─── Job 안전 쓰기 큐 ──────────────────────────────────────────────────
    // PlayerInteractor.Update 등에서 Job 실행 중 NativeArray에 직접 쓰는 것을 방지.
    // Complete() 이후 Update 초입에 일괄 적용.
    private readonly Queue<(int listIndex, int slot)> _pendingTargetIndexChanges
        = new Queue<(int, int)>();
    private readonly Queue<(int listIndex, byte value)> _pendingSuppressChanges
        = new Queue<(int, byte)>();

    // ─── 구조물 감지 억제 플래그 ──────────────────────────────────────────
    // Attack 상태에서 플레이어로 커밋된 적이 가까운 구조물 방향으로 끌려가지 않도록
    private NativeArray<byte> _suppressStructureDetection;

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

    // 적이 사망했을 때 호출 — Job 배열에서 제거 후 비활성화하여 풀에 반환
    public void ReturnToPool(Enemy enemy)
    {
        RemoveEnemy(enemy);                  // 다음 ProcessPendingChanges에서 Job 배열 정리
        enemy.gameObject.SetActive(false);   // 즉시 비활성화 (렌더링·이동 제외)
        _pool.Enqueue(enemy);
    }

    // 풀에 재사용 가능한 적이 있으면 꺼내고, 없으면 새로 생성
    private Enemy GetOrCreateEnemy(Vector3 spawnPos)
    {
        if (_pool.Count > 0)
        {
            Enemy pooled = _pool.Dequeue();
            pooled.transform.position = spawnPos;
            pooled.gameObject.SetActive(true);
            return pooled;
        }
        GameObject obj = Instantiate(enemyPrefab, spawnPos, Quaternion.identity);
        return obj.TryGetComponent(out Enemy e) ? e : null;
    }

    private void ProcessPendingChanges()
    {
        while (_pendingRemoves.Count > 0) ExecuteRemoveEnemy(_pendingRemoves.Dequeue());
        while (_pendingAdds.Count > 0) ExecuteAddEnemy(_pendingAdds.Dequeue());
    }

    private void ExecuteAddEnemy(Enemy enemy)
    {
        int newIndex = Enemies.Count;

        // 최종 방어선: 큐 경합으로 인해 NativeArray 한계를 초과하는 경우 Pool로 반환
        if (newIndex >= maxActiveEnemies)
        {
            enemy.motherListIndex = -1;
            enemy.gameObject.SetActive(false);
            _pool.Enqueue(enemy);
            return;
        }

        Enemies.Add(enemy);
        enemy.motherListIndex = newIndex;

        _transformAccessArray.Add(enemy.transform);
        _targetIndices[newIndex]              = enemy.GetTargetIndex();
        _yVelocities[newIndex]                = 0f;
        _animStates[newIndex]                 = 0;
        _currentPositions[newIndex]           = enemy.transform.position;
        _currentRotations[newIndex]           = enemy.transform.rotation;
        _speeds[newIndex]                     = enemy.speed;
        _suppressStructureDetection[newIndex] = 0;
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
            _targetIndices[removeIndex]              = _targetIndices[lastIndex];
            _yVelocities[removeIndex]                = _yVelocities[lastIndex];
            _animStates[removeIndex]                 = _animStates[lastIndex];
            _currentPositions[removeIndex]           = _currentPositions[lastIndex];
            _currentRotations[removeIndex]           = _currentRotations[lastIndex];
            _speeds[removeIndex]                     = _speeds[lastIndex];
            _suppressStructureDetection[removeIndex] = _suppressStructureDetection[lastIndex];
        }
        enemy.motherListIndex = -1;
    }

    private void Awake()
    {
        Enemies = new List<Enemy>(maxActiveEnemies);
    }

    public Enemy GetEnemyHitByRay(Ray ray, float maxRange, out float hitDist, out bool isHeadshot)
    {
        Enemy closestEnemy = null;
        float closestT = maxRange;
        isHeadshot = false;

        for (int i = 0; i < Enemies.Count; i++)
        {
            Enemy enemy  = Enemies[i];
            float radius = enemy.hitRadius;

            // 1차 컬링: Ray 방향 기준 투영 거리로 후방/사거리 초과 적 조기 제외
            Vector3 center  = enemy.transform.position + enemy.hitCenterOffset;
            Vector3 toEnemy = center - ray.origin;
            float along = Vector3.Dot(toEnemy, ray.direction);
            if (along < -radius || along > closestT + radius) continue;

            // 2차 컬링: 몸통 + 머리를 모두 포함하는 경계 반경으로 판정
            // 머리 구체가 몸통 중심에서 떨어져 있으므로, 그 거리를 컬링 반경에 포함해야 함
            float perpSqr = toEnemy.sqrMagnitude - along * along;
            float cullRadius = Mathf.Max(radius * 3f, enemy.hitHeadOffsetDist + enemy.hitHeadRadius);
            if (perpSqr > cullRadius * cullRadius) continue;

            // 머리 구체 검사 (Body보다 먼저 — 겹치는 경우 Head 우선)
            Vector3 headCenter = enemy.transform.position + enemy.hitHeadOffset;
            float tHead = RaySphere(ray.origin, ray.direction, headCenter, enemy.hitHeadRadius);
            if (tHead >= 0f && tHead < closestT)
            {
                closestT = tHead;
                closestEnemy = enemy;
                isHeadshot = true;
                continue;
            }

            // Body 캡슐 검사
            float halfExtent = Mathf.Max(0f, enemy.hitHeight * 0.5f - radius);
            Vector3 capBottom = center - new Vector3(0f, halfExtent, 0f);
            Vector3 capTop    = center + new Vector3(0f, halfExtent, 0f);

            float t = RayCapsule(ray.origin, ray.direction, capBottom, capTop, radius);
            if (t >= 0f && t < closestT)
            {
                closestT = t;
                closestEnemy = enemy;
                isHeadshot = false;
            }
        }

        hitDist = closestT;
        return closestEnemy;
    }

    // Ray-Sphere 교차 공식 (헤드샷 판정용)
    // 반환값: 교차 거리(t >= 0), 미교차 시 -1
    private static float RaySphere(Vector3 ro, Vector3 rd, Vector3 center, float r)
    {
        Vector3 oc = ro - center;
        float b = Vector3.Dot(rd, oc);
        float c = Vector3.Dot(oc, oc) - r * r;
        float h = b * b - c;
        if (h < 0f) return -1f;
        float t = -b - Mathf.Sqrt(h);
        return t >= 0f ? t : -1f;
    }

    // Inigo Quilez의 Ray-Capsule 교차 공식
    // 반환값: 교차 거리(t >= 0), 미교차 시 -1
    private static float RayCapsule(Vector3 ro, Vector3 rd, Vector3 pa, Vector3 pb, float r)
    {
        Vector3 ba = pb - pa;
        Vector3 oa = ro - pa;

        float baba = Vector3.Dot(ba, ba);
        float bard = Vector3.Dot(ba, rd);
        float baoa = Vector3.Dot(ba, oa);
        float rdoa = Vector3.Dot(rd, oa);
        float oaoa = Vector3.Dot(oa, oa);

        float a = baba - bard * bard;
        float b = baba * rdoa - baoa * bard;
        float c = baba * oaoa - baoa * baoa - r * r * baba;
        float h = b * b - a * c;

        if (h >= 0f && a > 1e-6f)
        {
            float t = (-b - Mathf.Sqrt(h)) / a;
            float y = baoa + t * bard;

            // 원통 몸체 적중
            if (y > 0f && y < baba && t >= 0f) return t;

            // 구형 캡 적중 (위/아래)
            Vector3 oc = y <= 0f ? oa : ro - pb;
            b = Vector3.Dot(rd, oc);
            c = Vector3.Dot(oc, oc) - r * r;
            h = b * b - c;
            if (h > 0f)
            {
                float t2 = -b - Mathf.Sqrt(h);
                if (t2 >= 0f) return t2;
            }
        }
        return -1f;
    }

    public override void OnStopNetwork()
    {
        base.OnStopNetwork();
        AllMothers.Remove(this);
        PlacedStructure.OnAnyDestroyed -= OnStructureDestroyed;
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
        if (_speeds.IsCreated) _speeds.Dispose();
        if (_spatialGrid.IsCreated) _spatialGrid.Dispose();
        if (_matricesBuffer.IsCreated)      _matricesBuffer.Dispose();
        if (_isWalkingBuffer.IsCreated)     _isWalkingBuffer.Dispose();
        if (_timeOffsetBuffer.IsCreated)    _timeOffsetBuffer.Dispose();
        if (_structurePositions.IsCreated)          _structurePositions.Dispose();
        if (_structureHalfExtents.IsCreated)        _structureHalfExtents.Dispose();
        if (_suppressStructureDetection.IsCreated)  _suppressStructureDetection.Dispose();
    }

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();
        AllMothers.Add(this);

        // [수정 1] 씬 전환 후 stale Transform 정리 — null이거나 비활성 오브젝트 제거
        // P5: RemoveAll(람다) → 수동 루프로 교체하여 람다 캡처 GC Alloc 제거
        for (int i = ValidTargets.Count - 1; i >= 0; i--)
            if (ValidTargets[i] == null || !ValidTargets[i].gameObject.activeInHierarchy)
                ValidTargets.RemoveAt(i);

        _nativeCapacity = maxActiveEnemies;
        _transformAccessArray = new TransformAccessArray(maxActiveEnemies);
        _raycastCommands = new NativeArray<RaycastCommand>(maxActiveEnemies * 2, Allocator.Persistent);
        _raycastHits = new NativeArray<RaycastHit>(maxActiveEnemies * 2, Allocator.Persistent);
        _yVelocities = new NativeArray<float>(maxActiveEnemies, Allocator.Persistent);
        _currentPositions = new NativeArray<Vector3>(maxActiveEnemies, Allocator.Persistent);
        _currentRotations = new NativeArray<Quaternion>(maxActiveEnemies, Allocator.Persistent);
        _animStates = new NativeArray<int>(maxActiveEnemies, Allocator.Persistent);
        _spatialGrid = new NativeParallelMultiHashMap<int, int>(maxActiveEnemies, Allocator.Persistent);
        _targetIndices = new NativeArray<int>(maxActiveEnemies, Allocator.Persistent);
        _activeTargetPositions = new NativeArray<Vector3>(4 + MaxDynamicTargets, Allocator.Persistent);
        _speeds = new NativeArray<float>(maxActiveEnemies, Allocator.Persistent);

        // P2: 렌더링 빌드 버퍼 (Worker Thread에서 TRS 계산 결과를 받는 저장소)
        _matricesBuffer    = new NativeArray<Matrix4x4>(maxActiveEnemies, Allocator.Persistent);
        _isWalkingBuffer   = new NativeArray<float>(maxActiveEnemies, Allocator.Persistent);
        _timeOffsetBuffer  = new NativeArray<float>(maxActiveEnemies, Allocator.Persistent);
        _structurePositions         = new NativeArray<Vector3>(MaxStructures, Allocator.Persistent);
        _structureHalfExtents       = new NativeArray<float>(MaxStructures, Allocator.Persistent);
        _suppressStructureDetection = new NativeArray<byte>(maxActiveEnemies, Allocator.Persistent);

        if (enemyPrefab != null && enemyPrefab.TryGetComponent(out Enemy prefabEnemy) && prefabEnemy.data != null)
        {
            _enemyMesh     = prefabEnemy.data.mesh;
            _enemyMaterial = prefabEnemy.data.material;
        }
        else
        {
            Debug.LogWarning($"[EnemyMother] {name}: enemyPrefab에 Enemy 컴포넌트 또는 EnemyDataSO가 없습니다.");
        }

        _groundQueryParams  = new QueryParameters(LayerMask.GetMask("Ground"), false, QueryTriggerInteraction.Ignore, false);
        // 전방 레이캐스트 — Inspector에서 설정한 wallDetectionLayer (건물 포함) 사용
        // 미설정 시 Ground만 사용 (기존 동작 유지)
        int fwdMask = _wallDetectionLayer.value != 0 ? _wallDetectionLayer.value : LayerMask.GetMask("Ground");
        _forwardQueryParams = new QueryParameters(fwdMask, false, QueryTriggerInteraction.Ignore, false);
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

        // StageHandler가 있으면 인덱스로 설정 조회 → Configure (모든 클라이언트)
        StageHandler sh = StageHandler.Instance;
        if (sh != null && motherIndex >= 0)
        {
            MotherSpawnConfig config = sh.GetConfig(motherIndex);
            if (config != null) Configure(config);
        }

        PlacedStructure.OnAnyDestroyed += OnStructureDestroyed;

        // Wave 시작 여부 결정 (서버만 SpawnWaveRoutine 실행)
        // StageHandler 없음  → 기존 동작 그대로 자동 시작
        // StageHandler 있음  → ShouldStartWave AND 해당 Mother isActive 일 때만 시작
        if (IsServerInitialized)
        {
            bool shouldStart = sh == null
                || (sh.ShouldStartWave && sh.GetConfig(motherIndex)?.isActive == true);

            if (shouldStart) StartSpawning();
        }
    }

    // ─── StageHandler에서 호출 ──────────────────────────────────────────────────

    /// <summary>
    /// 모든 클라이언트에서 호출 — enemyPrefab 교체 및 스폰 파라미터 설정.
    /// NativeArray 크기는 _nativeCapacity를 초과할 수 없음.
    /// </summary>
    public void Configure(MotherSpawnConfig config)
    {
        maxActiveEnemies = Mathf.Clamp(config.maxEnemies, 1, _nativeCapacity);
        startDelay       = config.startDelay;
        spawnInterval    = config.spawnInterval;
        enemiesPerSpawn  = config.enemiesPerSpawn;
    }

    /// <summary>
    /// 서버에서만 호출 — SpawnWaveRoutine 시작.
    /// </summary>
    public void StartSpawning()
    {
        if (IsServerInitialized) StartCoroutine(SpawnWaveRoutine());
    }

    private void Update()
    {
        // 1. 이전 프레임 Job 완료 대기 → 결과 확정
        _movementJobHandle.Complete();
        _hashJobHandle.Complete();

        // 1b. Job 완료 후 NativeArray 지연 쓰기 일괄 적용
        //     (PlayerInteractor 등이 Job 실행 중 직접 쓰면 AtomicSafety 오류 발생)
        while (_pendingTargetIndexChanges.Count > 0)
        {
            var (listIdx, slot) = _pendingTargetIndexChanges.Dequeue();
            if (listIdx >= 0 && listIdx < Enemies.Count)
                _targetIndices[listIdx] = slot;
        }
        while (_pendingSuppressChanges.Count > 0)
        {
            var (listIdx, val) = _pendingSuppressChanges.Dequeue();
            if (listIdx >= 0 && listIdx < Enemies.Count)
                _suppressStructureDetection[listIdx] = val;
        }

        // 2a. 플레이어 구조물 근접 대미지 (서버만)
        // 공간 해시 그리드(_spatialGrid, 이전 프레임 완료)로 O(structures × cells) 조회
        // → 구조물당 최대 MaxDamagePerSecond 캡으로 DPS 일정 유지
        if (PlacedStructure.All.Count > 0 && Enemies.Count > 0)
        {
            float dt          = Time.deltaTime;
            float dmgPerSec   = PlacedStructure.DamagePerEnemyPerSecond;
            float maxDmg      = PlacedStructure.MaxDamagePerSecond * dt;
            // 대미지 반경: 건물 중심 기준 최대 4m
            float meleeSqr    = 16f; // 4m 반경²

            for (int s = 0; s < PlacedStructure.All.Count; s++)
            {
                PlacedStructure str = PlacedStructure.All[s];
                if (str == null) continue;

                Vector3 sp      = str.transform.position;
                float totalDmg  = 0f;
                bool  done      = false;

                // 3×3 셀 탐색 — 공간 해시로 인접 적 빠르게 찾기
                for (int cx = -1; cx <= 1 && !done; cx++)
                {
                    for (int cz = -1; cz <= 1 && !done; cz++)
                    {
                        int hash = HashPositionsJob.GetGridHash(
                            new Unity.Mathematics.float3(sp.x + cx * cellSize, 0f, sp.z + cz * cellSize), cellSize);

                        if (_spatialGrid.TryGetFirstValue(hash, out int eIdx, out var it))
                        {
                            do
                            {
                                Vector3 ep  = _currentPositions[eIdx];
                                float   ddx = ep.x - sp.x;
                                float   ddz = ep.z - sp.z;
                                float   dSqr = ddx * ddx + ddz * ddz;

                                // 대미지 (서버만)
                                if (IsServerInitialized && dSqr < meleeSqr)
                                {
                                    totalDmg += dmgPerSec * dt;
                                    if (totalDmg >= maxDmg) { done = true; break; }
                                }

                                // 요구사항 3a: 공격 단계에서 아주 가까운 구조물 → 해당 구조물로 전환
                                Enemy enemy = Enemies[eIdx];
                                if (enemy.State == EnemyAIState.Attack
                                    && enemy.CommittedTarget != str.transform)
                                {
                                    float attackRangeSqr = enemy.data.attackRange * enemy.data.attackRange;
                                    if (dSqr < attackRangeSqr)
                                        CommitEnemyToTarget(enemy, str.transform);
                                }
                            }
                            while (_spatialGrid.TryGetNextValue(out eIdx, ref it));
                        }
                    }
                }
                if (IsServerInitialized && totalDmg > 0f) str.TakeDamage(totalDmg);
            }
        }

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

        // 3. 타겟 위치 갱신 — 플레이어(0..3) + 동적 슬롯(4..35)
        for (int i = 0; i < ValidTargets.Count && i < 4; i++)
        {
            if (ValidTargets[i] != null && ValidTargets[i].gameObject.activeInHierarchy)
                _activeTargetPositions[i] = ValidTargets[i].position;
        }
        // 동적 슬롯(구조물 등) 위치 갱신 — Dictionary 열거는 struct Enumerator, GC Alloc 없음
        foreach (var kvp in _dynamicSlotMap)
        {
            if (kvp.Key != null && kvp.Key.gameObject.activeInHierarchy)
                _activeTargetPositions[kvp.Value] = kvp.Key.position;
        }

        // 3c. 스태거드 AI 틱 — 어그로 상태 전환 처리 (200개/프레임)
        TickAIStates();

        // 3b. 구조물 위치 배열 갱신 (Job에 ReadOnly로 넘김 — 매 프레임 갱신, GC Alloc 없음)
        _activeStructureCount = 0;
        for (int i = 0; i < PlacedStructure.All.Count && _activeStructureCount < MaxStructures; i++)
        {
            var s = PlacedStructure.All[i];
            if (s != null && s.gameObject.activeInHierarchy)
            {
                _structurePositions[_activeStructureCount]  = s.transform.position;
                _structureHalfExtents[_activeStructureCount] = s.ColliderHalfExtent;
                _activeStructureCount++;
            }
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
                Commands         = _raycastCommands,
                DownQueryParams  = _groundQueryParams,
                ForwardQueryParams = _forwardQueryParams
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
            Speeds = _speeds,
            RotationSpeed = globalRotationSpeed,
            Gravity = 9.81f,
            SeparationRadius = separationRadius,
            SeparationWeight = separationWeight,
            CellSize = cellSize,
            FlowField0 = ff0,
            FlowField1 = ff1,
            FlowField2 = ff2,
            FlowField3 = ff3,
            GridCols   = _ffs != null ? _ffs.GridCols  : 0,
            GridRows   = _ffs != null ? _ffs.GridRows  : 0,
            AiCellSize = _ffs != null ? _ffs.aiCellSize : 2f,
            BottomLeft = _ffs != null ? _ffs.BottomLeft : Vector3.zero,
            // cost==255 셀 진입 차단 — 영구 장애물 통과 방지 (물리 연산 0회)
            CostField  = (_ffs != null && _ffs.CostField.IsCreated) ? _ffs.CostField : default,
            // 구조물 감지 — 탐지 반경 내 플레이어 구조물 방향으로 이동 방향 전환
            StructurePositions           = _structurePositions,
            StructureHalfExtents         = _structureHalfExtents,
            StructureCount               = _activeStructureCount,
            StructureDetectRangeSqr      = (_ffs != null ? _ffs.aiCellSize * 4f : 8f) *
                                           (_ffs != null ? _ffs.aiCellSize * 4f : 8f),  // 4셀 반경²
            SuppressStructureDetection   = _suppressStructureDetection
        };

        _movementJobHandle = moveJob.Schedule(_transformAccessArray, combinedHandle);

        if (_ffs != null) _ffs.RegisterReader(_movementJobHandle);

        JobHandle.ScheduleBatchedJobs();
    }

    private void DrawZombiesGPU()
    {
        int count = Enemies.Count;
        if (count == 0 || _enemyMesh == null || _enemyMaterial == null) return;

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

            Graphics.DrawMeshInstanced(_enemyMesh, 0, _enemyMaterial, _matrixBatches[b], batchCount, _mpb);
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

    // ─── 동적 타겟 슬롯 관리 ─────────────────────────────────────────────────

    private int GetOrAssignTargetSlot(Transform target)
    {
        // 플레이어 슬롯 (0..3)
        int playerIdx = ValidTargets.IndexOf(target);
        if (playerIdx >= 0) return playerIdx;

        // 이미 배정된 동적 슬롯
        if (_dynamicSlotMap.TryGetValue(target, out int existing)) return existing;

        // 빈 슬롯 배정
        for (int i = 0; i < MaxDynamicTargets; i++)
        {
            if (!_dynamicSlotInUse[i])
            {
                int slot = 4 + i;
                _dynamicSlotMap[target] = slot;
                _dynamicSlotInUse[i] = true;
                _activeTargetPositions[slot] = target.position;
                return slot;
            }
        }
        return -1; // 슬롯 부족 시 폴백
    }

    private void ReleaseTargetSlot(Transform target)
    {
        if (!_dynamicSlotMap.TryGetValue(target, out int slot)) return;
        _dynamicSlotMap.Remove(target);
        _dynamicSlotInUse[slot - 4] = false;
    }

    /// <summary>
    /// 적을 특정 타겟으로 커밋. Enemy.cs HandleAttackAggro 콜백 및 AI 틱에서 사용.
    /// _targetIndices / _suppressStructureDetection 쓰기는 Job 안전성을 위해 큐로 지연.
    /// </summary>
    public void CommitEnemyToTarget(Enemy enemy, Transform target)
    {
        int slot = GetOrAssignTargetSlot(target);
        if (slot < 0) return;

        enemy.SetCommittedTarget(target);

        int listIdx = enemy.motherListIndex;
        if (listIdx < 0 || listIdx >= Enemies.Count) return;

        // 버그 1 수정: Job 실행 중 직접 쓰지 않고 큐에 등록 → Complete() 이후 적용
        _pendingTargetIndexChanges.Enqueue((listIdx, slot));

        // 버그 2 수정: 플레이어 슬롯(0~3)으로 커밋 시 구조물 감지 억제
        //              구조물 슬롯(4+)으로 커밋 시 억제 해제
        byte suppress = (byte)(slot < 4 ? 1 : 0);
        _pendingSuppressChanges.Enqueue((listIdx, suppress));
    }

    private void OnStructureDestroyed(PlacedStructure structure)
    {
        if (!_dynamicSlotMap.TryGetValue(structure.transform, out int slot)) return;

        // 이 슬롯을 추적하던 적을 초기 플레이어로 폴백
        // 이 메서드는 EnemyMother.Update() 내 Complete() 이후에 호출되므로 직접 쓰기 안전
        for (int i = 0; i < Enemies.Count; i++)
        {
            if (_targetIndices[i] != slot) continue;

            Enemies[i].OnCommittedTargetDestroyed();
            _targetIndices[i] = Enemies[i].GetTargetIndex();

            // 버그 1 수정: 파괴된 구조물이 이번 프레임 _structurePositions에 아직 남아있어
            // Job이 해당 위치로 재유도하는 것을 억제. 다음 AI 틱에서 새 타겟 만나면 자동 해제.
            _suppressStructureDetection[i] = 1;
        }
        ReleaseTargetSlot(structure.transform);
    }

    // ─── 스태거드 AI 틱 ──────────────────────────────────────────────────────

    private void TickAIStates()
    {
        if (Enemies.Count == 0) return;

        int end = Mathf.Min(_aiTickCursor + AiTickBatchSize, Enemies.Count);
        for (int i = _aiTickCursor; i < end; i++)
            UpdateSingleEnemyAI(i);

        _aiTickCursor = end >= Enemies.Count ? 0 : end;
    }

    private void UpdateSingleEnemyAI(int i)
    {
        Enemy enemy = Enemies[i];
        if (enemy.CommittedTarget == null) return;

        Vector3 pos  = _currentPositions[i];
        Vector3 tp   = enemy.CommittedTarget.position;
        float dx = tp.x - pos.x, dz = tp.z - pos.z;
        float distSqr = dx * dx + dz * dz;
        float attackRangeSqr = enemy.data.attackRange * enemy.data.attackRange;

        switch (enemy.State)
        {
            case EnemyAIState.Chase:
                // 요구사항 1: 마주치는 플레이어 관련 오브젝트 감지 → 즉시 커밋
                CheckEncounterForEnemy(i, enemy, pos);
                // 공격 범위 진입 → Attack 전환
                if (distSqr <= attackRangeSqr)
                    enemy.TransitionToAttack();
                break;

            case EnemyAIState.Attack:
                // 타겟이 멀어지면 Chase로 복귀 + 억제 플래그 해제
                if (distSqr > attackRangeSqr * 9f)
                {
                    enemy.TransitionToChase();
                    _pendingSuppressChanges.Enqueue((i, 0));
                }
                // 요구사항 3a: 다른 플레이어(ValidTarget) 중 아주 가까운 것 → 전환
                else
                    CheckProximityAggroFromPlayers(i, enemy, pos, attackRangeSqr);
                break;
        }
    }

    /// <summary>
    /// Chase 상태의 적에게 가장 가까운 플레이어 관련 오브젝트(구조물 / 비할당 플레이어)를 감지해 커밋.
    /// 이미 커밋된 타겟보다 더 가까운 오브젝트가 있을 때만 전환.
    /// </summary>
    private void CheckEncounterForEnemy(int i, Enemy enemy, Vector3 pos)
    {
        float encounterRadiusSqr = enemy.data.encounterRadius * enemy.data.encounterRadius;

        // 버그 2 수정: nearestSqr 초기값을 현재 커밋 타겟까지의 거리로 제한.
        // "현재 커밋 타겟보다 더 가까운 것만 전환" → 플레이어가 구조물 뒤에서 접근해도
        // 구조물이 더 가까우므로 전환되지 않아 "끌려오는" 현상 방지.
        float nearestSqr = encounterRadiusSqr;
        if (enemy.CommittedTarget != null)
        {
            Vector3 ct = enemy.CommittedTarget.position;
            float cdx = ct.x - pos.x, cdz = ct.z - pos.z;
            float committedDistSqr = cdx * cdx + cdz * cdz;
            if (committedDistSqr < nearestSqr)
                nearestSqr = committedDistSqr;
        }

        Transform nearest = null;

        // 구조물 체크
        for (int s = 0; s < PlacedStructure.All.Count; s++)
        {
            PlacedStructure str = PlacedStructure.All[s];
            if (str == null || !str.gameObject.activeInHierarchy) continue;
            if (str.transform == enemy.CommittedTarget) continue;

            Vector3 sp = str.transform.position;
            float dSqr = (sp.x - pos.x) * (sp.x - pos.x) + (sp.z - pos.z) * (sp.z - pos.z);
            if (dSqr < nearestSqr)
            {
                nearestSqr = dSqr;
                nearest    = str.transform;
            }
        }

        // 비할당 플레이어 체크
        for (int t = 0; t < ValidTargets.Count && t < 4; t++)
        {
            Transform vt = ValidTargets[t];
            if (vt == null || !vt.gameObject.activeInHierarchy) continue;
            if (vt == enemy.CommittedTarget) continue;

            Vector3 tp = _activeTargetPositions[t];
            float dSqr = (tp.x - pos.x) * (tp.x - pos.x) + (tp.z - pos.z) * (tp.z - pos.z);
            if (dSqr < nearestSqr)
            {
                nearestSqr = dSqr;
                nearest    = vt;
            }
        }

        if (nearest != null)
            CommitEnemyToTarget(enemy, nearest);
    }

    /// <summary>
    /// Attack 상태에서 현재 타겟 외 다른 ValidTarget(플레이어)이 공격 범위 이내이면 전환.
    /// 구조물 근접 전환은 기존 구조물 루프에서 처리.
    /// </summary>
    private void CheckProximityAggroFromPlayers(int i, Enemy enemy, Vector3 pos, float attackRangeSqr)
    {
        for (int t = 0; t < ValidTargets.Count && t < 4; t++)
        {
            Transform vt = ValidTargets[t];
            if (vt == null || !vt.gameObject.activeInHierarchy) continue;
            if (vt == enemy.CommittedTarget) continue;

            Vector3 tp = _activeTargetPositions[t];
            float dSqr = (tp.x - pos.x) * (tp.x - pos.x) + (tp.z - pos.z) * (tp.z - pos.z);
            if (dSqr < attackRangeSqr)
            {
                CommitEnemyToTarget(enemy, vt);
                return;
            }
        }
    }

    private IEnumerator SpawnEnemyLocal(int seed, int spawnCount)
    {
        UnityEngine.Random.InitState(seed);
        _spawnStepWait ??= new WaitForSeconds(0.01f);

        for (int i = 0; i < spawnCount; i++)
        {
            // _pendingAdds까지 합산해 한계 초과 여부 판단 (Enemies.Count만 보면 큐 경합 발생)
            if (Enemies.Count + _pendingAdds.Count >= maxActiveEnemies) break;

            Vector3 spawnPos = GetSpawnPointFromMom();
            Enemy simpleEnemy = GetOrCreateEnemy(spawnPos);

            if (simpleEnemy != null)
            {
                Transform target = dedicatedTarget != null && dedicatedTarget.gameObject.activeInHierarchy
                    ? dedicatedTarget
                    : GetClosestTarget(transform.position);
                simpleEnemy.InitializeEnemy(this, target);
                AddEnemy(simpleEnemy);
            }

            yield return _spawnStepWait;
        }
    }

    private Vector3 GetSpawnPointFromMom()
    {
        Vector2 randomCircle = UnityEngine.Random.insideUnitCircle * spawnRadius;
        return transform.position + new Vector3(randomCircle.x, 1, randomCircle.y);
    }
}