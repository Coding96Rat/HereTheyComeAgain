using UnityEngine;

[CreateAssetMenu(fileName = "EnemyData", menuName = "Game/Enemy Data")]
public class EnemyDataSO : ScriptableObject
{
    [Header("Stats")]
    public float maxHealth = 100f;
    public float speed = 3f;

    [Header("Rendering")]
    public Mesh mesh;
    public Material material;
}
