using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;

namespace Yixuan.UI
{
    /// <summary>
    /// 仪玄数字人控制面板（UI Toolkit，纯 C# 构建，不依赖 UXML/USS）。
    ///
    /// 【为什么用 C# 构建而不是 UXML】
    /// 表情库和动作库是数据驱动的（ExpressionLibrary / GestureLibrary），
    /// 用代码构建可以按库内容自动生成按钮，加新表情/动作不用改 UI 文件。
    ///
    /// 【样式为什么内联】
    /// 项目里没有 ThemeStyleSheet 资产（FindAssets 返回 0），
    /// 所以不依赖 USS 文件，全部用 element.style.xxx 内联设置，更可靠。
    ///
    /// 挂载方式：场景根物体 UI_ControlPanel 上挂本脚本 + UIDocument，
    /// UIDocument.panelSettings 指向 Assets/UI/YixuanPanelSettings.asset
    /// </summary>
    [DisallowMultipleComponent]
    public class YixuanControlPanel : MonoBehaviour
    {
        [Header("引用（留空自动查找）")]
        public Yixuan.Core.YixuanCharacter Character;
        public Yixuan.Rendering.YixuanLightingSetup Lighting;

        [Header("面板")]
        public bool StartVisible = true;
        [Tooltip("面板宽度")]
        public float PanelWidth = 480f;
        [Tooltip("面板最大高度（超出会滚动）")]
        public float PanelMaxHeight = 900f;

        UIDocument _doc;
        VisualElement _root;
        VisualElement _body;
        bool _collapsed;

        VisualElement _scrollRoot;
        Label _statusLabel;
        Label _exprLabel;
        Label _gestureLabel;

        // 微调区
        VisualElement _tuningHost;
        readonly Dictionary<string, float> _tuneSliders = new Dictionary<string, float>();
        Yixuan.Core.PresetStore _preset;

        // 配色
        static readonly Color BgDark = new Color(0.09f, 0.10f, 0.13f, 0.92f);
        static readonly Color BgSection = new Color(0.14f, 0.15f, 0.19f, 1f);
        static readonly Color Accent = new Color(0.42f, 0.62f, 0.95f, 1f);
        static readonly Color TextMain = new Color(0.92f, 0.93f, 0.96f, 1f);
        static readonly Color TextDim = new Color(0.62f, 0.65f, 0.72f, 1f);

        void Awake()
        {
            _doc = GetComponent<UIDocument>();
            if (_doc == null) _doc = gameObject.AddComponent<UIDocument>();

            if (_doc.panelSettings == null)
            {
                Debug.LogError("[YixuanControlPanel] UIDocument.panelSettings 为空，面板不会显示。" +
                               "请把 Assets/UI/YixuanPanelSettings.asset 拖到 UIDocument 上。");
                return;
            }

            // ★ 必须在 Build() 之前解析引用。
            //   动作区的按钮是从角色的 MixamoActionManifest 动态生成的，
            //   若引用还没解析（原来放在 Start 里），Build 时 Character 还是 null
            //   → 找不到语义表 → 面板上只剩一句"尚未装配"。
            ResolveRefs();

            Build();
        }

        void Start()
        {
            ResolveRefs();
            EnsurePresetStore();
            ApplySavedSettings();
            RefreshLabels();
        }

        void EnsurePresetStore()
        {
            if (_preset == null) _preset = FindObjectOfType<Yixuan.Core.PresetStore>();
            if (_preset == null)
            {
                var holder = GameObject.Find("PresetStore");
                if (holder == null) holder = new GameObject("PresetStore");
                _preset = holder.GetComponent<Yixuan.Core.PresetStore>();
                if (_preset == null) _preset = holder.AddComponent<Yixuan.Core.PresetStore>();
            }
        }

