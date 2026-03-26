using UnityEngine;

public class Enemy : MonoBehaviour
{
    [Header("Data")]
    public EnemyDataSO data;

    // SO에서 읽는 편의 프로퍼티
    public float maxHealth => data.maxHealth;
    public float speed     => data.speed;

    [HideInInspector] public int motherListIndex = -1;
    private float _currentHealth;

    [Header("Hit Capsule — Body")]
    public Vector3 hitCenterOffset = new Vector3(0f, 1f, 0f);
    public float hitRadius = 0.5f;
    public float hitHeight = 2.0f;

    [Header("Hit Capsule — Head")]
    public Vector3 hitHeadOffset = new Vector3(0f, 1.75f, 0f);
    public float hitHeadRadius = 0.2f;

    // 컬링 계산용 캐시 — hitHeadOffset/hitCenterOffset은 런타임 불변이므로 Awake에서 1회 계산
    [HideInInspector] public float hitHeadOffsetDist;

    private Transform _targetPlayer;
    private EnemyMother _myMother;

#if UNITY_EDITOR
    [Header("Editor Preview (빌드에 포함되지 않음)")]
    [SerializeField] private Vector3 _previewMeshOffset = Vector3.zero;
    [SerializeField] private Vector3 _previewMeshScale = Vector3.one;
#endif

    private void Awake()
    {
        hitHeadOffsetDist = Vector3.Distance(hitHeadOffset, hitCenterOffset);
    }

    public void InitializeEnemy(EnemyMother myMother, Transform targetPlayer)
    {
        _myMother = myMother;
        _targetPlayer = targetPlayer;
        _currentHealth = maxHealth;
    }

    public int GetTargetIndex()
    {
        if (_targetPlayer == null || !_targetPlayer.gameObject.activeInHierarchy)
        {
            _targetPlayer = EnemyMother.GetClosestTarget(transform.position);
        }
        return EnemyMother.ValidTargets.IndexOf(_targetPlayer);
    }

    public void TakeDamage(float damageAmount)
    {
        _currentHealth -= damageAmount;
        if (_currentHealth <= 0f && _myMother != null)
            _myMother.ReturnToPool(this);
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (UnityEditor.EditorApplication.isPlaying) return;

        // 프리뷰 메시 — data.mesh를 반투명 고스트로 그려서 캡슐 크기 조절 기준으로 활용
        if (data != null && data.mesh != null)
        {
            Gizmos.color = new Color(1f, 1f, 1f, 0.25f);
            Gizmos.DrawMesh(
                data.mesh,
                transform.position + _previewMeshOffset,
                transform.rotation,
                _previewMeshScale
            );
        }

        // Hit Capsule
        float halfExtent = Mathf.Max(0f, hitHeight * 0.5f - hitRadius);
        Vector3 center       = transform.position + hitCenterOffset;
        Vector3 topCenter    = center + Vector3.up * halfExtent;
        Vector3 bottomCenter = center - Vector3.up * halfExtent;

        Gizmos.color = new Color(0f, 1f, 0.4f, 0.9f);
        Gizmos.DrawWireSphere(topCenter,    hitRadius);
        Gizmos.DrawWireSphere(bottomCenter, hitRadius);
        Gizmos.DrawLine(topCenter + Vector3.forward * hitRadius, bottomCenter + Vector3.forward * hitRadius);
        Gizmos.DrawLine(topCenter - Vector3.forward * hitRadius, bottomCenter - Vector3.forward * hitRadius);
        Gizmos.DrawLine(topCenter + Vector3.right   * hitRadius, bottomCenter + Vector3.right   * hitRadius);
        Gizmos.DrawLine(topCenter - Vector3.right   * hitRadius, bottomCenter - Vector3.right   * hitRadius);

        // 몸통 중심점 마커
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.9f);
        Gizmos.DrawWireSphere(center, 0.08f);

        // 머리 구체 (빨간색)
        Gizmos.color = new Color(1f, 0.15f, 0.15f, 0.9f);
        Gizmos.DrawWireSphere(transform.position + hitHeadOffset, hitHeadRadius);
    }
#endif
}
