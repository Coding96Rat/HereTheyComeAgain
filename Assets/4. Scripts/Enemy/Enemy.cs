using UnityEngine;
using FishNet.Object;

[RequireComponent(typeof(Rigidbody))]
public class Enemy : NetworkBehaviour
{
    // [추가됨] 자신이 EnemyMother 리스트의 몇 번째에 있는지 기억하는 변수
    [HideInInspector]
    public int motherListIndex = -1;

    [Header("이동 및 전투 설정")]
    public float baseMoveSpeed = 3f;
    public float rotationSpeed = 360f;
    public float attackRange = 2.0f;
    public float attackRate = 1.0f;

    [Header("시야 및 감지 설정")]
    public float aggroRadius = 20f;
    public LayerMask obstacleLayer;
    public LayerMask enemyLayer;

    [Header("Stats")]
    public float maxHealth = 100f;
    private float _currentHealth;

    private Rigidbody _rigidbody;
    private float _currentSpeed;
    private Transform _targetPlayer;

    public override void OnSpawnServer(FishNet.Connection.NetworkConnection connection)
    {
        base.OnSpawnServer(connection);
        _currentHealth = maxHealth;
        _rigidbody = GetComponent<Rigidbody>();
        _currentSpeed = baseMoveSpeed;

        // [수정됨] Add() 대신 AddEnemy() 전용 함수 사용
        if (EnemyMother.Instance != null)
        {
            EnemyMother.Instance.AddEnemy(this);
        }
    }

    public override void OnDespawnServer(FishNet.Connection.NetworkConnection connection)
    {
        // [수정됨] Remove() 대신 O(1) 처리가 되는 RemoveEnemy() 전용 함수 사용
        if (EnemyMother.Instance != null && motherListIndex != -1)
        {
            EnemyMother.Instance.RemoveEnemy(this);
        }

        base.OnDespawnServer(connection);
    }

    public void InitializeEnemy(Transform targetPlayer)
    {
        _targetPlayer = targetPlayer;
    }

    public void MotherTick()
    {
        // [수정됨] 타겟이 할당되지 않았거나 죽어서 사라진 경우 에러 방지
        if (_rigidbody == null || _targetPlayer == null) return;

        Vector3 dir = VectorExtensions.XZVector(_targetPlayer.position - _rigidbody.position).normalized;


        Vector3 nextPos = _rigidbody.position + dir * _currentSpeed * Time.fixedDeltaTime;

        Quaternion targetRotation = Quaternion.LookRotation(dir);
        Quaternion smoothedRotation = Quaternion.RotateTowards(
            _rigidbody.rotation,
            targetRotation,
            rotationSpeed * Time.fixedDeltaTime
        );


        _rigidbody.MoveRotation(smoothedRotation);
        _rigidbody.MovePosition(nextPos);
    }

    public void TakeDamage(float damageAmount)
    {
        if (!IsServerInitialized) return;
        _currentHealth -= damageAmount;
        if (_currentHealth <= 0) ServerManager.Despawn(gameObject, DespawnType.Pool);
    }
}