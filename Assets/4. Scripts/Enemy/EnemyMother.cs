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

    [HideInInspector] public int motherIndex = -1;
    [HideInInspector] public Vector3[] injectedStairs;

    private Mesh _enemyMesh;
    private Material _enemyMaterial;

    public List<Enemy> Enemies;
    private Queue<Enemy> _pendingAdds = new Queue<Enemy>();
    private Queue<Enemy> _pendingRemoves = new Queue<Enemy>();
    private Queue<Enemy> _pool = new Queue<Enemy>();

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

    private int _nativeCapacity;

    private FlowFieldSystem _ffs;
    private NativeParallelMultiHashMap<int, int> _spatialGrid;

    private NativeArray<Matrix4x4> _matricesBuffer;
    private NativeArray<float> _isWalkingBuffer;
    private NativeArray<float> _timeOffsetBuffer;

    private NativeArray<Vector3> _structurePositions;
    private NativeArray<Vector3> _structureExtents; // AABB 적용을 위해 Vector3로 변경
    private int _activeStructureCount;
    private const int MaxStructures = 64;

    [SerializeField] private LayerMask _wallDetectionLayer;
    private QueryParameters _forwardQueryParams;

    private const int MaxDynamicTargets = 32;
    private readonly Dictionary<Transform, int> _dynamicSlotMap = new Dictionary<Transform, int>();
    private readonly bool[] _dynamicSlotInUse = new bool[MaxDynamicTargets];

    private int _aiTickCursor;
    private const int AiTickBatchSize = 200;

    private readonly Queue<(int listIndex, int slot)> _pendingTargetIndexChanges = new Queue<(int, int)>();
    private readonly Queue<(int listIndex, byte value)> _pendingSuppressChanges = new Queue<(int, byte)>();

    private NativeArray<byte> _suppressStructureDetection;

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

    public void ReturnToPool(Enemy enemy)
    {
        RemoveEnemy(enemy);
        enemy.gameObject.SetActive(false);
        _pool.Enqueue(enemy);
    }

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
        _targetIndices[newIndex] = enemy.GetTargetIndex();
        _yVelocities[newIndex] = 0f;
        _animStates[newIndex] = 0;
        _currentPositions[newIndex] = enemy.transform.position;
        _currentRotations[newIndex] = enemy.transform.rotation;
        _speeds[newIndex] = enemy.speed;
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
            _targetIndices[removeIndex] = _targetIndices[lastIndex];
            _yVelocities[removeIndex] = _yVelocities[lastIndex];
            _animStates[removeIndex] = _animStates[lastIndex];
            _currentPositions[removeIndex] = _currentPositions[lastIndex];
            _currentRotations[removeIndex] = _currentRotations[lastIndex];
            _speeds[removeIndex] = _speeds[lastIndex];
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
            Enemy enemy = Enemies[i];
            float radius = enemy.hitRadius;

            Vector3 center = enemy.transform.position + enemy.hitCenterOffset;
            Vector3 toEnemy = center - ray.origin;
            float along = Vector3.Dot(toEnemy, ray.direction);
            if (along < -radius || along > closestT + radius) continue;

            float perpSqr = toEnemy.sqrMagnitude - along * along;
            float cullRadius = Mathf.Max(radius * 3f, enemy.hitHeadOffsetDist + enemy.hitHeadRadius);
            if (perpSqr > cullRadius * cullRadius) continue;

            Vector3 headCenter = enemy.transform.position + enemy.hitHeadOffset;
            float tHead = RaySphere(ray.origin, ray.direction, headCenter, enemy.hitHeadRadius);
            if (tHead >= 0f && tHead < closestT)
            {
                closestT = tHead;
                closestEnemy = enemy;
                isHeadshot = true;
                continue;
            }

            float halfExtent = Mathf.Max(0f, enemy.hitHeight * 0.5f - radius);
            Vector3 capBottom = center - new Vector3(0f, halfExtent, 0f);
            Vector3 capTop = center + new Vector3(0f, halfExtent, 0f);

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

            if (y > 0f && y < baba && t >= 0f) return t;

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
        if (_matricesBuffer.IsCreated) _matricesBuffer.Dispose();
        if (_isWalkingBuffer.IsCreated) _isWalkingBuffer.Dispose();
        if (_timeOffsetBuffer.IsCreated) _timeOffsetBuffer.Dispose();
        if (_structurePositions.IsCreated) _structurePositions.Dispose();
        if (_structureExtents.IsCreated) _structureExtents.Dispose(); // AABB 변경
        if (_suppressStructureDetection.IsCreated) _suppressStructureDetection.Dispose();
    }

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();
        AllMothers.Add(this);

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

        _matricesBuffer = new NativeArray<Matrix4x4>(maxActiveEnemies, Allocator.Persistent);
        _isWalkingBuffer = new NativeArray<float>(maxActiveEnemies, Allocator.Persistent);
        _timeOffsetBuffer = new NativeArray<float>(maxActiveEnemies, Allocator.Persistent);
        _structurePositions = new NativeArray<Vector3>(MaxStructures, Allocator.Persistent);
        _structureExtents = new NativeArray<Vector3>(MaxStructures, Allocator.Persistent);
        _suppressStructureDetection = new NativeArray<byte>(maxActiveEnemies, Allocator.Persistent);

        if (enemyPrefab != null && enemyPrefab.TryGetComponent(out Enemy prefabEnemy) && prefabEnemy.data != null)
        {
            _enemyMesh = prefabEnemy.data.mesh;
            _enemyMaterial = prefabEnemy.data.material;
        }
        else
        {
            Debug.LogWarning($"[EnemyMother] {name}: enemyPrefab에 Enemy 컴포넌트 또는 EnemyDataSO가 없습니다.");
        }

        _groundQueryParams = new QueryParameters(LayerMask.GetMask("Ground"), false, QueryTriggerInteraction.Ignore, false);
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

        _ffs = FindFirstObjectByType<FlowFieldSystem>();

        StageHandler sh = StageHandler.Instance;
        if (sh != null && motherIndex >= 0)
        {
            MotherSpawnConfig config = sh.GetConfig(motherIndex);
            if (config != null) Configure(config);
        }

        PlacedStructure.OnAnyDestroyed += OnStructureDestroyed;

        if (IsServerInitialized)
        {
            bool shouldStart = sh == null || (sh.ShouldStartWave && sh.GetConfig(motherIndex)?.isActive == true);
            if (shouldStart) StartSpawning();
        }
    }

    public void Configure(MotherSpawnConfig config)
    {
        maxActiveEnemies = Mathf.Clamp(config.maxEnemies, 1, _nativeCapacity);
        startDelay = config.startDelay;
        spawnInterval = config.spawnInterval;
        enemiesPerSpawn = config.enemiesPerSpawn;
    }

    public void StartSpawning()
    {
        if (IsServerInitialized) StartCoroutine(SpawnWaveRoutine());
    }

    private void Update()
    {
        _movementJobHandle.Complete();
        _hashJobHandle.Complete();

        // 큐 적용 처리
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

        if (PlacedStructure.All.Count > 0 && Enemies.Count > 0)
        {
            float dt = Time.deltaTime;
            float dmgPerSec = PlacedStructure.DamagePerEnemyPerSecond;
            float maxDmg = PlacedStructure.MaxDamagePerSecond * dt;
            float meleeSqr = 16f;

            for (int s = 0; s < PlacedStructure.All.Count; s++)
            {
                PlacedStructure str = PlacedStructure.All[s];
                if (str == null) continue;

                Vector3 sp = str.transform.position;
                float totalDmg = 0f;
                bool done = false;

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
                                Vector3 ep = _currentPositions[eIdx];
                                float ddx = ep.x - sp.x;
                                float ddz = ep.z - sp.z;
                                float dSqr = ddx * ddx + ddz * ddz;

                                if (IsServerInitialized && dSqr < meleeSqr)
                                {
                                    totalDmg += dmgPerSec * dt;
                                    if (totalDmg >= maxDmg) { done = true; break; }
                                }

                                Enemy enemy = Enemies[eIdx];
                                if (enemy.State == EnemyAIState.Attack && enemy.CommittedTarget != str.transform)
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

        ProcessPendingChanges();

        if (Enemies.Count > 0)
        {
            new BuildRenderDataJob
            {
                Positions = _currentPositions,
                Rotations = _currentRotations,
                AnimStates = _animStates,
                Matrices = _matricesBuffer,
                IsWalkingValues = _isWalkingBuffer,
                TimeOffsets = _timeOffsetBuffer
            }.Schedule(Enemies.Count, 64).Complete();
        }
        DrawZombiesGPU();

        if (Enemies.Count == 0) return;

        for (int i = 0; i < ValidTargets.Count && i < 4; i++)
        {
            if (ValidTargets[i] != null && ValidTargets[i].gameObject.activeInHierarchy)
                _activeTargetPositions[i] = ValidTargets[i].position;
        }
        foreach (var kvp in _dynamicSlotMap)
        {
            if (kvp.Key != null && kvp.Key.gameObject.activeInHierarchy)
                _activeTargetPositions[kvp.Value] = kvp.Key.position;
        }

        TickAIStates();

        _activeStructureCount = 0;
        for (int i = 0; i < PlacedStructure.All.Count && _activeStructureCount < MaxStructures; i++)
        {
            var s = PlacedStructure.All[i];
            if (s != null && s.gameObject.activeInHierarchy)
            {
                _structurePositions[_activeStructureCount] = s.transform.position;
                _structureExtents[_activeStructureCount] = s.ColliderExtents; // AABB 적용
                _activeStructureCount++;
            }
        }

        _spatialGrid.Clear();

        CopyPositionsJob copyJob = new CopyPositionsJob { CurrentPositions = _currentPositions };
        JobHandle copyHandle = copyJob.Schedule(_transformAccessArray);

        HashPositionsJob hashJob = new HashPositionsJob
        {
            Positions = _currentPositions,
            SpatialGrid = _spatialGrid.AsParallelWriter(),
            CellSize = cellSize
        };
        _hashJobHandle = hashJob.Schedule(Enemies.Count, 64, copyHandle);

        JobHandle combinedHandle = _hashJobHandle;
        if (Time.frameCount % 2 == 0)
        {
            RaycastSetupJob setupJob = new RaycastSetupJob
            {
                Commands = _raycastCommands,
                DownQueryParams = _groundQueryParams,
                ForwardQueryParams = _forwardQueryParams
            };
            int activeRayCount = Enemies.Count * 2;
            JobHandle setupHandle = setupJob.Schedule(_transformAccessArray, copyHandle);
            JobHandle raycastHandle = RaycastCommand.ScheduleBatch(
                _raycastCommands.GetSubArray(0, activeRayCount),
                _raycastHits.GetSubArray(0, activeRayCount),
                32, setupHandle);
            combinedHandle = JobHandle.CombineDependencies(_hashJobHandle, raycastHandle);
        }

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
            ElapsedTime = Time.time,
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
            GridCols = _ffs != null ? _ffs.GridCols : 0,
            GridRows = _ffs != null ? _ffs.GridRows : 0,
            AiCellSize = _ffs != null ? _ffs.aiCellSize : 2f,
            BottomLeft = _ffs != null ? _ffs.BottomLeft : Vector3.zero,
            CostField = (_ffs != null && _ffs.CostField.IsCreated) ? _ffs.CostField : default,

            StructurePositions = _structurePositions,
            StructureExtents = _structureExtents,
            StructureCount = _activeStructureCount,
            StructureDetectRangeSqr = (_ffs != null ? _ffs.aiCellSize * 4f : 8f) * (_ffs != null ? _ffs.aiCellSize * 4f : 8f),
            SuppressStructureDetection = _suppressStructureDetection
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
            int start = b * 1023;
            int batchCount = Mathf.Min(1023, count - start);

            NativeArray<Matrix4x4>.Copy(_matricesBuffer, start, _matrixBatches[b], 0, batchCount);
            NativeArray<float>.Copy(_isWalkingBuffer, start, _isWalkingBatches[b], 0, batchCount);
            NativeArray<float>.Copy(_timeOffsetBuffer, start, _timeOffsetBatches[b], 0, batchCount);

            _mpb.SetFloatArray(_isWalkingHash, _isWalkingBatches[b]);
            _mpb.SetFloatArray(_timeOffsetHash, _timeOffsetBatches[b]);

            Graphics.DrawMeshInstanced(_enemyMesh, 0, _enemyMaterial, _matrixBatches[b], batchCount, _mpb);
        }
    }

    private IEnumerator SpawnWaveRoutine()
    {
        yield return new WaitForSeconds(startDelay);

        WaitForSeconds interval = new WaitForSeconds(spawnInterval);

        while (Enemies.Count + _pendingAdds.Count < maxActiveEnemies)
        {
            int randomSeed = UnityEngine.Random.Range(0, 999999);
            StartCoroutine(SpawnEnemyLocal(randomSeed, enemiesPerSpawn));
            RpcStartSpawnWave(randomSeed, enemiesPerSpawn);
            yield return interval;
        }
    }

    [ObserversRpc(RunLocally = false)]
    private void RpcStartSpawnWave(int seed, int spawnCount)
    {
        if (IsServerInitialized) return;
        StartCoroutine(SpawnEnemyLocal(seed, spawnCount));
    }

    private int GetOrAssignTargetSlot(Transform target)
    {
        int playerIdx = ValidTargets.IndexOf(target);
        if (playerIdx >= 0) return playerIdx;

        if (_dynamicSlotMap.TryGetValue(target, out int existing)) return existing;

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
        return -1;
    }

    private void ReleaseTargetSlot(Transform target)
    {
        if (!_dynamicSlotMap.TryGetValue(target, out int slot)) return;
        _dynamicSlotMap.Remove(target);
        _dynamicSlotInUse[slot - 4] = false;
    }

    public void CommitEnemyToTarget(Enemy enemy, Transform target)
    {
        int slot = GetOrAssignTargetSlot(target);
        if (slot < 0) return;

        enemy.SetCommittedTarget(target);

        int listIdx = enemy.motherListIndex;
        if (listIdx < 0 || listIdx >= Enemies.Count) return;

        _pendingTargetIndexChanges.Enqueue((listIdx, slot));

        byte suppress = (byte)(slot < 4 ? 1 : 0);
        _pendingSuppressChanges.Enqueue((listIdx, suppress));
    }

    private void OnStructureDestroyed(PlacedStructure structure)
    {
        if (!_dynamicSlotMap.TryGetValue(structure.transform, out int slot)) return;

        for (int i = 0; i < Enemies.Count; i++)
        {
            if (Enemies[i].CommittedTarget == structure.transform)
            {
                Enemies[i].OnCommittedTargetDestroyed();

                // 💡 [버그 1 완벽 해결] 배열에 직접 쓰지 않고 반드시 Queue(대기열)를 통해 변경!
                // 직전에 들어온 공격 명령(Commit)보다 항상 나중에 처리됨을 보장합니다.
                _pendingTargetIndexChanges.Enqueue((i, Enemies[i].GetTargetIndex()));
                _pendingSuppressChanges.Enqueue((i, 1));
            }
        }
        ReleaseTargetSlot(structure.transform);
    }

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

        if (enemy.CommittedTarget == null || !enemy.CommittedTarget.gameObject.activeInHierarchy)
        {
            enemy.OnCommittedTargetDestroyed();
            _pendingTargetIndexChanges.Enqueue((i, enemy.GetTargetIndex()));
            _pendingSuppressChanges.Enqueue((i, 1));
            return;
        }

        Vector3 pos = _currentPositions[i];
        Vector3 tp = enemy.CommittedTarget.position;
        float dx = tp.x - pos.x, dz = tp.z - pos.z;
        float distSqr = dx * dx + dz * dz;
        float attackRangeSqr = enemy.data.attackRange * enemy.data.attackRange;

        switch (enemy.State)
        {
            case EnemyAIState.Chase:
                CheckEncounterForEnemy(i, enemy, pos);
                if (distSqr <= attackRangeSqr)
                    enemy.TransitionToAttack();
                break;

            case EnemyAIState.Attack:
                if (distSqr > attackRangeSqr * 9f)
                {
                    enemy.TransitionToChase();
                    _pendingSuppressChanges.Enqueue((i, 0));
                }
                else
                    CheckProximityAggroFromPlayers(i, enemy, pos, attackRangeSqr);
                break;
        }
    }

    private void CheckEncounterForEnemy(int i, Enemy enemy, Vector3 pos)
    {
        float encounterRadiusSqr = enemy.data.encounterRadius * enemy.data.encounterRadius;
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
                nearest = str.transform;
            }
        }

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
                nearest = vt;
            }
        }

        if (nearest != null)
            CommitEnemyToTarget(enemy, nearest);
    }

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