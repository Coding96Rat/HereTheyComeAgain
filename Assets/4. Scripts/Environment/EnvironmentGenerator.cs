using UnityEngine;

public class EnvironmentGenerator : MonoBehaviour
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
        // 1. 바닥 생성
        GameObject gObj = Instantiate(GroundBasePrefab, this.transform);
        float width = _gridSystem.Columns * _gridSystem.CellSize;
        float height = _gridSystem.Rows * _gridSystem.CellSize;
        gObj.transform.position = _gridSystem.LeftBottomLocation + new Vector3(width / 2, 0, height / 2);

        // 2. 크기(Scale) 자동 맞춤
        // Plane은 Scale 1 = 10 단위이므로 10으로 나눕니다.
        float scaleX = (_gridSystem.Columns * _gridSystem.CellSize) / 10f;
        float scaleZ = (_gridSystem.Rows * _gridSystem.CellSize) / 10f;
        gObj.transform.localScale = new Vector3(scaleX, 1, scaleZ);

        // 3. 머티리얼 Tiling 자동 맞춤
        Renderer groundRenderer = gObj.GetComponent<Renderer>();
        if (groundRenderer != null)
        {
            // 총 길이를 커버리지(50)로 나누어 최적의 타일링 값을 구함
            float tilingX = (_gridSystem.Columns * _gridSystem.CellSize) / _textureCoverage;
            float tilingY = (_gridSystem.Rows * _gridSystem.CellSize) / _textureCoverage;

            // 머티리얼의 메인 텍스처 타일링 값 적용
            groundRenderer.material.mainTextureScale = new Vector2(tilingX, tilingY);
        }
    }

}
