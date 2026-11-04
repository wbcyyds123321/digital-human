using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Yixuan.Diorama;

namespace Yixuan.Diorama.EditorTools
{
    /// <summary>
    /// 程序化生成「雨夜便利店街角」微缩景观。
    /// 菜单：Tools/Rainy Convenience/Rebuild Scene
    /// </summary>
    public static class DioramaSceneBuilder
    {
        const string RootDir = "Assets/Diorama";
        const string MatDir = "Assets/Diorama/Materials";
        const string ScenePath = "Assets/Scenes/RainyConvenience.unity";

        static readonly Dictionary<string, Material> Mats = new Dictionary<string, Material>();
        static Shader _toon;
        static Shader _glass;
        static Shader _unlit;

        [MenuItem("Tools/Rainy Convenience/Rebuild Scene")]
        public static void Build()
        {
            _toon = Shader.Find("Yixuan/DioramaToon");
            _glass = Shader.Find("Yixuan/DioramaGlass");
            _unlit = Shader.Find("Yixuan/DioramaUnlit");
            if (_toon == null || _glass == null || _unlit == null)
            {
                Debug.LogError("[Diorama] 找不到自定义 shader，请先等待编译完成。");
                return;
            }

            if (AssetDatabase.IsValidFolder(RootDir)) AssetDatabase.DeleteAsset(RootDir);
            AssetDatabase.CreateFolder("Assets", "Diorama");
            AssetDatabase.CreateFolder(RootDir, "Materials");
            if (System.IO.File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
            AssetDatabase.Refresh();

            Mats.Clear();
            CreateMaterials();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("RainyConvenienceDiorama").transform;

            BuildBase(root);
            BuildStreet(root);
            BuildApartment(root);
            BuildStore(root);
            BuildExterior(root);
            BuildLights(root);
            BuildRain(root);
            BuildRipples(root);
            BuildCamera(root);
            BuildPostFx(root);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.08f, 0.09f, 0.14f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.012f;
            RenderSettings.fogColor = new Color(0.065f, 0.095f, 0.155f);
            RenderSettings.skybox = null;

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeGameObject = root.gameObject;
            Debug.Log("[Diorama] 场景生成完成：" + ScenePath);
        }

        // ================================================================
        //  材质
        // ================================================================
        static void CreateMaterials()
        {
            Toon("Asphalt", new Color(0.30f, 0.33f, 0.40f), new Color(0.13f, 0.15f, 0.21f), 0.008f);
            Toon("Sidewalk", new Color(0.60f, 0.60f, 0.63f), new Color(0.33f, 0.34f, 0.39f), 0.008f);
            Toon("Curb", new Color(0.72f, 0.72f, 0.70f), new Color(0.38f, 0.39f, 0.42f), 0.008f);
            Toon("BaseDark", new Color(0.10f, 0.12f, 0.16f), new Color(0.04f, 0.05f, 0.08f), 0.014f);
            Toon("BaseRim", new Color(0.30f, 0.28f, 0.26f), new Color(0.13f, 0.12f, 0.12f), 0.014f);
            Toon("StoreWall", new Color(0.83f, 0.80f, 0.73f), new Color(0.44f, 0.45f, 0.48f), 0.010f);
            Toon("StoreGreen", new Color(0.06f, 0.33f, 0.21f), new Color(0.02f, 0.13f, 0.08f), 0.012f);
            Toon("StoreGreenLight", new Color(0.18f, 0.55f, 0.34f), new Color(0.06f, 0.23f, 0.13f), 0.008f);
            Toon("SignCream", new Color(0.96f, 0.94f, 0.83f), new Color(0.62f, 0.60f, 0.50f), 0.010f);
            Toon("Apartment", new Color(0.34f, 0.23f, 0.17f), new Color(0.15f, 0.10f, 0.08f), 0.012f);
            Toon("ApartmentDark", new Color(0.17f, 0.12f, 0.10f), new Color(0.07f, 0.05f, 0.04f), 0.012f);
            Toon("WindowWarm", new Color(1.0f, 0.86f, 0.56f), new Color(0.55f, 0.38f, 0.18f), 0f,
                new Color(1.0f, 0.64f, 0.28f), 1.4f);
            Toon("VanBlue", new Color(0.12f, 0.45f, 0.52f), new Color(0.04f, 0.18f, 0.22f), 0.012f);
            Toon("VanGlass", new Color(0.08f, 0.14f, 0.18f), new Color(0.03f, 0.05f, 0.07f), 0.008f);
            Toon("VendingOrange", new Color(0.95f, 0.42f, 0.12f), new Color(0.48f, 0.18f, 0.04f), 0.010f,
                new Color(1.0f, 0.38f, 0.06f), 0.8f);
            Toon("VendingGreen", new Color(0.18f, 0.55f, 0.28f), new Color(0.05f, 0.22f, 0.10f), 0.010f,
                new Color(0.15f, 0.7f, 0.3f), 0.7f);
            Toon("Gold", new Color(0.62f, 0.47f, 0.22f), new Color(0.30f, 0.21f, 0.08f), 0.010f,
                new Color(0.7f, 0.5f, 0.18f), 0.35f);
            Toon("SidewalkWarm", new Color(0.62f, 0.48f, 0.32f), new Color(0.32f, 0.24f, 0.16f), 0.008f);
            Toon("StoreDark", new Color(0.16f, 0.17f, 0.21f), new Color(0.07f, 0.08f, 0.10f), 0.010f);
            Toon("Frame", new Color(0.22f, 0.24f, 0.27f), new Color(0.09f, 0.10f, 0.12f), 0.012f);
            Toon("InteriorFloor", new Color(0.72f, 0.62f, 0.47f), new Color(0.38f, 0.32f, 0.24f), 0.006f);
            Toon("Shelf", new Color(0.82f, 0.83f, 0.84f), new Color(0.42f, 0.44f, 0.48f), 0.006f);
            Toon("Counter", new Color(0.55f, 0.36f, 0.22f), new Color(0.28f, 0.18f, 0.11f), 0.008f);
            Toon("White", new Color(0.92f, 0.92f, 0.90f), new Color(0.50f, 0.52f, 0.56f), 0.008f);
            Toon("Trash", new Color(0.38f, 0.42f, 0.40f), new Color(0.18f, 0.20f, 0.20f), 0.008f);
            Toon("Metal", new Color(0.55f, 0.58f, 0.62f), new Color(0.25f, 0.27f, 0.30f), 0.008f);
            Toon("Wood", new Color(0.48f, 0.31f, 0.20f), new Color(0.24f, 0.15f, 0.10f), 0.008f);

            Toon("NeonGreen", new Color(0.55f, 1.0f, 0.72f), new Color(0.20f, 0.55f, 0.35f), 0f,
                new Color(0.20f, 1.0f, 0.55f), 2.0f);
            Toon("NeonBlue", new Color(0.45f, 0.75f, 1.0f), new Color(0.18f, 0.30f, 0.48f), 0f,
                new Color(0.25f, 0.60f, 1.0f), 1.6f);
            Toon("NeonRed", new Color(1.0f, 0.38f, 0.38f), new Color(0.50f, 0.16f, 0.16f), 0f,
                new Color(1.0f, 0.18f, 0.18f), 1.8f);
            Toon("NeonYellow", new Color(1.0f, 0.86f, 0.45f), new Color(0.52f, 0.40f, 0.16f), 0f,
                new Color(1.0f, 0.72f, 0.20f), 1.8f);
            Toon("WarmLight", new Color(1.0f, 0.93f, 0.76f), new Color(0.60f, 0.48f, 0.30f), 0f,
                new Color(1.0f, 0.78f, 0.48f), 1.6f);
            Toon("CoolLight", new Color(0.82f, 0.92f, 1.0f), new Color(0.40f, 0.52f, 0.66f), 0f,
                new Color(0.55f, 0.75f, 1.0f), 1.4f);

            // 商品色
            string[] names = { "ProductRed", "ProductOrange", "ProductYellow", "ProductGreen", "ProductBlue", "ProductPink", "ProductPurple", "ProductCyan" };
            Color[] cols = {
                new Color(0.92f,0.28f,0.25f), new Color(0.95f,0.55f,0.20f), new Color(0.98f,0.82f,0.28f),
                new Color(0.30f,0.75f,0.42f), new Color(0.28f,0.55f,0.90f), new Color(0.95f,0.45f,0.65f),
                new Color(0.60f,0.38f,0.88f), new Color(0.25f,0.80f,0.82f)
            };
            for (int i = 0; i < names.Length; i++) Toon(names[i], cols[i], cols[i] * 0.55f, 0.004f);

            Glass("Glass", new Color(0.40f, 0.68f, 0.92f, 0.004f));
            Glass("GlassDark", new Color(0.20f, 0.32f, 0.45f, 0.18f));
            Glass("Puddle", new Color(0.26f, 0.42f, 0.62f, 0.42f));
            Glass("WarmGlass", new Color(1.0f, 0.72f, 0.38f, 0.10f));
            Unlit("Wire", new Color(0.05f, 0.06f, 0.08f, 1f));
            Unlit("Rain", new Color(0.65f, 0.80f, 1.0f, 0.45f));
            Unlit("Ripple", new Color(0.72f, 0.88f, 1.0f, 0.55f));
            Unlit("LineWhite", new Color(0.92f, 0.94f, 0.90f, 1f));
            Unlit("LineYellow", new Color(0.95f, 0.85f, 0.35f, 1f));
            Unlit("ReflectWarm", new Color(1.0f, 0.62f, 0.22f, 0.20f));
            Unlit("ReflectOrange", new Color(1.0f, 0.40f, 0.10f, 0.18f));
            Unlit("ReflectGreen", new Color(0.20f, 1.0f, 0.55f, 0.16f));
            Unlit("ReflectBlue", new Color(0.25f, 0.60f, 1.0f, 0.16f));
            Unlit("ReflectRed", new Color(1.0f, 0.22f, 0.12f, 0.15f));
        }

        static Material Toon(string key, Color baseCol, Color shadeCol, float outline = 0.01f, Color? emission = null, float emissionStrength = 0f)
        {
            var m = new Material(_toon);
            m.name = key;
            m.SetColor("_BaseColor", baseCol);
            m.SetColor("_ShadeColor", shadeCol);
            m.SetColor("_EmissionColor", emission ?? Color.black);
            m.SetFloat("_EmissionStrength", emissionStrength);
            m.SetColor("_OutlineColor", new Color(0.015f, 0.02f, 0.035f, 1f));
            m.SetFloat("_OutlineWidth", outline);
            AssetDatabase.CreateAsset(m, MatDir + "/" + key + ".mat");
            Mats[key] = m;
            return m;
        }

        static Material Glass(string key, Color col)
        {
            var m = new Material(_glass);
            m.name = key;
            m.SetColor("_Color", col);
            AssetDatabase.CreateAsset(m, MatDir + "/" + key + ".mat");
            Mats[key] = m;
            return m;
        }

        static Material Unlit(string key, Color col)
        {
            var m = new Material(_unlit);
            m.name = key;
            m.SetColor("_Color", col);
            AssetDatabase.CreateAsset(m, MatDir + "/" + key + ".mat");
            Mats[key] = m;
            return m;
        }

        static Material M(string key) { return Mats[key]; }

        // ================================================================
        //  小工具
        // ================================================================
        static Transform Group(Transform parent, string name, Vector3 localPos = default(Vector3))
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 size, string mat, Quaternion? rot = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot ?? Quaternion.identity;
            go.transform.localScale = size;
            var c = go.GetComponent<Collider>(); if (c != null) Object.DestroyImmediate(c);
            go.GetComponent<Renderer>().sharedMaterial = M(mat);
            return go;
        }

