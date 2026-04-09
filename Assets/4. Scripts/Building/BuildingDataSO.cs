using UnityEngine;
using UnityEngine.AddressableAssets;

[CreateAssetMenu(fileName = "NewBuildingData", menuName = "Building/Building Data")]
public class BuildingDataSO : ScriptableObject
{
    public int    id;
    public string buildingName;
    public GameObject prefab;

    [Tooltip("BuildingListUI에 표시할 아이콘. Addressable 에셋으로 등록 필요.")]
    public AssetReferenceSprite iconRef;

    // ─── Grid Footprint ───────────────────────────────────────────────────────

    [Header("Grid Footprint")]
    [Tooltip("true: prefab 메시 Bounds에서 자동 계산 (권장).\n" +
             "false: sizeX / sizeZ 수동 입력 (특수 케이스 전용).")]
    [SerializeField] private bool _autoSyncFromPrefab = true;

    [Tooltip("GridSystem의 CellSize와 동일하게 설정해야 자동 계산이 정확합니다.")]
    [SerializeField] private int _gridCellSize = 1;

    /// <summary>
    /// 풋프린트 X 칸 수.
    /// _autoSyncFromPrefab = true 일 때는 OnValidate가 자동 갱신하므로 직접 편집 불필요.
    /// </summary>
    public int sizeX = 1;

    /// <summary>
    /// 풋프린트 Z 칸 수.
    /// _autoSyncFromPrefab = true 일 때는 OnValidate가 자동 갱신하므로 직접 편집 불필요.
    /// </summary>
    public int sizeZ = 1;

    // ─── 에디터 전용: 프리팹 변경 시 자동 동기화 ─────────────────────────────
    // OnValidate는 에디터에서만 호출되며, 빌드에는 #if UNITY_EDITOR로 완전 제거.
    // 런타임에는 직렬화된 sizeX/sizeZ를 그대로 읽으므로 비용 0.

#if UNITY_EDITOR
    // SO Inspector에서 우클릭 → "Sync Footprint From Prefab"으로 수동 실행 가능
    [ContextMenu("Sync Footprint From Prefab")]
    private void SyncFromPrefabManual() => SyncFootprint();

    // prefab 필드 변경, 어떤 값이든 Inspector에서 수정 시 자동 실행
    private void OnValidate()
    {
        if (!_autoSyncFromPrefab || prefab == null) return;
        SyncFootprint();
    }

    private void SyncFootprint()
    {
        if (prefab == null) return;

        // BuildingHelper.GetPrefabLocalBounds와 동일한 방식:
        // MeshFilter 로컬 바운드를 localToWorldMatrix로 변환 후 Encapsulate.
        // 프리팹 에셋의 transform.localToWorldMatrix는 에디터에서 정상 동작.
        MeshFilter[] filters = prefab.GetComponentsInChildren<MeshFilter>(true);
        bool         initialized = false;
        Bounds       combined    = default;

        foreach (MeshFilter mf in filters)
        {
            if (mf.sharedMesh == null) continue;

            Matrix4x4 m    = mf.transform.localToWorldMatrix;
            Bounds    mesh = mf.sharedMesh.bounds;
            Vector3   c    = mesh.center;
            Vector3   e    = mesh.extents;

            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Vector3 corner = m.MultiplyPoint3x4(
                    c + new Vector3(e.x * sx, e.y * sy, e.z * sz));

                if (!initialized)
                {
                    combined    = new Bounds(corner, Vector3.zero);
                    initialized = true;
                }
                else
                {
                    combined.Encapsulate(corner);
                }
            }
        }

        if (!initialized) return;

        int cell = Mathf.Max(1, _gridCellSize);
        sizeX = Mathf.Max(1, Mathf.RoundToInt(combined.size.x / cell));
        sizeZ = Mathf.Max(1, Mathf.RoundToInt(combined.size.z / cell));

        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log($"[{name}] Footprint synced → sizeX={sizeX}, sizeZ={sizeZ}");
    }
#endif
}
