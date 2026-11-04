using UnityEngine;

namespace Yixuan.Motion
{
    /// <summary>
    /// 程序化待机微动作：呼吸 + 重心微移 + 头部轻晃。
    ///
    /// 【为什么需要这个】
    /// 只做"随机播放待机动画"的话，两次动作之间角色是完全静止的，看起来像掉线。
    /// 呼吸/微晃是"每帧都在跑"的底层动作，才是让角色一眼看着活着的原因。
    ///
    /// 参数来自 Blender 实测标定：
    ///   呼吸 周期 3.0s 幅度 ±2.2°
    ///   重心 周期 6.0s 幅度 ±1.2°
    ///   头部 周期 5.0s 幅度 ±1.5°
    ///
    /// 【与 Animator 的关系】
    /// 本脚本直接操作骨骼 Transform.localRotation，会与 Animator 冲突。
    /// 使用前提：Animator.cullingMode 正常 + 该骨骼未被 Animator 驱动，
    /// 或者干脆不用 Animator（状态机自己驱动骨骼）。
    /// </summary>
    [DisallowMultipleComponent]
    public class ProceduralIdleMotion : MonoBehaviour
    {
        [Header("骨骼引用（留空则按名称自动查找）")]
        public Transform SpineBone;      // 上半身2
        public Transform ChestBone;      // 胸
        public Transform CenterBone;     // センター
        public Transform NeckBone;       // 首
        public Transform HeadBone;       // 頭

        [Header("骨骼名（MMD 日文命名）")]
        public string SpineName = "上半身2";
        public string ChestName = "胸";
        public string CenterName = "センター";
        public string NeckName = "首";
        public string HeadName = "頭";

        [Header("呼吸")]
        public float BreathPeriod = 3.0f;
        public float BreathAmount = 2.2f;

        [Header("重心微移")]
        public float SwayPeriod = 6.0f;
        public float SwayAmount = 1.2f;

        [Header("头部轻晃")]
        public float HeadPeriod = 5.0f;
        public float HeadAmount = 1.5f;

        [Header("平滑")]
        [Tooltip("额外的一阶平滑，0 = 不平滑")]
        [Range(0f, 20f)] public float Smoothing = 6f;

        [Header("运行控制")]
        public bool Enabled = true;
        [Tooltip("全局幅度倍率，调试时可设 0 观察静止姿态")]
        [Range(0f, 3f)] public float AmplitudeScale = 1f;

        [Header("旋转轴映射（★ 需按实际模型标定）")]
        [Tooltip("呼吸作用的轴：MMD 上半身2 在 Blender 是 X 轴。Unity 需实测")]
        public Axis BreathAxis = Axis.X;
        [Tooltip("转头（左右）轴：Blender 标定为 Z")]
        public Axis HeadYawAxis = Axis.Y;
        [Tooltip("点头（上下）轴：Blender 标定为 X")]
        public Axis HeadPitchAxis = Axis.X;
        [Tooltip("重心摆动轴")]
        public Axis SwayAxis = Axis.Z;

        public enum Axis { X, Y, Z }

        // 基准姿态（Awake 时记录，之后在基准上叠加，避免累积漂移）
        Quaternion _spineBase, _chestBase, _centerBase, _neckBase, _headBase;
        float _t;
        bool _ready;

        void Awake()
        {
            Relink();
        }

        void Start()
        {
            // 再试一次：Awake 时若因实例化时序问题没找到，Start 通常已经就绪
            if (SpineBone == null) Relink();
        }

        /// <summary>
        /// 重新查找骨骼并缓存基准姿态。
        /// public 是因为 AddComponent 的时序可能让 Awake 早于模型就绪，
        /// 外部（YixuanCharacter）可在准备完成后显式调用。
        /// </summary>
        public void Relink()
        {
            AutoFind();
            CacheBasePose();
        }

        void AutoFind()
        {
            // 先按名字找
            if (SpineBone == null) SpineBone = FindDeep(transform, SpineName);
            if (ChestBone == null) ChestBone = FindDeep(transform, ChestName);
            if (CenterBone == null) CenterBone = FindDeep(transform, CenterName);
            if (NeckBone == null) NeckBone = FindDeep(transform, NeckName);
            if (HeadBone == null) HeadBone = FindDeep(transform, HeadName);

            // 兜底：按 MMD 常见备选名再找一轮
            if (SpineBone == null) SpineBone = FindDeep(transform, "上半身");
            if (ChestBone == null) ChestBone = FindDeep(transform, "上半身1");
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;

            // 先精确匹配整棵树（避免 "上半身" 抢先命中 "上半身2" 这种情况）
            var exact = FindDeepExact(root, name);
            if (exact != null) return exact;

            // 再放宽为包含匹配
            return FindDeepContains(root, name);
        }

