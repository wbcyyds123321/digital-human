using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Yixuan.AI;
using Yixuan.AI.Tasks;
using Yixuan.Motion;
using Yixuan.Presentation;
using Yixuan.Vision;
using Yixuan.World;
using Yixuan.WorldMap;

namespace Yixuan.UI
{
    /// <summary>
    /// 统一 HUD（IMGUI）：把原来散落/重叠的弹窗收拢成一个左侧标签面板 + 右上快捷条 + 小地图。
    ///
    /// 设计目标（2026-11-02 第二轮 UI 整理）：
    ///   1. 左侧只有一个「仪玄控制台」，用标签切换：地图 / 任务 / 视觉 / 相机 / 角色 / 设置；
    ///   2. 原来重复的入口只保留一处：
    ///      - 视觉开关（游戏内视角 / 电脑摄像头 / 眼睛画中画）→ 视觉标签 + 快捷条「眼睛」
    ///      - 第一/第三人称、机位参数 → 相机标签 + 快捷条「视角」
    ///      - 走路开关 → 快捷条「走路」
    ///      - 表情/动作/姿势/灯光 → 角色标签（复用原 YixuanControlPanel，不再常驻右屏）
    ///      - 回复长度/记忆/流式/打断/思考 → 设置标签（复用原 ChatUI 设置面板，去掉其中重复的视觉/移动块）
    ///      - 任务执行 → 任务标签（复用原 TaskMissionPanel，去掉其中重复的视觉/第一人称/语音开关）
    ///   3. 右上角一条快捷条始终可见；右下角原来被各种面板占用的区域空出来。
    ///   4. 小地图从地图碰撞/导航数据离线烘焙成一张纹理，显示角色位置/朝向、建筑、可去地点；
    ///      地图标签里还能直接点「走过去」。
    /// </summary>
    [DisallowMultipleComponent]
    public class UnifiedHud : MonoBehaviour
    {
        public enum Tab { Map = 0, Task = 1, Vision = 2, Camera = 3, Character = 4, Settings = 5 }
        static readonly string[] TabNames = { "地图", "任务", "视觉", "相机", "角色", "设置" };

        [Header("开场状态")]
        public bool OpenOnStart = true;
        public Tab StartTab = Tab.Map;
        [Tooltip("左侧面板宽度（像素）")]
        public float PanelWidth = 478f;

        bool _open;
        Tab _tab;
        string _status = "";

        /// <summary>Single live HUD instance (let the big map hide/restore the console).</summary>
        public static UnifiedHud Instance { get; private set; }
        public static bool IsConsoleVisible { get { return Instance != null && Instance._open; } }

        // ---- read-only test hooks (node-2 HUD/minimap regression probe) ----
        public static Texture2D MinimapTextureForTest
        {
            get { return Instance != null ? Instance._mapTex : null; }
        }

        public static int TabForTest
        {
            get { return Instance != null ? (int)Instance._tab : -1; }
        }

        public static int MinimapSizeForTest
        {
            get { return Instance != null ? Instance._mapW * 100000 + Instance._mapH : -1; }
        }

        public static void SetConsoleVisible(bool visible)
        {
            if (Instance == null) return;
            Instance._open = visible;
            Instance.SyncPanels();
        }

        public static void OpenBigMap()
        {
            if (Yixuan.SelectionLab.SelectionLabContext.IsActive)
            {
                if (Instance != null)
                {
                    Instance._open = true;
                    Instance._tab = Tab.Map;
                    Instance.SyncPanels();
                }
                return;
            }
            Yixuan.UI.BigMapPanel.Open();
        }

        // 引用
        ChatUI _chat;
        TaskMissionPanel _task;
        Yixuan.UI.YixuanControlPanel _characterPanel;
        VisionObserver _vision;
        QwenVisionClient _client;
        VisionControlUI _visionUi;
        EyeViewPreview _eyePreview;
        CameraDirector _camera;
        CharacterLocomotion _loco;
        Yixuan.Core.YixuanCharacter _character;
        ChatSession _session;
        MapWorldProfile _map;

        // 小地图
        Texture2D _mapTex;
        Texture2D _arrowTex;
        int _mapW = 560, _mapH = 426;
        float _worldMinX = -200f, _worldMaxX = 200f, _worldMinZ = -152f, _worldMaxZ = 152f;
        readonly List<WorldAnchor> _nearest = new List<WorldAnchor>();
        Vector2 _anchorScroll;

        // 样式
        GUIStyle _box, _header, _tabOn, _tabOff, _label, _small, _bigBtn, _arrowLabel;
        Texture2D _texPanel, _texHeader, _texTabOn, _texTabOff, _texBtn;
        bool _stylesReady;

        // 相机标签的滑条缓存
        float _cDist, _cPitch, _cLook, _cFov, _cWalkSpeed;
        bool _camInited;

        // ---------------- 生命周期 ----------------

