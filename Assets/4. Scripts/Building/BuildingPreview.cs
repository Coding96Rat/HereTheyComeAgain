using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// BuildingSystem과 같은 GameObject에 컴포넌트로 미리 부착.
/// 동적 AddComponent 없이 SetBuilding/ClearBuilding으로 비주얼만 교체.
///
/// 규칙:
///  - 프리뷰 중 그림자 OFF (ShadowCastingMode.Off)
///  - 설치 후 실제 스폰된 오브젝트는 원래 설정대로 그림자 ON
///  - 씬 이동/건물 취소 시 지형 미리보기 자동 복원
/// </summary>
public class BuildingPreview : MonoBehaviour
{
    // ─── 인스펙터 노출 설정 ───────────────────────────────────────────────────

    [Header("설치 가능 색상 (흰색 계열)")]
    [SerializeField] private Color _canPlaceColor = new Color(1f, 1f, 1f, 0.45f);

    [Header("설치 불가 색상 (빨간색 계열)")]
    [SerializeField] private Color _cantPlaceColor = new Color(1f, 0.15f, 0.15f, 0.5f);

    [Header("지형 미리보기 (설치 전 평탄화 시뮬레이션)")]
    [SerializeField] private bool _enableTerrainPreview = true;

    [Header("프리뷰 높이 오프셋 (지형 위에 살짝 띄우기)")]
    [SerializeField] private float _heightOffset = 0.02f;

    // ─── 내부 상태 ───────────────────────────────────────────────────────────

    private Material       _canPlaceMat;
    private Material       _cantPlaceMat;
    private Renderer[]     _renderers;
    private GameObject     _visualRoot;     // 현재 프리뷰 비주얼 (교체 가능)

    // ApplyMaterials 핫패스 GC 제로화 — SetBuilding 시점에 슬롯 수만큼 미리 배열 생성
    private Material[][]   _cachedCanMats;
    private Material[][]   _cachedCantMats;

    private GridSystem     _gridSystem;
    private BuildingDataSO _data;
    private Terrain        _terrain;

    // 지형 미리보기 저장/복원
    private float[,]       _savedHeights;
    private int            _savedXBase, _savedZBase;
    private bool           _hasTerrainPreview;

    // 이전 셀 위치 (중복 갱신 방지)
    private int            _lastGridX = int.MinValue;
    private int            _lastGridZ = int.MinValue;

    // ─── 초기화 (BuildingSystem.OnStartNetwork에서 호출) ─────────────────────

    public void Setup(GridSystem gridSystem)
    {
        _gridSystem = gridSystem;
        _terrain    = Terrain.activeTerrain;
    }

    // ─── 건물 교체 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 건물을 선택했을 때 호출. 기존 비주얼 파괴 후 새 비주얼 생성.
    /// </summary>
    public void SetBuilding(BuildingDataSO data)
    {
        DestroyVisual();
        RestoreTerrainPreview();
        ResetCellCache();

        _data = data;
        if (data?.prefab == null) return;

        _visualRoot = Instantiate(data.prefab, transform);
        _visualRoot.transform.localPosition = Vector3.zero;

        StripForPreview(_visualRoot);

        _renderers = GetComponentsInChildren<Renderer>(true);
        foreach (var r in _renderers)
            r.shadowCastingMode = ShadowCastingMode.Off;  // 프리뷰 중 그림자 OFF

        RebuildMaterials();
    }

    /// <summary>
    /// 건물 선택 취소 또는 빌딩 모드 종료 시 호출.
    /// 비주얼만 제거하고, 이 컴포넌트/GO는 그대로 유지.
    /// </summary>
    public void ClearBuilding()
    {
        RestoreTerrainPreview();
        DestroyVisual();
        ResetCellCache();
        _data = null;
    }

    public bool HasBuilding => _data != null && _visualRoot != null;

    // ─── 매 프레임 갱신 ──────────────────────────────────────────────────────

    public void UpdatePreview(Vector3 snappedPos, int gridX, int gridZ, PlacementResult result)
    {
        transform.position = snappedPos + Vector3.up * _heightOffset;

        bool canPlace = result == PlacementResult.Valid || result == PlacementResult.ValidWithFlattening;
        ApplyMaterials(canPlace);

        if (gridX == _lastGridX && gridZ == _lastGridZ) return;
        _lastGridX = gridX;
        _lastGridZ = gridZ;

        if (_enableTerrainPreview && result == PlacementResult.ValidWithFlattening)
            ShowTerrainPreview(gridX, gridZ);
        else
            RestoreTerrainPreview();
    }

    // ─── 지형 미리보기 ───────────────────────────────────────────────────────

