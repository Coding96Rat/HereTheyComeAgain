using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class TerrainShaper : MonoBehaviour
{
    [Header("References")]
    public Terrain targetTerrain;
    public SpawnTransformHandler spawnHandler;

    [Header("Base Shaping Settings (평탄화 구역)")]
    [Tooltip("중앙 기지 주변으로 완벽하게 평평해질 반지름 (Height 0)")]
    public float centerFlatRadius = 25f;
    [Tooltip("평지에서 산맥으로 부드럽게 이어질 경사면의 길이")]
    public float centerBlendDistance = 20f;

    [Tooltip("마더 스폰 위치 주변으로 평평해질 반지름 (Height 0)")]
    public float motherFlatRadius = 15f;
    public float motherBlendDistance = 15f;

    [Header("Mountain Generation (자동 산맥 생성)")]
    [Tooltip("체크하면 평지를 제외한 빈 공간에 자연스러운 산맥을 자동으로 생성합니다.")]
    public bool autoGenerateMountains = true;
    [Tooltip("산맥의 최대 높이")]
    public float mountainHeight = 30f;
    [Tooltip("산맥의 굵기/디테일 (값이 작을수록 웅장하고, 클수록 자글자글해집니다)")]
    public float noiseScale = 0.02f;

#if UNITY_EDITOR
    public void ShapeTerrain()
    {
        if (targetTerrain == null || spawnHandler == null)
        {
            Debug.LogError("Terrain과 SpawnTransformHandler를 연결해주세요!");
            return;
        }

        TerrainData tData = targetTerrain.terrainData;
        Undo.RegisterCompleteObjectUndo(tData, "Shape Terrain"); // Ctrl+Z 지원

        int res = tData.heightmapResolution;
        float[,] heights = tData.GetHeights(0, 0, res, res);

        Vector3 terrainPos = targetTerrain.transform.position;
        Vector3 terrainSize = tData.size;
        Vector3 centerPos = spawnHandler.gridSystem != null ? spawnHandler.gridSystem.MiddlePoint : Vector3.zero;

        // 매번 다른 모양의 산맥을 만들기 위한 랜덤 시드
        float offsetX = Random.Range(0f, 9999f);
        float offsetZ = Random.Range(0f, 9999f);

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float worldX = terrainPos.x + ((float)x / (res - 1)) * terrainSize.x;
                float worldZ = terrainPos.z + ((float)y / (res - 1)) * terrainSize.z;

                // 💡 1. 중앙 기지 영향력 계산 (0: 완전 평지, 1: 산맥)
                float distToCenter = Vector2.Distance(new Vector2(worldX, worldZ), new Vector2(centerPos.x, centerPos.z));
                float centerT = Mathf.Clamp01((distToCenter - centerFlatRadius) / centerBlendDistance);

                // 💡 2. 마더 스폰 지역 영향력 계산
                float motherT = 1f;
                if (spawnHandler.generatedMotherSpawns != null)
                {
                    for (int i = 0; i < spawnHandler.generatedMotherSpawns.Length; i++)
                    {
                        Transform motherSpawn = spawnHandler.generatedMotherSpawns[i];
                        if (motherSpawn == null) continue;

                        float distToMother = Vector2.Distance(new Vector2(worldX, worldZ), new Vector2(motherSpawn.position.x, motherSpawn.position.z));
                        float mT = Mathf.Clamp01((distToMother - motherFlatRadius) / motherBlendDistance);
                        motherT = Mathf.Min(motherT, mT); // 가장 가까운 마더의 깎임 정도 적용
                    }
                }

                // 💡 3. 최종 영향력 융합 (중앙 기지나 마더 중 한 곳이라도 닿으면 깎임)
                float finalT = Mathf.Min(centerT, motherT);

                // 경사면을 부드럽게(S자 곡선) 만듦
                float smoothedT = Mathf.SmoothStep(0f, 1f, finalT);

                float finalWorldHeight = 0f;

                // 💡 4. 지형 높이 최종 결정
                if (autoGenerateMountains)
                {
                    // 퍼린 노이즈 2개를 겹쳐서 자연스러운 바위산맥 형태 생성
                    float noise1 = Mathf.PerlinNoise(worldX * noiseScale + offsetX, worldZ * noiseScale + offsetZ);
                    float noise2 = Mathf.PerlinNoise(worldX * noiseScale * 2f + offsetX, worldZ * noiseScale * 2f + offsetZ) * 0.5f;
                    float rawMountainHeight = (noise1 + noise2) * (mountainHeight / 1.5f);

                    // 산맥 높이 * 영향력 (평지 구역은 smoothedT가 0이므로 완벽한 Height 0이 됨)
                    finalWorldHeight = rawMountainHeight * smoothedT;
                }
                else
                {
                    // 산맥 자동 생성을 끄면, 현재 지형을 유지하되 스폰 구역만 0으로 깎아냄
                    float currentWorldHeight = heights[y, x] * terrainSize.y;
                    finalWorldHeight = currentWorldHeight * smoothedT;
                }

                // 정규화(0~1)해서 저장
                heights[y, x] = finalWorldHeight / terrainSize.y;
            }
        }

        targetTerrain.terrainData.SetHeights(0, 0, heights);
        Debug.Log("✅ 지형 성형 완료! 마더 스폰지와 기지가 평탄화되고 주변이 산맥으로 덮였습니다.");
    }
#endif
}

#if UNITY_EDITOR
[CustomEditor(typeof(TerrainShaper))]
public class TerrainShaperEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        TerrainShaper script = (TerrainShaper)target;

        GUILayout.Space(20);
        if (GUILayout.Button("⛰️ 지형 자동 생성 및 평탄화 (Bake)", GUILayout.Height(40)))
        {
            script.ShapeTerrain();
        }

        GUILayout.Space(10);
        EditorGUILayout.HelpBox("체크박스를 켜고 이 버튼을 누르면, 기지와 마더 스폰 지점은 Height 0이 되고 주변에 멋진 산맥이 알아서 융기합니다!", MessageType.Info);
    }
}
#endif