        /// <summary>把磁盘里的设置应用到角色/照明</summary>
        void ApplySavedSettings()
        {
            if (_preset == null || _preset.Data == null) return;
            var s = _preset.Data.settings;
            if (s == null) return;

            if (Character != null)
            {
                Character.ExpressionInterval = s.expressionInterval;
                Character.GestureInterval = s.gestureInterval;
                Character.SetAutoPlay(s.autoPlay);
                if (Character.Blink != null) Character.Blink.BlinkEnabled = s.blinkEnabled;
            }
            if (Lighting != null)
            {
                Lighting.SetExposure(s.exposure);
                Lighting.SetBackground(s.backgroundIndex);
            }
            SetStatus("已应用保存的设置");
        }

        /// <summary>把当前设置写回磁盘（退出时调用）</summary>
        void SaveSettingsNow()
        {
            if (_preset == null || Character == null) return;
            var s = _preset.Data.settings;
            s.exposure = Lighting != null ? Lighting.Exposure : s.exposure;
            s.backgroundIndex = Lighting != null ? Lighting.BackgroundIndex : s.backgroundIndex;
            s.expressionInterval = Character.ExpressionInterval;
            s.gestureInterval = Character.GestureInterval;
            s.autoPlay = Character.AutoPlay;
            s.blinkEnabled = (Character.Blink != null) ? Character.Blink.BlinkEnabled : s.blinkEnabled;
            _preset.SaveSettings(s);
        }

        void OnApplicationQuit() { SaveSettingsNow(); }

        void ResolveRefs()
        {
            if (Character == null) Character = FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            if (Lighting == null) Lighting = FindObjectOfType<Yixuan.Rendering.YixuanLightingSetup>();
        }

        // ============================================================
        //  构建 UI
        // ============================================================
        void Build()
        {
            _root = _doc.rootVisualElement;
            _root.Clear();
            _root.style.position = Position.Absolute;
            _root.style.right = 12;
            _root.style.top = 12;
            _root.style.width = PanelWidth;

            var panel = MakeBox(BgDark);
            panel.style.borderTopLeftRadius = 8; panel.style.borderTopRightRadius = 8;
            panel.style.borderBottomLeftRadius = 8; panel.style.borderBottomRightRadius = 8;
            panel.style.borderLeftWidth = 1; panel.style.borderRightWidth = 1;
            panel.style.borderTopWidth = 1; panel.style.borderBottomWidth = 1;
            var borderCol = new Color(0.30f, 0.34f, 0.42f, 1f);
            panel.style.borderLeftColor = borderCol; panel.style.borderRightColor = borderCol;
            panel.style.borderTopColor = borderCol; panel.style.borderBottomColor = borderCol;
            _root.Add(panel);

            // ---- 标题栏（可点击折叠）----
            var header = MakeBox(new Color(0.16f, 0.18f, 0.23f, 1f));
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.paddingLeft = 10; header.style.paddingRight = 10;
            header.style.paddingTop = 8; header.style.paddingBottom = 8;
            header.style.borderTopLeftRadius = 8; header.style.borderTopRightRadius = 8;

            var title = new Label("仪玄 控制面板");
            title.style.color = TextMain;
            title.style.fontSize = 15;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexGrow = 1;
            header.Add(title);

            var toggleBtn = MakeButton(_collapsed ? "▼" : "▲", () => {
                _collapsed = !_collapsed;
                _body.style.display = _collapsed ? DisplayStyle.None : DisplayStyle.Flex;
            });
            toggleBtn.style.width = 28; toggleBtn.style.height = 22;
            header.Add(toggleBtn);
            panel.Add(header);

            // ---- 主体（放进 ScrollView，内容多时可滚动）----
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.maxHeight = PanelMaxHeight;
            _scrollRoot = scroll;
            panel.Add(scroll);

            _body = scroll.contentContainer;
            _body.style.paddingLeft = 10; _body.style.paddingRight = 10;
            _body.style.paddingTop = 8; _body.style.paddingBottom = 10;

            BuildExpressionSection();
            BuildGestureSection();
            BuildPoseSection();
            BuildPlaybackSection();
            BuildTuningSection();
            BuildLightingSection();

            // ---- 状态栏 ----
            _statusLabel = new Label("就绪");
            _statusLabel.style.color = TextDim;
            _statusLabel.style.fontSize = 11;
            _statusLabel.style.marginTop = 8;
            _statusLabel.style.whiteSpace = WhiteSpace.Normal;
            _body.Add(_statusLabel);
        }

