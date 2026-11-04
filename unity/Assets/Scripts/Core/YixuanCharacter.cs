using UnityEngine;
using System.Collections.Generic;

namespace Yixuan.Core
{
    /// <summary>
    /// 仪玄数字人主状态机 —— 整合表情 / 待机微动作 / 眨眼 / 手势 / 视线。
    ///
    /// 时间轴（可在 Inspector 调）：
    ///   表情每 3.5s 循环切换：待机 → 微笑 → 生气 → 惊讶
    ///   手势每 5.0s 随机播放一个：挥手 / 点头 / 转身
    ///
    /// 设计原则（延续 Blender 侧验证过的思路）：
    ///   底层微动作（呼吸/眨眼）永远在跑，不依赖任何输入 —— 保证角色不会"死"
    ///   上层表情/手势是点缀，叠加在底层之上
    /// </summary>
    [DisallowMultipleComponent]
    public class YixuanCharacter : MonoBehaviour
    {
        [Header("子控制器（留空自动挂载）")]
        public Yixuan.Face.BlinkController Blink;
        public Yixuan.Motion.ProceduralIdleMotion Idle;
        public Yixuan.Face.YixuanExpressions Expressions;
        [Tooltip("外部动画播放器（Mixamo 等）。有对应动画时优先用它")]
        public Yixuan.Motion.YixuanAnimationPlayer AnimPlayer;

        [Tooltip("Mixamo 语义动作表（wave→Waving Gesture 等）。留空自动查找")]
        public Yixuan.Motion.MixamoActionManifest Manifest;

        [Header("骨骼")]
        public Transform HeadBone;      // 頭
        public Transform NeckBone;      // 首
        public Transform ShoulderR;     // 肩.R
        public Transform ShoulderL;     // 肩.L
        public Transform ArmR;          // 腕.R
        public Transform ArmL;          // 腕.L
        public Transform ElbowR;        // ひじ.R
        public Transform ElbowL;        // ひじ.L
        public Transform WristR;        // 手首.R
        public Transform WristL;        // 手首.L
        public Transform Center;        // センター

        [Header("自动播放")]
        public bool AutoPlay = true;
        [Tooltip("表情切换间隔（秒）")]
        public float ExpressionInterval = 3.5f;
        [Tooltip("手势播放间隔（秒）")]
        public float GestureInterval = 5.0f;

        [Header("自然站姿（解析解算参数）")]
        [Tooltip("手臂外展角度（度）。0 = 完全垂直贴腿，12 = 自然放松，25 = 手离身体较远")]
        [Range(0f, 45f)] public float IdleOutward = 12f;
        [Tooltip("肘部弯曲（度）。0 = 完全伸直（僵），14 = 自然放松")]
        [Range(0f, 60f)] public float IdleElbowBend = 14f;

        [Header("视线跟随")]
        public bool LookAtMouse = false;
        [Range(10f, 90f)] public float LookYawLimit = 35f;
        [Range(10f, 90f)] public float LookPitchLimit = 25f;
        public float LookSmoothing = 6f;

        [Header("调试")]
        public bool VerboseLog = true;

        // ---- 内部状态 ----
        float _exprTimer;
        float _gestureTimer;
        int _exprIndex;
        System.Random _rng;

        // 手势播放状态
        enum Gesture { None, Wave, Nod, Turn, Library }
        Gesture _gesture = Gesture.None;
        float _gestureT;
        float _gestureDuration;

        // 动作库（GestureLibrary）当前播放的动作
        Yixuan.Motion.GestureLibrary.GestureDef _libGesture;
        bool _useTPose;

        /// <summary>
        /// 锁定当前手势姿态（调试/截图用）。
        /// 置 true 后 UpdateGesture 不再推进时间轴，姿态会停在设定值。
        /// </summary>
        public bool LockGesturePose = false;

        /// <summary>
        /// 调试用：播放指定动作并冻结在指定进度。
        /// progress 0.5 = 中间（保持姿态），0.1 = 刚抬起。
        /// </summary>
        public void DebugFreezeGesture(string id, float progress)
        {
            var def = Yixuan.Motion.GestureLibrary.Get(id);
            if (def == null) return;
            _libGesture = def;
            _gestureDuration = def.Duration;
            _gesture = Gesture.Library;
            _gestureT = Mathf.Clamp(progress, 0.01f, 0.99f) * def.Duration;
            LockGesturePose = true;
            RestoreGesturePose();   // ★ 从自然站姿解算，避免叠加到上一个动作
            float p = _gestureT / _gestureDuration;
            float env = Mathf.Min(1f, p * 4f) * Mathf.Min(1f, (1f - p) * 4f);
            DoLibraryGesture(p, Mathf.SmoothStep(0f, 1f, env));
            LastAction = "冻结→" + def.Label + " @" + (progress * 100f).ToString("F0") + "%";
        }

        /// <summary>解除姿势锁定</summary>
        public void UnfreezeGesture()
        {
            LockGesturePose = false;
            _gesture = Gesture.None;
            _libGesture = null;
            _gestureT = 0f;
            // ★ 先清状态再重建自然站姿。
            //   反过来的话 ApplyIdleBasePose 会把"上一次动作的姿态"当成基准缓存进去，
            //   之后 RestoreGesturePose 就会把手臂恢复到那个错误姿态（实测手臂抬到天上）。
            ApplyIdleBasePose();
        }

        // 骨骼基准姿态
        Quaternion _armBase, _elbowBase, _shoulderBase, _centerBase;
        Quaternion _headBase, _neckBase, _wristBase;
        Quaternion _armLBase, _elbowLBase, _shoulderLBase, _wristLBase;
        Quaternion _upperBase, _spineBase;
        bool _rigReady;

        // 视线
        float _yaw, _pitch;

        public string LastAction { get; private set; } = "(启动)";

        void Awake()
        {
            _rng = new System.Random(20260920);
            AutoWire();
            ApplyIdleBasePose();   // ★ 必须在 CachePose 之前：把 T-pose 改成自然站姿
            CachePose();
        }

