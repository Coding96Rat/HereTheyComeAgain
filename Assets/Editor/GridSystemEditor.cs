using UnityEngine;
using UnityEditor;

// GridSystem 스크립트의 인스펙터 창을 커스텀 하겠다는 뜻입니다.
[CustomEditor(typeof(GridSystem))]
public class GridSystemEditor : Editor
{
    public override void OnInspectorGUI()
    {
        // 원래 있던 변수들(LeftBottomLocation, Rows 등)을 그대로 그려줍니다.
        base.OnInspectorGUI();

        GridSystem grid = (GridSystem)target;

        EditorGUILayout.Space(10); // 여백 살짝 주고

        // 굽기 버튼
        if (GUILayout.Button("Bake Grid (맵 스캔 & 점거)", GUILayout.Height(40)))
        {
            grid.BakeGrid();
            EditorUtility.SetDirty(grid); // 중요: 유니티에게 "데이터가 바뀌었으니 씬에 저장해!" 라고 알려주는 함수
        }

        // 초기화 버튼
        if (GUILayout.Button("Clear Grid (초기화)", GUILayout.Height(30)))
        {
            grid.ClearGrid();
            EditorUtility.SetDirty(grid);
        }
    }
}