using UnityEngine;

namespace Yixuan.Presentation
{
    /// <summary>
    /// 演示镜头导演：待机=全身机位，说话/思考=半身近景，平滑推拉。
    ///
    /// 【为什么不用 Cinemachine】
    /// 项目里没装 Cinemachine；这里只需要两个固定机位之间平滑过渡，
    /// 用指数平滑（与帧率无关）足够，零依赖、行为可预测。
    ///
    /// 【为什么在 LateUpdate】
    /// 角色动画/表情都在 Update 与各组件 LateUpdate 里改骨骼；
    /// 相机只读状态（ChatSession.StateName），不写骨骼，放在 LateUpdate 末尾最稳。
    ///
    /// 【机位约定】
    /// 角色面朝 +Z（PMX 原始朝向 −Z，导入时 z 取反），所以相机放在 +Z 侧看向角色。
    /// Idle：拉远到全身；Talk：推近到半身（头部+胸口）。
    /// </summary>
    [DisallowMultipleComponent]
    public class CameraDirector : MonoBehaviour
    {
        [Header("引用（留空自动查找）")]
        public Yixuan.AI.ChatSession Session;
        public Camera Camera;
        public Transform FollowTarget;
        public Camera EyeSource;

        [Header("跟随 / 眼睛视角")]
        [Tooltip("跟随角色移动（不跟随就会走两步出画）")]
        public bool FollowCharacter = true;
        [Tooltip("主相机当角色眼睛（第一人称）；默认关闭：主视角用第三人称跟随，眼睛画面在右上角小窗")]
        public bool EyeView = false;
        public bool UseEyeCullingMask = true;

        [Header("待机机位（全身）")]
        public Vector3 IdlePosition = new Vector3(0.50f, 1.02f, 3.30f);
        public Vector3 IdleLookAt = new Vector3(0f, 0.92f, 0f);

        [Header("近景机位（说话/思考，半身）")]
        public Vector3 TalkPosition = new Vector3(0.42f, 1.26f, 2.05f);
        public Vector3 TalkLookAt = new Vector3(0f, 1.32f, 0f);

        [Header("第三人称跟随（大地图主视角）")]
        [Tooltip("启用后按角色朝向布置相机（正后方环绕），不再使用固定世界坐标机位")]
        public bool ThirdPerson = false;
        [Tooltip("相机水平偏角：0=正后方，180=正面；正值绕到角色一侧（看构图微调）")]
        public float TP_YawOffset = 18f;
        [Tooltip("相机与角色的水平距离（米）")]
        public float TP_Distance = 4.6f;
        [Tooltip("相机俯仰角（度）：正值相机高于看向点，向下拍")]
        public float TP_Pitch = 8.5f;
        [Tooltip("看向点离角色根节点的高度（米），一般取胸/颈 1.1~1.35")]
        public float TP_LookHeight = 1.18f;
        [Tooltip("第三人称相机 FOV（度）")]
        public float TP_Fov = 40f;
        [Tooltip("被建筑挡住时相机拉近的最小距离")]
        public float TP_MinDistance = 1.5f;
        [Tooltip("相机避障（用地图碰撞盒/BoxCollider 做球形扫描）")]
        public bool TP_Collision = true;
        public float TP_CollisionRadius = 0.3f;
        public LayerMask TP_CollisionMask = ~0;
        [Tooltip("右键拖拽环绕 / 滚轮缩放（演示时可手动调整视角）")]
        public bool TP_MouseOrbit = true;
        public float TP_OrbitSensitivity = 3.0f;
        public float TP_ZoomSpeed = 1.6f;
        public float TP_MinZoom = 2.2f;
        public float TP_MaxZoom = 9f;
        [Tooltip("相机跟随角色转向的平滑速度（越大越紧跟）")]
        public float TP_YawSharpness = 2.4f;
        public float TP_PosSharpness = 10f;
        [Tooltip("角色说话/思考时相机推近的米数（0=不推近）")]
        public float TP_TalkPullIn = 0.25f;

