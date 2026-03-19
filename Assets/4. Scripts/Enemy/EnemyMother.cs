using FishNet.Object;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

public class EnemyMother : NetworkBehaviour
{
    public NetworkObject enemyPrefab;

    //  [수정] 플레이어와 방화벽을 모두 담는 통합 타겟 리스트
    public static List<Transform> ValidTargets = new List<Transform>();

    [Header("우선 타겟팅 (자동 할당됨)")]
    [Tooltip("이 마더가 무조건 최우선으로 부술 타겟 (방화벽 등)")]
    public Transform dedicatedTarget;

    [Header("각 웨이브 숙주 스폰 설정")]
    public float startDelay = 5f;
    public float spawnInterval = 1f;
    public int enemiesPerSpawn = 5;
    public int maxActiveEnemies = 400;
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

    [Header("군집 (Separation) 설정")]
    public float separationRadius = 1.5f;
    public float separationWeight = 2.0f;
    public float globalEnemySpeed = 3f;
    public float globalRotationSpeed = 360f;

    [Header("경사도 제한 (Slope Limit)")]
    [Tooltip("좀비가 오를 수 있는 최대 각도 (유니티 CharacterController와 동일)")]
    public float maxSlopeAngle = 45f;

    // 스포너로부터 위치 데이터만 넘겨받을 배열 (인스펙터 노출 안 함)
    [HideInInspector]
    public Vector3[] injectedStairs;

    // Job에 넘겨줄 계단 위치 배열
    private NativeArray<Vector3> _stairPositions;

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

    //  특정 위치에서 가장 가까운 살아있는 타겟을 찾아주는 함수
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
            _mpb.SetFloat(_timeOffsetHash, Random.Range(0f, 2f));
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
        _raycastCommands = new NativeArray<RaycastCommand>(maxActiveEnemies, Allocator.Persistent);
        _raycastHits = new NativeArray<RaycastHit>(maxActiveEnemies, Allocator.Persistent);
        _yVelocities = new NativeArray<float>(maxActiveEnemies, Allocator.Persistent);
        _currentPositions = new NativeArray<Vector3>(maxActiveEnemies, Allocator.Persistent);
        _animStates = new NativeArray<int>(maxActiveEnemies, Allocator.Persistent);

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
        if(_stairPositions.IsCreated) _stairPositions.Dispose();    
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        // [핵심] 스포너가 데이터를 주입해준 직후인 여기서 NativeArray를 딱 한 번 할당합니다!
        if (injectedStairs != null && injectedStairs.Length > 0)
        {
            _stairPositions = new NativeArray<Vector3>(injectedStairs.Length, Allocator.Persistent);
            for (int i = 0; i < injectedStairs.Length; i++)
            {
                _stairPositions[i] = injectedStairs[i];
            }
        }
        else
        {
            // 계단이 아예 없을 경우를 대비한 빈 배열 안전장치
            _stairPositions = new NativeArray<Vector3>(0, Allocator.Persistent);
        }

        base.NetworkManager.ObjectPool.CacheObjects(enemyPrefab, maxActiveEnemies, true);
        StartCoroutine(SpawnWaveRoutine());
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        base.NetworkManager.ObjectPool.CacheObjects(enemyPrefab, maxActiveEnemies, false);
    }

    // 1. Update (프레임 시작): 
    // 가장 먼저 백그라운드 스레드에 4000마리 연산을 던져놓습니다! (메인 스레드는 대기하지 않고 지나감)
    private void Update()
    {
        if (Enemies.Count == 0) return;

        for (int i = 0; i < Enemies.Count; i++)
        {
            _targetPositions[i] = Enemies[i].GetTargetPosition();
        }

        CopyPositionsJob copyJob = new CopyPositionsJob { CurrentPositions = _currentPositions };
        JobHandle copyHandle = copyJob.Schedule(_transformAccessArray);

        RaycastSetupJob setupJob = new RaycastSetupJob
        {
            Commands = _raycastCommands,
            QueryParams = _groundQueryParams
        };
        JobHandle setupHandle = setupJob.Schedule(_transformAccessArray, copyHandle);

        JobHandle raycastHandle = RaycastCommand.ScheduleBatch(_raycastCommands, _raycastHits, 32, setupHandle);

        EnemyMovementJob moveJob = new EnemyMovementJob
        {
            TargetPositions = _targetPositions,
            RaycastHits = _raycastHits,
            YVelocities = _yVelocities,
            AllEnemyPositions = _currentPositions,
            StairPositions = _stairPositions,
            AnimStates = _animStates,
            DeltaTime = Time.deltaTime,
            Speed = globalEnemySpeed,
            RotationSpeed = globalRotationSpeed,
            Gravity = -9.81f,
            PivotOffset = 1.0f,
            SeparationRadius = separationRadius,
            SeparationWeight = separationWeight,
            SlopeThreshold = Mathf.Cos(maxSlopeAngle * Mathf.Deg2Rad)
        };

        // Job을 던져놓고 즉시 빠져나갑니다. 이제 백그라운드 스레드가 땀 뻘뻘 흘리며 일하기 시작합니다!
        _movementJobHandle = moveJob.Schedule(_transformAccessArray, raycastHandle);
    }

    // 2. LateUpdate (렌더링 직전): 
    // 다른 스크립트(네트워크, 플레이어 컨트롤러 등)가 다 실행될 동안 벌어둔 시간 덕분에, 
    // 여기서 Complete를 부르면 대기 시간(Wait)이 0ms에 가깝게 증발합니다!
    private void LateUpdate()
    {
        if (Enemies.Count == 0) return;

        // "Job 계산 다 끝났니?" -> (이미 다른 일 하는 동안 백그라운드에서 끝내놨음!) -> 대기 없음!
        _movementJobHandle.Complete();

        // 위치가 확정되었으니, 시각적 애니메이션을 동기화하고 렌더러로 넘겨줍니다.
        if (IsClientInitialized)
        {
            for (int i = 0; i < Enemies.Count; i++)
            {
                Enemy enemy = Enemies[i];
                if (enemy.myRenderer == null) continue;

                float isWalking = _animStates[i];

                if (enemy.lastAnimState != isWalking)
                {
                    enemy.lastAnimState = isWalking;
                    enemy.myRenderer.GetPropertyBlock(_mpb);
                    _mpb.SetFloat(_isWalkingHash, isWalking);
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

                //  1. 전용 방화벽이 살아있다면 무조건 돌격!
                if (dedicatedTarget != null && dedicatedTarget.gameObject.activeInHierarchy)
                {
                    target = dedicatedTarget;
                }
                else
                {
                    //  2. 방화벽이 파괴되었다면 가장 가까운 타겟(플레이어, 다른 건물)을 탐색!
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
        Vector2 randomCircle = Random.insideUnitCircle * spawnRadius;
        return transform.position + new Vector3(randomCircle.x, 1, randomCircle.y);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, spawnRadius);
    }
}