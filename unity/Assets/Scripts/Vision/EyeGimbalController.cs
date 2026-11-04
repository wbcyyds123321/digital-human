using UnityEngine;

namespace Yixuan.Vision
{
    public enum GazeState
    {
        Center,      // 回正/待机
        Commanded,   // 执行语音指定的绝对朝向
        Recentering, // 平滑回到正前方
        Tracking     // 持续看向世界目标
    }

    /// <summary>
    /// 数字人眼睛云台：
    ///   YawPivot 负责水平旋转（相对角色 forward，可 360° 连续转）
    ///   PitchPivot 负责俯仰（默认 ±89°）
    ///
    /// 这是相机云台，不写 Head/Neck 骨骼；人物模型保持不动。
    /// 状态机：Center / Commanded / Recentering / Tracking。
    /// </summary>
    public class EyeGimbalController : MonoBehaviour
    {
        [Header("绑定")]
        public Transform Character;
        public Transform YawPivot;
        public Transform PitchPivot;

        [Header("角度")]
        public float BaseYaw = 0f;      // 安装校准；0 = 相机默认朝身体正前方（+Z）
        public float HomeYaw = 0f;      // 相对 BaseYaw 的归零角
        public float MinPitch = -89f;   // 抬头为正
        public float MaxPitch = 89f;
        public bool UnlimitedPitch = false;
        public float Smooth = 8f;

        [Header("身体基准 / 有限观察")]
        [Tooltip("只允许相对身体正前方左右各 MaxRelativeYaw、上下各 MaxRelativePitch 的观察偏角")]
        public bool LimitRelativeRange = true;
        public float MaxRelativeYaw = 60f;
        public float MaxRelativePitch = 30f;
        [Tooltip("观察目标超出云台范围且角色站立时，让身体转过去配合（不转头骨）")]
        public bool BodyAssist = true;
        public Yixuan.Motion.CharacterLocomotion Locomotion;

        [Header("行为")]
        [Tooltip("自动回正。默认关闭：转过之后保持角度，只有明确说“回正/看前面”才回默认位置。")]
        public bool AutoRecenter = false;
        [Tooltip("仅当 AutoRecenter=true 时生效：保持多少秒后回正")]
        public float DefaultHoldSeconds = 6f;
        public float RecenterDistance = 0.1f;

        public GazeState State { get; private set; } = GazeState.Center;
        public float CurrentYaw { get; private set; }
        public float CurrentPitch { get; private set; }
        public float TargetYaw { get; private set; }
        public float TargetPitch { get; private set; }
        public string LastCommand { get; private set; } = "";

        /// <summary>身体配合转向任务的 owner（C1：只取消本观察拥有的转向）。</summary>
        public const string BodyAssistOwner = "gaze.body-assist";

        /// <summary>本次观察是否因目标超出云台范围、只做了限幅。</summary>
        public bool LastObservationLimited { get; private set; }
        /// <summary>本次观察目标是否超出相对云台范围（即使身体配合成功也会为 true）。</summary>
        public bool LastObservationOutOfRange { get; private set; }
        /// <summary>给 ChatSession 的限幅/未覆盖说明，不用于声称已经看清。</summary>
        public string LastObservationNote { get; private set; }
        /// <summary>身体配合结果："" / pending / completed / cancelled。</summary>
        public string LastBodyAssistOutcome { get; private set; } = "";

        /// <summary>本次观察发起的身体转向是否仍在进行。</summary>
        public bool IsBodyAssistTurning
        {
            get { return Locomotion != null && Locomotion.IsTurningOwnedBy(BodyAssistOwner); }
        }

        /// <summary>云台平滑 + 身体配合都结束才算观察稳定（F2）。</summary>
        public bool IsObservationSettling
        {
            get { return IsMoving || IsBodyAssistTurning; }
        }

        public bool IsMoving
        {
            get
            {
                return Mathf.Abs(Mathf.DeltaAngle(CurrentYaw, TargetYaw)) > 0.2f
                    || Mathf.Abs(CurrentPitch - TargetPitch) > 0.2f;
            }
        }

        float _holdUntil;
        Transform _trackTarget;
        public Transform TrackingTarget { get { return _trackTarget; } }
        float _bodyAssistTargetWorldYaw;
        bool _bodyAssistPending;

        void Awake()
        {
            if (Character == null)
            {
                var c = GameObject.Find("Yixuan_Test");
                if (c != null) Character = c.transform;
            }
            if (YawPivot == null) YawPivot = transform;
            if (PitchPivot == null && YawPivot != null)
            {
                var cams = YawPivot.GetComponentsInChildren<Camera>(true);
                if (cams.Length > 0) PitchPivot = cams[0].transform;
            }
            if (Locomotion == null) Locomotion = FindObjectOfType<Yixuan.Motion.CharacterLocomotion>();
            CurrentYaw = HomeYaw;
            TargetYaw = HomeYaw;
            CurrentPitch = 0f;
            TargetPitch = 0f;
            Apply();
        }

