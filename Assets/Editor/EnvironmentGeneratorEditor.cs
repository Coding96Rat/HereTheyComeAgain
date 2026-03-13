using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(EnvironmentGenerator))]
public class EnvironmentGeneratorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI(); // 기본 변수들 그려주기

        EnvironmentGenerator generator = (EnvironmentGenerator)target;

        EditorGUILayout.Space(15);

        GUI.backgroundColor = Color.green;
        if (GUILayout.Button("랜덤 방 생성 (Generate Rooms)", GUILayout.Height(40)))
        {
            generator.GenerateEnvironment();
        }

        GUI.backgroundColor = Color.red;
        if (GUILayout.Button("생성된 방 모두 지우기 (Clear)", GUILayout.Height(30)))
        {
            generator.ClearEnvironment();
        }
    }
}