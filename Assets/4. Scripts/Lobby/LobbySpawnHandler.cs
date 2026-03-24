using FishNet.Component.Spawning;
using UnityEngine;

public class LobbySpawnHandler : MonoBehaviour
{
    [Header("로비 스폰 위치들")]
    public Transform[] lobbySpawnPoints;

    private void Awake()
    {
        // 1. 씬이 켜지자마자 NetworkManager에 있는 PlayerSpawner를 찾습니다.
        PlayerSpawner playerSpawner = FindFirstObjectByType<PlayerSpawner>();

        if (playerSpawner != null && lobbySpawnPoints != null && lobbySpawnPoints.Length > 0)
        {
            // 2. "유저가 접속하면 여기(로비 스폰 포인트)에 스폰시켜라!" 라고 좌표를 덮어씌웁니다.
            playerSpawner.Spawns = lobbySpawnPoints;
            Debug.Log("[Lobby] 로비 스폰 포인트 덮어쓰기 완료!");
        }
        else
        {
            Debug.LogWarning("[Lobby] PlayerSpawner를 찾을 수 없거나 스폰 포인트가 비어있습니다.");
        }
    }
}
