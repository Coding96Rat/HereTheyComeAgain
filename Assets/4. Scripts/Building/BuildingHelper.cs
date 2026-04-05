using UnityEngine;

/// <summary>
/// MonoBehaviour 아님 — PlayerInteractor에서 new BuildingHelper(gridSystem)로 사용.
///
/// 역할:
///  1. 단일 레이캐스트 → 수학 연산으로 그리드 셀 특정 (셀별 Physics 없음, 성능 보장)
///  2. 건물 설치 가능 여부 검사 (그리드 점유 + 지형 유무)
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

        // Y=0 평면과 레이의 교차점으로 그리드를 결정
        float dirY = ray.direction.y;
        if (dirY >= -0.001f) return false;

        float t = -ray.origin.y / dirY;
        if (t < 0f || t > 150f) return false;

        UnityEngine.Vector3 groundPoint = ray.GetPoint(t);
        _gridSystem.GetGridPosition(groundPoint, out gridX, out gridZ);

        snappedWorldPos = _gridSystem.GetWorldPosition(gridX, gridZ);
        return true;
    }

    // ─── 2. 설치 가능 여부 검사 ───────────────────────────────────────────────

    /// <summary>
    /// 건물 풋프린트 내 셀 중 하나라도 Terrain 높이가 있으면 TerrainBlocked 반환.
    /// </summary>
    public PlacementResult CheckPlacement(BuildingDataSO data, int gridX, int gridZ)
    {
        UnityEngine.Terrain terrain = UnityEngine.Terrain.activeTerrain;

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
                    UnityEngine.Vector3 cellMin = _gridSystem.GetWorldPosition(cx, cz);
                    float coverage = GetTerrainCoverageRatio(terrain, cellMin, _gridSystem.CellSize);

                    // 외곽 셀: Terrain이 50% 이상 차지하면 설치 불가
                    // 내부 셀: 조금이라도 Terrain 있으면 설치 불가
                    bool isBorder = (dx == 0 || dx == data.sizeX - 1 ||
                                     dz == 0 || dz == data.sizeZ - 1);

                    if (isBorder ? coverage >= 0.5f : coverage > 0f)
                        return PlacementResult.TerrainBlocked;
                }
            }
        }

        return PlacementResult.Valid;
    }

    /// <summary>
    /// 배치 코너 그리드(placeGridX/Z)와 빌딩 데이터를 받아,
    /// 프리팹 메시 중심(Bounds.center)이 커서 셀 XZ 중앙에 오도록 계산된 Transform 월드 위치 반환.
    /// </summary>
    public UnityEngine.Vector3 GetCenteredSpawnPosition(BuildingDataSO data, int placeGridX, int placeGridZ)
    {
        int cursorX = placeGridX + data.sizeX / 2;
        int cursorZ = placeGridZ + data.sizeZ / 2;

        UnityEngine.Vector3 cornerPos = _gridSystem.GetWorldPosition(cursorX, cursorZ);
        float half = _gridSystem.CellSize * 0.5f;
        float targetCX = cornerPos.x + half;
        float targetCZ = cornerPos.z + half;

        UnityEngine.Bounds bounds = GetPrefabLocalBounds(data.prefab);

        return new UnityEngine.Vector3(targetCX - bounds.center.x,
                                       0f,
                                       targetCZ - bounds.center.z);
    }

    private static UnityEngine.Bounds GetPrefabLocalBounds(UnityEngine.GameObject prefab)
    {
        var filters = prefab.GetComponentsInChildren<UnityEngine.MeshFilter>(true);
        bool initialized = false;
        UnityEngine.Bounds result = default;

        foreach (var mf in filters)
        {
            if (mf.sharedMesh == null) continue;

            UnityEngine.Matrix4x4 m = mf.transform.localToWorldMatrix;
            UnityEngine.Bounds mesh  = mf.sharedMesh.bounds;
            UnityEngine.Vector3 c    = mesh.center;
            UnityEngine.Vector3 e    = mesh.extents;

            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                UnityEngine.Vector3 corner = m.MultiplyPoint3x4(
                    c + new UnityEngine.Vector3(e.x * sx, e.y * sy, e.z * sz));

                if (!initialized) { result = new UnityEngine.Bounds(corner, UnityEngine.Vector3.zero); initialized = true; }
                else               result.Encapsulate(corner);
            }
        }

        return initialized ? result : new UnityEngine.Bounds(UnityEngine.Vector3.zero, UnityEngine.Vector3.zero);
    }

    // ─── 내부 헬퍼 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 셀 내부를 3×3 격자(9개 지점)로 샘플링.
    /// 각 지점의 Terrain 월드 Y > 0.05f 인 비율(0~1)을 반환.
    /// 0 = 완전히 평탄, 1 = 9개 전부 지형에 덮힘.
    /// </summary>
    private float GetTerrainCoverageRatio(UnityEngine.Terrain terrain,
        UnityEngine.Vector3 cellMin, float cellSize)
    {
        float baseY  = terrain.transform.position.y;
        int   hits   = 0;
        float step   = cellSize * 0.5f;

        for (int xi = 0; xi <= 2; xi++)
        for (int zi = 0; zi <= 2; zi++)
        {
            float wx = cellMin.x + xi * step;
            float wz = cellMin.z + zi * step;
            float surfaceY = baseY + terrain.SampleHeight(new UnityEngine.Vector3(wx, 0f, wz));
            if (surfaceY > 0.05f) hits++;
        }
        return hits / 9f;
    }
}
