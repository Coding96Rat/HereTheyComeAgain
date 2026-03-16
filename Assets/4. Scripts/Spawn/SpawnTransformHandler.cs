using UnityEngine;
using FishNet.Object;
using FishNet.Component.Spawning;

public class SpawnTransformHandler : NetworkBehaviour
{
    [Header("References")]
    [Tooltip("그리드 시스템 연결 (비워두면 자동 할당)")]
    public GridSystem gridSystem;
    [Tooltip("FishNet 기본 PlayerSpawner 연결")]
    public PlayerSpawner playerSpawner;
    [Tooltip("스폰할 EnemyMother 프리팹 (반드시 NetworkObject가 있어야 함)")]
    public GameObject enemyMotherPrefab;

    [Header("Spawn Settings")]
    [Tooltip("중앙을 기점으로 플레이어가 얼마나 떨어져서 스폰될지 (가로)")]
    public float playerOffsetX = 5f;
    [Tooltip("중앙을 기점으로 플레이어가 얼마나 떨어져서 스폰될지 (세로)")]
    public float playerOffsetZ = 5f;

    private Vector3[] _playerSpawns = new Vector3[4];
    private Vector3[] _motherSpawns = new Vector3[4];

    private void Awake()
    {
        if (gridSystem == null) gridSystem = FindFirstObjectByType<GridSystem>();
        if (playerSpawner == null) playerSpawner = GetComponent<PlayerSpawner>();

        CalculateSpawnPositions();
        ApplyPlayerSpawnsToFishNet();
    }

    // 1. 4개의 플레이어 위치와 마더 위치를 수학적으로 계산합니다.
    private void CalculateSpawnPositions()
    {
        if (gridSystem == null) return;

        Vector3 mid = gridSystem.MiddlePoint;

        // 4방향 부호 (1사분면, 2사분면, 3사분면, 4사분면) -> + 모양 또는 정사각형 꼭짓점
        int[] signsX = { 1, -1, -1, 1 };
        int[] signsZ = { 1, 1, -1, -1 };

        float edgeX = (gridSystem.Columns * gridSystem.CellSize) / 2f;
        float edgeZ = (gridSystem.Rows * gridSystem.CellSize) / 2f;

        for (int i = 0; i < 4; i++)
        {
            // 플레이어: 중앙에서 offset만큼 떨어진 4방향
            _playerSpawns[i] = mid + new Vector3(playerOffsetX * signsX[i], 0, playerOffsetZ * signsZ[i]);

            // 마더: 플레이어가 있는 방향의 '정반대(-) 방향' 끝자락(Edge)
            _motherSpawns[i] = mid + new Vector3(edgeX * -signsX[i], 0, edgeZ * -signsZ[i]);
        }
    }

    // 2. 계산된 위치에 빈 오브젝트를 생성해 FishNet 스포너에 자동으로 먹여줍니다.
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

        // 수동으로 넣던 Spawns 배열을 우리가 코드로 만든 배열로 덮어씌웁니다!
        playerSpawner.Spawns = newSpawns;
    }

    // 3. 서버가 시작될 때, 계산해둔 4개의 끝자락에 EnemyMother를 스폰시킵니다!
    public override void OnStartServer()
    {
        base.OnStartServer();

        for (int i = 0; i < 4; i++)
        {
            GameObject mother = Instantiate(enemyMotherPrefab, _motherSpawns[i], Quaternion.identity);
            ServerManager.Spawn(mother); // FishNet 네트워크 스폰!
        }
    }

    // 4. 에디터에서 시각적으로 확인할 수 있게 기즈모를 그립니다.
    private void OnDrawGizmosSelected()
    {
        if (gridSystem == null) gridSystem = FindFirstObjectByType<GridSystem>();
        if (gridSystem == null) return;

        CalculateSpawnPositions();

        for (int i = 0; i < 4; i++)
        {
            // 플레이어 스폰 위치 (파란색 구체)
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(_playerSpawns[i], 2f);

            // 마더 스폰 위치 (빨간색 큐브)
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(_motherSpawns[i] + Vector3.up * 1f, new Vector3(4, 4, 4));

            // 중앙 기점(하얀색) -> 플레이어(파란색) -> 정반대편 마더(빨간색)를 잇는 연관선 그리기
            Gizmos.color = new Color(1f, 1f, 1f, 0.3f);
            Gizmos.DrawLine(gridSystem.MiddlePoint, _playerSpawns[i]);
            Gizmos.DrawLine(gridSystem.MiddlePoint, _motherSpawns[i]);
        }
    }
}