using UnityEngine;

public class EnvironmentSpawner : MonoBehaviour
{
    private GridSystem _gridSystem;

    [SerializeField]
    private GameObject GroundBasePrefab;
    // 텍스처 1장이 커버할 월드 크기 (현재 500맵에 Tiling 10이므로 50으로 설정
    private float _textureCoverage = 50f;

    private void Awake()
    {
        _gridSystem = FindFirstObjectByType<GridSystem>();
    }
    void Start()
    {
        CreateGround();
    }

    private void CreateGround()
    {
        // 1. 바닥 중심점 계산
        GameObject gObj = Instantiate(GroundBasePrefab, this.transform);
        float width = _gridSystem.Columns * _gridSystem.CellSize;
        float height = _gridSystem.Rows * _gridSystem.CellSize;

        // 위치는 맵의 정중앙에 배치합니다.
        gObj.transform.position = _gridSystem.LeftBottomLocation + new Vector3(width / 2, 0, height / 2);

        // 2. 크기(Scale) 2배 적용
        // Plane은 Scale 1 = 10 단위이므로 10으로 나눕니다. 추가로 전체 크기를 2배(2f)로 늘립니다.
        float scaleX = (width * 2f) / 10f;
        float scaleZ = (height * 2f) / 10f;
        gObj.transform.localScale = new Vector3(scaleX, 1, scaleZ);

        // 3. 머티리얼 Tiling 자동 맞춤
        Renderer groundRenderer = gObj.GetComponent<Renderer>();
        if (groundRenderer != null)
        {
            // 크기가 2배 늘어났으므로, 텍스처 타일링도 2배로 계산하여 해상도를 유지합니다.
            float tilingX = (width * 2f) / _textureCoverage;
            float tilingY = (height * 2f) / _textureCoverage;

            // 머티리얼의 메인 텍스처 타일링 값 적용
            groundRenderer.material.mainTextureScale = new Vector2(tilingX, tilingY);
        }
    }
}