        void Awake()
        {
            Instance = this;
            _open = OpenOnStart;
            _tab = StartTab;
            Resolve();
            // EyeViewPreview 的默认位置在右上角 360px 处，会和右上快捷条/角色面板打架；HUD 接管到 64px。
            if (_eyePreview != null) _eyePreview.PreviewTopOffset = 64f;
        }

        void Start()
        {
            Resolve();
            BuildMinimap();
            SyncPanels();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            WorldMapPanel.Shutdown();
            if (_mapTex != null) Destroy(_mapTex);
            if (_arrowTex != null) Destroy(_arrowTex);
            if (_texPanel != null) Destroy(_texPanel);
            if (_texHeader != null) Destroy(_texHeader);
            if (_texTabOn != null) Destroy(_texTabOn);
            if (_texTabOff != null) Destroy(_texTabOff);
            if (_texBtn != null) Destroy(_texBtn);
        }

        void Resolve()
        {
            if (_chat == null) _chat = FindObjectOfType<ChatUI>();
            if (_task == null) _task = FindObjectOfType<TaskMissionPanel>();
            if (_characterPanel == null) _characterPanel = FindObjectOfType<YixuanControlPanel>();
            if (_vision == null) _vision = FindObjectOfType<VisionObserver>();
            if (_visionUi == null) _visionUi = FindObjectOfType<VisionControlUI>();
            if (_eyePreview == null) _eyePreview = FindObjectOfType<EyeViewPreview>();
            if (_camera == null) _camera = FindObjectOfType<CameraDirector>();
            if (_loco == null) _loco = FindObjectOfType<CharacterLocomotion>();
            if (_character == null) _character = FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            if (_session == null) _session = FindObjectOfType<ChatSession>();
            if (_client == null) _client = FindObjectOfType<QwenVisionClient>();
            if (_map == null) _map = FindObjectOfType<MapWorldProfile>();
        }

        void Update()
        {
            Resolve();
            HandleHotkeys();
            WorldMapPanel.Tick();
            WorldMapPanel.UpdatePointerBlock(HudRect(), _open && _tab == Tab.Map);
            if (_eyePreview == null && Time.frameCount % 60 == 0) { }
            SyncPanels();
            if (_camera != null && !_camInited)
            {
                _camInited = true;
                _cDist = _camera.TP_Distance; _cPitch = _camera.TP_Pitch;
                _cLook = _camera.TP_LookHeight; _cFov = _camera.TP_Fov;
                _cWalkSpeed = _loco != null ? _loco.WalkSpeed : 1.05f;
            }
        }

        void HandleHotkeys()
        {
            // M = big map. It must never steal text input from the chat/search box.
            if (Input.GetKeyDown(KeyCode.M) && !TextInputFocused())
            {
                if (Yixuan.SelectionLab.SelectionLabContext.IsActive) OpenBigMap();
                else Yixuan.UI.BigMapPanel.Toggle();
                return;
            }
            if (Input.GetKeyDown(KeyCode.F5)) { _open = true; _tab = Tab.Map; SyncPanels(); }
            else if (Input.GetKeyDown(KeyCode.F6)) { _open = true; _tab = Tab.Task; SyncPanels(); }
            else if (Input.GetKeyDown(KeyCode.F7)) { _open = true; _tab = Tab.Vision; SyncPanels(); }
            else if (Input.GetKeyDown(KeyCode.F8)) { _open = true; _tab = Tab.Camera; SyncPanels(); }
            else if (Input.GetKeyDown(KeyCode.F9)) { _open = true; _tab = Tab.Character; SyncPanels(); }
            else if (Input.GetKeyDown(KeyCode.F10)) { _open = true; _tab = Tab.Settings; SyncPanels(); }
            else if (Input.GetKeyDown(KeyCode.F4)) { _open = !_open; SyncPanels(); }
        }

        /// <summary>True while any IMGUI or UI Toolkit text field owns the keyboard.</summary>
        public static bool TextInputFocused()
        {
            if (GUIUtility.keyboardControl != 0) return true;
            var bigMap = Yixuan.UI.BigMapPanel.Instance;
            if (bigMap != null && Yixuan.UI.BigMapPanel.IsOpen && Yixuan.UI.BigMapPanel.SearchFocused) return true;
            var chat = GameObject.Find("UI_Chat");
            if (chat == null) return false;
            var doc = chat.GetComponent<UnityEngine.UIElements.UIDocument>();
            if (doc == null || doc.rootVisualElement == null || doc.rootVisualElement.panel == null) return false;
            var focused = doc.rootVisualElement.panel.focusController != null
                ? doc.rootVisualElement.panel.focusController.focusedElement : null;
            return focused != null;
        }

        float InputTop()
        {
            return _chat != null ? _chat.TaskInputTopScreenY : Screen.height - 84f;
        }

        Rect HudRect()
        {
            float h = Mathf.Max(220f, InputTop() - 64f - 10f);
            float width = _tab == Tab.Map && WorldMapPanel.Expanded ? Mathf.Min(Screen.width - 32f, 1100f) : PanelWidth;
            return new Rect(16f, 64f, Mathf.Min(width, Screen.width - 32f), h);
        }

