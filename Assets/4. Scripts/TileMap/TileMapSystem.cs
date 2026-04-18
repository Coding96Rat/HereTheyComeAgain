using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 스타크래프트1 방식 그리드 타일맵 환경 시스템.
///
/// - GridSystem의 셀 크기와 동일한 1:1 타일 그리드를 유지한다.
/// - 타일 배치 시 FlowFieldSystem의 CostField를 갱신 → 적/유닛 모두 동일 장애물 인식.
/// - 각 타일은 하나의 Quad Mesh로 렌더링 (GPU Instancing 최적화 대상).
/// - 에디터 전용 Gizmo로 타일 경계 시각화.
/// </summary>
[RequireComponent(typeof(GridSystem))]
public class TileMapSystem : MonoBehaviour
{
    public static TileMapSystem Instance { get; private set; }

    [Header("Tile Settings")]
    [Tooltip("타일 팔레트 — 인덱스 0이 기본 타일")]
    public TileDataSO[] tilePalette;

    [Header("Map Size (GridSystem에서 자동 읽음)")]
    [SerializeField, HideInInspector] private int _cols;
    [SerializeField, HideInInspector] private int _rows;

    // 타일 데이터: 셀 인덱스 → 팔레트 인덱스
    [SerializeField, HideInInspector] private List<int> _tileIndices = new();

    // 런타임 타일 오브젝트 (시각)
    private GameObject[] _tileObjects;
    private MeshRenderer[] _tileRenderers;

