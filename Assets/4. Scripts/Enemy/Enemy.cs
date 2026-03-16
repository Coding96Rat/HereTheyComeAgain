using UnityEngine;
using FishNet.Object;

public class Enemy : NetworkBehaviour
{
    [HideInInspector] public int motherListIndex = -1;

    [Header("Stats")]
    public float maxHealth = 100f;
    private float _currentHealth;

    private Transform _targetPlayer;
    private Vector3 _cachedTargetPos;
    private EnemyMother _myMother;

    [HideInInspector] public MeshRenderer myRenderer; // Mother가 랜덤 시작 시간(TimeOffset)을 주입할 때 사용

    // [추가] 이전 애니메이션 상태를 기억하는 변수 (초기값을 -1로 주어 처음엔 무조건 업데이트 되도록 함)
    [HideInInspector] public float lastAnimState = -1f;

    private void Awake()
    {
        // 자식(Zombie1)의 구워진 메쉬 렌더러 가져오기
        myRenderer = GetComponentInChildren<MeshRenderer>();
    }

    public override void OnSpawnServer(FishNet.Connection.NetworkConnection connection)
    {
        base.OnSpawnServer(connection);
        _currentHealth = maxHealth;
        _myMother.AddEnemy(this);
    }

    public override void OnDespawnServer(FishNet.Connection.NetworkConnection connection)
    {
        if (motherListIndex != -1) _myMother.RemoveEnemy(this);
        base.OnDespawnServer(connection);
    }

    public void InitializeEnemy(EnemyMother myMother, Transform targetPlayer)
    {
        _myMother = myMother;
        _targetPlayer = targetPlayer;
    }

    public Vector3 GetTargetPosition()
    {
        if (_targetPlayer != null) _cachedTargetPos = _targetPlayer.position;
        return _cachedTargetPos;
    }

    public void TakeDamage(float damageAmount)
    {
        if (!IsServerInitialized) return;
        _currentHealth -= damageAmount;

        // 💡 [추후 타격감 작업] 피격 파티클(피 튀김) 스폰 함수 호출 자리
        // ObserversSpawnBloodEffect(transform.position);

        if (_currentHealth <= 0) ServerManager.Despawn(gameObject, DespawnType.Pool);
    }
}