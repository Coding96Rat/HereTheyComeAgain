using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Connection;
using UnityEngine;

/// <summary>
/// StageScene 전용 NetworkBehaviour.
///
/// 역할:
///  - BuildingRegistrySO 보유 → Addressables 비동기 로드 후 id로 BuildingDataSO 제공
///  - 클라이언트 설치 요청(ServerRpc) 수신 → 서버 검증 → NetworkObject 스폰 → 전체 동기화
///  - 런타임 점유 셀 SyncList → 모든 클라이언트가 동일한 그리드 상태 유지
/// </summary>
public class BuildingSystem : NetworkBehaviour
{
    [SerializeField] private BuildingRegistrySO _registry;

    [Tooltip("서버 측 설치 허용 최대 거리 (클라이언트 치트 방지용, PlayerInteractor 값과 동일하게 설정)")]
    [SerializeField] private float _maxPlacementDistance = 15f;

    public static BuildingSystem Instance { get; private set; }

    /// <summary>
    /// Addressables 레지스트리 로드가 완료되면 true.
    /// PlayerInteractor.InitializeStageRefs()에서 UI 초기화 타이밍 제어에 사용.
    /// </summary>
    public bool IsRegistryReady { get; private set; }

    /// <summary>
    /// 같은 GameObject에 부착된 BuildingPreview 컴포넌트.
    /// 로컬 클라이언트 전용 — 네트워크 동기화 없음.
    /// </summary>
    public BuildingPreview Preview { get; private set; }

    // 런타임 건물 점유 셀 인덱스 (gridZ * Columns + gridX) — 서버→클라이언트 자동 동기화
    private readonly SyncList<int> _occupiedCellIndices = new SyncList<int>();

    private GridSystem _gridSystem;

    // ─── 생명주기 ─────────────────────────────────────────────────────────────

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();
        Instance    = this;
        _gridSystem = Object.FindFirstObjectByType<GridSystem>();

        Preview = GetComponent<BuildingPreview>();
        if (Preview != null && _gridSystem != null)
            Preview.Setup(_gridSystem);

        StartCoroutine(InitRegistryAsync());
    }

    public override void OnStopNetwork()
    {
        base.OnStopNetwork();
        if (Instance == this) Instance = null;

        IsRegistryReady = false;
        _registry?.Unload();
    }

    // ─── 레지스트리 비동기 초기화 ────────────────────────────────────────────

    private IEnumerator InitRegistryAsync()
    {
        if (_registry == null)
        {
            Debug.LogError("[BuildingSystem] _registry가 Inspector에 할당되지 않았습니다.");
            yield break;
        }

        var loadTask = _registry.LoadAllAsync();
        yield return new WaitUntil(() => loadTask.IsCompleted);

        if (loadTask.IsFaulted)
        {
            Debug.LogError("[BuildingSystem] BuildingRegistrySO 로드 실패. 콘솔 로그를 확인하세요.");
            yield break;
        }

        IsRegistryReady = true;
        Debug.Log($"[BuildingSystem] Registry ready — {_registry.AllBuildings.Count}개 건물 로드 완료.");
    }

    // ─── 데이터 접근 ─────────────────────────────────────────────────────────

    public BuildingDataSO GetBuildingData(int id) => _registry?.GetById(id);

    public IReadOnlyCollection<BuildingDataSO> GetAllBuildings() => _registry?.AllBuildings;

    public bool IsOccupiedByBuilding(int gridX, int gridZ)
    {
        if (_gridSystem == null) return false;
        int idx = gridZ * _gridSystem.Columns + gridX;
        return _occupiedCellIndices.Contains(idx);
    }

    // ─── 설치 요청 (ServerRpc) ────────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void ServerPlaceBuilding(int buildingId, int gridX, int gridZ, NetworkConnection caller = null)
    {
        if (_registry == null || !IsRegistryReady || _gridSystem == null) return;

        BuildingDataSO data = _registry.GetById(buildingId);
        if (data == null || data.prefab == null) return;

        // 서버 측 재검증 (치트 방지 + 레이스 컨디션 방지)
        var helper = new BuildingHelper(_gridSystem);
        PlacementResult result = helper.CheckPlacement(data, gridX, gridZ);

        if (result != PlacementResult.Valid)
            return;

        // 건물 스폰
        Vector3 worldPos = helper.GetCenteredSpawnPosition(data, gridX, gridZ);

        // 서버 측 설치 거리 검증 (클라이언트 치트 방지)
        if (_maxPlacementDistance > 0f && caller?.FirstObject != null)
        {
            Vector3 playerPos = caller.FirstObject.transform.position;
            float distXZ = Mathf.Sqrt(
                (playerPos.x - worldPos.x) * (playerPos.x - worldPos.x) +
                (playerPos.z - worldPos.z) * (playerPos.z - worldPos.z));
            if (distXZ > _maxPlacementDistance) return;
        }

        GameObject instance = Instantiate(data.prefab, worldPos, Quaternion.identity);
        NetworkObject nob   = instance.GetComponent<NetworkObject>();
        if (nob != null) ServerManager.Spawn(nob);

        // 셀 점유 기록 (SyncList → 전체 클라이언트 자동 동기화)
        for (int dx = 0; dx < data.sizeX; dx++)
        {
            for (int dz = 0; dz < data.sizeZ; dz++)
            {
                int idx = (gridZ + dz) * _gridSystem.Columns + (gridX + dx);
                if (!_occupiedCellIndices.Contains(idx))
                    _occupiedCellIndices.Add(idx);
            }
        }
    }
}
