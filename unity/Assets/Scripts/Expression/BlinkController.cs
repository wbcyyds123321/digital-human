using UnityEngine;

namespace Yixuan.Face
{
    /// <summary>
    /// 随机间隔眨眼控制器。
    ///
    /// 参数全部来自 Blender 侧的实测标定（yixuan_poses.py / render_timeline.py）：
    ///   - 间隔 2.5~5.5 秒（确定性伪随机，同一种子可复现）
    ///   - 单次眨眼 0.167 秒（视觉上约 4 帧 @24fps）
    ///   - 峰值位置 0.40（闭合快、张开慢，贴近真实眨眼）
    ///
    /// 【为什么不用固定间隔】
    /// 固定每 3 秒眨一次，看久了明显是机器。参考 sidney 的实测结论。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(12200)]
    public class BlinkController : MonoBehaviour
    {
        [Header("目标")]
        public YixuanBlendShapes Shapes;

        [Header("间隔（秒）")]
        [Tooltip("两次眨眼的最短间隔")]
        public float MinInterval = 1.8f;
        [Tooltip("两次眨眼的最长间隔")]
        public float MaxInterval = 4.0f;

        [Header("单次眨眼")]
        [Tooltip("整次眨眼持续时间（闭合+张开）。调大 = 更明显")]
        public float Duration = 0.26f;
        [Tooltip("峰值位置 0~1。越小闭合越快")]
        [Range(0.1f, 0.9f)] public float PeakFraction = 0.40f;

        [Header("可见度增强（让眨眼一眼能看出来）")]
        [Tooltip("闭眼最大权重倍率。1.0 = 完全闭合，>1 会更用力（部分模型支持）")]
        [Range(0.5f, 1.0f)] public float ClosingStrength = 1.0f;
        [Tooltip("眨眼时连带轻微低头（度）。默认 0 —— 因为 ProceduralIdleMotion 也在控制 頭，避免互相覆盖")]
        public float BlinkNodDegrees = 0f;
        [Tooltip("眨眼时连带眉毛下压（如果模型有该形态键）")]
        public bool BlinkBrow = true;
        [Tooltip("闭眼时额外压低的眉毛量")]
        [Range(0f, 1f)] public float BrowAmount = 0.25f;

        [Header("随机")]
        [Tooltip("固定种子 → 间隔看起来随机但每次一致，方便回归对比")]
        public int Seed = 20260920;

        [Header("运行控制")]
        public bool Enabled = true;
        [Tooltip("是否眨眼（临时禁用可用于调试）")]
        public bool BlinkEnabled = true;

        [Header("调试")]
        public bool VerboseLog = false;

        float _timer;
        float _blinkStart = -1f;
        float _nextInterval;
        System.Random _rng;

        /// <summary>当前眨眼值 0~1（调试/验证用）</summary>
        public float CurrentValue { get; private set; }

        /// <summary>已眨眼次数</summary>
        public int BlinkCount { get; private set; }

        void Awake()
        {
            _rng = new System.Random(Seed);
            ScheduleNext();
            EnsureShapes();
        }

        void ScheduleNext()
        {
            _nextInterval = Mathf.Lerp(MinInterval, MaxInterval, (float)_rng.NextDouble());
            _timer = 0f;
        }

        /// <summary>
        /// 自愈：Shapes 是普通 C# 类，不会被 Unity 序列化，
        /// Prefab 实例化后必然为 null。这里自动重建。
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
                Debug.Log($"[Blink] 自动初始化 Shapes: 解析 {Shapes.ResolvedCount} 个, blinkIdx={Shapes.Blink}");

            EnsureHeadBone();
        }

        void Update()
        {
            if (!Enabled) return;

            EnsureShapes();
            if (Shapes == null || Shapes.Blink < 0)
            {
                CurrentValue = 0f;
                return;
            }

            if (!BlinkEnabled)
            {
                Apply(0f);
                return;
            }

            if (_blinkStart < 0f)
            {
                // 等待下一次眨眼
                _timer += Time.deltaTime;
                if (_timer >= _nextInterval)
                {
                    _blinkStart = 0f;
                    BlinkCount++;
                    if (VerboseLog) Debug.Log($"[Blink] 第 {BlinkCount} 次，间隔 {_nextInterval:F2}s");
                }
                else
                {
                    Apply(0f);
                    return;
                }
            }

            // 正在眨眼
            _blinkStart += Time.deltaTime;
            float p = _blinkStart / Duration;

            if (p >= 1f)
            {
                Apply(0f);
                _blinkStart = -1f;
                ScheduleNext();
                return;
            }

            // 非对称曲线：闭合快（0→PeakFraction），张开慢（PeakFraction→1）
            float v;
            if (p <= PeakFraction)
                v = p / PeakFraction;
            else
                v = (1f - p) / (1f - PeakFraction);

            v = Mathf.Clamp01(v);
            Apply(v);
        }

        // Reapply only the owned morphs after VRM; never repeat head-bone motion.
        void LateUpdate(){if(!Enabled||Shapes==null)return;Shapes.Set(Shapes.Blink,CurrentValue*ClosingStrength);if(BlinkBrow&&Shapes.LookDown>=0)Shapes.Set(Shapes.LookDown,CurrentValue*BrowAmount);}

        void Apply(float v)
        {
            CurrentValue = v;
            if (Shapes == null) return;

            // 主眨眼
            Shapes.Set(Shapes.Blink, v * ClosingStrength);

            // 连带：眉毛下压（让眨眼更明显）
            if (BlinkBrow && Shapes.LookDown >= 0)
                Shapes.Set(Shapes.LookDown, v * BrowAmount);

            // 连带：轻微低头（真人眨眼常伴随微小点头）
            if (BlinkNodDegrees > 0.01f && _headBone != null)
            {
                if (_headBase == default(Quaternion)) _headBase = _headBone.localRotation;
                _headBone.localRotation = _headBase * Quaternion.Euler(v * BlinkNodDegrees, 0f, 0f);
            }
        }

        Transform _headBone;
        Quaternion _headBase;

        void EnsureHeadBone()
        {
            if (_headBone != null) return;
            _headBone = FindDeep(transform, "頭");
            if (_headBone != null) _headBase = _headBone.localRotation;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>立即触发一次眨眼（外部调用，比如被点击时）</summary>
        public void TriggerBlink()
        {
            if (_blinkStart < 0f) _blinkStart = 0f;
        }

        /// <summary>验证用：返回当前状态字符串</summary>
        public string Status()
        {
            return $"BlinkController: enabled={Enabled} interval={_nextInterval:F2}s count={BlinkCount} value={CurrentValue:F3} blinkIdx={Shapes?.Blink}";
        }
    }
}
