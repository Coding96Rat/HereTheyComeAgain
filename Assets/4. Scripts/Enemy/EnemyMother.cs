using FishNet.Object;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics; // [추가] 공간 해싱 연산을 위함
using UnityEngine;
using UnityEngine.Jobs;

public class EnemyMother : NetworkBehaviour
{
    public NetworkObject enemyPrefab;

    // 플레이어와 방화벽을 모두 담는 통합 타겟 리스트
    public static List<Transform> ValidTargets = new List<Transform>();

    [Header("우선 타겟팅 (자동 할당됨)")]
    [Tooltip("이 마더가 무조건 최우선으로 부술 타겟 (방화벽 등)")]
    public Transform dedicatedTarget;

    [Header("각 웨이브 숙주 스폰 설정")]
    public float startDelay = 5f;
    public float spawnInterval = 1f;
    public int enemiesPerSpawn = 5;
    public int maxActiveEnemies = 4000; // 4000마리 렌더링 환경
    public float spawnRadius = 20f;

    public List<Enemy> Enemies;

    // --- Job System 관련 변수들 ---
    private TransformAccessArray _transformAccessArray;
    private NativeArray<Vector3> _targetPositions;
    private NativeArray<Vector3> _currentPositions;
    private JobHandle _movementJobHandle;

    private NativeArray<RaycastCommand> _raycastCommands;
    private NativeArray<RaycastHit> _raycastHits;
    private NativeArray<float> _yVelocities;

    private NativeArray<int> _animStates;
    private QueryParameters _groundQueryParams;

    // [핵심] 공간 해싱 맵 (O(1) 주변 탐색)
    private NativeParallelMultiHashMap<int, int> _spatialGrid;

    [Header("군집 (Separation) 및 물리 설정")]
    public float separationRadius = 1.5f;
    public float separationWeight = 2.0f;
    public float globalEnemySpeed = 3f;
    public float globalRotationSpeed = 360f;

    [Tooltip("공간 해싱 그리드 셀 크기 (너무 크면 연산량 증가, 너무 작으면 군집 분리됨)")]
    public float cellSize = 2.0f;

    [Tooltip("머리 위에 몇 마리가 올라타면 바닥으로 무너질지 결정하는 하중치")]
    public float maxWeightTolerance = 3.0f;

    // 스포너로부터 위치 데이터만 넘겨받을 배열 (인스펙터 노출 안 함)
    [HideInInspector]
    public Vector3[] injectedStairs;

    // 쉐이더 제어용 통신 블록
    private MaterialPropertyBlock _mpb;
    private readonly int _isWalkingHash = Shader.PropertyToID("_IsWalking");
    private readonly int _timeOffsetHash = Shader.PropertyToID("_TimeOffset");

    #region 타겟 관리 시스템 (Target Management)
    public static void RegisterTarget(Transform target)
    {
        if (!ValidTargets.Contains(target)) ValidTargets.Add(target);
    }

    public static void UnregisterTarget(Transform target)
    {
        if (ValidTargets.Contains(target)) ValidTargets.Remove(target);
    }

    // 특정 위치에서 가장 가까운 살아있는 타겟을 찾아주는 함수
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
    #endregion

    #region O(1) 리스트 관리 시스템 (Job System 연동)
    public void AddEnemy(Enemy enemy)
    {
        int newIndex = Enemies.Count;
        Enemies.Add(enemy);
        enemy.motherListIndex = newIndex;

        _transformAccessArray.Add(enemy.transform);
        _targetPositions[newIndex] = enemy.GetTargetPosition();

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
            _targetPositions[removeIndex] = _targetPositions[lastIndex];
        }

