using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Yixuan.Vision
{
    /// <summary>
    /// 把"角色的眼睛"（EyeCamera）显示出来，方便观察云台扫到哪：
    ///   1) 右上角一个画中画预览：实时显示她看到的画面；
    ///   2) 世界里画一个视锥线框（跟随 EyeCamera 旋转）：直接看到她能扫到的区域。
    ///
    /// 只做展示，不改任何视觉/对话逻辑。默认开启；可在 Inspector 关掉某一项。
    /// </summary>
    public class EyeViewPreview : MonoBehaviour
    {
        [Header("引用（留空自动查找）")]
        public VisionObserver Observer;
        public Camera EyeCamera;

        [Header("显示")]
        public bool ShowPreview = true;
        [Tooltip("统一 HUD 的视觉标签可开关这个画中画")]
        public bool PreviewEnabled = true;
        public bool ShowViewCone = true;
        [Tooltip("只在游戏视角（VisionObserver.GameViewEnabled）开启时显示小窗/视锥")]
        public bool OnlyWhenGameView = true;
        [Range(4, 30)] public float PreviewFps = 10f;
        public int PreviewWidth = 320;
        public int PreviewHeight = 240;
        public float PreviewRightMargin = 20f;
        public float PreviewTopOffset = 360f;

        [Header("视锥")]
        public Color ConeColor = new Color(0.30f, 0.95f, 1f, 0.55f);
        public float ConeFarDistance = 7f;
        public float ConeWidth = 0.02f;

        RenderTexture _rt;
        RawImage _raw;
        Canvas _canvas;
        LineRenderer _cone;
        float _nextRenderTime;
        float _nextConeTime;

        void Start()
        {
            Resolve();
            BuildPreview();
            BuildCone();
            if (EyeCamera == null)
                Debug.LogWarning("[EyeView] 找不到 EyeCamera，眼睛视角不会显示");
        }

        void Resolve()
        {
            if (Observer == null) Observer = FindObjectOfType<VisionObserver>();
            if (EyeCamera == null && Observer != null) EyeCamera = Observer.EyeCamera;
            if (EyeCamera == null)
            {
                var go = GameObject.Find("EyeCamera");
                if (go != null) EyeCamera = go.GetComponent<Camera>();
            }
        }

        // ============================================================
        //  右上角画中画：实时显示 EyeCamera 画面
        // ============================================================
        void BuildPreview()
        {
            if (!ShowPreview) return;

            var canvasGo = new GameObject("EyeViewCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 95;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            RectTransform panel = MakeRect("EyeViewPanel", canvasGo.transform,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-PreviewRightMargin, -PreviewTopOffset),
                new Vector2(PreviewWidth + 12f, PreviewHeight + 34f), new Vector2(1f, 1f));
            var bg = panel.gameObject.AddComponent<Image>();
            bg.color = new Color(0.03f, 0.05f, 0.08f, 0.92f);
            bg.raycastTarget = false;

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            RectTransform titleRt = MakeRect("Title", panel,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(8f, -4f), new Vector2(-16f, 22f), new Vector2(0f, 1f));
            var title = titleRt.gameObject.AddComponent<Text>();
            title.font = font;
            title.text = "眼睛视角（她看到的画面）";
            title.fontSize = 15;
            title.color = new Color(0.72f, 0.9f, 1f, 1f);
            title.alignment = TextAnchor.MiddleLeft;
            title.raycastTarget = false;

            var rawGo = new GameObject("EyeViewRaw");
            rawGo.transform.SetParent(panel, false);
            var rawRt = rawGo.AddComponent<RectTransform>();
            rawRt.anchorMin = Vector2.zero;
            rawRt.anchorMax = Vector2.one;
            rawRt.offsetMin = new Vector2(6f, 6f);
            rawRt.offsetMax = new Vector2(-6f, -28f);
            _raw = rawGo.AddComponent<RawImage>();
            _raw.color = Color.white;
            _raw.raycastTarget = false;

            _rt = new RenderTexture(PreviewWidth, PreviewHeight, 16, RenderTextureFormat.ARGB32);
            _rt.name = "EyeViewPreview_RT";
            _rt.Create();
            _raw.texture = _rt;
        }

        // ============================================================
        //  世界里的视锥线框（显示她能扫到的区域）
        // ============================================================
        void BuildCone()
        {
            if (!ShowViewCone || EyeCamera == null) return;

            var go = new GameObject("EyeViewCone");
            go.layer = 8;   // 和角色同层：EyeCamera 的 cullingMask 排除了 Layer8，不会拍到自己
            go.transform.SetParent(EyeCamera.transform, false);

            _cone = go.AddComponent<LineRenderer>();
            _cone.useWorldSpace = false;
            _cone.loop = false;
            _cone.widthMultiplier = ConeWidth;
            _cone.numCapVertices = 0;
            _cone.numCornerVertices = 0;
            _cone.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _cone.receiveShadows = false;
            _cone.startColor = ConeColor;
            _cone.endColor = ConeColor;

            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh != null)
            {
                var mat = new Material(sh);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", ConeColor);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", ConeColor);
                mat.renderQueue = 3000;
                _cone.material = mat;
            }

            UpdateCone();
        }

        void UpdateCone()
        {
            if (_cone == null || EyeCamera == null) return;
            float far = Mathf.Max(1f, ConeFarDistance);
            float halfH = Mathf.Tan(EyeCamera.fieldOfView * Mathf.Deg2Rad * 0.5f) * far;
            float halfW = halfH * Mathf.Max(0.2f, EyeCamera.aspect);
            Vector3[] c =
            {
                new Vector3(-halfW, -halfH, far),
                new Vector3( halfW, -halfH, far),
                new Vector3( halfW,  halfH, far),
                new Vector3(-halfW,  halfH, far),
            };

            // 4 条从眼睛出发的射线 + 远端 4 条边
            var pts = new List<Vector3>(16);
            for (int i = 0; i < 4; i++) { pts.Add(Vector3.zero); pts.Add(c[i]); }
            for (int i = 0; i < 4; i++) { pts.Add(c[i]); pts.Add(c[(i + 1) % 4]); }
            _cone.positionCount = pts.Count;
            _cone.SetPositions(pts.ToArray());
        }

        void Update()
        {
            if (EyeCamera == null) { Resolve(); return; }

            // 主视角现在是第三人称跟随；只有打开“游戏视角”时才显示眼睛小窗/视锥，避免挡主画面。
            bool visible = PreviewEnabled && (!OnlyWhenGameView || (Observer != null && Observer.GameViewEnabled));
            if (_canvas != null && _canvas.gameObject.activeSelf != visible)
                _canvas.gameObject.SetActive(visible);
            if (_cone != null) _cone.enabled = visible && ShowViewCone;
            if (!visible) return;

            if (_cone != null && Time.unscaledTime >= _nextConeTime)
            {
                _nextConeTime = Time.unscaledTime + 0.5f;
                UpdateCone();
            }
            if (!ShowPreview || _rt == null || _raw == null) return;
            if (Time.unscaledTime < _nextRenderTime) return;
            _nextRenderTime = Time.unscaledTime + 1f / Mathf.Max(1f, PreviewFps);

            RenderTexture prev = EyeCamera.targetTexture;
            EyeCamera.targetTexture = _rt;
            EyeCamera.Render();
            EyeCamera.targetTexture = prev;
        }

        /// <summary>HUD 视觉标签开关画中画（不影响眼睛相机/视觉识别本身）。</summary>
        public void SetPreviewEnabled(bool on)
        {
            PreviewEnabled = on;
            if (!on && _canvas != null) _canvas.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (_rt != null)
            {
                _rt.Release();
                Destroy(_rt);
                _rt = null;
            }
        }

        static RectTransform MakeRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 pos, Vector2 size, Vector2 pivot)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }
    }
}
