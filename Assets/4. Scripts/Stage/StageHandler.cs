using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// StageScene에 미리 배치되는 설정 전용 컴포넌트 (NetworkBehaviour 아님).
///
/// 역할:
///  - Mother 인덱스별 스폰 수치 보유
///  - Wave 전체 시작 여부 제어
///  - EnemyMother.OnStartNetwork에서 GetConfig(motherIndex)로 참조
/// </summary>
public class StageHandler : MonoBehaviour
{
    public static StageHandler Instance { get; private set; }

    [Header("Wave 설정")]
    [Tooltip("게임 시작 시 Wave를 자동으로 시작할지 여부.\nfalse면 Mother들이 소환되더라도 즉시 대기 상태로 있음.")]
    [SerializeField] private bool _startWaveOnInit = true;

    [Header("Mother별 스폰 설정 (인덱스 = SpawnTransformHandler 생성 순서 0~3)")]
    [SerializeField] private List<MotherSpawnConfig> _motherConfigs = new List<MotherSpawnConfig>();

    public bool ShouldStartWave => _startWaveOnInit;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// 인덱스에 해당하는 설정을 반환. 범위 밖이면 null.
    /// </summary>
    public MotherSpawnConfig GetConfig(int index)
    {
        if (index < 0 || index >= _motherConfigs.Count) return null;
        return _motherConfigs[index];
    }
}

/// <summary>
/// 인스펙터에서 EnemyMother 하나의 스폰 동작을 정의.
/// 리스트 인덱스가 SpawnTransformHandler의 생성 순서(0~3)와 대응.
/// </summary>
[Serializable]
public class MotherSpawnConfig
{
    [Tooltip("false면 Wave 시작 여부와 관계없이 이 Mother는 대기")]
    public bool isActive = true;

    [Tooltip("최대 동시 활성 적 수 (EnemyMother의 NativeArray 용량을 초과 불가)")]
    public int maxEnemies = 100;

    [Tooltip("첫 스폰까지 대기 시간 (초)")]
    public float startDelay = 5f;

    [Tooltip("스폰 주기 (초)")]
    public float spawnInterval = 1f;

    [Tooltip("주기당 소환 수")]
    public int enemiesPerSpawn = 5;
}