        /// <summary>
        /// 应用「自然站姿」到手臂骨骼。
        ///
        /// 【为什么必须在脚本里做，而不是只改 Prefab】
        /// 实测发现：只在 Prefab 里改骨骼旋转，Play 时会被重置回 T-pose。
        /// 原因：VRM 运行时会用内部缓存的初始姿态覆盖骨骼，
        /// 且在 YixuanCharacter.Awake 之前执行。
        ///
        /// 所以这里用 **PMX 原始值 + 绝对旋转**（幂等，不怕重复应用）
        /// 在 Awake 阶段重新设定，随后 CachePose 就会记录正确的基准。
        ///
        /// ============================================================
        ///  ★ 参数标定（第三版 —— 前两版都错在【基准姿态本身】）
        /// ============================================================
        /// 目标：双手自然下垂在身体两侧
        ///   右手 dX ≈ -0.445（身体右侧）, dY ≈ -0.246（下垂）, dZ ≈ 0（贴身）
        ///   左手 dX ≈ +0.446,              dY ≈ -0.382,              dZ ≈ +0.107
        ///
        /// 【前两版的错误】
        ///   第一版：只在 Prefab 改 → Play 被重置
        ///   第二版：写成 `origR * Euler(-75, 0, -15)`
        ///           但标定明确说 **X 正 = 外侧**，负值把手臂转向【内侧】
        ///           → 自然站姿下手就跑到身体左侧（dX 从 -0.21 变 +0.05）
        ///           → 所有基于这个基准的动作全部继承了这个错误
        ///           这就是"手横穿胸口""抱胸抱到背后"的总根源
        ///
        /// 【左右臂参数不同 —— 不假设镜像】
        ///   右臂：X 正 = 外侧抬起
        ///   左臂：X 负 = 外侧抬起（方向相反），且需要 Y 分量
        /// ============================================================
        /// </summary>
        public void ApplyIdleBasePose()
        {
            var aR = ArmR; var aL = ArmL;
            var eR = ElbowR; var eL = ElbowL;
            var wR = WristR; var wL = WristL;

            // ============================================================
            //  ★ 第七版 —— 放弃查表，改为【解析解算】
            // ============================================================
            //  【为什么前面六版都失败】
            //  第六版仍在用 ArmPoseDB 查表：localRotation = OrigR * Euler(148,0,0)。
            //  这要求"OrigR 就是模型真实 rest 值"。实测对不上：
            //
            //     骨骼            VRM 真实 rest            Prefab/场景里存的
            //     腕.R (右上臂)   271.4,   0, 268.6        57.9, 266.4, 357.4   ← 被污染
            //     腕.L (左上臂)    88.6, 180, 268.6         5.0,  91.5, 181.4   ← 被污染
            //     肩.R (右肩)     284.6,   0, 178.7         0.0,   0.0,   0.0   ← 被清零
            //     ひじ.R (肘)       0,   180, 356.5         0,   180, 341.5     ← 被改
            //
            //  基准是脏的，再叠偏移 → 误差累积 → 手臂折到胸前、穿进身体，
            //  而且左右不对称（左右两边被污染的幅度还不一样）。
            //
            //  【第七版做法 —— 与基准无关，所以不会累积误差】
            //   ① 先把整条手臂链复位到 VRM 原始 rest（T-pose），把脏基准洗掉
            //   ② 用 ArmPoseSolver 直接指定"手臂该指向哪"，反解出需要的旋转：
            //        Δ = FromToRotation(当前实际指向, 目标指向)
            //      因为它读的是【当前实际指向】再算差值，基准多脏都能一次到位。
            //
            //  自检：复位后右臂指向(-1,0,0)、左臂指向(+1,0,0)、肘角180° —— 干净的 T-pose ✅
            //        解算后右臂(-0.208,-0.978,0)、左臂(+0.208,-0.978,0) —— 精确镜像 ✅
            // ============================================================
            ResetArmToRest();

            // 自然下垂：外展 12°（手离开大腿一点，不夹），肘微屈 14°（放松不僵直）
            // ★ 用与动作库同一个解算路径（SolveArmPose lift=0），保证基准与动作一致。
            SolveArmPose(true, 0f, 0f, IdleElbowBend, IdleOutward);
            SolveArmPose(false, 0f, 0f, IdleElbowBend, IdleOutward);

            // ============================================================
            //  ★★ 基准骨骼自愈 —— 防止"角色倒立"事故重演
            // ============================================================
            // 事故经过：ProceduralIdleMotion 的 _centerBase 缓存到错误值后，
            // 把 センター 写成了 identity (0,0,0)；
            // YixuanCharacter.CachePose 又把这个坏值缓存、RestoreGesturePose 恢复它、
            // 最后存进场景 → 整个身体绕 X 翻转 180°，角色【倒立】。
            //
            // 修复：把这几根"躯体基准骨"强制复位到**绑定姿态**。
            // ★ 绑定姿态来自 CaptureRestPose() 的实测快照（AutoWire 里拍的），
            //   不再硬编码仪玄的欧拉角 —— 换模型自动适配。幂等，重复调用无害。
            RestoreBaseBones();

            _armBase = aR != null ? aR.localRotation : Quaternion.identity;
            _elbowBase = eR != null ? eR.localRotation : Quaternion.identity;
            _wristBase = wR != null ? wR.localRotation : Quaternion.identity;
            _armLBase = aL != null ? aL.localRotation : Quaternion.identity;
            _elbowLBase = eL != null ? eL.localRotation : Quaternion.identity;
            _wristLBase = wL != null ? wL.localRotation : Quaternion.identity;
            _shoulderBase = ShoulderR != null ? ShoulderR.localRotation : Quaternion.identity;
            _shoulderLBase = ShoulderL != null ? ShoulderL.localRotation : Quaternion.identity;
        }

        /// <summary>
        /// 把整条手臂链复位到 VRM 原始 rest（T-pose）。
        /// 用于洗掉 Prefab / 场景里被污染的骨骼值。
        /// </summary>
        public void ResetArmToRest()
        {
            Yixuan.Motion.ArmPoseSolver.ResetArmToRest(ShoulderR, ArmR, ElbowR, WristR, true);
            Yixuan.Motion.ArmPoseSolver.ResetArmToRest(ShoulderL, ArmL, ElbowL, WristL, false);
        }

        /// <summary>手臂姿态自检报告（调试用）</summary>
        public string ArmReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("===== 手臂姿态自检 =====");
            sb.AppendLine(Yixuan.Motion.ArmPoseSolver.Report(ShoulderR, ArmR, ElbowR, WristR, "右臂"));
            sb.AppendLine(Yixuan.Motion.ArmPoseSolver.Report(ShoulderL, ArmL, ElbowL, WristL, "左臂"));
            return sb.ToString();
        }

