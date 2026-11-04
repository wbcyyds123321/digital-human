using UnityEngine;
using System.Collections.Generic;

namespace Yixuan.Face
{
    /// <summary>
    /// 仪玄表情状态机：中性 / 微笑 / 生气 / 惊讶。
    ///
    /// 配方来自 Blender 侧实测（yixuan_poses.py），已在 Blender 渲染验证：
    ///   微笑 = 笑い0.35 + 口角上げ0.75 + にこり0.15
    ///   生气 = 怒り0.90 + 怒り目0.75 + 口角下げ0.60 + じと目0.30
    ///   惊讶 = びっくり1.00 + お0.75 + 上0.50
    ///
    /// 【踩坑记录 —— 为什么最终用"每帧直接写"】
    /// 第 1 版：用 _live 字典缓存活跃键 + 过渡 → 出现"目标 0.9 但实际读到 0"的状态残留。
    /// 第 2 版：改成 last/target 两张表 + Update 插值 → 仍然出现部分索引（怒り38、
    ///         びっくり6）读回为 0，而 Shapes.Set 单独调用却完全正常。
    /// 第 3 版（本文件）：**放弃过渡插值，Update 每帧直接按 Current 写入目标值**。
    ///         实测证明 ApplyNow（硬切）100% 可靠，那就把每一帧都当成硬切。
    ///         好处：无状态残留、无插值 bug、可预测。
    ///         代价：表情切换是瞬时的。对"AI 驱动表情切换"这个用例完全够用。
    /// </summary>
    // Direct morph driver runs after Vrm10Instance (11000).
    [DefaultExecutionOrder(12000)]
    [DisallowMultipleComponent]
    public class YixuanExpressions : MonoBehaviour
    {
        public enum Expression { Neutral, Smile, Angry, Surprised }

        [Header("目标（留空自动查找）")]
        public YixuanBlendShapes Shapes;

        [Header("当前状态")]
        public Expression Current = Expression.Neutral;

        [Header("强度倍率")]
        [Range(0f, 2f)] public float IntensityScale = 1f;

        [Header("切换到 Neutral 时清除全部残留")]
        public bool ClearAllOnNeutral = true;

        [Header("运行控制")]
        public bool Enabled = true;
        [Tooltip("外部口型驱动（MouthDriver）接管 A/I/U/E/O；开启后本组件不再写/清这五个口型键")]
        public bool ExternalMouthControl = false;

        [Header("调试")]
        public bool VerboseLog = false;

        // 当前目标表 + 上一帧影响过的键（用于清理残留）
        Dictionary<int, float> _target = new Dictionary<int, float>();
        readonly HashSet<int> _touched = new HashSet<int>();

        // ★ 动作库风格的表情定义（按 id 应用），非空时优先于枚举 Current
        ExpressionLibrary.ExpressionDef _activeDef;
        string _activeId;

        /// <summary>当前生效的表情 id（供 UI 反显）</summary>
        public string CurrentId { get { return _activeId ?? Current.ToString().ToLowerInvariant(); } }

        void Start()
        {
            EnsureShapes();
            RebuildTarget();
        }

        void Update()
        {
            if (!Enabled) return;
            EnsureShapes();
            if (Shapes == null || Shapes.Renderer == null) return;

            // 每帧重建目标表（只有 3~4 个条目）
            RebuildTarget();

            // 外部口型模式：表情不碰 A/I/U/E/O 五个口型键
            if (ExternalMouthControl && Shapes != null)
            {
                _target.Remove(Shapes.A);
                _target.Remove(Shapes.I);
                _target.Remove(Shapes.U);
                _target.Remove(Shapes.E);
                _target.Remove(Shapes.O);
            }

            // 只写目标键 + 上一帧写过但现在不在目标里的键（清理残留）
            // 不做全量 41 键遍历 —— 更精准，也避免任何潜在干扰
            var stale = new List<int>();
            foreach (var idx in _touched)
                if (!_target.ContainsKey(idx) && !(ExternalMouthControl && IsMouthIndex(idx)))
                    stale.Add(idx);

            foreach (var idx in _target) Shapes.Set(idx.Key, idx.Value);
            foreach (var idx in stale) Shapes.Set(idx, 0f);

            _touched.Clear();
            foreach (var idx in _target.Keys) _touched.Add(idx);
        }

        /// <summary>是否口型键（A/I/U/E/O）</summary>
        bool IsMouthIndex(int idx)
        {
            if (Shapes == null || idx < 0) return false;
            return idx == Shapes.A || idx == Shapes.I || idx == Shapes.U || idx == Shapes.E || idx == Shapes.O;
        }

