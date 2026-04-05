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
}
