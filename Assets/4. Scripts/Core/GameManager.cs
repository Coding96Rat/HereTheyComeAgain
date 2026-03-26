using UnityEngine;
using FishNet;

public class GameManager : MonoBehaviour
{
    [Header("Network")]
    public ushort networkTickRate = 60;


    private void Start()
    {
        // FishNet TickRate — 클라이언트/서버 각각 독립 설정이므로 모든 클라이언트에서 실행
        // 기본값 30 → 60으로 올려 다른 플레이어 위치 갱신 주기를 렌더 프레임에 맞춤
        if (InstanceFinder.NetworkManager != null)
            InstanceFinder.NetworkManager.TimeManager.SetTickRate(networkTickRate);
    }
}