        static GameObject Cyl(Transform parent, string name, Vector3 pos, float radius, float height, string mat, Quaternion? rot = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot ?? Quaternion.identity;
            go.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            var c = go.GetComponent<Collider>(); if (c != null) Object.DestroyImmediate(c);
            go.GetComponent<Renderer>().sharedMaterial = M(mat);
            return go;
        }

        static GameObject Sphere(Transform parent, string name, Vector3 pos, float diameter, string mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = Vector3.one * diameter;
            var c = go.GetComponent<Collider>(); if (c != null) Object.DestroyImmediate(c);
            go.GetComponent<Renderer>().sharedMaterial = M(mat);
            return go;
        }

        static void Text3D(Transform parent, string text, Vector3 pos, float size, Color color, Quaternion? rot = null)
        {
            var go = new GameObject("Text_" + text);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot ?? Quaternion.identity;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.fontSize = 64;
            tm.characterSize = size;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f != null) tm.font = f;
            var mr = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        static void Wire(Transform parent, string name, Vector3 a, Vector3 b, float width = 0.025f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
            lr.startWidth = width;
            lr.endWidth = width;
            lr.material = M("Wire");
            lr.numCapVertices = 2;
        }

        // ================================================================
        //  底座 / 街道
        // ================================================================
        static void BuildBase(Transform root)
        {
            var g = Group(root, "Base");
            Box(g, "Plinth", new Vector3(0f, -0.32f, 0f), new Vector3(14f, 0.64f, 14f), "BaseDark");
            Box(g, "Rim", new Vector3(0f, -0.66f, 0f), new Vector3(14.35f, 0.12f, 14.35f), "BaseRim");
            Box(g, "Ground", new Vector3(0f, 0.01f, 0f), new Vector3(14f, 0.04f, 14f), "Asphalt");
            // 底座边缘的一圈浅色描边，强化“模型感”
            Box(g, "EdgeFront", new Vector3(0f, -0.05f, 7.02f), new Vector3(14f, 0.10f, 0.06f), "BaseRim");
            Box(g, "EdgeBack", new Vector3(0f, -0.05f, -7.02f), new Vector3(14f, 0.10f, 0.06f), "BaseRim");
            Box(g, "EdgeLeft", new Vector3(-7.02f, -0.05f, 0f), new Vector3(0.06f, 0.10f, 14f), "BaseRim");
            Box(g, "EdgeRight", new Vector3(7.02f, -0.05f, 0f), new Vector3(0.06f, 0.10f, 14f), "BaseRim");
            // 黄铜色收藏底座线条
            Box(g, "GoldFront", new Vector3(0f, -0.02f, 7.04f), new Vector3(14.1f, 0.035f, 0.035f), "Gold");
            Box(g, "GoldBack", new Vector3(0f, -0.02f, -7.04f), new Vector3(14.1f, 0.035f, 0.035f), "Gold");
            Box(g, "GoldLeft", new Vector3(-7.04f, -0.02f, 0f), new Vector3(0.035f, 0.035f, 14.1f), "Gold");
            Box(g, "GoldRight", new Vector3(7.04f, -0.02f, 0f), new Vector3(0.035f, 0.035f, 14.1f), "Gold");
        }

