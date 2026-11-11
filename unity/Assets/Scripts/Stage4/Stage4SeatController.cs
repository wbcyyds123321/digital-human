using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Yixuan.AI.Tasks;
using Yixuan.Core;
using Yixuan.Motion;
using Yixuan.World;

namespace Yixuan.Stage4
{
    /// <summary>
    /// Stage-4 generic bench sitting controller: approach -> align -> snap -> sit
    /// -> stand. It deliberately uses one generic path for both plaza benches and
    /// reads all target coordinates from the Stage-3 sidecar loaded by Stage4SeatData.
    /// No navigation task or TaskInteractable registration is added.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Stage4SeatController : MonoBehaviour
    {
        public enum SeatState { Idle, WalkingToApproach, Aligning, Snapping, Seated, StandingUp, Cancelling, Failed }

        public static Stage4SeatController Instance { get; private set; }

        /// <summary>
        /// While seated / snapping / standing, normal locomotion must refuse new
        /// walk requests instead of trying to drag the character out of the seat.
        /// </summary>
        public static bool BlocksNormalWalking
        {
            get
            {
                if (Instance == null) return false;
                var s = Instance.State;
                return s == SeatState.Seated || s == SeatState.Aligning ||
                       s == SeatState.Snapping || s == SeatState.StandingUp;
            }
        }

        public SeatState State { get; private set; } = SeatState.Idle;
        public string CurrentEntity { get; private set; } = "";
        public string LastError { get; private set; } = "";
        public string LastStatus { get; private set; } = "idle";
        public bool DebugOpen;
        public readonly List<string> Events = new List<string>();
        public string RunLogPath { get; private set; } = "";

        YixuanCharacter _character;
        CharacterLocomotion _motor;
        GroundSnap _snap;
        MixamoActionManifest _manifest;
        YixuanAnimationPlayer _anim;
        Transform _pivot;
        Stage4SeatEntry _entry;
        string _owner;
        Coroutine _routine;
        int _runId;
        bool _cancelRequested;
        bool _savedWalking;
        bool _savedSnap;
        float _hipsAboveSeat;
        float _forwardOffset;
        float _approachPadding;
        string _sitClip;
        int _sameFrame;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Resolve();
            Stage4SeatData.EnsureLoaded();
            if (Application.isEditor)
            {
                try
                {
                    string root = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", "Logs", "stage4");
                    Directory.CreateDirectory(root);
                    RunLogPath = Path.Combine(root, "seat_run_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl");
                }
                catch (Exception e) { Debug.LogWarning("[Stage4Seat] 运行日志路径创建失败：" + e.Message); }
            }
            if (Stage4SeatData.Calibration != null)
            {
                _hipsAboveSeat = Stage4SeatData.Calibration.hips_above_seat_m;
                _forwardOffset = Stage4SeatData.Calibration.forward_offset_m;
                _approachPadding = Mathf.Max(0f, Stage4SeatData.Calibration.approach_navigation_padding_m);
            }
            if (!Stage4SeatData.Loaded)
            {
                State = SeatState.Failed;
                LastError = Stage4SeatData.Error ?? "Stage-4 数据未载入";
                Log("data_error " + LastError);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (FindObjectOfType<Stage4SeatController>() != null) return;
            var go = new GameObject("Stage4SeatController");
            go.AddComponent<Stage4SeatController>();
        }

        void Resolve()
        {
            _character = FindObjectOfType<YixuanCharacter>();
            if (_character == null) return;
            _motor = _character.GetComponent<CharacterLocomotion>();
            if (_motor == null) _motor = FindObjectOfType<CharacterLocomotion>();
            _snap = _character.GetComponent<GroundSnap>();
            if (_snap == null) _snap = FindObjectOfType<GroundSnap>();
            _manifest = _character.Manifest;
            if (_manifest == null) _manifest = _character.GetComponent<MixamoActionManifest>();
            _anim = _character.AnimPlayer;
            if (_anim == null) _anim = _character.GetComponent<YixuanAnimationPlayer>();
            _pivot = _character.Center;
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F11) && !TextFocusBlockingShortcut())
            {
                DebugOpen = !DebugOpen;
                if (DebugOpen) Log("debug_panel_open");
            }
        }

        static bool TextFocusBlockingShortcut()
        {
            if (GUIUtility.keyboardControl != 0) return true;
            var chat = GameObject.Find("UI_Chat");
            if (chat == null) return false;
            var doc = chat.GetComponent<UnityEngine.UIElements.UIDocument>();
            if (doc == null || doc.rootVisualElement == null || doc.rootVisualElement.panel == null) return false;
            var focused = doc.rootVisualElement.panel.focusController != null
                ? doc.rootVisualElement.panel.focusController.focusedElement : null;
            return focused != null;
        }

        public bool TrySit(string entityId)
        {
            LastError = "";
            if (State != SeatState.Idle && State != SeatState.Failed)
            {
                LastStatus = "busy_" + State.ToString().ToLowerInvariant();
                LastError = "当前状态不能重复启动：" + State;
                Log("sit_rejected_busy " + State);
                return false;
            }
            if (_routine != null) return Reject("busy_routine");
            Resolve();
            if (_character == null || _motor == null || _snap == null)
                return Reject("character_or_controllers_missing");
            if (_pivot == null)
                return Reject("pivot_bone_missing");
            if (!Stage4SeatData.Loaded)
                return Reject("stage4_data_not_loaded: " + Stage4SeatData.Error);
            var entry = Stage4SeatData.Get(entityId);
            if (entry == null) return Reject("unknown_or_unavailable_target: " + entityId);
            if (!entry.usable_for_interaction || entry.geometry_status != "verified")
                return Reject("target_not_verified: " + entityId);
            if (!_motor.WalkingEnabled) return Reject("walking_disabled");
            if (_motor.HasTarget || _motor.IsTurning) return Reject("character_is_moving");
            if (!TaskAuthority.Allows(3)) return Reject("busy_resources_owned_by_another_run");

            _sitClip = _manifest != null ? _manifest.Resolve("sit") : null;
            if (string.IsNullOrEmpty(_sitClip) || _anim == null || !_anim.HasClip(_sitClip))
                return Reject("sit_action_missing:" + (_sitClip ?? "null"));

            _owner = "stage4.seat." + entityId;
            if (!TaskAuthority.Acquire(_owner, 3)) { _owner = null; return Reject("busy_acquire_failed"); }
            _entry = entry;
            CurrentEntity = entityId;
            _savedWalking = _motor.WalkingEnabled;
            _savedSnap = _snap.Enabled;
            _cancelRequested = false;
            _runId++;
            LastStatus = "started";
            State = SeatState.WalkingToApproach;
            Log("start " + entityId + " approach=" + entry.ApproachPoint + " seat=" + entry.SeatPoint);
            _routine = StartCoroutine(SeatRoutine(_runId, entry));
            return true;
        }

        public void RequestStand()
        {
            if (State != SeatState.Seated) { LastError = "当前不在坐稳状态：" + State; LastStatus = "stand_rejected"; return; }
            if (_routine != null) return;
            _runId++;
            State = SeatState.StandingUp;
            Log("stand_request " + CurrentEntity);
            _routine = StartCoroutine(StandRoutine(_runId));
        }

        public void RequestCancel()
        {
            if (State == SeatState.Idle || State == SeatState.Failed) { LastError = "当前没有进行中的坐下流程"; return; }
            _cancelRequested = true;
            if (_routine != null) { LastStatus = "cancelling"; return; }
            Finish("cancelled", true);
        }

        /// <summary>Approach point actually resolved for the current/last sit run.</summary>
        public Vector3 ResolvedApproach { get; private set; }
        public string ResolvedApproachSide { get; private set; } = "";
        public string ResolvedApproachReason { get; private set; } = "";
        /// <summary>Navigation budget derived from the real remaining path length and speed.</summary>
        public float NavigationBudgetSeconds { get; private set; }
        public float NavigationPathMeters { get; private set; }
        public float NavigationStartedAt { get; private set; }
        /// <summary>Deadline suggestion for outer wrappers (Stage-5 visual agent etc.).</summary>
        public float SuggestedSitDeadline
        {
            get
            {
                if (NavigationBudgetSeconds <= 0f) return 0f;
                // extra time for align/snap/stand checks after the walk itself
                return NavigationStartedAt + NavigationBudgetSeconds + 20f;
            }
        }

        /// <summary>
        /// Budget for the walk leg: real remaining path length / effective walk
        /// speed, with a safety factor and a floor that keeps the legacy short
        /// walks unchanged.  209 m at 1.05 m/s needs ~199 s, so the old fixed
        /// 25 s patience could never succeed.
        /// </summary>
        static float ComputeNavigationBudget(CharacterLocomotion motor, Vector3 from,
                                             Vector3 target, out float pathMeters)
        {
            pathMeters = 0f;
            var remaining = new List<Vector3>();
            motor.CopyRemainingPath(remaining);
            Vector3 previous = from;
            if (remaining.Count > 0)
            {
                for (int i = 0; i < remaining.Count; i++)
                {
                    pathMeters += Vector3.Distance(previous, remaining[i]);
                    previous = remaining[i];
                }
            }
            else
            {
                pathMeters = Vector3.Distance(from, target);
            }
            float speed = Mathf.Max(0.3f, motor.WalkSpeed);
            return Mathf.Clamp(pathMeters / speed * 1.6f + 20f, 25f, 900f);
        }

        /// <summary>A failure reset is only legal after cleanup has completed.</summary>
        public bool CanResetFailureState
        {
            get { return State == SeatState.Failed && _routine == null && string.IsNullOrEmpty(_owner); }
        }

        public bool ResetFailureState()
        {
            if (!CanResetFailureState)
            {
                LastStatus = "reset_rejected";
                LastError = "只有清理完成的失败状态可以重置";
                Log("reset_rejected state=" + State + " owner=" + (_owner ?? "null") + " routine=" + (_routine != null));
                return false;
            }
            State = SeatState.Idle;
            LastError = "";
            LastStatus = "idle";
            Log("failure_reset");
            return true;
        }

        IEnumerator SeatRoutine(int run, Stage4SeatEntry entry)
        {
            Resolve();
            if (_character == null || _motor == null || _snap == null || _pivot == null)
            {
                Fail("missing_character_reference");
                yield break;
            }
            Vector3 approach;
            string approachReason;
            if (!TryResolveApproach(entry, true, out approach, out approachReason))
            {
                Fail("invalid_approach_frame:" + approachReason);
                yield break;
            }
            ResolvedApproach = approach;
            ResolvedApproachReason = approachReason;
            ResolvedApproachSide = ExtractSide(approachReason);
            Log("approach_resolved side=" + ResolvedApproachSide + " point=" +
                approach.ToString("F2") + " reason=" + approachReason);
            if (!WorldData.IsWalkable(approach))
            {
                Fail("approach_not_walkable:" + approach);
                yield break;
            }

            State = SeatState.WalkingToApproach;
            LastStatus = "walking_to_approach";
            bool started = false;
            using (TaskAuthority.Enter(_owner))
                started = _motor.MoveTo(approach);
            if (!started)
            {
                Fail("navigate_failed:" + _motor.LastError);
                yield break;
            }

            float pathMeters;
            NavigationStartedAt = Time.realtimeSinceStartup;
            NavigationBudgetSeconds = ComputeNavigationBudget(_motor, _character.transform.position,
                                                              approach, out pathMeters);
            NavigationPathMeters = pathMeters;
            Log("navigation_budget path=" + pathMeters.ToString("F1") + "m speed=" +
                _motor.WalkSpeed.ToString("F2") + "m/s budget=" +
                NavigationBudgetSeconds.ToString("F1") + "s");
            float deadline = NavigationStartedAt + NavigationBudgetSeconds;
            while (_motor.HasTarget)
            {
                if (_cancelRequested) { Finish("cancelled", true); yield break; }
                if (Time.realtimeSinceStartup > deadline)
                {
                    Fail("navigation_timeout path=" + pathMeters.ToString("F1") +
                         "m budget=" + NavigationBudgetSeconds.ToString("F1") + "s");
                    yield break;
                }
                yield return null;
            }
            if (_cancelRequested) { Finish("cancelled", true); yield break; }
            if (_motor.LastStatus != "arrived")
            {
                Fail("navigation_" + _motor.LastStatus + ":" + _motor.LastError);
                yield break;
            }
            float flat = FlatDistance(_motor.transform.position, approach);
            if (flat > 0.65f) { Fail("approach_distance_" + flat.ToString("F2")); yield break; }
            Log("arrived approach " + approach.ToString("F2") + " status=" + _motor.LastStatus);

            State = SeatState.Aligning;
            LastStatus = "turning_to_seat";
            float startYaw = _character.transform.eulerAngles.y;
            float targetYaw = entry.SeatYaw;
            float alignStart = Time.realtimeSinceStartup;
            const float alignTime = 0.28f;
            while (Time.realtimeSinceStartup - alignStart < alignTime)
            {
                if (_cancelRequested) { Finish("cancelled", true); yield break; }
                float t = Mathf.Clamp01((Time.realtimeSinceStartup - alignStart) / alignTime);
                float yaw = Mathf.LerpAngle(startYaw, targetYaw, Smooth(t));
                _character.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                yield return null;
            }
            if (_cancelRequested || run != _runId) { Finish("cancelled", true); yield break; }
            _character.transform.rotation = Quaternion.Euler(0f, targetYaw, 0f);
            Log("aligned yaw=" + targetYaw.ToString("F1"));

            if (!_savedSnap && _snap.Enabled) { /* already off before */ }
            _snap.Enabled = false;
            State = SeatState.Snapping;
            LastStatus = "playing_sit_and_snapping";
            using (TaskAuthority.Enter(_owner)) _character.PlayGesture("sit");
            yield return null;
            yield return null;
            if (_cancelRequested) { Finish("cancelled", true); yield break; }
            if (_anim == null || _anim.CurrentName != _sitClip)
            {
                Fail("sit_clip_not_played:" + (_anim != null ? _anim.CurrentName : "null"));
                yield break;
            }

            // Let the sit clip settle, then align the character's Center bone to
            // the seat surface. This is the single calibrated character/action offset.
            yield return new WaitForSeconds(0.18f);
            if (_cancelRequested) { Finish("cancelled", true); yield break; }
            Vector3 seatForward = Quaternion.Euler(0f, entry.SeatYaw, 0f) * Vector3.forward;
            Vector3 desiredCenter = entry.SeatPoint + Vector3.up * _hipsAboveSeat + seatForward * _forwardOffset;
            Vector3 beforePivot = _pivot.position;
            Vector3 startRoot = _character.transform.position;
            Vector3 targetRoot = startRoot + (desiredCenter - beforePivot);
            Quaternion startRotation = _character.transform.rotation;
            Quaternion targetRotation = Quaternion.Euler(0f, entry.SeatYaw, 0f);
            float snapStart = Time.realtimeSinceStartup;
            float snapTime = Mathf.Max(0.05f, Stage4SeatData.Calibration != null ? Stage4SeatData.Calibration.snap_seconds : 0.35f);
            while (Time.realtimeSinceStartup - snapStart < snapTime)
            {
                if (_cancelRequested) { Finish("cancelled", true); yield break; }
                float t = Smooth(Mathf.Clamp01((Time.realtimeSinceStartup - snapStart) / snapTime));
                _character.transform.position = Vector3.Lerp(startRoot, targetRoot, t);
                _character.transform.rotation = Quaternion.Slerp(startRotation, targetRotation, t);
                yield return null;
            }
            if (_cancelRequested || run != _runId) { Finish("cancelled", true); yield break; }
            _character.transform.position = targetRoot;
            _character.transform.rotation = targetRotation;
            yield return null;
            if (_cancelRequested || run != _runId) { Finish("cancelled", true); yield break; }
            float pivotError = Vector3.Distance(_pivot.position, desiredCenter);
            if (pivotError > 0.2f)
            {
                Fail("seat_calibration_error_" + pivotError.ToString("F3"));
                yield break;
            }
            _motor.WalkingEnabled = false;
            State = SeatState.Seated;
            LastStatus = "seated";
            _routine = null;
            Log("seated " + entry.entity_id + " pivot_error=" + pivotError.ToString("F3") + " root=" + targetRoot.ToString("F2"));
        }

        IEnumerator StandRoutine(int run)
        {
            Resolve();
            State = SeatState.StandingUp;
            LastStatus = "standing";
            Vector3 approach;
            string approachReason;
            if (!TryResolveApproach(_entry, false, out approach, out approachReason))
            {
                Finish("stood", true);
                yield break;
            }
            Vector3 start = _character.transform.position;
            Quaternion startRot = _character.transform.rotation;
            Quaternion targetRot = Quaternion.Euler(0f, _entry != null ? _entry.ApproachYaw : 0f, 0f);
            using (TaskAuthority.Enter(_owner)) _character.PlayGesture("idle");
            float t0 = Time.realtimeSinceStartup;
            const float standTime = 0.3f;
            while (Time.realtimeSinceStartup - t0 < standTime)
            {
                if (_cancelRequested || run != _runId) { Finish("cancelled", true); yield break; }
                float t = Smooth(Mathf.Clamp01((Time.realtimeSinceStartup - t0) / standTime));
                _character.transform.position = Vector3.Lerp(start, approach, t);
                _character.transform.rotation = Quaternion.Slerp(startRot, targetRot, t);
                yield return null;
            }
            if (_cancelRequested || run != _runId) { Finish("cancelled", true); yield break; }
            _character.transform.position = approach;
            _character.transform.rotation = targetRot;
            yield return null;
            if (_cancelRequested || run != _runId) { Finish("cancelled", true); yield break; }
            _snap.Enabled = true;
            _snap.SnapNow();
            _motor.WalkingEnabled = _savedWalking;
            Finish("stood", true);
        }

        void Fail(string reason)
        {
            LastError = reason;
            LastStatus = "failed";
            Log("failed " + reason);
            Finish("failed", true);
        }

        bool Reject(string reason)
        {
            LastError = reason;
            LastStatus = "rejected";
            Log("rejected " + reason);
            return false;
        }

        void Finish(string status, bool restore)
        {
            _cancelRequested = false;
            if (_routine != null) { StopCoroutine(_routine); _routine = null; }
            Resolve();
            if (_motor != null)
            {
                using (TaskAuthority.Enter(_owner))
                {
                    _motor.Stop("Stage-4 " + status, status == "failed" ? "failed" : "cancelled");
                    _motor.WalkingEnabled = _savedWalking;
                }
            }
            if (_snap != null && restore && _character != null)
            {
                bool needSafe = _entry != null &&
                    (State == SeatState.Seated || State == SeatState.Snapping || State == SeatState.Aligning || State == SeatState.StandingUp);
                if (needSafe)
                {
                    Vector3 safe;
                    string reasonSafe;
                    if (!TryResolveApproach(_entry, false, out safe, out reasonSafe))
                        safe = _entry.ApproachPoint;
                    safe.y = WorldData.GroundHeightAt(safe);
                    _character.transform.position = safe;
                }
                _snap.Enabled = _savedSnap;
                if (_savedSnap) _snap.SnapNow();
            }
            if (_character != null && status != "stood")
            {
                using (TaskAuthority.Enter(_owner)) _character.PlayGesture("idle");
            }
            _motor.WalkingEnabled = _savedWalking;
            LastStatus = status;
            if (status != "failed") LastError = "";
            if (status == "failed") State = SeatState.Failed;
            else State = SeatState.Idle;
            CurrentEntity = "";
            TaskAuthority.Release(_owner);
            _owner = null;
            _entry = null;
            Log("finish " + status + " state=" + State);
        }

        void OnDisable()
        {
            if (!Application.isPlaying)
            {
                State = SeatState.Idle;
                return;
            }
            // Reuse the normal cancellation cleanup so disabling the component while
            // seated also returns to the approach, restores GroundSnap/WalkingEnabled
            // and releases the TaskAuthority. Merely flipping State here would strand
            // the character in the seated pose with no way to stand/cancel.
            if (!string.IsNullOrEmpty(_owner)) Finish("disabled", true);
            else
            {
                if (_routine != null) { StopCoroutine(_routine); _routine = null; }
                State = SeatState.Idle;
            }
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = b.y = 0f;
            return Vector3.Distance(a, b);
        }

        /// <summary>
        /// Stage-3 approach is exactly AgentRadius+Margin outside the bench bbox.
        /// Navigation expands the bench by the same amount on each side, so the
        /// raw point would sit on the collision boundary. Add the small configured
        /// padding outward (away from the seat) and sample ground height.
        /// </summary>
        /// <summary>
        /// Resolve a walkable (and optionally path-connected) approach point for
        /// one bench. The universal seat package carries front/right/left/back
        /// candidates; the front one wins when it is usable, otherwise a side is
        /// used instead of declaring the bench unreachable.
        /// </summary>
        bool TryResolveApproach(Stage4SeatEntry entry, bool requirePath, out Vector3 approach,
                                out string reason)
        {
            if (Stage4SeatData.ResolveApproach(entry, _character != null ? _character.transform.position : Vector3.zero,
                                               requirePath, out approach, out reason))
            {
                if (string.IsNullOrEmpty(reason)) reason = "approach";
                return true;
            }
            return false;
        }

        static string ExtractSide(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "";
            int index = reason.IndexOf("side=", StringComparison.Ordinal);
            if (index < 0) return "";
            int start = index + 5;
            int end = reason.IndexOf(' ', start);
            return end < 0 ? reason.Substring(start) : reason.Substring(start, end - start);
        }

        static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

        void Log(string message)
        {
            string line = Time.realtimeSinceStartup.ToString("F2") + " | " + message;
            Events.Add(line);
            if (Events.Count > 200) Events.RemoveAt(0);
            Debug.Log("[Stage4Seat] " + message);
            if (Application.isEditor && !string.IsNullOrEmpty(RunLogPath))
            {
                try
                {
                    var payload = new System.Text.StringBuilder();
                    payload.Append("{\"t\":").Append(Time.realtimeSinceStartup.ToString("F3"))
                        .Append(",\"state\":\"").Append(State).Append("\"")
                        .Append(",\"status\":\"").Append(LastStatus ?? "").Append("\"")
                        .Append(",\"entity\":\"").Append(CurrentEntity ?? "").Append("\"")
                        .Append(",\"error\":\"").Append((LastError ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"")
                        .Append(",\"message\":\"").Append(message.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"");
                    if (_character != null)
                    {
                        payload.Append(",\"root\":[").Append(_character.transform.position.x.ToString("F3")).Append(',')
                            .Append(_character.transform.position.y.ToString("F3")).Append(',')
                            .Append(_character.transform.position.z.ToString("F3")).Append(']');
                        if (_character.Center != null)
                            payload.Append(",\"center\":[").Append(_character.Center.position.x.ToString("F3")).Append(',')
                                .Append(_character.Center.position.y.ToString("F3")).Append(',')
                                .Append(_character.Center.position.z.ToString("F3")).Append(']');
                    }
                    payload.Append('}');
                    File.AppendAllText(RunLogPath, payload.ToString() + "\n");
                }
                catch (Exception e) { Debug.LogWarning("[Stage4Seat] 运行日志写入失败：" + e.Message); }
            }
        }

        void OnGUI()
        {
            if (!DebugOpen) return;
            int previousDepth = GUI.depth;
            GUI.depth = -300;
            const float w = 390f;
            float x = Mathf.Min(1100f, Mathf.Max(20f, Screen.width - w - 20f));
            float y = 80f;
            GUI.Box(new Rect(x, y, w, 430f), "Stage 4 · 通用坐下调试（F11 关闭）");
            y += 30f;
            GUI.Label(new Rect(x + 10, y, w - 20, 22), "状态: " + State + "  " + LastStatus);
            y += 22;
            GUI.Label(new Rect(x + 10, y, w - 20, 22), "目标: " + (string.IsNullOrEmpty(CurrentEntity) ? "-" : CurrentEntity));
            y += 22;
            GUI.Label(new Rect(x + 10, y, w - 20, 44), "错误: " + (string.IsNullOrEmpty(LastError) ? "-" : LastError));
            y += 48;
            if (Stage4SeatData.Loaded)
            {
                foreach (var entry in Stage4SeatData.Entries)
                {
                    if (GUI.Button(new Rect(x + 10, y, w - 20, 30), "坐下 → " + entry.entity_id))
                        TrySit(entry.entity_id);
                    y += 34;
                }
            }
            else GUI.Label(new Rect(x + 10, y, w - 20, 22), "数据未载入: " + Stage4SeatData.Error);
            y += 10;
            if (GUI.Button(new Rect(x + 10, y, 110, 30), "起身")) RequestStand();
            if (GUI.Button(new Rect(x + 126, y, 110, 30), "取消")) RequestCancel();
            bool previousEnabled = GUI.enabled;
            GUI.enabled = CanResetFailureState;
            if (GUI.Button(new Rect(x + 242, y, 128, 30), "重置失败态")) ResetFailureState();
            GUI.enabled = previousEnabled;
            y += 40;
            GUI.Label(new Rect(x + 10, y, w - 20, 22), "hips_above_seat=" + _hipsAboveSeat.ToString("F3") + "  forward=" + _forwardOffset.ToString("F3"));
            y += 26;
            if (GUI.Button(new Rect(x + 10, y, 86, 26), " hips -0.02")) _hipsAboveSeat = Mathf.Max(-0.3f, _hipsAboveSeat - 0.02f);
            if (GUI.Button(new Rect(x + 100, y, 86, 26), " hips +0.02")) _hipsAboveSeat = Mathf.Min(0.3f, _hipsAboveSeat + 0.02f);
            if (GUI.Button(new Rect(x + 190, y, 86, 26), " fwd -0.02")) _forwardOffset -= 0.02f;
            if (GUI.Button(new Rect(x + 280, y, 86, 26), " fwd +0.02")) _forwardOffset += 0.02f;
            y += 34;
            GUI.Label(new Rect(x + 10, y, w - 20, 22), "F11 只用于本调试面板；F12 仍是阶段 2 拾取。");
            GUI.Label(new Rect(x + 10, y + 22, w - 20, 22), "数据: " + (Stage4SeatData.Loaded ? "loaded" : "error") + " · " + Stage4SeatData.LoadLog);
            GUI.depth = previousDepth;
        }
    }
}
