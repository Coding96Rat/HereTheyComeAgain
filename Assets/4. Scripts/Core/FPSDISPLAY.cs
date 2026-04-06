using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

public class FPSDISPLAY : MonoBehaviour
{
    public TextMeshProUGUI fpsText;

    private bool _visible = false;

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        if (fpsText != null)
            fpsText.gameObject.SetActive(false);

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void Start()
    {
        RefreshDisplay();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        if (!_visible || fpsText == null) return;

        // 문자열 할당 없이 정수 직접 출력
        fpsText.SetText("Target FPS: {0}", Application.targetFrameRate);
    }

    void Update()
    {
        if (!Input.GetKeyDown(KeyCode.F11)) return;

        _visible = !_visible;
        if (fpsText != null)
            fpsText.gameObject.SetActive(_visible);

        if (_visible)
            RefreshDisplay();
    }
}