        string _cameraOwner;
        void Update(){using(Yixuan.AI.Tasks.TaskAuthority.Enter(_cameraOwner))UpdateOwned();}
        void UpdateOwned()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            // F2/C1：先结算身体配合是否完成/被取消，再决定云台和自动回正。
            RefreshBodyAssistOutcome();

            // 默认 AutoRecenter=false：转过之后一直保持，只有明确“回正/看前面”才回默认位置。
            // （用户要求：转角先保留，别自动回正；否则刚转过去看东西，几秒后又自己转回来了。）
            if ((State == GazeState.Commanded || State == GazeState.Tracking) &&
                AutoRecenter && Time.time >= _holdUntil)
                BeginRecenter();

            if (State == GazeState.Tracking)
            {
                if (_trackTarget == null) BeginRecenter();
                else SetTargetFromWorld(_trackTarget.position);
            }

            float k = 1f - Mathf.Exp(-Mathf.Max(0.01f, Smooth) * dt);
            CurrentYaw = Mathf.LerpAngle(CurrentYaw, TargetYaw, k);
            CurrentPitch = Mathf.Lerp(CurrentPitch, TargetPitch, k);

            if (State == GazeState.Recentering &&
                Mathf.Abs(Mathf.DeltaAngle(CurrentYaw, TargetYaw)) < RecenterDistance &&
                Mathf.Abs(CurrentPitch - TargetPitch) < RecenterDistance)
            {
                State = GazeState.Center;
            }

            Apply();
        }

        void Apply()
        {
            if (YawPivot != null)
                YawPivot.localRotation = Quaternion.Euler(0f, BaseYaw + CurrentYaw, 0f);
            if (PitchPivot != null)
                PitchPivot.localRotation = Quaternion.Euler(-CurrentPitch, 0f, 0f);
        }