        static void BuildStreet(Transform root)
        {
            var g = Group(root, "Street");

            // 店前人行道（z 0..3，x -7..3）
            Box(g, "Sidewalk_Front", new Vector3(-2f, 0.09f, 1.5f), new Vector3(10f, 0.18f, 3f), "Sidewalk");
            // 右侧人行道（x 1.4..3，z -7..3）
            Box(g, "Sidewalk_Right", new Vector3(2.2f, 0.09f, -2f), new Vector3(1.6f, 0.18f, 10f), "Sidewalk");
            // 路缘石
            Box(g, "Curb_Front", new Vector3(-2f, 0.12f, 3.02f), new Vector3(10f, 0.22f, 0.12f), "Curb");
            Box(g, "Curb_Right", new Vector3(3.02f, 0.12f, -2f), new Vector3(0.12f, 0.22f, 10f), "Curb");

            // 斑马线（前侧马路）
            for (int i = 0; i < 7; i++)
                Box(g, "Crosswalk_" + i, new Vector3(-1.8f + i * 0.62f, 0.045f, 4.8f),
                    new Vector3(0.36f, 0.012f, 3.0f), "LineWhite");

            // 右侧停车位线
            for (int i = 0; i < 3; i++)
            {
                Box(g, "ParkLineA_" + i, new Vector3(4.0f, 0.045f, -3.5f + i * 2.2f), new Vector3(0.07f, 0.012f, 2.0f), "LineWhite");
                Box(g, "ParkLineB_" + i, new Vector3(6.6f, 0.045f, -3.5f + i * 2.2f), new Vector3(0.07f, 0.012f, 2.0f), "LineWhite");
            }

            // 排水沟/篦子
            Box(g, "Gutter_Front", new Vector3(-2f, 0.035f, 3.16f), new Vector3(10f, 0.01f, 0.16f), "StoreDark");
            for (int i = 0; i < 6; i++)
                Box(g, "Drain_" + i, new Vector3(-6f + i * 1.8f, 0.045f, 3.16f), new Vector3(0.55f, 0.012f, 0.12f), "Metal");
            Box(g, "Gutter_Right", new Vector3(3.16f, 0.035f, -2f), new Vector3(0.16f, 0.01f, 10f), "StoreDark");

            // 水洼 + 反射色块
            Puddle(g, "Puddle_1", new Vector3(-4.8f, 0.025f, 1.9f), new Vector2(1.4f, 0.6f));
            Puddle(g, "Puddle_2", new Vector3(0.4f, 0.025f, 2.4f), new Vector2(0.9f, 0.45f));
            Puddle(g, "Puddle_3", new Vector3(4.3f, 0.025f, 3.7f), new Vector2(1.8f, 0.8f));
            Puddle(g, "Puddle_4", new Vector3(-1.8f, 0.025f, 5.7f), new Vector2(1.2f, 0.5f));

            // 店前暖色人行道带（参考图店门口的暖黄铺装）
            Box(g, "WarmSidewalk", new Vector3(-2.4f, 0.195f, 0.65f), new Vector3(4.9f, 0.015f, 1.2f), "SidewalkWarm");
            Box(g, "WarmCurb", new Vector3(-2.4f, 0.21f, 1.28f), new Vector3(4.9f, 0.02f, 0.1f), "VendingOrange");

            // 店前湿地面上的暖/彩色反光长条（人行道表面 y≈0.21）
            Box(g, "WetReflection_Warm", new Vector3(-2.6f, 0.212f, 2.3f), new Vector3(2.4f, 0.01f, 0.22f), "ReflectWarm");
            Box(g, "WetReflection_Warm2", new Vector3(-1.8f, 0.212f, 2.9f), new Vector3(1.6f, 0.01f, 0.16f), "ReflectWarm");
            Box(g, "WetReflection_Green", new Vector3(-4.3f, 0.212f, 2.0f), new Vector3(1.1f, 0.01f, 0.14f), "ReflectGreen");
            Box(g, "WetReflection_Blue", new Vector3(0.5f, 0.212f, 2.6f), new Vector3(0.9f, 0.01f, 0.12f), "ReflectBlue");
            Box(g, "WetReflection_Red", new Vector3(4.4f, 0.06f, 5.8f), new Vector3(1.4f, 0.01f, 0.16f), "ReflectRed");
            // 大块湿地反光
            Box(g, "WetGlow_1", new Vector3(-2.8f, 0.214f, 2.1f), new Vector3(3.4f, 0.01f, 0.5f), "ReflectWarm");
            Box(g, "WetGlow_2", new Vector3(-3.2f, 0.06f, 3.55f), new Vector3(1.8f, 0.01f, 0.24f), "ReflectOrange");
            Box(g, "WetGlow_3", new Vector3(-1.0f, 0.06f, 3.8f), new Vector3(1.7f, 0.01f, 0.22f), "ReflectGreen");
            Box(g, "WetGlow_4", new Vector3(0.7f, 0.06f, 4.15f), new Vector3(1.2f, 0.01f, 0.18f), "ReflectBlue");
            Box(g, "WetGlow_5", new Vector3(-2.0f, 0.06f, 4.7f), new Vector3(2.6f, 0.01f, 0.26f), "ReflectWarm");

            // 电线杆与电线
            var pole = Group(g, "UtilityPole", new Vector3(5.8f, 0f, 1.4f));
            Cyl(pole, "Pole", new Vector3(0f, 2.8f, 0f), 0.12f, 5.6f, "Metal");
            Box(pole, "Cross1", new Vector3(0f, 4.9f, 0f), new Vector3(1.8f, 0.08f, 0.08f), "Metal");
            Box(pole, "Cross2", new Vector3(0f, 4.5f, 0f), new Vector3(1.4f, 0.07f, 0.07f), "Metal");
            Wire(g, "Wire1", new Vector3(5.8f, 4.9f, 0.5f), new Vector3(7.2f, 4.6f, -5.6f));
            Wire(g, "Wire2", new Vector3(5.8f, 4.6f, 0.5f), new Vector3(7.2f, 4.2f, -5.6f));
            Wire(g, "Wire3", new Vector3(5.8f, 4.4f, 0.5f), new Vector3(-6.8f, 4.9f, -5.8f));
        }

        static void Puddle(Transform parent, string name, Vector3 pos, Vector2 size)
        {
            var p = Box(parent, name, pos, new Vector3(size.x, 0.012f, size.y), "Puddle");
            // 霓虹反光条（细长的色块）
            Box(parent, name + "_ReflectA", pos + new Vector3(-size.x * 0.15f, 0.008f, 0f), new Vector3(size.x * 0.12f, 0.006f, size.y * 0.75f), "NeonGreen");
            Box(parent, name + "_ReflectB", pos + new Vector3(size.x * 0.22f, 0.008f, 0.04f), new Vector3(size.x * 0.08f, 0.006f, size.y * 0.55f), "NeonYellow");
            Box(parent, name + "_ReflectC", pos + new Vector3(0.05f, 0.009f, -size.y * 0.2f), new Vector3(size.x * 0.28f, 0.005f, size.y * 0.08f), "NeonRed");
        }

