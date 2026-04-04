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

    [Header("Building Mode (B 키)")]
    [Tooltip("빌딩 레이캐스트가 맞을 레이어 (Terrain/Ground)")]
    public LayerMask buildGroundLayer;

    private float _nextFireTime = 0f;

    public bool IsInteractDetected { get; private set; }
    public string CurrentInteractPrompt { get; private set; }

    // ─── Building Mode ────────────────────────────────────────────────────────
    private bool           _isBuildMode;
    private BuildingHelper _buildingHelper;
    private BuildingListUI _cachedBuildingListUI;
    private bool           _helperInitialized;
    private bool           _listUIInitialized;
    private int            _selectedBuildingId = -1;
    // ──────────────────────────────────────────────────────────────────────────

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
            // 이미 StageScene에 있는 상태로 네트워크가 시작된 경우 즉시 초기화
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "StageScene")
            {
                isArmed = true;
                InitializeStageRefs();
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

        // 빌딩 모드 정리
        if (_isBuildMode)
        {
            CancelSelection();
            _isBuildMode = false;
        }
    }

    private void OnSceneLoadEnd(SceneLoadEndEventArgs args)
    {
        foreach (UnityEngine.SceneManagement.Scene scene in args.LoadedScenes)
        {
            if (scene.name == "StageScene")
            {
                isArmed = true;
                InitializeStageRefs();
                base.SceneManager.OnLoadEnd -= OnSceneLoadEnd;
                return;
            }
        }
    }

    /// <summary>
    /// StageScene 로드 시 딱 한 번만 실행 — 무거운 Find 계열 호출을 여기에 집중.
    /// BuildingSystem.Instance, GridSystem.Instance는 싱글턴이라 Find 없이 접근 가능.
    /// </summary>
    private void InitializeStageRefs()
    {
        // ── 1. BuildingHelper: GridSystem이 준비되면 즉시 생성 (1회)
        if (!_helperInitialized && GridSystem.Instance != null)
        {
            _buildingHelper    = new BuildingHelper(GridSystem.Instance);
            _helperInitialized = true;
        }

        // ── 2. BuildingListUI: 씬에서 1회 Find (이미 찾았으면 스킵)
        if (_cachedBuildingListUI == null)
            _cachedBuildingListUI = Object.FindFirstObjectByType<BuildingListUI>();

        // ── 3. 리스트 UI 초기화: BuildingSystem이 준비됐을 때만 실행 (1회)
        //       BuildingSystem.Instance가 null이면 이 블록을 건너뛰고
        //       다음 B 키 진입 시 다시 시도한다 (_listUIInitialized = false 유지)
        if (!_listUIInitialized && _cachedBuildingListUI != null && BuildingSystem.Instance != null)
        {
            _cachedBuildingListUI.Initialize(BuildingSystem.Instance, OnBuildingSelected);
            _listUIInitialized = true;
        }
    }

    void Update()
    {
        if (!IsOwner) return;

        // ── 빌딩 모드 토글 (StageScene에서만 유효) ──
        if (Input.GetKeyDown(KeyCode.B) && isArmed)
            ToggleBuildMode();

        if (_isBuildMode)
        {
            HandleBuildMode();
            return;   // 빌딩 모드 중엔 일반 인터랙션/사격 스킵
        }

        CheckInteractHover();

        if (isArmed && Input.GetMouseButton(0) && Time.time >= _nextFireTime)
        {
            _nextFireTime = Time.time + 1f / fireRate;
            TryShoot();
        }
    }

    // ─── 빌딩 모드 진입/종료 ─────────────────────────────────────────────────

    private void ToggleBuildMode()
    {
        _isBuildMode = !_isBuildMode;

        if (_isBuildMode)
        {
            EnterBuildMode();
        }
        else
        {
            ExitBuildMode();
        }
    }

    private void EnterBuildMode()
    {
        // 미완료 항목이 있으면 재시도 (BuildingSystem.Instance 타이밍 문제 대비)
        InitializeStageRefs();
        Debug.Log($"[BuildMode] 진입 — helper={_helperInitialized}, listUI={_listUIInitialized}, B:종료 Tab:목록");
    }

    private void ExitBuildMode()
    {
        CancelSelection();                          // 선택 중이면 먼저 정리
        _cachedBuildingListUI?.SetVisible(false);   // 목록 UI 닫기

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;

        Debug.Log("[BuildMode] 종료");
    }

    // ─── 빌딩 모드 업데이트 ──────────────────────────────────────────────────

    private void HandleBuildMode()
    {
        // ESC: 건물 선택만 취소, 빌딩 모드는 유지 (Idle 상태로 복귀)
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            CancelSelection();
            return;
        }

        // Tab: 건물 목록 토글
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            _cachedBuildingListUI?.ToggleVisible();
            return;
        }

        // 목록이 열려 있으면 미리보기/설치 로직 스킵
        bool listOpen = _cachedBuildingListUI != null && _cachedBuildingListUI.IsVisible;
        if (listOpen) return;

        // 건물이 선택되지 않았으면 미리보기 없음 (Idle)
        if (_selectedBuildingId < 0 || _buildingHelper == null) return;

        if (buildGroundLayer == 0)
        {
            Debug.LogWarning("[BuildMode] buildGroundLayer가 설정되지 않았습니다! PlayerInteractor 인스펙터에서 Terrain 레이어를 지정하세요.");
            return;
        }

        var preview = BuildingSystem.Instance?.Preview;
        if (preview == null) return;

        // 레이캐스트 → 그리드 스냅 (수학 연산, O(1))
        Ray  ray = new Ray(cameraTransform.position, cameraTransform.forward);
        bool hit = _buildingHelper.GetGridFromRay(ray, buildGroundLayer,
            out Vector3 snappedPos, out int gridX, out int gridZ);

        if (!hit) return;

        BuildingDataSO data = BuildingSystem.Instance.GetBuildingData(_selectedBuildingId);
        if (data == null) return;

        PlacementResult result = _buildingHelper.CheckPlacement(data, gridX, gridZ);
        preview.UpdatePreview(snappedPos, gridX, gridZ, result);

        // 좌클릭: 설치 확정
        if (Input.GetMouseButtonDown(0))
            TryConfirmPlacement(gridX, gridZ, result);
    }

    private void TryConfirmPlacement(int gridX, int gridZ, PlacementResult result)
    {
        if (result == PlacementResult.Blocked       ||
            result == PlacementResult.TerrainTooHigh ||
            result == PlacementResult.OutOfBounds)
            return;

        BuildingSystem.Instance?.ServerPlaceBuilding(_selectedBuildingId, gridX, gridZ);
        CancelSelection();  // 설치 후 선택 초기화 → Idle 상태
    }

    // ─── 건물 선택 / 취소 ────────────────────────────────────────────────────

    private void OnBuildingSelected(int buildingId)
    {
        _selectedBuildingId = buildingId;

        BuildingDataSO data = BuildingSystem.Instance?.GetBuildingData(buildingId);
        BuildingSystem.Instance?.Preview?.SetBuilding(data);
    }

    /// <summary>
    /// 건물 선택을 취소하고 빌딩 모드 Idle 상태로 복귀.
    /// 프리뷰 비주얼/지형 미리보기 복원. 빌딩 모드 자체는 유지.
    /// </summary>
    private void CancelSelection()
    {
        if (_selectedBuildingId < 0) return;   // 이미 Idle
        BuildingSystem.Instance?.Preview?.ClearBuilding();
        _selectedBuildingId = -1;
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