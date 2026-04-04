using UnityEngine;

/// <summary>
/// MonoBehaviour 아님 — PlayerInteractor에서 new BuildingHelper(gridSystem)로 사용.
///
/// 역할:
///  1. 단일 레이캐스트 → 수학 연산으로 그리드 셀 특정 (셀별 Physics 없음, 성능 보장)
///  2. 건물 설치 가능 여부 검사 (그리드 점유 + 지형 높이)
///  3. 침범 지형 영역만 정확히 평탄화
/// </summary>
public class BuildingHelper
{
    private readonly GridSystem _gridSystem;

    public BuildingHelper(GridSystem gridSystem)
    {
        _gridSystem = gridSystem;
    }

    // ─── 1. 레이캐스트 → 그리드 스냅 ─────────────────────────────────────────

    /// <summary>
    /// 레이를 groundMask에 쏜 뒤, 수학 연산만으로 그리드 셀 인덱스와 스냅 좌표를 반환.
    /// 셀마다 PhysicsRaycast를 하지 않으므로 O(1) 성능.
    /// </summary>
    public bool GetGridFromRay(UnityEngine.Ray ray, UnityEngine.LayerMask groundMask,
        out UnityEngine.Vector3 snappedWorldPos, out int gridX, out int gridZ)
    {
        snappedWorldPos = UnityEngine.Vector3.zero;
        gridX = gridZ = 0;

        if (!UnityEngine.Physics.Raycast(ray, out UnityEngine.RaycastHit hit, UnityEngine.Mathf.Infinity, groundMask))
            return false;

        _gridSystem.GetGridPosition(hit.point, out gridX, out gridZ);
        snappedWorldPos = _gridSystem.GetWorldPosition(gridX, gridZ);
        return true;
    }

    // ─── 2. 설치 가능 여부 검사 ───────────────────────────────────────────────

    public PlacementResult CheckPlacement(BuildingDataSO data, int gridX, int gridZ)
    {
        UnityEngine.Terrain terrain = UnityEngine.Terrain.activeTerrain;
        float maxIntrusion = _gridSystem.CellSize * data.maxTerrainIntrusionRatio;
        bool needsFlattening = false;

        for (int dx = 0; dx < data.sizeX; dx++)
        {
            for (int dz = 0; dz < data.sizeZ; dz++)
            {
                int cx = gridX + dx;
                int cz = gridZ + dz;

                if (cx < 0 || cz < 0 || cx >= _gridSystem.Columns || cz >= _gridSystem.Rows)
                    return PlacementResult.OutOfBounds;

                if (_gridSystem.IsOccupied(cx, cz))
                    return PlacementResult.Blocked;

                if (BuildingSystem.Instance != null && BuildingSystem.Instance.IsOccupiedByBuilding(cx, cz))
                    return PlacementResult.Blocked;

                if (terrain != null)
                {
                    UnityEngine.Vector3 cellCenter = _gridSystem.GetWorldPosition(cx, cz)
                        + new UnityEngine.Vector3(_gridSystem.CellSize * 0.5f, 0f, _gridSystem.CellSize * 0.5f);
                    float terrainHeight = terrain.SampleHeight(cellCenter);

                    if (terrainHeight > maxIntrusion)
                        return PlacementResult.TerrainTooHigh;

                    if (terrainHeight > 0.01f)
                        needsFlattening = true;
                }
            }
        }

        return needsFlattening ? PlacementResult.ValidWithFlattening : PlacementResult.Valid;
    }

    // ─── 3. 지형 평탄화 ───────────────────────────────────────────────────────

    /// <summary>
    /// 건물 풋프린트 중 실제로 지형이 침범한 셀만 정확히 높이 0으로 평탄화.
    /// 모든 클라이언트에서 동일하게 호출돼야 하므로 ObserversRpc로 트리거됨.
    /// </summary>
    public void FlattenTerrainUnderBuilding(BuildingDataSO data, int gridX, int gridZ)
    {
        UnityEngine.Terrain terrain = UnityEngine.Terrain.activeTerrain;
        if (terrain == null) return;

        UnityEngine.TerrainData tData = terrain.terrainData;
        int res = tData.heightmapResolution;
        UnityEngine.Vector3 terrainPos = terrain.transform.position;

        for (int dx = 0; dx < data.sizeX; dx++)
        {
            for (int dz = 0; dz < data.sizeZ; dz++)
            {
                UnityEngine.Vector3 cellMin = _gridSystem.GetWorldPosition(gridX + dx, gridZ + dz);
                UnityEngine.Vector3 cellMax = cellMin + new UnityEngine.Vector3(_gridSystem.CellSize, 0f, _gridSystem.CellSize);

                // SampleHeight 조건 제거 — 호스트 모드 타이밍 버그 방지
                // (프리뷰가 이미 지형을 0으로 만든 상태에서 호출될 수 있으므로)
                FlattenRect(tData, res, terrainPos, cellMin, cellMax);
            }
        }
    }

    private void FlattenRect(UnityEngine.TerrainData tData, int res,
        UnityEngine.Vector3 terrainPos, UnityEngine.Vector3 worldMin, UnityEngine.Vector3 worldMax)
    {
        int xMin = ToHmX(tData, res, terrainPos, worldMin.x);
        int xMax = ToHmX(tData, res, terrainPos, worldMax.x);
        int zMin = ToHmZ(tData, res, terrainPos, worldMin.z);
        int zMax = ToHmZ(tData, res, terrainPos, worldMax.z);

        xMin = UnityEngine.Mathf.Clamp(xMin, 0, res - 1);
        xMax = UnityEngine.Mathf.Clamp(xMax, 0, res - 1);
        zMin = UnityEngine.Mathf.Clamp(zMin, 0, res - 1);
        zMax = UnityEngine.Mathf.Clamp(zMax, 0, res - 1);

        int w = xMax - xMin + 1;
        int h = zMax - zMin + 1;
        if (w <= 0 || h <= 0) return;

        tData.SetHeights(xMin, zMin, new float[h, w]);
    }

    private int ToHmX(UnityEngine.TerrainData d, int res, UnityEngine.Vector3 tp, float wx)
        => UnityEngine.Mathf.RoundToInt((wx - tp.x) / d.size.x * (res - 1));

    private int ToHmZ(UnityEngine.TerrainData d, int res, UnityEngine.Vector3 tp, float wz)
        => UnityEngine.Mathf.RoundToInt((wz - tp.z) / d.size.z * (res - 1));
}