        // ================================================================
        //  公寓楼（参考图左侧的多层住宅）
        // ================================================================
        static void BuildApartment(Transform root)
        {
            var g = Group(root, "Apartment", new Vector3(-5.2f, 0.18f, -5.0f));
            const float W = 3.4f, D = 2.8f, H = 7.2f;

            Box(g, "Body", new Vector3(0f, H * 0.5f, 0f), new Vector3(W, H, D), "Apartment");
            Box(g, "Roof", new Vector3(0f, H + 0.08f, 0f), new Vector3(W + 0.18f, 0.16f, D + 0.18f), "ApartmentDark");
            Box(g, "RoofLip", new Vector3(0f, H - 0.15f, D * 0.5f + 0.03f), new Vector3(W + 0.1f, 0.12f, 0.08f), "ApartmentDark");

            // 正面（+Z）三层窗
            for (int f = 0; f < 3; f++)
            {
                float y = 1.65f + f * 2.05f;
                for (int c = 0; c < 2; c++)
                {
                    float x = -0.8f + c * 1.6f;
                    Box(g, "WinFrame_" + f + "_" + c, new Vector3(x, y, D * 0.5f + 0.015f), new Vector3(0.95f, 1.05f, 0.06f), "ApartmentDark");
                    Box(g, "WinGlow_" + f + "_" + c, new Vector3(x, y, D * 0.5f + 0.05f), new Vector3(0.75f, 0.82f, 0.02f), "WindowWarm");
                    Box(g, "WinSill_" + f + "_" + c, new Vector3(x, y - 0.58f, D * 0.5f + 0.12f), new Vector3(1.05f, 0.07f, 0.22f), "ApartmentDark");
                    // 空调外机
                    if (f > 0 && c == 1)
                        Box(g, "ApartmentAC_" + f, new Vector3(x + 0.1f, y - 0.95f, D * 0.5f + 0.2f), new Vector3(0.5f, 0.32f, 0.25f), "White");
                }
            }

            // 二层小阳台
            Box(g, "Balcony", new Vector3(0f, 4.55f, D * 0.5f + 0.45f), new Vector3(2.9f, 0.12f, 0.9f), "ApartmentDark");
            for (int i = 0; i < 5; i++)
                Box(g, "BalconyPost_" + i, new Vector3(-1.2f + i * 0.6f, 4.85f, D * 0.5f + 0.86f), new Vector3(0.04f, 0.6f, 0.04f), "Metal");
            Box(g, "BalconyRail", new Vector3(0f, 5.15f, D * 0.5f + 0.86f), new Vector3(2.9f, 0.05f, 0.05f), "Metal");
            Box(g, "BalconyDoor", new Vector3(0.55f, 4.15f, D * 0.5f + 0.02f), new Vector3(0.8f, 1.5f, 0.04f), "WindowWarm");

            // 侧面（+X）窗
            for (int f = 0; f < 2; f++)
            {
                float y = 2.2f + f * 2.2f;
                Box(g, "SideWin_" + f, new Vector3(W * 0.5f + 0.03f, y, 0.45f), new Vector3(0.05f, 0.8f, 0.75f), "WindowWarm");
                Box(g, "SideWinFrame_" + f, new Vector3(W * 0.5f + 0.01f, y, 0.45f), new Vector3(0.03f, 0.95f, 0.9f), "ApartmentDark");
            }

            // 一层入口和小雨棚
            Box(g, "GroundDoor", new Vector3(-0.45f, 1.0f, D * 0.5f + 0.03f), new Vector3(0.75f, 1.6f, 0.05f), "ApartmentDark");
            Box(g, "GroundAwning", new Vector3(-0.45f, 2.05f, D * 0.5f + 0.42f), new Vector3(1.5f, 0.07f, 0.8f), "StoreGreen");
            Box(g, "GroundLamp", new Vector3(0.65f, 2.35f, D * 0.5f + 0.12f), new Vector3(0.16f, 0.22f, 0.12f), "WarmLight");

            // 外墙管线和屋顶水箱
            Cyl(g, "Pipe1", new Vector3(W * 0.5f + 0.05f, 3.2f, -0.9f), 0.035f, 6.2f, "Metal");
            Cyl(g, "Pipe2", new Vector3(W * 0.5f + 0.08f, 5.3f, -0.9f), 0.045f, 1.2f, "Metal");
            Cyl(g, "WaterTank", new Vector3(-0.9f, H + 0.55f, -0.6f), 0.35f, 0.9f, "White");
            Box(g, "RoofAC", new Vector3(0.9f, H + 0.3f, -0.5f), new Vector3(0.6f, 0.4f, 0.45f), "White");
        }

