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
            if (_myMother != null && motherListIndex == -1) _myMother.AddEnemy(this);
        }
    }

    private void OnTargetSynced(NetworkObject prev, NetworkObject next, bool asServer)
    {
        if (!asServer && next != null) _targetPlayer = next.transform;
    }

    public override void OnStopClient()
    {
        base.OnStopClient();
        if (!IsServerInitialized && motherListIndex != -1 && _myMother != null)
        {
            _myMother.RemoveEnemy(this);
        }
    }

    // 💡 초기화 시 1번만 호출하여 타겟 인덱스만 Mother에게 넘겨줍니다. 
    // Update에서 4000번씩 호출하던 대참사 방지.
    public int GetTargetIndex()
    {
        if (!IsServerInitialized) return 0;

        if (_targetPlayer == null || !_targetPlayer.gameObject.activeInHierarchy)
        {
            Transform newTarget = EnemyMother.GetClosestTarget(transform.position);
            _targetPlayer = newTarget;

            if (newTarget != null && newTarget.TryGetComponent(out NetworkObject netObj))
            {
                _syncedTargetObj.Value = netObj;
            }
        }

        return EnemyMother.ValidTargets.IndexOf(_targetPlayer);
    }

    public void TakeDamage(float damageAmount)
    {
        if (!IsServerInitialized) return;
        _currentHealth.Value -= damageAmount;
        if (_currentHealth.Value <= 0) ServerManager.Despawn(gameObject, DespawnType.Pool);
    }
}