        /// <summary>回正到角色 forward（HomeYaw）。C1：只取消本云台发起的身体配合，不误杀导航转身。</summary>
        public void Center(bool instant = false)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(2))return;
            _cameraOwner=Yixuan.AI.Tasks.TaskAuthority.Caller;
            bool wasBodyAssistPending = _bodyAssistPending;
            CancelOwnedBodyAssistTurn();
            // 取消本观察的身体配合时保留 cancelled，供 F2 视觉等待判定失败；
            // 已经完成或普通回正则清掉旧结果（回正后不能冒充“已转向新方向”）。
            ResetObservationStatus(wasBodyAssistPending && LastBodyAssistOutcome == "cancelled");
            _trackTarget = null;
            TargetYaw = HomeYaw;
            TargetPitch = 0f;
            LastCommand = "回正";
            if (instant)
            {
                CurrentYaw = TargetYaw;
                CurrentPitch = TargetPitch;
                State = GazeState.Center;
                Apply();
            }
            else
            {
                State = GazeState.Recentering;
            }
        }

        /// <summary>
        /// 相对角色 forward 偏转 yaw（正=右，负=左），pitch 正=抬头。
        /// allowBodyAssist=false 时绝不启动身体转向，只把云台限幅到允许范围（F3）。
        /// </summary>
        public void TurnRelative(float yaw, float pitch, float holdSeconds = -1f, bool allowBodyAssist = true)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(2))return;
            // 新观察命令替换旧观察：先把自己发起的身体配合收掉，避免两个观察任务叠转。
            CancelOwnedBodyAssistTurn();
            ResetObservationStatus();
            _trackTarget = null;
            float desiredYaw = NormalizeYaw(yaw);
            float desiredPitch = ClampPitch(pitch);
            if (TryBodyAssist(desiredYaw, allowBodyAssist))
            {
                TargetYaw = HomeYaw;
                TargetPitch = ClampPitchRelative(desiredPitch);
                LastCommand = "身体配合转向 yaw=" + desiredYaw.ToString("F0");
                State = GazeState.Commanded;
                _holdUntil = Time.time + (holdSeconds > 0f ? holdSeconds : DefaultHoldSeconds);
                return;
            }
            TargetYaw = NormalizeYaw(HomeYaw + ClampYawRelative(desiredYaw));
            TargetPitch = ClampPitchRelative(desiredPitch);
            LastCommand = LastObservationLimited
                ? "仅转镜头 yaw=" + yaw.ToString("F0") + "（限幅，身体未动）"
                : "相对 yaw=" + yaw.ToString("F0") + " pitch=" + pitch.ToString("F0");
            State = GazeState.Commanded;
            _holdUntil = Time.time + (holdSeconds > 0f ? holdSeconds : DefaultHoldSeconds);
        }

        /// <summary>
        /// 持续看一个世界目标；目标丢失则自动回正。
        /// 超出云台范围且允许身体配合时站立可请求身体转向；allowBodyAssist=false 时只限幅。
        /// </summary>
        public void Track(Transform target, float holdSeconds = -1f, bool allowBodyAssist = true)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(2))return;
            _trackTarget = target;
            if (target == null)
            {
                BeginRecenter();
                return;
            }

            CancelOwnedBodyAssistTurn();
            ResetObservationStatus();
            float desiredYaw = RelativeYawToWorld(target.position);
            float desiredPitch = RelativePitchToWorld(target.position);
            if (TryBodyAssist(desiredYaw, allowBodyAssist))
            {
                TargetYaw = HomeYaw;
                TargetPitch = ClampPitchRelative(desiredPitch);
                LastCommand = "身体配合看 " + target.name;
                State = GazeState.Commanded;
                _holdUntil = Time.time + (holdSeconds > 0f ? holdSeconds : DefaultHoldSeconds * 2f);
                return;
            }

            SetTargetFromWorld(target.position);
            LastCommand = LastObservationLimited
                ? "仅转镜头看 " + target.name + "（限幅，身体未动）"
                : "看 " + target.name;
            State = GazeState.Tracking;
            _holdUntil = Time.time + (holdSeconds > 0f ? holdSeconds : DefaultHoldSeconds * 2f);
        }

        bool TryBodyAssist(float desiredRelativeYaw, bool allowBodyAssist)
        {
            float absYaw = Mathf.Abs(desiredRelativeYaw);
            if (!LimitRelativeRange || absYaw <= Mathf.Abs(MaxRelativeYaw)) return false;

            LastObservationOutOfRange = true;
            if (!allowBodyAssist)
            {
                MarkObservationLimited(desiredRelativeYaw, "用户明确要求只转镜头");
                return false;
            }
            if (!BodyAssist)
            {
                MarkObservationLimited(desiredRelativeYaw, "身体配合已关闭");
                return false;
            }
            if (Locomotion == null || Locomotion.IsMoving || Locomotion.IsTurning)
            {
                MarkObservationLimited(desiredRelativeYaw,
                    Locomotion == null ? "场景没有走路控制器" : "角色正在移动/转身");
                return false;
            }
            if (!Locomotion.TurnRelative(desiredRelativeYaw, BodyAssistOwner))
            {
                MarkObservationLimited(desiredRelativeYaw, "身体转向未启动");
                return false;
            }

            _bodyAssistTargetWorldYaw = Mathf.Repeat(Locomotion.transform.eulerAngles.y + desiredRelativeYaw, 360f);
            _bodyAssistPending = true;
            LastBodyAssistOutcome = "pending";
            LastObservationLimited = false;
            return true;
        }

        void MarkObservationLimited(float desiredYaw, string reason)
        {
            LastObservationLimited = true;
            LastObservationNote = "目标方向 yaw=" + NormalizeYaw(desiredYaw).ToString("F0") +
                                  "° 超出云台相对范围（±" + Mathf.Abs(MaxRelativeYaw).ToString("F0") + "°）；" +
                                  reason + "，本次仅限幅转镜头，未覆盖完整方向。";
        }

        /// <summary>
        /// F2：结算身体配合是完成还是被取消。返回 true 表示仍在进行。
        /// Update 每帧调用；离线回归可手动调用，不依赖 Play 模式。
        /// </summary>
        public bool RefreshBodyAssistOutcome()
        {
            if (!_bodyAssistPending) return false;
            if (Locomotion == null)
            {
                _bodyAssistPending = false;
                LastBodyAssistOutcome = "cancelled";
                return false;
            }
            if (Locomotion.IsTurningOwnedBy(BodyAssistOwner)) return true;
            if (Locomotion.IsTurning)
            {
                // 被另一个任务（导航/身体转身）替换：本次观察的身体配合没有完成。
                _bodyAssistPending = false;
                LastBodyAssistOutcome = "cancelled";
                return false;
            }

            float bodyYaw = Locomotion.transform.eulerAngles.y;
            bool reached = Mathf.Abs(Mathf.DeltaAngle(bodyYaw, _bodyAssistTargetWorldYaw)) <= 1.5f;
            _bodyAssistPending = false;
            LastBodyAssistOutcome = reached ? "completed" : "cancelled";
            return false;
        }

        void CancelOwnedBodyAssistTurn()
        {
            RefreshBodyAssistOutcome();
            if (!_bodyAssistPending) return;
            if (Locomotion != null) Locomotion.StopTurning(BodyAssistOwner);
            _bodyAssistPending = false;
            LastBodyAssistOutcome = "cancelled";
        }

        /// <summary>
        /// F01：新轮次/用户打断 = 上一轮观察作废，彻底丢弃它的执行结果。
        /// 与 <see cref="Center"/> 的区别：Center 会为“同一次观察被取消”保留 cancelled，
        /// 供 F2 视觉等待判失败；而新轮次的“回正 + 看某方向”不能把旧任务的 cancelled
        /// 当成自己的失败，否则合法的“回正后看前面”会被绑定 Ask 的 gate 拦掉。
        /// </summary>
        public void AbandonObservationForNewTurn()
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(2))return;
            CancelOwnedBodyAssistTurn();
            _bodyAssistPending = false;
            LastBodyAssistOutcome = "";
            ResetObservationStatus();
        }

        void ResetObservationStatus(bool preserveBodyAssistOutcome = false)
        {
            LastObservationLimited = false;
            LastObservationOutOfRange = false;
            LastObservationNote = null;
            if (!preserveBodyAssistOutcome && !_bodyAssistPending) LastBodyAssistOutcome = "";
        }

        public void BeginRecenter()
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(2))return;
            _cameraOwner=Yixuan.AI.Tasks.TaskAuthority.Caller;
            bool wasBodyAssistPending = _bodyAssistPending;
            CancelOwnedBodyAssistTurn();
            ResetObservationStatus(wasBodyAssistPending && LastBodyAssistOutcome == "cancelled");
            _trackTarget = null;
            TargetYaw = HomeYaw;
            TargetPitch = 0f;
            State = GazeState.Recentering;
        }

        void SetTargetFromWorld(Vector3 worldPos)
        {
            TargetYaw = ClampYawRelative(RelativeYawToWorld(worldPos));
            TargetPitch = ClampPitchRelative(RelativePitchToWorld(worldPos));
        }

        float RelativeYawToWorld(Vector3 worldPos)
        {
            Transform basis = Character != null ? Character : transform;
            Vector3 localDir = basis.InverseTransformDirection(worldPos - (YawPivot != null ? YawPivot.position : transform.position));
            float worldYaw = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;
            return NormalizeYaw(worldYaw - BaseYaw - HomeYaw);
        }

        float RelativePitchToWorld(Vector3 worldPos)
        {
            Transform basis = Character != null ? Character : transform;
            Vector3 localDir = basis.InverseTransformDirection(worldPos - (YawPivot != null ? YawPivot.position : transform.position));
            float horiz = Mathf.Sqrt(localDir.x * localDir.x + localDir.z * localDir.z);
            return Mathf.Atan2(localDir.y, Mathf.Max(0.0001f, horiz)) * Mathf.Rad2Deg;
        }

        float ClampYawRelative(float yaw)
        {
            float normalized = NormalizeYaw(yaw);
            if (!LimitRelativeRange) return normalized;
            return Mathf.Clamp(normalized, -Mathf.Abs(MaxRelativeYaw), Mathf.Abs(MaxRelativeYaw));
        }

        float ClampPitchRelative(float pitch)
        {
            float p = ClampPitch(pitch);
            if (!LimitRelativeRange) return p;
            float limit = Mathf.Abs(MaxRelativePitch);
            return Mathf.Clamp(p, -limit, limit);
        }

        float ClampPitch(float p)
        {
            if (UnlimitedPitch) return p;
            return Mathf.Clamp(p, MinPitch, MaxPitch);
        }

        static float NormalizeYaw(float y)
        {
            return Mathf.Repeat(y + 180f, 360f) - 180f;
        }

        /// <summary>从中文语音/文字里解析云台命令，命中就立即执行；返回是否命中。</summary>
        public bool HandleUtterance(string text)
        {
            if(!Yixuan.AI.Tasks.TaskAuthority.Allows(2))return false;
            GazeIntent intent;
            if (!GazeVoiceIntent.TryParse(text, out intent)) return false;

            switch (intent.Type)
            {
                case GazeIntentType.Center:
                    Center();
                    return true;
                case GazeIntentType.Relative:
                    TurnRelative(intent.Yaw, intent.Pitch, intent.HoldSeconds, intent.AllowBodyAssist);
                    return true;
                case GazeIntentType.LookAtStore:
                    {
                        var store = GameObject.Find("Store");
                        if (store != null) { Track(store.transform, intent.HoldSeconds, intent.AllowBodyAssist); return true; }
                        TurnRelative(180f, 0f, intent.HoldSeconds, intent.AllowBodyAssist);
                        return true;
                    }
                case GazeIntentType.LookAtUser:
                    {
                        var cam = Camera.main;
                        if (cam != null) { Track(cam.transform, intent.HoldSeconds, intent.AllowBodyAssist); return true; }
                        Center();
                        return true;
                    }
            }
            return false;
        }
    }
}