        static Transform FindDeepExact(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeepExact(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        static Transform FindDeepContains(Transform root, string name)
        {
            if (root.name.Contains(name)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeepContains(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        void CacheBasePose()
        {
            if (SpineBone != null) _spineBase = SpineBone.localRotation;
            if (ChestBone != null) _chestBase = ChestBone.localRotation;
            if (CenterBone != null) _centerBase = CenterBone.localRotation;
            if (NeckBone != null) _neckBase = NeckBone.localRotation;
            if (HeadBone != null) _headBase = HeadBone.localRotation;
            _ready = (SpineBone != null);
        }

        void Update()
        {
            if (!Enabled || !_ready) return;

            _t += Time.deltaTime;

            // 三个不同周期的正弦，避免同步导致的机械感
            float breath = Mathf.Sin(_t * 2f * Mathf.PI / Mathf.Max(0.01f, BreathPeriod)) * BreathAmount;
            float sway = Mathf.Sin(_t * 2f * Mathf.PI / Mathf.Max(0.01f, SwayPeriod)) * SwayAmount;
            float headWave = Mathf.Sin(_t * 2f * Mathf.PI / Mathf.Max(0.01f, HeadPeriod)) * HeadAmount;

            breath *= AmplitudeScale;
            sway *= AmplitudeScale;
            headWave *= AmplitudeScale;

            // 呼吸：上半身 + 胸（胸幅度略小，避免呼吸过猛）
            if (SpineBone != null)
                SpineBone.localRotation = _spineBase * AxisRot(BreathAxis, breath * 0.5f);
            if (ChestBone != null)
                ChestBone.localRotation = _chestBase * AxisRot(BreathAxis, breath * 0.4f);

            // 重心：センター 轻微摆动
            if (CenterBone != null)
                CenterBone.localRotation = _centerBase * AxisRot(SwayAxis, sway);

            // 头部：偏航 + 俯仰，幅度减半作用在颈部更自然
            if (NeckBone != null)
                NeckBone.localRotation = _neckBase
                    * AxisRot(HeadPitchAxis, -breath * 0.25f)
                    * AxisRot(HeadYawAxis, headWave * 0.5f);
            if (HeadBone != null)
                HeadBone.localRotation = _headBase
                    * AxisRot(HeadYawAxis, headWave * 0.5f)
                    * AxisRot(HeadPitchAxis, headWave * 0.3f);

            _lastBreath = breath;
            _lastSway = sway;
            _lastHead = headWave;
        }

        /// <summary>把基准姿态恢复（关闭本组件时调用，避免留下偏移）</summary>
        public void RestoreBasePose()
        {
            if (SpineBone != null) SpineBone.localRotation = _spineBase;
            if (ChestBone != null) ChestBone.localRotation = _chestBase;
            if (CenterBone != null) CenterBone.localRotation = _centerBase;
            if (NeckBone != null) NeckBone.localRotation = _neckBase;
            if (HeadBone != null) HeadBone.localRotation = _headBase;
        }

        void OnDisable() => RestoreBasePose();

        static Quaternion AxisRot(Axis a, float degrees)
        {
            switch (a)
            {
                case Axis.X: return Quaternion.Euler(degrees, 0f, 0f);
                case Axis.Y: return Quaternion.Euler(0f, degrees, 0f);
                default:     return Quaternion.Euler(0f, 0f, degrees);
            }
        }

        float _lastBreath, _lastSway, _lastHead;

        /// <summary>验证用：当前各波形瞬时值 + 骨骼查找结果</summary>
        public string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[ProceduralIdleMotion] enabled={Enabled} t={_t:F2}s ready={_ready}");
            sb.AppendLine($"  波形: breath={_lastBreath:F3} sway={_lastSway:F3} head={_lastHead:F3}");
            sb.AppendLine($"  骨骼: spine={(SpineBone != null ? SpineBone.name : "❌未找到")} chest={(ChestBone != null ? ChestBone.name : "❌")} center={(CenterBone != null ? CenterBone.name : "❌")} neck={(NeckBone != null ? NeckBone.name : "❌")} head={(HeadBone != null ? HeadBone.name : "❌")}");
            return sb.ToString();
        }
    }
}
