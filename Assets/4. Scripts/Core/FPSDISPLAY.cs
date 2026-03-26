using TMPro;
using UnityEngine;

public class FPSDISPLAY : MonoBehaviour
{
    public TextMeshProUGUI fpsText;

    // 0.5초 간격으로 평균 FPS 표시 — 순간값보다 안정적이고 매 프레임 문자열 할당 제거
    private const float UPDATE_INTERVAL = 0.5f;
    private int   _frameCount  = 0;
    private float _elapsedTime = 0f;
    private bool  _visible     = false;

    private void Awake()
    {
        // 씬 전환(LobbyScene → StageScene) 후에도 유지
        DontDestroyOnLoad(gameObject);

        if (fpsText != null)
            fpsText.gameObject.SetActive(false);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F11))
        {
            _visible = !_visible;
            if (fpsText != null)
                fpsText.gameObject.SetActive(_visible);
        }

        if (!_visible) return;

        _frameCount++;
        _elapsedTime += Time.unscaledDeltaTime;

        if (_elapsedTime >= UPDATE_INTERVAL)
        {
            float fps = _frameCount / _elapsedTime;
            // TMP SetText(format, arg) — 내부 StringBuilder 재사용으로 GC Alloc 없음
            fpsText.SetText("FPS: {0}", (int)fps);
            _frameCount  = 0;
            _elapsedTime = 0f;
        }
    }
}
