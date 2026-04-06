using FishNet.Object;
using FishNet.Connection;
using UnityEngine;
using Unity.Cinemachine;
using System.Collections;

public class PlayerController : NetworkBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 5f;
    public float sprintSpeed = 10f;
    private float _currentSpeed = 5f;
    public float lookSpeed = 2f;
    public float gravity = -9.81f;

    [Header("Jump Settings")]
    public float jumpHeight = 1.2f;
    public float terminalVelocity = -30f;

    [Header("Landing HeadBob")]
    public float landingBobAmount = 0.08f;
    public float landingBobDuration = 0.2f;
    public float bobVelocityThreshold = -4f;

    [Header("Custom Ground Check")]
    public LayerMask groundLayer;
    public float groundCheckDistance = 0.15f;
    private bool _isGrounded;
    private bool _wasGrounded;

    [Header("References")]
    public CharacterController characterController;
    public Transform cameraTarget;
    public CinemachineCamera cinemachineCamera;
    public Transform visualRoot;
    public GameObject[] visualMeshes;

    // Landing HeadBob (Math Optimized)
    private bool _isLandingBobbing = false;
    private float _landingBobTimer = 0f;

    private float rotationX = 0f;
    private Vector3 velocity;

    private Vector3 _originalCameraPos;

    // [수정 5] CharacterController 크기 조정을 한 번만 실행하기 위한 플래그
    // 씬 전환 시 OnStartNetwork가 다시 호출돼도 누적 감소가 일어나지 않음
    private bool _controllerInitialized = false;

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();

        _currentSpeed = moveSpeed;

        // 좀비 추적 타겟 명단 등록
        EnemyMother.RegisterTarget(this.transform);

        if (cameraTarget != null)
        {
            _originalCameraPos = cameraTarget.localPosition;
        }

        // [수정 5] 최초 1회만 CharacterController 크기 조정 — 씬 전환마다 누적 감소 방지
        if (!_controllerInitialized && characterController != null && visualRoot != null)
        {
            visualRoot.localPosition -= new Vector3(0, characterController.skinWidth, 0);
            characterController.height -= characterController.skinWidth;
            characterController.center -= new Vector3(0, characterController.skinWidth / 2f, 0);
            _controllerInitialized = true;
        }

        if (base.Owner.IsLocalClient)
        {
            cinemachineCamera.gameObject.SetActive(true);
            cinemachineCamera.Priority = 10;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            int invisibleLayer = LayerMask.NameToLayer("LocalPlayerBody");
            foreach (GameObject mesh in visualMeshes)
            {
                if (mesh != null) mesh.layer = invisibleLayer;
            }
        }
        else
        {
            cinemachineCamera.gameObject.SetActive(false);
        }
    }

    public override void OnStopNetwork()
    {
        base.OnStopNetwork();
        // 플레이어가 죽거나 게임을 나갈 때 타겟 명단에서 제거
        EnemyMother.UnregisterTarget(this.transform);
    }

    void Update()
    {
        if (!IsOwner) return;

        HandleMovement();
    }

    // 캐릭터 물리 이동이 모두 끝난 뒤 카메라를 회전시켜 프레임 페이싱 끊김 방지
    void LateUpdate()
    {
        if (!IsOwner) return;

        HandleLook();
    }

    private void HandleMovement()
    {
        CheckGroundedCustom();

        // 착지 감지 및 헤드밥 트리거
        if (!_wasGrounded && _isGrounded)
        {
            if (velocity.y < bobVelocityThreshold)
            {
                _isLandingBobbing = true;
                _landingBobTimer = 0f;
            }
        }

        HandleMathematicalLandingBob();

        _wasGrounded = _isGrounded;

        // 수평 이동 벡터 계산
        float moveX = Input.GetAxis("Horizontal");
        float moveZ = Input.GetAxis("Vertical");
        Vector3 horizontalMove = transform.right * moveX + transform.forward * moveZ;

        // 대각선 폭주 방지 — 벡터 최대 길이 1로 제한
        horizontalMove = Vector3.ClampMagnitude(horizontalMove, 1f);

        // 공중에서는 속도 변경 금지 — 점프 전 속도 유지
        if (_isGrounded)
        {
            _currentSpeed = Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : moveSpeed;
        }

        // 수직 이동(중력/점프) 계산
        if (_isGrounded && velocity.y < 0)
        {
            velocity.y = -2f;
        }

        if (Input.GetKeyDown(KeyCode.Space) && _isGrounded)
        {
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        velocity.y += gravity * Time.deltaTime;
        if (velocity.y < terminalVelocity) velocity.y = terminalVelocity;

        // 수평 + 수직 벡터를 하나로 합쳐 Move 1회 호출
        Vector3 finalMove = (horizontalMove * _currentSpeed) + (Vector3.up * velocity.y);
        characterController.Move(finalMove * Time.deltaTime);
    }

    private void HandleLook()
    {
        // 빌딩 목록 UI 등으로 커서가 해제된 상태에선 시점 이동 금지
        if (Cursor.lockState != CursorLockMode.Locked) return;

        float mouseX = Input.GetAxis("Mouse X") * lookSpeed;
        float mouseY = Input.GetAxis("Mouse Y") * lookSpeed;

        rotationX -= mouseY;
        rotationX = Mathf.Clamp(rotationX, -90f, 90f);

        cameraTarget.localRotation = Quaternion.Euler(rotationX, 0f, 0f);
        transform.rotation *= Quaternion.Euler(0f, mouseX, 0f);
    }

    // 코루틴 대신 매 프레임 수학적으로 계산하는 HeadBob
    private void HandleMathematicalLandingBob()
    {
        if (!_isLandingBobbing) return;

        _landingBobTimer += Time.deltaTime;
        float t = _landingBobTimer / landingBobDuration;

        if (t >= 1f)
        {
            _isLandingBobbing = false;
            cameraTarget.localPosition = _originalCameraPos;
        }
        else
        {
            // Sin 곡선(0→1→0)으로 부드러운 착지 효과
            float bobOffset = Mathf.Sin(t * Mathf.PI) * landingBobAmount;
            cameraTarget.localPosition = _originalCameraPos - new Vector3(0f, bobOffset, 0f);
        }
    }

    private void CheckGroundedCustom()
    {
        float sphereRadius = characterController.radius * 0.9f;
        Vector3 origin = transform.position + Vector3.up * sphereRadius;
        _isGrounded = Physics.SphereCast(origin, sphereRadius, Vector3.down, out RaycastHit hit, groundCheckDistance, groundLayer, QueryTriggerInteraction.Ignore);
    }

    [TargetRpc]
    public void TargetTeleport(NetworkConnection conn, Vector3 newPosition)
    {
        if (characterController != null) characterController.enabled = false;
        transform.position = newPosition;
        if (characterController != null) characterController.enabled = true;

        velocity = Vector3.zero;

        _isLandingBobbing = false;
        _landingBobTimer = 0f;

        if (cameraTarget != null)
        {
            cameraTarget.localPosition = new Vector3(0f, 1.6f, 0f);
        }
    }
}