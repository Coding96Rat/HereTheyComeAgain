using UnityEngine;

public enum EnemyAIState { Chase, Attack }

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

    // ─── AI 상태 ──────────────────────────────────────────────────────────────
    public EnemyAIState State { get; private set; }

    /// <summary>현재 추적 대상. 초기값 = 스폰 시 할당된 플레이어.</summary>
    public Transform CommittedTarget { get; private set; }

    // ─── 위협 추적 — 고정 크기 배열로 GC Alloc 없음 ───────────────────────────
    private struct ThreatEntry
    {
        public Transform Source;
        public float AccumulatedDamage;
    }

    private const int MaxThreatSources = 8;
    private ThreatEntry[] _threatEntries;
    private int _threatCount;

    private Transform _targetPlayer; // 스폰 시 할당된 초기 플레이어 (폴백 용)
    private EnemyMother _myMother;

#if UNITY_EDITOR
    [Header("Editor Preview (빌드에 포함되지 않음)")]
    [SerializeField] private Vector3 _previewMeshOffset = Vector3.zero;
    [SerializeField] private Vector3 _previewMeshScale = Vector3.one;
#endif

    private void Awake()
    {
        hitHeadOffsetDist = Vector3.Distance(hitHeadOffset, hitCenterOffset);
        _threatEntries = new ThreatEntry[MaxThreatSources];
    }

    public void InitializeEnemy(EnemyMother myMother, Transform targetPlayer)
    {
        _myMother        = myMother;
        _targetPlayer    = targetPlayer;
        CommittedTarget  = targetPlayer;
        _currentHealth   = maxHealth;
        State            = EnemyAIState.Chase;
        _threatCount     = 0;
        for (int i = 0; i < MaxThreatSources; i++)
            _threatEntries[i] = default;
    }

    // ─── 외부 호출 API ────────────────────────────────────────────────────────

    public int GetTargetIndex()
    {
        if (_targetPlayer == null || !_targetPlayer.gameObject.activeInHierarchy)
            _targetPlayer = EnemyMother.GetClosestTarget(transform.position);
        return EnemyMother.ValidTargets.IndexOf(_targetPlayer);
    }

    /// <summary>EnemyMother가 동적 슬롯을 배정한 뒤 호출 — CommittedTarget만 갱신.</summary>
    public void SetCommittedTarget(Transform target)
    {
        CommittedTarget = target;
    }

    /// <summary>추적 중인 대상이 파괴됐을 때 EnemyMother가 호출 — 초기 플레이어로 폴백.</summary>
    public void OnCommittedTargetDestroyed()
    {
        CommittedTarget = _targetPlayer;
        State           = EnemyAIState.Chase;
        ClearThreats();
    }

    /// <summary>공격 범위 진입 시 EnemyMother AI 틱에서 호출.</summary>
    public void TransitionToAttack()
    {
        State = EnemyAIState.Attack;
        ClearThreats();
    }

    /// <summary>타겟이 멀어졌을 때 EnemyMother AI 틱에서 호출.</summary>
    public void TransitionToChase()
    {
        State = EnemyAIState.Chase;
    }

    // ─── 대미지 처리 ─────────────────────────────────────────────────────────

    /// <param name="source">피격 원인 Transform. null이면 어그로 무시 (환경 데미지 등).</param>
    public void TakeDamage(float damageAmount, Transform source = null)
    {
        _currentHealth -= damageAmount;

        if (source != null)
            HandleAttackAggro(source, damageAmount);

        if (_currentHealth <= 0f && _myMother != null)
            _myMother.ReturnToPool(this);
    }

    // ─── 어그로 내부 로직 ─────────────────────────────────────────────────────

    private void HandleAttackAggro(Transform attacker, float damage)
    {
        if (State == EnemyAIState.Chase)
        {
            // 요구사항 2: 추적 단계에서 공격받으면 즉시 공격원을 추적
            if (attacker != CommittedTarget)
                _myMother?.CommitEnemyToTarget(this, attacker);
        }
        else // Attack 단계
        {
            // 요구사항 3b: 누적 데미지가 HP의 damageAggroRatio 이상이면 해당 공격원으로 전환
            AddThreat(attacker, damage);
            Transform highThreat = GetHighestThreatAboveThreshold();
            if (highThreat != null && highThreat != CommittedTarget)
            {
                _myMother?.CommitEnemyToTarget(this, highThreat);
                ClearThreats();
            }
        }
    }

    private void AddThreat(Transform source, float damage)
    {
        // 기존 항목 갱신
        for (int i = 0; i < _threatCount; i++)
        {
            if (_threatEntries[i].Source == source)
            {
                _threatEntries[i].AccumulatedDamage += damage;
                return;
            }
        }
        // 빈 슬롯에 추가
        if (_threatCount < MaxThreatSources)
        {
            _threatEntries[_threatCount] = new ThreatEntry { Source = source, AccumulatedDamage = damage };
            _threatCount++;
            return;
        }
        // 슬롯 가득 찼을 때 — 가장 낮은 위협 항목 교체
        int lowestIdx = 0;
        float lowestDmg = _threatEntries[0].AccumulatedDamage;
        for (int i = 1; i < MaxThreatSources; i++)
        {
            if (_threatEntries[i].AccumulatedDamage < lowestDmg)
            {
                lowestDmg = _threatEntries[i].AccumulatedDamage;
                lowestIdx = i;
            }
        }
        if (damage > lowestDmg)
            _threatEntries[lowestIdx] = new ThreatEntry { Source = source, AccumulatedDamage = damage };
    }

    private Transform GetHighestThreatAboveThreshold()
    {
        float threshold   = maxHealth * data.damageAggroRatio;
        Transform highest = null;
        float highestDmg  = threshold; // 임계값 이상인 것만 유효

        for (int i = 0; i < _threatCount; i++)
        {
            if (_threatEntries[i].Source == null) continue;
            if (_threatEntries[i].AccumulatedDamage > highestDmg)
            {
                highestDmg = _threatEntries[i].AccumulatedDamage;
                highest    = _threatEntries[i].Source;
            }
        }
        return highest;
    }

    private void ClearThreats()
    {
        _threatCount = 0;
        for (int i = 0; i < MaxThreatSources; i++)
            _threatEntries[i] = default;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        // 플레이 중에는 호출 자체를 막음 — 좀비 수만큼 매 프레임 실행되므로
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

        // Hit Capsule — Body
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

        // Hit Capsule — Head
        Gizmos.color = new Color(1f, 0.15f, 0.15f, 0.9f);
        Gizmos.DrawWireSphere(transform.position + hitHeadOffset, hitHeadRadius);
    }
#endif

}
