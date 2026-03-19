using UnityEngine;
using FishNet.Object;
using FishNet.Component.Spawning;

public class SpawnTransformHandler : NetworkBehaviour
{
    [Header("References")]
    public GridSystem gridSystem;
    public PlayerSpawner playerSpawner;
    public GameObject enemyMotherPrefab;

    [Header("Frontline Spawn Settings")]
    public float playerSpacing = 20f;
    public float playerBaselineZ = 10f;

    [Header("Dedicated Targets (방화벽)")]
    public Transform[] playerFirewalls = new Transform[4];

    // [새로 추가] 씬에 있는 계단 입구들을 여기에 끌어다 넣습니다!
    [Header("Environment (Stairs)")]
    [Tooltip("지형의 계단 입구(빈 오브젝트)를 넣어주세요.")]
    public Transform[] stairEntrances;

    private Vector3[] _playerSpawns = new Vector3[4];
    private Vector3[] _motherSpawns = new Vector3[4];

    private void Awake()
    {
        if (gridSystem == null) gridSystem = FindFirstObjectByType<GridSystem>();
        if (playerSpawner == null) playerSpawner = GetComponent<PlayerSpawner>();

        CalculateSpawnPositions();
        ApplyPlayerSpawnsToFishNet();
    }

    private void CalculateSpawnPositions()
    {
        if (gridSystem == null) return;
        Vector3 mid = gridSystem.MiddlePoint;
        float edgeZ = (gridSystem.Rows * gridSystem.CellSize) / 2f;
        float startX = mid.x - (playerSpacing * 1.5f);

        for (int i = 0; i < 4; i++)
        {
            float posX = startX + (i * playerSpacing);
            _playerSpawns[i] = new Vector3(posX, 10, mid.z + playerBaselineZ);
            _motherSpawns[i] = new Vector3(posX, 0, mid.z + edgeZ);
        }
    }

    private void ApplyPlayerSpawnsToFishNet()
    {
        if (playerSpawner == null) return;
        Transform[] newSpawns = new Transform[4];
        for (int i = 0; i < 4; i++)
        {
            GameObject spawnPoint = new GameObject($"DynamicSpawn_Player_{i + 1}");
            spawnPoint.transform.SetParent(this.transform);
            spawnPoint.transform.position = _playerSpawns[i];
            newSpawns[i] = spawnPoint.transform;
        }
        playerSpawner.Spawns = newSpawns;
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        for (int i = 0; i < 4; i++)
        {
            GameObject mother = Instantiate(enemyMotherPrefab, _motherSpawns[i], Quaternion.identity);

            if (mother.TryGetComponent(out EnemyMother motherScript))
            {
                // 1. 방화벽 타겟 주입
                if (i < playerFirewalls.Length && playerFirewalls[i] != null)
                {
                    motherScript.dedicatedTarget = playerFirewalls[i];
                }

                // 2. [핵심] 씬에 있는 계단의 "고정된 위치(Vector3)"만 뽑아서 마더에게 주입!
                if (stairEntrances != null && stairEntrances.Length > 0)
                {
                    motherScript.injectedStairs = new Vector3[stairEntrances.Length];
                    for (int j = 0; j < stairEntrances.Length; j++)
                    {
                        if (stairEntrances[j] != null)
                            motherScript.injectedStairs[j] = stairEntrances[j].position;
                    }
                }
            }
            ServerManager.Spawn(mother);
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (gridSystem == null) gridSystem = FindFirstObjectByType<GridSystem>();
        if (gridSystem == null) return;

        CalculateSpawnPositions();

        for (int i = 0; i < 4; i++)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(_playerSpawns[i], 2f);

            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(_motherSpawns[i] + Vector3.up * 1f, new Vector3(4, 4, 4));

            Gizmos.color = new Color(1f, 1f, 1f, 0.3f);
            Gizmos.DrawLine(_playerSpawns[i], _motherSpawns[i]);
        }
    }
}