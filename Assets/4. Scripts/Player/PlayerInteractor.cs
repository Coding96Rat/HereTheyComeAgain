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

        // 💡 [핵심 추가] 씬이 넘어가서 HUD가 파괴되었다면 새로 배치된 녀석을 다시 찾습니다!
        if (hudController == null)
        {
            hudController = Object.FindFirstObjectByType<HUDController>();
            if (hudController != null) hudController.SetPlayer(this);
            else return; // 여전히 없으면(HUD를 씬에 안 뒀으면) 상호작용 UI 로직 건너뜀
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