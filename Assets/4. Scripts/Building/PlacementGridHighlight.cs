using UnityEngine;

/// <summary>
/// 건물 배치 프리뷰 중 커서 주변 그리드 셀을 원형 흰색 아웃라인으로 강조.
/// Graphics.DrawMesh를 사용해 퍼시스턴트 MeshRenderer 없이 렌더링.
/// (BuildingPreview의 GetComponentsInChildren<Renderer>와 충돌하지 않음)
///
/// 사용법: BuildingSystem/BuildingPreview와 같은 GameObject에 추가.
///         Inspector에서 _shader 슬롯에 PlacementGridHighlight 셰이더 할당.
/// </summary>
public class PlacementGridHighlight : MonoBehaviour
{
    [Header("하이라이트 반경 (월드 단위)")]
    [SerializeField] private float _radius = 8f;

    [Header("페이드 시작 지점 (0=중심, 1=가장자리)")]
    [SerializeField] [Range(0f, 1f)] private float _fadeStart = 0.55f;

    [Header("라인 두께 (셀 크기 대비 비율)")]
    [SerializeField] [Range(0.01f, 0.15f)] private float _lineWidth = 0.04f;

    [Header("최대 불투명도")]
    [SerializeField] [Range(0f, 1f)] private float _maxAlpha = 0.75f;

    [Header("셰이더 (미지정 시 Shader.Find로 자동 탐색)")]
    [SerializeField] private Shader _shader;

    // ─── 내부 상태 ────────────────────────────────────────────────────────────

    private Mesh     _mesh;
    private Material _mat;
    private bool     _visible;
    private Vector3  _center;

    // 셰이더 프로퍼티 ID (PropertyToID 캐싱: Update 핫패스 GC 방지)
    private static readonly int _centerPosId  = Shader.PropertyToID("_CenterPos");
    private static readonly int _gridOriginId = Shader.PropertyToID("_GridOrigin");
    private static readonly int _radiusId     = Shader.PropertyToID("_Radius");
    private static readonly int _cellSizeId   = Shader.PropertyToID("_CellSize");
    private static readonly int _lineWidthId  = Shader.PropertyToID("_LineWidth");
    private static readonly int _fadeStartId  = Shader.PropertyToID("_FadeStart");
    private static readonly int _maxAlphaId   = Shader.PropertyToID("_MaxAlpha");

    // ─── 생명주기 ────────────────────────────────────────────────────────────

    private void Awake()
    {
        _mesh = BuildQuadMesh();

        Shader s = _shader != null
            ? _shader
            : Shader.Find("Custom/PlacementGridHighlight");

        if (s == null)
        {
            Debug.LogError("[PlacementGridHighlight] 셰이더를 찾을 수 없습니다. " +
                           "Inspector의 _shader 슬롯에 PlacementGridHighlight 셰이더를 할당하세요.");
            enabled = false;
            return;
        }

        _mat = new Material(s) { renderQueue = 3001 };
    }

    private void LateUpdate()
    {
        if (!_visible || _mesh == null || _mat == null) return;

        // 메시 원점을 커서 위치로 이동해 draw (지면 살짝 위)
        Graphics.DrawMesh(
            _mesh,
            new Vector3(_center.x, 0.01f, _center.z),
            Quaternion.identity,
            _mat,
            0   // Default layer
        );
    }

    private void OnDestroy()
    {
        if (_mat  != null) Destroy(_mat);
        if (_mesh != null) Destroy(_mesh);
    }

    // ─── 공개 API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 매 프레임 UpdatePreview 시 호출. 중심 위치와 그리드 정보를 갱신한다.
    /// </summary>
    /// <param name="center">스냅된 빌딩 배치 중심 (월드 좌표)</param>
    /// <param name="cellSize">그리드 셀 한 변 길이</param>
    /// <param name="gridOrigin">그리드 BottomLeft 월드 좌표 (셀 경계 정렬용)</param>
    /// <param name="radius">실제 사용할 반경 = Inspector 기본값 + 구조물 크기 보정치</param>
    public void Show(Vector3 center, float cellSize, Vector3 gridOrigin, float radius)
    {
        if (_mat == null) return;

        _visible = true;
        _center  = center;

        _mat.SetVector(_centerPosId,  new Vector4(center.x,     0f, center.z,     0f));
        _mat.SetVector(_gridOriginId, new Vector4(gridOrigin.x, 0f, gridOrigin.z, 0f));
        _mat.SetFloat (_radiusId,     radius);
        _mat.SetFloat (_cellSizeId,   cellSize);
        _mat.SetFloat (_lineWidthId,  _lineWidth);
        _mat.SetFloat (_fadeStartId,  _fadeStart);
        _mat.SetFloat (_maxAlphaId,   _maxAlpha);
    }

    /// <summary>Inspector에 설정된 기본 반경값 (BuildingPreview에서 구조물 크기 보정 후 사용).</summary>
    public float BaseRadius => _radius;

    public void Hide()
    {
        _visible = false;
    }

    // ─── 내부 헬퍼 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 셰이더가 원 밖 픽셀을 discard하므로 메시는 최대 반경(50)을 커버하는 정사각 쿼드면 충분.
    /// 메시 로컬 원점 = 배치 중심 → Graphics.DrawMesh position으로 이동.
    /// </summary>
    private static Mesh BuildQuadMesh()
    {
        const float r = 55f; // 반경 최대 50 + 여유
        var mesh = new Mesh { name = "PlacementGridHighlightQuad" };
        mesh.vertices = new Vector3[]
        {
            new Vector3(-r, 0f, -r),
            new Vector3( r, 0f, -r),
            new Vector3(-r, 0f,  r),
            new Vector3( r, 0f,  r),
        };
        mesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
