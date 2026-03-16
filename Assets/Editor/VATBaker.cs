using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

public class VATBaker : ScriptableWizard
{
    [Header("Bake Target")]
    public GameObject targetGameObject; // 쪼개진 좀비의 최상단 부모 객체
    public AnimationClip animationClip;

    [Header("Settings")]
    public int frameRate = 30;
    public string savePath = "Assets/BakedVAT";

    [MenuItem("CodingRat/VAT Baker (Multi-Parts)")]
    static void CreateWizard()
    {
        ScriptableWizard.DisplayWizard<VATBaker>("VAT Animation Baker", "Bake Animation!");
    }

    private void OnWizardCreate()
    {
        if (targetGameObject == null || animationClip == null)
        {
            Debug.LogError("타겟 게임오브젝트와 애니메이션 클립을 모두 넣어주세요!");
            return;
        }

        // 핵심: 자식들에 있는 모든 SkinnedMeshRenderer를 싹 다 긁어옵니다.
        SkinnedMeshRenderer[] smrs = targetGameObject.GetComponentsInChildren<SkinnedMeshRenderer>();
        if (smrs.Length == 0)
        {
            Debug.LogError("타겟에 SkinnedMeshRenderer가 하나도 없습니다.");
            return;
        }

        if (!Directory.Exists(savePath)) Directory.CreateDirectory(savePath);

        // 1. 모든 파츠의 버텍스 개수를 합산
        int totalVertexCount = 0;
        foreach (var smr in smrs) totalVertexCount += smr.sharedMesh.vertexCount;

        int totalFrames = (int)(animationClip.length * frameRate);

        Texture2D vatTexture = new Texture2D(totalVertexCount, totalFrames, TextureFormat.RGBAHalf, false);
        vatTexture.filterMode = FilterMode.Point;
        vatTexture.wrapMode = TextureWrapMode.Clamp;

        Vector2[] uv2 = new Vector2[totalVertexCount];
        Mesh[] tempBakedMeshes = new Mesh[smrs.Length];
        for (int i = 0; i < smrs.Length; i++) tempBakedMeshes[i] = new Mesh();

        float timePerFrame = animationClip.length / totalFrames;

        // 2. 프레임별로 모든 파츠를 순회하며 텍스처 굽기
        for (int frame = 0; frame < totalFrames; frame++)
        {
            float currentTime = frame * timePerFrame;
            animationClip.SampleAnimation(targetGameObject, currentTime);

            int currentVertexOffset = 0;

            for (int i = 0; i < smrs.Length; i++)
            {
                smrs[i].BakeMesh(tempBakedMeshes[i]);
                Vector3[] vertices = tempBakedMeshes[i].vertices;

                for (int v = 0; v < vertices.Length; v++)
                {
                    // 각 파츠의 위치가 다르므로, 최상단 부모 기준의 로컬 좌표로 통일시켜줍니다.
                    Vector3 worldPos = smrs[i].transform.TransformPoint(vertices[v]);
                    Vector3 localPos = targetGameObject.transform.InverseTransformPoint(worldPos);

                    int globalV = currentVertexOffset + v;
                    vatTexture.SetPixel(globalV, frame, new Color(localPos.x, localPos.y, localPos.z, 1.0f));

                    if (frame == 0) uv2[globalV] = new Vector2((globalV + 0.5f) / (float)totalVertexCount, 0);
                }
                currentVertexOffset += vertices.Length;
            }
        }
        vatTexture.Apply();

        // 3. 모든 찰흙 파츠를 영혼까지 끌어모아 하나의 통짜 Mesh로 용접 (Combine)
        CombineInstance[] combine = new CombineInstance[smrs.Length];
        for (int i = 0; i < smrs.Length; i++)
        {
            combine[i].mesh = smrs[i].sharedMesh;
            // 각 파츠의 오프셋을 계산하여 정확한 위치에 조립
            combine[i].transform = targetGameObject.transform.worldToLocalMatrix * smrs[i].transform.localToWorldMatrix;
        }

        Mesh finalMesh = new Mesh();
        // 버텍스가 너무 많을 경우를 대비해 32bit 인덱스 포맷 사용
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combine, true, true);
        finalMesh.uv2 = uv2; // 용접된 메쉬에 마법의 바코드 ID 덮어씌우기

        // 4. 저장
        string objName = targetGameObject.name;
        AssetDatabase.CreateAsset(finalMesh, $"{savePath}/{objName}_VATMesh.asset");
        AssetDatabase.CreateAsset(vatTexture, $"{savePath}/{objName}_VATTexture.asset");
        AssetDatabase.SaveAssets();

        Debug.Log($"<color=lime>[합체 베이킹 성공!]</color> {smrs.Length}개의 파츠가 완벽하게 하나로 조립되어 구워졌습니다! (총 버텍스: {totalVertexCount})");
    }
}