        void OnApplicationFocus(bool focused) { if (!focused) WorldMapPanel.CancelPointerCapture(); }
        void OnDisable() { WorldMapPanel.CancelPointerCapture(); }

        Rect ContentRect()
        {
            var r = HudRect();
            return new Rect(r.x + 8f, r.y + 42f, r.width - 16f, r.height - 50f);
        }

        // ---------------- 面板可见性/位置同步 ----------------

        void SyncPanels()
        {
            var content = ContentRect();
            bool taskTab = _open && _tab == Tab.Task;
            bool charTab = _open && _tab == Tab.Character;
            bool setTab = _open && _tab == Tab.Settings;

            // 任务面板：嵌入左侧面板的「任务」页
            if (_task != null)
            {
                _task.Embedded = true;
                _task.EmbeddedRect = content;
                _task.Visible = taskTab;
            }

            // 角色面板（原右侧常驻大面板）：只在「角色」页出现，并搬到左侧内容区
            if (_characterPanel != null)
            {
                _characterPanel.SetPanelVisible(charTab);
                if (charTab) _characterPanel.ApplyHudRect(content.x, content.y, content.width, content.height);
            }

            // ChatUI 设置面板：只在「设置」页出现，搬进内容区；其余时候关掉（它原本是「面板」按钮弹窗）
            if (_chat != null)
            {
                _chat.SetHudMode(_open);
                if (setTab)
                {
                    _chat.SetControlPanelPublic(true);
                    _chat.ApplyHudRect(content.x, content.y, content.width, content.height);
                }
                else
                {
                    _chat.SetControlPanelPublic(false);
                }
            }

            // 旧的视觉小块（左上角 VisionControlUI 的 linkPanel）彻底让位给统一控制台
            if (_visionUi != null)
            {
                _visionUi.SetLinkPanelVisible(false);
                _visionUi.SetTaskPanelLayout(false);
            }
        }

        // ---------------- 小地图烘焙 ----------------

        void BuildMinimap()
        {
            if (Yixuan.SelectionLab.SelectionLabContext.IsActive) return;
            if (_map == null) _map = FindObjectOfType<MapWorldProfile>();
            if (_map != null) _map.EnsureData();

            if (_map != null)
            {
                _worldMinX = _map.WalkableMin.x; _worldMaxX = _map.WalkableMax.x;
                _worldMinZ = _map.WalkableMin.y; _worldMaxZ = _map.WalkableMax.y;
            }
            float aspect = (_worldMaxZ - _worldMinZ) / Mathf.Max(1f, _worldMaxX - _worldMinX);
            _mapW = 560;
            _mapH = Mathf.RoundToInt(_mapW * aspect);
            _mapTex = new Texture2D(_mapW, _mapH, TextureFormat.RGBA32, false);
            _mapTex.wrapMode = TextureWrapMode.Clamp;
            _mapTex.filterMode = FilterMode.Bilinear;
            var px = new Color32[_mapW * _mapH];
            Color32 bg = new Color32(13, 17, 24, 255);
            for (int i = 0; i < px.Length; i++) px[i] = bg;

            // 1) 导航网络（可走的路/广场）—— 淡蓝灰
            Color32 road = new Color32(58, 74, 94, 255);
            if (_map != null && _map.Nodes != null)
            {
                for (int e = 0; e < _map.Edges.Length; e++)
                {
                    int a = _map.Edges[e].a, b = _map.Edges[e].b;
                    if (a < 0 || b < 0 || a >= _map.Nodes.Length || b >= _map.Nodes.Length) continue;
                    DrawLine(px, _map.Nodes[a].x, _map.Nodes[a].z, _map.Nodes[b].x, _map.Nodes[b].z, road);
                }
            }

            // 2) 建筑/水体/道具（来自碰撞盒原始 AABB）
            if (_map != null && _map.RawObstacleBounds != null)
            {
                Color32 building = new Color32(96, 105, 122, 255);
                Color32 landmark = new Color32(126, 136, 156, 255);
                Color32 water = new Color32(34, 62, 94, 255);
                Color32 terrace = new Color32(56, 64, 78, 255);
                Color32 prop = new Color32(80, 84, 94, 255);
                for (int i = 0; i < _map.RawObstacleBounds.Count; i++)
                {
                    var b = _map.RawObstacleBounds[i];
                    string kind = i < _map.RawObstacleKinds.Count ? _map.RawObstacleKinds[i] : "";
                    Color32 c;
                    if (kind == "building") c = building;
                    else if (kind == "landmark") c = landmark;
                    else if (kind == "water") c = water;
                    else if (kind == "terrace_step" || kind == "terrace_platform" || kind == "platform") c = terrace;
                    else c = prop;
                    FillRect(px, b.min.x, b.max.x, b.min.z, b.max.z, c);
                }
            }

            // 3) 可去地点
            if (_map != null)
            {
                Color32 dot = new Color32(245, 205, 120, 255);
                for (int i = 0; i < _map.Anchors.Count; i++)
                {
                    var a = _map.Anchors[i];
                    FillCircle(px, a.Position.x, a.Position.z, 2, dot);
                }
            }

            // 4) 地图边界
            Color32 border = new Color32(120, 138, 166, 255);
            DrawLine(px, _worldMinX, _worldMinZ, _worldMaxX, _worldMinZ, border);
            DrawLine(px, _worldMaxX, _worldMinZ, _worldMaxX, _worldMaxZ, border);
            DrawLine(px, _worldMaxX, _worldMaxZ, _worldMinX, _worldMaxZ, border);
            DrawLine(px, _worldMinX, _worldMaxZ, _worldMinX, _worldMinZ, border);

            _mapTex.SetPixels32(px);
            _mapTex.Apply(false, false);

            // 玩家箭头（朝上 = +Z 北）
            _arrowTex = new Texture2D(24, 24, TextureFormat.RGBA32, false);
            _arrowTex.filterMode = FilterMode.Bilinear;
            var ap = new Color32[24 * 24];
            for (int i = 0; i < ap.Length; i++) ap[i] = new Color32(0, 0, 0, 0);
            Color32 ac = new Color32(90, 240, 255, 255);
            for (int y = 1; y < 22; y++)
                for (int x = 0; x < 24; x++)
                {
                    int half = Mathf.Max(1, (22 - y) / 2);
                    if (Mathf.Abs(x - 12) <= half) ap[y * 24 + x] = ac;
                }
            _arrowTex.SetPixels32(ap);
            _arrowTex.Apply(false, false);
        }

