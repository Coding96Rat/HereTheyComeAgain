using System.Collections.Generic;
using FishNet.Object;
using FishNet.Managing.Object;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Building/Setup Building Prefabs 메뉴로 실행.
/// BuildingRegistrySO에 등록된 모든 건물 프리팹에:
///  1. NetworkObject 자동 추가
///  2. FishNet NetworkManager 의 DefaultPrefabObjects 에 자동 등록
/// </summary>
public static class BuildingPrefabSetupTool
{
    [MenuItem("Building/Setup Building Prefabs (NetworkObject 자동 등록)")]
    public static void SetupBuildingPrefabs()
    {
        // 1. 프로젝트 내 BuildingRegistrySO 탐색
        string[] guids = AssetDatabase.FindAssets("t:BuildingRegistrySO");
        if (guids.Length == 0)
        {
            EditorUtility.DisplayDialog("오류",
                "BuildingRegistrySO 에셋을 찾을 수 없습니다.\n" +
                "Assets 폴더 내에 BuildingRegistry SO를 먼저 생성해주세요.", "확인");
            return;
        }

        string registryPath = AssetDatabase.GUIDToAssetPath(guids[0]);
        var registry = AssetDatabase.LoadAssetAtPath<BuildingRegistrySO>(registryPath);
        if (registry == null) return;

        // 2. FishNet DefaultPrefabObjects 탐색
        string[] prefabObjGuids = AssetDatabase.FindAssets("t:DefaultPrefabObjects");
        DefaultPrefabObjects defaultPrefabs = null;
        if (prefabObjGuids.Length > 0)
            defaultPrefabs = AssetDatabase.LoadAssetAtPath<DefaultPrefabObjects>(
                AssetDatabase.GUIDToAssetPath(prefabObjGuids[0]));

        int addedNOB  = 0;
        int addedFish = 0;

        foreach (var data in registry.AllBuildings)
        {
            if (data == null || data.prefab == null) continue;

            string prefabPath = AssetDatabase.GetAssetPath(data.prefab);
            if (string.IsNullOrEmpty(prefabPath)) continue;

            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null) continue;

            bool dirty = false;

            // ── NetworkObject 추가 ──
            if (prefabAsset.GetComponent<NetworkObject>() == null)
            {
                prefabAsset.AddComponent<NetworkObject>();
                dirty = true;
                addedNOB++;
            }

            if (dirty)
            {
                EditorUtility.SetDirty(prefabAsset);
                PrefabUtility.SavePrefabAsset(prefabAsset);
            }

            // ── DefaultPrefabObjects 등록 ──
            if (defaultPrefabs != null)
            {
                var nob = prefabAsset.GetComponent<NetworkObject>();
                bool alreadyRegistered = false;
                foreach (var p in defaultPrefabs.Prefabs)
                    if (p == nob) { alreadyRegistered = true; break; }

                if (nob != null && !alreadyRegistered)
                {
                    defaultPrefabs.AddObject(nob, true);
                    addedFish++;
                }
            }
        }

        if (defaultPrefabs != null)
        {
            EditorUtility.SetDirty(defaultPrefabs);
            AssetDatabase.SaveAssets();
        }

        EditorUtility.DisplayDialog("완료",
            $"처리 결과:\n" +
            $"• NetworkObject 추가: {addedNOB}개 프리팹\n" +
            $"• FishNet 등록: {addedFish}개 프리팹\n\n" +
            (defaultPrefabs == null
                ? "⚠ DefaultPrefabObjects를 찾지 못했습니다.\n" +
                  "FishNet NetworkManager 인스펙터에서 수동으로 등록하거나\n" +
                  "DefaultPrefabObjects SO를 생성해주세요."
                : "FishNet SpawnablePrefabs 등록 완료."),
            "확인");
    }
}
