using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Yixuan.Presentation;
using Yixuan.Rendering;
using Yixuan.World;

namespace Yixuan.EditorTools
{
    /// <summary>
    /// 把 SovietBlueHour 大地图接进「任务验收场景」的可重复构建器。
    ///
    /// 思路（不重建整个场景，保留角色 / UI / AI / 语音 / 视觉全部组件）：
    ///   1. 打开旧场景 RainyConvenience_TaskPlan.unity，另存为 SovietBlueHour_TaskPlan.unity；
    ///   2. 删除旧微缩景观 RainyConvenienceDiorama（连同雨、涟漪、店灯）；
    ///   3. 实例化地图 prefab，挂 MapWorldProfile（碰撞/导航/锚点数据）；
    ///   4. 角色放到地图出生点，验收标记点搬到附近；
    ///   5. 光照 / 天空 / 雾 / 后处理 / 第三人称相机按 scene_config.json 统一应用；
    ///   6. 保存场景并写入 Build Settings。
    ///
    /// 调参时不需要重跑完整构建：改完 scene_config.json 后只跑
    /// 「UnityLITF/地图/② 只更新相机与灯光（当前场景）」即可。
    /// </summary>
    public static class SovietBlueHourSceneBuilder
    {
        public const string SourceScene = "Assets/Scenes/RainyConvenience_TaskPlan.unity";
        public const string TargetScene = "Assets/Scenes/SovietBlueHour_TaskPlan.unity";
        public const string MapPrefabPath = "Assets/Maps/SovietBlueHour/SovietBlueHour_Map.prefab";
        public const string DataDir = "Assets/Resources/SovietBlueHour";
        public const string ConfigPath = DataDir + "/scene_config.json";
        public const string SkyMatPath = "Assets/Materials/SovietBlueHour_Sky.mat";
        public const string PostFxPath = "Assets/Settings/SovietBlueHourPostFX.asset";
        const string SkyShaderName = "Yixuan/SkyGradient";
        const string RootName = "SovietBlueHour_Map";
        const string SkyName = "SovietBlueHourSky";
        const string MoonName = "MoonLight";
        const string VolumeName = "Global Volume";

        // ============================================================
        //  配置结构（JsonUtility 用）
        // ============================================================
        [Serializable]
        public class SceneConfig
        {
            public Vector3 spawn = new Vector3(0f, 0f, -72f);
            public float spawnYaw = 0f;
            public float[] boundsMin = { -196f, -145.5f };
            public float[] boundsMax = { 200f, 152.4f };
            public CameraCfg camera = new CameraCfg();
            public LightCfg lighting = new LightCfg();
            public SkyCfg sky = new SkyCfg();
            public QualityCfg quality = new QualityCfg();
            public MarkerCfg[] markers = new MarkerCfg[0];
        }

        [Serializable]
        public class CameraCfg
        {
            public float yawOffset = 18f;
            public float distance = 4.6f;
            public float pitch = 8.5f;
            public float lookHeight = 1.18f;
            public float fov = 40f;
            public float talkPullIn = 0.25f;
            public float minDistance = 1.5f;
            public float minZoom = 2.2f;
            public float maxZoom = 9f;
            public bool collision = true;
            public bool mouseOrbit = true;
            public float yawSharpness = 2.4f;
            public float posSharpness = 10f;
        }

        [Serializable]
        public class LightCfg
        {
            public float keyIntensity = 0.80f;
            public Color keyColor = new Color(1f, 0.96f, 0.90f, 1f);
            public Vector3 keyEuler = new Vector3(24f, 195f, 0f);
            // 环境冷光：只照地图（不照角色层 8），给建筑一点月光填充
            public float moonIntensity = 0.42f;
            public Color moonColor = new Color(0.52f, 0.70f, 1f, 1f);
            public Vector3 moonEuler = new Vector3(32f, 128f, 0f);
            // 角色专用补光/轮廓光（cullingMask = 角色层 8）
            public float fillIntensity = 0.42f;
            public Color fillColor = new Color(1f, 0.93f, 0.84f, 1f);
            public float rimIntensity = 0.75f;
            public Color rimColor = new Color(0.55f, 0.75f, 1f, 1f);
            public Color ambientSky = new Color(0.17f, 0.22f, 0.32f, 1f);
            public Color ambientEquator = new Color(0.11f, 0.14f, 0.20f, 1f);
            public Color ambientGround = new Color(0.045f, 0.05f, 0.065f, 1f);
            public float exposure = -0.35f;
            public float contrast = 10f;
            public float saturation = 8f;
            public bool fog = true;
            public Color fogColor = new Color(0.045f, 0.075f, 0.135f, 1f);
            public float fogDensity = 0.0022f;
            public float bloomIntensity = 0.42f;
            public float bloomThreshold = 0.88f;
            public float vignette = 0.24f;
            public Color background = new Color(0.085f, 0.15f, 0.29f, 1f);
        }

