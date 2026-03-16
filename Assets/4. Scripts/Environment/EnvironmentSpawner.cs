using UnityEngine;

public class EnvironmentSpawner : MonoBehaviour
{
    private GridSystem _gridSystem;

    [SerializeField]
    private GameObject GroundBasePrefab;
    [SerializeField]
    private GameObject PlayerSpawnPrefab;

    // 텍스처 1장이 커버할 월드 크기 (현재 500맵에 Tiling 10이므로 50으로 설정)
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
        GameObject gObj = Instantiate(GroundBasePrefab, this.transform);
        float width = _gridSystem.Columns * _gridSystem.CellSize;
        float height = _gridSystem.Rows * _gridSystem.CellSize;

        // 1. 바닥 중심점 배치 (이제 복잡한 계산 없이 MiddlePoint 자체가 중앙입니다!)
        gObj.transform.position = _gridSystem.MiddlePoint;
        GameObject pObj = Instantiate(PlayerSpawnPrefab, this.transform);
        pObj.transform.position = _gridSystem.MiddlePoint + new Vector3(0, pObj.transform.localScale.y/2, 0);

        // 2. 크기(Scale) 2배 적용
        float scaleX = (width * 4f) / 10f;
        float scaleZ = (height * 4f) / 10f;
        gObj.transform.localScale = new Vector3(scaleX, 1, scaleZ);

        // 3. 머티리얼 Tiling 자동 맞춤
        Renderer groundRenderer = gObj.GetComponent<Renderer>();
        if (groundRenderer != null)
        {
            float tilingX = (width * 2f) / _textureCoverage;
            float tilingY = (height * 2f) / _textureCoverage;

            groundRenderer.material.mainTextureScale = new Vector2(tilingX, tilingY);
        }
    }
}