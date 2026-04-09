using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

/// <summary>
/// 모든 BuildingDataSO를 Addressables 레이블 기반으로 on-demand 로드하는 레지스트리.
///
/// 사용 흐름:
///   1. BuildingSystem.OnStartNetwork → StartCoroutine(InitRegistryAsync)
///   2. WaitUntil(LoadAllAsync().IsCompleted)
///   3. IsLoaded == true → GetById / AllBuildings 사용 가능
///   4. BuildingSystem.OnStopNetwork → Unload()
///
/// Inspector 설정:
///   _buildingLabel 슬롯에 Addressables 창에서 BuildingDataSO 에셋에 부여한
///   레이블(예: "BuildingData")을 지정할 것.
/// </summary>
[CreateAssetMenu(fileName = "BuildingRegistry", menuName = "Building/Building Registry")]
public class BuildingRegistrySO : ScriptableObject
{
    [Tooltip("Addressables 창에서 BuildingDataSO 에셋에 부여한 레이블 (예: \"BuildingData\")")]
    [SerializeField] private AssetLabelReference _buildingLabel;

    private Dictionary<int, BuildingDataSO>           _cache;
    private AsyncOperationHandle<IList<BuildingDataSO>> _loadHandle;

    // ─── 상태 조회 ────────────────────────────────────────────────────────────

    public bool IsLoaded => _cache != null;

    // ─── 로드 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 레이블에 해당하는 모든 BuildingDataSO를 비동기 로드.
    /// 완료 시 내부 캐시가 구성되며 IsLoaded = true가 된다.
    /// BuildingSystem의 InitRegistryAsync 코루틴에서 WaitUntil로 대기할 것.
    /// </summary>
    public Task LoadAllAsync()
    {
        if (IsLoaded) return Task.CompletedTask;

        if (_buildingLabel == null || string.IsNullOrEmpty(_buildingLabel.labelString))
        {
            Debug.LogError("[BuildingRegistry] _buildingLabel이 설정되지 않았습니다. " +
                           "Inspector에서 Addressables 레이블을 지정하세요.");
            return Task.FromException(new InvalidOperationException("BuildingLabel not set"));
        }

        var tcs = new TaskCompletionSource<bool>();

        _loadHandle = Addressables.LoadAssetsAsync<BuildingDataSO>(
            _buildingLabel.labelString, null);

        _loadHandle.Completed += op =>
        {
            if (op.Status == AsyncOperationStatus.Succeeded)
            {
                BuildCache(op.Result);
                tcs.SetResult(true);
            }
            else
            {
                string msg = $"[BuildingRegistry] 레이블 '{_buildingLabel.labelString}' 로드 실패.";
                Debug.LogError(msg);
                tcs.SetException(new Exception(msg));
            }
        };

        return tcs.Task;
    }

    // ─── 언로드 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// OnStopNetwork 시 호출. 로드된 에셋 핸들을 해제한다.
    /// </summary>
    public void Unload()
    {
        if (_loadHandle.IsValid())
            Addressables.Release(_loadHandle);
        _cache = null;
    }

    // ─── 데이터 접근 ──────────────────────────────────────────────────────────

    public BuildingDataSO GetById(int id)
    {
        if (_cache == null)
        {
            Debug.LogWarning("[BuildingRegistry] 아직 로드되지 않았습니다. " +
                             "BuildingSystem.IsRegistryReady 확인 후 호출하세요.");
            return null;
        }
        return _cache.TryGetValue(id, out var data) ? data : null;
    }

    /// <summary>로드 완료 후 사용 가능. 그 전에는 빈 컬렉션 반환.</summary>
    public IReadOnlyCollection<BuildingDataSO> AllBuildings =>
        _cache != null
            ? (IReadOnlyCollection<BuildingDataSO>)_cache.Values
            : Array.Empty<BuildingDataSO>();

    // ─── 내부 ─────────────────────────────────────────────────────────────────

    private void BuildCache(IList<BuildingDataSO> list)
    {
        _cache = new Dictionary<int, BuildingDataSO>(list.Count);
        foreach (var b in list)
            if (b != null) _cache[b.id] = b;
    }

#if UNITY_EDITOR
    private void OnValidate() => _cache = null;
#endif
}
