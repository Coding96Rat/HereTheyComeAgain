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
    public static List<Transform> activePlayers = new List<Transform>();

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

    // 쉐이더 제어용 통신 블록 (Animator 완벽 대체)
    private MaterialPropertyBlock _mpb;
    private readonly int _isWalkingHash = Shader.PropertyToID("_IsWalking");
    private readonly int _timeOffsetHash = Shader.PropertyToID("_TimeOffset");

    #region O(1) 리스트 관리 시스템 (Job System 연동)
    public void AddEnemy(Enemy enemy)
    {
        int newIndex = Enemies.Count;
        Enemies.Add(enemy);
        enemy.motherListIndex = newIndex;

        _transformAccessArray.Add(enemy.transform);
        _targetPositions[newIndex] = enemy.GetTargetPosition();

        // 몬스터 스폰 시 랜덤 발걸음 부여 및 정지 상태로 세팅
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

        // [에러 폭탄 방지] 씬 종료 시 Mother가 먼저 파괴되어 배열이 해제된 경우 접근 차단!
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

        _mpb = new MaterialPropertyBlock(); // 초기화
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
    }

    // 네트워크가 시작될 때 FishNet 공식 API를 사용하여 깔끔하게 메모리에 올려둡니다.
    // 1. 오직 서버만: 서버용 주머니를 채우고 스폰 시작!
    public override void OnStartServer()
    {
        base.OnStartServer();

        // 서버용 주머니 예열 (asServer: true) -> 이걸 해야 SpawnEnemy에서 Instantiate(GC)가 발생하지 않습니다!
        base.NetworkManager.ObjectPool.CacheObjects(enemyPrefab, maxActiveEnemies, true);
        Debug.Log("<color=cyan>[서버 주머니 준비 완료]</color>");

        StartCoroutine(SpawnWaveRoutine());
    }

    // 2. 클라이언트: 클라이언트 화면용 주머니를 채우기!
    public override void OnStartClient()
    {
        base.OnStartClient();

        // 클라이언트용 주머니 예열 (asServer: false) -> 이걸 해야 화면에 몬스터가 짠! 하고 나타날 때 렉이 안 걸립니다!
        base.NetworkManager.ObjectPool.CacheObjects(enemyPrefab, maxActiveEnemies, false);
        Debug.Log("<color=lime>[클라이언트 주머니 준비 완료]</color>");
    }

    public void RegisterPlayer(Transform playerTransform)
    {
        if (!activePlayers.Contains(playerTransform)) activePlayers.Add(playerTransform);
    }

    public void UnregisterPlayer(Transform playerTransform)
    {
        if (activePlayers.Contains(playerTransform)) activePlayers.Remove(playerTransform);
    }

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
            AnimStates = _animStates, // 애니 상태 계산!
            DeltaTime = Time.deltaTime,
            Speed = globalEnemySpeed,
            RotationSpeed = globalRotationSpeed,
            Gravity = -9.81f,
            PivotOffset = 1.0f,
            SeparationRadius = separationRadius,
            SeparationWeight = separationWeight
        };

        _movementJobHandle = moveJob.Schedule(_transformAccessArray, raycastHandle);
    }

    private void LateUpdate()
    {
        _movementJobHandle.Complete();

        if (!IsClientInitialized) return;

        // 4000마리의 쉐이더에게 걷기(1) / 멈춤(0) 상태를 일괄 전송!
        for (int i = 0; i < Enemies.Count; i++)
        {
            Enemy enemy = Enemies[i];
            if (enemy.myRenderer == null) continue;

            float isWalking = _animStates[i];

            // [극한의 최적화] 상태가 변했을 때만(0 -> 1 또는 1 -> 0) PropertyBlock을 업데이트합니다!
            if (enemy.lastAnimState != isWalking)
            {
                enemy.lastAnimState = isWalking; // 상태 갱신

                enemy.myRenderer.GetPropertyBlock(_mpb);
                _mpb.SetFloat(_isWalkingHash, isWalking);
                enemy.myRenderer.SetPropertyBlock(_mpb);
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
        // new를 한 번만 해서 변수에 담아두고 계속 재사용합니다! (GC Zero)
        WaitForSeconds wait01 = new WaitForSeconds(0.01f);

        for (int i = 0; i < enemiesPerSpawn; i++)
        {
            // [핵심 안전장치] 현재 몬스터 수가 최대치(1000)에 도달했으면 스폰을 즉시 중단합니다!
            if (Enemies.Count >= maxActiveEnemies)
            {
                // 이번 스폰 타이밍은 포기하고 반복문을 빠져나감 (나중에 좀비가 죽어서 자리가 비면 다시 스폰됨)
                break;
            }

            NetworkObject pooledEnemy = base.NetworkManager.GetPooledInstantiated(enemyPrefab, GetSpawnPointFromMom(), Quaternion.identity, true);

            if (pooledEnemy.TryGetComponent(out Enemy simpleEnemy))
            {
                Transform target = activePlayers.Count > 0 ? activePlayers[Enemies.Count % activePlayers.Count] : null;
                simpleEnemy.InitializeEnemy(this, target);
            }

            ServerManager.Spawn(pooledEnemy);

            // 변수를 재사용하여 쓰레기 발생을 원천 차단!
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