        enemy.motherListIndex = -1;
    }
    #endregion

    private void Awake()
    {
        Enemies = new List<Enemy>(maxActiveEnemies);
        _transformAccessArray = new TransformAccessArray(maxActiveEnemies);
        _targetPositions = new NativeArray<Vector3>(maxActiveEnemies, Allocator.Persistent);

        // 하단과 전방, 2개의 레이캐스트를 쏘기 위해 배열 크기를 2배로 늘림
        _raycastCommands = new NativeArray<RaycastCommand>(maxActiveEnemies * 2, Allocator.Persistent);
        _raycastHits = new NativeArray<RaycastHit>(maxActiveEnemies * 2, Allocator.Persistent);

        _yVelocities = new NativeArray<float>(maxActiveEnemies, Allocator.Persistent);
        _currentPositions = new NativeArray<Vector3>(maxActiveEnemies, Allocator.Persistent);
        _animStates = new NativeArray<int>(maxActiveEnemies, Allocator.Persistent);

        // [핵심] NativeParallelMultiHashMap 할당
        _spatialGrid = new NativeParallelMultiHashMap<int, int>(maxActiveEnemies, Allocator.Persistent);

        _groundQueryParams = new QueryParameters(LayerMask.GetMask("Ground"), false, QueryTriggerInteraction.Ignore, false);
        _mpb = new MaterialPropertyBlock();
    }

    private void OnDestroy()
    {
        _movementJobHandle.Complete();
        if (_transformAccessArray.isCreated) _transformAccessArray.Dispose();
        if (_targetPositions.IsCreated) _targetPositions.Dispose();
        if (_raycastCommands.IsCreated) _raycastCommands.Dispose();
        if (_raycastHits.IsCreated) _raycastHits.Dispose();
        if (_yVelocities.IsCreated) _yVelocities.Dispose();
        if (_currentPositions.IsCreated) _currentPositions.Dispose();
        if (_animStates.IsCreated) _animStates.Dispose();

        if (_spatialGrid.IsCreated) _spatialGrid.Dispose(); // 해시맵 파기
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
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

        for (int i = 0; i < Enemies.Count; i++)
        {
            _targetPositions[i] = Enemies[i].GetTargetPosition();
        }

        // 매 프레임 해시맵 초기화
        _spatialGrid.Clear();

        // 1. 위치 복사 Job
        CopyPositionsJob copyJob = new CopyPositionsJob { CurrentPositions = _currentPositions };
        JobHandle copyHandle = copyJob.Schedule(_transformAccessArray);

        // 2. 공간 해싱 Job
        HashPositionsJob hashJob = new HashPositionsJob
        {
            Positions = _currentPositions,
            SpatialGrid = _spatialGrid.AsParallelWriter(),
            CellSize = cellSize
        };
        JobHandle hashHandle = hashJob.Schedule(Enemies.Count, 64, copyHandle);

        // 3. 레이캐스트 셋업 및 실행 Job
        RaycastSetupJob setupJob = new RaycastSetupJob
        {
            Commands = _raycastCommands,
            DownQueryParams = _groundQueryParams
        };
        JobHandle setupHandle = setupJob.Schedule(_transformAccessArray, copyHandle);
        JobHandle raycastHandle = RaycastCommand.ScheduleBatch(_raycastCommands, _raycastHits, 32, setupHandle);

        // 4. Hash 연산과 Raycast 연산이 모두 끝날 때까지 대기
        JobHandle combinedHandle = JobHandle.CombineDependencies(hashHandle, raycastHandle);

        // 5. 메인 물리 연산 Job
        EnemyMovementJob moveJob = new EnemyMovementJob
        {
            TargetPositions = _targetPositions,
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
            MaxWeightTolerance = maxWeightTolerance
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

                    if (currentState == 2)
                    {
                        _mpb.SetFloat(_isWalkingHash, 0f);
                    }
                    else if (currentState == 1)
                    {
                        _mpb.SetFloat(_isWalkingHash, 1f);
                    }
                    else
                    {
                        _mpb.SetFloat(_isWalkingHash, 0f);
                    }

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
                Transform target = null;

                if (dedicatedTarget != null && dedicatedTarget.gameObject.activeInHierarchy)
                {
                    target = dedicatedTarget;
                }
                else
                {
                    target = GetClosestTarget(transform.position);
                }

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