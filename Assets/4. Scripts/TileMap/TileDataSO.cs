using UnityEngine;

/// <summary>
/// 타일 종류별 시각/이동 데이터 ScriptableObject.
/// TileMapSystem이 이 SO를 배열로 가져 타일 팔레트를 구성한다.
/// </summary>
[CreateAssetMenu(menuName = "HereTheyComeAgain/TileData", fileName = "TileData")]
public class TileDataSO : ScriptableObject
{
    [Header("Identity")]
    public string tileName = "Tile";
    public TileType tileType = TileType.Walkable;

    [Header("Visual")]
    public Material material;
    public Color    tintColor = Color.white;

    [Header("FlowField Cost Override")]
    [Tooltip("0이면 TileType 기본값 사용")]
    public byte customCost = 0;

    public byte GetCost() => customCost > 0 ? customCost : (byte)tileType;
}