        /// <summary>
        /// 按语义名重新绑定所有骨骼引用（骨名走 MmdBoneNames 中央表）。
        ///
        /// ★ 换模型后必须调一次 —— 或者干脆不调：
        ///   AutoWire 里的 `if (xx == null)` 兜底会在运行时自动补上。
        ///   但为了让 Inspector 里看得见、也为了让编辑模式下的工具能用，
        ///   换装流程应该显式调一次。
        /// </summary>
        public void RebindBones()
        {
            HeadBone = Yixuan.Model.MmdBoneNames.Resolve("Head", transform);
            NeckBone = Yixuan.Model.MmdBoneNames.Resolve("Neck", transform);
            ShoulderR = Yixuan.Model.MmdBoneNames.Resolve("ShoulderR", transform);
            ShoulderL = Yixuan.Model.MmdBoneNames.Resolve("ShoulderL", transform);
            ArmR = Yixuan.Model.MmdBoneNames.Resolve("ArmR", transform);
            ArmL = Yixuan.Model.MmdBoneNames.Resolve("ArmL", transform);
            ElbowR = Yixuan.Model.MmdBoneNames.Resolve("ElbowR", transform);
            ElbowL = Yixuan.Model.MmdBoneNames.Resolve("ElbowL", transform);
            WristR = Yixuan.Model.MmdBoneNames.Resolve("WristR", transform);
            WristL = Yixuan.Model.MmdBoneNames.Resolve("WristL", transform);
            Center = Yixuan.Model.MmdBoneNames.Resolve("Center", transform);

            // 绑定姿态快照也要重拍（换模型后 rest 变了）
            _restPose = null;
            CaptureRestPose();

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        /// <summary>把某根躯体基准骨强制复位（幂等）</summary>
        void ForceBaseBone(string boneName, Vector3 euler)
        {
            var t = FindDeep(boneName);
            if (t != null) t.localRotation = Quaternion.Euler(euler);
        }

        /// <summary>
        /// 把所有「躯体基准骨」复位到实测绑定姿态。
        /// 语义名 → 骨头由 MmdBoneNames 解析，因此仪玄 / 庄方宜都能用。
        /// </summary>
        void RestoreBaseBones()
        {
            if (_restPose == null || _restPose.Count == 0) return;
            foreach (var kv in _restPose)
            {
                var t = Yixuan.Model.MmdBoneNames.Resolve(kv.Key, transform);
                if (t != null) t.localRotation = kv.Value;
            }
        }

        void Start()
        {
            // 统一到 AutoWire 做初始化（它内部会把 Shapes 注入给子控制器）
            AutoWire();

            if (AutoPlay)
            {
                ApplyExpression(0);
                LastAction = "初始化完成";
            }

            // ★ 立刻播待机动画，别停在"绑定姿态"上。
            //
            //   为什么要专门做这件事：
            //   本模型的**网格绑定姿态和骨架 rest pose 不一致** ——
            //   骨架 rest pose 是 A-pose + 高跟脚型，而网格是按 T-pose 绑的。
            //   结果：不播任何动画时（纯绑定姿态）脚会严重畸形
            //   （实测渲染出来像"管子末端外翻"，没有鞋跟和脚掌），
            //   而一旦播上 idle，脚就恢复正常的高跟鞋形状。
            //   所以启动时必须马上把待机动画跑起来。
            StartIdleAnimation();

            if (VerboseLog) Debug.Log(Status());
        }

        /// <summary>启动时播待机（优先用 Mixamo 的 idle，没有就退回程序化姿势）</summary>
        void StartIdleAnimation()
        {
            if (AnimPlayer != null && Manifest != null)
            {
                string clip = Manifest.Resolve("idle");
                if (!string.IsNullOrEmpty(clip) && AnimPlayer.HasClip(clip))
                {
                    SuppressProceduralForExternal();
                    AnimPlayer.Play(clip, true, 1f);
                    _usingExternalAnim = true;
                    _usingLoopAnim = true;
                    LastAction = "动画→" + clip;
                    if (VerboseLog) Debug.Log("[Yixuan] 启动待机 " + LastAction);
                    return;
                }
            }
            // 没有 Mixamo 待机 → 用程序化姿态（手臂由 ArmPoseSolver 解算）
            ApplyIdleBasePose();
            LastAction = "程序化待机";
        }

        void AutoWire()
        {
            // ---- 0) ★ 先拍下「绑定姿态」快照 ----
            //   必须放在最前面：此时动画还没跑起来，读到的就是模型导入时的 rest pose。
            //   之后的 RestoreBaseBones() 全部依赖这份快照。
            if (_restPose == null || _restPose.Count == 0) CaptureRestPose();

            // ---- 1) 子控制器：没有就自动挂上（解决 Prefab 丢失组件的问题）----
            if (Blink == null) Blink = GetComponent<Yixuan.Face.BlinkController>();
            if (Blink == null) Blink = gameObject.AddComponent<Yixuan.Face.BlinkController>();

            if (Expressions == null) Expressions = GetComponent<Yixuan.Face.YixuanExpressions>();
            if (Expressions == null) Expressions = gameObject.AddComponent<Yixuan.Face.YixuanExpressions>();

            if (Idle == null) Idle = GetComponent<Yixuan.Motion.ProceduralIdleMotion>();
            if (Idle == null) Idle = gameObject.AddComponent<Yixuan.Motion.ProceduralIdleMotion>();

            if (AnimPlayer == null) AnimPlayer = GetComponent<Yixuan.Motion.YixuanAnimationPlayer>();
            if (Manifest == null) Manifest = GetComponent<Yixuan.Motion.MixamoActionManifest>();
            // 一次性 Mixamo 动作播完 → 自动回待机
            if (AnimPlayer != null)
            {
                AnimPlayer.AnimationFinished -= OnAnimFinished;
                AnimPlayer.AnimationFinished += OnAnimFinished;
            }

            // ---- 2) 骨骼 ----
            // ★ 骨名走 MmdBoneNames 中央表（同时支持仪玄 腕.R 与庄方宜 左腕），
            //   不要在这里写死某一个模型的骨名。
            if (HeadBone == null) HeadBone = Yixuan.Model.MmdBoneNames.Resolve("Head", transform);
            if (NeckBone == null) NeckBone = Yixuan.Model.MmdBoneNames.Resolve("Neck", transform);
            if (ShoulderR == null) ShoulderR = Yixuan.Model.MmdBoneNames.Resolve("ShoulderR", transform);
            if (ShoulderL == null) ShoulderL = Yixuan.Model.MmdBoneNames.Resolve("ShoulderL", transform);
            if (ArmR == null) ArmR = Yixuan.Model.MmdBoneNames.Resolve("ArmR", transform);
            if (ArmL == null) ArmL = Yixuan.Model.MmdBoneNames.Resolve("ArmL", transform);
            if (ElbowR == null) ElbowR = Yixuan.Model.MmdBoneNames.Resolve("ElbowR", transform);
            if (ElbowL == null) ElbowL = Yixuan.Model.MmdBoneNames.Resolve("ElbowL", transform);
            if (WristR == null) WristR = Yixuan.Model.MmdBoneNames.Resolve("WristR", transform);
            if (WristL == null) WristL = Yixuan.Model.MmdBoneNames.Resolve("WristL", transform);
            if (Center == null) Center = Yixuan.Model.MmdBoneNames.Resolve("Center", transform);

            // ---- 2b) 手臂链优先用 Humanoid 映射（比名字可靠，且能避开捻转骨）----
            WireArmFromHumanoid();

            // ---- 3) 让子控制器自己初始化（内部会创建并解析 Shapes）----
            if (Blink != null) Blink.EnsureShapes();
            if (Expressions != null) Expressions.EnsureShapes();
            if (Idle != null) Idle.Relink();

            // ---- 4) 记录基准姿态 ----
            CachePose();
        }

        /// <summary>
        /// 从 Animator 的 Humanoid 映射取手臂骨骼。
        /// 比按名字找可靠 —— 名字是 MMD 日文命名（腕.R / ひじ.R / 手首.R），
        /// 而且 腕.R 的【第一个子骨骼是 腕捩.R 捻转骨，不是肘】，
        /// 靠 child0 推"下一个关节"会出错。
        /// </summary>
        void WireArmFromHumanoid()
        {
            var anim = GetComponentInChildren<Animator>();
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman) return;

            if (ShoulderR == null) ShoulderR = anim.GetBoneTransform(HumanBodyBones.RightShoulder);
            if (ShoulderL == null) ShoulderL = anim.GetBoneTransform(HumanBodyBones.LeftShoulder);
            if (ArmR == null) ArmR = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (ArmL == null) ArmL = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            if (ElbowR == null) ElbowR = anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
            if (ElbowL == null) ElbowL = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            if (WristR == null) WristR = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (WristL == null) WristL = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            if (Center == null) Center = anim.GetBoneTransform(HumanBodyBones.Hips);
        }

