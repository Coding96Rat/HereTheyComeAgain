using FishNet.Object;
using UnityEngine;
using Unity.Cinemachine;

public class PlayerController : NetworkBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 5f;
    public float sprintSpeed = 10f;
    private float _currentSpeed;
    public float lookSpeed = 2f;
    public float gravity = -9.81f; // 추가: 중력 값

    [Header("References")]
    [Tooltip("유니티 내장 캐릭터 컨트롤러")]
    public CharacterController characterController; // 추가: CharacterController 연결용

    [Tooltip("상하로 고개를 까딱일 때 기준이 되는 목뼈(빈 오브젝트)")]
    public Transform cameraTarget;

    [Tooltip("내 화면에서 숨길 내 캐릭터의 렌더러(Mesh)들을 여기에 모두 넣습니다.")]
    public GameObject[] visualMeshes;

    [Tooltip("Unity 6 전용 시네마신 카메라")]
    public CinemachineCamera cinemachineCamera;

    private float rotationX = 0f;
    private Vector3 velocity; // 추가: 중력 적용을 위한 수직 속도 보관용

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();

        if (base.Owner.IsLocalClient)
        {
            cinemachineCamera.gameObject.SetActive(true);
            cinemachineCamera.Priority = 10;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            int invisibleLayer = LayerMask.NameToLayer("LocalPlayerBody");
            foreach (GameObject mesh in visualMeshes)
            {
                if (mesh != null)
                {
                    mesh.layer = invisibleLayer;
                }
            }
        }
        else
        {
            cinemachineCamera.gameObject.SetActive(false);
        }
    }

    // 플레이어 이동/조작 스크립트에 작성 (기존 OnStartClient 쪽 내용은 지워주세요!)
    public override void OnStartServer()
    {
        base.OnStartServer();

        // 서버(방장)가 직접 스포너를 찾아서, 방금 스폰된 이 캐릭터를 꽂아 넣습니다.
        WaveSpawner spawner = FindFirstObjectByType<WaveSpawner>();

        if (spawner != null)
        {
            spawner.RegisterPlayer(transform);
        }
    }

    // (선택) 플레이어가 게임을 끄거나 나갔을 때 리스트에서 빼주는 기능
    public override void OnStopServer()
    {
        base.OnStopServer();
        WaveSpawner spawner = FindFirstObjectByType<WaveSpawner>();
        if (spawner != null)
        {
            spawner.UnregisterPlayer(transform);
        }
    }

    void Update()
    {
        if (!IsOwner) return;

        HandleMovement();
        HandleLook();
    }

    private void HandleMovement()
    {
        // 1. 수평 이동 (WASD)
        float moveX = Input.GetAxis("Horizontal");
        float moveZ = Input.GetAxis("Vertical");

        Vector3 move = transform.right * moveX + transform.forward * moveZ;

        _currentSpeed = Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : moveSpeed;

        // Transform 대신 CharacterController의 Move 함수 사용
        characterController.Move(move * _currentSpeed * Time.deltaTime);

        // 2. 수직 이동 (중력)
        // 캐릭터가 땅에 닿아있고, 아래로 떨어지는 중이라면 수직 속도를 살짝만 유지하여 바닥에 밀착시킵니다.
        if (characterController.isGrounded && velocity.y < 0)
        {
            velocity.y = -2f;
        }

        // 매 프레임 중력을 더해주고 적용합니다.
        velocity.y += gravity * Time.deltaTime;
        characterController.Move(velocity * Time.deltaTime);
    }

    private void HandleLook()
    {
        float mouseX = Input.GetAxis("Mouse X") * lookSpeed;
        float mouseY = Input.GetAxis("Mouse Y") * lookSpeed;

        rotationX -= mouseY;
        rotationX = Mathf.Clamp(rotationX, -90f, 90f);
        cameraTarget.localRotation = Quaternion.Euler(rotationX, 0f, 0f);

        transform.rotation *= Quaternion.Euler(0f, mouseX, 0f);
    }
}