using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using FishNet.Object.Synchronizing;

/// <summary>
/// 로비 UI 전담 (순수 MonoBehaviour).
/// LobbyTerminal(NetworkBehaviour)의 PlayerNames SyncList를 구독해 Dropdown을 갱신하고,
/// Start 버튼 클릭을 LobbyTerminal.InteractStartGame()에 위임한다.
/// </summary>
public class LobbyUIManager : MonoBehaviour
{
    [Header("Player List (Dropdown)")]
    [SerializeField] private TMP_Dropdown _playerDropdown;

    [Header("Start Button")]
    [SerializeField] private Button _startButton;
    [SerializeField] private TextMeshProUGUI _startButtonText;

    private LobbyTerminal _terminal;

    // ─── 생명주기 ─────────────────────────────────────────────────────────────

    private IEnumerator Start()
    {
        // LobbyTerminal이 Awake에서 Instance를 설정할 때까지 대기
        while (LobbyTerminal.Instance == null)
            yield return null;

        _terminal = LobbyTerminal.Instance;
        _terminal.PlayerNames.OnChange += OnNamesChanged;

        if (_startButton != null)
            _startButton.onClick.AddListener(OnStartClicked);

        // 기본 상태: 네트워크 초기화 전이므로 버튼 비활성, 텍스트 대기 중
        // 실제 갱신은 OnNamesChanged(OnStartNetwork 이후 발화)에서 처리
        if (_startButton != null)    _startButton.interactable = false;
        if (_startButtonText != null) _startButtonText.text = "대기 중...";
    }

    private void OnDestroy()
    {
        if (_terminal != null)
            _terminal.PlayerNames.OnChange -= OnNamesChanged;

        if (_startButton != null)
            _startButton.onClick.RemoveListener(OnStartClicked);
    }

    // ─── Start 버튼 ──────────────────────────────────────────────────────────

    private void OnStartClicked()
    {
        _terminal?.InteractStartGame();
    }

    // ─── SyncList 변경 → UI 갱신 ──────────────────────────────────────────────

    private void OnNamesChanged(SyncListOperation op, int index, string prev, string next, bool asServer)
    {
        RefreshAll();
    }

    private void RefreshAll()
    {
        if (_terminal == null) return;

        // Dropdown 갱신
        if (_playerDropdown != null)
        {
            _playerDropdown.ClearOptions();
            _playerDropdown.AddOptions(new List<string>(_terminal.PlayerNames));
        }

        // Start 버튼 상태 (네트워크 초기화 후 IsHost가 확정됨)
        bool isHost = _terminal.IsHost;
        if (_startButton != null)    _startButton.interactable = isHost;
        if (_startButtonText != null) _startButtonText.text = isHost ? "Start" : "대기 중...";
    }
}
