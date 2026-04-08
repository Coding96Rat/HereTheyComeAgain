using UnityEngine;
using UnityEditor;

// GridSystem ��ũ��Ʈ�� �ν����� â�� Ŀ���� �ϰڴٴ� ���Դϴ�.
[CustomEditor(typeof(GridSystem))]
public class GridSystemEditor : Editor
{
    public override void OnInspectorGUI()
    {
        // ���� �ִ� ������(LeftBottomLocation, Rows ��)�� �״�� �׷��ݴϴ�.
        base.OnInspectorGUI();

        GridSystem grid = (GridSystem)target;

        EditorGUILayout.Space(10); // ���� ��¦ �ְ�

        // ���� ��ư
        if (GUILayout.Button("Bake Grid + FlowField (전체 베이크)", GUILayout.Height(40)))
        {
            grid.BakeGrid();
            EditorUtility.SetDirty(grid);

            FlowFieldSystem ffs = FindFirstObjectByType<FlowFieldSystem>();
            if (ffs != null)
            {
                ffs.BakeInEditor();
                EditorUtility.SetDirty(ffs);
                Debug.Log("[GridSystemEditor] FlowFieldSystem 베이크 완료.");
            }
            else
            {
                Debug.LogWarning("[GridSystemEditor] FlowFieldSystem을 씬에서 찾을 수 없습니다.");
            }
        }

        // �ʱ�ȭ ��ư
        if (GUILayout.Button("Clear Grid (삭제)", GUILayout.Height(30)))
        {
            grid.ClearGrid();
            EditorUtility.SetDirty(grid);
        }
    }
}
