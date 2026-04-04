using UnityEngine;
using FishNet.Object;
using FishNet.Component.Spawning;
using FishNet.Connection;
using FishNet.Managing.Scened;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class SpawnTransformHandler : NetworkBehaviour
{
    [Header("References")]
    public GridSystem gridSystem;
    public GameObject enemyMotherPrefab;

    [Header("Player Cross Settings")]
    public float playerOffsetFromCenter = 15f;
    public float playerSpawnHeight = 10f;

    [Header("Mother Donut Spawn Settings")]
    public float motherMinRadius = 30f;
    public float motherMaxRadius = 50f;
    [Range(0f, 45f)]
    public float motherSpawnAngleVariance = 15f;

    [Header("Dedicated Targets (방화벽)")]
    public Transform[] playerFirewalls = new Transform[4];

    [Header("Environment (Stairs)")]
    public Transform[] stairEntrances;

    [Header("★ Pre-calculated Spawns")]
    public Transform[] generatedMotherSpawns = new Transform[4];

    private Vector3[] _playerSpawns = new Vector3[4];
    private readonly Vector3[] _baseDirections = { Vector3.forward, Vector3.back, Vector3.right, Vector3.left };

    private bool _isStageSetup = false;

    private void Awake()
    {
        if (gridSystem == null) gridSystem = FindFirstObjectByType<GridSystem>();
        CalculatePlayerSpawns();

        // 씬이 켜지면 PlayerSpawner 스폰 좌표를 새 좌표로 교체
        PlayerSpawner playerSpawner = FindFirstObjectByType<PlayerSpawner>();

        if (playerSpawner != null)
        {
            Transform[] newSpawns = new Transform[4];
            for (int i = 0; i < 4; i++)
            {
                GameObject spawnPoint = new GameObject($"DynamicSpawn_Player_{i + 1}");
                spawnPoint.transform.position = _playerSpawns[i];
                newSpawns[i] = spawnPoint.transform;
            }

            playerSpawner.Spawns = newSpawns;
            Debug.Log("[StageScene] PlayerSpawner 스폰 좌표 교체 완료!");
        }
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        base.SceneManager.OnLoadEnd += SceneManager_OnLoadEnd;
    }

    public override void OnStopServer()
    {
        base.OnStopServer();
        if (base.NetworkManager != null && base.SceneManager != null)
            base.SceneManager.OnLoadEnd -= SceneManager_OnLoadEnd;
    }

    private void SceneManager_OnLoadEnd(SceneLoadEndEventArgs args)
    {
        if (_isStageSetup) return;
        _isStageSetup = true;
        base.SceneManager.OnLoadEnd -= SceneManager_OnLoadEnd;

        SetupStage();
    }

    private void SetupStage()
    {
        FlowFieldSystem ffs = FindFirstObjectByType<FlowFieldSystem>();
        if (ffs != null)
        {
            ffs.Initialize(4);
            // ValidTargets.Clear()를 여기서 호출하지 않음.
            // FishNet 이벤트 순서: OnStartNetwork(PlayerController → RegisterTarget) 이후에
            // OnLoadEnd(SetupStage)가 실행되므로, Clear()를 호출하면 Host 플레이어 타겟이
            // 삭제되어 Host에서 좀비가 이동하지 않는 버그 발생.
            // stale 정리는 EnemyMother.OnStartNetwork()의 RemoveAll이 담당.
        }

        SpawnMothers();
        TeleportPlayers();
    }

    private void CalculatePlayerSpawns()
    {
        if (gridSystem == null) return;
        Vector3 mid = gridSystem.MiddlePoint;
        for (int i = 0; i < 4; i++)
        {
            _playerSpawns[i] = mid + _baseDirections[i] * playerOffsetFromCenter;
            _playerSpawns[i].y = playerSpawnHeight;
        }
    }

    private void SpawnMothers()
    {
        Vector3 mid = gridSystem != null ? gridSystem.MiddlePoint : Vector3.zero;

        for (int i = 0; i < 4; i++)
        {
            Vector3 finalMotherPos;
            if (generatedMotherSpawns.Length > i && generatedMotherSpawns[i] != null)
            {
                finalMotherPos = generatedMotherSpawns[i].position;
            }
            else
            {
                float randomAngle = Random.Range(-motherSpawnAngleVariance, motherSpawnAngleVariance);
                Vector3 randomDir = Quaternion.Euler(0, randomAngle, 0) * _baseDirections[i];
                float randomDist = Random.Range(motherMinRadius, motherMaxRadius);
                finalMotherPos = mid + (randomDir * randomDist);
            }

            GameObject mother = Instantiate(enemyMotherPrefab, finalMotherPos, Quaternion.identity);

            if (mother == null) continue;

            if (mother.TryGetComponent(out EnemyMother motherScript))
            {
                // StageHandler 설정 인덱스와 대응 — OnStartNetwork에서 GetConfig(motherIndex)로 참조
                motherScript.motherIndex = i;

                if (i < playerFirewalls.Length && playerFirewalls[i] != null)
                    motherScript.dedicatedTarget = playerFirewalls[i];

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

    private void TeleportPlayers()
    {
        int playerIndex = 0;
        foreach (NetworkConnection conn in ServerManager.Clients.Values)
        {
            if (conn.FirstObject == null) continue;

            Vector3 spawnPos = _playerSpawns[playerIndex % 4];

            CharacterController cc = conn.FirstObject.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            conn.FirstObject.transform.position = spawnPos;
            if (cc != null) cc.enabled = true;

            PlayerController pc = conn.FirstObject.GetComponent<PlayerController>();
            if (pc != null) pc.TargetTeleport(conn, spawnPos);

            // [수정 6] RegisterTarget 중복 제거
            // PlayerController.OnStartNetwork()에서 이미 RegisterTarget을 호출하므로 여기서 또 하면 중복 등록됨
            // EnemyMother.RegisterTarget(conn.FirstObject.transform); ← 제거

            playerIndex++;
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (gridSystem == null) gridSystem = FindFirstObjectByType<GridSystem>();
        if (gridSystem == null) return;

        CalculatePlayerSpawns();
        Vector3 mid = gridSystem.MiddlePoint;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < 4; i++) Gizmos.DrawSphere(_playerSpawns[i], 1f);

        Gizmos.color = Color.magenta;
        for (int i = 0; i < generatedMotherSpawns.Length; i++)
        {
            if (generatedMotherSpawns[i] != null)
            {
                Gizmos.DrawWireCube(generatedMotherSpawns[i].position + Vector3.up, new Vector3(4, 4, 4));
                Gizmos.DrawLine(mid, generatedMotherSpawns[i].position);
            }
        }
    }

    public void GenerateMotherSpawnsInEditor()
    {
        if (gridSystem == null) gridSystem = FindFirstObjectByType<GridSystem>();
        if (gridSystem == null) return;

        for (int i = 0; i < generatedMotherSpawns.Length; i++)
        {
            if (generatedMotherSpawns[i] != null) DestroyImmediate(generatedMotherSpawns[i].gameObject);
        }

        Vector3 mid = gridSystem.MiddlePoint;
        generatedMotherSpawns = new Transform[4];

        for (int i = 0; i < 4; i++)
        {
            float randomAngle = Random.Range(-motherSpawnAngleVariance, motherSpawnAngleVariance);
            Vector3 randomDir = Quaternion.Euler(0, randomAngle, 0) * _baseDirections[i];
            float randomDist = Random.Range(motherMinRadius, motherMaxRadius);
            Vector3 spawnPos = mid + (randomDir * randomDist);

            GameObject spawnObj = new GameObject($"PreSpawn_Mother_{i + 1}");
            spawnObj.transform.position = spawnPos;
            // SetParent 없음 — NetworkObject 자식 계층에 넣지 않음
            generatedMotherSpawns[i] = spawnObj.transform;
        }

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
    }
#endif
}

#if UNITY_EDITOR
[CustomEditor(typeof(SpawnTransformHandler))]
[CanEditMultipleObjects]
public class SpawnTransformHandlerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        GUILayout.Space(20);
        if (GUILayout.Button("마더 스폰 위치 미리 생성하기", GUILayout.Height(40)))
        {
            foreach (var selectedTarget in targets)
            {
                SpawnTransformHandler script = (SpawnTransformHandler)selectedTarget;
                script.GenerateMotherSpawnsInEditor();
            }
        }
    }
}
#endif