        // ---------------- 统一 HUD 接口 ----------------
        public void SetPanelVisible(bool on)
        {
            if (_root == null) return;
            _root.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public void ApplyHudRect(float left, float top, float width, float height)
        {
            if (_root == null) return;
            _root.style.right = StyleKeyword.Auto;
            _root.style.left = left;
            _root.style.top = top;
            _root.style.width = width;
            if (_scrollRoot != null) _scrollRoot.style.maxHeight = Mathf.Max(140f, height - 56f);
        }

        void BuildExpressionSection()
        {
            Section("表情");
            _exprLabel = new Label("当前：-");
            _exprLabel.style.color = Accent;
            _exprLabel.style.fontSize = 11;
            _exprLabel.style.marginBottom = 4;
            _body.Add(_exprLabel);

            var names = new string[] { "中性", "微笑", "大笑", "坏笑", "得意", "生气", "大怒", "悲伤", "困扰", "惊讶", "震惊", "认真", "害羞", "眨眼", "右眨眼" };
            var ids = new string[] { "neutral", "smile", "laugh", "grin", "smug", "angry", "rage", "sad", "troubled", "surprised", "shock", "serious", "shy", "wink", "winkR" };
            _body.Add(MakeGrid(names, ids, delegate(string id) {
                if (Character == null) { SetStatus("❌ 未找到 YixuanCharacter"); return; }
                Character.SetExpression(id);
                SetStatus("表情 → " + id);
                RefreshLabels();
            }));
        }

        void BuildGestureSection()
        {
            Section("动作（Mixamo）");
            _gestureLabel = new Label("上次：-");
            _gestureLabel.style.color = Accent;
            _gestureLabel.style.fontSize = 11;
            _gestureLabel.style.marginBottom = 4;
            _body.Add(_gestureLabel);

            // ★ 按钮从 MixamoActionManifest 动态生成 —— 面板上有什么，就真的能播什么。
            //
            //   踩坑：原来这里写死了一张"中文名 + 语义 id"的表
            //   （挥手/wave、叉腰/handsOnHips、抱胸/crossArms…），
            //   但那些 id 对应的 clip 根本不存在 → PlayGesture 全部落空 →
            //   退回程序化姿态（手臂是歪的）→ 点了按钮肢体还是错的。
            //   现在直接从语义表生成，不会再对不上。
            var manifest = Character != null ? Character.Manifest : null;
            if (manifest == null) manifest = FindObjectOfType<Yixuan.Motion.MixamoActionManifest>();

            if (manifest == null || manifest.Actions == null || manifest.Actions.Length == 0)
            {
                var warn = new Label("尚未装配 Mixamo 动作。\n请先选中角色 → 菜单 UnityLITF/仪玄/② 装配 Mixamo 动作到播放器");
                warn.style.color = new Color(0.95f, 0.72f, 0.35f, 1f);
                warn.style.fontSize = 11;
                warn.style.whiteSpace = WhiteSpace.Normal;
                _body.Add(warn);
                return;
            }

            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;

            foreach (var e in manifest.Actions)
            {
                if (e == null || string.IsNullOrEmpty(e.Id)) continue;
                string id = e.Id;
                string clip = e.ClipName;
                var btn = MakeButton(LabelOf(id, clip), delegate {
                    if (Character == null) { SetStatus("❌ 未找到 YixuanCharacter"); return; }
                    Character.PlayGesture(id);
                    SetStatus("动作 → " + clip);
                    RefreshLabels();
                });
                btn.tooltip = id + "  →  " + clip;
                grid.Add(btn);
            }
            _body.Add(grid);

            // 一行控制：停止外部动画，回到程序化待机
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 4;
            row.Add(MakeButton("■ 停止动作", delegate {
                if (Character != null) { Character.StopGesture(); SetStatus("已停止，回到待机"); RefreshLabels(); }
            }));
            _body.Add(row);
        }

        /// <summary>语义 id → 面板显示名</summary>
        static string LabelOf(string id, string clipName)
        {
            switch (id)
            {
                case "idle":        return "待机";
                case "idle_breath": return "呼吸待机";
                case "idle_shift":  return "换重心";
                case "wave":        return "挥手";
                case "wave_both":   return "双手挥";
                case "clap":        return "鼓掌";
                case "nod":         return "点头";
                case "shake_head":  return "摇头";
                case "think":       return "思考";
                case "look_around": return "环顾";
                case "walk":        return "走路";
                case "jog":         return "慢跑";
                case "sit":         return "坐下";
                case "sit_point":   return "坐着指";
                case "kneel":       return "单膝跪";
            }
            return string.IsNullOrEmpty(clipName) ? id : clipName;
        }

        void BuildPoseSection()
        {
            Section("站姿");
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.Add(MakeButton("自然站立", delegate {
                if (Character != null) { Character.SetBasePose("idle"); SetStatus("站姿 → 自然站立"); }
            }));
            row.Add(MakeButton("T-Pose", delegate {
                if (Character != null) { Character.SetBasePose("tpose"); SetStatus("站姿 → T-Pose"); }
            }));
            _body.Add(row);
        }

        void BuildPlaybackSection()
        {
            Section("播放控制");

            var row1 = new VisualElement();
            row1.style.flexDirection = FlexDirection.Row;
            row1.style.alignItems = Align.Center;
            var autoTog = MakeToggle("自动播放", true, delegate(bool v) {
                if (Character != null) { Character.SetAutoPlay(v); SetStatus("自动播放 " + (v ? "开" : "关")); }
            });
            autoTog.style.flexGrow = 1;
            row1.Add(autoTog);
            _body.Add(row1);

            var row2 = new VisualElement();
            row2.style.flexDirection = FlexDirection.Row;
            row2.style.alignItems = Align.Center;
            var blinkTog = MakeToggle("眨眼", true, delegate(bool v) {
                if (Character != null && Character.Blink != null) { Character.Blink.BlinkEnabled = v; SetStatus("眨眼 " + (v ? "开" : "关")); }
            });
            blinkTog.style.flexGrow = 1;
            row2.Add(blinkTog);
            _body.Add(row2);

            _body.Add(MakeSlider("表情间隔", 1f, 10f, 2.4f, delegate(float v) {
                if (Character != null) Character.ExpressionInterval = v;
                SetStatus("表情间隔 " + v.ToString("F1") + "s");
            }));
            _body.Add(MakeSlider("动作间隔", 1f, 15f, 4.5f, delegate(float v) {
                if (Character != null) Character.GestureInterval = v;
                SetStatus("动作间隔 " + v.ToString("F1") + "s");
            }));
        }

        void BuildTuningSection()
        {
            Section("微调与预设");
            _tuningHost = new VisualElement();
            _body.Add(_tuningHost);

            _body.Add(MakeButton("读取当前表情配方", delegate { SyncTuningFromCurrent(); }));
            _body.Add(MakeButton("保存为自定义预设", delegate { SaveCurrentAsPreset(); }));
        }

        /// <summary>按当前表情库定义，生成形态键滑条</summary>
        void SyncTuningFromCurrent()
        {
            if (_tuningHost == null) return;
            _tuningHost.Clear();
            _tuneSliders.Clear();

            if (Character == null || Character.Expressions == null) { SetStatus("❌ 无角色"); return; }
            var def = Yixuan.Face.ExpressionLibrary.Get(Character.Expressions.CurrentId);
            if (def == null || def.Morphs == null || def.Morphs.Length == 0)
            {
                SetStatus("当前表情无配方（中性）");
                return;
            }

            var smr = Character.GetComponentInChildren<SkinnedMeshRenderer>();
            var mesh = smr != null ? smr.sharedMesh : null;

            for (int i = 0; i < def.Morphs.Length; i++)
            {
                var mk = def.Morphs[i];
                if (mk == null) continue;
                string morphName = mk.Morph;
                float cur = mk.Weight;
                // 用实际权重初始化
                if (mesh != null)
                {
                    int idx = FindMorph(mesh, morphName);
                    if (idx >= 0) cur = smr.GetBlendShapeWeight(idx) / 100f;
                }

                var wrap = new VisualElement();
                wrap.style.marginTop = 3; wrap.style.marginBottom = 3;
                var lbl = new Label(morphName + "  " + cur.ToString("F2"));
                lbl.style.color = TextDim; lbl.style.fontSize = 11;
                wrap.Add(lbl);

                var s = new Slider(0f, 1f);
                s.value = cur;
                string mn = morphName;
                Label lb = lbl;
                Slider sl = s;
                s.RegisterValueChangedCallback(delegate(ChangeEvent<float> e) {
                    lb.text = mn + "  " + e.newValue.ToString("F2");
                    if (smr != null) {
                        int idx = FindMorph(smr.sharedMesh, mn);
                        if (idx >= 0) smr.SetBlendShapeWeight(idx, e.newValue * 100f);
                    }
                    _tuneSliders[mn] = e.newValue;
                });
                _tuneSliders[morphName] = cur;
                wrap.Add(s);
                _tuningHost.Add(wrap);
            }
            SetStatus("已载入 " + def.Id + " 的 " + def.Morphs.Length + " 个形态键");
        }

        void SaveCurrentAsPreset()
        {
            if (_preset == null) _preset = FindObjectOfType<Yixuan.Core.PresetStore>();
            if (_preset == null)
            {
                var holder = new GameObject("PresetStore");
                _preset = holder.AddComponent<Yixuan.Core.PresetStore>();
            }
            if (Character == null || Character.Expressions == null) { SetStatus("❌ 无角色"); return; }

            string id = Character.Expressions.CurrentId;
            var list = new List<Yixuan.Core.PresetStore.MorphPair>();
            foreach (var kv in _tuneSliders)
                list.Add(new Yixuan.Core.PresetStore.MorphPair { name = kv.Key, weight = kv.Value });

            _preset.SaveCustomExpression(id + "_custom", id + "(自定义)", list);
            SetStatus("✅ 已保存预设 " + id + "_custom（" + list.Count + " 键）");
        }

        static int FindMorph(Mesh mesh, string name)
        {
            if (mesh == null || string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < mesh.blendShapeCount; i++)
                if (mesh.GetBlendShapeName(i) == name) return i;
            for (int i = 0; i < mesh.blendShapeCount; i++)
                if (mesh.GetBlendShapeName(i).Contains(name)) return i;
            return -1;
        }

        void BuildLightingSection()
        {
            Section("照明");

            _body.Add(MakeSlider("曝光", -2f, 1f, -0.15f, delegate(float v) {
                if (Lighting != null) { Lighting.SetExposure(v); SetStatus("曝光 " + v.ToString("F2")); }
            }));

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            string[] bgNames = { "深灰", "蓝灰", "亮灰" };
            for (int i = 0; i < bgNames.Length; i++) {
                int idx = i;
                row.Add(MakeButton(bgNames[i], delegate {
                    if (Lighting != null) { Lighting.SetBackground(idx); SetStatus("背景 → " + bgNames[idx]); }
                }));
            }
            _body.Add(row);
        }

        // ============================================================
        //  组件工厂
        // ============================================================
        void Section(string text)
        {
            var box = MakeBox(BgSection);
            box.style.marginTop = 10;
            box.style.marginBottom = 4;
            box.style.paddingLeft = 8; box.style.paddingRight = 8;
            box.style.paddingTop = 6; box.style.paddingBottom = 6;
            box.style.borderTopLeftRadius = 5; box.style.borderTopRightRadius = 5;
            box.style.borderBottomLeftRadius = 5; box.style.borderBottomRightRadius = 5;

            var lbl = new Label(text);
            lbl.style.color = Accent;
            lbl.style.fontSize = 13;
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.marginBottom = 2;
            box.Add(lbl);
            _body.Add(box);
        }

        static VisualElement MakeBox(Color c)
        {
            var e = new VisualElement();
            e.style.backgroundColor = c;
            return e;
        }

        VisualElement MakeGrid(string[] labels, string[] ids, System.Action<string> onClick)
        {
            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            for (int i = 0; i < labels.Length; i++) {
                string id = (i < ids.Length) ? ids[i] : labels[i];
                grid.Add(MakeButton(labels[i], delegate { onClick(id); }));
            }
            return grid;
        }

        VisualElement MakeButton(string text, System.Action onClick)
        {
            var btn = new Button();
            btn.text = text;
            btn.style.fontSize = 12;
            btn.style.color = TextMain;
            btn.style.backgroundColor = new Color(0.22f, 0.25f, 0.32f, 1f);
            btn.style.marginLeft = 2; btn.style.marginRight = 2;
            btn.style.marginTop = 2; btn.style.marginBottom = 2;
            btn.style.paddingLeft = 8; btn.style.paddingRight = 8;
            btn.style.paddingTop = 5; btn.style.paddingBottom = 5;
            btn.style.minWidth = 62;
            btn.style.borderTopLeftRadius = 4; btn.style.borderTopRightRadius = 4;
            btn.style.borderBottomLeftRadius = 4; btn.style.borderBottomRightRadius = 4;
            btn.style.borderLeftWidth = 0; btn.style.borderRightWidth = 0;
            btn.style.borderTopWidth = 0; btn.style.borderBottomWidth = 0;
            if (onClick != null) btn.clicked += delegate { onClick(); };
            return btn;
        }

        VisualElement MakeToggle(string text, bool def, System.Action<bool> onChange)
        {
            var t = new Toggle(text);
            t.value = def;
            t.style.color = TextMain;
            t.style.fontSize = 12;
            if (onChange != null) t.RegisterValueChangedCallback(delegate(ChangeEvent<bool> e) { onChange(e.newValue); });
            return t;
        }

        VisualElement MakeSlider(string label, float min, float max, float def, System.Action<float> onChange)
        {
            var wrap = new VisualElement();
            wrap.style.marginTop = 4; wrap.style.marginBottom = 4;

            var lbl = new Label(label + "  " + def.ToString("F1"));
            lbl.style.color = TextDim;
            lbl.style.fontSize = 11;
            wrap.Add(lbl);

            var s = new Slider(min, max);
            s.value = def;
            s.RegisterValueChangedCallback(delegate(ChangeEvent<float> e) {
                lbl.text = label + "  " + e.newValue.ToString("F1");
                if (onChange != null) onChange(e.newValue);
            });
            wrap.Add(s);
            return wrap;
        }

        void SetStatus(string s)
        {
            if (_statusLabel != null) _statusLabel.text = s;
        }

        void RefreshLabels()
        {
            if (Character == null) return;
            if (_gestureLabel != null) _gestureLabel.text = "上次：" + Character.LastAction;
        }

        void Update()
        {
            // 轻量刷新（每秒 1 次，避免每帧开销）
            if (Time.frameCount % 60 != 0) return;
            if (Character == null) { ResolveRefs(); return; }
            RefreshLabels();
        }
    }
}
