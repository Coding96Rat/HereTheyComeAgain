using UnityEngine;

[CreateAssetMenu(fileName = "NewBuildingData", menuName = "Building/Building Data")]
public class BuildingDataSO : ScriptableObject
{
    public int id;
    public string buildingName;
    public GameObject prefab;
    public Sprite icon;

    [Header("Grid Footprint (칸 수)")]
    public int sizeX = 1;
    public int sizeZ = 1;

    [Header("Terrain Settings")]
    [Tooltip("이 비율(cellSize 기준) 이하로 지형이 침범하면 자동 평탄화. 초과 시 설치 불가.")]
    [Range(0f, 1f)]
    public float maxTerrainIntrusionRatio = 0.4f;
}
