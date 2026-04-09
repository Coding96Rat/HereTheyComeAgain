using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// StageScene 전용 건물 선택 UI.
///
/// B 키로 빌딩 모드 진입 → Tab 키로 이 패널 토글.
/// 패널 열릴 때 커서 해제, 닫힐 때 커서 잠금.
/// BuildingSystem의 프리팹 목록을 기반으로 버튼을 동적 생성.
/// 아이콘은 AssetReferenceSprite를 통해 on-demand 로드하며, 닫힐 때 핸들 해제.
/// </summary>
public class BuildingListUI : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject  _panel;
    [SerializeField] private Transform   _buttonContainer;
    [SerializeField] private GameObject  _buttonPrefab;   // Button + TextMeshProUGUI 포함

    private CanvasGroup  _canvasGroup;
    private Action<int>  _onBuildingSelected;

    // Addressables 아이콘 핸들 — PopulateList 호출 시 이전 핸들 해제 후 재구성
    private readonly List<AsyncOperationHandle<Sprite>> _iconHandles = new List<AsyncOperationHandle<Sprite>>();

    // ─── 초기화 ───────────────────────────────────────────────────────────────

    private void Awake()
    {
        _canvasGroup = _panel.GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
            _canvasGroup = _panel.AddComponent<CanvasGroup>();

        SetVisible(false);
    }

    private void OnDestroy()
    {
        ReleaseIconHandles();
    }

    /// <summary>
    /// PlayerInteractor에서 빌딩 모드 진입 시 한 번 호출.
    /// IsRegistryReady == true 이후에 호출해야 AllBuildings가 채워진다.
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

    private void PopulateList(IReadOnlyCollection<BuildingDataSO> buildings)
    {
        if (buildings == null || _buttonContainer == null || _buttonPrefab == null) return;

        // 기존 버튼 및 아이콘 핸들 제거
        foreach (Transform child in _buttonContainer)
            Destroy(child.gameObject);
        ReleaseIconHandles();

        foreach (var data in buildings)
        {
            if (data == null) continue;

            GameObject btnGo = Instantiate(_buttonPrefab, _buttonContainer);

            // 텍스트 설정
            var label = btnGo.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = data.buildingName;

            // 아이콘: Addressables 비동기 로드
            var img = btnGo.GetComponent<Image>();
            if (img != null && data.iconRef != null && data.iconRef.RuntimeKeyIsValid())
                StartCoroutine(LoadIconCoroutine(data.iconRef, img));

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

    // ─── 아이콘 로드 / 해제 ──────────────────────────────────────────────────

    private IEnumerator LoadIconCoroutine(AssetReferenceSprite iconRef, Image target)
    {
        AsyncOperationHandle<Sprite> handle = iconRef.LoadAssetAsync();
        _iconHandles.Add(handle);

        yield return handle;

        if (target != null && handle.Status == AsyncOperationStatus.Succeeded)
            target.sprite = handle.Result;
    }

    private void ReleaseIconHandles()
    {
        foreach (var h in _iconHandles)
            if (h.IsValid()) Addressables.Release(h);
        _iconHandles.Clear();
    }
}