        // ================================================================
        //  便利店
        // ================================================================
        static void BuildStore(Transform root)
        {
            var store = Group(root, "Store", new Vector3(-2.4f, 0.18f, -1.9f));
            const float W = 4.8f, D = 3.8f, H = 3.0f;

            // 外壳
            Box(store, "BackWall", new Vector3(0f, H * 0.5f, -D * 0.5f), new Vector3(W, H, 0.12f), "StoreWall");
            Box(store, "LeftWall", new Vector3(-W * 0.5f, H * 0.5f, 0f), new Vector3(0.12f, H, D), "StoreWall");
            Box(store, "RightWall", new Vector3(W * 0.5f, H * 0.5f, 0f), new Vector3(0.12f, H, D), "StoreWall");
            Box(store, "Roof", new Vector3(0f, H + 0.05f, 0f), new Vector3(W + 0.2f, 0.16f, D + 0.2f), "StoreDark");
            Box(store, "FrontTop", new Vector3(0f, H - 0.18f, D * 0.5f), new Vector3(W, 0.36f, 0.12f), "StoreGreen");
            // 店面底部绿色墙裙和立柱
            Box(store, "FrontKickGreen", new Vector3(0f, 0.32f, D * 0.5f + 0.04f), new Vector3(W, 0.55f, 0.06f), "StoreGreen");
            Box(store, "FrontPillar_L", new Vector3(-W * 0.5f + 0.08f, 1.5f, D * 0.5f + 0.05f), new Vector3(0.24f, 2.8f, 0.08f), "StoreGreen");
            Box(store, "FrontPillar_R", new Vector3(W * 0.5f - 0.08f, 1.5f, D * 0.5f + 0.05f), new Vector3(0.24f, 2.8f, 0.08f), "StoreGreen");

            // 前玻璃幕墙
            Box(store, "Glass_L", new Vector3(-1.45f, 1.35f, D * 0.5f + 0.02f), new Vector3(1.8f, 2.45f, 0.035f), "Glass");
            Box(store, "Glass_R", new Vector3(1.45f, 1.35f, D * 0.5f + 0.02f), new Vector3(1.8f, 2.45f, 0.035f), "Glass");
            Box(store, "FrameMidL", new Vector3(-0.62f, 1.45f, D * 0.5f + 0.03f), new Vector3(0.07f, 2.6f, 0.06f), "Frame");
            Box(store, "FrameMidR", new Vector3(0.62f, 1.45f, D * 0.5f + 0.03f), new Vector3(0.07f, 2.6f, 0.06f), "Frame");
            Box(store, "FrameL", new Vector3(-W * 0.5f + 0.03f, 1.45f, D * 0.5f + 0.03f), new Vector3(0.08f, 2.6f, 0.06f), "Frame");
            Box(store, "FrameR", new Vector3(W * 0.5f - 0.03f, 1.45f, D * 0.5f + 0.03f), new Vector3(0.08f, 2.6f, 0.06f), "Frame");
            Box(store, "KickPanel", new Vector3(0f, 0.2f, D * 0.5f + 0.03f), new Vector3(W, 0.4f, 0.08f), "Frame");

            // 自动门（两片玻璃）
            var doorRoot = Group(store, "AutoDoor", new Vector3(0f, 0f, D * 0.5f + 0.08f));
            var left = Box(doorRoot, "Door_L", new Vector3(-0.31f, 1.35f, 0f), new Vector3(0.6f, 2.35f, 0.035f), "Glass");
            var right = Box(doorRoot, "Door_R", new Vector3(0.31f, 1.35f, 0f), new Vector3(0.6f, 2.35f, 0.035f), "Glass");
            Box(doorRoot, "DoorFrame_L", new Vector3(-0.62f, 1.35f, 0f), new Vector3(0.035f, 2.4f, 0.06f), "Frame");
            Box(doorRoot, "DoorFrame_R", new Vector3(0.62f, 1.35f, 0f), new Vector3(0.035f, 2.4f, 0.06f), "Frame");
            var doorAnim = doorRoot.gameObject.AddComponent<DioramaAutoDoor>();
            doorAnim.LeftPanel = left.transform;
            doorAnim.RightPanel = right.transform;

            // 门垫
            Box(store, "DoorMat", new Vector3(0f, 0.02f, D * 0.5f + 0.5f), new Vector3(1.25f, 0.03f, 0.75f), "StoreDark");

            // 屋檐雨棚
            Box(store, "Canopy", new Vector3(0f, 2.72f, D * 0.5f + 0.62f), new Vector3(W + 0.5f, 0.08f, 1.25f), "StoreGreen");
            // 雨棚边缘的米白条纹，增加便利店识别度
            for (int i = 0; i < 7; i++)
                Box(store, "CanopyStripe_" + i, new Vector3(-2.2f + i * 0.73f, 2.66f, D * 0.5f + 1.2f), new Vector3(0.36f, 0.05f, 0.08f),
                    i % 2 == 0 ? "SignCream" : "StoreGreenLight");
            Cyl(store, "CanopyPost_L", new Vector3(-2.1f, 1.35f, D * 0.5f + 1.1f), 0.045f, 2.7f, "Metal");
            Cyl(store, "CanopyPost_R", new Vector3(2.1f, 1.35f, D * 0.5f + 1.1f), 0.045f, 2.7f, "Metal");

            // 招牌：米白底 + 墨绿边 + 竖式 24 小时灯箱
            Box(store, "SignBorder", new Vector3(0f, 3.35f, D * 0.5f + 0.10f), new Vector3(4.86f, 0.86f, 0.14f), "StoreGreen");
            Box(store, "SignBoard", new Vector3(0f, 3.35f, D * 0.5f + 0.18f), new Vector3(4.65f, 0.68f, 0.06f), "SignCream");
            Text3D(store, "HOSHIMI MART", new Vector3(0f, 3.35f, D * 0.5f + 0.23f), 0.058f, new Color(0.03f, 0.20f, 0.10f), Quaternion.Euler(0f, 180f, 0f));
            Box(store, "SignUnderline", new Vector3(0f, 3.02f, D * 0.5f + 0.2f), new Vector3(4.3f, 0.06f, 0.12f), "NeonGreen");
            Box(store, "VerticalSignBorder", new Vector3(2.32f, 2.35f, D * 0.5f + 0.07f), new Vector3(0.48f, 1.45f, 0.12f), "StoreGreen");
            Box(store, "VerticalSign", new Vector3(2.32f, 2.35f, D * 0.5f + 0.15f), new Vector3(0.38f, 1.32f, 0.05f), "SignCream");
            Text3D(store, "24", new Vector3(2.32f, 2.5f, D * 0.5f + 0.2f), 0.11f, new Color(0.03f, 0.22f, 0.10f), Quaternion.Euler(0f, 180f, 0f));
            Text3D(store, "H", new Vector3(2.32f, 2.1f, D * 0.5f + 0.2f), 0.09f, new Color(0.03f, 0.22f, 0.10f), Quaternion.Euler(0f, 180f, 0f));

            // 左侧墙的暖窗和海报，避免大块空白
            Box(store, "SideWindow", new Vector3(-W * 0.5f - 0.07f, 1.65f, 0.35f), new Vector3(0.03f, 1.05f, 1.1f), "WarmGlass");
            Box(store, "SideWindowFrame", new Vector3(-W * 0.5f - 0.09f, 1.65f, 0.35f), new Vector3(0.02f, 1.18f, 1.22f), "Frame");
            Box(store, "SidePoster", new Vector3(-W * 0.5f - 0.09f, 1.7f, -1.0f), new Vector3(0.02f, 0.8f, 0.65f), "ProductPink");

            // 门头上的 OPEN 牌（贴在玻璃上方，不悬空）
            Box(store, "DoorSignBoard", new Vector3(0f, 2.4f, D * 0.5f + 0.08f), new Vector3(1.05f, 0.32f, 0.06f), "Frame");
            Text3D(store, "OPEN", new Vector3(0f, 2.4f, D * 0.5f + 0.13f), 0.06f, Color.white, Quaternion.Euler(0f, 180f, 0f));

            // ---------------- 店内 ----------------
            var inner = Group(store, "Interior");
            Box(inner, "Floor", new Vector3(0f, 0.08f, 0f), new Vector3(W - 0.35f, 0.08f, D - 0.35f), "InteriorFloor");
            // 店内后墙暖光背板，让货架像剪影一样可读
            Box(inner, "BackGlow", new Vector3(0f, 1.55f, -1.76f), new Vector3(4.25f, 2.45f, 0.03f), "WarmLight");
            Box(inner, "CeilingPanel_1", new Vector3(-1.2f, 2.9f, -0.6f), new Vector3(0.85f, 0.035f, 0.85f), "WarmLight");
            Box(inner, "CeilingPanel_2", new Vector3(0.1f, 2.9f, -0.6f), new Vector3(0.85f, 0.035f, 0.85f), "WarmLight");
            Box(inner, "CeilingPanel_3", new Vector3(1.3f, 2.9f, -0.6f), new Vector3(0.85f, 0.035f, 0.85f), "WarmLight");
            Box(inner, "CeilingPanel_4", new Vector3(-0.6f, 2.9f, 1.1f), new Vector3(0.85f, 0.035f, 0.85f), "WarmLight");
            Box(inner, "CeilingPanel_5", new Vector3(0.8f, 2.9f, 1.1f), new Vector3(0.85f, 0.035f, 0.85f), "WarmLight");

            // 货架（后墙）
            BuildShelf(inner, "Shelf_A", new Vector3(-1.25f, 0f, -1.5f), 1.45f, 3);
            BuildShelf(inner, "Shelf_B", new Vector3(0.55f, 0f, -1.5f), 1.45f, 3);
            BuildShelf(inner, "Shelf_C", new Vector3(2.0f, 0f, -1.2f), 0.8f, 3, true);

            // 饮料冷柜（右侧墙）
            for (int i = 0; i < 3; i++)
            {
                var z = -1.0f + i * 1.0f;
                Box(inner, "Fridge_" + i, new Vector3(2.12f, 1.25f, z), new Vector3(0.62f, 2.1f, 0.9f), "Frame");
                Box(inner, "FridgeGlass_" + i, new Vector3(2.45f, 1.25f, z), new Vector3(0.03f, 1.85f, 0.82f), "CoolLight");
                for (int k = 0; k < 3; k++)
                    Box(inner, "FridgeShelf_" + i + "_" + k, new Vector3(2.2f, 0.55f + k * 0.45f, z), new Vector3(0.4f, 0.03f, 0.75f), "Shelf");
            }

            // 收银台（往里挪，避免挡住整面玻璃）
            Box(inner, "Counter", new Vector3(-1.55f, 0.5f, 0.15f), new Vector3(1.7f, 0.95f, 0.5f), "Counter");
            Box(inner, "CounterTop", new Vector3(-1.55f, 1.0f, 0.15f), new Vector3(1.8f, 0.08f, 0.6f), "Shelf");
            Box(inner, "Register", new Vector3(-1.05f, 1.15f, 0.1f), new Vector3(0.4f, 0.3f, 0.3f), "Frame");
            Box(inner, "CoffeeMachine", new Vector3(-1.9f, 1.28f, 0.2f), new Vector3(0.32f, 0.55f, 0.32f), "Metal");
            Box(inner, "Oden", new Vector3(-1.95f, 1.1f, -0.2f), new Vector3(0.5f, 0.25f, 0.45f), "Metal");
            for (int i = 0; i < 4; i++)
                Cyl(inner, "OdenSkewer_" + i, new Vector3(-2.1f + i * 0.1f, 1.28f, -0.2f), 0.018f, 0.3f, "NeonYellow");

            // 杂志架、海报
            Box(inner, "MagazineRack", new Vector3(1.45f, 0.65f, 0.15f), new Vector3(0.8f, 1.15f, 0.4f), "Shelf");
            for (int i = 0; i < 4; i++)
                Box(inner, "Magazine_" + i, new Vector3(1.25f + i * 0.1f, 0.9f - i * 0.16f, 0.38f), new Vector3(0.28f, 0.32f, 0.04f),
                    i % 2 == 0 ? "ProductPink" : "ProductYellow");
            Box(inner, "Poster_A", new Vector3(-1.8f, 1.9f, -1.84f), new Vector3(0.75f, 0.65f, 0.04f), "NeonRed");
            Box(inner, "Poster_B", new Vector3(-0.9f, 1.9f, -1.84f), new Vector3(0.65f, 0.55f, 0.04f), "NeonBlue");
            Box(inner, "Poster_C", new Vector3(1.2f, 1.85f, -1.84f), new Vector3(0.55f, 0.75f, 0.04f), "NeonYellow");
            Box(inner, "Lightbox", new Vector3(2.1f, 2.45f, 0.2f), new Vector3(0.04f, 0.55f, 1.2f), "WarmLight");
            Box(inner, "BackDoor", new Vector3(1.9f, 1.15f, -1.78f), new Vector3(0.65f, 2.1f, 0.06f), "Frame");

            // 地贴导视
            Box(store, "FloorGuide", new Vector3(0f, 0.13f, 0.8f), new Vector3(0.12f, 0.01f, 1.2f), "NeonGreen");

            // 屋顶设备，让剪影更丰富
            var roof = Group(store, "RoofProps");
            Box(roof, "RooftopAC", new Vector3(1.35f, 3.28f, -0.9f), new Vector3(0.75f, 0.42f, 0.55f), "White");
            Cyl(roof, "RooftopFan", new Vector3(1.35f, 3.5f, -0.63f), 0.18f, 0.03f, "Metal", Quaternion.Euler(90f, 0f, 0f));
            Cyl(roof, "VentPipe1", new Vector3(-1.4f, 3.4f, -1.0f), 0.08f, 0.55f, "Metal");
            Cyl(roof, "VentPipe2", new Vector3(-1.1f, 3.3f, -0.7f), 0.06f, 0.35f, "Metal");
            Box(roof, "RoofRailing_F", new Vector3(0f, 3.35f, 1.9f), new Vector3(4.6f, 0.05f, 0.05f), "Metal");
            Cyl(roof, "Antenna", new Vector3(-2.0f, 3.9f, -1.4f), 0.022f, 1.2f, "Metal");
        }