        Transform FindDeep(string name)
        {
            return FindDeepRec(transform, name);
        }

        static Transform FindDeepRec(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeepRec(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        void CachePose()
        {
            _armBase = ArmR != null ? ArmR.localRotation : Quaternion.identity;
            _elbowBase = ElbowR != null ? ElbowR.localRotation : Quaternion.identity;
            _wristBase = WristR != null ? WristR.localRotation : Quaternion.identity;
            _shoulderBase = ShoulderR != null ? ShoulderR.localRotation : Quaternion.identity;
            _centerBase = Center != null ? Center.localRotation : Quaternion.identity;
            _headBase = HeadBone != null ? HeadBone.localRotation : Quaternion.identity;
            _neckBase = NeckBone != null ? NeckBone.localRotation : Quaternion.identity;

            _armLBase = ArmL != null ? ArmL.localRotation : Quaternion.identity;
            _elbowLBase = ElbowL != null ? ElbowL.localRotation : Quaternion.identity;
            _wristLBase = WristL != null ? WristL.localRotation : Quaternion.identity;
            _shoulderLBase = ShoulderL != null ? ShoulderL.localRotation : Quaternion.identity;

            var upper = FindDeep("上半身");
            var sp = FindDeep("上半身2");
            _upperBase = upper != null ? upper.localRotation : Quaternion.identity;
            _spineBase = sp != null ? sp.localRotation : Quaternion.identity;

            _rigReady = (ArmR != null || HeadBone != null);
        }

        void Update()
        {
            if (!_rigReady) return;

            UpdateAutoPlay();
            UpdateGesture();
            UpdateLookAt();

            if (Input.GetKeyDown(KeyCode.Space))
            {
                if (Blink != null) Blink.TriggerBlink();
            }
        }

        void UpdateAutoPlay()
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1|8))return;
            if (!AutoPlay) return;

            // 表情循环
            _exprTimer += Time.deltaTime;
            if (_exprTimer >= ExpressionInterval)
            {
                _exprTimer = 0f;
                _exprIndex = (_exprIndex + 1) % 4;
                ApplyExpression(_exprIndex);
            }

