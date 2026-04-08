using System;
using System.Collections.Generic;
using FishNet.Object;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 플레이어가 설치했거나 플레이어 소유인 구조물(포탑, 벽, 기지 건물 등)에 부착.
///
/// ★ NetworkBehaviour 사용 이유:
///   BuildingPreview.StripForPreview()가 MonoBehaviour를 Destroy(비동기)해도,
///   Unity Start()는 다음 프레임에 호출되어 프리뷰 인스턴스에서도 FlowField를 오염시킨다.
///   OnStartNetwork()는 FishNet이 실제로 Spawn한 인스턴스에서만 호출되므로
///   프리뷰 인스턴스에서는 절대 실행되지 않아 안전하다.
///
/// 동작:
///  1. OnStartNetwork : 체력 초기화 → FlowField 셀 cost=255 마킹 → All 등록
///  2. 적이 2m 이내 접근 → EnemyMother(서버)가 TakeDamage 호출
///  3. 체력 0 → RestoreCells(cost=1) → ServerManager.Despawn
///  4. OnStopNetwork : All 제거 (클라이언트도 동일 실행)
/// </summary>
public class PlacedStructure : NetworkBehaviour, IPlayerRelated
{
    [Header("구조물 체력")]
    public float maxHealth = 500f;

    // ─── IPlayerRelated ───────────────────────────────────────────────────────
    public Transform GetTransform() => transform;
    public bool IsAlive => _health > 0f;

    // 구조물 파괴 시 모든 EnemyMother가 타겟 슬롯을 해제할 수 있도록 알림
    public static event Action<PlacedStructure> OnAnyDestroyed;

    /// <summary>적 1명이 초당 가하는 대미지 (EnemyMother에서 참조).</summary>
    public const float DamagePerEnemyPerSecond = 30f;

    /// <summary>구조물당 최대 초당 누적 대미지 (무한 적 중첩 방지).</summary>
    public const float MaxDamagePerSecond = 300f;

    // ─── 정적 레지스트리 ──────────────────────────────────────────────────────
    /// <summary>EnemyMother가 매 프레임 참조하는 활성 구조물 목록.</summary>
    public static readonly List<PlacedStructure> All = new List<PlacedStructure>();

    // ─── 인스턴스 상태 ────────────────────────────────────────────────────────
    private float _health;
    private FlowFieldSystem _ffs;
    private readonly List<int> _ownedCells = new List<int>(8);

    // ─── FishNet 생명주기 ─────────────────────────────────────────────────────

    /// <summary>
    /// 실제 스폰된 인스턴스에서만 호출 (프리뷰 인스턴스 절대 진입 안 함).
    /// 서버 + 모든 클라이언트에서 실행.
    /// </summary>
    public override void OnStartNetwork()
    {
        base.OnStartNetwork();

        _health = maxHealth;
        All.Add(this);

        // FlowField 셀 마킹 — 적이 이 구조물을 우회하거나 탐지할 수 있도록
        _ffs = UnityEngine.Object.FindFirstObjectByType<FlowFieldSystem>();
        if (_ffs != null && _ffs.CostField.IsCreated)
            RegisterCells();
    }

    /// <summary>Despawn 시 서버 + 모든 클라이언트에서 실행.</summary>
    public override void OnStopNetwork()
    {
        base.OnStopNetwork();
        All.Remove(this);
        // TakeDamage에서 이미 복구했다면 _ownedCells가 비어 있어 무해
        RestoreCells();
    }

    // ─── 콜라이더 반지름 (EnemyMother → EnemyMovementJob 전달용) ──────────────
    /// <summary>XZ 평면 기준 콜라이더 최대 반지름 (구조물 표면까지 거리 추정).</summary>
    public float ColliderHalfExtent { get; private set; } = 1f;

    // ─── FlowField 셀 등록 ────────────────────────────────────────────────────

    private void RegisterCells()
    {
        Collider col = GetComponentInChildren<Collider>();
        if (col == null)
        {
            TryMarkCell(_ffs.WorldToCell(transform.position));
            return;
        }

        Bounds b = col.bounds;
        // 콜라이더 bounds에서 XZ 반지름 계산 (EnemyMovementJob 정밀 정지용)
        ColliderHalfExtent = Mathf.Max(b.extents.x, b.extents.z);
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
        if (_ffs.TrySetCellCost(flat, 255))
            _ownedCells.Add(flat);
    }

    // ─── 셀 복구 ──────────────────────────────────────────────────────────────

    private void RestoreCells()
    {
        if (_ffs == null || _ownedCells.Count == 0) return;
        foreach (int flat in _ownedCells)
            _ffs.TrySetCellCost(flat, 1);
        _ownedCells.Clear();
    }

    // ─── 대미지 ───────────────────────────────────────────────────────────────

    /// <summary>서버에서만 호출 (EnemyMother — IsServerInitialized 조건 하에).</summary>
    public void TakeDamage(float amount)
    {
        if (!IsServerInitialized || _health <= 0f) return;
        _health -= amount;
        if (_health > 0f) return;

        // 파괴 전에 FlowField 복구 (OnStopNetwork보다 먼저 실행해 경로를 즉시 열어줌)
        RestoreCells();
        OnAnyDestroyed?.Invoke(this);
        ServerManager.Despawn(NetworkObject);
    }
}