        [Header("过渡")]
        [Tooltip("位置平滑速度（越大越快）")]
        public float MoveSharpness = 2.2f;
        [Tooltip("朝向平滑速度")]
        public float LookSharpness = 4.0f;
        [Tooltip("启动时直接吸附到待机机位（不从头飞过去）")]
        public bool SnapOnStart = true;

        [Header("待机微动（让画面不像静态截图）")]
        public bool IdleBreathing = true;
        [Tooltip("位移幅度（米），建议 0.004~0.015")]
        public float BreathAmplitude = 0.006f;
        public float BreathSpeed = 0.55f;

        [Header("调试")]
        public bool VerboseLog = false;

        string _lastState;
        bool _eyeModeApplied;
        int _defaultCullingMask;
        float _defaultFov = 34f;
        Vector3 _followOrigin;
        Vector3 _idleOffset, _talkOffset, _idleLookOffset, _talkLookOffset;
        bool _offsetsReady;

        // 第三人称运行时状态
        float _tpYaw, _tpDist, _tpOrbitYaw, _tpOrbitPitch;
        bool _tpReady;

        void Awake()
        {
            ResolveRefs();
        }

        void Start()
        {
            ResolveRefs();
            if (Camera != null)
            {
                _defaultCullingMask = Camera.cullingMask;
                _defaultFov = Camera.fieldOfView;
            }
            CaptureFollowOffsets();
            if (FollowCharacter && EyeView && EyeSource != null)
            {
                ApplyEyeMode(true);
            }
            else if (ThirdPerson)
            {
                SetupThirdPerson();
            }
            else
            {
                if (Camera != null) Camera.fieldOfView = 34f;
                if (SnapOnStart) SnapTo(IdlePosition, IdleLookAt);
            }
        }

        /// <summary>运行时切换第三人称模式（任务面板/控制面板/菜单可用）。</summary>
        public void SetThirdPerson(bool on)
        {
            ThirdPerson = on;
            if (on)
            {
                EyeView = false;
                _tpReady = false;
                SetupThirdPerson();
            }
            else if (Camera != null)
            {
                Camera.fieldOfView = _defaultFov;
            }
        }

        void ResolveRefs()
        {
            if (Session == null) Session = FindObjectOfType<Yixuan.AI.ChatSession>();
            if (Camera == null) Camera = GetComponent<Camera>();
            if (Camera == null) Camera = Camera.main;
            if (FollowTarget == null)
            {
                var character = GameObject.Find("Yixuan_Test");
                if (character != null) FollowTarget = character.transform;
            }
            if (EyeSource == null)
            {
                var eye = GameObject.Find("EyeCamera");
                if (eye != null) EyeSource = eye.GetComponent<Camera>();
            }
        }

        void CaptureFollowOffsets()
        {
            if (FollowTarget == null) return;
            _followOrigin = FollowTarget.position;
            _idleOffset = IdlePosition - _followOrigin;
            _talkOffset = TalkPosition - _followOrigin;
            _idleLookOffset = IdleLookAt - _followOrigin;
            _talkLookOffset = TalkLookAt - _followOrigin;
            _offsetsReady = true;
        }

        void ApplyEyeMode(bool on)
        {
            if (Camera == null) return;
            _eyeModeApplied = on;
            if (on)
            {
                Camera.fieldOfView = EyeSource != null ? EyeSource.fieldOfView : _defaultFov;
                if (UseEyeCullingMask && EyeSource != null) Camera.cullingMask = EyeSource.cullingMask;
            }
            else
            {
                Camera.cullingMask = _defaultCullingMask;
                Camera.fieldOfView = ThirdPerson ? TP_Fov : _defaultFov;
            }
        }