            // 手势随机
            if (_gesture == Gesture.None)
            {
                _gestureTimer += Time.deltaTime;
                if (_gestureTimer >= GestureInterval)
                {
                    _gestureTimer = 0f;
                    StartRandomGesture();
                }
            }
        }

        void ApplyExpression(int idx)
        {
            if (Expressions == null) return;
            switch (idx)
            {
                case 1: Expressions.SetExpression(Yixuan.Face.YixuanExpressions.Expression.Smile); LastAction = "表情→微笑"; break;
                case 2: Expressions.SetExpression(Yixuan.Face.YixuanExpressions.Expression.Angry); LastAction = "表情→生气"; break;
                case 3: Expressions.SetExpression(Yixuan.Face.YixuanExpressions.Expression.Surprised); LastAction = "表情→惊讶"; break;
                default: Expressions.SetExpression(Yixuan.Face.YixuanExpressions.Expression.Neutral); LastAction = "表情→待机"; break;
            }
            if (VerboseLog) Debug.Log($"[Yixuan] {LastAction}");
        }

        void StartRandomGesture()
        {
            int pick = _rng.Next(0, 3);
            switch (pick)
            {
                case 0: _gesture = Gesture.Wave; _gestureDuration = 3.0f; LastAction = "手势→挥手"; break;
                case 1: _gesture = Gesture.Nod;  _gestureDuration = 2.0f; LastAction = "手势→点头"; break;
                default: _gesture = Gesture.Turn; _gestureDuration = 3.0f; LastAction = "手势→转身"; break;
            }
            _gestureT = 0f;
            if (VerboseLog) Debug.Log($"[Yixuan] {LastAction}");
        }

        void UpdateGesture()
        {
            if (_gesture == Gesture.None) return;

            // ★ 姿势锁定：调试/截图用。锁定后姿态停在当前帧，不再推进
            if (LockGesturePose) return;

            _gestureT += Time.deltaTime;
            float p = Mathf.Clamp01(_gestureT / _gestureDuration);

            // 起落包络：前 25% 抬起，后 25% 放下，避免瞬间切换
            float env = Mathf.Min(1f, p * 4f) * Mathf.Min(1f, (1f - p) * 4f);
            env = Mathf.SmoothStep(0f, 1f, env);

            switch (_gesture)
            {
                case Gesture.Wave: DoWave(p); break;
                case Gesture.Nod: DoNod(p); break;
                case Gesture.Turn: DoTurn(p); break;
                case Gesture.Library: DoLibraryGesture(p, env); break;
            }

            if (p >= 1f)
            {
                RestoreGesturePose();
                _gesture = Gesture.None;
                _libGesture = null;
                LastAction = "动作结束";
            }
        }

        /// <summary>
        /// 按 GestureLibrary 的定义施加姿态。
        ///
        /// 【第八版改动】手臂不再走"基准 × 欧拉增量"，改走解析解算器。
        /// 原因：那些增量是按旧 ArmPoseDB 的错误基准标定的，
        /// 在新基准上叠加会把手甩到身体侧面/脸前（"挥手"变成横伸）。
        /// 现在手臂用 lift(抬起度) + elbow(肘弯曲) 直接指定方向求解，
        /// 躯干/头/颈仍走原来的增量路径（那部分是好的）。
        /// </summary>
        void DoLibraryGesture(float p, float env)
        {
            if (_libGesture == null) return;
            float swingPhase = Mathf.Sin(p * Mathf.PI * 2f * _libGesture.SwingCycles);

            // ★ 每帧先回到自然站姿再解算。
            //   解算器读的是"当前实际指向"，如果不先复位就会在上一次的姿态上
            //   继续叠（挥手再挥手 → 手臂越抬越高、动作互相污染）。
            RestoreGesturePose();

            if (_libGesture.Hold != null)
            {
                for (int i = 0; i < _libGesture.Hold.Length; i++)
                    ApplyBoneDelta(_libGesture.Hold[i], env);
            }
            if (_libGesture.Swing != null)
            {
                for (int i = 0; i < _libGesture.Swing.Length; i++)
                    ApplyBoneDelta(_libGesture.Swing[i], env * swingPhase);
            }

            // 手臂：解析解算（不受增量标定误差影响）
            if (_libGesture.Arms != null)
            {
                for (int i = 0; i < _libGesture.Arms.Length; i++)
                {
                    var a = _libGesture.Arms[i];
                    float lift = a.Lift + (a.LiftSwing != 0f ? a.LiftSwing * swingPhase : 0f);
                    float fwd = a.Forward + (a.ForwardSwing != 0f ? a.ForwardSwing * swingPhase : 0f);
                    SolveArmPose(a.Right, lift * env, fwd * env, a.Elbow * env, a.Outward);
                }
            }
        }

        /// <summary>
        /// 角色上半身朝向（世界空间，水平）。
        ///
        /// ★ 必须用【胸骨自身的 forward】，不能用 髋→胸 的位置差：
        ///   "转身"动作是靠 センター 整体旋转实现的，髋和胸一起转，
        ///   位置差始终是同一个方向 —— 于是手臂被钉在世界空间里不跟着转身，
        ///   躯干转 40° 而手臂不转，肩膀就被拧了（实测右肘余量 -0.027）。
        ///   用 chest.forward 后，手臂随躯干一起转，全部转正。
        ///
        /// 注意：chest.forward 的世界方向约 -Z（与角色面朝方向相反），
        /// 所以调用方（SolveArmPose）拿到的是一个"沿身体纵轴的稳定向量"，
        /// 具体前后由叉积推导，不再依赖手写符号。
        /// </summary>
        Vector3 CharacterForwardWorld()
        {
            var anim = GetComponentInChildren<Animator>();
            if (anim != null && anim.avatar != null && anim.avatar.isHuman)
            {
                var chest = anim.GetBoneTransform(HumanBodyBones.Chest);
                if (chest != null)
                {
                    Vector3 f = chest.forward;
                    f.y = 0f;
                    if (f.sqrMagnitude > 1e-4f) return f.normalized;
                }
            }
            return transform.forward;
        }

        /// <summary>
        /// 解析解算一条手臂的姿态。
        ///
        /// ============================================================
        ///  【为什么全程用世界空间，而不是"父骨空间"】
        /// ============================================================
        /// 早先版本把所有方向都换算到 肩C 父骨空间再解算，结果自然站姿
        /// 外展 12° 却摆出 49.7°（差 30°）。原因：
        ///   TransformDirection 假设纯旋转，但 MMD 的 肩C/上半身2 局部轴
        ///   与世界轴并不正交（骨骼自带 roll/pitch），方向一进一出就被剪切，
        ///   down 轴被带歪，抬起角自然全错。
        ///
        /// 现在：所有方向、基向量都在【世界空间】算，直接把世界向量交给
        /// ArmPoseSolver（它内部本来就按世界空间推导 Δ = FromToRotation）。
        /// 只在最后一步由 Aim() 做一次 世界→局部 的四元数共轭变换，那是精确的。
        /// ============================================================
        /// </summary>
        /// <param name="isRight">true = 右臂</param>
        /// <param name="lift">抬起度：0 = 自然下垂(外展 IdleOutward)，90 = 侧平举，180 = 举过头顶</param>
        /// <param name="forward">前摆：正 = 手臂往身前摆</param>
        /// <param name="elbowBend">肘弯曲（0 = 伸直）</param>
        /// <param name="outwardOverride">外展覆盖（&lt;0 表示用 IdleOutward）</param>
        public void SolveArmPose(bool isRight, float lift, float forward, float elbowBend, float outwardOverride = -1f)
        {
            var upper = isRight ? ArmR : ArmL;
            var lower = isRight ? ElbowR : ElbowL;
            var hand = isRight ? WristR : WristL;
            if (upper == null) return;

            float outward = outwardOverride >= 0f ? outwardOverride : IdleOutward;

            // ---- 世界空间水平基：fwd(角色朝向) / out(该臂外侧) / up ----
            Vector3 worldFwd = CharacterForwardWorld();
            worldFwd.y = 0f;
            if (worldFwd.sqrMagnitude < 1e-6f) worldFwd = transform.forward;
            worldFwd.y = 0f;
            if (worldFwd.sqrMagnitude < 1e-6f) worldFwd = Vector3.back;
            worldFwd.Normalize();

            // 角色右手方向 = up × fwd（右手系）
            Vector3 charRight = Vector3.Cross(Vector3.up, worldFwd).normalized;
            // 本臂的外侧：右臂 = 角色右，左臂 = 角色左
            Vector3 myOut = isRight ? charRight : -charRight;

            // ---- 上臂方向（世界空间）：从垂直向下朝外侧/身前旋转 ----
            float fromVertical = Mathf.Lerp(outward, 180f - outward, Mathf.Clamp01(lift / 180f));
            float rad = fromVertical * Mathf.Deg2Rad;
            float fwdRad = forward * Mathf.Deg2Rad;

            Vector3 armDown = (Vector3.down * Mathf.Cos(rad) + myOut * Mathf.Sin(rad)).normalized;
            Vector3 dir = (armDown * Mathf.Cos(fwdRad) + worldFwd * Mathf.Sin(fwdRad)).normalized;

            // ---- 肘弯曲轴（世界空间）：随抬起度从"身后折"过渡到"肘向外张" ----
            //   t=0 下垂 → 绕外侧轴折 → 小臂往身后折（抱胸/叉腰自然）
            //   t=1 高举 → 绕身前轴折 → 肘向外张（伸懒腰自然，不越中线）
            float t = Mathf.Clamp01(lift / 180f);
            Vector3 bendAxis = Vector3.Slerp(myOut, worldFwd, t);

            // 直接用世界空间向量解算（避免父骨空间的剪切误差）
            Yixuan.Motion.ArmPoseSolver.SolveArm(upper, lower, hand, dir, bendAxis,
                                                 isRight ? elbowBend : -elbowBend);
        }

        /// <summary>把某个骨骼按增量旋转（相对其基准姿态）</summary>
        void ApplyBoneDelta(Yixuan.Motion.GestureLibrary.BoneKey k, float scale)
        {
            if (k == null || string.IsNullOrEmpty(k.Bone)) return;
            // 手臂骨不走增量路径 —— 它们由 ArmPoseSolver 解析解算，
            // 否则两套写法会互相打架（且旧增量是按错误基准标定的）
            if (Yixuan.Motion.GestureLibrary.IsArmBone(k.Bone)) return;
            var t = FindDeep(k.Bone);
            if (t == null) return;

            var basis = GetBaseOf(k.Bone);
            t.localRotation = basis * Quaternion.Euler(k.X * scale, k.Y * scale, k.Z * scale);
        }

        /// <summary>
        /// 取某骨骼的基准姿态（用于叠加增量）。
        ///
        /// ★ 不再按骨名硬编码分支 —— 而是「先查缓存的绑定姿态，再按语义名解析」。
        ///   原来是一串 `case "腕.R": return _armBase;`（仪玄的命名），
        ///   换成庄方宜（`左腕`）后**一个 case 都命中不了**，
        ///   全都掉到最后的 `FindDeep(bone)` 返回 null → 基准变 identity → 姿态错乱。
        /// </summary>
        Quaternion GetBaseOf(string bone)
        {
            Quaternion q;
            if (_restPose != null && _restPose.TryGetValue(bone, out q)) return q;

            var t = FindDeep(bone);
            return t != null ? t.localRotation : Quaternion.identity;
        }

        /// <summary>
        /// 记录「躯体基准骨」的绑定姿态，供 <see cref="ForceBaseBone"/> 自愈使用。
        ///
        /// ★ 为什么必须缓存而不是写死常量：
        ///   原来的实现把仪玄的 rest 欧拉角硬编码在这里（センター 180/181.2、上半身 324.2 …），
        ///   那是**仪玄那一版导入时**的数值。换个模型导入器，rest 就可能不同
        ///   （例如 ×0.078 与 ×0.08 缩放，会差 0.1~0.5°），
        ///   一旦对不上，这套「自愈」就会把角色掰成畸形，而且是**每帧**在掰。
        ///
        ///   现在改成：在 AutoWire 里（动画还没跑起来之前）把真实 rest 拍下来，
        ///   之后 ForceBaseBone 用这份快照自愈 —— 换任何模型都自动正确。
        /// </summary>
        void CaptureRestPose()
        {
            _restPose = new Dictionary<string, Quaternion>();
            string[] semantics = { "Center", "Groove", "Waist", "LowerBody",
                                   "UpperBody", "UpperBody1", "UpperBody2", "Neck", "Head" };
            foreach (var s in semantics)
            {
                var t = Yixuan.Model.MmdBoneNames.Resolve(s, transform);
                if (t != null) _restPose[s] = t.localRotation;
            }
        }

        Dictionary<string, Quaternion> _restPose;

        /// <summary>
        /// 挥手：右臂抬起 + 小臂摆动。
        ///
        /// 【轴向标定 —— Unity 里实测得出，与 Blender 侧不同！】
        /// 用逐轴旋转 + 读手首世界坐标的方式测出 腕.R 的语义：
        ///   X +60°  -> 手 dY = +0.398   ✅ 抬起手臂
        ///   X -60°  -> 手 dY = -0.388     放下手臂
        ///   Z -60°  -> 手 dZ = +0.393   ✅ 往前摆
        ///   Z +60°  -> 手 dZ = -0.393     往后退
        ///   Y       -> 扭转（无效）
        ///
        /// 【踩坑】最初按 Blender 侧的标定写了 X:-95 / Z:-15，
        /// 结果手臂被"放下"并转向背后 —— 两侧坐标系不同，必须各自标定。
        /// </summary>
        void DoWave(float p)
        {
            float swing = Mathf.Sin(p * Mathf.PI * 2f * 3f);          // 3 个来回
            float raise = Mathf.SmoothStep(0f, 1f, Mathf.Min(p * 4f, 1f)) *
                          Mathf.SmoothStep(0f, 1f, Mathf.Min((1f - p) * 4f, 1f));

            if (ShoulderR != null)
                ShoulderR.localRotation = _shoulderBase * Quaternion.Euler(0f, 0f, 8f * raise);
            if (ArmR != null)
                // X 正 = 抬臂；Z 负 = 往前摆（稍微前伸，避免看着像背后）
                ArmR.localRotation = _armBase * Quaternion.Euler(78f * raise, 0f, -18f * raise);
            if (ElbowR != null)
                // 肘部小幅摆动增加挥手感
                ElbowR.localRotation = _elbowBase * Quaternion.Euler(0f, 0f, 14f * swing * raise);
            if (WristR != null)
                WristR.localRotation = _wristBase * Quaternion.Euler(0f, 0f, 18f * swing * raise);
            if (HeadBone != null)
                HeadBone.localRotation = _headBase * Quaternion.Euler(0f, 0f, -6f * raise);
        }

        /// <summary>点头：头部上下摆动</summary>
        void DoNod(float p)
        {
            float n = Mathf.Sin(p * Mathf.PI * 2f * 2f);              // 2 个来回
            float env = Mathf.Sin(p * Mathf.PI);                       // 包络
            if (HeadBone != null)
                HeadBone.localRotation = _headBase * Quaternion.Euler(14f * env - 11f * n * env, 0f, 0f);
            if (NeckBone != null)
                NeckBone.localRotation = _neckBase * Quaternion.Euler(6f * env - 4f * n * env, 0f, 0f);
        }

        /// <summary>
        /// 转身 / 环顾。
        ///
        /// 【轴向标定 —— Unity 里实测】
        /// センター: Z +30° -> 头 dX = +0.315  ✅ 真正的左右转身
        ///            Y +30° -> 头 dX = +0.021  ⚠️ 几乎无效（原代码用错了这个）
        /// 頭:       Z = 偏航（转头），X = 俯仰（点头）
        /// </summary>
        void DoTurn(float p)
        {
            float t = Mathf.Sin(p * Mathf.PI);                         // 0→1→0 一次往返
            if (Center != null)
                Center.localRotation = _centerBase * Quaternion.Euler(0f, 0f, 40f * t);
            if (HeadBone != null)
                HeadBone.localRotation = _headBase * Quaternion.Euler(0f, 0f, 25f * t);
            if (NeckBone != null)
                NeckBone.localRotation = _neckBase * Quaternion.Euler(0f, 0f, 10f * t);
        }

        void RestoreGesturePose()
        {
            if (ShoulderR != null) ShoulderR.localRotation = _shoulderBase;
            if (ArmR != null) ArmR.localRotation = _armBase;
            if (ElbowR != null) ElbowR.localRotation = _elbowBase;
            if (ShoulderL != null) ShoulderL.localRotation = _shoulderLBase;
            if (ArmL != null) ArmL.localRotation = _armLBase;
            if (ElbowL != null) ElbowL.localRotation = _elbowLBase;
            if (WristR != null) WristR.localRotation = _wristBase;
            if (WristL != null) WristL.localRotation = _wristLBase;
            if (Center != null) Center.localRotation = _centerBase;
            if (HeadBone != null) HeadBone.localRotation = _headBase;
            if (NeckBone != null) NeckBone.localRotation = _neckBase;
        }

        /// <summary>视线跟随鼠标（柔和，带角度限制）</summary>
        void UpdateLookAt()
        {
            if (!LookAtMouse || HeadBone == null) return;

            var cam = Camera.main;
            if (cam == null) return;

            Vector3 mp = Input.mousePosition;
            mp.z = Mathf.Abs(cam.transform.position.z - HeadBone.position.z);
            Vector3 world = cam.ScreenToWorldPoint(mp);
            Vector3 dir = world - HeadBone.position;

            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(dir.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;

            yaw = Mathf.Clamp(yaw, -LookYawLimit, LookYawLimit);
            pitch = Mathf.Clamp(pitch, -LookPitchLimit, LookPitchLimit);

            _yaw = Mathf.Lerp(_yaw, yaw, Time.deltaTime * LookSmoothing);
            _pitch = Mathf.Lerp(_pitch, pitch, Time.deltaTime * LookSmoothing);

            // 只改基准姿态的增量，不影响 IdleMotion（简单起见以基准为准）
            HeadBone.localRotation = _headBase * Quaternion.Euler(_pitch, _yaw, 0f);
        }

        // ---------------- 外部接口 ----------------

        /// <summary>设置表情（按 id，供 UI / AI 调用）。走 ExpressionLibrary，支持 15 种</summary>
        public void SetExpression(string id)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(8))return;
            if (Expressions == null) return;
            Expressions.ApplyById(id ?? "neutral");
            LastAction = "表情→" + id;
            if (VerboseLog) Debug.Log("[Yixuan] " + LastAction);
        }

        /// <summary>
        /// 播放一个动作。
        ///
        /// 【查找顺序】
        ///   ① 语义 id 经 MixamoActionManifest 翻译成 clip 名
        ///       例： "wave" → "Waving Gesture"
        ///   ② 直接按 clip 名找（用户可能传的就是 Mixamo 原始名）
        ///   ③ 都没有 → 退回程序化姿态（GestureLibrary）
        ///
        /// ★ 为什么非要 ① 这一步
        ///   控制面板按的是语义 id（wave/nod/clap…），
        ///   而 Mixamo 的 clip 叫 "Waving Gesture" / "Lengthy Head Nod" / "Standing Clap"。
        ///   不做翻译的话 ①②都落空 → 全部退回程序化姿态，
        ///   而程序化那套的手臂是歪的 —— 这正是"点了按钮肢体还是错"的原因。
        /// </summary>
        public void PlayGesture(string id)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1))return;
            if (string.IsNullOrEmpty(id)) return;

            // ① 语义 → clip 名
            string clipName = null;
            if (Manifest != null)
            {
                clipName = Manifest.Resolve(id);
            }
            // ② 直接当 clip 名试
            if (string.IsNullOrEmpty(clipName) && AnimPlayer != null && AnimPlayer.HasClip(id))
            {
                clipName = id;
            }

            if (!string.IsNullOrEmpty(clipName) && AnimPlayer != null && AnimPlayer.HasClip(clipName))
            {
                // ★ 播外部动画前，把会抢骨骼的程序化待机让开
                //   否则两套同时写骨骼，动画会被部分覆盖
                SuppressProceduralForExternal();

                // 循环动作 vs 一次性动作：
                //   待机/走路/慢跑/坐姿 → 循环（是持续状态）
                //   挥手/点头/鼓掌…    → 播一次，结束后自动回待机
                bool loop = IsLoopingAction(id);
                if (!AnimPlayer.Play(clipName, loop, 1f)) return;
                _externalAnimOwner = Yixuan.AI.Tasks.TaskAuthority.Caller;
                _externalClipName = clipName;
                _usingExternalAnim = true;
                _usingLoopAnim = loop;
                LastAction = "动画→" + clipName;
                if (VerboseLog) Debug.Log("[Yixuan] " + LastAction + "（Mixamo 动画" + (loop ? "，循环" : "，单次") + "）");
                return;
            }

            // ③ 退回程序化（手臂由 ArmPoseSolver 解算，但不如 Mixamo 数据自然）
            var def = Yixuan.Motion.GestureLibrary.Get(id);
            if (def == null)
            {
                Debug.LogWarning("[Yixuan] 未知动作 id: " + id +
                                 "（语义表里没有，也不是 clip 名）");
                return;
            }

            ReturnFromExternalAnim();
            _libGesture = def;
            _gestureT = 0f;
            _gestureDuration = def.Duration;
            _gesture = Gesture.Library;
            LastAction = "动作→" + def.Label;
            if (VerboseLog) Debug.Log("[Yixuan] " + LastAction + "（程序化）");
        }

        /// <summary>停止外部动画，回到程序化待机</summary>
        public void StopGesture()
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1))return;
            ReturnFromExternalAnim();
            LastAction = "动作停止";
        }

        string _externalAnimOwner, _externalClipName;
        bool _usingExternalAnim;
        bool _usingLoopAnim;

        /// <summary>这个语义是不是"持续状态"（应该循环播放）</summary>
        static bool IsLoopingAction(string id)
        {
            switch (id)
            {
                case "idle":
                case "idle_breath":
                case "idle_shift":
                case "walk":
                case "jog":
                case "sit":
                case "sit_point":
                case "kneel":
                case "look_around":
                case "think":
                    return true;      // 持续状态
                default:
                    return false;     // 挥手/点头/鼓掌/摇头 → 播一次
            }
        }

        /// <summary>一次性动作播完 → 回到待机</summary>
        void OnAnimFinished(string clipName)
        {
            if (!_usingExternalAnim || clipName != _externalClipName) return;
            // Continue only the authority captured when this animation actually started.
            // A stale completion must never borrow a newer mission's body lease.
            if (!Yixuan.AI.Tasks.TaskAuthority.CanContinue(_externalAnimOwner, 1)) return;
            using (Yixuan.AI.Tasks.TaskAuthority.Enter(_externalAnimOwner))
            {
                LastAction = "动画结束→回待机";
                if (VerboseLog) Debug.Log("[Yixuan] " + clipName + " 播完，回待机");
                // Switch directly to a manifest idle; do not expose procedural/rest pose.
                if (Manifest != null && AnimPlayer != null)
                    foreach (var idle in Yixuan.AI.Tasks.TaskGestureSkills.Available())
                        if (idle.Loop && idle.Posture == "stand" && (idle.Label ?? "").Contains("待机") && AnimPlayer.HasClip(idle.ClipName))
                        { PlayGesture(idle.Id); return; }
                ReturnFromExternalAnim();
            }
        }

        /// <summary>播外部动画期间，停掉程序化待机和手势叠加，避免抢骨骼</summary>
        void SuppressProceduralForExternal()
        {
            if (Idle != null) Idle.Enabled = false;
            LockGesturePose = true;      // 让 UpdateGesture 不再推进/覆盖
            _gesture = Gesture.None;
            _libGesture = null;
        }

        /// <summary>恢复程序化待机</summary>
        void ReturnFromExternalAnim()
        {
            if (!_usingExternalAnim) return;
            _usingExternalAnim = false;
            _usingLoopAnim = false;
            LockGesturePose = false;
            if (Idle != null) Idle.Enabled = true;
            if (AnimPlayer != null && AnimPlayer.IsPlaying) AnimPlayer.Stop();
            ApplyIdleBasePose();          // 回到自然站姿基准
        }

        /// <summary>切换基准站姿</summary>
        public void SetBasePose(string pose)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1))return;
            bool wantT = (pose == "tpose" || pose == "t-pose" || pose == "T");
            _useTPose = wantT;

            if (wantT)
            {
                // 恢复 T-pose：用 VRM 原始 rest 值（唯一权威来源）
                ResetArmToRest();
            }
            else
            {
                ApplyIdleBasePose();
                CachePose();
            }
            LastAction = "站姿→" + (wantT ? "T-Pose" : "自然站立");
            if (VerboseLog) Debug.Log("[Yixuan] " + LastAction);
        }

        /// <summary>开关自动播放</summary>
        public void SetAutoPlay(bool on)
        {
            AutoPlay = on;
            if (on) { _exprTimer = 0f; _gestureTimer = 0f; }
            LastAction = "自动播放" + (on ? "开" : "关");
            if (VerboseLog) Debug.Log("[Yixuan] " + LastAction);
        }

        [ContextMenu("触发挥手")]
        public void TriggerWave() { if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1))return; _gesture = Gesture.Wave; _gestureDuration = 3f; _gestureT = 0f; LastAction = "手动→挥手"; }

        [ContextMenu("触发点头")]
        public void TriggerNod() { if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1))return; _gesture = Gesture.Nod; _gestureDuration = 2f; _gestureT = 0f; LastAction = "手动→点头"; }

        [ContextMenu("触发转身")]
        public void TriggerTurn() { if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1))return; _gesture = Gesture.Turn; _gestureDuration = 3f; _gestureT = 0f; LastAction = "手动→转身"; }

        /// <summary>验证用摘要</summary>
        public string Status()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== YixuanCharacter 状态 ==========");
            sb.AppendLine($"AutoPlay={AutoPlay} 表情间隔={ExpressionInterval}s 手势间隔={GestureInterval}s");
            sb.AppendLine($"当前手势={_gesture}  上次动作={LastAction}");
            sb.AppendLine($"骨骼: head={(HeadBone != null ? HeadBone.name : "❌")} neck={(NeckBone != null ? NeckBone.name : "❌")}");
            sb.AppendLine($"      shoulderR={(ShoulderR != null ? ShoulderR.name : "❌")} armR={(ArmR != null ? ArmR.name : "❌")} elbowR={(ElbowR != null ? ElbowR.name : "❌")} wristR={(WristR != null ? WristR.name : "❌")}");
            sb.AppendLine($"      center={(Center != null ? Center.name : "❌")}  rigReady={_rigReady}");
            if (Blink != null) sb.AppendLine("  " + Blink.Status());
            if (Idle != null) sb.AppendLine("  " + Idle.Status().Replace("\n", "\n  "));
            if (Expressions != null) sb.AppendLine("  " + Expressions.Status().Replace("\n", "\n  "));
            return sb.ToString();
        }
    }
}