        static void BuildShelf(Transform parent, string name, Vector3 pos, float width, int levels, bool side = false)
        {
            var g = Group(parent, name, pos);
            Box(g, "Back", new Vector3(0f, 0.95f, 0f), new Vector3(width, 1.9f, 0.08f), "Shelf");
            Box(g, "SideL", new Vector3(-width * 0.5f, 0.95f, 0.24f), new Vector3(0.06f, 1.9f, 0.5f), "Shelf");
            Box(g, "SideR", new Vector3(width * 0.5f, 0.95f, 0.24f), new Vector3(0.06f, 1.9f, 0.5f), "Shelf");
            for (int l = 0; l < levels; l++)
            {
                float y = 0.3f + l * 0.55f;
                Box(g, "Board_" + l, new Vector3(0f, y, 0.25f), new Vector3(width, 0.04f, 0.52f), "Shelf");
                int count = side ? 3 : 5;
                for (int i = 0; i < count; i++)
                {
                    string[] products = { "ProductRed", "ProductOrange", "ProductYellow", "ProductGreen", "ProductBlue", "ProductPink", "ProductPurple", "ProductCyan" };
                    Box(g, "Product_" + l + "_" + i, new Vector3(-width * 0.5f + 0.15f + i * (width - 0.3f) / Mathf.Max(1, count - 1), y + 0.12f, 0.32f),
                        new Vector3(0.13f, 0.18f, 0.2f), products[(l * 3 + i) % products.Length]);
                }
            }
        }