        void WorldToPixel(float x, float z, out float px, out float py)
        {
            px = (x - _worldMinX) / Mathf.Max(0.001f, _worldMaxX - _worldMinX) * (_mapW - 1);
            py = (z - _worldMinZ) / Mathf.Max(0.001f, _worldMaxZ - _worldMinZ) * (_mapH - 1);
        }

        void DrawLine(Color32[] px, float x0, float z0, float x1, float z1, Color32 c)
        {
            float ax, ay, bx, by;
            WorldToPixel(x0, z0, out ax, out ay);
            WorldToPixel(x1, z1, out bx, out by);
            int ix0 = Mathf.RoundToInt(ax), iy0 = Mathf.RoundToInt(ay);
            int ix1 = Mathf.RoundToInt(bx), iy1 = Mathf.RoundToInt(by);
            int dx = Mathf.Abs(ix1 - ix0), dy = Mathf.Abs(iy1 - iy0);
            int sx = ix0 < ix1 ? 1 : -1, sy = iy0 < iy1 ? 1 : -1;
            int err = dx - dy;
            int guard = 0;
            while (guard++ < 5000)
            {
                Plot(px, ix0, iy0, c);
                if (ix0 == ix1 && iy0 == iy1) break;
                int e2 = err * 2;
                if (e2 > -dy) { err -= dy; ix0 += sx; }
                if (e2 < dx) { err += dx; iy0 += sy; }
            }
        }

