using UnityEngine;
using TMPro;

public class HUDController : MonoBehaviour
{
    [Header("UI Elements")]
    public TextMeshProUGUI interactPromptText;

    [Header("Settings")]
    public float fadeSpeed = 8f; // 페이드 전환 속도

    private PlayerInteractor _targetPlayer;

    [SerializeField]
    private CanvasGroup _interactPromptCanvaGroup;

    private void Awake()
    {
        _interactPromptCanvaGroup.alpha = 0f; // 시작할 때 투명하게 숨김
    }

    // 💡 PlayerInteractor가 스폰될 때 이 함수를 불러서 자기 자신을 등록합니다.
    public void SetPlayer(PlayerInteractor player)
    {
        _targetPlayer = player;
    }

    private void Update()
    {
        // 연결된 플레이어가 없으면 작동하지 않음
        if (_targetPlayer == null) return;

        // 💡 플레이어의 Property를 실시간으로 감시!
        if (_targetPlayer.IsInteractDetected)
        {
            // 글씨를 업데이트하고 선명하게(Alpha 1) 페이드 인
            interactPromptText.text = $"[E] {_targetPlayer.CurrentInteractPrompt}";
            FadeTo(1f);
        }
        else
        {
            // 감지된 게 없으면 투명하게(Alpha 0) 페이드 아웃
            FadeTo(0f);
        }
    }

    // 부드러운 투명도 전환 함수 (Lerp 사용)
    private void FadeTo(float targetAlpha)
    {
        // 현재 알파값과 목표 알파값이 다를 때만 부드럽게 이동시킴
        if (!Mathf.Approximately(_interactPromptCanvaGroup.alpha, targetAlpha))
        {
            _interactPromptCanvaGroup.alpha = Mathf.Lerp(_interactPromptCanvaGroup.alpha, targetAlpha, Time.deltaTime * fadeSpeed);

            // 거의 다다르면 딱 맞춰서 연산 종료
            if (Mathf.Abs(_interactPromptCanvaGroup.alpha - targetAlpha) < 0.01f)
            {
                _interactPromptCanvaGroup.alpha = targetAlpha;
            }
        }
    }
}