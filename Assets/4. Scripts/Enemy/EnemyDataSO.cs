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

    [Header("AI Settings")]
    [Tooltip("이 반경 내에 플레이어 관련 오브젝트가 들어오면 즉시 해당 오브젝트를 추적")]
    public float encounterRadius = 8f;
    [Tooltip("공격 상태로 전환되는 거리 (타겟과의 XZ 거리)")]
    public float attackRange = 2.5f;
    [Tooltip("누적 데미지가 최대 체력의 이 비율을 넘으면 해당 공격원을 추적 (0.7 ~ 0.8 권장)")]
    [Range(0.6f, 0.9f)] public float damageAggroRatio = 0.75f;
}
