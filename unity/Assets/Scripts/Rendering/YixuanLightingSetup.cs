using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Yixuan.Rendering
{
    /// <summary>
    /// 光照与渲染设置统一管理。
    ///
    /// 【为什么需要这个脚本】
    /// 之前调试时反复加灯，最后场景里有 4 盏 Directional 叠在一起：
    ///   KeyLight 1.5 + FillLight 1.4 + RimLight 0.5 + Unity 默认 1.0
    /// 再叠加环境光 Flat(0.55,0.56,0.60)，画面严重过曝。
    ///
    /// 更隐蔽的问题：Main Camera 没有 UniversalAdditionalCameraData，
    /// 且 UniversalRenderer.postProcessData 为 null
    /// —— 导致 URP 后处理（Tonemapping / Color Grading）根本不生效，
    /// 没有这一层压曝光，光靠调灯光强度治标不治本。
    ///
    /// 本脚本：
    ///   编辑模式：菜单 Yixuan/Setup Lighting 一键重建全部设置
    ///   运行时  ：提供 SetExposure / SetBackground 供 UI 调用
    /// </summary>
    [DisallowMultipleComponent]
    public class YixuanLightingSetup : MonoBehaviour
    {
        public const string KeyLightName = "KeyLight";
        public const string VolumeName = "Global Volume";

        [Header("主光")]
        public float KeyIntensity = 0.9f;
        public Color KeyColor = new Color(1f, 0.98f, 0.95f, 1f);

        [Header("环境光（Trilight）")]
        public Color AmbientSky = new Color(0.35f, 0.37f, 0.42f, 1f);
        public Color AmbientEquator = new Color(0.28f, 0.29f, 0.33f, 1f);
        public Color AmbientGround = new Color(0.18f, 0.18f, 0.21f, 1f);

        [Header("相机背景")]
        public int BackgroundIndex = 0;
        public Color[] Backgrounds = new Color[]
        {
            new Color(0.18f, 0.19f, 0.22f, 1f),   // 0 深灰
            new Color(0.16f, 0.20f, 0.27f, 1f),   // 1 蓝灰
            new Color(0.55f, 0.57f, 0.60f, 1f),   // 2 亮灰
        };

        [Header("曝光补偿（后处理）")]
        [Range(-2f, 1f)] public float Exposure = -0.15f;

        // ---------------- 运行时接口（供 UI 调用）----------------

        /// <summary>设置曝光补偿（-2 ~ +1）</summary>
        public void SetExposure(float v)
        {
            Exposure = Mathf.Clamp(v, -2f, 1f);
            ApplyExposure();
        }

        /// <summary>切换背景</summary>
        public void SetBackground(int idx)
        {
            if (Backgrounds == null || Backgrounds.Length == 0) return;
            BackgroundIndex = ((idx % Backgrounds.Length) + Backgrounds.Length) % Backgrounds.Length;
            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Backgrounds[BackgroundIndex];
            }
        }

        void ApplyExposure()
        {
            var vol = GameObject.Find(VolumeName);
            if (vol == null) return;
            var v = vol.GetComponent<UnityEngine.Rendering.Volume>();
            if (v == null || v.profile == null) return;

            UnityEngine.Rendering.Universal.ColorAdjustments ca;
            if (v.profile.TryGet(out ca))
            {
                ca.postExposure.value = Exposure;
                ca.postExposure.overrideState = true;
            }
        }

        // ---------------- 编辑器：一键重建 ----------------

#if UNITY_EDITOR
        [ContextMenu("应用全部设置")]
        public void ApplyAll()
        {
            var map = Object.FindObjectOfType<Yixuan.World.MapWorldProfile>();
            if (map != null)
            {
                Debug.LogWarning("[YixuanLightingSetup] 当前是大地图场景（SovietBlueHour）：请用菜单 " +
                                 "UnityLITF/地图/② 只更新相机与灯光，本菜单会用旧微缩景观参数覆盖地图光照配置。");
            }
            SetupLights();
            SetupAmbient();
            SetupCamera();
            SetupVolume();
            SetBackground(BackgroundIndex);

#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
            Debug.Log("[YixuanLightingSetup] 光照/后处理设置已应用");
        }

        void SetupLights()
        {
            // 保留 KeyLight，删掉其余 Directional（避免多光叠加过曝）
            var lights = Object.FindObjectsOfType<Light>();
            for (int i = 0; i < lights.Length; i++)
            {
                var L = lights[i];
                if (L == null) continue;
                if (L.type != LightType.Directional) continue;
                if (L.gameObject.name == KeyLightName) continue;
                // 大地图场景的月亮光 / 角色补光 / 轮廓光由 SovietBlueHourSceneBuilder 管理，不在这里删。
                if (L.gameObject.name == "MoonLight" ||
                    L.gameObject.name == "FillLight" ||
                    L.gameObject.name == "RimLight") continue;

                Debug.Log("[YixuanLightingSetup] 移除多余方向光: " + L.gameObject.name);
                DestroyImmediate(L.gameObject);
            }

            // 配置主光
            var keyGO = GameObject.Find(KeyLightName);
            if (keyGO == null)
            {
                keyGO = new GameObject(KeyLightName);
                keyGO.transform.SetParent(transform, false);
            }
            keyGO.transform.position = new Vector3(0.4f, 2.1f, -1.08f);
            keyGO.transform.rotation = Quaternion.Euler(20f, 190f, 0f);

            var key = keyGO.GetComponent<Light>();
            if (key == null) key = keyGO.AddComponent<Light>();
            key.type = LightType.Directional;
            key.intensity = KeyIntensity;
            key.color = KeyColor;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.6f;
        }

        void SetupAmbient()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = AmbientSky;
            RenderSettings.ambientEquatorColor = AmbientEquator;
            RenderSettings.ambientGroundColor = AmbientGround;
            RenderSettings.fog = false;
        }

        void SetupCamera()
        {
            var cam = Camera.main;
            if (cam == null) { Debug.LogWarning("[YixuanLightingSetup] 找不到 Main Camera"); return; }

            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Backgrounds[Mathf.Clamp(BackgroundIndex, 0, Backgrounds.Length - 1)];

            // 关键：URP 后处理要求相机上有 UniversalAdditionalCameraData
            var t = System.Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
            if (t == null) { Debug.LogWarning("[YixuanLightingSetup] 找不到 UniversalAdditionalCameraData 类型"); return; }

            var add = cam.GetComponent(t) ?? cam.gameObject.AddComponent(t);
            var pPost = t.GetProperty("renderPostProcessing");
            if (pPost != null) pPost.SetValue(add, true, null);
            var pType = t.GetProperty("renderType");
            Debug.Log("[YixuanLightingSetup] 相机后处理已启用: " + (pPost != null));
        }

        void SetupVolume()
        {
            var volGO = GameObject.Find(VolumeName);
            if (volGO == null) volGO = new GameObject(VolumeName);

            var vol = volGO.GetComponent<UnityEngine.Rendering.Volume>();
            if (vol == null) vol = volGO.AddComponent<UnityEngine.Rendering.Volume>();
            vol.isGlobal = true;
            vol.priority = 0f;
            vol.weight = 1f;

            // 试图加载已存在的 Profile，没有则报告（Profile 由编辑器流程创建）
            if (vol.profile == null)
            {
                var p = Resources.Load<UnityEngine.Rendering.VolumeProfile>("YixuanPostFX");
                if (p != null) vol.profile = p;
            }
        }
#endif

        void Start()
        {
            // 运行时把 Inspector 里的值落一次，确保和 UI 状态一致
            SetBackground(BackgroundIndex);
            ApplyExposure();
        }
    }
}
