using UnityEngine;
using FishNet.Object;
using FishNet.Component.Spawning;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class SpawnTransformHandler : NetworkBehaviour
{
    [Header("References")]
    public GridSystem gridSystem;
    public PlayerSpawner playerSpawner;
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

    private void Awake()
    {
        if (gridSystem == null) gridSystem = FindFirstObjectByType<GridSystem>();
        if (playerSpawner == null) playerSpawner = GetComponent<PlayerSpawner>();

        CalculatePlayerSpawns();
        ApplyPlayerSpawnsToFishNet();
    }

    private void CalculatePlayerSpawns()
    {
        if (gridSystem == null) return;
        Vector3 mid = gridSystem.MiddlePoint;

        _playerSpawns[0] = mid + _baseDirections[0] * playerOffsetFromCenter;
        _playerSpawns[1] = mid + _baseDirections[1] * playerOffsetFromCenter;
        _playerSpawns[2] = mid + _baseDirections[2] * playerOffsetFromCenter;
        _playerSpawns[3] = mid + _baseDirections[3] * playerOffsetFromCenter;

        for (int i = 0; i < 4; i++) _playerSpawns[i].y = playerSpawnHeight;
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

        // 1. FlowFieldSystem 세팅
        FlowFieldSystem ffs = FindFirstObjectByType<FlowFieldSystem>();
        if (ffs != null)
        {
            ffs.Initialize(4);

            EnemyMother.ValidTargets.Clear();
            List<Transform> flowTargets = new List<Transform>();

            for (int i = 0; i < playerFirewalls.Length; i++)
            {
                if (playerFirewalls[i] != null)
                {
                    flowTargets.Add(playerFirewalls[i]);
                    EnemyMother.ValidTargets.Add(playerFirewalls[i]); // 마더 공통 타겟에 등록!
                }
            }
            ffs.StartUpdatingFlowFields(flowTargets);
        }

        Vector3 mid = gridSystem != null ? gridSystem.MiddlePoint : Vector3.zero;

        // 2. Mother 스폰
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

            if (mother.TryGetComponent(out EnemyMother motherScript))
            {
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

    private void OnDrawGizmosSelected()
    {
        if (gridSystem == null) gridSystem = FindFirstObjectByType<GridSystem>();
        if (gridSystem == null) return;

        CalculatePlayerSpawns();
        Vector3 mid = gridSystem.MiddlePoint;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < 4; i++) Gizmos.DrawSphere(_playerSpawns[i], 1f);

        for (int i = 0; i < 4; i++) DrawGizmoArc(mid, _baseDirections[i], motherMinRadius, motherMaxRadius, motherSpawnAngleVariance);

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

    private void DrawGizmoArc(Vector3 center, Vector3 baseDir, float minRadius, float maxRadius, float angleVariance)
    {
        Vector3 leftDir = Quaternion.Euler(0, -angleVariance, 0) * baseDir;
        Vector3 rightDir = Quaternion.Euler(0, angleVariance, 0) * baseDir;

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(center + leftDir * minRadius, center + rightDir * minRadius);
        Gizmos.color = Color.red;
        Gizmos.DrawLine(center + leftDir * maxRadius, center + rightDir * maxRadius);
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.5f);
        Gizmos.DrawLine(center + leftDir * minRadius, center + leftDir * maxRadius);
        Gizmos.DrawLine(center + rightDir * minRadius, center + rightDir * maxRadius);
    }

#if UNITY_EDITOR
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
            spawnObj.transform.SetParent(this.transform);

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
        if (GUILayout.Button("🎲 마더 스폰 위치 미리 생성하기", GUILayout.Height(40)))
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