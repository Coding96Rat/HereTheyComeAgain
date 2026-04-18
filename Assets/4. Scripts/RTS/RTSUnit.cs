using UnityEngine;

/// <summary>
/// RTS 플레이어 유닛 데이터 컨테이너. Enemy.cs와 동일한 역할.
/// 이동 처리는 RTSUnitMother + RTSUnitMovementJob이 담당한다.
/// </summary>
public class RTSUnit : MonoBehaviour, IPlayerRelated
{
    // ─── IPlayerRelated ──────────────────────────────────────────────────────
    public Transform GetTransform() => transform;
    public bool IsAlive => gameObject.activeInHierarchy && _currentHealth > 0f;

    [Header("Stats")]
    public float maxHealth = 100f;
    public float speed = 5f;

    [Header("Hit Capsule — Body")]
    public Vector3 hitCenterOffset = new Vector3(0f, 1f, 0f);
    public float hitRadius = 0.35f;
    public float hitHeight = 2f;

    [Header("Hit Capsule — Head")]
    public Vector3 hitHeadOffset = new Vector3(0f, 1.75f, 0f);
    public float hitHeadRadius = 0.18f;

    [Header("Visual")]
    [SerializeField] private GameObject _selectionIndicator;

    // RTSUnitMother가 관리하는 리스트 내 인덱스 (Enemy.motherListIndex와 동일)
    [HideInInspector] public int unitListIndex = -1;

    // 이 유닛을 소유한 플레이어 인덱스 (FlowField 슬롯과 대응)
    [HideInInspector] public int playerOwnerIndex = -1;

    [HideInInspector] public float hitHeadOffsetDist;

    private float _currentHealth;
    private RTSUnitMother _myMother;

    // ─── 초기화 ──────────────────────────────────────────────────────────────

    private void Awake()
    {
        hitHeadOffsetDist = Vector3.Distance(hitHeadOffset, hitCenterOffset);
    }

    private void OnEnable()
    {
        _currentHealth = maxHealth;
        EnemyMother.RegisterTarget(transform);
    }

    private void OnDisable()
    {
        EnemyMother.UnregisterTarget(transform);
    }

    public void Initialize(RTSUnitMother mother, int ownerIndex)
    {
        _myMother       = mother;
        playerOwnerIndex = ownerIndex;
        _currentHealth  = maxHealth;
    }

    // ─── 선택 상태 ───────────────────────────────────────────────────────────

    public void SetSelected(bool selected)
    {
        if (_selectionIndicator != null)
            _selectionIndicator.SetActive(selected);
    }

    // ─── 피격 (Enemy.TakeDamage와 동일 패턴) ─────────────────────────────────

    public void TakeDamage(float amount)
    {
        _currentHealth -= amount;
        if (_currentHealth <= 0f && _myMother != null)
            _myMother.RemoveUnit(this);
    }

    public float CurrentHealth => _currentHealth;

#if UNITY_EDITOR
    // Enemy.cs의 OnDrawGizmos와 동일한 구조로 가상 캡슐 콜라이더 시각화.
    // 플레이 중에는 비활성화 (유닛 수만큼 매 프레임 실행 방지).
    private void OnDrawGizmos()
    {
        if (UnityEditor.EditorApplication.isPlaying) return;

        // ── Body 캡슐 ──────────────────────────────────────────────────────────
        float halfExtent     = Mathf.Max(0f, hitHeight * 0.5f - hitRadius);
        Vector3 center       = transform.position + hitCenterOffset;
        Vector3 topCenter    = center + Vector3.up * halfExtent;
        Vector3 bottomCenter = center - Vector3.up * halfExtent;

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);   // 파란색 (적과 구분)
        Gizmos.DrawWireSphere(topCenter,    hitRadius);
        Gizmos.DrawWireSphere(bottomCenter, hitRadius);
        Gizmos.DrawLine(topCenter    + Vector3.forward * hitRadius, bottomCenter + Vector3.forward * hitRadius);
        Gizmos.DrawLine(topCenter    - Vector3.forward * hitRadius, bottomCenter - Vector3.forward * hitRadius);
        Gizmos.DrawLine(topCenter    + Vector3.right   * hitRadius, bottomCenter + Vector3.right   * hitRadius);
        Gizmos.DrawLine(topCenter    - Vector3.right   * hitRadius, bottomCenter - Vector3.right   * hitRadius);

        // 몸통 중심 마커
        Gizmos.color = new Color(0f, 1f, 1f, 0.9f);
        Gizmos.DrawWireSphere(center, 0.06f);

        // ── Head 캡슐 ──────────────────────────────────────────────────────────
        Gizmos.color = new Color(1f, 0.8f, 0.1f, 0.9f);   // 노란색
        Gizmos.DrawWireSphere(transform.position + hitHeadOffset, hitHeadRadius);
    }
#endif
}
