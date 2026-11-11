using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.AI
{
    /// <summary>
    /// 表演编排层（T2 最小版）—— 把 LLM 产出的 <see cref="BehaviorPlan"/> 翻译成角色动作。
    ///
    /// 职责（design.md 第 3 节“自动化感”的雏形）：
    ///   ① 表情：intensity 写进 YixuanExpressions.IntensityScale，再 SetExpression(emotion)
    ///   ② 动作：LLM 指定了 gesture → 校验 Catalog 后播放（带冷却去重）
    ///   ③ 动作没指定 → 按情绪默认表 + EmotionGestureChance 概率抽一个（不是每句都动）
    ///   ④ 姿态：posture 变了且 Catalog 有同名动作才播（完整姿态图留给 T4）
    ///   ⑤ 同一动作 GestureCooldownSec 内不重复播（B5 判据）
    ///   ⑥ 说话期间挂起 YixuanBrain 的主动行为，说完 N 秒后恢复（协程，不空转）
    ///
    /// 【铁律】动作 id 一律经 ActionCatalog 校验，绝不把未知 id 丢给 PlayGesture
    /// —— PlayGesture 找不到会静默回退到程序化姿态（手臂是歪的），很难排查。
    ///
    /// 挂载方式：Lead 在场景里挂到一个常驻物体（UI_Chat 或角色根节点均可），
    /// 三个引用留空会自动 FindObjectOfType 兜底。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(900)]   // LateUpdate 在 ProceduralIdleMotion / BlinkController 之后叠加头部神态
    public class Director : MonoBehaviour
    {
        [Header("引用（留空自动查找）")]
        public Yixuan.Core.YixuanCharacter Character;
        public Yixuan.Brain.YixuanBrain Brain;
        public AIConfig Config;
        [Tooltip("相机云台（camera 字段用）；留空自动查找")]
        public Yixuan.Vision.EyeGimbalController Gaze;
        [Tooltip("角色走路控制器（move 字段用）；留空自动查找")]
        public Yixuan.Motion.CharacterLocomotion Locomotion;
        public ChatSession Session;

        /// <summary>本轮由本地关键词先转了云台时，ChatSession 置 true：忽略 LLM 的 camera 字段，避免一架云台两处打架。</summary>
        [System.NonSerialized] public bool SuppressCameraForTurn;

        [Header("姿态（简单姿态图，完整过渡见 T4）")]
        [Tooltip("当前姿态 id：stand / sit / kneel。初始按站立处理")]
        public string CurrentPosture = "stand";

        // gestureId → 上次真正播放的时间（Time.time）
        readonly Dictionary<string, float> _lastPlayedAt = new Dictionary<string, float>();
        Coroutine _resumeCo;

        /// <summary>最近一次真正播放的动作 id（调试/验收用）</summary>
        public string LastGesture { get; private set; } = "(无)";

        /// <summary>
        /// 最近一次 Execute 是否安排了动作（显式 / 情绪补 / 姿态都算）。
        /// ChatSession 用它决定说完话后要不要回 idle —— 有动作时让动作自己播完，不打断。
        /// </summary>
        public bool LastPlanHadGesture { get; private set; }

        void Awake()
        {
            ResolveRefs();
            EnsureCatalog();
        }

        void Start()
        {
            ResolveRefs();
        }

        void ResolveRefs()
        {
            if (Character == null) Character = FindObjectOfType<Yixuan.Core.YixuanCharacter>();
            if (Brain == null) Brain = FindObjectOfType<Yixuan.Brain.YixuanBrain>();
            if (Config == null) Config = AIConfig.FindDefault();
            if (Gaze == null) Gaze = FindObjectOfType<Yixuan.Vision.EyeGimbalController>();
            if (Locomotion == null) Locomotion = FindObjectOfType<Yixuan.Motion.CharacterLocomotion>();
            if (Session == null) Session = FindObjectOfType<ChatSession>();
        }

        static void EnsureCatalog()
        {
            // ActionCatalog 自己会惰性初始化；这里只是提前预热，失败不抛异常
            if (!ActionCatalog.Ready) ActionCatalog.Refresh();
        }

        // ============================================================
        //  外部接口
        // ============================================================

        /// <summary>
        /// 执行一个完整的行为计划：表情 → 动作 → （概率补动作）→ 姿态 → 运动 → 观察。
        /// allowBodyAssist 用于流式前缀：未通过权威校验前禁止 camera 触发身体转向（F1）。
        /// </summary>
        public void Execute(BehaviorPlan plan, bool includeMotion = true, bool allowBodyAssist = true)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1|2|8))return;
            if (plan == null)
            {
                Debug.LogWarning("[Director] Execute: plan 为 null，忽略");
                return;
            }

            ResolveRefs();
            EnsureCatalog();

            if (Character == null)
            {
                Debug.LogWarning("[Director] 找不到 YixuanCharacter，无法表演");
                return;
            }

            LastPlanHadGesture = false;

            ApplyDemeanor(plan);
            ApplyExpression(plan);
            if (includeMotion) ApplyMove(plan);
            ApplyGesture(plan);
            ApplyPosture(plan);
            ApplyCamera(plan, allowBodyAssist);
            // 本地关键词已转过云台时，抑制标记必须保留到本轮最终观察提交点；
            // 流式前缀（includeMotion=false）不能提前清掉。
            if (includeMotion) SuppressCameraForTurn = false;

            var report = BehaviorReport.Build(plan, LastPlanHadGesture ? LastGesture : null, _gestureNote, CurrentDemeanor);
            LastReport = report;
            if (report.HasMissing) report.AppendToMissingLog();
            if (OnReport != null) OnReport(report);
        }

        /// <summary>
        /// F1：流式前缀只做无身体副作用的表演。
        /// camera 仍可由云台执行（保证首帧有反应），但绝不启动身体配合转向；
        /// 完整 JSON 权威校验通过后由 <see cref="ExecuteFinal"/> 兑现一次观察任务。
        /// </summary>
        public void ExecuteStreaming(BehaviorPlan plan)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1|2|8))return;
            Execute(plan, false, false);
        }

        /// <summary>只执行位移（旧接口，保留给不需要最终观察的调用方）。</summary>
        public void ExecuteMotion(BehaviorPlan plan)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1|2|8))return;
            if (plan == null) return;
            ResolveRefs();
            ApplyMove(plan);
        }

        /// <summary>
        /// R4/F1：完整计划通过权威校验后的唯一运动/观察提交点。
        /// 位移和 camera 只在这里提交一次；camera 允许身体配合，
        /// 因此“回头看”不会只剩 60° 限幅，也不会在流式前缀里提前转身。
        /// </summary>
        public void ExecuteFinal(BehaviorPlan plan, bool allowBodyAssist = true)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1|2|8))return;
            if (plan == null) return;
            ResolveRefs();
            ApplyMove(plan);
            ApplyCamera(plan, allowBodyAssist);
            SuppressCameraForTurn = false;
        }

        /// <summary>用户打断/取消时，停掉正在执行的“转身→相对移动”序列。</summary>
        public void CancelMotion()
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1|2|8))return;
            if (_motionCo != null)
            {
                StopCoroutine(_motionCo);
                _motionCo = null;
            }
            if (Locomotion != null) Locomotion.StopTurning();
        }

        Coroutine _motionCo;

        // ============================================================
        //  ④ 走路（LLM 的 move 字段 → CharacterLocomotion）
        // ============================================================

        void ApplyMove(BehaviorPlan plan)
        {
            if (plan == null) return;
            ResolveRefs();
            if (Locomotion == null)
            {
                if (!string.IsNullOrEmpty(plan.move) || Mathf.Abs(plan.body_turn) > 0.0001f ||
                    Mathf.Abs(plan.move_forward_m) > 0.0001f || Mathf.Abs(plan.move_right_m) > 0.0001f)
                    Debug.LogWarning("[Director] 场景里没有 CharacterLocomotion，运动字段被忽略");
                return;
            }

            // 身体转身 / 相对移动：按顺序执行（先转身完成，再按新的身体 forward 计算相对目标）。
            if (Mathf.Abs(plan.body_turn) > 0.0001f ||
                Mathf.Abs(plan.move_forward_m) > 0.0001f ||
                Mathf.Abs(plan.move_right_m) > 0.0001f)
            {
                if (_motionCo != null) StopCoroutine(_motionCo);
                _motionCo = StartCoroutine(CoBodyMotion(plan));
                return;
            }

            if (string.IsNullOrEmpty(plan.move)) return;
            if (plan.move == "stop")
            {
                Locomotion.Stop("LLM 命令停下");
                Debug.Log("[Director] 走路 → 停下");
                return;
            }
            if (plan.move != "walk_to") return;   // 解析器已过滤，这里兜底

            bool ok;
            if (!string.IsNullOrEmpty(plan.move_target))
                ok = Locomotion.MoveToAnchor(plan.move_target);
            else
                ok = Locomotion.MoveTo(new Vector3(plan.move_x, 0f, plan.move_z));

            if (ok)
                Debug.Log("[Director] 走路 → " +
                          (string.IsNullOrEmpty(plan.move_target)
                              ? "坐标(" + plan.move_x.ToString("F1") + "," + plan.move_z.ToString("F1") + ")"
                              : plan.move_target));
            else
                Debug.LogWarning("[Director] 走路失败：" + Locomotion.LastError);
            if (ok && !string.IsNullOrEmpty(plan.move_target) && Session != null)
                Session.TryAdoptChatNavigation(plan.move_target);
        }

        IEnumerator CoBodyMotion(BehaviorPlan plan)
        {
            if (Locomotion == null) { _motionCo = null; yield break; }

            if (Mathf.Abs(plan.body_turn) > 0.0001f)
            {
                if (!Locomotion.TurnRelative(plan.body_turn))
                {
                    Debug.LogWarning("[Director] 身体转身失败（角度非法）");
                    _motionCo = null;
                    yield break;
                }
                float deadline = Time.realtimeSinceStartup + 4f;
                while (Locomotion.IsTurning && Time.realtimeSinceStartup < deadline)
                    yield return null;
                if (Locomotion.IsTurning)
                {
                    Locomotion.StopTurning();
                    Debug.LogWarning("[Director] 身体转身超时");
                    _motionCo = null;
                    yield break;
                }
            }

            bool hasRelative = Mathf.Abs(plan.move_forward_m) > 0.0001f ||
                               Mathf.Abs(plan.move_right_m) > 0.0001f;
            if (hasRelative)
            {
                // ★ 必须在转身完成之后读取新的身体 forward，不能提前换算成世界坐标。
                Vector3 forward = Locomotion.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
                forward.Normalize();
                Vector3 right = Locomotion.transform.right;
                right.y = 0f;
                if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
                right.Normalize();

                Vector3 target = Locomotion.transform.position +
                                 forward * plan.move_forward_m + right * plan.move_right_m;
                target.y = 0f;
                if (!Locomotion.MoveTo(target))
                {
                    Debug.LogWarning("[Director] 相对移动失败：" + Locomotion.LastError);
                    _motionCo = null;
                    yield break;
                }
                float deadline = Time.realtimeSinceStartup + 25f;
                while (Locomotion.IsMoving && Time.realtimeSinceStartup < deadline)
                    yield return null;
                if (Locomotion.IsMoving) Locomotion.Stop("相对移动超时", "stalled");
                Debug.Log("[Director] 相对移动结束：" + Locomotion.LastStatus);
            }

            _motionCo = null;
        }

        // ============================================================
        //  ④ 相机云台（LLM 的 camera 字段 → EyeGimbalController）
        // ============================================================

        /// <summary>
        /// 让 LLM 像控制表情/动作那样控制虚拟云台。
        /// 只接受高层语义（不给坐标）；本地关键词层已经在 ChatSession 里先执行，
        /// 若本轮本地已转过（SuppressCameraForTurn），这里就跳过，避免同一架云台两处打架。
        /// </summary>
        public bool ExecuteObservationCamera(BehaviorPlan plan, bool allowBodyAssist = true)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(3))return false;
            ResolveRefs();
            if (Gaze == null || plan == null || !CameraMoveCatalog.Has(plan.camera)) return false;
            SuppressCameraForTurn = false;
            ApplyCamera(plan, allowBodyAssist);
            SuppressCameraForTurn = true;
            return true;
        }

        void ApplyCamera(BehaviorPlan plan, bool allowBodyAssist)
        {
            if (plan == null || string.IsNullOrEmpty(plan.camera)) return;
            // Explicit target attention survives ordinary replies/proactive camera suggestions.
            // A new local gaze command or movement releases the task lock first.
            var attention = FindObjectOfType<Yixuan.AI.Tasks.TaskMissionRunner>();
            if (attention != null && Gaze != null && Gaze.State == Yixuan.Vision.GazeState.Tracking &&
                !string.IsNullOrEmpty(attention.AttentionTargetId)) return;
            if (SuppressCameraForTurn)
            {
                Debug.Log("[Director] 云台：本轮已由本地关键词转过，忽略 LLM camera=" + plan.camera);
                return;
            }
            ResolveRefs();
            if (Gaze == null)
            {
                Debug.LogWarning("[Director] 场景里没有 EyeGimbalController，camera=" + plan.camera + " 被忽略");
                return;
            }

            float hold = plan.camera_hold > 0f ? Mathf.Clamp(plan.camera_hold, 1f, 15f) : -1f;

            // 云台动作和角度统一走 CameraMoveCatalog（和动作/表情同一套"目录 + 校验"做法）
            float yaw, pitch;
            if (!CameraMoveCatalog.TryResolve(plan.camera, plan.camera_angle, out yaw, out pitch))
            {
                Debug.LogWarning("[Director] 未知 camera id=" + plan.camera + "（应已被解析器过滤）");
                return;
            }
            var move = CameraMoveCatalog.Get(plan.camera);

            if (move.Track == "user")
            {
                var cam = Camera.main;
                if (cam != null) Gaze.Track(cam.transform, hold, allowBodyAssist);
                else Gaze.Center();
            }
            else if (move.Track == "store")
            {
                var store = GameObject.Find("Store");
                if (store != null) Gaze.Track(store.transform, hold, allowBodyAssist);
                else Gaze.TurnRelative(180f, 0f, hold, allowBodyAssist);
            }
            else if (plan.camera == "recenter")
            {
                Gaze.Center();
            }
            else
            {
                Gaze.TurnRelative(yaw, pitch, hold, allowBodyAssist);
            }

            Debug.Log("[Director] 云台 → " + plan.camera +
                      (plan.camera_angle > 0f ? " angle=" + plan.camera_angle.ToString("F0") + "°" : "") +
                      "（实际 yaw=" + yaw.ToString("F0") + " pitch=" + pitch.ToString("F0") + "）" +
                      (plan.camera_hold > 0f ? " 保持 " + plan.camera_hold.ToString("F0") + "s" : ""));
        }

        // ============================================================
        //  ⓪ 神态：头部姿态 + 视线 + 眨眼节奏（契约 v2）
        // ============================================================
        [Header("神态（头部叠加）")]
        [Tooltip("头部姿态过渡时间（秒）")]
        public float DemeanorBlendSec = 0.4f;
        [Tooltip("gaze=away 时头部偏转角度（度）；真正的眼球控制在 P3")]
        public float GazeAwayYaw = 10f;
        [Tooltip("gaze=up/down 时额外的俯仰角（度）")]
        public float GazeUpDownPitch = 5f;

        public string CurrentDemeanor { get; private set; } = DemeanorCatalog.DefaultId;

        Transform _head;
        Vector3 _headTarget, _headNow;          // (pitch, yaw, roll) 度
        Quaternion _lastApplied = Quaternion.identity;
        Quaternion _lastWritten;
        bool _hasWritten;
        float _yawSide = 1f;

        Yixuan.Face.BlinkController _blink;
        float _blinkMin0 = -1f, _blinkMax0 = -1f;

        void ApplyDemeanor(BehaviorPlan plan)
        {
            string id = DemeanorCatalog.Has(plan.demeanor) ? plan.demeanor : DemeanorCatalog.ForEmotion(plan.emotion);
            var d = DemeanorCatalog.Get(id);
            CurrentDemeanor = d.Id;

            string gaze = string.IsNullOrEmpty(plan.gaze) ? d.DefaultGaze : plan.gaze;
            float pitch = d.Pitch, yaw = 0f, roll = d.Roll;
            if (gaze == "down") pitch += GazeUpDownPitch;
            else if (gaze == "up") pitch -= GazeUpDownPitch;
            else if (gaze == "away") { _yawSide = -_yawSide; yaw = GazeAwayYaw * _yawSide; }   // 左右交替，不总往一边看
            _headTarget = new Vector3(pitch, yaw, roll);

            SetBlinkRate(d.BlinkRate);
            Debug.Log("[Director] 神态 → " + d.Id + "（" + d.Label + "）gaze=" + gaze +
                      " 头部 pitch=" + pitch.ToString("F0") + " yaw=" + yaw.ToString("F0") + " roll=" + roll.ToString("F0") +
                      " 眨眼×" + d.BlinkRate.ToString("F1"));
        }

        /// <summary>说完话、恢复 Brain 时调用：神态回到专注、头回正、表情强度和眨眼节奏复位</summary>
        public void ResetDemeanor()
        {
            CurrentDemeanor = DemeanorCatalog.DefaultId;
            _headTarget = Vector3.zero;
            SetBlinkRate(1f);
            ResolveRefs();
            if (Character != null && Character.Expressions != null) Character.Expressions.IntensityScale = 1f;
            Debug.Log("[Director] 神态复位 → attentive，IntensityScale=1");
        }

        void SetBlinkRate(float rate)
        {
            if (_blink == null && Character != null) _blink = Character.GetComponent<Yixuan.Face.BlinkController>();
            if (_blink == null) return;
            if (_blinkMin0 < 0f) { _blinkMin0 = _blink.MinInterval; _blinkMax0 = _blink.MaxInterval; }
            rate = Mathf.Clamp(rate, 0.2f, 3f);
            _blink.MinInterval = _blinkMin0 / rate;     // 倍率越大 → 间隔越短 → 眨得越勤
            _blink.MaxInterval = _blinkMax0 / rate;
        }

        void LateUpdate()
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1|2|8))return;
            if (Character == null) return;
            if (_head == null)
            {
                var anim = Character.GetComponent<Animator>();
                if (anim != null && anim.isHuman) _head = anim.GetBoneTransform(HumanBodyBones.Head);
                if (_head == null) return;
            }

            // 平滑过渡（指数逼近，DemeanorBlendSec 内走完约 95%）
            float k = DemeanorBlendSec > 0.01f ? 1f - Mathf.Exp(-3f * Time.deltaTime / DemeanorBlendSec) : 1f;
            _headNow = Vector3.Lerp(_headNow, _headTarget, k);

            // ★ 只叠加增量，不覆盖动画。
            //   如果这一帧没有任何东西重写过头部（动画没驱动它），头的旋转还是我们上一帧写的值 →
            //   必须先撤掉上一帧的增量，否则会一帧帧累积，头越转越歪。
            if (_hasWritten && _head.rotation == _lastWritten)
                _head.rotation = Quaternion.Inverse(_lastApplied) * _head.rotation;

            if (_headNow.sqrMagnitude < 0.0001f && _headTarget.sqrMagnitude < 0.0001f)
            {
                _hasWritten = false;
                return;
            }

            Transform r = Character.transform;
            Quaternion delta = Quaternion.AngleAxis(_headNow.y, r.up)
                             * Quaternion.AngleAxis(_headNow.x, r.right)      // pitch > 0 低头
                             * Quaternion.AngleAxis(_headNow.z, r.forward);   // roll  > 0 侧倾
            _head.rotation = delta * _head.rotation;
            _lastApplied = delta;
            _lastWritten = _head.rotation;
            _hasWritten = true;
        }

        /// <summary>当前头部神态偏移（pitch, yaw, roll），验收用</summary>
        public Vector3 HeadOffsetNow { get { return _headNow; } }

        // ============================================================
        //  行为报告（给 ChatUI 的行为面板 + 缺失素材日志）
        // ============================================================
        public event System.Action<BehaviorReport> OnReport;
        public BehaviorReport LastReport { get; private set; }
        string _gestureNote;

        /// <summary>回到待机（Catalog 里有 idle 才播）。</summary>
        public void ReturnToIdle()
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(1|2|8))return;
            ResolveRefs();
            if (Character == null) return;
            // 走路/身体转身时 base 层不能被“回到待机”打断
            if (Yixuan.Motion.CharacterLocomotion.AnyWalking ||
                (Locomotion != null && Locomotion.IsTurning)) return;

            if (!ActionCatalog.HasGesture("idle"))
            {
                Debug.Log("[Director] Catalog 里没有 idle，保持当前动作");
                return;
            }

            Character.PlayGesture("idle");
            LastGesture = "idle";
            Debug.Log("[Director] 回到待机");
        }

        /// <summary>
        /// 挂起 / 恢复 YixuanBrain 的主动行为。
        /// 恢复是延迟的：配置 ResumeBrainDelaySec 秒后才把 EnableProactive 放回 true
        /// （用协程 WaitForSeconds，绝不 while 空转）。连续调用会重置定时。
        /// </summary>
        public void SuspendBrain(bool suspend)
        {
            ResolveRefs();

            if (_resumeCo != null)
            {
                StopCoroutine(_resumeCo);
                _resumeCo = null;
            }

            if (Brain == null)
            {
                Debug.LogWarning("[Director] 找不到 YixuanBrain，无法挂起/恢复主动行为");
                return;
            }

            if (suspend)
            {
                Brain.EnableProactive = false;
                Debug.Log("[Director] 挂起 Brain 主动行为（t=" + Time.realtimeSinceStartup.ToString("F2") + "）");
                return;
            }

            float delay = Config != null ? Mathf.Max(0f, Config.ResumeBrainDelaySec) : 3f;
            // 未激活对象（编辑器离线回归夹具）无法启动协程：同步恢复，避免
            // "Coroutine couldn't be started because the game object is inactive" 报错刷屏。
            // 场景里的 Director 始终是激活状态，生产行为不受影响。
            if (!isActiveAndEnabled)
            {
                if (Brain != null) Brain.EnableProactive = true;
                return;
            }
            _resumeCo = StartCoroutine(ResumeBrainAfterDelay(delay));
            Debug.Log("[Director] " + delay.ToString("F1") + "s 后恢复 Brain 主动行为（t=" + Time.realtimeSinceStartup.ToString("F2") + " scale=" + Time.timeScale.ToString("F2") + "）");
        }

        System.Collections.IEnumerator ResumeBrainAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            _resumeCo = null;
            ResetDemeanor();
            if (Brain != null)
            {
                Brain.EnableProactive = true;
                Debug.Log("[Director] 恢复 Brain 主动行为（t=" + Time.realtimeSinceStartup.ToString("F2") + "）");
            }
        }

        public bool BrainResumePending { get { return _resumeCo != null; } }

        // ============================================================
        //  ① 表情
        // ============================================================
        void ApplyExpression(BehaviorPlan plan)
        {
            string emotion = string.IsNullOrEmpty(plan.emotion) ? "neutral" : plan.emotion.Trim();

            // 铁律：表情 id 也走 Catalog 校验（ExpressionLibrary 现读，不写死清单）
            if (!ActionCatalog.HasEmotion(emotion))
            {
                Debug.LogWarning("[Director] 未知表情 id：" + emotion + " → neutral");
                emotion = "neutral";
            }

            // intensity：解析器会补默认值；这里再兜一层，避免 0 导致“表情看不见”
            float intensity = plan.intensity;
            if (intensity <= 0f) intensity = Config != null ? Config.DefaultIntensity : 0.6f;
            intensity = Mathf.Clamp01(intensity);

            // 现有配方是按 1.0 倍调的：intensity 0 → 0.5 倍，0.5 → 1.0 倍，1 → 1.5 倍。
            // 说完话 ResetDemeanor() 会把它恢复为 1（以前不恢复，之后 Brain 的表情也一直被压弱）。
            if (Character.Expressions != null)
                Character.Expressions.IntensityScale = 0.5f + intensity;

            Character.SetExpression(emotion);
            Debug.Log("[Director] 表情 → " + emotion + "（intensity=" + intensity.ToString("F2") + "）");
        }

        // ============================================================
        //  ②③ 动作：LLM 指定 / 情绪默认表概率补
        // ============================================================
        void ApplyGesture(BehaviorPlan plan)
        {
            _gestureNote = null;
            // 走路/原地转身时不做其他动作（当前没有上下半身分层，避免两套动画抢骨骼）
            if (Yixuan.Motion.CharacterLocomotion.AnyWalking ||
                (Locomotion != null && Locomotion.IsTurning))
            {
                LastPlanHadGesture = false;
                _gestureNote = "走路/转身中，不做动作";
                return;
            }
            // ② LLM 明确指定了动作：先校验，再带冷却播放
            if (!string.IsNullOrEmpty(plan.gesture))
            {
                string wanted = plan.gesture.Trim();
                if (ActionCatalog.HasGesture(wanted))
                {
                    LastPlanHadGesture = true;   // 冷却跳过也算“有动作”，别回头用 idle 打断它
                    _gestureNote = IsCoolingDown(wanted) ? "冷却中，没有重播" : "按要求";
                    TryPlayWithCooldown(wanted, "LLM 指定");
                    return;     // 指定了就交给它，即使因冷却跳过也不再按情绪补
                }
                Debug.LogWarning("[Director] LLM 指定的动作不在 Catalog：" + wanted + "（忽略，改按情绪补）");
            }

            // ③ 没指定（或非法）：按情绪默认表以配置概率抽一个
            float chance = Config != null ? Config.EmotionGestureChance : 0.5f;
            if (Random.value > chance) { _gestureNote = "本句不做动作"; return; }      // 不是每句都动 —— 像真人才有“自动化感”

            string emotion = string.IsNullOrEmpty(plan.emotion) ? "neutral" : plan.emotion.Trim();
            string[] candidates = ActionCatalog.GesturesForEmotion(emotion);
            if (candidates == null || candidates.Length == 0)
            {
                // 该情绪没有默认动作表：保持现状，不硬塞动作
                return;
            }

            // 过滤：未知 id / 正在冷却的 id
            List<string> usable = new List<string>();
            for (int i = 0; i < candidates.Length; i++)
            {
                string id = candidates[i];
                if (string.IsNullOrEmpty(id)) continue;
                if (!ActionCatalog.HasGesture(id)) continue;
                if (IsCoolingDown(id)) continue;
                usable.Add(id);
            }

            if (usable.Count == 0)
            {
                Debug.Log("[Director] 情绪「" + emotion + "」的候选动作都在冷却，保持现状");
                return;
            }

            string pick = usable[Random.Range(0, usable.Count)];
            LastPlanHadGesture = true;
            _gestureNote = "按情绪「" + emotion + "」自动补";
            TryPlayWithCooldown(pick, "情绪默认(" + emotion + ")");
        }

        // ============================================================
        //  ④ 姿态（简单姿态图）
        // ============================================================
        void ApplyPosture(BehaviorPlan plan)
        {
            if (string.IsNullOrEmpty(plan.posture)) return;
            // 走路/转身时不允许切坐/跪等姿态
            if (Yixuan.Motion.CharacterLocomotion.AnyWalking ||
                (Locomotion != null && Locomotion.IsTurning)) return;

            string posture = plan.posture.Trim();
            if (posture == CurrentPosture) return;      // 没切换就不用动
            CurrentPosture = posture;

            // 姿态 id 合法性（Catalog.Postures = {"stand","sit","kneel"}）
            bool known = false;
            if (ActionCatalog.Postures != null)
            {
                for (int i = 0; i < ActionCatalog.Postures.Length; i++)
                    if (ActionCatalog.Postures[i] == posture) { known = true; break; }
            }
            if (!known)
            {
                Debug.LogWarning("[Director] 未知姿态 id：" + posture + "（跳过）");
                return;
            }

            if (!ActionCatalog.HasGesture(posture))
            {
                // 例如 stand：语义表里没有同名动作，回站立留给 T4 的姿态图
                Debug.Log("[Director] 姿态「" + posture + "」没有同名动作，保持当前动作（T4 再做过渡）");
                return;
            }

            Character.PlayGesture(posture);
            LastGesture = posture;
            LastPlanHadGesture = true;
            _lastPlayedAt[posture] = Time.time;
            Debug.Log("[Director] 姿态 → " + posture);
        }

        // ============================================================
        //  冷却（B5：连续 3 句要求同一动作，第 2/3 句不重复播）
        // ============================================================
        void TryPlayWithCooldown(string id, string reason)
        {
            if (IsCoolingDown(id))
            {
                float last = _lastPlayedAt[id];
                float cd = Config != null ? Mathf.Max(0f, Config.GestureCooldownSec) : 8f;
                Debug.Log("[Director] 冷却跳过 " + id +
                          "（距上次 " + (Time.time - last).ToString("F1") + "s < " + cd.ToString("F1") + "s，" +
                          reason + "）");
                return;
            }

            _lastPlayedAt[id] = Time.time;
            LastGesture = id;
            Character.PlayGesture(id);
            Debug.Log("[Director] 动作 → " + id + "（" + reason + "）");
        }

        bool IsCoolingDown(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (!_lastPlayedAt.ContainsKey(id)) return false;
            float cd = Config != null ? Mathf.Max(0f, Config.GestureCooldownSec) : 8f;
            return (Time.time - _lastPlayedAt[id]) < cd;
        }

        void OnDestroy()
        {
            if (_resumeCo != null)
            {
                StopCoroutine(_resumeCo);
                _resumeCo = null;
            }
        }
    }
}
