using System;
using System.Collections.Generic;
using FishNet.Object;
using Unity.Mathematics;
using UnityEngine;

public class PlacedStructure : NetworkBehaviour, IPlayerRelated
{
    [Header("구조물 체력")]
    public float maxHealth = 500f;

    public Transform GetTransform() => transform;
    public bool IsAlive => _health > 0f;

    public static event Action<PlacedStructure> OnAnyDestroyed;

    public const float DamagePerEnemyPerSecond = 30f;
    public const float MaxDamagePerSecond = 300f;

    public static readonly List<PlacedStructure> All = new List<PlacedStructure>();

    private float _health;
    private FlowFieldSystem _ffs;
    private readonly List<int> _ownedCells = new List<int>(8);

    // 이벤트 중복 호출 방지용 플래그
    private bool _isDestroyedEventFired = false;

    // AABB 정밀 충돌 판정용 크기
    public Vector3 ColliderExtents { get; private set; } = Vector3.one;

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();
        _health = maxHealth;
        All.Add(this);

        _ffs = UnityEngine.Object.FindFirstObjectByType<FlowFieldSystem>();
        if (_ffs != null && _ffs.CostField.IsCreated)
            RegisterCells();
    }

    public override void OnStopNetwork()
    {
        base.OnStopNetwork();
        All.Remove(this);
        RestoreCells();

        // 💡 [버그 1 해결] 클라이언트 측에서도 이벤트 발생을 보장하여 슬롯 메모리 누수 방지
        if (!_isDestroyedEventFired)
        {
            _isDestroyedEventFired = true;
            OnAnyDestroyed?.Invoke(this);
        }
    }

    private void RegisterCells()
    {
        Collider col = GetComponentInChildren<Collider>();
        if (col == null)
        {
            TryMarkCell(_ffs.WorldToCell(transform.position));
            return;
        }

        Bounds b = col.bounds;
        ColliderExtents = b.extents;

        int x0 = Mathf.FloorToInt((b.min.x - _ffs.BottomLeft.x) / _ffs.aiCellSize);
        int x1 = Mathf.FloorToInt((b.max.x - _ffs.BottomLeft.x) / _ffs.aiCellSize);
        int z0 = Mathf.FloorToInt((b.min.z - _ffs.BottomLeft.z) / _ffs.aiCellSize);
        int z1 = Mathf.FloorToInt((b.max.z - _ffs.BottomLeft.z) / _ffs.aiCellSize);

        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                TryMarkCell(new int2(x, z));
    }

    private void TryMarkCell(int2 cell)
    {
        if (!_ffs.IsCellValid(cell)) return;
        int flat = _ffs.CellToFlat(cell);

        // 절대벽(255)이 아닌 254로 등록
        if (_ffs.TrySetCellCost(flat, 254))
            _ownedCells.Add(flat);
    }

    private void RestoreCells()
    {
        if (_ffs == null || _ownedCells.Count == 0) return;
        foreach (int flat in _ownedCells)
            _ffs.TrySetCellCost(flat, 1);
        _ownedCells.Clear();
    }

    public void TakeDamage(float amount)
    {
        if (!IsServerInitialized || _health <= 0f) return;
        _health -= amount;
        if (_health > 0f) return;

        RestoreCells();

        // 💡 [버그 1 해결] 파괴 확정 시 서버에서 1차 발송
        if (!_isDestroyedEventFired)
        {
            _isDestroyedEventFired = true;
            OnAnyDestroyed?.Invoke(this);
        }

        ServerManager.Despawn(NetworkObject);
    }
}