using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Yixuan.SelectionLab;

namespace Yixuan.SelectionLab.Editor
{
    /// <summary>
    /// Deterministic builder for the whitebox selection lab. It copies the
    /// existing task scene to a new file, removes only the Soviet-map-specific
    /// roots, adds the white floor, occluder screen and seven explicit target
    /// instances, and leaves the user's current scene untouched.
    /// </summary>
    public static class SelectionLabBuilder
    {
        const string SourceScene = "Assets/Scenes/SovietBlueHour_TaskPlan.unity";
        const string LabScene = "Assets/Scenes/WhiteboxSelectionLab.unity";
        const string ResourceRoot = "Assets/Resources/SelectionLab";
        const string MaterialRoot = ResourceRoot + "/Materials";

        [MenuItem("Tools/白色选中实验室/构建实验室场景")]
        public static void BuildFromMenu()
        {
            Build();
        }

        public static void Build()
        {
            var active = EditorSceneManager.GetActiveScene();
            if (active.isDirty)
                throw new InvalidOperationException("当前场景有未保存修改；请先保存或关闭，实验室构建不会覆盖用户场景。");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(LabScene) != null)
                throw new InvalidOperationException("实验室场景已存在，默认不覆盖：" + LabScene);

            EnsureFolder("Assets/Resources", "SelectionLab");
            EnsureFolder(ResourceRoot, "Materials");
            if (!AssetDatabase.CopyAsset(SourceScene, LabScene))
                throw new InvalidOperationException("无法复制场景：" + SourceScene);
            AssetDatabase.Refresh();
            EditorSceneManager.OpenScene(LabScene, OpenSceneMode.Single);

            RemoveSovietRoots();
            var root = new GameObject("SelectionLab");
            var lab = root.AddComponent<SelectionLabScene>();
            var duplicateScenes = root.GetComponents<SelectionLabScene>();
            for (int i = 1; i < duplicateScenes.Length; i++) UnityEngine.Object.DestroyImmediate(duplicateScenes[i]);
            root.AddComponent<SelectionLabHud>();

            var floor = MakeCube("LabFloor", new Vector3(0f, -0.07f, 0f), new Vector3(24f, 0.1f, 20f), Material("LabFloor", new Color(0.82f, 0.84f, 0.86f)));
            floor.transform.SetParent(root.transform, true);
            var screen = MakeCube("LabScreen_Occluder", new Vector3(0f, 1.4f, 3.8f), new Vector3(5.0f, 2.5f, 0.18f), Material("LabScreen", new Color(0.97f, 0.97f, 0.97f)));
            screen.transform.SetParent(root.transform, true);

            lab.entities = new[]
            {
                AddTarget(root, "lab/red_cube", "cube", "红色立方体", "红", PrimitiveType.Cube,
                    new Vector3(-4.5f, 1.25f, 1.0f), new Vector3(0.8f, 0.8f, 0.8f), new Color(0.82f, 0.12f, 0.12f)),
                AddTarget(root, "lab/blue_sphere", "sphere", "蓝色球体", "蓝", PrimitiveType.Sphere,
                    new Vector3(0.4f, 1.35f, 1.8f), Vector3.one * 0.8f, new Color(0.12f, 0.38f, 0.92f)),
                AddTarget(root, "lab/yellow_cylinder", "cylinder", "黄色圆柱", "黄", PrimitiveType.Cylinder,
                    new Vector3(-2.1f, 1.25f, 1.0f), new Vector3(0.55f, 0.8f, 0.55f), new Color(0.95f, 0.75f, 0.12f)),
                AddTarget(root, "lab/green_cube_left", "cube", "绿色方块（左）", "绿", PrimitiveType.Cube,
                    new Vector3(2.4f, 1.25f, 1.2f), new Vector3(0.8f, 0.8f, 0.8f), new Color(0.16f, 0.72f, 0.28f)),
                AddTarget(root, "lab/green_cube_right", "cube", "绿色方块（右）", "绿", PrimitiveType.Cube,
                    new Vector3(4.1f, 1.25f, 1.2f), new Vector3(0.8f, 0.8f, 0.8f), new Color(0.16f, 0.72f, 0.28f)),
                AddTarget(root, "lab/purple_capsule", "capsule", "紫色胶囊体", "紫", PrimitiveType.Capsule,
                    new Vector3(2.8f, 1.5f, 3.4f), Vector3.one * 0.8f, new Color(0.56f, 0.28f, 0.82f)),
                AddTarget(root, "lab/orange_hidden_sphere", "sphere", "橙色球体（屏风后）", "橙", PrimitiveType.Sphere,
                    new Vector3(0f, 1.25f, 4.8f), Vector3.one * 0.8f, new Color(0.95f, 0.48f, 0.12f)),
            };

