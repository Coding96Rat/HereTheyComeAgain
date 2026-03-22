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
    public NetworkObject enemyPrefab;
    public static List<Transform> ValidTargets = new List<Transform>();

    public Transform dedicatedTarget;

    [Header("스폰 설정")]
    public float startDelay = 5f;
    public float spawnInterval = 1f;
    public int enemiesPerSpawn = 5;
    public int maxActiveEnemies = 4000;
    public float spawnRadius = 20f;

    public List<Enemy> Enemies;

    private TransformAccessArray _transformAccessArray;
    private NativeArray<Vector3> _currentPositions;
    private JobHandle _movementJobHandle;

    private NativeArray<RaycastCommand> _raycastCommands;
    private NativeArray<RaycastHit> _raycastHits;
    private NativeArray<float> _yVelocities;
    private NativeArray<int> _animStates;
    private QueryParameters _groundQueryParams;

    private NativeArray<int> _targetIndices;
    private NativeArray<Vector3> _activeTargetPositions; // 💡 타겟 위치를 담아 Job으로 한 번에 쏘는 배열

    private FlowFieldSystem _ffs;
    private NativeParallelMultiHashMap<int, int> _spatialGrid;

    [Header("물리 설정")]
    public float separationRadius = 1.5f;
    public float separationWeight = 2.0f;
    public float globalEnemySpeed = 3f;
    public float globalRotationSpeed = 360f;
    public float cellSize = 2.0f;
    public float maxWeightTolerance = 3.0f;

    [HideInInspector]
    public Vector3[] injectedStairs;

    private MaterialPropertyBlock _mpb;
    private readonly int _isWalkingHash = Shader.PropertyToID("_IsWalking");
    private readonly int _timeOffsetHash = Shader.PropertyToID("_TimeOffset");

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

    public void AddEnemy(Enemy enemy)
    {
        int newIndex = Enemies.Count;
        Enemies.Add(enemy);
        enemy.motherListIndex = newIndex;

        _transformAccessArray.Add(enemy.transform);
        _targetIndices[newIndex] = enemy.GetTargetIndex(); // 💡 스폰 시 인덱스만 캐싱
        _yVelocities[newIndex] = 0f;
        _animStates[newIndex] = 0;

        if (enemy.myRenderer != null)
        {
            enemy.myRenderer.GetPropertyBlock(_mpb);
            _mpb.SetFloat(_timeOffsetHash, UnityEngine.Random.Range(0f, 2f));
            _mpb.SetFloat(_isWalkingHash, 0f);
            enemy.myRenderer.SetPropertyBlock(_mpb);
        }
    }

    public void RemoveEnemy(Enemy enemy)
    {
        if (!_transformAccessArray.isCreated) return;
        _movementJobHandle.Complete();

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

            // 💡 [치명적 버그 수정] 모든 데이터 배열을 당겨와야 배열 오염이 안 생깁니다.
            _targetIndices[removeIndex] = _targetIndices[lastIndex];
            _yVelocities[removeIndex] = _yVelocities[lastIndex];
            _animStates[removeIndex] = _animStates[lastIndex];
            _currentPositions[removeIndex] = _currentPositions[lastIndex];
        }
        enemy.motherListIndex = -1;
    }

    private void Awake()
    {
        Enemies = new List<Enemy>(maxActiveEnemies);
        _transformAccessArray = new TransformAccessArray(maxActiveEnemies);
        _raycastCommands = new NativeArray<RaycastCommand>(maxActiveEnemies * 2, Allocator.Persistent);
        _raycastHits = new NativeArray<RaycastHit>(maxActiveEnemies * 2, Allocator.Persistent);
        _yVelocities = new NativeArray<float>(maxActiveEnemies, Allocator.Persistent);
        _currentPositions = new NativeArray<Vector3>(maxActiveEnemies, Allocator.Persistent);
        _animStates = new NativeArray<int>(maxActiveEnemies, Allocator.Persistent);
        _spatialGrid = new NativeParallelMultiHashMap<int, int>(maxActiveEnemies, Allocator.Persistent);
        _targetIndices = new NativeArray<int>(maxActiveEnemies, Allocator.Persistent);
        _activeTargetPositions = new NativeArray<Vector3>(4, Allocator.Persistent); // 최대 4인 타겟용

        _groundQueryParams = new QueryParameters(LayerMask.GetMask("Ground"), false, QueryTriggerInteraction.Ignore, false);
        _mpb = new MaterialPropertyBlock();
    }

    private void OnDestroy()
    {
        _movementJobHandle.Complete();
        if (_transformAccessArray.isCreated) _transformAccessArray.Dispose();
        if (_raycastCommands.IsCreated) _raycastCommands.Dispose();
        if (_raycastHits.IsCreated) _raycastHits.Dispose();
        if (_yVelocities.IsCreated) _yVelocities.Dispose();
        if (_currentPositions.IsCreated) _currentPositions.Dispose();
        if (_animStates.IsCreated) _animStates.Dispose();
        if (_targetIndices.IsCreated) _targetIndices.Dispose();
        if (_activeTargetPositions.IsCreated) _activeTargetPositions.Dispose();
        if (_spatialGrid.IsCreated) _spatialGrid.Dispose();
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        _ffs = FindFirstObjectByType<FlowFieldSystem>();

        // 💡 [여기가 빠져있었습니다!] FFS 엔진 시동 켜기
        if (_ffs != null)
        {
            _ffs.Initialize(4); // FFS 도화지 세팅 및 굽기
            _ffs.StartUpdatingFlowFields(ValidTargets); // 플레이어 목록 연결
            Debug.Log("[EnemyMother] FFS 엔진 시동 완료!");
        }

        base.NetworkManager.ObjectPool.CacheObjects(enemyPrefab, maxActiveEnemies, true);
        StartCoroutine(SpawnWaveRoutine());
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        base.NetworkManager.ObjectPool.CacheObjects(enemyPrefab, maxActiveEnemies, false);
    }

    private void Update()
    {
        if (Enemies.Count == 0) return;

        // 💡 [초고속 최적화] 4000번 반복하던 루프 삭제. 현재 유효한 타겟(플레이어 1~4명) 좌표만 업데이트.
        for (int i = 0; i < ValidTargets.Count && i < 4; i++)
        {
            if (ValidTargets[i] != null && ValidTargets[i].gameObject.activeInHierarchy)
            {
                _activeTargetPositions[i] = ValidTargets[i].position;
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
        JobHandle hashHandle = hashJob.Schedule(Enemies.Count, 64, copyHandle);

        RaycastSetupJob setupJob = new RaycastSetupJob
        {
            Commands = _raycastCommands,
            DownQueryParams = _groundQueryParams
        };
        JobHandle setupHandle = setupJob.Schedule(_transformAccessArray, copyHandle);
        JobHandle raycastHandle = RaycastCommand.ScheduleBatch(_raycastCommands, _raycastHits, 32, setupHandle);

        JobHandle combinedHandle = JobHandle.CombineDependencies(hashHandle, raycastHandle);

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
            ActiveTargetPositions = _activeTargetPositions, // 💡 무거운 배열 대신 가벼운 배열 1개 전달
            TargetIndices = _targetIndices,
            RaycastHits = _raycastHits,
            AllEnemyPositions = _currentPositions,
            SpatialGrid = _spatialGrid,
            YVelocities = _yVelocities,
            AnimStates = _animStates,
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
    }

    private void LateUpdate()
    {
        if (Enemies.Count == 0) return;
        _movementJobHandle.Complete();

        if (IsClientInitialized)
        {
            for (int i = 0; i < Enemies.Count; i++)
            {
                Enemy enemy = Enemies[i];
                if (enemy.myRenderer == null) continue;

                int currentState = _animStates[i];

                if (enemy.lastAnimState != currentState)
                {
                    enemy.lastAnimState = currentState;

                    if (currentState == 2) _mpb.SetFloat(_isWalkingHash, 0f);
                    else if (currentState == 1) _mpb.SetFloat(_isWalkingHash, 1f);
                    else _mpb.SetFloat(_isWalkingHash, 0f);

                    enemy.myRenderer.SetPropertyBlock(_mpb);
                }
            }
        }
    }

    private IEnumerator SpawnWaveRoutine()
    {
        yield return new WaitForSeconds(startDelay);
        StartCoroutine(SpawnEnemy());
    }

    private IEnumerator SpawnEnemy()
    {
        WaitForSeconds wait01 = new WaitForSeconds(0.01f);

        for (int i = 0; i < enemiesPerSpawn; i++)
        {
            if (Enemies.Count >= maxActiveEnemies) break;

            NetworkObject pooledEnemy = base.NetworkManager.GetPooledInstantiated(enemyPrefab, GetSpawnPointFromMom(), Quaternion.identity, true);

            if (pooledEnemy.TryGetComponent(out Enemy simpleEnemy))
            {
                Transform target = dedicatedTarget != null && dedicatedTarget.gameObject.activeInHierarchy ? dedicatedTarget : GetClosestTarget(transform.position);
                simpleEnemy.InitializeEnemy(this, target);
            }

            ServerManager.Spawn(pooledEnemy);
            yield return wait01;
        }
    }

    private Vector3 GetSpawnPointFromMom()
    {
        Vector2 randomCircle = UnityEngine.Random.insideUnitCircle * spawnRadius;
        return transform.position + new Vector3(randomCircle.x, 1, randomCircle.y);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, spawnRadius);
    }
}