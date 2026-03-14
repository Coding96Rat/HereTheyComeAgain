using UnityEngine;
using FishNet.Object;

[RequireComponent(typeof(CharacterController))]
public class SimpleEnemy : NetworkBehaviour
{
    public float moveSpeed = 3f;
    public float gravity = -9.81f;

    [Header("Stats")]
    public float maxHealth = 100f;
    private float _currentHealth;

    private FlowFieldSystem _flowFieldSystem;
    private int _myTargetPlayerIndex;

    private CharacterController _characterController;
    private Vector3 _velocity;

    // 배회(Wander)를 위한 변수
    private Vector3 _wanderDir;
    private float _wanderTimer;

    private void Awake()
    {
        _characterController = GetComponent<CharacterController>();
    }

    public override void OnSpawnServer(FishNet.Connection.NetworkConnection connection)
    {
        base.OnSpawnServer(connection);
        _currentHealth = maxHealth;
        _velocity = Vector3.zero;
        _wanderTimer = 0f;
    }

    public void InitializeEnemy(FlowFieldSystem ffs, int targetIndex)
    {
        _flowFieldSystem = ffs;
        _myTargetPlayerIndex = targetIndex;
    }

    private void Update()
    {
        if (!IsServerInitialized || _flowFieldSystem == null) return;

        // 1. 바닥의 화살표(과거 스냅샷 좌표) 방향을 읽어옵니다.
        Vector3 moveDir = _flowFieldSystem.GetFlowDirection(_myTargetPlayerIndex, transform.position);

        if (moveDir != Vector3.zero)
        {
            // 화살표가 있으면 그 길을 따라갑니다.
            _wanderTimer = 0f; // 배회 타이머 초기화
        }
        else
        {
            // 2. [유저 아이디어 적용] 목적지에 도착했는데 없거나 화살표가 끊기면 '배회(Wander)' 시작!
            _wanderTimer -= Time.deltaTime;
            if (_wanderTimer <= 0f)
            {
                // 1.5 ~ 3초마다 새로운 방향을 잡고 방을 샅샅이 뒤집니다.
                _wanderDir = new Vector3(Random.Range(-1f, 1f), 0, Random.Range(-1f, 1f)).normalized;
                _wanderTimer = Random.Range(1.5f, 3f);
            }
            moveDir = _wanderDir; // 벽 뚫고 날아가는 버그 원인을 배회 로직으로 교체!
        }

        // 3. 이동 및 중력 처리
        Vector3 horizontalMove = moveDir * moveSpeed;

        if (_characterController.isGrounded && _velocity.y < 0)
        {
            _velocity.y = -2f;
        }
        _velocity.y += gravity * Time.deltaTime;

        _characterController.Move((horizontalMove + _velocity) * Time.deltaTime);

        // 4. 회전 처리
        if (moveDir != Vector3.zero)
        {
            moveDir.y = 0;
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(moveDir), Time.deltaTime * 10f);
        }
    }

    public void TakeDamage(float damageAmount)
    {
        if (!IsServerInitialized) return;

        _currentHealth -= damageAmount;
        if (_currentHealth <= 0) Die();
    }

    private void Die()
    {
        ServerManager.Despawn(gameObject, DespawnType.Pool);
    }
}