        // ================================================================
        //  外部道具
        // ================================================================
        static void BuildExterior(Transform root)
        {
            var g = Group(root, "Exterior");

            // 自动贩卖机
            for (int i = 0; i < 2; i++)
            {
                float x = -4.55f + i * 1.05f;
                Box(g, "Vending_" + i, new Vector3(x, 1.05f, 0.75f), new Vector3(0.9f, 1.85f, 0.6f), "SignCream");
                Box(g, "VendingPanel_" + i, new Vector3(x, 1.2f, 1.08f), new Vector3(0.75f, 1.1f, 0.03f),
                    i == 0 ? "VendingOrange" : "VendingGreen");
                Box(g, "VendingPanel2_" + i, new Vector3(x, 1.85f, 1.08f), new Vector3(0.75f, 0.3f, 0.03f), "SignCream");
                Box(g, "VendingTop_" + i, new Vector3(x, 2.02f, 1.06f), new Vector3(0.85f, 0.12f, 0.06f), "StoreGreen");
                for (int k = 0; k < 3; k++)
                    Box(g, "VendingRow_" + i + "_" + k, new Vector3(x, 0.65f + k * 0.22f, 1.1f), new Vector3(0.7f, 0.035f, 0.02f), "Shelf");
                for (int k = 0; k < 4; k++)
                    Box(g, "VendingLight_" + i + "_" + k, new Vector3(x - 0.28f + k * 0.19f, 0.95f, 1.105f), new Vector3(0.13f, 0.16f, 0.01f),
                        k % 2 == 0 ? "ProductYellow" : "ProductCyan");
            }

            // 户外杂志/宣传架
            var outdoor = Group(g, "OutdoorRack", new Vector3(1.7f, 0.18f, 1.3f));
            Box(outdoor, "Frame", new Vector3(0f, 0.5f, 0f), new Vector3(1.2f, 1.0f, 0.35f), "StoreGreen");
            for (int i = 0; i < 4; i++)
                Box(outdoor, "Cover_" + i, new Vector3(-0.4f + i * 0.26f, 0.62f, 0.2f), new Vector3(0.22f, 0.32f, 0.03f),
                    i % 2 == 0 ? "ProductPink" : "ProductYellow");

            // 蓝色小货车（参考图右侧停车位）
            var van = Group(g, "ParkedVan", new Vector3(4.85f, 0.18f, 0.9f));
            Box(van, "Body", new Vector3(0f, 0.72f, 0f), new Vector3(2.25f, 0.95f, 1.25f), "VanBlue");
            Box(van, "Cabin", new Vector3(-0.25f, 1.35f, 0f), new Vector3(1.55f, 0.62f, 1.18f), "VanBlue");
            Box(van, "Windshield", new Vector3(0.55f, 1.3f, 0f), new Vector3(0.04f, 0.55f, 1.0f), "VanGlass");
            Box(van, "SideWindow_L", new Vector3(-0.35f, 1.35f, 0.6f), new Vector3(1.15f, 0.42f, 0.04f), "VanGlass");
            Box(van, "SideWindow_R", new Vector3(-0.35f, 1.35f, -0.6f), new Vector3(1.15f, 0.42f, 0.04f), "VanGlass");
            Box(van, "Bumper_F", new Vector3(1.15f, 0.38f, 0f), new Vector3(0.12f, 0.22f, 1.2f), "Metal");
            Box(van, "Headlight_L", new Vector3(1.18f, 0.65f, 0.38f), new Vector3(0.05f, 0.16f, 0.22f), "WarmLight");
            Box(van, "Headlight_R", new Vector3(1.18f, 0.65f, -0.38f), new Vector3(0.05f, 0.16f, 0.22f), "WarmLight");
            Box(van, "Taillight_L", new Vector3(-1.13f, 0.65f, 0.38f), new Vector3(0.04f, 0.12f, 0.18f), "NeonRed");
            Box(van, "Taillight_R", new Vector3(-1.13f, 0.65f, -0.38f), new Vector3(0.04f, 0.12f, 0.18f), "NeonRed");
            Cyl(van, "Wheel_FL", new Vector3(0.72f, 0.34f, 0.63f), 0.3f, 0.2f, "ApartmentDark", Quaternion.Euler(0f, 0f, 90f));
            Cyl(van, "Wheel_FR", new Vector3(0.72f, 0.34f, -0.63f), 0.3f, 0.2f, "ApartmentDark", Quaternion.Euler(0f, 0f, 90f));
            Cyl(van, "Wheel_BL", new Vector3(-0.72f, 0.34f, 0.63f), 0.3f, 0.2f, "ApartmentDark", Quaternion.Euler(0f, 0f, 90f));
            Cyl(van, "Wheel_BR", new Vector3(-0.72f, 0.34f, -0.63f), 0.3f, 0.2f, "ApartmentDark", Quaternion.Euler(0f, 0f, 90f));
            var vanLight = new GameObject("VanHeadlight");
            vanLight.transform.SetParent(van, false);
            vanLight.transform.localPosition = new Vector3(1.35f, 0.55f, 0f);
            var vl = vanLight.AddComponent<Light>();
            vl.type = LightType.Point;
            vl.color = new Color(0.75f, 0.90f, 1.0f);
            vl.intensity = 2.2f;
            vl.range = 3.5f;

            // 自行车（简化）
            var bike = Group(g, "Bicycle", new Vector3(-5.6f, 0.18f, 1.9f));
            Cyl(bike, "Wheel_L", new Vector3(0f, 0.35f, 0f), 0.34f, 0.035f, "Metal", Quaternion.Euler(0f, 0f, 90f));
            Cyl(bike, "Wheel_R", new Vector3(0.95f, 0.35f, 0f), 0.34f, 0.035f, "Metal", Quaternion.Euler(0f, 0f, 90f));
            Wire(bike, "Frame1", new Vector3(0f, 0.35f, 0f), new Vector3(0.45f, 0.75f, 0f), 0.035f);
            Wire(bike, "Frame2", new Vector3(0.45f, 0.75f, 0f), new Vector3(0.95f, 0.35f, 0f), 0.035f);
            Wire(bike, "Frame3", new Vector3(0f, 0.35f, 0f), new Vector3(0.55f, 0.35f, 0f), 0.035f);
            Box(bike, "Basket", new Vector3(0.95f, 0.62f, 0f), new Vector3(0.25f, 0.22f, 0.3f), "Metal");

            // 伞架
            var umb = Group(g, "UmbrellaStand", new Vector3(-1.15f, 0.18f, 1.25f));
            Cyl(umb, "Bucket", new Vector3(0f, 0.22f, 0f), 0.18f, 0.44f, "Metal");
            Cyl(umb, "Umbrella1", new Vector3(-0.05f, 0.55f, 0f), 0.035f, 0.8f, "NeonRed", Quaternion.Euler(0f, 0f, 12f));
            Cyl(umb, "Umbrella2", new Vector3(0.08f, 0.55f, 0.04f), 0.035f, 0.75f, "NeonBlue", Quaternion.Euler(0f, 0f, -10f));

            // 垃圾桶
            Cyl(g, "Trash_1", new Vector3(-0.55f, 0.42f, 0.95f), 0.25f, 0.78f, "Trash");
            Cyl(g, "Trash_2", new Vector3(0.05f, 0.42f, 0.95f), 0.22f, 0.65f, "Metal");

            // 街灯
            var lamp = Group(g, "StreetLamp", new Vector3(1.85f, 0.18f, 2.55f));
            Cyl(lamp, "Pole", new Vector3(0f, 2.3f, 0f), 0.085f, 4.6f, "Metal");
            Box(lamp, "Arm", new Vector3(-0.45f, 4.55f, 0f), new Vector3(0.9f, 0.07f, 0.07f), "Metal");
            Box(lamp, "Head", new Vector3(-0.9f, 4.45f, 0f), new Vector3(0.35f, 0.12f, 0.28f), "CoolLight");

            // 路牌
            var sign = Group(g, "RoadSign", new Vector3(1.0f, 0.18f, 2.8f));
            Cyl(sign, "Pole", new Vector3(0f, 1.25f, 0f), 0.035f, 2.5f, "Metal");
            Box(sign, "Board", new Vector3(0f, 2.15f, 0f), new Vector3(0.7f, 0.28f, 0.04f), "White");
            Box(sign, "BoardColor", new Vector3(0f, 2.15f, 0.025f), new Vector3(0.62f, 0.2f, 0.01f), "NeonBlue");

            // 护栏（前侧路缘）
            for (int i = 0; i < 5; i++)
            {
                float x = -6.2f + i * 1.35f;
                Cyl(g, "RailPost_" + i, new Vector3(x, 0.45f, 3.18f), 0.035f, 0.9f, "Metal");
            }
            Box(g, "RailTop", new Vector3(-3.5f, 0.82f, 3.18f), new Vector3(6.0f, 0.05f, 0.05f), "Metal");

            // 公告栏
            var board = Group(g, "NoticeBoard", new Vector3(-6.0f, 0.18f, 1.1f));
            Cyl(board, "LegL", new Vector3(-0.65f, 0.7f, 0f), 0.035f, 1.4f, "Metal");
            Cyl(board, "LegR", new Vector3(0.65f, 0.7f, 0f), 0.035f, 1.4f, "Metal");
            Box(board, "Board", new Vector3(0f, 1.35f, 0f), new Vector3(1.5f, 0.9f, 0.06f), "Frame");
            Box(board, "Poster1", new Vector3(-0.38f, 1.35f, 0.04f), new Vector3(0.5f, 0.65f, 0.01f), "NeonYellow");
            Box(board, "Poster2", new Vector3(0.35f, 1.35f, 0.04f), new Vector3(0.5f, 0.6f, 0.01f), "NeonBlue");

            // 空调外机
            var ac = Group(g, "AC_StoreRight", new Vector3(0.55f, 0.18f, -2.2f));
            Box(ac, "Body", new Vector3(0f, 0.65f, 0f), new Vector3(0.9f, 0.6f, 0.4f), "White");
            Cyl(ac, "Fan", new Vector3(0.05f, 0.65f, 0.21f), 0.22f, 0.03f, "Metal", Quaternion.Euler(90f, 0f, 0f));
            Box(ac, "Grill", new Vector3(0.05f, 0.65f, 0.23f), new Vector3(0.38f, 0.38f, 0.01f), "Frame");

            // 右侧巷口
            var alley = Group(g, "Alley", new Vector3(0.9f, 0.18f, -2.0f));
            Box(alley, "Floor", new Vector3(0f, 0.02f, 0f), new Vector3(0.9f, 0.04f, 3.8f), "StoreDark");
            Box(alley, "Wall_R", new Vector3(0.48f, 1.3f, 0f), new Vector3(0.1f, 2.6f, 3.8f), "StoreDark");
            Box(alley, "Sign", new Vector3(0f, 1.9f, 1.8f), new Vector3(0.55f, 0.65f, 0.05f), "NeonRed");

            // 停车位附近的小锥桶
            Cyl(g, "Cone", new Vector3(4.2f, 0.3f, 1.6f), 0.13f, 0.55f, "NeonYellow");

            // 远处交通灯
            var tl = Group(g, "TrafficLight", new Vector3(5.2f, 0.18f, 3.2f));
            Cyl(tl, "Pole", new Vector3(0f, 1.9f, 0f), 0.07f, 3.8f, "Metal");
            Box(tl, "Body", new Vector3(0f, 2.9f, 0.3f), new Vector3(0.22f, 0.75f, 0.2f), "Frame");
            Sphere(tl, "Red", new Vector3(0f, 3.15f, 0.42f), 0.12f, "NeonRed");
            Sphere(tl, "Yellow", new Vector3(0f, 2.92f, 0.42f), 0.12f, "NeonYellow");
            Sphere(tl, "Green", new Vector3(0f, 2.69f, 0.42f), 0.12f, "NeonGreen");
        }

