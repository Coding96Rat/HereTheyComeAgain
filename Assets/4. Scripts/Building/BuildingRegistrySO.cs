using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 모든 빌딩 데이터를 id 기반으로 조회할 수 있는 ScriptableObject 레지스트리.
/// BuildingSystem이 이 레지스트리를 참조한다.
/// </summary>
[CreateAssetMenu(fileName = "BuildingRegistry", menuName = "Building/Building Registry")]
public class BuildingRegistrySO : ScriptableObject
{
    [SerializeField] private List<BuildingDataSO> _buildings = new List<BuildingDataSO>();

    private Dictionary<int, BuildingDataSO> _cache;

    public IReadOnlyList<BuildingDataSO> AllBuildings => _buildings;

    public BuildingDataSO GetById(int id)
    {
        if (_cache == null) BuildCache();
        return _cache.TryGetValue(id, out var data) ? data : null;
    }

    private void BuildCache()
    {
        _cache = new Dictionary<int, BuildingDataSO>(_buildings.Count);
        foreach (var b in _buildings)
        {
            if (b != null) _cache[b.id] = b;
        }
    }

    // 에디터에서 수정 시 캐시 무효화
    private void OnValidate() => _cache = null;
}
