using System.Collections.Generic;
using UnityEngine;
using FishNet;
using FishNet.Transporting;

/// <summary>
/// 로컬 클라이언트의 RTS 입력 처리.
/// 좌클릭: 유닛 선택 / 우클릭: 선택된 유닛에게 이동 명령.
/// 이 클라이언트가 소유한 유닛(playerOwnerIndex == localPlayerIndex)만 선택 가능.
/// </summary>
public class RTSSelectionManager : MonoBehaviour
{
    [Header("Input Settings")]
    [SerializeField] private LayerMask _groundLayer;
    [SerializeField] private float _unitPickRadius = 0.6f;

    [Header("Camera Reference")]
    [SerializeField] private Camera _rtsCam;

    // 로컬 플레이어 인덱스 (FishNet ClientId 기반, Host=0, Client1=1 ...)
    private int _localPlayerIndex = -1;

    private readonly List<RTSUnit> _selected = new();

    private void Start()
    {
        if (_rtsCam == null) _rtsCam = Camera.main;

        var cm = InstanceFinder.ClientManager;
        if (cm == null) return;

        // 이미 연결돼 있으면 즉시 할당, 아니면 이벤트 구독
        if (cm.Started)
            _localPlayerIndex = cm.Connection.ClientId;
        else
            cm.OnClientConnectionState += OnClientConnectionState;
    }

    private void OnDestroy()
    {
        var cm = InstanceFinder.ClientManager;
        if (cm != null)
            cm.OnClientConnectionState -= OnClientConnectionState;
    }

    // FishNet 정식 API: LocalConnectionState.Started 시 ClientId 획득
    private void OnClientConnectionState(ClientConnectionStateArgs args)
    {
        if (args.ConnectionState != LocalConnectionState.Started) return;

        _localPlayerIndex = InstanceFinder.ClientManager.Connection.ClientId;
        InstanceFinder.ClientManager.OnClientConnectionState -= OnClientConnectionState;
    }

    private void Update()
    {
        if (_localPlayerIndex < 0) return;
        if (RTSUnitMother.Instance == null) return;

        if (Input.GetMouseButtonDown(0)) HandleLeftClick();
        if (Input.GetMouseButtonDown(1)) HandleRightClick();
    }

    // ─── 선택 ─────────────────────────────────────────────────────────────────

    private void HandleLeftClick()
    {
        if (_rtsCam == null) return;
        Ray ray = _rtsCam.ScreenPointToRay(Input.mousePosition);
        RTSUnit clicked = FindUnitOnRay(ray);

        if (!Input.GetKey(KeyCode.LeftShift)) ClearSelection();

        if (clicked != null && clicked.playerOwnerIndex == _localPlayerIndex)
            SelectUnit(clicked);
    }

    private RTSUnit FindUnitOnRay(Ray ray)
    {
        RTSUnit closest = null;
        float closestT = float.MaxValue;

        foreach (RTSUnit unit in RTSUnitMother.Instance.Units)
        {
            if (unit.playerOwnerIndex != _localPlayerIndex) continue;

            // Collider 없이 수학으로 구체-Ray 교차 판정 (Enemy 피격과 동일 방식)
            Vector3 center = unit.transform.position + unit.hitCenterOffset;
            Vector3 oc = ray.origin - center;
            float b = Vector3.Dot(ray.direction, oc);
            float c = Vector3.Dot(oc, oc) - _unitPickRadius * _unitPickRadius;
            float h = b * b - c;
            if (h < 0f) continue;

            float t = -b - Mathf.Sqrt(h);
            if (t >= 0f && t < closestT) { closestT = t; closest = unit; }
        }
        return closest;
    }

    private void SelectUnit(RTSUnit unit)
    {
        if (_selected.Contains(unit)) return;
        _selected.Add(unit);
        unit.SetSelected(true);
    }

    private void ClearSelection()
    {
        foreach (RTSUnit u in _selected) u.SetSelected(false);
        _selected.Clear();
    }

    // ─── 이동 명령 ────────────────────────────────────────────────────────────

    private void HandleRightClick()
    {
        if (_selected.Count == 0 || _rtsCam == null) return;

        Ray ray = _rtsCam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, 1000f, _groundLayer)) return;

        RTSUnitMother.Instance.CmdIssueMoveCommand(_localPlayerIndex, hit.point);
    }
}