        void FillRect(Color32[] px, float minX, float maxX, float minZ, float maxZ, Color32 c)
        {
            float ax, ay, bx, by;
            WorldToPixel(minX, minZ, out ax, out ay);
            WorldToPixel(maxX, maxZ, out bx, out by);
            int x0 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(ax, bx)), 0, _mapW - 1);
            int x1 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(ax, bx)), 0, _mapW - 1);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(ay, by)), 0, _mapH - 1);
            int y1 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(ay, by)), 0, _mapH - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    px[y * _mapW + x] = c;
        }

        void FillCircle(Color32[] px, float wx, float wz, int r, Color32 c)
        {
            float ax, ay;
            WorldToPixel(wx, wz, out ax, out ay);
            int cx = Mathf.RoundToInt(ax), cy = Mathf.RoundToInt(ay);
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    if (x * x + y * y > r * r) continue;
                    int xx = cx + x, yy = cy + y;
                    if (xx < 0 || yy < 0 || xx >= _mapW || yy >= _mapH) continue;
                    px[yy * _mapW + xx] = c;
                }
        }

        void Plot(Color32[] px, int x, int y, Color32 c)
        {
            if (x < 0 || y < 0 || x >= _mapW || y >= _mapH) return;
            px[y * _mapW + x] = c;
        }

        // ---------------- 绘制 ----------------

        void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;
            _texPanel = Solid(new Color(0.055f, 0.065f, 0.085f, 0.94f));
            _texHeader = Solid(new Color(0.11f, 0.13f, 0.17f, 0.98f));
            _texTabOn = Solid(new Color(0.24f, 0.36f, 0.55f, 1f));
            _texTabOff = Solid(new Color(0.10f, 0.12f, 0.16f, 1f));
            _texBtn = Solid(new Color(0.15f, 0.19f, 0.26f, 1f));

            _box = new GUIStyle(GUI.skin.box);
            _box.normal.background = _texPanel;
            _header = new GUIStyle(GUI.skin.label);
            _header.fontSize = 14;
            _header.fontStyle = FontStyle.Bold;
            _header.normal.textColor = new Color(0.88f, 0.92f, 1f);
            _tabOn = new GUIStyle(GUI.skin.button);
            _tabOn.normal.background = _texTabOn; _tabOn.hover.background = _texTabOn;
            _tabOn.normal.textColor = Color.white; _tabOn.fontSize = 13;
            _tabOff = new GUIStyle(GUI.skin.button);
            _tabOff.normal.background = _texTabOff; _tabOff.hover.background = _texBtn;
            _tabOff.normal.textColor = new Color(0.68f, 0.74f, 0.84f); _tabOff.fontSize = 13;
            _label = new GUIStyle(GUI.skin.label);
            _label.fontSize = 12; _label.wordWrap = true;
            _label.normal.textColor = new Color(0.82f, 0.87f, 0.95f);
            _small = new GUIStyle(GUI.skin.label);
            _small.fontSize = 11; _small.wordWrap = true;
            _small.normal.textColor = new Color(0.58f, 0.65f, 0.76f);
            _bigBtn = new GUIStyle(GUI.skin.button);
            _bigBtn.fontSize = 13;
            _arrowLabel = new GUIStyle(GUI.skin.label);
            _arrowLabel.fontSize = 15; _arrowLabel.alignment = TextAnchor.MiddleCenter;
            _arrowLabel.normal.textColor = new Color(0.75f, 0.85f, 1f);
        }

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply(false, false);
            return t;
        }

        void OnGUI()
        {
            Resolve();
            EnsureStyles();
            DrawQuickBar();
            if (!_open) { WorldMapPanel.CancelPointerCapture(); return; }

            var hud = HudRect();
            bool nativeTab = _tab == Tab.Map || _tab == Tab.Vision || _tab == Tab.Camera;
            if (nativeTab)
            {
                GUI.Box(hud, GUIContent.none, _box);
            }
            else
            {
                // 任务/角色/设置页由各自面板自己画内容；HUD 只画标题条和细边框，避免半透明底把表格压暗。
                GUI.DrawTexture(new Rect(hud.x, hud.y, hud.width, 34f), _texHeader);
                GUI.DrawTexture(new Rect(hud.x, hud.y, 1f, hud.height), _texTabOff);
                GUI.DrawTexture(new Rect(hud.xMax - 1f, hud.y, 1f, hud.height), _texTabOff);
                GUI.DrawTexture(new Rect(hud.x, hud.yMax - 1f, hud.width, 1f), _texTabOff);
            }

            // 标题 + 标签（标题压窄，保证 6 个标签都能露全）
            GUI.Label(new Rect(hud.x + 8, hud.y + 6, 112, 22), "仪玄 · 控制台", _header);
            float tabX = hud.x + 120f;
            float tabW = 44f;
            for (int i = 0; i < TabNames.Length; i++)
            {
                bool on = (int)_tab == i;
                if (GUI.Button(new Rect(tabX + i * (tabW + 1f), hud.y + 5f, tabW, 24f), TabNames[i], on ? _tabOn : _tabOff))
                {
                    _tab = (Tab)i;
                    SyncPanels();
                }
            }
            if (GUI.Button(new Rect(hud.xMax - 80f, hud.y + 5f, 38f, 24f), "大图", _tabOff))
            {
                OpenBigMap();
            }
            if (GUI.Button(new Rect(hud.xMax - 38f, hud.y + 5f, 30f, 24f), "×", _tabOff))
            {
                _open = false;
                SyncPanels();
            }


            var content = ContentRect();
            if (_tab != Tab.Map) WorldMapPanel.CancelPointerCapture();
            switch (_tab)
            {
                case Tab.Map: DrawMapTab(content); break;
                case Tab.Vision: DrawVisionTab(content); break;
                case Tab.Camera: DrawCameraTab(content); break;
                default: DrawEmbeddedHint(content); break;
            }

            if (!string.IsNullOrEmpty(_status))
                GUI.Label(new Rect(content.x, content.yMax - 20f, content.width, 18f), _status, _small);
        }

        void DrawEmbeddedHint(Rect content)
        {
            // 任务/角色/设置页由各自面板绘制，HUD 这里不画额外内容，避免盖住/干扰它们。
        }

        // ---------- 快捷条 ----------

        void DrawQuickBar()
        {
            EnsureStyles();
            string walk = _loco != null ? (_loco.WalkingEnabled ? "走路：开" : "走路：关") : "走路：?";
            string view = _camera != null ? (_camera.EyeView ? "第一人称" : "第三人称") : "视角：?";
            string voice = (_session != null && _session.Config != null && _session.Config.VoiceEnabled) ? "语音：开" : "语音：关";
            string eye = (_eyePreview != null && _eyePreview.PreviewEnabled) ? "眼睛：开" : "眼睛：关";
            string task = "任务空闲";
            if (_session != null && _session.Missions != null)
            {
                var m = _session.Missions;
                if (m.IsSuspended) task = "任务插话";
                else if (m.IsRunning) task = "任务运行中";
            }
            string[] labels = { "面板", "地图", task, walk, view, voice, eye };
            float bw = 76f, gap = 4f;
            float total = labels.Length * bw + (labels.Length - 1) * gap;
            float x = Screen.width - 16f - total;
            float y = 14f;
            for (int i = 0; i < labels.Length; i++)
            {
                var r = new Rect(x + i * (bw + gap), y, bw, 28f);
                if (!GUI.Button(r, labels[i])) continue;
                if (i == 0) { _open = !_open; SyncPanels(); }
                else if (i == 1) { _open = true; _tab = Tab.Map; SyncPanels(); }
                else if (i == 2) { _open = true; _tab = Tab.Task; SyncPanels(); }
                else if (i == 3) { if (_loco != null) _loco.ToggleWalking(); }
                else if (i == 4) { if (_camera != null) _camera.EyeView = !_camera.EyeView; }
                else if (i == 5) { if (_session != null && _session.Config != null) _session.SetVoiceOutputEnabled(!_session.Config.VoiceEnabled); }
                else if (i == 6) { if (_eyePreview != null) _eyePreview.SetPreviewEnabled(!_eyePreview.PreviewEnabled); }
            }
        }

        // ---------- 地图页 ----------

        void DrawMapTab(Rect content)
        {
            if (Yixuan.SelectionLab.SelectionLabContext.IsActive)
            {
                var labHud = FindObjectOfType<Yixuan.SelectionLab.SelectionLabHud>();
                if (labHud != null) labHud.DrawConsole(content);
                else GUI.Label(content, "实验室界面未就绪", _label);
                return;
            }
            // The fine world-map panel owns the map tab when its generated data
            // package is available. The old procedural minimap remains a fallback
            // for an incomplete checkout.
            if (WorldMapPanel.Draw(content, _loco, _character, _map)) return;
            if (_mapTex == null) BuildMinimap();
            if (_mapTex == null) { GUI.Label(content, "小地图：地图数据未就绪", _label); return; }
            if (_map == null) _map = FindObjectOfType<MapWorldProfile>();

            float mapW = content.width;
            float mapH = mapW * _mapH / Mathf.Max(1, _mapW);
            float maxH = content.height * 0.52f;
            if (mapH > maxH) { mapH = maxH; mapW = mapH * _mapW / Mathf.Max(1, _mapH); }
            Rect mapRect = new Rect(content.x + (content.width - mapW) * 0.5f, content.y, mapW, mapH);
            GUI.DrawTexture(mapRect, _mapTex, ScaleMode.StretchToFill, false);

            Vector3 pos = _character != null ? _character.transform.position : (_loco != null ? _loco.transform.position : Vector3.zero);
            float yaw = _character != null ? _character.transform.eulerAngles.y : 0f;

            // 可去地点小点
            if (_map != null)
            {
                for (int i = 0; i < _map.Anchors.Count; i++)
                {
                    var a = _map.Anchors[i];
                    float px, py;
                    WorldToPixel(a.Position.x, a.Position.z, out px, out py);
                    float sx = mapRect.x + px / (_mapW - 1) * mapRect.width;
                    float sy = mapRect.yMax - py / (_mapH - 1) * mapRect.height;
                    GUI.DrawTexture(new Rect(sx - 2, sy - 2, 4, 4), Texture2D.whiteTexture);
                }
            }

            // 角色箭头
            float ppx, ppy;
            WorldToPixel(pos.x, pos.z, out ppx, out ppy);
            float ax = mapRect.x + ppx / (_mapW - 1) * mapRect.width;
            float ay = mapRect.yMax - ppy / (_mapH - 1) * mapRect.height;
            var saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(-yaw, new Vector2(ax, ay));
            GUI.DrawTexture(new Rect(ax - 10, ay - 10, 20, 20), _arrowTex);
            GUI.matrix = saved;

            GUI.Label(new Rect(mapRect.x + 4, mapRect.y + 2, mapRect.width - 8, 18), "N ↑   黄色点=可去地点，箭头=角色朝向", _small);

            // 当前位置
            string zone = Yixuan.World.WorldData.ZoneAt(pos);
            GUI.Label(new Rect(content.x, mapRect.yMax + 4, content.width, 18),
                      "当前位置：" + zone + "  （x=" + pos.x.ToString("F0") + ", z=" + pos.z.ToString("F0") + "）", _small);

            // 附近可去地点（点击走过去）
            ComputeNearest(pos, 12);
            GUI.Label(new Rect(content.x, mapRect.yMax + 22, content.width, 18), "附近可去地点（点一下就走过去）：", _label);
            Rect listRect = new Rect(content.x, mapRect.yMax + 40, content.width, Mathf.Max(40, content.yMax - mapRect.yMax - 64));
            _anchorScroll = GUI.BeginScrollView(listRect, _anchorScroll, new Rect(0, 0, content.width - 20, _nearest.Count * 22f + 4f));
            for (int i = 0; i < _nearest.Count; i++)
            {
                var a = _nearest[i];
                Vector3 d = a.Position - pos; d.y = 0;
                float dist = d.magnitude;
                string arrow = Bearing(d);
                string label = arrow + " " + a.Label + "   " + dist.ToString("F0") + "m";
                if (GUI.Button(new Rect(0, i * 22f, content.width - 24, 20f), label, _small))
                {
                    if (_loco != null)
                    {
                        bool ok = _loco.MoveToAnchor(a.Id);
                        _status = ok ? ("正在前往：" + a.Label) : ("无法前往：" + a.Label + "（" + _loco.LastError + "）");
                    }
                }
            }
            GUI.EndScrollView();
        }

        static string Bearing(Vector3 d)
        {
            if (d.sqrMagnitude < 0.01f) return "●";
            float ang = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; // 0=+Z=北
            if (ang < 0) ang += 360f;
            string[] arrows = { "↑", "↗", "→", "↘", "↓", "↙", "←", "↖" };
            int idx = Mathf.RoundToInt(ang / 45f) % 8;
            return arrows[idx];
        }

        void ComputeNearest(Vector3 pos, int count)
        {
            _nearest.Clear();
            if (_map == null) return;
            var list = new List<WorldAnchor>(_map.Anchors);
            list.Sort(delegate (WorldAnchor a, WorldAnchor b)
            {
                float da = (a.Position - pos).sqrMagnitude;
                float db = (b.Position - pos).sqrMagnitude;
                return da.CompareTo(db);
            });
            for (int i = 0; i < list.Count && i < count; i++) _nearest.Add(list[i]);
        }

        // ---------- 视觉页 ----------

        void DrawVisionTab(Rect content)
        {
            float y = content.y;
            if (Yixuan.SelectionLab.SelectionLabContext.IsActive)
            {
                GUI.Label(new Rect(content.x, y, content.width, 88f),
                    "第一人称视觉选中\n在聊天框描述物体，系统会截图、定位并自动对准确认。\n当前场景按请求识图；不启动周期观察。", _label);
                y += 96f;
                bool previewOn = _eyePreview != null && _eyePreview.PreviewEnabled;
                if (GUI.Button(new Rect(content.x, y, content.width, 28f),
                    "眼睛画中画：" + (previewOn ? "开" : "关"), _bigBtn) && _eyePreview != null)
                    _eyePreview.SetPreviewEnabled(!previewOn);
                y += 36f;
                GUI.Label(new Rect(content.x, y, content.width, 120f),
                    "状态：" + Yixuan.Pick.FirstPersonTargetingService.LastStatus + "\n" +
                    (string.IsNullOrEmpty(Yixuan.Pick.FirstPersonTargetingService.LastError)
                        ? Yixuan.Pick.FirstPersonTargetingService.LastReply
                        : Yixuan.Pick.FirstPersonTargetingService.LastError), _small);
                return;
            }
            if (_vision == null)
            {
                GUI.Label(new Rect(content.x, y, content.width, 20), "场景里没有 VisionObserver", _label);
                return;
            }
            float btnW = (content.width - 8) * 0.5f;
            string game = "游戏内视角：" + (_vision.GameViewEnabled ? "开" : "关");
            string cam = "电脑摄像头：" + (_vision.WebcamEnabled ? "开" : "关");
            if (GUI.Button(new Rect(content.x, y, btnW, 32), game, _bigBtn)) _vision.ToggleGameView();
            if (GUI.Button(new Rect(content.x + btnW + 8, y, btnW, 32), cam, _bigBtn)) _vision.ToggleWebcam();
            y += 40;
            bool eyeOn = _eyePreview != null && _eyePreview.PreviewEnabled;
            if (GUI.Button(new Rect(content.x, y, btnW, 28), "眼睛画中画：" + (eyeOn ? "开" : "关"), _bigBtn) && _eyePreview != null)
                _eyePreview.SetPreviewEnabled(!_eyePreview.PreviewEnabled);
            if (GUI.Button(new Rect(content.x + btnW + 8, y, btnW, 28), "视锥线：" + (_eyePreview != null && _eyePreview.ShowViewCone ? "开" : "关"), _bigBtn) && _eyePreview != null)
                _eyePreview.ShowViewCone = !_eyePreview.ShowViewCone;
            y += 40;

            GUI.Label(new Rect(content.x, y, content.width, 60), VisionStatusText(), _label);
            y += 64;
            if (GUI.Button(new Rect(content.x, y, content.width, 28), "重置额度退避（改了 key / 额度后点这里）", _bigBtn))
            {
                if (_client != null) _client.ResetServiceBackoff();
                _status = "已重置视觉服务退避";
            }
            y += 36;
            GUI.Label(new Rect(content.x, y, content.width, 120),
                "视觉模型：" + (_client != null ? _client.Model : "?") + "\n" +
                "回退链：" + (_client != null ? string.Join(" → ", _client.FallbackModels) : "?") + "\n" +
                "两路都关时不发请求、不耗额度。\n" +
                "（额度不足时 HUD 会显示服务状态，不会静默失败。）", _small);
        }

        string VisionStatusText()
        {
            if (_vision == null) return "无 VisionObserver";
            if (_client == null) return "无视觉客户端";
            if (!_client.HasKey) return "未找到视觉 API key（检查 _vision_poc/qwen_apikey.txt）";
            if (!_vision.GameViewEnabled && !_vision.WebcamEnabled) return "两路都关 · 不消耗额度";
            if (_client.ServiceBlocked) return "服务暂停：" + _client.ServiceStatus;
            if (_vision.Asking) return "正在看图回答…";
            if (_vision.RequestInFlight) return "正在观察画面…";
            if (!string.IsNullOrEmpty(_vision.LastError)) return "视觉错误：" + _vision.LastError;
            if (!string.IsNullOrEmpty(_vision.LastRealtimeAnswer)) return "刚回答：" + Trim(_vision.LastRealtimeAnswer, 80);
            if (!string.IsNullOrEmpty(_vision.LastObservation)) return "已观察 " + _vision.TimelineCount + " 条：" + Trim(_vision.LastObservation, 60);
            return "等待第一次观察…";
        }

        static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        // ---------- 相机页 ----------

        void DrawCameraTab(Rect content)
        {
            if (_camera == null) { GUI.Label(content, "找不到 CameraDirector", _label); return; }
            float y = content.y;
            float btnW = (content.width - 8) * 0.5f;
            if (GUI.Button(new Rect(content.x, y, btnW, 30), _camera.EyeView ? "当前：第一人称" : "当前：第三人称", _bigBtn))
                _camera.EyeView = !_camera.EyeView;
            if (GUI.Button(new Rect(content.x + btnW + 8, y, btnW, 30), "重置第三人称机位", _bigBtn))
            {
                _camera.ResetThirdPersonView();
                _cDist = _camera.TP_Distance; _cPitch = _camera.TP_Pitch; _cLook = _camera.TP_LookHeight; _cFov = _camera.TP_Fov;
            }
            y += 38;

            y = Slider(content, y, "距离", ref _cDist, 2f, 9f, delegate (float v) { _camera.TP_Distance = v; });
            y = Slider(content, y, "俯仰", ref _cPitch, -10f, 40f, delegate (float v) { _camera.TP_Pitch = v; });
            y = Slider(content, y, "看向高度", ref _cLook, 0.6f, 1.8f, delegate (float v) { _camera.TP_LookHeight = v; });
            y = Slider(content, y, "视野 FOV", ref _cFov, 25f, 70f, delegate (float v) { _camera.TP_Fov = v; });
            y = Slider(content, y, "走路速度", ref _cWalkSpeed, 0.4f, 2.5f, delegate (float v) { if (_loco != null) _loco.WalkSpeed = v; });

            if (GUI.Button(new Rect(content.x, y, btnW, 28), "预设：面对面 3/4", _bigBtn))
            {
                _camera.TP_YawOffset = 210f; _camera.TP_Pitch = 6.5f; _camera.TP_Distance = 3.9f;
                _camera.SnapThirdPersonView();
            }
            if (GUI.Button(new Rect(content.x + btnW + 8, y, btnW, 28), "预设：越肩跟随", _bigBtn))
            {
                _camera.TP_YawOffset = 0f; _camera.TP_Pitch = 8f; _camera.TP_Distance = 4.2f;
                _camera.SnapThirdPersonView();
            }
            y += 36;
            _camera.TP_MouseOrbit = GUI.Toggle(new Rect(content.x, y, content.width, 22), _camera.TP_MouseOrbit, " 右键拖拽环绕 / 滚轮缩放");
            y += 24;
            _camera.TP_Collision = GUI.Toggle(new Rect(content.x, y, content.width, 22), _camera.TP_Collision, " 相机被建筑挡住时自动拉近");
            y += 30;
            GUI.Label(new Rect(content.x, y, content.width, 120),
                "第三人称：\n  角色朝向 yaw = " + _camera.FollowTarget.eulerAngles.y.ToString("F0") + "°\n" +
                "  相机偏角 yawOffset = " + _camera.TP_YawOffset.ToString("F0") + "°（0=越肩，180=面对面）\n" +
                "  说话时会自动推近 " + _camera.TP_TalkPullIn.ToString("F2") + "m", _small);
        }

        float Slider(Rect content, float y, string label, ref float value, float min, float max, Action<float> onChange)
        {
            GUI.Label(new Rect(content.x, y, 70, 20), label, _small);
            float v = GUI.HorizontalSlider(new Rect(content.x + 76, y + 7, content.width - 150, 16), value, min, max);
            GUI.Label(new Rect(content.xMax - 66, y, 66, 20), value.ToString("F2"), _small);
            if (Mathf.Abs(v - value) > 0.0001f) { value = v; onChange(v); }
            return y + 24;
        }
    }
}
