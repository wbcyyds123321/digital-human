using UnityEngine;

namespace Yixuan.Motion
{
    /// <summary>
    /// 地面吸附 —— 让角色永远踩在地面上，不悬空、不穿地。
    ///
    /// 【为什么需要】
    /// Mixamo 的动画是给"另一个角色"做的，髋高和仪玄不一样。
    /// 重定向后同一个动作的垂直落位会偏。实测（网格最低顶点）：
    ///     Standing Idle   -0.001   ✅ 贴地
    ///     Waving Gesture  +0.031   ✅
    ///     Thinking        -0.039   ⚠️
    ///     Walking         -0.038   ⚠️
    ///     Jogging         -0.105   ❌ 穿地 10cm
    ///     Sitting Idle    -0.446   ❌ 穿地 45cm（整个人沉到地下）
    ///     Kneeling Idle   -0.485   ❌ 穿地 48cm
    /// 表现就是"人像被吊在空中 / 下半身陷进地里"。
    ///
    /// 【为什么不直接按网格最低点吸附】
    /// 试过，不行。坐姿时网格最低点在【胯下】（裙摆/衣摆），
    /// 比脚还低 42cm。按网格吸附会把角色抬高 45cm —— 过度修正，
    /// 变成"悬空坐着"。
    /// 所以默认按【脚骨】吸附（つま先/足首），这才是真正的接地点。
    ///
    /// 【和物理的关系】
    /// 本组件不用 Rigidbody、不用重力 —— 那是"可操控角色"的做法。
    /// 这里角色是固定站位、只播动画，所以只需要一个每帧的垂直偏移修正。
    /// 这也是数字人/展示类项目的标准做法。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]   // 必须在动画之后跑
    public class GroundSnap : MonoBehaviour
    {
        [Header("开关")]
        public bool Enabled = true;

        [Tooltip("接地点参考：脚趾骨 / 脚踝骨 / 网格最低顶点")]
        public RefPoint Reference = RefPoint.Toes;

        [Tooltip("目标接地高度（地面 y）；UseWorldGround=true 时作为兜底")]
        public float GroundY = 0f;

        [Tooltip("按角色当前位置动态取地面高度（大地图 GroundSnap 用；旧微缩景观可关）")]
        public bool UseWorldGround = true;

        [Tooltip("平滑速度，0 = 不平滑（每帧硬贴，坐下这种大位移会瞬移）")]
        [Range(0f, 30f)] public float Smoothing = 8f;

        [Tooltip("最大修正量（米）—— 防止异常帧把角色甩飞")]
        public float MaxOffset = 0.8f;

        [Header("调试")]
        public bool VerboseLog = false;

        public enum RefPoint { Toes, Ankles, MeshLowest }

        // 运行时
        Transform _root;
        Transform _toeL, _toeR, _ankL, _ankR;
        SkinnedMeshRenderer _smr;
        float _currentOffset;
        bool _ready;
        float _baseRootY;

        void Awake()
        {
            _root = transform;
            _baseRootY = _root.localPosition.y;
            Relink();
        }

        void Start()
        {
            // 首帧先把脚放到正确高度，避免进入 Play 的前 0.3 秒看到脚埋在地里再慢慢升起来。
            if (Enabled) SnapNow();
        }

        /// <summary>当前该贴的地面高度：大地图按高度图动态取，否则用固定 GroundY。</summary>
        float TargetGroundY()
        {
            if (UseWorldGround && Yixuan.World.WorldData.Map != null)
                return Yixuan.World.WorldData.GroundHeightAt(_root.position);
            return GroundY;
        }

