using System.Collections.Generic;
using UnityEngine;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Connection;
using FishNet.Managing.Scened;
using FishNet.Transporting;
using FishNet;

/// <summary>
/// 로비 네트워크 총괄.
/// - 플레이어 목록을 SyncList로 전 클라이언트에 자동 동기화.
/// - 게임 시작(씬 전환)을 담당.
/// LobbyUIManager(MonoBehaviour)가 이 클래스를 참조해 UI를 표시한다.
/// </summary>
public class LobbyTerminal : NetworkBehaviour
{
    public static LobbyTerminal Instance { get; private set; }

    // SyncList: 서버가 쓰고, 늦게 접속한 클라이언트를 포함한 모든 클라이언트에 자동 동기화
    public readonly SyncList<string> PlayerNames = new SyncList<string>();

    public bool IsHost => IsServerInitialized;

    private readonly Dictionary<int, string> _connToName = new();
    private bool _isStarting;

    // ─── 생명주기 ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();

        if (IsServerInitialized)
        {
            NetworkManager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;

            // Host 자신을 목록에 추가
            if (NetworkManager.ClientManager.Started)
                AddPlayer(NetworkManager.ClientManager.Connection.ClientId);
        }
    }

    public override void OnStopNetwork()
    {
        base.OnStopNetwork();

        if (IsServerInitialized && NetworkManager != null)
            NetworkManager.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
    }

    // ─── 게임 시작 ────────────────────────────────────────────────────────────

    /// <summary>UI 버튼 또는 3D StartButtonInteract 양쪽에서 호출 가능.</summary>
    public void InteractStartGame()
    {
        // IsServerInitialized가 이미 서버 전용 진입 방어선 — ServerRpc 불필요
        if (!IsServerInitialized || _isStarting) return;
        _isStarting = true;
        Debug.Log("[LobbyTerminal] 게임 시작 → StageScene 로드");
        LoadStageScene();
    }

    private void LoadStageScene()
    {
        SceneLoadData sld = new SceneLoadData("StageScene");
        sld.ReplaceScenes = ReplaceOption.All;
        Debug.Log($"[LobbyTerminal] LoadGlobalScenes 호출, SceneManager null={NetworkManager?.SceneManager == null}");
        NetworkManager.SceneManager.LoadGlobalScenes(sld);
    }

    // ─── 플레이어 목록 관리 (서버 전용) ──────────────────────────────────────

    private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
    {
        if (args.ConnectionState == RemoteConnectionState.Started)
            AddPlayer(conn.ClientId);
        else
            RemovePlayer(conn.ClientId);
    }

    private void AddPlayer(int clientId)
    {
        string name = $"Player {clientId + 1}";
        _connToName[clientId] = name;
        PlayerNames.Add(name);
    }

    private void RemovePlayer(int clientId)
    {
        if (_connToName.TryGetValue(clientId, out string name))
        {
            _connToName.Remove(clientId);
            PlayerNames.Remove(name);
        }
    }
}