            lab.spawnPoint = Marker(root.transform, "Observation_A", new Vector3(0f, 0.02f, -7.0f), Vector3.forward);
            lab.observationA = Marker(root.transform, "Observation_A_View", new Vector3(0f, 0.02f, -7.0f), Vector3.forward);
            lab.observationB = Marker(root.transform, "Observation_B_View", new Vector3(5.5f, 0.02f, -5.0f), new Vector3(-0.55f, 0f, 0.84f));
            lab.mapMin = new Vector2(-12f, -8f);
            lab.mapMax = new Vector2(12f, 8f);

            ConfigureLighting(root);
            var eye = GameObject.Find("EyeCamera");
            if (eye != null)
            {
                var camera = eye.GetComponent<Camera>();
                if (camera != null) camera.backgroundColor = new Color(0.62f, 0.66f, 0.70f);
            }
            var character = GameObject.Find("Yixuan_Test");
            if (character != null)
            {
                var loco = character.GetComponent<Yixuan.Motion.CharacterLocomotion>();
                if (loco != null) loco.Stop("selection_lab_editor_build");
                character.transform.position = lab.spawnPoint.position;
                character.transform.rotation = lab.spawnPoint.rotation;
            }
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), LabScene);
            AssetDatabase.Refresh();
            Debug.Log("[SelectionLab] built " + LabScene + " entities=" + lab.entities.Length + " screen=non_selectable");
        }

        static SelectionLabEntityDef AddTarget(GameObject root, string id, string cls, string display, string color,
                                               PrimitiveType type, Vector3 position, Vector3 scale, Color tint)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = id.Replace('/', '_');
            go.transform.SetParent(root.transform, false);
            go.transform.position = position;
            go.transform.localScale = scale;
            var collider = go.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
            var renderer = go.GetComponent<Renderer>();
            var mat = Material("Mat_" + id.Replace('/', '_'), tint);
            renderer.sharedMaterial = mat;
            return new SelectionLabEntityDef
            {
                id = id, cls = cls, display_name = display, shape = cls, color = color,
                target = go.transform, renderer = renderer, capabilities = "query,select"
            };
        }

        static GameObject MakeCube(string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = scale;
            var c = go.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.DestroyImmediate(c);
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        static Transform Marker(Transform parent, string name, Vector3 pos, Vector3 forward)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
            return go.transform;
        }

        static void ConfigureLighting(GameObject root)
        {
            var key = GameObject.Find("KeyLight");
            if (key != null) key.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
            var fill = GameObject.Find("FillLight");
            if (fill != null) fill.transform.rotation = Quaternion.Euler(30f, 150f, 0f);
            var rim = GameObject.Find("RimLight");
            if (rim != null) rim.transform.rotation = Quaternion.Euler(20f, 70f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.58f, 0.62f);
            RenderSettings.fog = false;
            RenderSettings.skybox = null;
        }

        static void RemoveSovietRoots()
        {
            string[] remove =
            {
                "SovietBlueHour_Map", "SovietBlueHourSky", "MoonLight", "DynamicInteractionSystem",
                "TaskAcceptanceRoot", "LightingSetup"
            };
            foreach (var name in remove)
            {
                var go = GameObject.Find(name);
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
            var vision = GameObject.Find("VisionSystem");
            if (vision != null)
            {
                var observer = vision.GetComponent<Yixuan.Vision.VisionObserver>();
                if (observer != null) UnityEngine.Object.DestroyImmediate(observer);
            }
        }

        static Material Material(string name, Color color)
        {
            string path = MaterialRoot + "/" + name + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                existing.color = color;
                return existing;
            }
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader) { name = name };
            mat.color = color;
            mat.SetFloat("_Smoothness", 0.18f);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + child))
                AssetDatabase.CreateFolder(parent, child);
        }
    }
}
