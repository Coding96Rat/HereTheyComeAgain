using UnityEngine;

/// <summary>
/// CameraPoint(빈 GO)를 XZ 이동시키는 컨트롤러.
/// Cinemachine이 이 오브젝트를 Follow하며 고정 Offset+회전으로 아이소메트릭 뷰를 구성한다.
/// _cameraYaw는 CinemachineCamera의 Y 회전값과 반드시 일치시킬 것.
/// </summary>
public class RTSCameraController : MonoBehaviour
{
    [Header("Pan Settings")]
    [SerializeField] private float _panSpeed = 20f;
    [SerializeField] private float _edgeScrollThreshold = 10f;
    [SerializeField] private bool  _enableEdgeScroll = true;

    [Header("Isometric Yaw — CinemachineCamera Y 회전값과 일치")]
    [SerializeField] private float _cameraYaw = 45f;

    [Header("Map Bounds (XZ)")]
    [SerializeField] private float _minX = -100f;
    [SerializeField] private float _maxX =  100f;
    [SerializeField] private float _minZ = -100f;
    [SerializeField] private float _maxZ =  100f;

    private Vector3 _panInput;
    private Vector3 _right;
    private Vector3 _forward;

    private void Start()
    {
        RecalcDirections();
        Cursor.lockState = CursorLockMode.Confined;
        Cursor.visible = true;
    }

    private void Update()
    {
        GatherPanInput();
    }

    private void LateUpdate()
    {
        ApplyPan();
    }

    private void RecalcDirections()
    {
        Quaternion yawRot = Quaternion.Euler(0f, _cameraYaw, 0f);
        _right   = yawRot * Vector3.right;
        _forward = yawRot * Vector3.forward;
    }

    private void GatherPanInput()
    {
        _panInput = Vector3.zero;

        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))    _panInput.z += 1f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))  _panInput.z -= 1f;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))  _panInput.x -= 1f;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) _panInput.x += 1f;

        if (_enableEdgeScroll)
        {
            Vector3 mp = Input.mousePosition;
            if (mp.x < _edgeScrollThreshold)                     _panInput.x -= 1f;
            if (mp.x > Screen.width  - _edgeScrollThreshold)     _panInput.x += 1f;
            if (mp.y < _edgeScrollThreshold)                     _panInput.z -= 1f;
            if (mp.y > Screen.height - _edgeScrollThreshold)     _panInput.z += 1f;
        }

        if (_panInput.sqrMagnitude > 1f) _panInput = _panInput.normalized;
    }

    private void ApplyPan()
    {
        if (_panInput.sqrMagnitude < 0.001f) return;

        Vector3 delta = (_right * _panInput.x + _forward * _panInput.z) * _panSpeed * Time.deltaTime;
        Vector3 pos   = transform.position + delta;
        pos.x = Mathf.Clamp(pos.x, _minX, _maxX);
        pos.z = Mathf.Clamp(pos.z, _minZ, _maxZ);
        transform.position = pos;
    }

    public void SetMapBounds(float minX, float maxX, float minZ, float maxZ)
    {
        _minX = minX; _maxX = maxX;
        _minZ = minZ; _maxZ = maxZ;
    }
}