        /// <summary>
        /// 重新找骨骼（模型实例化时序可能让 Awake 时还没就绪）。
        ///
        /// ★ 骨名不再写死 —— 走 <see cref="Yixuan.Model.MmdBoneNames"/> 的中央表。
        ///   原来硬编码 "つま先.L"/"足首.L"（仪玄的命名），
        ///   换成庄方宜（"左つま先"/"左足首"）后会**一个都找不到**，
        ///   而 _ready 会因为 SMR 存在而为 true → 吸附静默失效，很难发现。
        /// </summary>
        public void Relink()
        {
            _toeL = Yixuan.Model.MmdBoneNames.Resolve("ToeL", transform);
            _toeR = Yixuan.Model.MmdBoneNames.Resolve("ToeR", transform);
            // 脚踝：直接用腿链的 Foot（也是变形骨）
            _ankL = Yixuan.Model.MmdBoneNames.Resolve("FootL", transform);
            _ankR = Yixuan.Model.MmdBoneNames.Resolve("FootR", transform);

            _smr = GetComponentInChildren<SkinnedMeshRenderer>();
            _ready = (_toeL != null || _ankL != null || _smr != null);
            if (VerboseLog)
                Debug.Log(string.Format("[GroundSnap] Relink 趾L={0} 趾R={1} 踝L={2} 踝R={3} 网格={4}",
                    _toeL != null ? _toeL.name : "无", _toeR != null ? _toeR.name : "无",
                    _ankL != null ? _ankL.name : "无", _ankR != null ? _ankR.name : "无", _smr != null));
            if (!_ready)
                Debug.LogWarning("[GroundSnap] 找不到接地点参考骨，吸附不生效");
        }

        /// <summary>当前姿势的最低接地点（世界 Y）</summary>
        float MeasureLowest()
        {
            switch (Reference)
            {
                case RefPoint.Toes:
                    if (_toeL == null && _toeR == null) return 0f;
                    if (_toeL == null) return _toeR.position.y;
                    if (_toeR == null) return _toeL.position.y;
                    return Mathf.Min(_toeL.position.y, _toeR.position.y);

                case RefPoint.Ankles:
                    if (_ankL == null && _ankR == null) return 0f;
                    if (_ankL == null) return _ankR.position.y;
                    if (_ankR == null) return _ankL.position.y;
                    return Mathf.Min(_ankL.position.y, _ankR.position.y);

                case RefPoint.MeshLowest:
                    if (_smr == null) return 0f;
                    var m = new Mesh();
                    _smr.BakeMesh(m);
                    var vs = m.vertices;
                    float lo = 9999f;
                    for (int i = 0; i < vs.Length; i++) if (vs[i].y < lo) lo = vs[i].y;
                    Object.Destroy(m);
                    return lo;
            }
            return 0f;
        }

        void LateUpdate()
        {
            if (!Enabled || !_ready) return;

            // 当前最低点（世界坐标，此时 root 已经在 base+_currentOffset）。
            // 注意：want 是相对 _baseRootY 的偏移，所以要把当前偏移从测量值里扣掉再算，
            // 否则会形成 offset = want - offset 的反馈，只收敛到一半（草地/台阶上会浮空几厘米）。
            float lowest = MeasureLowest();
            float want = TargetGroundY() - (lowest - _currentOffset);

            if (want > MaxOffset) want = MaxOffset;
            if (want < -MaxOffset) want = -MaxOffset;

            if (Smoothing > 0.01f)
                _currentOffset = Mathf.Lerp(_currentOffset, want, 1f - Mathf.Exp(-Smoothing * Time.deltaTime));
            else
                _currentOffset = want;

            Vector3 p = _root.position;
            p.y = _baseRootY + _currentOffset;
            _root.position = p;
        }

        /// <summary>立刻吸附（切换动作时用，避免渐变期间穿地）</summary>
        public void SnapNow()
        {
            if (!_ready) return;
            _currentOffset = 0f;
            Vector3 p = _root.position; p.y = _baseRootY; _root.position = p;
            float want = TargetGroundY() - MeasureLowest();
            if (want > MaxOffset) want = MaxOffset;
            if (want < -MaxOffset) want = -MaxOffset;
            _currentOffset = want;
            p.y = _baseRootY + _currentOffset;
            _root.position = p;
        }

        public string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[GroundSnap] " + (Enabled ? "开" : "关") +
                          "  参考=" + Reference + "  最低点=" + MeasureLowest().ToString("F4") +
                          "  偏移=" + _currentOffset.ToString("F4"));
            sb.AppendLine("  骨: 趾L=" + (_toeL != null) + " 趾R=" + (_toeR != null) +
                          " 踝L=" + (_ankL != null) + " 踝R=" + (_ankR != null) +
                          " 网格=" + (_smr != null));
            return sb.ToString();
        }
    }
}
