using FishNet.Object;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EnemyMother : NetworkBehaviour
{
    public static EnemyMother Instance;

    public NetworkObject enemyPrefab;
    public List<Transform> activePlayers = new List<Transform>();

    [Header("웨이브 스폰 설정")]
    public float startDelay = 5f;
    public float spawnInterval = 1f;
    public int enemiesPerSpawn = 5;
    public int maxActiveEnemies = 400;
    public float spawnRadius = 20f;

    [Tooltip("현재 맵에 살아있는 모든 적의 리스트")]
    public List<Enemy> Enemies;

    [Header("최적화 설정")]
    private bool startedTicking = false;

    #region O(1) 리스트 관리 시스템
    public void AddEnemy(Enemy enemy)
    {
        // 1. 적이 들어갈 인덱스를 기억
        int newIndex = Enemies.Count;

        // 2. 리스트에 추가
        Enemies.Add(enemy);

        // 3. 적에게 "너는 지금 newIndex 번째 방에 있어"라고 알려줌
        enemy.motherListIndex = newIndex;
    }

    public void RemoveEnemy(Enemy enemy)
    {
        int removeIndex = enemy.motherListIndex;

        // 이미 지워졌거나 잘못된 인덱스면 무시 (안전 장치)
        if (removeIndex < 0 || removeIndex >= Enemies.Count || Enemies[removeIndex] != enemy)
            return;

        int lastIndex = Enemies.Count - 1;

        // 지우려는 애가 마침 맨 끝에 있는 애라면, 자리 바꿀 필요 없이 바로 삭제
        if (removeIndex == lastIndex)
        {
            Enemies.RemoveAt(lastIndex);
        }
        else
        {
            // [Swap and Pop 핵심 로직]
            // 1. 맨 끝에 있는 적을 가져옴
            Enemy lastEnemy = Enemies[lastIndex];

            // 2. 삭제할 빈자리에 맨 끝 적을 채워 넣음
            Enemies[removeIndex] = lastEnemy;

            // 3. 자리가 바뀐 맨 끝 적에게 "너 방 번호 바뀌었어"라고 알려줌 (매우 중요!)
            lastEnemy.motherListIndex = removeIndex;

            // 4. 이제 쓸모없어진 맨 끝자리를 삭제
            Enemies.RemoveAt(lastIndex);
        }

        // 지워진 적의 인덱스 초기화
        enemy.motherListIndex = -1;
    }
    #endregion

    private void Awake()
    {
        if (Instance == null) Instance = this;

    }

    #region Server Setting
    public override void OnStartServer()
    {
        base.OnStartServer();
        // GridSystem 관련 로직은 에러 방지를 위해 임시 주석 처리 (원래 쓰시던 대로 쓰시면 됩니다)
        // GridSystem gridSystem = FindFirstObjectByType<GridSystem>();
        // transform.position = new Vector3(gridSystem.Columns/2 * gridSystem.CellSize, 0, gridSystem.Rows / 2 * gridSystem.CellSize);
        Enemies = new List<Enemy>(maxActiveEnemies);
        startedTicking = false;
        StartCoroutine(SpawnWaveRoutine());
    }

    public void RegisterPlayer(Transform playerTransform)
    {
        if (!activePlayers.Contains(playerTransform)) activePlayers.Add(playerTransform);
    }

    public void UnregisterPlayer(Transform playerTransform)
    {
        if (activePlayers.Contains(playerTransform)) activePlayers.Remove(playerTransform);
    }
    #endregion

    private void FixedUpdate()
    {

        for (int i = 0; i < Enemies.Count; i++)
        {
            Enemies[i].MotherTick();
        }
    }

    private IEnumerator MyFixedUpdate()
    {
        while (Enemies.Count > 0)
        {
            for (int i = 0; i < Enemies.Count; i++)
            {
                Enemies[i].MotherTick();
            }
            yield return new WaitForSeconds(0.02f);
        }
    }

    private IEnumerator SpawnWaveRoutine()
    {
        yield return new WaitForSeconds(startDelay);

        //NetworkObject pooledEnemy = base.NetworkManager.GetPooledInstantiated(enemyPrefab, GetSpawnPointFromMom(), Quaternion.identity, true);
        //ServerManager.Spawn(pooledEnemy);

        //int assignedTargetIndex = Enemies.Count % activePlayers.Count;
        //if (pooledEnemy.TryGetComponent(out Enemy simpleEnemy))
        //{
        //    simpleEnemy.InitializeEnemy(activePlayers[assignedTargetIndex]);
        //}

        StartCoroutine(SpawnEnemy());
    }

    private IEnumerator SpawnEnemy()
    {
        for(int i=0; i<enemiesPerSpawn; i++)
        {
            NetworkObject pooledEnemy = base.NetworkManager.GetPooledInstantiated(enemyPrefab, GetSpawnPointFromMom(), Quaternion.identity, true);
            ServerManager.Spawn(pooledEnemy);
            int assignedTargetIndex = Enemies.Count % activePlayers.Count;
            if (pooledEnemy.TryGetComponent(out Enemy simpleEnemy))
            {
                simpleEnemy.InitializeEnemy(activePlayers[assignedTargetIndex]);
            }
            yield return new WaitForSeconds(0.01f);
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