        void LateUpdate()
        {
            if (Camera == null) { ResolveRefs(); if (Camera == null) return; }

            bool wantEye = FollowCharacter && EyeView && EyeSource != null;
            if (wantEye != _eyeModeApplied) ApplyEyeMode(wantEye);

            if (wantEye)
            {
                transform.position = EyeSource.transform.position;
                transform.rotation = EyeSource.transform.rotation;
                return;
            }

            string state = (Session != null) ? Session.StateName : "Idle";
            bool talk = state == "Speaking" || state == "Thinking";

            if (ThirdPerson)
            {
                UpdateThirdPerson(talk);
                return;
            }

            Vector3 wantPos = talk ? TalkPosition : IdlePosition;
            Vector3 wantLook = talk ? TalkLookAt : IdleLookAt;

            if (FollowCharacter && FollowTarget != null)
            {
                if (!_offsetsReady) CaptureFollowOffsets();
                wantPos = FollowTarget.position + (talk ? _talkOffset : _idleOffset);
                wantLook = FollowTarget.position + (talk ? _talkLookOffset : _idleLookOffset);
            }

            // 待机时加一点极小的呼吸式位移（只动相机，不动角色，避免"机器人感"）
            if (!talk && IdleBreathing)
            {
                float t = Time.unscaledTime * BreathSpeed;
                wantPos += new Vector3(Mathf.Sin(t) * BreathAmplitude,
                                       Mathf.Sin(t * 0.73f) * BreathAmplitude * 0.6f,
                                       0f);
            }

            // 指数平滑：与帧率无关，画面不会因为帧率波动而忽快忽慢
            float kPos = 1f - Mathf.Exp(-MoveSharpness * Time.deltaTime);
            float kRot = 1f - Mathf.Exp(-LookSharpness * Time.deltaTime);

            transform.position = Vector3.Lerp(transform.position, wantPos, kPos);

            Vector3 dir = wantLook - transform.position;
            if (dir.sqrMagnitude > 0.0001f)
            {
                Quaternion wantRot = Quaternion.LookRotation(dir.normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, wantRot, kRot);
            }

            if (VerboseLog && state != _lastState)
            {
                _lastState = state;
                Debug.Log("[CameraDirector] 状态=" + state + " → " + (talk ? "半身近景" : "全身待机"));
            }
        }

