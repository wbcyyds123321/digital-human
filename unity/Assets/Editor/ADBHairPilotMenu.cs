using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Yixuan.Motion;

namespace Yixuan.EditorTools
{
    public static class ADBHairPilotMenu
    {
        const string RootName = "Yixuan_Test";

        [MenuItem("UnityLITF/仪玄/ADB 头发试点/添加到当前角色", false, 1)]
        static void AddToCharacter()
        {
            var characterRoot = GameObject.Find(RootName);
            if (characterRoot == null)
            {
                Debug.LogError("[ADBHairPilot] 找不到 " + RootName);
                return;
            }
            var dynamics = characterRoot.GetComponent<ZhuangfyHairDynamics>();
            if (dynamics == null) dynamics = characterRoot.AddComponent<ZhuangfyHairDynamics>();
            EditorUtility.SetDirty(dynamics);
            EditorSceneManager.MarkSceneDirty(characterRoot.scene);
            Selection.activeGameObject = characterRoot;
            Debug.Log("[ADBHairPilot] 已添加试点组件，场景已标记为脏；Play 下验证后再保存场景");
        }

        [MenuItem("UnityLITF/仪玄/ADB 头发试点/重建", false, 2)]
        static void Rebuild()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[ADBHairPilot] 请在 Play 模式下重建");
                return;
            }
            var dynamics = FindDynamics();
            if (dynamics == null) return;
            dynamics.Enabled = true;
            dynamics.Rebuild();
            Debug.Log("[ADBHairPilot] 已请求延迟重建（下一帧执行）");
        }

        [MenuItem("UnityLITF/仪玄/ADB 头发试点/重置", false, 3)]
        static void ResetPilot()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[ADBHairPilot] 请在 Play 模式下重置");
                return;
            }
            var dynamics = FindDynamics();
            if (dynamics == null) return;
            dynamics.ResetPilot();
            Debug.Log("[ADBHairPilot] 已恢复模拟骨 local pose");
        }

        [MenuItem("UnityLITF/仪玄/ADB 头发试点/移除", false, 4)]
        static void Remove()
        {
            var dynamics = FindDynamics();
            if (dynamics == null) return;
            dynamics.SetEnabled(false);
            var scene = dynamics.gameObject.scene;
            Object.DestroyImmediate(dynamics);
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[ADBHairPilot] 已移除试点组件");
        }

        static ZhuangfyHairDynamics FindDynamics()
        {
            var characterRoot = GameObject.Find(RootName);
            if (characterRoot == null)
            {
                Debug.LogError("[ADBHairPilot] 找不到 " + RootName);
                return null;
            }
            var dynamics = characterRoot.GetComponent<ZhuangfyHairDynamics>();
            if (dynamics == null)
            {
                Debug.LogError("[ADBHairPilot] 先执行 添加到当前角色");
                return null;
            }
            return dynamics;
        }
    }

    [CustomEditor(typeof(ZhuangfyHairDynamics))]
    public class ZhuangfyHairDynamicsEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var dynamics = (ZhuangfyHairDynamics)target;
            GUILayout.Space(6);
            GUILayout.Label("ADB 头发试点", EditorStyles.boldLabel);
            GUILayout.Label("Built: " + dynamics.IsBuilt + "  Chains: " + dynamics.ChainCount + "  Points: " + dynamics.PointCount
                + "  Bones: " + dynamics.SimulatedBoneCount + "  Weighted: " + dynamics.WeightedHairBoneCount);
            if (!string.IsNullOrEmpty(dynamics.BuildSummary)) GUILayout.Label(dynamics.BuildSummary);
            bool enableToggle = GUILayout.Toggle(dynamics.Enabled, "启用 ADB 头发");
            if (enableToggle != dynamics.Enabled)
            {
                dynamics.SetEnabled(enableToggle);
                if (!Application.isPlaying) EditorUtility.SetDirty(dynamics);
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("重建")) dynamics.Rebuild();
            if (GUILayout.Button("重置")) dynamics.ResetPilot();
            EditorGUILayout.EndHorizontal();
            if (GUILayout.Button("打开验收输出目录")) EditorUtility.RevealInFinder("Tools/_verify/ADB");
        }
    }
}