    private GridSystem _grid;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        _grid = GetComponent<GridSystem>();
    }

    private void Start()
    {
        BuildVisuals();
        ApplyCostFieldAll();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ─── 초기화 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 에디터 또는 런타임에서 타일 인덱스 배열을 초기화한다.
    /// 팔레트 인덱스 0(기본 타일)으로 전체 채움.
    /// </summary>
    public void InitializeMap()
    {
        _cols = _grid.Columns;
        _rows = _grid.Rows;
        int total = _cols * _rows;

        _tileIndices = new List<int>(new int[total]);
    }

    /// <summary>런타임: 타일 Quad 오브젝트 생성.</summary>
    private void BuildVisuals()
    {
        if (_tileIndices == null || _tileIndices.Count == 0) return;
        if (tilePalette == null || tilePalette.Length == 0) return;

        _cols = _grid.Columns;
        _rows = _grid.Rows;

        _tileObjects = new GameObject[_cols * _rows];
        _tileRenderers = new MeshRenderer[_cols * _rows];

        float size = _grid.CellSize;

        for (int x = 0; x < _cols; x++)
        {
            for (int z = 0; z < _rows; z++)
            {
                int flat = z * _cols + x;
                int paletteIdx = flat < _tileIndices.Count ? _tileIndices[flat] : 0;
                TileDataSO data = GetTileData(paletteIdx);

                Vector3 center = _grid.GetWorldPosition(x, z) + new Vector3(size * 0.5f, 0f, size * 0.5f);

                GameObject tile = GameObject.CreatePrimitive(PrimitiveType.Quad);
                tile.name = $"Tile_{x}_{z}";
                tile.transform.SetParent(transform, false);
                tile.transform.position = center;
                tile.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                tile.transform.localScale = Vector3.one * size;

                // Collider 제거 (타일은 시각 전용, 물리는 별도 지형 레이어)
                DestroyImmediate(tile.GetComponent<MeshCollider>());

                MeshRenderer mr = tile.GetComponent<MeshRenderer>();
                if (data != null && data.material != null)
                    mr.sharedMaterial = data.material;

                _tileObjects[flat] = tile;
                _tileRenderers[flat] = mr;
            }
        }
    }

    // ─── 타일 배치 API ───────────────────────────────────────────────────────

    /// <summary>
    /// 특정 셀에 타일을 배치한다.
    /// FlowFieldSystem 및 GridSystem CostField를 즉시 갱신한다.
    /// </summary>
    public void PlaceTile(int gridX, int gridZ, int paletteIndex)
    {
        if (!IsValid(gridX, gridZ)) return;
        if (tilePalette == null || paletteIndex >= tilePalette.Length) return;

        int flat = gridZ * _cols + gridX;
        if (flat >= _tileIndices.Count) return;

        _tileIndices[flat] = paletteIndex;
        TileDataSO data = GetTileData(paletteIndex);
        byte cost = data != null ? data.GetCost() : (byte)1;

        // FlowFieldSystem CostField 갱신 (적 + 유닛 공유)
        if (FlowFieldSystem.Instance != null)
        {
            int ffFlat = WorldToFlowFieldFlat(gridX, gridZ);
            if (ffFlat >= 0) FlowFieldSystem.Instance.TrySetCellCost(ffFlat, cost);
        }

        // GridSystem occupied 갱신
        _grid.SetOccupied(gridX, gridZ, cost >= 255);

        // RTSFlowFieldSystem 캐시 무효화 (유닛 FlowField 재계산 트리거)
        RTSFlowFieldSystem.Instance?.InvalidateAllCaches();

        // 비주얼 갱신
        UpdateTileVisual(flat, data);
    }

    /// <summary>셀 범위 일괄 배치 (에디터 도구용).</summary>
    public void PlaceTileRect(int startX, int startZ, int width, int height, int paletteIndex)
    {
        for (int x = startX; x < startX + width; x++)
            for (int z = startZ; z < startZ + height; z++)
                PlaceTile(x, z, paletteIndex);
    }

    // ─── 조회 ─────────────────────────────────────────────────────────────────

    public TileDataSO GetTileAt(int gridX, int gridZ)
    {
        if (!IsValid(gridX, gridZ)) return null;
        int flat = gridZ * _cols + gridX;
        return flat < _tileIndices.Count ? GetTileData(_tileIndices[flat]) : null;
    }

    public bool IsWalkable(int gridX, int gridZ)
    {
        TileDataSO data = GetTileAt(gridX, gridZ);
        return data == null || data.tileType != TileType.Blocked;
    }

    // ─── 내부 유틸 ───────────────────────────────────────────────────────────

    private void ApplyCostFieldAll()
    {
        if (FlowFieldSystem.Instance == null) return;
        if (_tileIndices == null) return;

        for (int x = 0; x < _cols; x++)
        {
            for (int z = 0; z < _rows; z++)
            {
                int flat = z * _cols + x;
                if (flat >= _tileIndices.Count) continue;

                TileDataSO data = GetTileData(_tileIndices[flat]);
                // 185번째 줄 근처 ApplyCostFieldAll 메서드 내부
                byte cost = data != null ? data.GetCost() : (byte)1;

                int ffFlat = WorldToFlowFieldFlat(x, z);
                if (ffFlat >= 0) FlowFieldSystem.Instance.TrySetCellCost(ffFlat, cost);
            }
        }

        RTSFlowFieldSystem.Instance?.InvalidateAllCaches();
    }

    private void UpdateTileVisual(int flat, TileDataSO data)
    {
        if (_tileRenderers == null || flat >= _tileRenderers.Length) return;
        MeshRenderer mr = _tileRenderers[flat];
        if (mr == null) return;

        if (data != null && data.material != null)
            mr.sharedMaterial = data.material;
    }

    private TileDataSO GetTileData(int paletteIdx)
    {
        if (tilePalette == null || paletteIdx < 0 || paletteIdx >= tilePalette.Length) return null;
        return tilePalette[paletteIdx];
    }

    private bool IsValid(int x, int z) => x >= 0 && x < _cols && z >= 0 && z < _rows;

    /// <summary>GridSystem 셀 좌표 → FlowFieldSystem flat 인덱스 변환.</summary>
    private int WorldToFlowFieldFlat(int gridX, int gridZ)
    {
        if (FlowFieldSystem.Instance == null) return -1;

        Vector3 cellCenter = _grid.GetWorldPosition(gridX, gridZ)
            + new Vector3(_grid.CellSize * 0.5f, 0f, _grid.CellSize * 0.5f);

        Unity.Mathematics.int2 cell = FlowFieldSystem.Instance.WorldToCell(cellCenter);
        if (!FlowFieldSystem.Instance.IsCellValid(cell)) return -1;

        return FlowFieldSystem.Instance.CellToFlat(cell);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (_grid == null) _grid = GetComponent<GridSystem>();
        if (_grid == null) return;

        Gizmos.color = new Color(0f, 1f, 0f, 0.15f);
        float size = _grid.CellSize;

        for (int x = 0; x < _grid.Columns; x += 5)
        {
            for (int z = 0; z < _grid.Rows; z += 5)
            {
                Vector3 pos = _grid.GetWorldPosition(x, z) + new Vector3(size * 0.5f, 0.01f, size * 0.5f);
                Gizmos.DrawWireCube(pos, new Vector3(size, 0f, size));
            }
        }
    }
#endif
}