        [Serializable]
        public class SkyCfg
        {
            public Color zenith = new Color(0.015f, 0.030f, 0.075f, 1f);
            public Color horizon = new Color(0.085f, 0.150f, 0.290f, 1f);
            public Color ground = new Color(0.012f, 0.016f, 0.024f, 1f);
            public Color glowColor = new Color(0.45f, 0.34f, 0.22f, 1f);
            public float horizonPower = 1.35f;
            public float horizonBias = 0.015f;
            public float glow = 0.25f;
            public float radius = 700f;
        }

        [Serializable]
        public class QualityCfg
        {
            public float shadowDistance = 70f;
            public int cascadeCount = 2;
            public bool softShadows = true;
        }

        [Serializable]
        public class MarkerCfg
        {
            public string name;
            public Vector3 position;
            public Vector3 approach;
        }

        // ============================================================
        //  菜单入口
        // ============================================================

        [MenuItem("UnityLITF/地图/① 构建 SovietBlueHour 任务场景（完整）", false, 1)]
        public static void BuildAll()
        {
            var cfg = LoadConfig();
            if (cfg == null) return;
            if (!PrepareData()) return;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MapPrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[SovietBlueHour] 找不到地图 prefab：" + MapPrefabPath + "，请先完成 GLB 导入。");
                return;
            }

            if (!Application.isBatchMode)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            }

