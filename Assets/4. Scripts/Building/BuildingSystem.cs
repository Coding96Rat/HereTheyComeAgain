using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Connection;
using UnityEngine;

/// <summary>
/// StageScene 전용 NetworkBehaviour.
///
/// 역할:
///  - BuildingRegistrySO 보유 → id로 BuildingDataSO 제공
///  - 클라이언트 설치 요청(ServerRpc) 수신 → 서버 검증 → NetworkObject 스폰 → 전체 동기화
///  - 런타임 점유 셀 SyncList → 모든 클라이언트가 동일한 그리드 상태 유지
/// </summary>
public class BuildingSystem : NetworkBehaviour
{
    [SerializeField] private BuildingRegistrySO _registry;

    public static BuildingSystem Instance { get; private set; }

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
    }

    public override void OnStopNetwork()
    {
        base.OnStopNetwork();
        if (Instance == this) Instance = null;
    }

    // ─── 데이터 접근 ─────────────────────────────────────────────────────────

    public BuildingDataSO             GetBuildingData(int id) => _registry?.GetById(id);
    public IReadOnlyList<BuildingDataSO> GetAllBuildings()    => _registry?.AllBuildings;

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
        if (_registry == null || _gridSystem == null) return;

        BuildingDataSO data = _registry.GetById(buildingId);
        if (data == null || data.prefab == null) return;

        // 서버 측 재검증 (치트 방지 + 레이스 컨디션 방지)
        var helper = new BuildingHelper(_gridSystem);
        PlacementResult result = helper.CheckPlacement(data, gridX, gridZ);

        if (result != PlacementResult.Valid)
            return;

        // 건물 스폰
        Vector3 worldPos    = helper.GetCenteredSpawnPosition(data, gridX, gridZ);
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
