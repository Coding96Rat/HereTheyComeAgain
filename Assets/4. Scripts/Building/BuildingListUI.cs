using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// StageScene 전용 건물 선택 UI.
///
/// B 키로 빌딩 모드 진입 → Tab 키로 이 패널 토글.
/// 패널 열릴 때 커서 해제, 닫힐 때 커서 잠금.
/// BuildingSystem의 프리팹 목록을 기반으로 버튼을 동적 생성.
/// </summary>
public class BuildingListUI : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject  _panel;
    [SerializeField] private Transform   _buttonContainer;
    [SerializeField] private GameObject  _buttonPrefab;   // Button + TextMeshProUGUI 포함

    private CanvasGroup  _canvasGroup;
    private Action<int>  _onBuildingSelected;

    // ─── 초기화 ───────────────────────────────────────────────────────────────

    private void Awake()
    {
        _canvasGroup = _panel.GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
            _canvasGroup = _panel.AddComponent<CanvasGroup>();

        SetVisible(false);
    }

    /// <summary>
    /// PlayerInteractor에서 빌딩 모드 진입 시 한 번 호출.
    /// </summary>
    public void Initialize(BuildingSystem buildingSystem, Action<int> onBuildingSelected)
    {
        if (buildingSystem == null)
        {
            Debug.LogWarning("[BuildingListUI] BuildingSystem이 아직 준비되지 않았습니다.");
            return;
        }
        _onBuildingSelected = onBuildingSelected;
        PopulateList(buildingSystem.GetAllBuildings());
    }

    // ─── 가시성 제어 ─────────────────────────────────────────────────────────

    public bool IsVisible => _canvasGroup != null && _canvasGroup.alpha > 0.5f;

    public void ToggleVisible() => SetVisible(!IsVisible);

    public void SetVisible(bool visible)
    {
        if (_canvasGroup == null) return;

        _canvasGroup.alpha          = visible ? 1f : 0f;
        _canvasGroup.interactable   = visible;
        _canvasGroup.blocksRaycasts = visible;

        // 커서 상태 전환
        Cursor.lockState = visible ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible   = visible;
    }

    // ─── 목록 생성 ───────────────────────────────────────────────────────────

    private void PopulateList(IReadOnlyList<BuildingDataSO> buildings)
    {
        if (buildings == null || _buttonContainer == null || _buttonPrefab == null) return;

        // 기존 버튼 제거
        foreach (Transform child in _buttonContainer)
            Destroy(child.gameObject);

        foreach (var data in buildings)
        {
            if (data == null) continue;

            GameObject btnGo = Instantiate(_buttonPrefab, _buttonContainer);

            // 텍스트 설정
            var label = btnGo.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = data.buildingName;

            // 아이콘 설정
            var img = btnGo.GetComponent<Image>();
            if (img != null && data.icon != null) img.sprite = data.icon;

            // 클릭 리스너
            var btn = btnGo.GetComponent<Button>();
            if (btn != null)
            {
                int capturedId = data.id;
                btn.onClick.AddListener(() =>
                {
                    _onBuildingSelected?.Invoke(capturedId);
                    SetVisible(false);
                });
            }
        }
    }
}