        /// <summary>
        /// 按形态键名字查索引。**精确优先，模糊兜底**。
        ///
        /// 【为什么必须这样 —— 踩过的坑】
        /// 直接 Contains 会让 "い" 命中 "笑い"、"怒り" 命中 "怒り目"。
        /// 之前就是这个问题导致表情配方错位。
        /// </summary>
        static int FindMorphIndex(Mesh mesh, string name)
        {
            if (mesh == null || string.IsNullOrEmpty(name)) return -1;
            // 第一轮：精确
            for (int i = 0; i < mesh.blendShapeCount; i++)
                if (mesh.GetBlendShapeName(i) == name) return i;
            // 第二轮：包含
            for (int i = 0; i < mesh.blendShapeCount; i++)
                if (mesh.GetBlendShapeName(i).Contains(name)) return i;
            return -1;
        }

        /// <summary>
        /// 按表情库 id 应用表情（供 UI / AI 调用）。
        /// 未找到时回退到 4 个内置枚举。
        /// </summary>
        public void ApplyById(string id)
        {
            EnsureShapes();
            var def = ExpressionLibrary.Get(id);

            if (def == null)
            {
                // 回退：把 id 当作旧枚举名处理
                _activeDef = null;
                _activeId = id;
                SetExpression(id ?? "neutral");
                return;
            }

            _activeDef = def;
            _activeId = def.Id;
            RebuildTarget();
            ApplyAll();     // 立即生效，不等下一帧
            if (VerboseLog) Debug.Log("[YixuanExpressions] ApplyById -> " + def.Id + " (" + def.Label + ")");
        }

        /// <summary>把所有形态键归零（调试/切换用）</summary>
        public void ClearAll()
        {
            _activeDef = null;
            _activeId = "neutral";
            _target.Clear();
            var r = ResolveRenderer();
            if (r != null && r.sharedMesh != null)
                for (int i = 0; i < r.sharedMesh.blendShapeCount; i++) r.SetBlendShapeWeight(i, 0f);
            _touched.Clear();
        }

        /// <summary>
        /// 自愈：Shapes 是普通 C# 类（非 UnityEngine.Object），
        /// **不会被 Unity 序列化**，Prefab 实例化 / 场景重载后必然是 null。
        /// </summary>
        public void EnsureShapes()
        {
            if (Shapes != null && Shapes.Renderer != null && Shapes.ResolvedCount > 0) return;

            SkinnedMeshRenderer smr = null;
            if (Shapes != null && Shapes.Renderer != null) smr = Shapes.Renderer;
            if (smr == null) smr = GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) return;

            Shapes = new YixuanBlendShapes(smr).ResolveAll();
            if (VerboseLog)
                Debug.Log($"[YixuanExpressions] 初始化 Shapes: {Shapes.ResolvedCount} 个形态键");
        }

        /// <summary>
        /// 构造目标表。
        ///
        /// 【重要：形态键选择】
        /// 实测发现部分索引在运行时会"写不进去"（笑い1 / 怒り38 / びっくり6），
        /// 而直接 smr.SetBlendShapeWeight 单测又完全正常。为规避这个问题，
        /// 这里优先使用**等效的邻近形态键**：
        ///   笑い(1) → にやり(25)   （都是笑，にやり偏坏笑但可用）
        ///   怒り(38) → 怒り目(11)   （眼部怒意，配合口角下げ足够传达生气）
        ///   びっくり(6) → びっくり右(7)（单侧惊讶，视觉上等价）
        /// </summary>
        void RebuildTarget()
        {
            _target.Clear();
            if (Shapes == null) return;

            void Add(int idx, float w)
            {
                if (idx < 0) return;
                float v = Mathf.Clamp01(w * IntensityScale);
                if (v > 0.001f) _target[idx] = v;
            }

            // ★ 优先：按表情库定义（按形态键名字解析索引，最稳健）
            if (_activeDef != null)
            {
                var mesh = Shapes.Renderer != null ? Shapes.Renderer.sharedMesh : null;
                if (mesh != null && _activeDef.Morphs != null)
                {
                    for (int i = 0; i < _activeDef.Morphs.Length; i++)
                    {
                        var mk = _activeDef.Morphs[i];
                        if (mk == null || string.IsNullOrEmpty(mk.Morph)) continue;
                        int idx = FindMorphIndex(mesh, mk.Morph);
                        if (idx >= 0) Add(idx, mk.Weight);
                    }
                }
                return;
            }

            switch (Current)
            {
                case Expression.Smile:
                    Add(Shapes.MouthCornerUp, 0.80f);   // 主要靠嘴部出笑容
                    Add(Shapes.SmileSoft,     0.35f);   // にこり
                    Add(Shapes.Grin,          0.30f);   // にやり（替代 笑い）
                    break;

                case Expression.Angry:
                    Add(Shapes.AngryEyes,       0.85f);  // 怒り目（眼部怒意）
                    Add(Shapes.MouthCornerDown, 0.65f);  // 口角下げ
                    Add(Shapes.Glare,           0.45f);  // じと目
                    Add(Shapes.Serious,         0.40f);  // 真面目
                    break;

                case Expression.Surprised:
                    Add(Shapes.O,         0.85f);        // お（张嘴）
                    Add(Shapes.MouthWide, 0.60f);        // 口横広げ
                    Add(Shapes.Surprised, 1.00f);        // びっくり（可能写不进，失败也无害）
                    Add(Shapes.LookUp,    0.50f);        // 上
                    break;

                // Neutral: 空表 → 全部清零
            }
        }

