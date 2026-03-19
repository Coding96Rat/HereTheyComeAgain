using UnityEngine;
using FishNet.Object;
using FishNet.Object.Synchronizing;

public class Enemy : NetworkBehaviour
{
    [HideInInspector] public int motherListIndex = -1;

    [Header("Stats")]
    public float maxHealth = 100f;
    private readonly SyncVar<float> _currentHealth = new SyncVar<float>();

    private Transform _targetPlayer;
    private Vector3 _cachedTargetPos;
    private EnemyMother _myMother;

    [HideInInspector] public MeshRenderer myRenderer;
    [HideInInspector] public float lastAnimState = -1f;

    private readonly SyncVar<NetworkObject> _syncedMotherObj = new SyncVar<NetworkObject>();
    private readonly SyncVar<NetworkObject> _syncedTargetObj = new SyncVar<NetworkObject>();

    private void Awake()
    {
        myRenderer = GetComponentInChildren<MeshRenderer>();
        _syncedMotherObj.OnChange += OnMotherSynced;
        _syncedTargetObj.OnChange += OnTargetSynced;
    }

    public void InitializeEnemy(EnemyMother myMother, Transform targetPlayer)
    {
        _myMother = myMother;
        _targetPlayer = targetPlayer;
        _syncedMotherObj.Value = myMother.NetworkObject;

        if (targetPlayer != null && targetPlayer.TryGetComponent(out NetworkObject netObj))
        {
            _syncedTargetObj.Value = netObj;
        }
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        _currentHealth.Value = maxHealth;
        _myMother.AddEnemy(this);
    }

    public override void OnStopServer()
    {
        if (motherListIndex != -1 && _myMother != null) _myMother.RemoveEnemy(this);
        base.OnStopServer();
    }

    public override void OnSpawnServer(FishNet.Connection.NetworkConnection connection)
    {
        base.OnSpawnServer(connection);
        TargetSnapPosition(connection, transform.position);
    }

    [TargetRpc]
    private void TargetSnapPosition(FishNet.Connection.NetworkConnection conn, Vector3 currentServerPos)
    {
        transform.position = currentServerPos;
    }

    private void OnMotherSynced(NetworkObject prev, NetworkObject next, bool asServer)
    {
        if (!asServer && next != null)
        {
            _myMother = next.GetComponent<EnemyMother>();
            if (_myMother != null && motherListIndex == -1)
            {
                _myMother.AddEnemy(this);
            }
        }
    }

    private void OnTargetSynced(NetworkObject prev, NetworkObject next, bool asServer)
    {
        if (!asServer && next != null)
        {
            _targetPlayer = next.transform;
        }
    }

    public override void OnStopClient()
    {
        base.OnStopClient();
        if (!IsServerInitialized && motherListIndex != -1 && _myMother != null)
        {
            _myMother.RemoveEnemy(this);
        }
    }

    public Vector3 GetTargetPosition()
    {
        // 오직 서버(Host)만 타겟 생존 여부를 판단하고 새 타겟을 정할 권한을 가짐
        if (IsServerInitialized)
        {
            // 치열하게 때리던 타겟(방화벽)이 파괴되거나 사라졌다면?!
            if (_targetPlayer == null || !_targetPlayer.gameObject.activeInHierarchy)
            {
                // [핵심] "좀비 자신의 현재 위치"에서 가장 가까운 다음 목표물(건물, 플레이어)로 시선을 돌림!
                Transform newTarget = EnemyMother.GetClosestTarget(transform.position);
                _targetPlayer = newTarget;

                // 새 목표를 클라이언트들에게 알려 똑같이 바라보게 함
                if (newTarget != null && newTarget.TryGetComponent(out NetworkObject netObj))
                {
                    _syncedTargetObj.Value = netObj;
                }
            }
        }

        if (_targetPlayer != null) _cachedTargetPos = _targetPlayer.position;
        return _cachedTargetPos;
    }

    public void TakeDamage(float damageAmount)
    {
        if (!IsServerInitialized) return;
        _currentHealth.Value -= damageAmount;
        if (_currentHealth.Value <= 0) ServerManager.Despawn(gameObject, DespawnType.Pool);
    }
}