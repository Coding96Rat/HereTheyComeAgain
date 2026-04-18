using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using FishNet;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Network")]
    public ushort networkTickRate = 60;

    private void Awake()
    {
        // 멀티플레이에서 씬 재진입 시 중복 생성 방지
        // FishNet은 NetworkBehaviour가 아닌 이 오브젝트를 관리하지 않으므로 클라이언트별 독립 유지
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        ApplyDisplaySettings();
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void Start()
    {
        // FishNet TickRate — 클라이언트/서버 각각 독립 설정이므로 모든 클라이언트에서 실행
        // 기본값 30 → 60으로 올려 다른 플레이어 위치 갱신 주기를 렌더 프레임에 맞춤
        if (InstanceFinder.NetworkManager != null)
            InstanceFinder.NetworkManager.TimeManager.SetTickRate(networkTickRate);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
    }

    // 플레이어 모니터 주사율을 읽어 FrameRate를 자동 설정
    // VSync OFF 상태에서 무제한 FPS 대신 주사율에 맞춰 찢김 빈도를 최소화
    private void ApplyDisplaySettings()
    {
        QualitySettings.vSyncCount = 0;

        int refreshRate = Mathf.RoundToInt((float)Screen.currentResolution.refreshRateRatio.value);
        if (refreshRate <= 0) refreshRate = 60;

        Application.targetFrameRate = refreshRate;
    }

    // 게임 옵션 UI에서 호출 — 플레이어가 직접 FPS 상한을 조정할 수 있음
    public void SetTargetFrameRate(int fps)
    {
        Application.targetFrameRate = fps;
    }

}
