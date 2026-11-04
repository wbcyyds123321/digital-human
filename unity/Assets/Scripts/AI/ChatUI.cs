using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yixuan.AI
{
    /// <summary>
    /// 最简聊天 UI（UI Toolkit，纯 C# 构建，不依赖 UXML/USS）。
    ///
    /// 布局（全部内联样式，风格与 YixuanControlPanel 一致）：
    ///   左上角  角色名「庄方宜」
    ///   底部中央 输入行（TextField + 「发送」按钮，回车也发送）
    ///   输入行上方 字幕气泡（半透明圆角、自动换行、最大宽度 70%，初始隐藏）
    ///   右下角  状态文字（在线 / 思考中… / 说话中…）
    ///
    /// 挂载方式：场景根物体 UI_Chat 上挂本脚本 + UIDocument，
    /// UIDocument.panelSettings 指向 Assets/UI/YixuanPanelSettings.asset。
    ///
    /// 【为什么纯 C# 构建】
    /// 项目里没有主题 USS 资产（见 YixuanControlPanel 的说明），
    /// 全部用 element.style.xxx 内联设置最可靠；也不依赖 UXML 导入设置。
    ///
    /// 【设计约束】
    /// - 只负责“显示”，不持有任何对话逻辑 —— 一切由 ChatSession 驱动；
    /// - 任何方法在 UI 未构建（panelSettings 为空等）时都必须静默返回，不能抛异常；
    /// - Lead 会直接用 execute_code 调 ChatSession.Submit(...) 验收，
    ///   所以 UI 不能是对话流程的必要条件。
    /// </summary>
    [DisallowMultipleComponent]
    public partial class ChatUI : MonoBehaviour
    {
        [Header("引用（留空自动查找）")]
        public ChatSession Session;

        [Header("角色名（留空则用 AIConfig.CharacterName，再退「庄方宜」）")]
        public string CharacterName = "";

        // ---- 配色（和 YixuanControlPanel 保持同一套）----
        static readonly Color BubbleBg = new Color(0.09f, 0.10f, 0.13f, 0.86f);
        static readonly Color InputBg = new Color(0.12f, 0.13f, 0.17f, 0.92f);
        static readonly Color Accent = new Color(0.42f, 0.62f, 0.95f, 1f);
        static readonly Color TextMain = new Color(0.92f, 0.93f, 0.96f, 1f);
        static readonly Color TextDim = new Color(0.62f, 0.65f, 0.72f, 1f);
        static readonly Color BorderCol = new Color(0.30f, 0.34f, 0.42f, 0.85f);

        UIDocument _doc;
        VisualElement _root;
        Label _nameLabel;
        Label _subtitle;
        Label _status;
        TextField _input;
        Button _sendBtn;
        bool _built;
        bool _hudMode;
        Button _settingsCloseBtn;
        bool _inputEnabled = true;
        bool _warnedNoSession;
        bool _inputFocused;
        Label _placeholder;

        // ---- 表现层（2026-10-18 交互优化）----
        VisualElement _scrim;        // 底部遮罩条：增强字幕/输入框可读性
        Label _hint;                 // F1/F2 操作提示（10 秒后自动淡出）
        float _speechAlpha = 1f;
        string _statusBase = "";
        float _statusDotTimer;
        float _hintTimer;
        bool _devPanelHiddenOnce;

        // ---- 契约 v2：内心想法 + 行为面板 ----
        VisualElement _speech;      // 字幕区容器（内心想法在上、字幕在下）
        Label _feeling;
        VisualElement _panel;       // 行为状态（现在嵌进统一控制面板）
        Label _behaviorTitle;
        Label _panelSource;
        VisualElement _panelRows;

        // ---- 统一控制面板（点击按钮开关，F1/F2/F3 仅保留快捷键）----
        Button _panelToggleBtn;
        bool _controlPanelOpen;

        // ---- 设置/记忆面板（F3）----
        VisualElement _settingsPanel;
        Slider _lenSlider;
        Label _lenValue;
        Slider _memSlider;
        Label _memValue;
        DropdownField _streamDrop;
        Toggle _bargeToggle;
        Toggle _thinkToggle;
        DropdownField _thinkDrop;
        Label _memoryStats;
        Label _settingsSaved;
        bool _settingsRefreshing;

        // ---- 视觉链路（嵌进统一控制面板；面板关闭时用左上角独立小面板）----
        Yixuan.Vision.VisionObserver _vision;
        Yixuan.Vision.VisionControlUI _visionUI;
        Button _visGameBtn;
        Button _visCamBtn;
        Label _visStatus;
        float _visNextRefresh;

        // ---- 移动（嵌进统一控制面板）----
        Yixuan.Motion.CharacterLocomotion _locomotion;
        Button _moveBtn;
        Label _moveStatus;

        static readonly Color OkCol = new Color(0.55f, 0.85f, 0.60f, 1f);
        static readonly Color AutoCol = new Color(0.62f, 0.65f, 0.72f, 1f);
        static readonly Color MissCol = new Color(1.00f, 0.55f, 0.45f, 1f);

        void Awake()
        {
            ResolveRefs();
            Build();
        }

        void Start()
        {
            // Awake 时 panelSettings 可能还没赋上（场景加载顺序），这里补建一次
            if (!_built) Build();
            ResolveRefs();

            // 演示默认隐藏左侧开发者面板（F1 可打开）——design.md 6.1 的"演示时隐藏"
            if (!_devPanelHiddenOnce) { _devPanelHiddenOnce = true; SetDevPanelVisible(false); }
            RefreshSettingsPanel();
        }

        void ResolveRefs()
        {
            if (Session == null) Session = FindObjectOfType<ChatSession>();
        }

        // ============================================================
        //  构建 UI
        // ============================================================
        void Build()
        {
            if (_built) return;

            _doc = GetComponent<UIDocument>();
            if (_doc == null) _doc = gameObject.AddComponent<UIDocument>();

            if (_doc.panelSettings == null)
            {
                Debug.LogError("[ChatUI] UIDocument.panelSettings 为空，聊天 UI 不会显示。" +
                               "请把 Assets/UI/YixuanPanelSettings.asset 拖到 UI_Chat 的 UIDocument 上。");
                return;
            }

            _root = _doc.rootVisualElement;
            if (_root == null)
            {
                Debug.LogError("[ChatUI] UIDocument.rootVisualElement 为空，聊天 UI 不会显示。");
                return;
            }

            _root.Clear();
            _root.pickingMode = PickingMode.Ignore;   // 根节点不拦截点击
            // 铺满屏幕（背景透明，别挡住后面的角色）
            _root.style.position = Position.Absolute;
            _root.style.left = 0; _root.style.top = 0;
            _root.style.right = 0; _root.style.bottom = 0;
            _root.style.backgroundColor = new Color(0f, 0f, 0f, 0f);

            BuildScrim();
            BuildNameLabel();
            BuildSubtitle();
            BuildBehaviorPanel();
            BuildStatusLabel();
            BuildHint();
            BuildInputRow();

            _built = true;
        }

        /// <summary>底部遮罩：字幕/输入框压在亮背景或深色裙摆上也能看清（不抢角色）
        /// UI Toolkit 2022 没有背景渐变，这里用多条半透明色带叠出"从下往上渐变"，避免硬边。</summary>
        void BuildScrim()
        {
            _scrim = new VisualElement();
            _scrim.style.position = Position.Absolute;
            _scrim.style.left = 0; _scrim.style.right = 0; _scrim.style.bottom = 0;
            _scrim.style.height = 200;
            _scrim.pickingMode = PickingMode.Ignore;

            const int bands = 10;
            for (int i = 0; i < bands; i++)
            {
                var band = new VisualElement();
                band.style.position = Position.Absolute;
                band.style.left = 0; band.style.right = 0;
                band.style.bottom = i * 20;
                band.style.height = 21;                      // 相邻重叠 1px，避免出现缝
                float t = i / (float)(bands - 1);            // 0=最底（最暗）→1=最上（透明）
                band.style.backgroundColor = new Color(0.02f, 0.03f, 0.05f, Mathf.Lerp(0.62f, 0.04f, t));
                band.pickingMode = PickingMode.Ignore;
                _scrim.Add(band);
            }
            _root.Add(_scrim);
        }

        /// <summary>左上角 F1/F2 操作提示（10 秒后自动淡出，不干扰演示）</summary>
        void BuildHint()
        {
            _hint = new Label("点击右上角「面板」打开设置 / 记忆 / 行为   ·   F1/F2/F3 快捷键");
            _hint.style.position = Position.Absolute;
            _hint.style.left = 26;
            _hint.style.top = 62;
            _hint.style.fontSize = 12;
            _hint.style.color = new Color(0.62f, 0.65f, 0.72f, 0.85f);
            _hint.pickingMode = PickingMode.Ignore;
            _root.Add(_hint);
        }

        void BuildNameLabel()
        {
            string name = CharacterName;
            if (string.IsNullOrEmpty(name) && Session != null && Session.Config != null)
                name = Session.Config.CharacterName;
            if (string.IsNullOrEmpty(name)) name = "庄方宜";

            _nameLabel = new Label(name);
            _nameLabel.style.position = Position.Absolute;
            _nameLabel.style.left = 24;
            _nameLabel.style.top = 18;
            _nameLabel.style.fontSize = 22;
            _nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _nameLabel.style.color = TextMain;
            _nameLabel.style.backgroundColor = new Color(0.09f, 0.10f, 0.13f, 0.55f);
            _nameLabel.style.paddingLeft = 14; _nameLabel.style.paddingRight = 14;
            _nameLabel.style.paddingTop = 6; _nameLabel.style.paddingBottom = 6;
            _nameLabel.style.borderTopLeftRadius = 8; _nameLabel.style.borderTopRightRadius = 8;
            _nameLabel.style.borderBottomLeftRadius = 8; _nameLabel.style.borderBottomRightRadius = 8;
            _root.Add(_nameLabel);
        }

        void BuildSubtitle()
        {
            _subtitle = new Label("");
            _subtitle.style.position = Position.Absolute;
            _subtitle.style.left = Length.Percent(8);
            _subtitle.style.bottom = 96;
            _subtitle.style.maxWidth = Length.Percent(70);
            _subtitle.style.backgroundColor = BubbleBg;
            _subtitle.style.color = TextMain;
            _subtitle.style.fontSize = 20;
            _subtitle.style.whiteSpace = WhiteSpace.Normal;
            _subtitle.style.paddingLeft = 18; _subtitle.style.paddingRight = 18;
            _subtitle.style.paddingTop = 12; _subtitle.style.paddingBottom = 12;
            _subtitle.style.borderTopLeftRadius = 12; _subtitle.style.borderTopRightRadius = 12;
            _subtitle.style.borderBottomLeftRadius = 12; _subtitle.style.borderBottomRightRadius = 12;
            _subtitle.style.borderLeftWidth = 1; _subtitle.style.borderRightWidth = 1;
            _subtitle.style.borderTopWidth = 1; _subtitle.style.borderBottomWidth = 1;
            _subtitle.style.borderLeftColor = BorderCol; _subtitle.style.borderRightColor = BorderCol;
            _subtitle.style.borderTopColor = BorderCol; _subtitle.style.borderBottomColor = BorderCol;
            _subtitle.style.display = DisplayStyle.None;   // 初始隐藏，SetReply 时才显示
            _subtitle.pickingMode = PickingMode.Ignore;

            // 字幕改为放进一个从底部往上长的容器：上面一行灰色小字是"内心想法"（feeling）
            _subtitle.style.position = Position.Relative;
            _subtitle.style.left = StyleKeyword.Auto;
            _subtitle.style.bottom = StyleKeyword.Auto;
            _subtitle.style.maxWidth = Length.Percent(100);

            _speech = new VisualElement();
            _speech.style.position = Position.Absolute;
            _speech.style.left = Length.Percent(8);      // 与输入框对齐；开发者面板默认隐藏（F1 打开）
            _speech.style.bottom = 96;
            _speech.style.maxWidth = Length.Percent(62);
            _speech.style.flexDirection = FlexDirection.Column;
            _speech.style.alignItems = Align.FlexStart;
            _speech.pickingMode = PickingMode.Ignore;

            _feeling = new Label("");
            _feeling.style.fontSize = 15;
            _feeling.style.color = TextDim;
            _feeling.style.unityFontStyleAndWeight = FontStyle.Italic;
            _feeling.style.marginBottom = 6;
            _feeling.style.paddingLeft = 6;
            _feeling.style.whiteSpace = WhiteSpace.Normal;
            _feeling.style.display = DisplayStyle.None;
            _feeling.pickingMode = PickingMode.Ignore;

            _speech.Add(_feeling);
            _speech.Add(_subtitle);
            _root.Add(_speech);
        }

        /// <summary>右侧行为面板：每一轮显示 AI 要了什么神态/表情/动作、实际做了什么、库里缺什么</summary>
        void BuildBehaviorPanel()
        {
            _panel = new VisualElement();
            _panel.style.position = Position.Absolute;
            _panel.style.right = 24;
            _panel.style.top = 70;
            _panel.style.width = 330;
            _panel.style.backgroundColor = new Color(0.09f, 0.10f, 0.13f, 0.78f);
            _panel.style.paddingLeft = 14; _panel.style.paddingRight = 14;
            _panel.style.paddingTop = 10; _panel.style.paddingBottom = 12;
            _panel.style.borderTopLeftRadius = 10; _panel.style.borderTopRightRadius = 10;
            _panel.style.borderBottomLeftRadius = 10; _panel.style.borderBottomRightRadius = 10;
            _panel.style.borderLeftWidth = 1; _panel.style.borderRightWidth = 1;
            _panel.style.borderTopWidth = 1; _panel.style.borderBottomWidth = 1;
            _panel.style.borderLeftColor = BorderCol; _panel.style.borderRightColor = BorderCol;
            _panel.style.borderTopColor = BorderCol; _panel.style.borderBottomColor = BorderCol;
            _panel.pickingMode = PickingMode.Ignore;

            var title = new Label("行为面板  ·  F2 隐藏");
            _behaviorTitle = title;
            title.style.fontSize = 15;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = TextMain;
            title.style.marginBottom = 2;
            _panel.Add(title);

            var legend = new Label("<color=#8CD999>✔ 照做</color>   <color=#9EA6B8>~ 自动</color>   <color=#FF8C73>✘ 库里没有</color>");
            legend.style.fontSize = 12;
            legend.style.marginBottom = 6;
            _panel.Add(legend);

            _panelSource = new Label("等待第一句对话…");
            _panelSource.style.fontSize = 12;
            _panelSource.style.color = TextDim;
            _panelSource.style.marginBottom = 6;
            _panel.Add(_panelSource);

            _panelRows = new VisualElement();
            _panelRows.style.flexDirection = FlexDirection.Column;
            _panel.Add(_panelRows);

            // 不再单独挂到 root：由 BuildSettingsPanel 嵌进统一控制面板
            BuildSettingsPanel();
        }

        // ============================================================
        //  设置/记忆面板（F3）
        // ============================================================
        void BuildSettingsPanel()
        {
            _settingsPanel = new VisualElement();
            _settingsPanel.style.position = Position.Absolute;
            _settingsPanel.style.left = 16;
            _settingsPanel.style.top = 70;
            _settingsPanel.style.bottom = 90;      // 不压住底部输入框；内容超出时用 ScrollView 滚动
            _settingsPanel.style.width = 360;
            _settingsPanel.style.backgroundColor = new Color(0.09f, 0.10f, 0.13f, 0.94f);
            _settingsPanel.style.paddingLeft = 16; _settingsPanel.style.paddingRight = 16;
            _settingsPanel.style.paddingTop = 12; _settingsPanel.style.paddingBottom = 12;
            _settingsPanel.style.borderTopLeftRadius = 10; _settingsPanel.style.borderTopRightRadius = 10;
            _settingsPanel.style.borderBottomLeftRadius = 10; _settingsPanel.style.borderBottomRightRadius = 10;
            _settingsPanel.style.borderLeftWidth = 1; _settingsPanel.style.borderRightWidth = 1;
            _settingsPanel.style.borderTopWidth = 1; _settingsPanel.style.borderBottomWidth = 1;
            _settingsPanel.style.borderLeftColor = BorderCol; _settingsPanel.style.borderRightColor = BorderCol;
            _settingsPanel.style.borderTopColor = BorderCol; _settingsPanel.style.borderBottomColor = BorderCol;
            _settingsPanel.style.display = DisplayStyle.None;

            // 标题栏：标题 + 关闭按钮
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 4;

            var title = new Label("控制面板");
            title.style.fontSize = 15;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = TextMain;
            title.style.flexGrow = 1;
            header.Add(title);

            var closeBtn = new Button();
            closeBtn.text = "×";
            closeBtn.style.width = 28; closeBtn.style.height = 26;
            closeBtn.style.fontSize = 16;
            closeBtn.clicked += delegate { SetControlPanel(false); };
            header.Add(closeBtn);
            _settingsCloseBtn = closeBtn;
            _settingsPanel.Add(header);

            // 视觉链路开关放进面板里（面板打开时左上角的独立小面板会隐藏，避免两块 UI 重叠）
            // 视觉/走路开关已收进统一控制台左侧标签与右上快捷条，这里不再重复。
            BuildCharacterSkillEntry(_settingsPanel);

            // 回复长度（两行：标题+数值，第二行滑条，避免挤压）
            var lenHeader = new VisualElement();
            lenHeader.style.flexDirection = FlexDirection.Row;
            lenHeader.style.alignItems = Align.Center;
            lenHeader.style.marginTop = 8;
            var lenLabel = new Label("回复长度");
            lenLabel.style.fontSize = 13;
            lenLabel.style.color = TextMain;
            lenLabel.style.flexGrow = 1;
            lenHeader.Add(lenLabel);
            _lenValue = new Label("60 字");
            _lenValue.style.fontSize = 12;
            _lenValue.style.color = TextDim;
            lenHeader.Add(_lenValue);
            _settingsPanel.Add(lenHeader);

            _lenSlider = new Slider(30, 400);
            _lenSlider.value = 60;
            _lenSlider.showInputField = false;
            _lenSlider.style.marginTop = 2;
            _lenSlider.RegisterValueChangedCallback(delegate (ChangeEvent<float> ev)
            {
                if (_settingsRefreshing) return;
                _lenValue.text = Mathf.RoundToInt(ev.newValue) + " 字";
                ApplySettingsFromPanel();
            });
            _settingsPanel.Add(_lenSlider);

            // 记忆预算（16000 token 就是"每次发给模型的上下文 token 上限"）
            var memHeader = new VisualElement();
            memHeader.style.flexDirection = FlexDirection.Row;
            memHeader.style.alignItems = Align.Center;
            memHeader.style.marginTop = 8;
            var memLabel = new Label("记忆预算");
            memLabel.style.fontSize = 13;
            memLabel.style.color = TextMain;
            memLabel.style.flexGrow = 1;
            memHeader.Add(memLabel);
            _memValue = new Label("12000 token");
            _memValue.style.fontSize = 12;
            _memValue.style.color = TextDim;
            memHeader.Add(_memValue);
            _settingsPanel.Add(memHeader);

            _memSlider = new Slider(2000, 32000);
            _memSlider.value = 12000;
            _memSlider.showInputField = false;
            _memSlider.style.marginTop = 2;
            _memSlider.RegisterValueChangedCallback(delegate (ChangeEvent<float> ev)
            {
                if (_settingsRefreshing) return;
                _memValue.text = Mathf.RoundToInt(ev.newValue) + " token";
                ApplySettingsFromPanel();
            });
            _settingsPanel.Add(_memSlider);

            _memoryStats = new Label("记忆：0 条");
            _memoryStats.style.color = TextDim;
            _memoryStats.style.fontSize = 12;
            _memoryStats.style.marginTop = 4;
            _settingsPanel.Add(_memoryStats);

            // 流式模式
            var streamRow = new VisualElement();
            streamRow.style.flexDirection = FlexDirection.Row;
            streamRow.style.alignItems = Align.Center;
            streamRow.style.marginTop = 8;
            var streamLabel = new Label("流式模式");
            streamLabel.style.color = TextMain;
            streamLabel.style.fontSize = 13;
            streamLabel.style.width = 90;
            streamRow.Add(streamLabel);
            _streamDrop = new DropdownField();
            _streamDrop.choices = new List<string> { "关闭（整段）", "轻量（分句）", "完整（边生成边播）" };
            _streamDrop.index = 2;
            _streamDrop.style.flexGrow = 1;
            _streamDrop.RegisterValueChangedCallback(delegate (ChangeEvent<string> ev)
            {
                if (_settingsRefreshing) return;
                ApplySettingsFromPanel();
            });
            streamRow.Add(_streamDrop);
            _settingsPanel.Add(streamRow);

            // 语音打断
            var bargeRow = new VisualElement();
            bargeRow.style.flexDirection = FlexDirection.Row;
            bargeRow.style.alignItems = Align.Center;
            bargeRow.style.marginTop = 8;
            var bargeLabel = new Label("语音打断");
            bargeLabel.style.width = 78;
            bargeLabel.style.fontSize = 13;
            bargeLabel.style.color = TextMain;
            bargeRow.Add(bargeLabel);
            _bargeToggle = new Toggle();
            _bargeToggle.value = true;
            _bargeToggle.RegisterValueChangedCallback(delegate (ChangeEvent<bool> ev)
            {
                if (_settingsRefreshing) return;
                ApplySettingsFromPanel();
            });
            bargeRow.Add(_bargeToggle);
            var bargeHint = new Label("允许用户说话时打断 AI");
            bargeHint.style.fontSize = 11;
            bargeHint.style.color = TextDim;
            bargeHint.style.marginLeft = 8;
            bargeRow.Add(bargeHint);
            _settingsPanel.Add(bargeRow);

            // 思考模式（DeepSeek thinking）
            var thinkRow = new VisualElement();
            thinkRow.style.flexDirection = FlexDirection.Row;
            thinkRow.style.alignItems = Align.Center;
            thinkRow.style.marginTop = 8;
            var thinkLabel = new Label("思考模式");
            thinkLabel.style.width = 78;
            thinkLabel.style.fontSize = 13;
            thinkLabel.style.color = TextMain;
            thinkRow.Add(thinkLabel);
            _thinkToggle = new Toggle();
            _thinkToggle.value = false;
            _thinkToggle.RegisterValueChangedCallback(delegate (ChangeEvent<bool> ev)
            {
                if (_settingsRefreshing) return;
                if (_thinkDrop != null) _thinkDrop.SetEnabled(ev.newValue);
                ApplySettingsFromPanel();
            });
            thinkRow.Add(_thinkToggle);
            var thinkHint = new Label("开启后先思考再回答（更慢）");
            thinkHint.style.fontSize = 11;
            thinkHint.style.color = TextDim;
            thinkHint.style.marginLeft = 8;
            thinkRow.Add(thinkHint);
            _settingsPanel.Add(thinkRow);

            // 思考强度
            var effortRow = new VisualElement();
            effortRow.style.flexDirection = FlexDirection.Row;
            effortRow.style.alignItems = Align.Center;
            effortRow.style.marginTop = 6;
            var effortLabel = new Label("思考强度");
            effortLabel.style.width = 78;
            effortLabel.style.fontSize = 13;
            effortLabel.style.color = TextMain;
            effortRow.Add(effortLabel);
            _thinkDrop = new DropdownField();
            _thinkDrop.choices = new List<string> { "低（快）", "高（深）" };
            _thinkDrop.index = 0;
            _thinkDrop.style.flexGrow = 1;
            _thinkDrop.RegisterValueChangedCallback(delegate (ChangeEvent<string> ev)
            {
                if (_settingsRefreshing) return;
                ApplySettingsFromPanel();
            });
            effortRow.Add(_thinkDrop);
            _settingsPanel.Add(effortRow);

            // 操作按钮
            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.marginTop = 10;

            var clearBtn = new Button();
            clearBtn.text = "清空记忆";
            clearBtn.style.flexGrow = 1;
            clearBtn.style.height = 30;
            clearBtn.style.marginRight = 6;
            clearBtn.clicked += delegate
            {
                if (Session != null) Session.ClearHistory();
                RefreshSettingsPanel();
            };
            btnRow.Add(clearBtn);

            var resetBtn = new Button();
            resetBtn.text = "恢复默认";
            resetBtn.style.flexGrow = 1;
            resetBtn.style.height = 30;
            resetBtn.clicked += delegate
            {
                if (Session != null) Session.RestoreDefaultSettings();
                RefreshSettingsPanel();
            };
            btnRow.Add(resetBtn);
            _settingsPanel.Add(btnRow);

            _settingsSaved = new Label("修改会自动保存");
            _settingsSaved.style.color = TextDim;
            _settingsSaved.style.fontSize = 11;
            _settingsSaved.style.marginTop = 6;
            _settingsPanel.Add(_settingsSaved);

            // 小窗口下内容会超出：把标题以外的内容放进 ScrollView（输入框保持可见）
            ScrollView scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            while (_settingsPanel.childCount > 1)
            {
                VisualElement child = _settingsPanel[1];
                _settingsPanel.RemoveAt(1);
                scroll.contentContainer.Add(child);
            }

            // 行为状态：原右侧行为面板整体嵌进来（不再单独占屏幕）
            if (_panel != null)
            {
                _panel.style.position = Position.Relative;
                _panel.style.left = 0; _panel.style.top = 0; _panel.style.right = 0;
                _panel.style.width = new Length(100, LengthUnit.Percent);
                _panel.style.backgroundColor = new Color(0, 0, 0, 0);
                _panel.style.paddingLeft = 0; _panel.style.paddingRight = 0;
                _panel.style.paddingTop = 6; _panel.style.paddingBottom = 0;
                _panel.style.borderLeftWidth = 0; _panel.style.borderRightWidth = 0;
                _panel.style.borderTopWidth = 0; _panel.style.borderBottomWidth = 0;
                _panel.style.marginTop = 10;
                if (_behaviorTitle != null) _behaviorTitle.text = "行为状态";
                scroll.contentContainer.Add(_panel);
            }

            // 开发者面板入口（原 F1）
            var devRow = new VisualElement();
            devRow.style.flexDirection = FlexDirection.Row;
            devRow.style.alignItems = Align.Center;
            devRow.style.marginTop = 12;
            var devLabel = new Label("开发者");
            devLabel.style.color = TextDim;
            devLabel.style.fontSize = 12;
            devLabel.style.width = 90;
            devRow.Add(devLabel);
            var devBtn = new Button();
            devBtn.text = "打开 / 关闭开发者面板";
            devBtn.style.flexGrow = 1;
            devBtn.style.height = 30;
            devBtn.clicked += delegate { ToggleDevPanel(); };
            devRow.Add(devBtn);
            scroll.contentContainer.Add(devRow);

            _settingsPanel.Add(scroll);
            _root.Add(_settingsPanel);

            // 右上角：统一面板开关（点击弹出 / 再点关闭）
            _panelToggleBtn = new Button();
            _panelToggleBtn.text = "面板";
            _panelToggleBtn.style.position = Position.Absolute;
            _panelToggleBtn.style.right = 24;
            _panelToggleBtn.style.top = 24;
            _panelToggleBtn.style.width = 92;
            _panelToggleBtn.style.height = 34;
            _panelToggleBtn.style.fontSize = 14;
            _panelToggleBtn.clicked += delegate { ToggleControlPanel(); };
            _root.Add(_panelToggleBtn);
        }

        /// <summary>统一控制面板开关：点按钮弹出，再点关闭；面板内 × 也关闭。</summary>
        void ToggleControlPanel()
        {
            if (!_built || _settingsPanel == null) return;
            bool show = !_controlPanelOpen;
            SetControlPanel(show);
        }

        void SetControlPanel(bool show)
        {
            if (_settingsPanel == null) return;
            _controlPanelOpen = show;
            _settingsPanel.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            // 面板打开时先收起左上角操作提示，避免和面板标题重叠
            if (show && _hint != null) _hint.style.display = DisplayStyle.None;
            if (_panelToggleBtn != null) _panelToggleBtn.text = show ? "关闭面板" : "面板";

            // 面板打开时：隐藏左上角的独立视觉开关小块（已嵌进控制面板），
            // 但保留右上角摄像头预览，方便用户边调边看。
            EnsureVisionRefs();
            if (_visionUI != null) _visionUI.SetLinkPanelVisible(!show);
            if (_speech != null)
            {
                // HUD 打开时左侧被控制台占用（宽 478+16），字幕/对话区整体右移，避免被面板盖住。
                if (_hudMode) _speech.style.left = new Length(524f, LengthUnit.Pixel);
                else _speech.style.left = show ? new Length(396f, LengthUnit.Pixel) : Length.Percent(8);
            }

            if (show) { RefreshSettingsPanel(); RefreshVisionSection(); RefreshMovementSection(); }
        }

        void EnsureVisionRefs()
        {
            if (_vision == null) _vision = FindObjectOfType<Yixuan.Vision.VisionObserver>();
            if (_visionUI == null) _visionUI = FindObjectOfType<Yixuan.Vision.VisionControlUI>();
        }

        /// <summary>控制面板里的"视觉链路"区块：两个开关 + 当前状态。</summary>
        void BuildVisionSectionInto(VisualElement parent)
        {
            var section = new VisualElement();
            section.style.marginTop = 10;

            var titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.alignItems = Align.Center;
            var title = new Label("视觉链路");
            title.style.fontSize = 13;
            title.style.color = TextMain;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexGrow = 1;
            titleRow.Add(title);
            var cost = new Label("按需计费");
            cost.style.fontSize = 11;
            cost.style.color = TextDim;
            titleRow.Add(cost);
            section.Add(titleRow);

            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.marginTop = 4;
            _visGameBtn = new Button();
            _visGameBtn.text = "游戏视角：关";
            _visGameBtn.style.flexGrow = 1;
            _visGameBtn.style.height = 28;
            _visGameBtn.style.fontSize = 12;
            _visGameBtn.clicked += delegate
            {
                EnsureVisionRefs();
                if (_vision != null) { _vision.ToggleGameView(); RefreshVisionSection(); }
            };
            btnRow.Add(_visGameBtn);

            _visCamBtn = new Button();
            _visCamBtn.text = "电脑摄像头：关";
            _visCamBtn.style.flexGrow = 1;
            _visCamBtn.style.height = 28;
            _visCamBtn.style.fontSize = 12;
            _visCamBtn.style.marginLeft = 6;
            _visCamBtn.clicked += delegate
            {
                EnsureVisionRefs();
                if (_vision != null) { _vision.ToggleWebcam(); RefreshVisionSection(); }
            };
            btnRow.Add(_visCamBtn);
            section.Add(btnRow);

            _visStatus = new Label("等待视觉连接…");
            _visStatus.style.fontSize = 11;
            _visStatus.style.color = TextDim;
            _visStatus.style.marginTop = 3;
            _visStatus.style.whiteSpace = WhiteSpace.Normal;
            section.Add(_visStatus);

            parent.Add(section);
        }

        /// <summary>刷新视觉区块（面板打开时每 0.25s 一次）。</summary>
        void RefreshVisionSection()
        {
            EnsureVisionRefs();
            if (_visGameBtn == null || _visCamBtn == null || _visStatus == null) return;
            if (_vision == null)
            {
                _visStatus.text = "场景里没有 VisionObserver";
                return;
            }
            _visGameBtn.text = "游戏视角：" + (_vision.GameViewEnabled ? "开" : "关");
            _visCamBtn.text = "电脑摄像头：" + (_vision.WebcamEnabled ? "开" : "关");

            string msg;
            if (_vision.Client == null || !_vision.Client.HasKey) msg = "未找到 Qwen key";
            else if (!_vision.AnySourceEnabled) msg = "两路都关 · 不消耗额度";
            else if (_vision.Asking) msg = "正在看…";
            else if (_vision.RequestInFlight) msg = "正在观察…";
            else if (!string.IsNullOrEmpty(_vision.LastRealtimeAnswer))
                msg = "刚回答：" + Trim30(_vision.LastRealtimeAnswer);
            else if (!string.IsNullOrEmpty(_vision.LastObservation))
                msg = "已观察 " + _vision.TimelineCount + " 条（画面在持续记录）";
            else msg = "等待第一次观察…";
            _visStatus.text = msg;
        }

        static string Trim30(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ");
            return s.Length <= 30 ? s : s.Substring(0, 30) + "…";
        }

        // ============================================================
        //  移动（走路开关）
        // ============================================================

        void EnsureLocomotion()
        {
            if (_locomotion == null) _locomotion = FindObjectOfType<Yixuan.Motion.CharacterLocomotion>();
        }

        void BuildMovementSectionInto(VisualElement parent)
        {
            EnsureLocomotion();
            var section = new VisualElement();
            section.style.marginTop = 10;

            var titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.alignItems = Align.Center;
            var title = new Label("移动");
            title.style.fontSize = 13;
            title.style.color = TextMain;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexGrow = 1;
            titleRow.Add(title);
            var note = new Label("走路时不做其他动作");
            note.style.fontSize = 11;
            note.style.color = TextDim;
            titleRow.Add(note);
            section.Add(titleRow);

            _moveBtn = new Button();
            _moveBtn.text = "允许走路：开";
            _moveBtn.style.height = 28;
            _moveBtn.style.fontSize = 12;
            _moveBtn.style.marginTop = 4;
            _moveBtn.clicked += delegate
            {
                EnsureLocomotion();
                if (_locomotion != null) _locomotion.ToggleWalking();
                RefreshMovementSection();
            };
            section.Add(_moveBtn);

            _moveStatus = new Label("等待移动组件…");
            _moveStatus.style.fontSize = 11;
            _moveStatus.style.color = TextDim;
            _moveStatus.style.marginTop = 3;
            _moveStatus.style.whiteSpace = WhiteSpace.Normal;
            section.Add(_moveStatus);
            parent.Add(section);
        }

        void RefreshMovementSection()
        {
            EnsureLocomotion();
            if (_moveBtn == null || _moveStatus == null) return;
            if (_locomotion == null)
            {
                _moveBtn.text = "允许走路：?";
                _moveStatus.text = "场景里没有 CharacterLocomotion";
                return;
            }
            _moveBtn.text = "允许走路：" + (_locomotion.WalkingEnabled ? "开" : "关");
            if (!_locomotion.WalkingEnabled) _moveStatus.text = "走路已关闭 · 其他动作照常";
            else if (_locomotion.IsMoving)
                _moveStatus.text = "移动中" +
                    (string.IsNullOrEmpty(_locomotion.TargetAnchor) ? "" : " → " + _locomotion.TargetAnchor);
            else _moveStatus.text = "待机 · 可以走路";
        }

        void ApplySettingsFromPanel()
        {
            if (Session == null || Session.Config == null) return;
            RuntimeSettings s = Session.CurrentRuntimeSettings();
            s.maxReplyChars = Mathf.RoundToInt(_lenSlider.value);
            s.historyTokenBudget = Mathf.RoundToInt(_memSlider.value);
            s.streamMode = _streamDrop.index == 0 ? "off" : (_streamDrop.index == 1 ? "light" : "full");
            s.bargeInEnabled = _bargeToggle.value;
            s.thinkingEnabled = _thinkToggle.value;
            s.thinkingEffort = _thinkDrop.index == 1 ? "high" : "low";
            Session.ApplyRuntimeSettings(s, true);

            _lenValue.text = s.maxReplyChars + " 字";
            _memValue.text = s.historyTokenBudget + " token";
            if (_settingsSaved != null) _settingsSaved.text = "已保存 · " + System.DateTime.Now.ToString("HH:mm:ss");
            RefreshMemoryStats();
        }

        /// <summary>从当前 Config 刷新面板；ChatSession.Start/设置变更/清空记忆后调用。</summary>
        public void RefreshSettingsPanel()
        {
            if (!_built || _settingsPanel == null || Session == null || Session.Config == null) return;
            _settingsRefreshing = true;
            try
            {
                AIConfig c = Session.Config;
                _lenSlider.value = c.MaxReplyChars;
                _lenValue.text = c.MaxReplyChars + " 字";
                _memSlider.value = c.HistoryTokenBudget;
                _memValue.text = c.HistoryTokenBudget + " token";
                _streamDrop.index = c.StreamMode == "off" ? 0 : (c.StreamMode == "light" ? 1 : 2);
                _bargeToggle.value = c.BargeInEnabled;
                _thinkToggle.value = c.ThinkingEnabled;
                _thinkDrop.index = c.ThinkingEffort == "high" ? 1 : 0;
                _thinkDrop.SetEnabled(c.ThinkingEnabled);
                RefreshMemoryStats();
            }
            finally
            {
                _settingsRefreshing = false;
            }
        }

        void RefreshMemoryStats()
        {
            if (_memoryStats == null || Session == null) return;
            _memoryStats.text = "记忆：" + Session.HistoryCount + " 条 / 约 " +
                                Session.HistoryTokenEstimate + " token（预算 " +
                                (Session.Config != null ? Session.Config.HistoryTokenBudget : 0) + "）";
        }

        void BuildStatusLabel()
        {
            _status = new Label("在线");
            _status.style.position = Position.Absolute;
            _status.style.right = 24;
            _status.style.bottom = 96;
            _status.style.fontSize = 14;
            _status.style.color = TextDim;
            _status.style.backgroundColor = new Color(0.09f, 0.10f, 0.13f, 0.55f);
            _status.style.paddingLeft = 10; _status.style.paddingRight = 10;
            _status.style.paddingTop = 4; _status.style.paddingBottom = 4;
            _status.style.borderTopLeftRadius = 10; _status.style.borderTopRightRadius = 10;
            _status.style.borderBottomLeftRadius = 10; _status.style.borderBottomRightRadius = 10;
            _status.pickingMode = PickingMode.Ignore;
            _root.Add(_status);
        }

        void BuildInputRow()
        {
            var row = new VisualElement();
            row.style.position = Position.Absolute;
            row.style.left = Length.Percent(8);
            row.style.right = Length.Percent(8);
            row.style.bottom = 24;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;

            _input = new TextField();
            _input.multiline = false;
            _input.style.flexGrow = 1;
            _input.style.height = 46;
            _input.style.fontSize = 18;
            _input.style.backgroundColor = InputBg;
            _input.style.color = TextMain;
            _input.style.paddingLeft = 12; _input.style.paddingRight = 12;
            _input.style.borderTopLeftRadius = 10; _input.style.borderTopRightRadius = 10;
            _input.style.borderBottomLeftRadius = 10; _input.style.borderBottomRightRadius = 10;
            _input.style.borderLeftWidth = 1; _input.style.borderRightWidth = 1;
            _input.style.borderTopWidth = 1; _input.style.borderBottomWidth = 1;
            _input.style.borderLeftColor = BorderCol; _input.style.borderRightColor = BorderCol;
            _input.style.borderTopColor = BorderCol; _input.style.borderBottomColor = BorderCol;
            _input.RegisterCallback<KeyDownEvent>(OnInputKeyDown);
            row.Add(_input);

            // ★ 修复（2026-10-18 用户实测）：默认 runtime theme 给内层 unity-text-input 白色底，
            //   而输入文字颜色继承外层浅色 → 白字白底，用户完全看不到自己输入的内容。
            //   这里显式把可见文字压成深色（白底黑字），并固定内层背景，任何主题下都可见。
            StyleInputText();

            // 占位提示：输入框为空且未聚焦时显示，避免用户不知道在哪打字
            _placeholder = new Label("在这里输入文字…（回车发送）");
            _placeholder.style.position = Position.Absolute;
            _placeholder.style.left = 26;
            _placeholder.style.right = 118;
            _placeholder.style.top = 0;
            _placeholder.style.bottom = 0;
            _placeholder.style.unityTextAlign = TextAnchor.MiddleLeft;
            _placeholder.style.color = new Color(0.42f, 0.45f, 0.52f, 1f);
            _placeholder.style.fontSize = 15;
            _placeholder.pickingMode = PickingMode.Ignore;
            row.Add(_placeholder);

            _input.RegisterCallback<FocusInEvent>(OnInputFocusIn);
            _input.RegisterCallback<FocusOutEvent>(OnInputFocusOut);
            _input.RegisterValueChangedCallback(OnInputValueChanged);

            _sendBtn = new Button();
            _sendBtn.text = "发送";
            _sendBtn.style.height = 46;
            _sendBtn.style.minWidth = 92;
            _sendBtn.style.marginLeft = 8;
            _sendBtn.style.fontSize = 18;
            _sendBtn.style.color = Color.white;
            _sendBtn.style.backgroundColor = Accent;
            _sendBtn.style.borderTopLeftRadius = 10; _sendBtn.style.borderTopRightRadius = 10;
            _sendBtn.style.borderBottomLeftRadius = 10; _sendBtn.style.borderBottomRightRadius = 10;
            _sendBtn.style.borderLeftWidth = 0; _sendBtn.style.borderRightWidth = 0;
            _sendBtn.style.borderTopWidth = 0; _sendBtn.style.borderBottomWidth = 0;
            _sendBtn.clicked += OnSendClicked;
            row.Add(_sendBtn);

            _root.Add(row);
            UpdatePlaceholder();   // 初始状态：显示占位提示
        }

        // ============================================================
        //  输入事件
        // ============================================================
        void OnInputKeyDown(KeyDownEvent evt)
        {
            // 回车 / 小键盘回车：发送（先拦下，避免 TextField 把它当换行处理）
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
            evt.StopPropagation();
            SendCurrentInput();
        }

        void OnSendClicked()
        {
            SendCurrentInput();
        }

        void SendCurrentInput()
        {
            if (!_inputEnabled) return;
            if (_input == null) return;

            string text = _input.value;
            if (string.IsNullOrEmpty(text)) return;
            text = text.Trim();
            if (text.Length == 0) return;

            if (Session == null) ResolveRefs();
            if (Session == null)
            {
                if (!_warnedNoSession)
                {
                    _warnedNoSession = true;
                    Debug.LogWarning("[ChatUI] 场景里找不到 ChatSession，输入无法发送。");
                }
                return;
            }

            _input.value = "";          // 发送后清空
            UpdatePlaceholder();
            _input.Focus();             // 保持焦点，方便连续输入
            // 忙碌时（思考/说话）也允许发送：ChatSession 会先打断当前轮再提交，
            // 不再把用户刚打的字静默丢掉。
            Session.SubmitFromUI(text);
        }

        // ============================================================
        //  输入框可见性 / 焦点 / 回车兜底
        // ============================================================

        /// <summary>
        /// 强制把输入框里的“可见文字”设成深色。
        /// TextField 的可见文字在内部 TextElement 上；默认 runtime theme 是白底，
        /// 而外层 style.color 是浅色 → 白字白底看不见（用户 2026-10-18 实测）。
        /// </summary>
        void StyleInputText()
        {
            if (_input == null) return;

            // 内层容器（unity-text-input）固定浅色背景，保证深色文字有对比度
            if (_input.childCount > 0)
            {
                var inner = _input.ElementAt(0);
                if (inner != null)
                {
                    inner.style.backgroundColor = new Color(0.95f, 0.96f, 0.98f, 1f);
                    inner.style.borderTopLeftRadius = 10; inner.style.borderTopRightRadius = 10;
                    inner.style.borderBottomLeftRadius = 10; inner.style.borderBottomRightRadius = 10;
                }
            }

            // 所有文本元素显式深色（选择高亮/光标颜色跟随文字色）
            foreach (var te in _input.Query<TextElement>().Build())
            {
                te.style.color = new Color(0.10f, 0.11f, 0.15f, 1f);
                te.style.fontSize = 18;
            }
        }

        void OnInputFocusIn(FocusInEvent evt) { _inputFocused = true; UpdatePlaceholder(); }
        void OnInputFocusOut(FocusOutEvent evt) { _inputFocused = false; UpdatePlaceholder(); }
        void OnInputValueChanged(ChangeEvent<string> evt) { UpdatePlaceholder(); }

        /// <summary>输入框为空且未聚焦时显示占位提示</summary>
        void UpdatePlaceholder()
        {
            if (_placeholder == null) return;
            bool show = !_inputFocused && (_input == null || string.IsNullOrEmpty(_input.value));
            _placeholder.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// 回车兜底：某些输入法/主题下 TextField 内部会先消费 KeyDownEvent，
        /// 这里再用 legacy Input 检测一次（只在输入框有焦点且启用时触发，不会误触）。
        /// </summary>
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1)) ToggleDevPanel();
            if (_built && _settingsPanel != null && (Input.GetKeyDown(KeyCode.F2) || Input.GetKeyDown(KeyCode.F3)))
                ToggleControlPanel();

            if (_built) AnimateHud();

            // 控制面板打开时，低频率刷新"视觉链路"状态（不加重每帧开销）
            if (_built && _controlPanelOpen && Time.unscaledTime >= _visNextRefresh)
            {
                _visNextRefresh = Time.unscaledTime + 0.25f;
                RefreshVisionSection();
                RefreshMovementSection();
            }

            if (!_built || !_inputFocused || !_inputEnabled || _input == null) return;
            if (!Input.GetKeyDown(KeyCode.Return) && !Input.GetKeyDown(KeyCode.KeypadEnter)) return;
            SendCurrentInput();
        }

        /// <summary>轻量 HUD 动画：字幕淡入 + "思考中…"动态省略号 + 操作提示自动淡出</summary>
        void AnimateHud()
        {
            // 字幕淡入（0.2s 左右，避免生硬弹出）
            if (_speech != null && _speechAlpha < 1f)
            {
                _speechAlpha = Mathf.MoveTowards(_speechAlpha, 1f, Time.deltaTime * 5f);
                _speech.style.opacity = _speechAlpha;
            }

            // 思考中：省略号动画（每 0.35s 加一个点）
            if (_status != null && !string.IsNullOrEmpty(_statusBase))
            {
                _statusDotTimer += Time.deltaTime;
                int dots = 1 + ((int)(_statusDotTimer / 0.35f)) % 3;
                _status.text = _statusBase + "·" + new string('·', dots);
            }

            // 操作提示：10 秒后淡出
            if (_hint != null && _hint.style.display != DisplayStyle.None)
            {
                _hintTimer += Time.deltaTime;
                if (_hintTimer > 10f)
                {
                    float a = Mathf.Max(0f, 1f - (_hintTimer - 10f) / 1.5f);
                    _hint.style.color = new Color(0.62f, 0.65f, 0.72f, 0.85f * a);
                    if (a <= 0.01f) _hint.style.display = DisplayStyle.None;
                }
            }
        }

        // ============================================================
        //  对外接口（ChatSession 调用）
        // ============================================================

        /// <summary>更新右下角状态文字（在线 / 思考中… / 说话中…）</summary>
        // ---------------- 统一 HUD 接口 ----------------
        public bool ControlPanelOpen { get { return _controlPanelOpen; } }
        public void SetControlPanelPublic(bool show) { SetControlPanel(show); }
        public void SetHudMode(bool on)
        {
            _hudMode = on;
            if (_panelToggleBtn != null)
                _panelToggleBtn.style.display = on ? DisplayStyle.None : DisplayStyle.Flex;
            if (_settingsCloseBtn != null)
                _settingsCloseBtn.style.display = on ? DisplayStyle.None : DisplayStyle.Flex;
            if (_hint != null && on)
                _hint.style.display = DisplayStyle.None;
        }
        public void ApplyHudRect(float left, float top, float width, float height)
        {
            if (_settingsPanel == null) return;
            _settingsPanel.style.left = left;
            _settingsPanel.style.top = top;
            _settingsPanel.style.width = width;
            _settingsPanel.style.height = height;
            _settingsPanel.style.bottom = StyleKeyword.Auto;
        }

        public void SetStatus(string text)
        {
            if (!_built || _status == null) return;
            if (string.IsNullOrEmpty(text))
            {
                _status.text = "";
                _statusBase = "";
                _statusDotTimer = 0f;
                return;
            }
            if (text.StartsWith("思考中"))
            {
                // 交给 AnimateHud 做动态省略号
                _statusBase = "思考中";
                _statusDotTimer = 0f;
                _status.text = "思考中";
            }
            else
            {
                _statusBase = "";
                _status.text = text;
            }
        }

        /// <summary>更新字幕气泡；传空串 = 隐藏气泡</summary>
        public void SetReply(string text)
        {
            if (!_built || _subtitle == null) return;
            if (string.IsNullOrEmpty(text))
            {
                _subtitle.text = "";
                _subtitle.style.display = DisplayStyle.None;
                return;
            }
            _subtitle.text = text;
            _subtitle.style.display = DisplayStyle.Flex;
            _speechAlpha = 0f;                               // 淡入
            if (_speech != null) _speech.style.opacity = 0f;
        }

        /// <summary>P6 流式字幕：同一轮里不断更新文字；只有首块重新淡入。</summary>
        public void SetReplyStreaming(string text, bool isFirstChunk)
        {
            if (!_built || _subtitle == null) return;
            if (string.IsNullOrEmpty(text))
            {
                _subtitle.text = "";
                _subtitle.style.display = DisplayStyle.None;
                return;
            }
            _subtitle.text = text;
            _subtitle.style.display = DisplayStyle.Flex;
            if (isFirstChunk)
            {
                _speechAlpha = 0f;
                if (_speech != null) _speech.style.opacity = 0f;
            }
            else
            {
                _speechAlpha = 1f;
                if (_speech != null) _speech.style.opacity = 1f;
            }
        }

        /// <summary>字幕上方的灰色小字：AI 的内心想法（契约 v2 的 feeling）；空 = 隐藏</summary>
        public void SetFeeling(string text)
        {
            if (!_built || _feeling == null) return;
            bool show = !string.IsNullOrEmpty(text);
            _feeling.text = show ? "（" + text + "）" : "";
            _feeling.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>F1：显示/隐藏左侧开发者控制面板（UI_ControlPanel），演示时默认隐藏</summary>
        void ToggleDevPanel()
        {
            var doc = FindDevPanelDoc();
            if (doc == null || doc.rootVisualElement == null) return;
            var r = doc.rootVisualElement;
            bool show = r.resolvedStyle.display == DisplayStyle.None;
            r.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>显式设置开发者面板显隐（不读 UI Toolkit 内部状态，便于启动时调用）</summary>
        void SetDevPanelVisible(bool visible)
        {
            var doc = FindDevPanelDoc();
            if (doc == null || doc.rootVisualElement == null) return;
            doc.rootVisualElement.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        static UIDocument FindDevPanelDoc()
        {
            var go = GameObject.Find("UI_ControlPanel");
            return go != null ? go.GetComponent<UIDocument>() : null;
        }

        /// <summary>刷新右侧行为面板</summary>
        public void SetBehavior(BehaviorReport report)
        {
            if (!_built || _panelRows == null || report == null) return;
            _panelRows.Clear();
            _panelSource.text = "来源：" + report.Source +
                                (report.HasMissing ? "   <color=#FF8C73>缺 " + report.MissingItems.Count + " 项（已记录）</color>" : "");

            foreach (var row in report.Rows)
            {
                var line = new VisualElement();
                line.style.flexDirection = FlexDirection.Row;
                line.style.marginBottom = 3;

                string mark = row.State == BehaviorReport.Status.Ok ? "✔" : row.State == BehaviorReport.Status.Auto ? "~" : "✘";
                Color col = row.State == BehaviorReport.Status.Ok ? OkCol : row.State == BehaviorReport.Status.Auto ? AutoCol : MissCol;

                var key = new Label(mark + " " + row.Label);
                key.style.width = 64;
                key.style.flexShrink = 0;
                key.style.fontSize = 14;
                key.style.color = col;
                line.Add(key);

                var val = new Label(row.Value);
                val.style.fontSize = 14;
                val.style.color = row.State == BehaviorReport.Status.Missing ? MissCol : TextMain;
                val.style.whiteSpace = WhiteSpace.Normal;
                val.style.flexShrink = 1;
                line.Add(val);

                _panelRows.Add(line);
            }
        }

        /// <summary>启用/禁用输入（对话进行中禁用来防抖）</summary>
        public void SetInputEnabled(bool enabled)
        {
            _inputEnabled = enabled;
            if (!_built) return;
            if (_input != null) _input.SetEnabled(enabled);
            if (_sendBtn != null) _sendBtn.SetEnabled(enabled);
        }

        // ============================================================
        //  清理
        // ============================================================
        void OnDestroy()
        {
            // 事件解绑（MonoBehaviour 销毁时 UIDocument 的子元素也会销毁，
            // 但显式解绑更稳，避免重载场景后回调打到已销毁对象上）
            if (_input != null)
            {
                _input.UnregisterCallback<KeyDownEvent>(OnInputKeyDown);
                _input.UnregisterCallback<FocusInEvent>(OnInputFocusIn);
                _input.UnregisterCallback<FocusOutEvent>(OnInputFocusOut);
                _input.UnregisterValueChangedCallback(OnInputValueChanged);
            }
            if (_sendBtn != null)
                _sendBtn.clicked -= OnSendClicked;

            _root = null;
            _built = false;
        }
    }
}