            // 1) 旧场景 → 新场景（角色/UI/AI/相机组件全部保留）
            var scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TargetScene) != null) AssetDatabase.DeleteAsset(TargetScene);
            if (!EditorSceneManager.SaveScene(scene, TargetScene))
            {
                Debug.LogError("[SovietBlueHour] 另存新场景失败：" + TargetScene);
                return;
            }
            scene = EditorSceneManager.OpenScene(TargetScene, OpenSceneMode.Single);
            Debug.Log("[SovietBlueHour] 新场景已从旧场景复制：" + TargetScene);

            // 2) 删旧微缩景观
            var diorama = GameObject.Find("RainyConvenienceDiorama");
            if (diorama != null) UnityEngine.Object.DestroyImmediate(diorama);

            // 3) 地图
            var old = GameObject.Find(RootName);
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var wrapper = new GameObject(RootName);
            var mapInstance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            mapInstance.name = "SovietBlueHour_Geometry";
            mapInstance.transform.SetParent(wrapper.transform, false);
            mapInstance.transform.localPosition = Vector3.zero;
            mapInstance.transform.localRotation = Quaternion.identity;
            mapInstance.transform.localScale = Vector3.one;

            var profile = wrapper.AddComponent<MapWorldProfile>();
            profile.EnableMapNavigation = true;
            profile.CreateCameraColliders = true;
            profile.GroundY = 0f;
            profile.WalkableMin = new Vector2(cfg.boundsMin[0], cfg.boundsMin[1]);
            profile.WalkableMax = new Vector2(cfg.boundsMax[0], cfg.boundsMax[1]);
            profile.SpawnPoint = cfg.spawn;
            profile.SpawnYaw = cfg.spawnYaw;
            profile.CollisionJson = AssetDatabase.LoadAssetAtPath<TextAsset>(DataDir + "/collision.json");
            profile.NavJson = AssetDatabase.LoadAssetAtPath<TextAsset>(DataDir + "/nav_compact.json");
            profile.AnchorsJson = AssetDatabase.LoadAssetAtPath<TextAsset>(DataDir + "/anchors.json");
            profile.GroundJson = AssetDatabase.LoadAssetAtPath<TextAsset>(DataDir + "/ground_height.json");
            profile.EnsureData();
            float spawnSurface = profile.SurfaceY(cfg.spawn.x, cfg.spawn.z);
            cfg.spawn.y = spawnSurface;
            profile.SpawnPoint = cfg.spawn;
            EditorUtility.SetDirty(profile);
            Debug.Log("[SovietBlueHour] 地面高度图：出生点地面 y=" + spawnSurface.ToString("F3"));

            PrepareMapRenderers(mapInstance);

            // 4) 角色 / 验收标记
            PlaceCharacter(cfg);
            PlaceMarkers(cfg);
            // node-1 regression: UnifiedHud (tabs + quick bar + minimap) must survive every
            // scene rebuild. The Rainy base scene does not contain it, so ensure it explicitly.
            if (UnityEngine.Object.FindObjectOfType<Yixuan.UI.UnifiedHud>() == null)
            {
                var hudGo = new GameObject("UnifiedHud");
                var hud = hudGo.AddComponent<Yixuan.UI.UnifiedHud>();
                hud.OpenOnStart = true;
                hud.StartTab = Yixuan.UI.UnifiedHud.Tab.Map;
                hud.PanelWidth = 478f;
            }
            if (GameObject.Find("DynamicInteractionSystem") == null)
            {
                var dynamicSystem = new GameObject("DynamicInteractionSystem");
                dynamicSystem.AddComponent<Yixuan.Dynamic.DynamicInteractionBootstrap>();
                dynamicSystem.AddComponent<Yixuan.Dynamic.DynamicActorSystem>();
                dynamicSystem.AddComponent<Yixuan.Dynamic.DynamicVisionFlow>();
            }

            // 5) 天空 / 光照 / 相机 / 画质
            ApplySky(cfg);
            ApplyLighting(cfg);
            ApplyCamera(cfg);
            ApplyQuality(cfg);

            // 6) 保存 + Build Settings
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AddSceneToBuildSettings(TargetScene);
            AssetDatabase.SaveAssets();
            Debug.Log("[SovietBlueHour] 构建完成：" + TargetScene + "（出生点 " + cfg.spawn.ToString("F1") + "，yaw=" + cfg.spawnYaw.ToString("F0") + "）");
        }

        [MenuItem("UnityLITF/地图/② 只更新相机与灯光（当前场景）", false, 2)]
        public static void UpdateCameraAndLighting()
        {
            var cfg = LoadConfig();
            if (cfg == null) return;
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != TargetScene)
            {
                Debug.LogWarning("[SovietBlueHour] 当前场景不是 " + TargetScene + "，请先打开它。");
                return;
            }
            var profile = UnityEngine.Object.FindObjectOfType<MapWorldProfile>();
            if (profile != null)
            {
                profile.WalkableMin = new Vector2(cfg.boundsMin[0], cfg.boundsMin[1]);
                profile.WalkableMax = new Vector2(cfg.boundsMax[0], cfg.boundsMax[1]);
                profile.SpawnPoint = cfg.spawn;
                profile.SpawnYaw = cfg.spawnYaw;
                EditorUtility.SetDirty(profile);
            }
            PlaceCharacter(cfg);
            ApplySky(cfg);
            ApplyLighting(cfg);
            ApplyCamera(cfg);
            ApplyQuality(cfg);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[SovietBlueHour] 相机/灯光已更新：" + TargetScene);
        }

        [MenuItem("UnityLITF/地图/③ 把角色移到出生点", false, 3)]
        public static void MoveCharacterToSpawn()
        {
            var cfg = LoadConfig();
            if (cfg == null) return;
            PlaceCharacter(cfg);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        // ============================================================
        //  配置 / 数据
        // ============================================================
        public static SceneConfig LoadConfig()
        {
            if (!File.Exists(ConfigPath))
            {
                Debug.LogError("[SovietBlueHour] 找不到配置：" + ConfigPath);
                return null;
            }
            try
            {
                var cfg = JsonUtility.FromJson<SceneConfig>(File.ReadAllText(ConfigPath));
                if (cfg == null) throw new Exception("配置文件解析为空");
                if (cfg.markers == null) cfg.markers = new MarkerCfg[0];
                return cfg;
            }
            catch (Exception e)
            {
                Debug.LogError("[SovietBlueHour] 配置解析失败：" + e.Message);
                return null;
            }
        }

        /// <summary>把集成阶段生成的 collision/nav/anchors 拷进 Resources 并导入。</summary>
        static bool PrepareData()
        {
            EnsureAssetFolder("Assets/Resources");
            EnsureAssetFolder(DataDir);
            string srcDir = "Maps/SovietBlueHour/integration";
            var files = new Dictionary<string, string>
            {
                { "collision_unity.json", DataDir + "/collision.json" },
                { "nav_compact.json", DataDir + "/nav_compact.json" },
                { "anchors.json", DataDir + "/anchors.json" },
                { "ground_height.json", DataDir + "/ground_height.json" },
            };
            foreach (var kv in files)
            {
                string src = Path.Combine(srcDir, kv.Key);
                if (!File.Exists(src))
                {
                    Debug.LogError("[SovietBlueHour] 缺少集成数据文件：" + src + "（先运行地图数据集成脚本）");
                    return false;
                }
                File.Copy(src, kv.Value, true);
                AssetDatabase.ImportAsset(kv.Value, ImportAssetOptions.ForceUpdate);
            }
            return true;
        }

        /// <summary>地图网格：静态批处理 + 只接收阴影不投射（1.4M tris 阴影 pass 太贵）。</summary>
        static bool IsDynamicNode(Transform transform)
        {
            for (var t = transform; t != null; t = t.parent)
                if (t.name.StartsWith("DYN_")) return true;
            return false;
        }

        static void PrepareMapRenderers(GameObject mapInstance)
        {
            Renderer[] renderers = mapInstance.GetComponentsInChildren<Renderer>(true);
            int staticCount = 0, dynamicCount = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = true;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                if (IsDynamicNode(r.transform))
                {
                    dynamicCount++;
                    continue;
                }
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, StaticEditorFlags.BatchingStatic);
                staticCount++;
            }
            Debug.Log("[SovietBlueHour] 地图渲染器=" + renderers.Length +
                      "（静态批处理 " + staticCount + "，动态实体 " + dynamicCount + "，关闭投影）");
        }

        // ============================================================
        //  角色 / 标记点
        // ============================================================
        static float SurfaceAt(float x, float z)
        {
            var profile = UnityEngine.Object.FindObjectOfType<MapWorldProfile>();
            return profile != null ? profile.SurfaceY(x, z) : 0f;
        }

        static void PlaceCharacter(SceneConfig cfg)
        {
            var actor = GameObject.Find("Yixuan_Test");
            if (actor == null) { Debug.LogError("[SovietBlueHour] 找不到角色 Yixuan_Test"); return; }
            float surface = SurfaceAt(cfg.spawn.x, cfg.spawn.z);
            actor.transform.position = new Vector3(cfg.spawn.x, surface, cfg.spawn.z);
            actor.transform.rotation = Quaternion.Euler(0f, cfg.spawnYaw, 0f);
            var snap = actor.GetComponent<Yixuan.Motion.GroundSnap>();
            if (snap != null)
            {
                snap.GroundY = 0f;          // 兜底；大地图由高度图动态取地面
                snap.UseWorldGround = true;
                snap.MaxOffset = 0.8f;
                snap.Enabled = true;
                EditorUtility.SetDirty(snap);
            }
            var loco = actor.GetComponent<Yixuan.Motion.CharacterLocomotion>();
            if (loco != null) { loco.WalkingEnabled = true; EditorUtility.SetDirty(loco); }
            Debug.Log("[SovietBlueHour] 角色位置=" + cfg.spawn.ToString("F2") + " yaw=" + cfg.spawnYaw.ToString("F0"));
        }

        static void PlaceMarkers(SceneConfig cfg)
        {
            var root = GameObject.Find("TaskAcceptanceRoot");
            if (root == null) { Debug.LogWarning("[SovietBlueHour] 找不到 TaskAcceptanceRoot（验收标记保持原位）"); return; }
            for (int i = 0; i < cfg.markers.Length; i++)
            {
                var m = cfg.markers[i];
                if (string.IsNullOrEmpty(m.name)) continue;
                Transform t = root.transform.Find(m.name);
                if (t == null) { Debug.LogWarning("[SovietBlueHour] 标记不存在：" + m.name); continue; }
                Vector3 pos = new Vector3(m.position.x, SurfaceAt(m.position.x, m.position.z) + m.position.y, m.position.z);
                t.position = pos;
                Transform approach = root.transform.Find(m.name + "_Approach");
                Vector3 app = new Vector3(m.approach.x, SurfaceAt(m.approach.x, m.approach.z), m.approach.z);
                if (approach != null) approach.position = app;
                Transform pad = root.transform.Find(m.name + "_StandingPad");
                if (pad != null) pad.position = app + Vector3.up * 0.015f;
            }
            Transform wall = root.transform.Find("GSTestWall");
            if (wall != null && cfg.markers.Length > 0)
            {
                // 测试墙放到 2 号标记点旁边，避免挡在默认相机视野里。
                var m = cfg.markers[cfg.markers.Length - 1];
                float wx = m.position.x + 2.5f, wz = m.position.z;
                wall.position = new Vector3(wx, SurfaceAt(wx, wz) + 0.9f, wz);
            }
            // 清掉旧场景里遗留的验收请求文本（不应作为新场景的默认输入）。
            var panel = UnityEngine.Object.FindObjectOfType<Yixuan.AI.Tasks.TaskMissionPanel>();
            if (panel != null) { panel.Request = ""; UnityEditor.EditorUtility.SetDirty(panel); }
        }

        // ============================================================
        //  天空 / 光照 / 相机 / 画质
        // ============================================================
        static void ApplySky(SceneConfig cfg)
        {
            var shader = Shader.Find(SkyShaderName);
            if (shader == null) { Debug.LogError("[SovietBlueHour] 找不到天空 shader：" + SkyShaderName); return; }
            EnsureAssetFolder("Assets/Materials");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyMatPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, SkyMatPath);
            }
            else if (mat.shader != shader)
            {
                mat.shader = shader;
            }
            mat.SetColor("_ZenithColor", cfg.sky.zenith);
            mat.SetColor("_HorizonColor", cfg.sky.horizon);
            mat.SetColor("_GroundColor", cfg.sky.ground);
            mat.SetColor("_GlowColor", cfg.sky.glowColor);
            mat.SetFloat("_HorizonPower", cfg.sky.horizonPower);
            mat.SetFloat("_HorizonBias", cfg.sky.horizonBias);
            mat.SetFloat("_HorizonGlow", cfg.sky.glow);
            EditorUtility.SetDirty(mat);

            var old = GameObject.Find(SkyName);
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var sky = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sky.name = SkyName;
            var col = sky.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.DestroyImmediate(col);
            sky.transform.position = Vector3.zero;
            sky.transform.localScale = Vector3.one * (cfg.sky.radius * 2f);
            var renderer = sky.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            RenderSettings.skybox = null;
            Debug.Log("[SovietBlueHour] 天空罩半径=" + cfg.sky.radius + "m");
        }

        static void ApplyLighting(SceneConfig cfg)
        {
            // 主光：场景唯一投射阴影的方向光（角色 + 地图都吃）
            var keyGo = GameObject.Find("KeyLight");
            if (keyGo == null) keyGo = new GameObject("KeyLight");
            keyGo.transform.position = new Vector3(0.4f, 2.1f, -1.08f);
            keyGo.transform.rotation = Quaternion.Euler(cfg.lighting.keyEuler);
            var key = keyGo.GetComponent<Light>();
            if (key == null) key = keyGo.AddComponent<Light>();
            key.type = LightType.Directional;
            key.color = cfg.lighting.keyColor;
            key.intensity = cfg.lighting.keyIntensity;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.65f;
            key.shadowBias = 0.05f;
            key.shadowNormalBias = 0.4f;
            key.cullingMask = ~0;
            EditorUtility.SetDirty(key);

            // 环境冷光（月亮/城市天光）：只照地图，不照角色层 8，避免角色被蓝光洗白
            int worldMask = ~(1 << 8);
            var moonGo = GameObject.Find(MoonName);
            if (moonGo == null) moonGo = new GameObject(MoonName);
            moonGo.transform.rotation = Quaternion.Euler(cfg.lighting.moonEuler);
            var moon = moonGo.GetComponent<Light>();
            if (moon == null) moon = moonGo.AddComponent<Light>();
            moon.type = LightType.Directional;
            moon.color = cfg.lighting.moonColor;
            moon.intensity = cfg.lighting.moonIntensity;
            moon.shadows = LightShadows.None;
            moon.cullingMask = worldMask;
            EditorUtility.SetDirty(moon);

            // 角色专用补光 / 轮廓光：只照角色层，不再污染地图
            int charMask = 1 << 8;
            ApplyCharacterLight("FillLight", cfg.lighting.fillColor, cfg.lighting.fillIntensity, charMask);
            ApplyCharacterLight("RimLight", cfg.lighting.rimColor, cfg.lighting.rimIntensity, charMask);

            RenderSettings.sun = key;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = cfg.lighting.ambientSky;
            RenderSettings.ambientEquatorColor = cfg.lighting.ambientEquator;
            RenderSettings.ambientGroundColor = cfg.lighting.ambientGround;
            RenderSettings.ambientIntensity = 1f;

            RenderSettings.fog = cfg.lighting.fog;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = cfg.lighting.fogColor;
            RenderSettings.fogDensity = cfg.lighting.fogDensity;

            // LightingSetup：Start() 会用它覆盖曝光/背景色，必须同步改。
            var setup = UnityEngine.Object.FindObjectOfType<YixuanLightingSetup>();
            if (setup != null)
            {
                setup.KeyIntensity = cfg.lighting.keyIntensity;
                setup.KeyColor = cfg.lighting.keyColor;
                setup.AmbientSky = cfg.lighting.ambientSky;
                setup.AmbientEquator = cfg.lighting.ambientEquator;
                setup.AmbientGround = cfg.lighting.ambientGround;
                setup.Exposure = cfg.lighting.exposure;
                setup.BackgroundIndex = 0;
                if (setup.Backgrounds == null || setup.Backgrounds.Length == 0) setup.Backgrounds = new Color[1];
                setup.Backgrounds[0] = cfg.lighting.background;
                EditorUtility.SetDirty(setup);
            }

            ApplyPostFx(cfg);
        }

        /// <summary>把已有的角色补光限制在角色层，并设置颜色/强度；找不到就跳过。</summary>
        static void ApplyCharacterLight(string name, Color color, float intensity, int cullingMask)
        {
            var go = GameObject.Find(name);
            if (go == null) { Debug.LogWarning("[SovietBlueHour] 找不到角色补光：" + name); return; }
            var light = go.GetComponent<Light>();
            if (light == null) return;
            light.type = LightType.Directional;
            light.color = color;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            light.cullingMask = cullingMask;
            EditorUtility.SetDirty(light);
        }

        static void ApplyPostFx(SceneConfig cfg)
        {
            var src = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/YixuanPostFX.asset");
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(PostFxPath);
            if (profile == null && src != null)
            {
                AssetDatabase.CopyAsset("Assets/Settings/YixuanPostFX.asset", PostFxPath);
                AssetDatabase.Refresh();
                profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(PostFxPath);
            }
            if (profile == null) { Debug.LogWarning("[SovietBlueHour] 没有后处理 Profile，跳过 Bloom 调整"); return; }

            Bloom bloom;
            if (profile.TryGet(out bloom))
            {
                bloom.intensity.overrideState = true; bloom.intensity.value = cfg.lighting.bloomIntensity;
                bloom.threshold.overrideState = true; bloom.threshold.value = cfg.lighting.bloomThreshold;
                bloom.scatter.overrideState = true; bloom.scatter.value = 0.7f;
                bloom.highQualityFiltering.overrideState = true; bloom.highQualityFiltering.value = true;
            }
            ColorAdjustments ca;
            if (profile.TryGet(out ca))
            {
                ca.postExposure.overrideState = true; ca.postExposure.value = cfg.lighting.exposure;
                ca.contrast.overrideState = true; ca.contrast.value = cfg.lighting.contrast;
                ca.saturation.overrideState = true; ca.saturation.value = cfg.lighting.saturation;
            }
            Vignette vg;
            if (profile.TryGet(out vg))
            {
                vg.intensity.overrideState = true; vg.intensity.value = cfg.lighting.vignette;
            }
            EditorUtility.SetDirty(profile);

            var volGo = GameObject.Find(VolumeName);
            if (volGo != null)
            {
                var vol = volGo.GetComponent<Volume>();
                // URP Volume.profile 的 setter 只写非序列化字段 m_InternalProfile，保存场景就丢；
                // 必须写 sharedProfile，后处理才会随场景保存并生效。
                if (vol != null) { vol.sharedProfile = profile; EditorUtility.SetDirty(vol); }
            }
        }

        static void ApplyCamera(SceneConfig cfg)
        {
            var actor = GameObject.Find("Yixuan_Test");
            var mainCam = Camera.main;
            if (mainCam == null) { Debug.LogError("[SovietBlueHour] 找不到 Main Camera"); return; }

            mainCam.nearClipPlane = 0.05f;
            mainCam.farClipPlane = 900f;
            mainCam.clearFlags = CameraClearFlags.SolidColor;
            mainCam.backgroundColor = cfg.lighting.background;
            EditorUtility.SetDirty(mainCam);

            var director = mainCam.GetComponent<CameraDirector>();
            if (director == null) director = mainCam.gameObject.AddComponent<CameraDirector>();
            director.FollowCharacter = true;
            director.FollowTarget = actor != null ? actor.transform : director.FollowTarget;
            director.EyeView = false;
            director.ThirdPerson = true;
            director.TP_YawOffset = cfg.camera.yawOffset;
            director.TP_Distance = cfg.camera.distance;
            director.TP_Pitch = cfg.camera.pitch;
            director.TP_LookHeight = cfg.camera.lookHeight;
            director.TP_Fov = cfg.camera.fov;
            director.TP_TalkPullIn = cfg.camera.talkPullIn;
            director.TP_MinDistance = cfg.camera.minDistance;
            director.TP_MinZoom = cfg.camera.minZoom;
            director.TP_MaxZoom = cfg.camera.maxZoom;
            director.TP_Collision = cfg.camera.collision;
            director.TP_MouseOrbit = cfg.camera.mouseOrbit;
            director.TP_YawSharpness = cfg.camera.yawSharpness;
            director.TP_PosSharpness = cfg.camera.posSharpness;
            director.SnapOnStart = true;
            EditorUtility.SetDirty(director);

            var orbit = mainCam.GetComponent<Yixuan.Diorama.DioramaOrbitCamera>();
            if (orbit != null) { orbit.enabled = false; EditorUtility.SetDirty(orbit); }

            // 编辑模式先把相机摆到默认第三人称机位，打开场景就能看到构图
            if (actor != null)
            {
                Vector3 pivot = actor.transform.position + Vector3.up * cfg.camera.lookHeight;
                float yaw = cfg.spawnYaw + cfg.camera.yawOffset;
                float yawRad = yaw * Mathf.Deg2Rad, pitchRad = cfg.camera.pitch * Mathf.Deg2Rad;
                Vector3 back = new Vector3(-Mathf.Sin(yawRad) * Mathf.Cos(pitchRad),
                                           Mathf.Sin(pitchRad),
                                           -Mathf.Cos(yawRad) * Mathf.Cos(pitchRad));
                mainCam.transform.position = pivot + back * cfg.camera.distance;
                mainCam.transform.rotation = Quaternion.LookRotation((pivot - mainCam.transform.position).normalized, Vector3.up);
                mainCam.fieldOfView = cfg.camera.fov;
                EditorUtility.SetDirty(mainCam.transform);
            }

            // 眼睛相机：视距要够长，否则看不到远处地标
            var eye = GameObject.Find("EyeCamera");
            if (eye != null)
            {
                var cam = eye.GetComponent<Camera>();
                if (cam != null)
                {
                    cam.farClipPlane = 900f;
                    cam.nearClipPlane = 0.05f;
                    cam.backgroundColor = cfg.lighting.background;
                    EditorUtility.SetDirty(cam);
                }
            }
        }

        static void ApplyQuality(SceneConfig cfg)
        {
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null) { Debug.LogWarning("[SovietBlueHour] 当前管线不是 URP，跳过阴影设置"); return; }
            var so = new SerializedObject(urp);
            var shadowDistance = so.FindProperty("m_ShadowDistance");
            if (shadowDistance != null) shadowDistance.floatValue = cfg.quality.shadowDistance;
            var cascades = so.FindProperty("m_ShadowCascadeCount");
            if (cascades != null) cascades.intValue = Mathf.Clamp(cfg.quality.cascadeCount, 1, 4);
            var soft = so.FindProperty("m_SoftShadowsSupported");
            if (soft != null) soft.boolValue = cfg.quality.softShadows;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(urp);
            AssetDatabase.SaveAssets();
            Debug.Log("[SovietBlueHour] URP 阴影：距离=" + cfg.quality.shadowDistance + "m，级联=" + cfg.quality.cascadeCount + "，软阴影=" + cfg.quality.softShadows);
        }

        static void AddSceneToBuildSettings(string scenePath)
        {
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            bool exists = false;
            for (int i = 0; i < list.Count; i++) if (list[i].path == scenePath) { exists = true; list[i].enabled = true; }
            if (!exists) list.Insert(0, new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        static void EnsureAssetFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent)) EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