        // ---------------- 公共接口 ----------------

        public void SetExpression(Expression e)
        {
            _activeDef = null;          // 退出库模式，回到枚举配方
            _activeId = e.ToString().ToLowerInvariant();
            if (Current == e) return;
            Current = e;
            RebuildTarget();
            ApplyAll();
            if (VerboseLog) Debug.Log($"[YixuanExpressions] -> {e}");
        }

        /// <summary>
        /// 把 _target 写入 Renderer，并清理已不在目标里的残留键。
        ///
        /// 【为什么直接操作 Renderer 而不走 Shapes.Set】
        /// 实测发现 Shapes.Set 这条路径在 Update 里写入后读回为 0，
        /// 但直接 smr.SetBlendShapeWeight 100% 成功。
        /// 为排除中间层干扰，这里直接用 SkinnedMeshRenderer 的 API 写。
        /// </summary>
        public void ApplyAll()
        {
            var r = ResolveRenderer();
            if (r == null) return;

            // 清理上一帧写过、但已不在目标里的键
            foreach (var idx in _touched)
            {
                if (ExternalMouthControl && IsMouthIndex(idx)) continue;
                if (!_target.ContainsKey(idx)) r.SetBlendShapeWeight(idx, 0f);
            }

            // 写入目标值：用显式数组一次写完，避免枚举器/哈希顺序的任何影响
            int n = _target.Count;
            if (n > 0)
            {
                var keys = new int[n];
                var vals = new float[n];
                _target.Keys.CopyTo(keys, 0);
                _target.Values.CopyTo(vals, 0);
                for (int i = 0; i < n; i++)
                {
                    int idx = keys[i];
                    if (idx < 0) continue;
                    if (ExternalMouthControl && IsMouthIndex(idx)) continue;
                    r.SetBlendShapeWeight(idx, vals[i] * 100f);
                }
            }

            _touched.Clear();
            foreach (var idx in _target.Keys) _touched.Add(idx);

            _applyCount++;
        }

        /// <summary>拿到真正要写的 Renderer（多路兜底）</summary>
        public SkinnedMeshRenderer ResolveRenderer()
        {
            if (Shapes != null && Shapes.Renderer != null) return Shapes.Renderer;
            return GetComponentInChildren<SkinnedMeshRenderer>();
        }

        /// <summary>应用次数（验证用）</summary>
        public int ApplyCount { get { return _applyCount; } }
        int _applyCount;

        /// <summary>
        /// LateUpdate 兜底：确保在其他所有组件（含 Blink / Idle）之后写入。
        /// </summary>
        void LateUpdate()
        {
            if (!Enabled) return;
            if (_target.Count > 0 || _touched.Count > 0) ApplyAll();
        }

        public void SetExpression(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "smile":     SetExpression(Expression.Smile); break;
                case "angry":     SetExpression(Expression.Angry); break;
                case "surprised": SetExpression(Expression.Surprised); break;
                default:          SetExpression(Expression.Neutral); break;
            }
        }

        /// <summary>验证用：列出目标与实际</summary>
        public string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[YixuanExpressions] 当前={Current}  目标键数={_target.Count}");
            var mesh = (Shapes != null && Shapes.Renderer != null) ? Shapes.Renderer.sharedMesh : null;
            if (mesh != null)
            {
                int shown = 0;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    float actual = mesh != null ? Shapes.Renderer.GetBlendShapeWeight(i) : 0f;
                    float target = _target.TryGetValue(i, out var t) ? t * 100f : 0f;
                    if (actual > 0.5f || target > 0.5f)
                    {
                        string flag = (System.Math.Abs(actual - target) < 2f) ? "✅" : "⚠️";
                        sb.AppendLine($"    {flag} [{i}] {mesh.GetBlendShapeName(i)}  实际={actual:F1}  目标={target:F1}");
                        shown++;
                    }
                }
                if (shown == 0) sb.AppendLine("    (全为 0 —— Neutral 状态)");
            }
            return sb.ToString();
        }
    }
}
