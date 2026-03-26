using UnityEngine;
using FishNet.Object;
using FishNet.Managing.Scened;

public class PlayerInteractor : NetworkBehaviour
{
    [Header("Raycast Settings")]
    public Transform cameraTransform;

    [Header("Interaction (E 키)")]
    public float interactDistance = 3f;
    public LayerMask interactLayer;

    [Header("Shooting (좌클릭)")]
    public bool isArmed = false;
    public float currentWeaponDamage = 25f;
    public float headshotMultiplier = 3f;
    public float fireRate = 10f;        // 초당 발사 횟수
    public LayerMask shootableLayer;
    public LayerMask blockingLayer;

    private float _nextFireTime = 0f;

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

        if (base.Owner.IsLocalClient)
        {
            // 이미 StageScene에 있는 상태로 네트워크가 시작된 경우 즉시 무장
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "StageScene")
            {
                isArmed = true;
            }
            else
            {
                base.SceneManager.OnLoadEnd += OnSceneLoadEnd;
            }
        }
    }

    public override void OnStopNetwork()
    {
        base.OnStopNetwork();

        // OnStopNetwork 시점엔 소유권 정보가 이미 해제될 수 있으므로 IsOwner 대신 무조건 해제
        if (base.SceneManager != null)
            base.SceneManager.OnLoadEnd -= OnSceneLoadEnd;
    }

    private void OnSceneLoadEnd(SceneLoadEndEventArgs args)
    {
        foreach (UnityEngine.SceneManagement.Scene scene in args.LoadedScenes)
        {
            if (scene.name == "StageScene")
            {
                isArmed = true;
                base.SceneManager.OnLoadEnd -= OnSceneLoadEnd;
                return;
            }
        }
    }

    void Update()
    {
        if (!IsOwner) return;

        CheckInteractHover();

        if (isArmed && Input.GetMouseButton(0) && Time.time >= _nextFireTime)
        {
            _nextFireTime = Time.time + 1f / fireRate;
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

        // 지형/구조물 차단 거리 계산 (Infinity 사거리이므로 관통 방지를 위해 먼저 검사)
        float blockDist = Mathf.Infinity;
        if (Physics.Raycast(ray, out RaycastHit blockHit, Mathf.Infinity, blockingLayer))
            blockDist = blockHit.distance;

        // 💡 사격용 디버그 레이저 그리기 (빨간색) — Infinity 대신 500f 사용 (DrawRay에 Infinity 불가)
        Debug.DrawRay(ray.origin, ray.direction * (float.IsInfinity(blockDist) ? 500f : blockDist), Color.red, 2f);

        // 환경/벽 등 일반 Physics 오브젝트 명중 로그
        if (Physics.Raycast(ray, out RaycastHit hit, blockDist, shootableLayer))
            Debug.Log($"[{hit.collider.name}] 명중!");

        // 적은 CapsuleCollider가 없으므로 Ray-Capsule 수학 검사로 처리
        // blockDist로 캡을 걸어 지형/구조물 뒤 적은 판정 제외
        Enemy closestEnemy = null;
        float closestDist = blockDist;
        bool isHeadshot = false;
        foreach (EnemyMother mother in EnemyMother.AllMothers)
        {
            Enemy candidate = mother.GetEnemyHitByRay(ray, closestDist, out float t, out bool headshot);
            if (candidate != null && t < closestDist)
            {
                closestDist = t;
                closestEnemy = candidate;
                isHeadshot = headshot;
            }
        }

        if (closestEnemy != null)
        {
            float damage = isHeadshot ? currentWeaponDamage * headshotMultiplier : currentWeaponDamage;
            Debug.Log(isHeadshot ? $"[HEADSHOT] {damage} 데미지" : $"[Hit] {damage} 데미지");
            closestEnemy.TakeDamage(damage);
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