        // ================================================================
        //  灯光
        // ================================================================
        static void BuildLights(Transform root)
        {
            var g = Group(root, "Lights");
            var moon = new GameObject("MoonKey");
            moon.transform.SetParent(g, false);
            moon.transform.rotation = Quaternion.Euler(42f, -32f, 0f);
            var ml = moon.AddComponent<Light>();
            ml.type = LightType.Directional;
            ml.color = new Color(0.58f, 0.70f, 1.0f);
            ml.intensity = 0.80f;
            ml.shadows = LightShadows.Soft;
            RenderSettings.sun = ml;

            var cool = new GameObject("StreetFill");
            cool.transform.SetParent(g, false);
            cool.transform.position = new Vector3(1.8f, 4.3f, 2.5f);
            var cl = cool.AddComponent<Light>();
            cl.type = LightType.Point;
            cl.color = new Color(0.55f, 0.72f, 1.0f);
            cl.intensity = 3.0f;
            cl.range = 9f;

            // 正前方补光：把底座/斑马线/店前道具从黑里拉出来
            var front = new GameObject("FrontFill");
            front.transform.SetParent(g, false);
            front.transform.position = new Vector3(-1.5f, 3.8f, 4.6f);
            var fr = front.AddComponent<Light>();
            fr.type = LightType.Point;
            fr.color = new Color(0.72f, 0.82f, 1.0f);
            fr.intensity = 6.0f;
            fr.range = 13f;

            var warm = new GameObject("StoreGlow");
            warm.transform.SetParent(g, false);
            warm.transform.position = new Vector3(-2.4f, 2.4f, -0.2f);
            var wl = warm.AddComponent<Light>();
            wl.type = LightType.Point;
            wl.color = new Color(1.0f, 0.78f, 0.52f);
            wl.intensity = 12.0f;
            wl.range = 10f;

            var glow = new GameObject("StoreGlow2");
            glow.transform.SetParent(g, false);
            glow.transform.position = new Vector3(-0.8f, 2.3f, 0.0f);
            var gl = glow.AddComponent<Light>();
            gl.type = LightType.Point;
            gl.color = new Color(1.0f, 0.86f, 0.62f);
            gl.intensity = 7.0f;
            gl.range = 7f;

            var frontGlow = new GameObject("StoreFrontGlow");
            frontGlow.transform.SetParent(g, false);
            frontGlow.transform.position = new Vector3(-2.4f, 2.2f, 1.7f);
            var fg = frontGlow.AddComponent<Light>();
            fg.type = LightType.Point;
            fg.color = new Color(1.0f, 0.82f, 0.54f);
            fg.intensity = 6.0f;
            fg.range = 6f;

            var lamp = new GameObject("LampLight");
            lamp.transform.SetParent(g, false);
            lamp.transform.position = new Vector3(0.95f, 4.45f, 2.55f);
            var ll = lamp.AddComponent<Light>();
            ll.type = LightType.Point;
            ll.color = new Color(0.72f, 0.86f, 1.0f);
            ll.intensity = 6.0f;
            ll.range = 8f;

            // 招牌闪烁
            var sign = GameObject.Find("DioramaRoot/Lights");
            // 由 BuildStore 生成的招牌 Renderer 通过名称查找
            var signBoard = GameObject.Find("SignBoard");
            var signRenderer = signBoard != null ? signBoard.GetComponent<Renderer>() : null;
            var flickerGo = new GameObject("SignFlicker");
            flickerGo.transform.SetParent(g, false);
            var fl = flickerGo.AddComponent<DioramaFlicker>();
            fl.Lamp = ll;
            if (signRenderer != null) fl.Targets = new Renderer[] { signRenderer };
            fl.BaseIntensity = 5.0f;

            // 交通灯灯光
            var tl = GameObject.Find("TrafficLight");
            if (tl != null)
            {
                var tlc = tl.AddComponent<DioramaTrafficLight>();
                tlc.RedLight = MakeLight(tl.transform, "RedLight", new Vector3(0f, 3.15f, 0.55f), new Color(1f, 0.2f, 0.2f), 3f);
                tlc.YellowLight = MakeLight(tl.transform, "YellowLight", new Vector3(0f, 2.92f, 0.55f), new Color(1f, 0.8f, 0.2f), 0.1f);
                tlc.GreenLight = MakeLight(tl.transform, "GreenLight", new Vector3(0f, 2.69f, 0.55f), new Color(0.2f, 1f, 0.4f), 0.1f);
            }
        }

        static Light MakeLight(Transform parent, string name, Vector3 pos, Color color, float intensity)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = color;
            l.intensity = intensity;
            l.range = 2.5f;
            return l;
        }

        // ================================================================
        //  雨 / 涟漪 / 相机 / 后处理
        // ================================================================
        static void BuildRain(Transform root)
        {
            var fx = Group(root, "FX");
            var rain = new GameObject("Rain");
            rain.transform.SetParent(fx, false);
            rain.transform.localPosition = new Vector3(0f, 7.2f, 0f);
            var ps = rain.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime = 1.15f;
            main.startSpeed = 19f;
            main.startSize = 0.008f;
            main.startColor = new Color(0.62f, 0.78f, 1.0f, 0.30f);
            main.maxParticles = 1800;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0.15f;
            var emission = ps.emission;
            emission.rateOverTime = 180f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(14.5f, 0.6f, 14.5f);
            var renderer = rain.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.22f;
            renderer.lengthScale = 0.75f;
            renderer.material = M("Rain");

            // 屋檐滴水
            var drip = new GameObject("CanopyDrips");
            drip.transform.SetParent(fx, false);
            drip.transform.localPosition = new Vector3(-2.4f, 2.9f, 1.0f);
            var dp = drip.AddComponent<ParticleSystem>();
            var dm = dp.main;
            dm.startLifetime = 0.8f;
            dm.startSpeed = 2.8f;
            dm.startSize = 0.025f;
            dm.startColor = new Color(0.7f, 0.85f, 1f, 0.6f);
            dm.maxParticles = 80;
            var de = dp.emission; de.rateOverTime = 12f;
            var ds = dp.shape; ds.shapeType = ParticleSystemShapeType.Box; ds.scale = new Vector3(3.8f, 0.02f, 0.1f);
            var dr = drip.GetComponent<ParticleSystemRenderer>(); dr.renderMode = ParticleSystemRenderMode.Stretch; dr.lengthScale = 2f; dr.velocityScale = .3f; dr.material = M("Rain");
        }

        static void BuildRipples(Transform root)
        {
            var fx = Group(root, "Ripples");
            var rings = new LineRenderer[4];
            Vector3[] centers = {
                new Vector3(-4.8f, 0.04f, 1.9f), new Vector3(0.4f, 0.04f, 2.4f),
                new Vector3(4.3f, 0.04f, 3.7f), new Vector3(-1.8f, 0.04f, 5.7f)
            };
            for (int i = 0; i < centers.Length; i++)
            {
                var go = new GameObject("Ripple_" + i);
                go.transform.SetParent(fx, false);
                go.transform.position = centers[i];
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = false;
                lr.loop = true;
                lr.positionCount = 32;
                lr.startWidth = 0.018f;
                lr.endWidth = 0.018f;
                lr.material = M("Ripple");
                for (int p = 0; p < 32; p++)
                {
                    float a = p / 32f * Mathf.PI * 2f;
                    lr.SetPosition(p, new Vector3(Mathf.Cos(a) * 0.45f, 0f, Mathf.Sin(a) * 0.45f));
                }
                rings[i] = lr;
            }
            var comp = fx.gameObject.AddComponent<DioramaRipples>();
            comp.Rings = rings;
        }

        static void BuildCamera(Transform root)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.065f, 0.095f, 0.155f);
            cam.fieldOfView = 34f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 80f;
            go.AddComponent<AudioListener>();
            var orbit = go.AddComponent<DioramaOrbitCamera>();
            orbit.Target = new Vector3(0f, 2.8f, 0f);
            orbit.Distance = 19.5f;
            orbit.Yaw = 208f;
            orbit.Pitch = 26f;
            go.transform.position = new Vector3(-10f, 6f, 14f);
            go.transform.LookAt(orbit.Target);
        }

        static void BuildPostFx(Transform root)
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, RootDir + "/DioramaVolume.asset");
            var bloom = profile.Add<Bloom>();
            bloom.threshold.value = 0.85f;
            bloom.intensity.value = 1.3f;
            bloom.scatter.value = 0.72f;
            var vig = profile.Add<Vignette>();
            vig.intensity.value = 0.32f;
            vig.smoothness.value = 0.45f;
            var ca = profile.Add<ColorAdjustments>();
            ca.postExposure.value = 0.45f;
            ca.contrast.value = 10f;
            ca.saturation.value = -6f;
            ca.colorFilter.value = new Color(0.92f, 0.96f, 1.0f);
            var tone = profile.Add<Tonemapping>();
            tone.mode.value = TonemappingMode.ACES;

            var go = new GameObject("Global Volume");
            var vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.profile = profile;
        }
    }
}
