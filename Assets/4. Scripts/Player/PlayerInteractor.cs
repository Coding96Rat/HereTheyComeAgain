using UnityEngine;
using FishNet.Object;

public class PlayerInteractor : NetworkBehaviour
{
    [Header("Raycast Settings")]
    public Transform cameraTransform;

    [Header("Interaction (E 키)")]
    public float interactDistance = 3f;
    public LayerMask interactLayer;

    [Header("Shooting (좌클릭)")]
    public bool isArmed = false;
    public float currentWeaponRange = 50f;
    public LayerMask shootableLayer;

    public bool IsInteractDetected { get; private set; }
    public string CurrentInteractPrompt { get; private set; }

    private HUDController hudController;

    // 씬 전환 후 HUD 재탐색용 — FindFirstObjectByType을 매 프레임 호출하지 않도록 쿨다운 관리
    private float _hudRetryTimer = 0f;
    private const float HUD_RETRY_INTERVAL = 1f;

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();

        if (base.Owner.IsLocalClient)
        {
            hudController = Object.FindFirstObjectByType<HUDController>();

            if (hudController != null)
            {
                hudController.SetPlayer(this);
            }
            else
            {
                Debug.LogWarning("씬에 HUDController가 없습니다!");
            }
        }
    }

    void Update()
    {
        if (!IsOwner) return;

        CheckInteractHover();

        if (isArmed && Input.GetMouseButtonDown(0))
        {
            TryShoot();
        }
    }

    private void CheckInteractHover()
    {

        // 씬 전환 후 HUD가 파괴된 경우 재탐색 — 단, 1초 간격으로만 시도 (매 프레임 탐색 방지)
        if (hudController == null)
        {
            _hudRetryTimer += Time.deltaTime;
            if (_hudRetryTimer < HUD_RETRY_INTERVAL) return;
            _hudRetryTimer = 0f;

            hudController = Object.FindFirstObjectByType<HUDController>();
            if (hudController != null) hudController.SetPlayer(this);
            else return;
        }

        Ray ray = new Ray(cameraTransform.position, cameraTransform.forward);

        // 💡 인게임 디버그용 레이저 그리기 (Game 뷰에서 Gizmos를 켜야 보입니다)
        //Debug.DrawRay(ray.origin, ray.direction * interactDistance, Color.green);

        if (Physics.Raycast(ray, out RaycastHit hit, interactDistance, interactLayer))
        {
            IInteractable interactableObj = hit.collider.GetComponent<IInteractable>();

            if (interactableObj != null)
            {
                IsInteractDetected = true;
                CurrentInteractPrompt = interactableObj.GetInteractPrompt();

                if (Input.GetKeyDown(KeyCode.E))
                {
                    interactableObj.OnInteract();
                }
                return;
            }
        }

        IsInteractDetected = false;
    }

    private void TryShoot()
    {
        Ray ray = new Ray(cameraTransform.position, cameraTransform.forward);

        // 💡 사격용 디버그 레이저 그리기 (빨간색)
        Debug.DrawRay(ray.origin, ray.direction * currentWeaponRange, Color.red, 2f);

        if (Physics.Raycast(ray, out RaycastHit hit, currentWeaponRange, shootableLayer))
        {
            Debug.Log($"[{hit.collider.name}] 명중!");
        }
    }

    // 💡 에디터 Scene 뷰에서 항상 레이캐스트 길이를 확인할 수 있게 해주는 기능
    private void OnDrawGizmos()
    {
        if (cameraTransform == null) return;

        // 상호작용 사거리 (초록색 구체와 선)
        Gizmos.color = Color.green;
        Gizmos.DrawRay(cameraTransform.position, cameraTransform.forward * interactDistance);
        Gizmos.DrawWireSphere(cameraTransform.position + cameraTransform.forward * interactDistance, 0.1f);
    }
}