    private void ShowTerrainPreview(int gridX, int gridZ)
    {
        if (_terrain == null || _data == null || _gridSystem == null) return;

        RestoreTerrainPreview();

        TerrainData tData = _terrain.terrainData;
        int         res   = tData.heightmapResolution;
        Vector3     tPos  = _terrain.transform.position;

        Vector3 footMin = _gridSystem.GetWorldPosition(gridX,                gridZ);
        Vector3 footMax = _gridSystem.GetWorldPosition(gridX + _data.sizeX, gridZ + _data.sizeZ);

        int xMin = Mathf.Clamp(ToHmX(tData, res, tPos, footMin.x), 0, res - 1);
        int xMax = Mathf.Clamp(ToHmX(tData, res, tPos, footMax.x), 0, res - 1);
        int zMin = Mathf.Clamp(ToHmZ(tData, res, tPos, footMin.z), 0, res - 1);
        int zMax = Mathf.Clamp(ToHmZ(tData, res, tPos, footMax.z), 0, res - 1);

        int w = xMax - xMin + 1;
        int h = zMax - zMin + 1;
        if (w <= 0 || h <= 0) return;

        _savedXBase   = xMin;
        _savedZBase   = zMin;
        _savedHeights = tData.GetHeights(xMin, zMin, w, h);

        tData.SetHeights(xMin, zMin, new float[h, w]);
        _hasTerrainPreview = true;
    }

    public void RestoreTerrainPreview()
    {
        if (!_hasTerrainPreview || _terrain == null || _savedHeights == null) return;
        _terrain.terrainData.SetHeights(_savedXBase, _savedZBase, _savedHeights);
        _savedHeights      = null;
        _hasTerrainPreview = false;
    }

    // ─── 내부 헬퍼 ───────────────────────────────────────────────────────────

    private void ApplyMaterials(bool canPlace)
    {
        if (_renderers == null) return;
        Material[][] cache = canPlace ? _cachedCanMats : _cachedCantMats;
        if (cache == null) return;
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (cache[i].Length == 1)
                _renderers[i].sharedMaterial = cache[i][0];
            else
                _renderers[i].sharedMaterials = cache[i];
        }
    }

    private void RebuildMaterials()
    {
        if (_canPlaceMat  != null) Destroy(_canPlaceMat);
        if (_cantPlaceMat != null) Destroy(_cantPlaceMat);
        _canPlaceMat  = BuildTransparentMaterial(_canPlaceColor);
        _cantPlaceMat = BuildTransparentMaterial(_cantPlaceColor);
        BuildMaterialCache();
    }

    // SetBuilding 시점에 슬롯 수만큼 배열을 미리 생성 — ApplyMaterials 핫패스 GC 제로화
    private void BuildMaterialCache()
    {
        if (_renderers == null) return;
        _cachedCanMats  = new Material[_renderers.Length][];
        _cachedCantMats = new Material[_renderers.Length][];
        for (int i = 0; i < _renderers.Length; i++)
        {
            int slots = _renderers[i].sharedMaterials.Length;
            _cachedCanMats[i]  = new Material[slots];
            _cachedCantMats[i] = new Material[slots];
            for (int s = 0; s < slots; s++)
            {
                _cachedCanMats[i][s]  = _canPlaceMat;
                _cachedCantMats[i][s] = _cantPlaceMat;
            }
        }
    }

    private void DestroyVisual()
    {
        if (_visualRoot != null) { Destroy(_visualRoot); _visualRoot = null; }
        _renderers = null;
    }

    private void ResetCellCache()
    {
        _lastGridX = int.MinValue;
        _lastGridZ = int.MinValue;
    }

    private static void StripForPreview(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<Collider>(true))      Destroy(c);
        foreach (var r in go.GetComponentsInChildren<Rigidbody>(true))     Destroy(r);
        foreach (var m in go.GetComponentsInChildren<MonoBehaviour>(true)) Destroy(m);
    }

    private static Material BuildTransparentMaterial(Color color)
    {
        bool isURP = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline != null;
        Material mat;

        if (isURP)
        {
            Shader s = Shader.Find("Universal Render Pipeline/Lit")
                    ?? Shader.Find("Universal Render Pipeline/Unlit")
                    ?? Shader.Find("Standard");
            mat = new Material(s);
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Surface",   1f);   // Transparent
            mat.SetFloat("_Blend",     0f);   // Alpha
            mat.SetFloat("_SrcBlend",  5f);   // SrcAlpha
            mat.SetFloat("_DstBlend",  10f);  // OneMinusSrcAlpha
            mat.SetFloat("_ZWrite",    0f);
            mat.SetFloat("_AlphaClip", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        }
        else
        {
            mat = new Material(Shader.Find("Standard"));
            mat.color = color;
            mat.SetFloat("_Mode",    3f);
            mat.SetInt("_SrcBlend",  5);
            mat.SetInt("_DstBlend",  10);
            mat.SetInt("_ZWrite",    0);
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        }

        mat.renderQueue = 3000;
        return mat;
    }

    private static int ToHmX(TerrainData d, int res, Vector3 tp, float wx)
        => Mathf.RoundToInt((wx - tp.x) / d.size.x * (res - 1));

    private static int ToHmZ(TerrainData d, int res, Vector3 tp, float wz)
        => Mathf.RoundToInt((wz - tp.z) / d.size.z * (res - 1));

    private void OnDestroy()
    {
        RestoreTerrainPreview();
        if (_canPlaceMat  != null) Destroy(_canPlaceMat);
        if (_cantPlaceMat != null) Destroy(_cantPlaceMat);
    }
}
