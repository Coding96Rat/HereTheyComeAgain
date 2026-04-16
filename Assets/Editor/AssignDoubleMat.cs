using UnityEditor;
using UnityEngine;

public class AssignDoubleMat
{
    [MenuItem("Tools/Assign Toon Materials")]
    public static void Assign()
    {
        GameObject cube = GameObject.Find("Cube");
        Material cel = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/YoutubeCelMat.mat");
        Material outline = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/YoutubeOutlineMat.mat");
        
        if (cube != null && cel != null && outline != null)
        {
            var renderer = cube.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                // 강제로 매테리얼 배열 사이즈를 2로 늘리고 각각 할당합니다.
                renderer.sharedMaterials = new Material[] { cel, outline };
                Debug.Log("Materials assigned to Cube successfully!");
            }
        }
    }
}