        void SnapTo(Vector3 pos, Vector3 look)
        {
            transform.position = pos;
            Vector3 dir = look - pos;
            if (dir.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        // ============================================================
        //  第三人称跟随（大地图主视角）
        // ============================================================

        void SetupThirdPerson()
        {
            if (FollowTarget == null) ResolveRefs();
            _tpDist = Mathf.Clamp(TP_Distance, TP_MinZoom, TP_MaxZoom);
            _tpOrbitYaw = 0f;
            _tpOrbitPitch = 0f;
            _tpYaw = FollowTarget != null ? FollowTarget.eulerAngles.y + TP_YawOffset : TP_YawOffset;
            _tpReady = true;
            if (Camera != null)
            {
                Camera.fieldOfView = TP_Fov;
                if (Camera.farClipPlane < 500f)
                {
                    Camera.farClipPlane = 900f;
                    if (VerboseLog) Debug.Log("[CameraDirector] 第三人称：farClipPlane 提到 900（大地图）");
                }
            }
            if (FollowTarget != null) SnapThirdPerson();
        }

        /// <summary>把当前视角重置到默认的“身后 3/4”机位（菜单/UI 可调用）。</summary>
        public void ResetThirdPersonView()
        {
            TP_YawOffset = 18f;
            TP_Pitch = 8.5f;
            TP_LookHeight = 1.18f;
            _tpOrbitYaw = 0f;
            _tpOrbitPitch = 0f;
            SetThirdPerson(true);
        }

        /// <summary>立刻把第三人称相机吸附到目标机位（不插值），用于菜单/验收截图。</summary>
        public void SnapThirdPersonView()
        {
            if (!ThirdPerson) return;
            if (FollowTarget == null) ResolveRefs();
            if (FollowTarget == null) return;
            // 关键：改完 TP_* 参数后必须按新参数重算 _tpYaw/_tpDist，否则 Snap 会沿用旧的平滑值。
            _tpYaw = FollowTarget.eulerAngles.y + TP_YawOffset + _tpOrbitYaw;
            _tpDist = Mathf.Clamp(TP_Distance, TP_MinZoom, TP_MaxZoom);
            _tpReady = true;
            SnapThirdPerson();
        }

        void SnapThirdPerson()
        {
            Vector3 pos, pivot;
            ComputeThirdPerson(out pos, out pivot);
            transform.position = pos;
            Vector3 dir = pivot - pos;
            if (dir.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        void HandleThirdPersonInput()
        {
            if (!TP_MouseOrbit) return;
            if (Yixuan.WorldMap.WorldMapPanel.CameraInputBlocked) return;
            if (Input.GetMouseButton(1))
            {
                _tpOrbitYaw += Input.GetAxis("Mouse X") * TP_OrbitSensitivity;
                _tpOrbitPitch -= Input.GetAxis("Mouse Y") * TP_OrbitSensitivity;
                // 水平环绕允许 360°；只把数值规整到 [-180,180]，避免长时间旋转后浮点值无限增大。
                _tpOrbitYaw = Mathf.Repeat(_tpOrbitYaw + 180f, 360f) - 180f;
                _tpOrbitPitch = Mathf.Clamp(_tpOrbitPitch, -25f, 40f);
            }
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.0001f)
                _tpDist = Mathf.Clamp(_tpDist - scroll * TP_ZoomSpeed * 4f, TP_MinZoom, TP_MaxZoom);
        }

        void ComputeThirdPerson(out Vector3 position, out Vector3 pivot)
        {
            pivot = FollowTarget.position + Vector3.up * TP_LookHeight;
            float pitch = Mathf.Clamp(TP_Pitch + _tpOrbitPitch, -20f, 70f);
            float yawRad = _tpYaw * Mathf.Deg2Rad;
            float pitchRad = Mathf.Clamp(pitch, -20f, 70f) * Mathf.Deg2Rad;
            // 相机在 pivot 的“背后上方”：back = -forward(yaw) 抬高 pitch
            Vector3 back = new Vector3(-Mathf.Sin(yawRad) * Mathf.Cos(pitchRad),
                                       Mathf.Sin(pitchRad),
                                       -Mathf.Cos(yawRad) * Mathf.Cos(pitchRad));
            float dist = _tpDist;
            if (TP_Collision)
            {
                RaycastHit hit;
                if (Physics.SphereCast(pivot, TP_CollisionRadius, back, out hit, dist, TP_CollisionMask,
                                       QueryTriggerInteraction.Ignore))
                {
                    dist = Mathf.Max(TP_MinDistance, hit.distance - 0.05f);
                }
            }
            position = pivot + back * dist;
        }

        void UpdateThirdPerson(bool talk)
        {
            if (FollowTarget == null) { ResolveRefs(); if (FollowTarget == null) return; }
            if (!_tpReady) SetupThirdPerson();

            HandleThirdPersonInput();

            float targetYaw = FollowTarget.eulerAngles.y + TP_YawOffset + _tpOrbitYaw;
            // 角色转身时相机平滑跟到背后，而不是硬切。
            _tpYaw = Mathf.LerpAngle(_tpYaw, targetYaw, 1f - Mathf.Exp(-TP_YawSharpness * Time.deltaTime));

            Vector3 wantPos, pivot;
            ComputeThirdPerson(out wantPos, out pivot);

            // 说话时轻微推近（可关闭：把 TP_TalkPullIn 设为 0）
            if (talk && TP_TalkPullIn > 0.001f)
            {
                Vector3 dir = (wantPos - pivot).normalized;
                float pulled = Mathf.Max(TP_MinDistance, _tpDist - TP_TalkPullIn);
                wantPos = pivot + dir * pulled;
            }

            float kPos = 1f - Mathf.Exp(-TP_PosSharpness * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, wantPos, kPos);
            Vector3 lookDir = pivot - transform.position;
            if (lookDir.sqrMagnitude > 0.0001f)
            {
                Quaternion wantRot = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, wantRot, kPos);
            }

            string state = (Session != null) ? Session.StateName : "Idle";
            if (VerboseLog && state != _lastState)
            {
                _lastState = state;
                Debug.Log("[CameraDirector] 第三人称：状态=" + state + "，距离=" + _tpDist.ToString("F2") +
                          "，yaw=" + _tpYaw.ToString("F0"));
            }
        }
    }
}
