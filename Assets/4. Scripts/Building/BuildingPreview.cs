using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// BuildingSystem과 같은 GameObject에 컴포넌트로 미리 부착.
/// 동적 AddComponent 없이 SetBuilding/ClearBuilding으로 비주얼만 교체.
///
/// 규칙:
///  - 프리뷰 중 그림자 OFF (ShadowCastingMode.Off)
///  - 설치 후 실제 스폰된 오브젝트는 원래 설정대로 그림자 ON
/// </summary>
public class BuildingPreview : MonoBehaviour
{
    // ─── 인스펙터 노출 설정 ───────────────────────────────────────────────────

    [Header("설치 가능 색상 (흰색 계열)")]
    [SerializeField] private Color _canPlaceColor = new Color(1f, 1f, 1f, 0.45f);

    [Header("설치 불가 색상 (빨간색 계열)")]
    [SerializeField] private Color _cantPlaceColor = new Color(1f, 0.15f, 0.15f, 0.5f);

    [Header("프리뷰 높이 오프셋 (지형 위에 살짝 띄우기)")]
    [SerializeField] private float _heightOffset = 0.02f;

    // ─── 내부 상태 ───────────────────────────────────────────────────────────

    private Material       _canPlaceMat;
    private Material       _cantPlaceMat;
    private Renderer[]     _renderers;
    private GameObject     _visualRoot;

    // ApplyMaterials 핫패스 GC 제로화
    private Material[][]   _cachedCanMats;
    private Material[][]   _cachedCantMats;

    private GridSystem              _gridSystem;
    private BuildingDataSO          _data;
    private PlacementGridHighlight  _gridHighlight;

    // ─── 초기화 ──────────────────────────────────────────────────────────────

    public void Setup(GridSystem gridSystem)
    {
        _gridSystem    = gridSystem;
        _gridHighlight = GetComponent<PlacementGridHighlight>();
    }

    // ─── 건물 교체 ───────────────────────────────────────────────────────────

    public void SetBuilding(BuildingDataSO data)
    {
        DestroyVisual();

        _data = data;
        if (data?.prefab == null) return;

        _visualRoot = Instantiate(data.prefab, transform);
        _visualRoot.transform.localPosition = Vector3.zero;

        StripForPreview(_visualRoot);

        _renderers = GetComponentsInChildren<Renderer>(true);
        foreach (var r in _renderers)
            r.shadowCastingMode = ShadowCastingMode.Off;

        RebuildMaterials();
    }

    public void ClearBuilding()
    {
        DestroyVisual();
        _data = null;
        _gridHighlight?.Hide();
    }

    public bool HasBuilding => _data != null && _visualRoot != null;

    /// <summary>
    /// 프리뷰 비주얼을 일시적으로 숨기거나 복원한다.
    /// 데이터와 오브젝트는 유지 — 범위 안으로 돌아오면 UpdatePreview가 즉시 복원.
    /// </summary>
    public void SetPreviewVisible(bool visible)
    {
        if (_visualRoot != null)
            _visualRoot.SetActive(visible);
        if (!visible)
            _gridHighlight?.Hide();
    }

    // ─── 매 프레임 갱신 ──────────────────────────────────────────────────────

    public void UpdatePreview(Vector3 snappedPos, PlacementResult result)
    {
        transform.position = snappedPos + Vector3.up * _heightOffset;

        bool canPlace = result == PlacementResult.Valid;
        ApplyMaterials(canPlace);

        // 그리드 하이라이트: 반경 = Inspector 기본값 + max(sizeX, sizeZ) * cellSize
        if (_gridSystem != null && _gridHighlight != null && _data != null)
        {
            float buildingExtent = Mathf.Max(_data.sizeX, _data.sizeZ) * _gridSystem.CellSize;
            float dynamicRadius  = _gridHighlight.BaseRadius + buildingExtent;
            _gridHighlight.Show(snappedPos, _gridSystem.CellSize,
                                _gridSystem.GetBottomLeft(), dynamicRadius);
        }
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
            mat.SetFloat("_Surface",   1f);
            mat.SetFloat("_Blend",     0f);
            mat.SetFloat("_SrcBlend",  5f);
            mat.SetFloat("_DstBlend",  10f);
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

    private void OnDestroy()
    {
        if (_canPlaceMat  != null) Destroy(_canPlaceMat);
        if (_cantPlaceMat != null) Destroy(_cantPlaceMat);
    }
}
