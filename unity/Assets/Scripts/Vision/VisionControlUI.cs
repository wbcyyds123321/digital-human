using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Yixuan.Vision
{
    /// <summary>
    /// 游戏内视觉链路开关：
    ///   - 游戏视角（EyeCamera → Qwen）
    ///   - 电脑摄像头（WebCamTexture → Qwen）
    /// 两路都关时不会发请求，不消耗额度。
    /// 电脑摄像头开启时，右侧显示本地预览。
    /// </summary>
    public class VisionControlUI : MonoBehaviour
    {
        public VisionObserver Observer;

        Canvas _canvas;
        GameObject _linkPanel;
        Text _gameBtnText, _camBtnText, _status;
        RawImage _preview;
        GameObject _previewRoot;
        float _nextRefresh;
        bool _taskPanelLayout, _linkPanelRequested = true;
        Font _font;
        Sprite _white;

        void Start()
        {
            if (Observer == null) Observer = GetComponent<VisionObserver>();
            if (Observer == null) Observer = FindObjectOfType<VisionObserver>();
            Build();
        }

        void Build()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var whiteTex = Texture2D.whiteTexture;
            _white = Sprite.Create(whiteTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));

            if (FindObjectOfType<EventSystem>() == null)
            {
                var es = new GameObject("VisionEventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }

            var canvasGo = new GameObject("VisionCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            // 左上控制面板（★ 锚点必须两端都固定在左上：之前 anchorMax=(1,1) 导致面板被拉伸到整个屏幕宽）
            // 下移到 -140：避免和左上角角色名"庄方宜"重叠
            var panel = MakeRect("VisionPanel", canvasGo.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(20f, -140f), new Vector2(300f, 170f), new Vector2(0f, 1f), new Vector2(0f, 1f));
            _linkPanel = panel.gameObject;
            var bg = panel.gameObject.AddComponent<Image>();
            bg.sprite = _white;
            bg.color = new Color(0.05f, 0.065f, 0.09f, 0.88f);
            bg.raycastTarget = true;

            MakeText(panel, "视觉链路  <size=18>按需计费</size>", new Vector2(12f, -10f), new Vector2(276f, 26f), 22, new Color(0.9f, 0.94f, 1f));
            var gameBtn = MakeButton(panel, "游戏视角：关", new Vector2(12f, -42f), new Vector2(276f, 34f),
                new Color(0.15f, 0.22f, 0.33f, 0.95f), delegate { if (Observer != null) Observer.ToggleGameView(); });
            _gameBtnText = gameBtn.GetComponentInChildren<Text>();
            var camBtn = MakeButton(panel, "电脑摄像头：关", new Vector2(12f, -82f), new Vector2(276f, 34f),
                new Color(0.15f, 0.22f, 0.33f, 0.95f), delegate { if (Observer != null) Observer.ToggleWebcam(); });
            _camBtnText = camBtn.GetComponentInChildren<Text>();
            _status = MakeText(panel, "两路都关 · 不消耗额度", new Vector2(12f, -124f), new Vector2(276f, 36f), 17,
                new Color(0.65f, 0.72f, 0.84f));

            // 右上摄像头预览（下移到"关闭面板"按钮下方，避免和按钮/标题重叠）
            _previewRoot = MakeRect("CamPreviewPanel", canvasGo.transform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-20f, -346f), new Vector2(300f, 225f), new Vector2(1f, 1f), new Vector2(1f, 1f)).gameObject;
            var pbg = _previewRoot.AddComponent<Image>();
            pbg.sprite = _white;
            pbg.color = new Color(0.02f, 0.03f, 0.05f, 0.92f);
            var title = MakeText(_previewRoot.transform, "电脑摄像头", new Vector2(8f, -6f), new Vector2(284f, 22f), 17,
                new Color(0.8f, 0.86f, 1f));
            title.alignment = TextAnchor.MiddleLeft;

            var rawGo = new GameObject("WebcamRawImage");
            rawGo.transform.SetParent(_previewRoot.transform, false);
            var rawRt = rawGo.AddComponent<RectTransform>();
            rawRt.anchorMin = new Vector2(0f, 0f);
            rawRt.anchorMax = new Vector2(1f, 1f);
            rawRt.offsetMin = new Vector2(8f, 8f);
            rawRt.offsetMax = new Vector2(-8f, -32f);
            _preview = rawGo.AddComponent<RawImage>();
            _preview.color = Color.white;
            _previewRoot.SetActive(false);
            ApplyLinkLayout();
            SetLinkPanelVisible(false);   // 统一控制台已接管视觉开关，旧左上小块不再显示
        }

        RectTransform MakeRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 pos, Vector2 size, Vector2 pivot, Vector2 _unused)
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

        Button MakeButton(Transform parent, string label, Vector2 pos, Vector2 size, Color color, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("VisionButton");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var img = go.AddComponent<Image>();
            img.sprite = _white;
            img.color = color;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);

            var textGo = new GameObject("Label");
            textGo.transform.SetParent(go.transform, false);
            var trt = textGo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(8f, 0f);
            trt.offsetMax = new Vector2(-8f, 0f);
            var text = textGo.AddComponent<Text>();
            text.font = _font;
            text.text = label;
            text.fontSize = 18;
            text.color = new Color(0.92f, 0.95f, 1f);
            text.alignment = TextAnchor.MiddleLeft;
            text.raycastTarget = false;
            return btn;
        }

        Text MakeText(Transform parent, string text, Vector2 pos, Vector2 size, int fontSize, Color color)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var t = go.AddComponent<Text>();
            t.font = _font;
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = TextAnchor.UpperLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>显示/隐藏左上角视觉开关小块（统一控制面板打开时隐藏，避免两块 UI 重叠）。</summary>
        public void SetLinkPanelVisible(bool on)
        {
            _linkPanelRequested = on;
            ApplyLinkLayout();
        }

        public void SetTaskPanelLayout(bool active)
        { _taskPanelLayout = active; ApplyLinkLayout(); }
        void ApplyLinkLayout()
        { if (_linkPanel != null) _linkPanel.SetActive(_linkPanelRequested && !_taskPanelLayout); }
        public bool LinkPanelActive { get { return _linkPanel != null && _linkPanel.activeInHierarchy; } }
        /// <summary>显示/隐藏整个视觉 UI（含右上摄像头预览）。</summary>
        public void SetUiVisible(bool on)
        {
            if (_canvas != null) _canvas.gameObject.SetActive(on);
        }

        void Update()
        {
            if (Observer == null || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.2f;
            if (_gameBtnText != null)
                _gameBtnText.text = "游戏视角：" + (Observer.GameViewEnabled ? "开" : "关");
            if (_camBtnText != null)
                _camBtnText.text = "电脑摄像头：" + (Observer.WebcamEnabled ? "开" : "关");

            if (_previewRoot != null)
            {
                bool show = Observer.WebcamEnabled && Observer.WebcamTexture != null;
                if (_previewRoot.activeSelf != show) _previewRoot.SetActive(show);
                if (show && _preview != null) _preview.texture = Observer.WebcamTexture;
            }

            if (_status != null)
            {
                string msg;
                if (!Observer.Client.HasKey) msg = "未找到 Qwen key";
                else if (!Observer.GameViewEnabled && !Observer.WebcamEnabled) msg = "两路都关 · 不消耗额度";
                else if (Observer.Client.ServiceBlocked) msg = Observer.Client.ServiceStatus;
                else if (Observer.Asking) msg = "正在看…";
                else if (Observer.RequestInFlight) msg = "正在观察…";
                else if (!string.IsNullOrEmpty(Observer.LastError)) msg = "视觉错误：" + Observer.LastError;
                else if (!string.IsNullOrEmpty(Observer.LastRealtimeAnswer))
                    msg = "刚回答 · " + ObserveSummary(Observer.LastRealtimeAnswer);
                else if (!string.IsNullOrEmpty(Observer.LastObservation))
                    msg = "视频 " + Observer.BufferedVideoFrames + " 帧 · 事件 " + Observer.VisualEventCount + " · 事实 " + Observer.VisualFactCount + " · " + ObserveSummary(Observer.LastObservation);
                else msg = "等待第一次观察…";
                _status.text = msg;
                _status.color = !Observer.Client.HasKey ? new Color(1f, 0.55f, 0.45f)
                    : ((Observer.RequestInFlight || Observer.Asking) ? new Color(1f, 0.82f, 0.45f)
                    : new Color(0.65f, 0.72f, 0.84f));
            }
        }

        static string ObserveSummary(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ");
            return s.Length <= 30 ? s : s.Substring(0, 30) + "…";
        }
    }
}
