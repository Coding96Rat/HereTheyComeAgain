using UnityEngine;
using FishNet.Object;
using FishNet.Managing.Scened;

public class LobbyTerminal : NetworkBehaviour
{
    private bool _isStarting = false; // 💡 중복 실행(연타) 방지용 자물쇠

    public void InteractStartGame()
    {
        // 서버장이고, 아직 시작 버튼을 누르지 않은 상태일 때만 실행
        if (IsServerInitialized && !_isStarting)
        {
            _isStarting = true;
            Debug.Log("[Lobby] 방장이 미션을 시작합니다! StageScene으로 이동...");
            CmdStartGame();
        }
        else if (!IsServerInitialized)
        {
            Debug.Log("[Lobby] 방장(Host)만 게임을 시작할 수 있습니다.");
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void CmdStartGame()
    {
        SceneLoadData sld = new SceneLoadData("StageScene");
        sld.ReplaceScenes = ReplaceOption.All;
        NetworkManager.SceneManager.LoadGlobalScenes(sld);
    }
}