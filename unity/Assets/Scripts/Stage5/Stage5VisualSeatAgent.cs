using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Yixuan.AI;
using Yixuan.AI.Tasks;
using Yixuan.Core;
using Yixuan.Motion;
using Yixuan.Stage2;
using Yixuan.Stage4;
using Yixuan.Vision;
using Yixuan.World;

namespace Yixuan.Stage5
{
    /// <summary>
    /// Stage-5 first version: DeepSeek image localization -> real EyeGimbal aim ->
    /// new-frame center confirmation -> Stage-4 sitting. It is deliberately fed
    /// from ChatSession's normal text entry and only knows the two verified benches.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Stage5VisualSeatAgent : MonoBehaviour
    {
        public enum TaskState { Idle, Capturing, Locating, Aiming, Confirming, WalkingToSeat, Seated, Standing, Failed, Cancelled }

        public static Stage5VisualSeatAgent Instance { get; private set; }

        [Header("Runtime")]
        public bool ShowReticle = true;
        public int CaptureWidth = 960;
        public int CaptureHeight = 540;
        public int MaxVisionAttempts = 3;
        public float SitTimeoutSeconds = 120f;
        public float AimTimeoutSeconds = 8f;
        public float NearbySearchRadiusMeters = 30f;

        public TaskState State { get; private set; } = TaskState.Idle;
        public bool IsActive { get; private set; }
        public int RunGeneration { get { return _generation; } }
        bool _seated;
        public bool IsSeated { get { return _seated; } }
        public string LastError { get; private set; } = "";
        public string LastStatus { get; private set; } = "idle";
        string _lastSelectedEntity = "";
        public string LastSelectedEntity { get { return _lastSelectedEntity; } }
        public string RunDirectory { get; private set; } = "";
        public string LastReply { get; private set; } = "";
        public string CurrentPhase { get { return State.ToString(); } }

        Stage5EyeCapture _capture;
        Stage5Intent _pendingIntent;
        int _queuedToken;
        ChatSession _session;
        Stage4SeatController _seat;
        EyeGimbalController _gaze;
        CharacterLocomotion _motor;
        YixuanCharacter _character;
        Yixuan.Presentation.CameraDirector _director;
        bool _eyeViewOverridden;
        bool _savedEyeView;
        bool _savedThirdPerson;
        AIConfig _config;
        Stage2SceneData _stage2;
        Coroutine _routine;
        string _owner;

        sealed class Stage5Run
        {
            public int Generation;
            public string Owner;
            public bool Stage4Active;
            public string NavigationAfterStand;
        }

        Stage5Run _task;
        int _generation;
        bool _cancelRequested;
        bool _stage4Active;
        bool _keepReticle;
        string _aimError;
        Stage5EyeFrame _lastFrame;
        Stage5VisionResult _lastVision;
        Stage2PickResult _lastPick;
        Stage2PickResult _lastConfirm;
        string _frozenCandidateId;
        string _frozenReason;
        Transform _frozenCharacter;
        Vector3 _frozenPosition;
        float _frozenYaw;
        string _lastFeedback = "";
        int _requestSerial;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBootstrap()
        {
            try
            {
                if (FindObjectOfType<Stage5VisualSeatAgent>() != null) return;
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (scene.name != null && scene.name.IndexOf("SovietBlueHour", StringComparison.OrdinalIgnoreCase) < 0 &&
                    FindObjectOfType<Yixuan.World.MapWorldProfile>() == null) return;
                var go = new GameObject("Stage5VisualSeatAgent");
                go.AddComponent<Stage5VisualSeatAgent>();
            }
            catch (Exception e) { Debug.LogWarning("[Stage5] bootstrap failed: " + e.Message); }
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Resolve();
            _capture = GetComponent<Stage5EyeCapture>();
            if (_capture == null) _capture = gameObject.AddComponent<Stage5EyeCapture>();
            _capture.Width = CaptureWidth;
            _capture.Height = CaptureHeight;
        }

        void OnDestroy()
        {
            Cancel("component_destroyed");
            SetEyePreview(false);
            if (Instance == this) Instance = null;
        }

        void OnDisable()
        {
            if (!Application.isPlaying) return;
            _generation++;
            if (IsActive)
            {
                var task = _task;
                if (task != null && task.Stage4Active && _seat != null) _seat.RequestCancel();
                if (_seat != null && _seat.State != Stage4SeatController.SeatState.Idle) _seat.RequestCancel();
                if (task != null && !string.IsNullOrEmpty(task.Owner)) TaskAuthority.Release(task.Owner);
                ReleaseOwner();
                // The component is being disabled: stop the actual iterator, not
                // just the handle. Otherwise re-enabling lets the old coroutine
                // resume and write into a new task.
                StopAllCoroutines();
                _routine = null;
                _task = null;
                _stage4Active = false;
                _pendingIntent = null;
                _queuedToken++;
                IsActive = false;
                State = TaskState.Cancelled;
                LastStatus = "cancelled";
                LastError = "component_disabled";
                SetEyePreview(false);
                RestoreIdleIfFree();
            }
            else
            {
                if (_pendingIntent != null) ClearQueuedIntent("component_disabled");
                SetEyePreview(false);
            }
        }

        void Resolve()
        {
            if (_session == null) _session = FindObjectOfType<ChatSession>();
            if (_seat == null) _seat = FindObjectOfType<Stage4SeatController>();
            if (_gaze == null) _gaze = FindObjectOfType<EyeGimbalController>();
            if (_motor == null) _motor = FindObjectOfType<CharacterLocomotion>();
            if (_character == null) _character = FindObjectOfType<YixuanCharacter>();
            if (_character == null && _motor != null) _character = _motor.GetComponent<YixuanCharacter>();
            if (_director == null) _director = FindObjectOfType<Yixuan.Presentation.CameraDirector>();
            if (_config == null && _session != null) _config = _session.Config;
            if (_config == null) _config = AIConfig.FindDefault();
            if (_capture == null) _capture = GetComponent<Stage5EyeCapture>();
            if (_capture == null) _capture = gameObject.AddComponent<Stage5EyeCapture>();
            _capture.Width = CaptureWidth;
            _capture.Height = CaptureHeight;
        }

        // ------------------------------------------------------------
        // Chat entry
        // ------------------------------------------------------------

        public static bool TryHandleChat(string text, ChatSession session)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var agent = FindOrCreateInstance();
            if (agent == null) return false;
            return agent.HandleChat(text, session);
        }

        static Stage5VisualSeatAgent FindOrCreateInstance()
        {
            if (Instance != null)
            {
                var extras = FindObjectsOfType<Stage5VisualSeatAgent>();
                for (int i = 0; i < extras.Length; i++)
                    if (extras[i] != null && extras[i] != Instance) Destroy(extras[i].gameObject);
                return Instance;
            }
            var existing = FindObjectsOfType<Stage5VisualSeatAgent>();
            if (existing != null && existing.Length > 0)
            {
                Instance = existing[0];
                for (int i = 1; i < existing.Length; i++)
                    if (existing[i] != null) Destroy(existing[i].gameObject);
                return Instance;
            }
            try
            {
                var go = new GameObject("Stage5VisualSeatAgent");
                go.AddComponent<Stage5VisualSeatAgent>();
            }
            catch (Exception e) { Debug.LogWarning("[Stage5] lazy bootstrap failed: " + e.Message); return null; }
            return Instance;
        }

        /// <summary>Chat turn cancellation; never auto-stands a successfully seated character.</summary>
        public static void NotifyChatCancelled(string reason)
        {
            if (Instance != null && Instance.IsActive) Instance.Cancel(reason);
        }

        public bool HandleChat(string text, ChatSession session)
        {
            if (session != null) _session = session;
            Resolve();
            var intent = Stage5IntentParser.Parse(text);
            if (intent.Kind == Stage5IntentKind.None)
            {
                string anchor, label, navigationError;
                if (_seat != null && _seat.State == Stage4SeatController.SeatState.Seated &&
                    _session != null && _session.TaskPlanningEnabled && _session.InputMode == TaskInputMode.Auto &&
                    NamedAnchorCommand.TryResolve(text, out anchor, out label, out navigationError) && navigationError == null)
                {
                    StandFromChat(text);
                    return true;
                }
                // A new normal-chat turn invalidates both an in-flight visual action
                // and any replacement command waiting for the next frame.
                if (IsActive) Cancel("new_normal_chat_turn");
                if (_pendingIntent != null) ClearQueuedIntent("new_normal_chat_turn");
                return false;
            }

            return HandleIntent(intent);
        }

        public static bool TryHandleIntent(Stage5Intent intent, ChatSession session)
        {
            if (intent == null || intent.Kind == Stage5IntentKind.None) return false;
            var agent = FindOrCreateInstance();
            if (agent == null || !agent.isActiveAndEnabled) return false;
            if (session != null) agent._session = session;
            agent.Resolve();
            return agent.HandleIntent(intent);
        }

        bool HandleIntent(Stage5Intent intent)
        {
            string text = intent.Original ?? "";

            if (intent.Kind == Stage5IntentKind.Unsupported)
            {
                if (IsActive) Cancel("new_unsupported_request");
                if (_pendingIntent != null) ClearQueuedIntent("new_unsupported_request");
                Reply("这句话包含多个动作，第一版只支持单个“看椅子/坐下/起身/取消”指令；我没有只执行其中一段。");
                return true;
            }

            if (intent.Kind == Stage5IntentKind.Stand)
            {
                if (IsActive) Cancel("new_stand_request");
                if (_pendingIntent != null) ClearQueuedIntent("new_stand_request");
                StandFromChat();
                return true;
            }
            if (intent.Kind == Stage5IntentKind.Cancel)
            {
                // Only claim stop/取消 when Stage-5 owns the running task, a
                // queued replacement, or the seat. Normal navigation/legacy task
                // stops must fall through to ChatSession's original stop route.
                if (IsActive) { Cancel("explicit_cancel"); return true; }
                if (_pendingIntent != null) { CancelQueuedIntent("explicit_cancel", true); return true; }
                if (_seat != null && _seat.State == Stage4SeatController.SeatState.Seated) { StandFromChat(); return true; }
                return false;
            }

            if (_seat != null && _seat.State == Stage4SeatController.SeatState.Seated)
            {
                Reply("我已经坐下了；要起来请说“起身”。");
                return true;
            }

            if (IsActive)
            {
                _pendingIntent = intent;
                _queuedToken++;
                Cancel("replaced_by_new_task");
                return true;
            }
            StartTask(intent, text);
            return true;
        }

        void StartTask(Stage5Intent intent, string original)
        {
            Resolve();
            if (_seat == null || _seat.State == Stage4SeatController.SeatState.Seated) { Reply("当前座位状态不可用。"); return; }
            if (!Stage4SeatData.Loaded) { Reply("座位数据没有载入：" + (Stage4SeatData.Error ?? "")); return; }
            if (!Stage5SeatRegistry.Ready) { Reply("座位逻辑实体没有就绪。"); return; }
            if (_session == null || _config == null || !_config.HasApiKey) { Reply("角色核心 DeepSeek 图片配置/Key 不可用。"); return; }
            if (_capture == null || !_capture.Resolve()) { Reply("真实 EyeCamera 不可用，无法做第一人称确认。"); return; }
            if (_gaze == null || _character == null) { Reply("视线/角色控制器不可用。"); return; }
            _stage2 = Stage2PickDataLoader.Load();
            if (_stage2 == null || !_stage2.Valid) { Reply("阶段 2 拾取数据不可用：" + (_stage2 != null ? _stage2.Error : "null")); return; }

            _generation++;
            _requestSerial = 0;
            _cancelRequested = false;
            _pendingIntent = null;
            _queuedToken++;
            _stage4Active = false;
            _keepReticle = true;
            _lastFeedback = "";
            _lastSelectedEntity = "";
            _frozenCandidateId = "";
            _frozenReason = "";
            _frozenCharacter = _character != null ? _character.transform : null;
            if (_frozenCharacter != null)
            {
                _frozenPosition = _frozenCharacter.position;
                _frozenYaw = _frozenCharacter.eulerAngles.y;
            }
            _owner = "stage5.visual-seat." + _generation + "." + Guid.NewGuid().ToString("N").Substring(0, 6);
            _task = new Stage5Run { Generation = _generation, Owner = _owner };
            LastError = "";
            LastStatus = "starting";
            State = TaskState.Capturing;
            IsActive = true;
            EnsureRunDirectory();
            SetEyePreview(true);
            LogEvent("task_start", "intent=" + intent.Kind + ";dir=" + intent.Direction + ";text=" + Clean(original));
            Ack(intent.Kind == Stage5IntentKind.LookOnly ? "好，我先看看，再确认位置；不会过去。" :
                "好，我先用第一人称画面找椅子，确认后再过去。");
            _routine = StartCoroutine(RunTaskOwned(_task, intent));
        }

        IEnumerator RunTaskOwned(Stage5Run task, Stage5Intent intent)
        {
            yield return RunTask(task != null ? task.Generation : _generation, intent);
            if (task != null && task == _task && task.Generation == _generation)
            {
                _routine = null;
                _task = null;
            }
        }

        void Ack(string text)
        {
            if (_session != null && _session.UI != null)
            {
                _session.UI.SetReply(text);
                _session.UI.SetStatus("视觉确认中…");
            }
        }

        /// <summary>
        /// Show the real EyeCamera as the main view while Stage-5 is evaluating
        /// the scene. This is the actual camera, not a test camera or a static
        /// screenshot; the previous CameraDirector mode is restored on finish.
        /// </summary>
        void SetEyePreview(bool on)
        {
            if (_director == null) _director = FindObjectOfType<Yixuan.Presentation.CameraDirector>();
            if (_director == null) return;
            if (on)
            {
                if (_eyeViewOverridden) return;
                _savedEyeView = _director.EyeView;
                _savedThirdPerson = _director.ThirdPerson;
                _director.EyeView = true;
                _director.ThirdPerson = false;
                _eyeViewOverridden = true;
            }
            else if (_eyeViewOverridden)
            {
                _director.EyeView = _savedEyeView;
                _director.ThirdPerson = _savedThirdPerson;
                _eyeViewOverridden = false;
            }
        }

        void Reply(string text)
        {
            LastReply = text ?? "";
            if (_session != null) _session.MissionFinished(LastReply, false);
            else Debug.Log("[Stage5] " + LastReply);
        }

        void StandFromChat(string navigationAfterStand = null)
        {
            Resolve();
            if (_seat == null) { Reply("座位控制器不可用。"); return; }
            if (_seat.State != Stage4SeatController.SeatState.Seated)
            {
                Reply(_seat.State == Stage4SeatController.SeatState.Failed ? "当前座位控制器处于失败状态，请先重置。" : "现在没有处于坐姿。");
                return;
            }
            _generation++;
            _cancelRequested = false;
            _keepReticle = false;
            State = TaskState.Standing;
            IsActive = true;
            _task = new Stage5Run { Generation = _generation, Owner = null, NavigationAfterStand = navigationAfterStand };
            if (!string.IsNullOrEmpty(navigationAfterStand))
                LogEvent("navigation_waiting_for_stand", "request=" + Clean(navigationAfterStand));
            _routine = StartCoroutine(StandRoutineOwned(_task));
        }

        IEnumerator StandRoutineOwned(Stage5Run task)
        {
            yield return StandRoutine(task);
            if (task != null && task == _task)
            {
                _routine = null;
                _task = null;
            }
        }

        IEnumerator StandRoutine(Stage5Run task)
        {
            int gen = task != null ? task.Generation : _generation;
            _seat.RequestStand();
            float deadline = Time.realtimeSinceStartup + 12f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (task != _task || gen != _generation) { CleanupStandAfterCancel(task); yield break; }
                if (_seat.State != Stage4SeatController.SeatState.Seated &&
                    _seat.State != Stage4SeatController.SeatState.StandingUp) break;
                yield return null;
            }
            yield return null;
            if (task != _task || gen != _generation) { CleanupStandAfterCancel(task); yield break; }
            bool ok = _seat.State == Stage4SeatController.SeatState.Idle;
            if (ok && task != null && !string.IsNullOrEmpty(task.NavigationAfterStand))
            {
                yield return null;
                if (task != _task || gen != _generation || _cancelRequested || !isActiveAndEnabled)
                { CleanupStandAfterCancel(task); yield break; }
                string request = task.NavigationAfterStand;
                IsActive = false;
                _seated = false;
                State = TaskState.Idle;
                LastStatus = "stood";
                LastError = "";
                _stage4Active = false;
                _task = null;
                _routine = null;
                SetEyePreview(false);
                LogEvent("navigation_after_stand", "request=" + Clean(request));
                if (_session != null) _session.SubmitFromUI(request);
                yield break;
            }
            IsActive = false;
            _seated = false;
            SetEyePreview(false);
            State = ok ? TaskState.Idle : TaskState.Failed;
            LastStatus = ok ? "stood" : "stand_failed";
            LastError = ok ? "" : (_seat.LastError ?? "stand_not_completed");
            LogEvent("task_end", "stand;status=" + LastStatus + ";error=" + Clean(LastError));
            Reply(ok ? "已经起身了，可以继续普通走路了。" : "起身没有完成：" + LastError);
        }

        void CleanupStandAfterCancel(Stage5Run task)
        {
            if (task == null || task != _task) return; // stale stand coroutine: no global writes
            _routine = null;
            _task = null;
            if (_seat != null && _seat.State != Stage4SeatController.SeatState.Idle)
                _seat.RequestCancel();
            _stage4Active = false;
            _pendingIntent = null;
            _queuedToken++;
            IsActive = false;
            _seated = false;
            SetEyePreview(false);
            RestoreIdleIfFree();
            State = TaskState.Cancelled;
            LastStatus = "cancelled";
            LastError = "stand_cancelled";
            LogEvent("task_end", "cancelled_during_stand");
        }

        // ------------------------------------------------------------
        // Main task
        // ------------------------------------------------------------

        IEnumerator RunTask(int gen, Stage5Intent intent)
        {
            Stage5Run task = _task;
            if (!TaskAuthority.Acquire(_owner, 1 | 2))
            {
                Fail("resources_owned_by_another_run", "当前有其他任务占用移动/视线，暂时不能执行。");
                yield break;
            }
            LogEvent("authority_acquired", "owner=" + _owner + ";mask=3");
            // New visual task starts from a known body-relative view. The eye
            // gimbal intentionally does not auto-recenter after previous looks, so
            // reset the real gimbal before the first screenshot (body is unchanged).
            using (TaskAuthority.Enter(_owner))
            {
                if (_gaze != null) _gaze.Center(true);
            }
            yield return null;

            if (gen != _generation || _cancelRequested) { yield return CancelCleanup(task); yield break; }

            bool nearby = intent.Kind == Stage5IntentKind.SitNearby ||
                          (intent.Kind == Stage5IntentKind.LookOnly && intent.Direction == "nearby");
            if (nearby)
            {
                yield return ResolveNearbyCandidate(gen);
                if (gen != _generation || _cancelRequested) { yield return CancelCleanup(task); yield break; }
                if (string.IsNullOrEmpty(_frozenCandidateId))
                {
                    Fail("nearby_candidate_" + _frozenReason,
                        "附近没有确认到可用座位：" + _frozenReason + "。目前只有两张广场长椅接通坐下；需要靠近且无遮挡，视觉确认后才会过去。");
                    yield break;
                }
            }

            if (intent.Kind == Stage5IntentKind.SitBehind || (intent.Kind == Stage5IntentKind.LookOnly && intent.Direction == "behind"))
            {
                yield return ResolveBehindCandidate(gen, intent);
                if (gen != _generation || _cancelRequested) { yield return CancelCleanup(task); yield break; }
            }

            if (_cancelRequested) { yield return CancelCleanup(task); yield break; }
            if (!string.IsNullOrEmpty(_frozenReason) && _frozenCandidateId == "")
            {
                Fail("behind_candidate_" + _frozenReason, "身后目标不明确：" + _frozenReason);
                yield break;
            }

            Stage5SeatCandidate candidate = null;
            Stage5EyeFrame locateFrame = null;
            int attempts = Mathf.Max(1, MaxVisionAttempts);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (gen != _generation || _cancelRequested) { yield return CancelCleanup(task); yield break; }
                State = TaskState.Capturing;
                locateFrame = _capture.Capture();
                if (locateFrame == null) { Fail("eye_capture_failed", "第一人称截图失败：" + _capture.LastError); yield break; }
                _capture.SaveFrame(locateFrame, Path.Combine(RunDirectory, "raw"), "locate_" + attempt);
                _lastFrame = locateFrame;
                LogEvent("frame_captured", "phase=locate;attempt=" + attempt + ";jpeg=" + locateFrame.JpegSha256 +
                         ";frame=" + locateFrame.FrameId + ";pos=" + VectorText(locateFrame.Snapshot.Position) +
                         ";rot=" + QuatText(locateFrame.Snapshot.Rotation));
                State = TaskState.Locating;
                string instruction = BuildInstruction(intent, _lastFeedback);
                byte[] visionImage = locateFrame.JpegBytes;
                int visionWidth = locateFrame.Width, visionHeight = locateFrame.Height;
                Rect visionCrop = new Rect(0f, 0f, 1f, 1f);
                if (nearby && attempt > 0)
                {
                    visionImage = Stage5EyeCapture.CenterCropJpeg(locateFrame, attempt == 1 ? 1f / 3f : 1f / 6f,
                        out visionCrop, out visionWidth, out visionHeight);
                    instruction += " 这张图是刚才画面中心区域的原始像素裁剪；请在此裁剪图内返回坐标。";
                }
                string visionHash = Stage5EyeCapture.Sha256(visionImage);
                SaveVisionInput(locateFrame, attempt, visionImage, visionWidth, visionHeight, visionHash, visionCrop);
                Stage5VisionResult vision = null;
                _lastVision = null;
                if (Stage5TestHooks.Active)
                {
                    vision = Stage5TestHooks.Invoke(instruction, attempt);
                    LogEvent("vision_injected", "test_hook;attempt=" + attempt + ";calls=" + Stage5TestHooks.Calls);
                }
                else
                {
                    yield return Stage5VisionClient.Locate(this, _config, instruction, visionImage,
                        visionWidth, visionHeight, visionHash,
                        () => gen != _generation || _cancelRequested, r => vision = r);
                }
                if (gen != _generation || _cancelRequested) { yield return CancelCleanup(task); yield break; }
                _lastVision = vision;
                if (vision == null) { _lastFeedback = "图片请求没有返回"; LogEvent("vision_failed", _lastFeedback); continue; }
                SaveVisionResult(vision, "locate_" + attempt);
                if (!vision.Ok)
                {
                    _lastFeedback = "图片定位请求失败：" + vision.Error;
                    LogEvent("vision_failed", Clean(_lastFeedback));
                    continue;
                }
                var reply = vision.Reply;
                string validation = ValidateLocation(reply);
                if (validation != null)
                {
                    _lastFeedback = "模型输出无效：" + validation;
                    LogEvent("vision_invalid", Clean(_lastFeedback) + ";raw=" + Clean(vision.Raw));
                    continue;
                }
                if (reply.status != "located")
                {
                    _lastFeedback = "模型没有确认目标：status=" + reply.status + " " + (reply.description ?? "");
                    LogEvent("vision_not_located", Clean(_lastFeedback));
                    continue;
                }
                reply = MapLocationToFrame(reply, visionCrop);
                LogEvent("vision_uv_mapped", "image=" + visionHash + ";crop=" + UvText(new[] { visionCrop.xMin, visionCrop.yMin, visionCrop.xMax, visionCrop.yMax }) +
                         ";frame_uv=" + reply.point_uv[0].ToString("F4") + "," + reply.point_uv[1].ToString("F4") +
                         ";frame_bbox=" + UvText(reply.bbox_uv));
                var pick = PickAtUv(locateFrame, reply.point_uv[0], reply.point_uv[1]);
                _lastPick = pick;
                LogEvent("pick_original", "status=" + pick.Status + ";sample=" + (pick.SampleId ?? "") +
                         ";uv=" + reply.point_uv[0].ToString("F3") + "," + reply.point_uv[1].ToString("F3") +
                         ";snapshot=" + locateFrame.Snapshot.SnapshotId + ";hit=" + VectorText(pick.HitPoint) +
                         ";occluder=" + (pick.OccluderId ?? ""));
                if (pick.Status == Stage2PickStatus.NoTarget && reply.bbox_uv != null && reply.bbox_uv.Length == 4)
                {
                    // The model's single point can land in a slat gap or just off the
                    // visible plank while its bbox still covers the bench. Refine only
                    // inside the model-provided bbox; collect all identities first so
                    // a box spanning two benches is reported as ambiguous, never
                    // silently resolved by scan order.
                    var refined = PickInsideBbox(locateFrame, reply.bbox_uv);
                    if (refined != null && refined.CandidateIds.Count > 1)
                    {
                        _lastFeedback = "模型框内命中多个实体：" + string.Join(",", refined.CandidateIds.ToArray());
                        LogEvent("pick_bbox_ambiguous", _lastFeedback + ";bbox=" + UvText(reply.bbox_uv));
                        continue;
                    }
                    if (refined != null && refined.Pick != null && refined.CandidateIds.Count == 1)
                    {
                        pick = refined.Pick;
                        _lastPick = pick;
                        LogEvent("pick_bbox_fallback", "sample=" + (pick.SampleId ?? "") + ";hit=" + VectorText(pick.HitPoint) +
                                 ";chosen_uv=" + refined.U.ToString("F4") + "," + refined.V.ToString("F4") +
                                 ";candidates=" + string.Join(",", refined.CandidateIds.ToArray()) +
                                 ";bbox=" + UvText(reply.bbox_uv));
                    }
                }
                if (pick.Status != Stage2PickStatus.Hit || string.IsNullOrEmpty(pick.SampleId))
                {
                    _lastFeedback = "点位没有命中可交互长椅：" + pick.ShortLabel();
                    continue;
                }
                if (!Stage5SeatRegistry.CanSit(pick.SampleId))
                {
                    _lastFeedback = "命中的实体没有注册坐下能力：" + pick.SampleId;
                    LogEvent("ability_rejected", _lastFeedback);
                    continue;
                }
                if (!string.IsNullOrEmpty(_frozenCandidateId) && pick.SampleId != _frozenCandidateId)
                {
                    _lastFeedback = "新帧看到的不是本次冻结的候选：" + pick.SampleId;
                    LogEvent("candidate_mismatch", _lastFeedback);
                    continue;
                }
                candidate = Stage5SeatRegistry.Find(pick.SampleId);
                if (candidate == null) { _lastFeedback = "逻辑实体查询失败：" + pick.SampleId; continue; }
                _lastSelectedEntity = candidate.EntityId;
                LogEvent("candidate_bound", "entity=" + candidate.EntityId + ";snapshot=" + locateFrame.Snapshot.SnapshotId);
                break;
            }

            if (candidate == null)
            {
                Fail("vision_target_not_confirmed", "没有从真实画面确认到可交互长椅：" + Clean(_lastFeedback));
                yield break;
            }
            if (_cancelRequested) { yield return CancelCleanup(task); yield break; }

            // Aim the real EyeGimbal (and body assist) at the picked surface point.
            State = TaskState.Aiming;
            yield return AimAt(gen, _lastPick.HitPoint);
            if (gen != _generation || _cancelRequested) { yield return CancelCleanup(task); yield break; }
            if (!string.IsNullOrEmpty(_aimError))
            {
                Fail("aim_failed", "真实视线瞄准失败：" + _aimError);
                yield break;
            }

            // New frame after aiming: the center ray must confirm the same sample.
            State = TaskState.Confirming;
            bool confirmed = false;
            for (int attempt = 0; attempt < 2 && !confirmed; attempt++)
            {
                if (gen != _generation || _cancelRequested) { yield return CancelCleanup(task); yield break; }
                var confirmFrame = _capture.Capture();
                if (confirmFrame == null) { Fail("confirm_capture_failed", "中心确认截图失败：" + _capture.LastError); yield break; }
                _capture.SaveFrame(confirmFrame, Path.Combine(RunDirectory, "raw"), "confirm_" + attempt);
                _lastFrame = confirmFrame;
                var center = Stage2PickMath.PickCenter(_stage2, confirmFrame.Snapshot,
                    _character != null ? _character.transform.position : confirmFrame.Snapshot.Position, 0f);
                _lastConfirm = center;
                LogEvent("center_pick", "status=" + center.Status + ";sample=" + (center.SampleId ?? "") +
                         ";snapshot=" + confirmFrame.Snapshot.SnapshotId + ";hit=" + VectorText(center.HitPoint) +
                         ";occluder=" + (center.OccluderId ?? ""));
                if (center.Status == Stage2PickStatus.Hit && center.SampleId == candidate.EntityId)
                {
                    confirmed = true;
                    break;
                }
                _lastFeedback = "中心准星没有确认到同一实体：" + center.ShortLabel();
                yield return AimAt(gen, _lastPick.HitPoint);
                if (gen != _generation || _cancelRequested) { yield return CancelCleanup(task); yield break; }
            }
            if (!confirmed)
            {
                Fail("center_confirmation_failed", "新帧中心没有确认到同一把椅子：" + Clean(_lastFeedback));
                yield break;
            }

            State = intent.Kind == Stage5IntentKind.LookOnly ? TaskState.Seated : TaskState.WalkingToSeat;
            if (intent.Kind == Stage5IntentKind.LookOnly)
            {
                ReleaseOwner();
                IsActive = false;
                State = TaskState.Idle;
                _seated = false;
                LastStatus = "selected_only";
                LastError = "";
                RestoreIdleIfFree();
                // Keep the real first-person view and reticle visible briefly so the
                // selection is observable, then restore the previous camera mode.
                yield return new WaitForSeconds(0.5f);
                if (gen != _generation) yield break;
                SetEyePreview(false);
                _keepReticle = false;
                LogEvent("task_end", "look_only;entity=" + candidate.EntityId + ";snapshot=" + _lastFrame.Snapshot.SnapshotId);
                Reply("已选中 " + candidate.EntityId + "；只保持注意，不会过去或坐下。");
                yield break;
            }

            // Hand off from Stage-5 vision authority to Stage-4's seat owner.
            ReleaseOwner();
            State = TaskState.WalkingToSeat;
            _lastFeedback = "";
            if (_seat == null) { Fail("seat_controller_missing", "座位控制器不可用。"); yield break; }
            if (!_seat.TrySit(candidate.EntityId))
            {
                Fail("sit_start_failed", "阶段 4 没有启动坐下：" + (_seat.LastError ?? _seat.LastStatus));
                yield break;
            }
            _stage4Active = true;
            if (task != null) task.Stage4Active = true;
            LogEvent("stage4_started", "entity=" + candidate.EntityId + ";state=" + _seat.State);
            float deadline = Time.realtimeSinceStartup + Mathf.Max(20f, SitTimeoutSeconds);
            while (Time.realtimeSinceStartup < deadline)
            {
                // The Stage-4 walk leg computes its own real-path budget; never let
                // the fixed outer wrapper timeout cut a legitimate long walk short.
                if (_seat != null && _seat.SuggestedSitDeadline > deadline)
                    deadline = _seat.SuggestedSitDeadline;
                if (gen != _generation || _cancelRequested)
                {
                    if (_seat != null) _seat.RequestCancel();
                    yield return CancelCleanup(task);
                    yield break;
                }
                var st = _seat.State;
                if (st == Stage4SeatController.SeatState.Seated)
                {
                    _stage4Active = false;
                    if (_seat.CurrentEntity == candidate.EntityId)
                    {
                        _seated = true;
                        IsActive = false;
                        State = TaskState.Seated;
                        LastStatus = "seated";
                        LastError = "";
                        _keepReticle = false;
                        SetEyePreview(false);
                        LogEvent("task_end", "seated;entity=" + candidate.EntityId + ";state=" + st +
                                 ";root=" + VectorText(_character != null ? _character.transform.position : Vector3.zero));
                        Reply("已坐下：" + candidate.EntityId + "。我现在保持坐姿，要起来请说“起身”。");
                    }
                    else
                    {
                        Fail("wrong_entity_seated", "实际坐下的实体不是视觉确认目标：" + (_seat.CurrentEntity ?? "?"));
                    }
                    yield break;
                }
                if (st == Stage4SeatController.SeatState.Failed || st == Stage4SeatController.SeatState.Idle)
                {
                    _stage4Active = false;
                    Fail("sit_failed", "坐下没有完成：" + (_seat.LastError ?? _seat.LastStatus));
                    yield break;
                }
                yield return null;
            }
            _stage4Active = false;
            if (_seat != null) _seat.RequestCancel();
            Fail("sit_timeout", "坐下流程超时，未确认 Seated。");
        }

        IEnumerator ResolveNearbyCandidate(int gen)
        {
            _frozenCandidateId = "";
            _frozenReason = "no_reachable_visible_seat_within_radius";
            var candidates = Stage5SeatRegistry.Snapshot(_frozenCharacter);
            candidates.Sort((first, second) =>
            {
                int distance = first.Distance.CompareTo(second.Distance);
                return distance != 0 ? distance : string.CompareOrdinal(first.EntityId, second.EntityId);
            });
            float radius = NearbySearchRadiusMeters;
            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0f)
            {
                _frozenReason = "invalid_search_radius";
                yield break;
            }
            LogEvent("nearby_candidates", "frozen_pos=" + VectorText(_frozenPosition) + ";radius=" + radius.ToString("F1") +
                     ";count=" + candidates.Count + ";ids=" + JoinIds(candidates));
            foreach (var candidate in candidates)
            {
                if (gen != _generation || _cancelRequested) yield break;
                string rejection = "";
                Vector3 lookPoint = Vector3.zero;
                if (candidate.Distance > radius) rejection = "outside_search_radius";
                else if (!WorldData.IsWalkable(candidate.ApproachPoint)) rejection = "approach_not_walkable";
                else
                {
                    var path = WorldData.FindPath(_frozenPosition, candidate.ApproachPoint);
                    if (path == null || path.Count == 0) rejection = "approach_unreachable";
                    else if (!TryNearbyLookPoint(candidate, out lookPoint)) rejection = "no_visible_proxy_surface";
                }
                if (!string.IsNullOrEmpty(rejection))
                {
                    LogEvent("nearby_candidate_rejected", "entity=" + candidate.EntityId + ";distance=" + candidate.Distance.ToString("F2") + ";reason=" + rejection);
                    continue;
                }
                LogEvent("nearby_candidate_aim", "entity=" + candidate.EntityId + ";distance=" + candidate.Distance.ToString("F2") +
                         ";approach=" + VectorText(candidate.ApproachPoint) + ";look_point=" + VectorText(lookPoint));
                State = TaskState.Aiming;
                yield return AimAt(gen, lookPoint);
                if (gen != _generation || _cancelRequested) yield break;
                if (!string.IsNullOrEmpty(_aimError))
                {
                    LogEvent("nearby_candidate_rejected", "entity=" + candidate.EntityId + ";reason=" + _aimError);
                    continue;
                }
                _frozenCandidateId = candidate.EntityId;
                _frozenReason = "";
                LogEvent("nearby_turned", "frozen_id=" + _frozenCandidateId + ";body_yaw=" + _character.transform.eulerAngles.y.ToString("F1"));
                yield break;
            }
        }

        bool TryNearbyLookPoint(Stage5SeatCandidate candidate, out Vector3 lookPoint)
        {
            lookPoint = Vector3.zero;
            Stage2SampleMesh mesh;
            var eye = _capture != null ? _capture.EyeCamera : null;
            if (eye == null || !_stage2.Meshes.TryGetValue(candidate.EntityId, out mesh)) return false;
            Vector3 origin = eye.transform.position;
            for (int index = 0; index + 2 < mesh.Triangles.Length; index += 3)
            {
                Vector3 point = (mesh.Vertices[mesh.Triangles[index]] + mesh.Vertices[mesh.Triangles[index + 1]] +
                                 mesh.Vertices[mesh.Triangles[index + 2]]) / 3f;
                var pick = Stage2PickMath.Pick(_stage2, origin, point - origin, eye.farClipPlane,
                    eye.nearClipPlane, _frozenPosition, null);
                if (pick.Status != Stage2PickStatus.Hit || pick.SampleId != candidate.EntityId) continue;
                lookPoint = pick.HitPoint;
                return true;
            }
            return false;
        }

        IEnumerator ResolveBehindCandidate(int gen, Stage5Intent intent)
        {
            _frozenCandidateId = "";
            _frozenReason = "";
            if (_frozenCharacter == null) { _frozenReason = "missing_character"; yield break; }
            var candidates = Stage5SeatRegistry.Snapshot(_frozenCharacter);
            var behind = new List<Stage5SeatCandidate>();
            foreach (var c in candidates) if (c.IsBehind) behind.Add(c);
            LogEvent("behind_candidates", "frozen_pos=" + VectorText(_frozenPosition) + ";frozen_yaw=" + _frozenYaw.ToString("F1") +
                     ";count=" + behind.Count + ";ids=" + JoinIds(behind));
            if (behind.Count == 0) { _frozenReason = "no_candidate_behind"; yield break; }
            if (behind.Count > 1) { _frozenReason = "ambiguous_multiple_behind"; yield break; }
            _frozenCandidateId = behind[0].EntityId;
            var target = Stage5SeatRegistry.Find(_frozenCandidateId);
            if (target == null) { _frozenReason = "logical_entity_missing"; yield break; }
            State = TaskState.Aiming;
            yield return AimAt(gen, target.SeatPoint);
            if (gen != _generation || _cancelRequested) yield break;
            if (!string.IsNullOrEmpty(_aimError))
            {
                _frozenReason = "behind_turn_failed:" + _aimError;
                _frozenCandidateId = "";
                yield break;
            }
            LogEvent("behind_turned", "frozen_id=" + _frozenCandidateId + ";body_yaw=" + _character.transform.eulerAngles.y.ToString("F1"));
        }

        Stage2PickResult PickAtUv(Stage5EyeFrame frame, float u, float vTopLeft)
        {
            float viewportY = 1f - Mathf.Clamp01(vTopLeft);
            Vector3 dir = Stage2PickMath.RayFromSnapshot(frame.Snapshot, Mathf.Clamp01(u), viewportY);
            return Stage2PickMath.Pick(_stage2, frame.Snapshot.Position, dir,
                frame.Snapshot.Far, Mathf.Max(0f, frame.Snapshot.Near),
                _character != null ? _character.transform.position : frame.Snapshot.Position,
                frame.Snapshot, null, null);
        }

        public sealed class Stage5BboxProbe
        {
            public Stage2PickResult Pick;
            public readonly List<string> CandidateIds = new List<string>();
            public float U, V;
            public bool Ambiguous { get { return CandidateIds.Count > 1; } }
        }

        Stage5BboxProbe PickInsideBbox(Stage5EyeFrame frame, float[] bbox)
        {
            var probe = new Stage5BboxProbe();
            if (frame == null || bbox == null || bbox.Length != 4) return probe;
            var byId = new Dictionary<string, Stage2PickResult>();
            float x1 = bbox[0], y1 = bbox[1], x2 = bbox[2], y2 = bbox[3];
            for (int iy = 0; iy < 7; iy++)
            {
                for (int ix = 0; ix < 7; ix++)
                {
                    float fx = (ix + 0.5f) / 7f;
                    float fy = (iy + 0.5f) / 7f;
                    float u = Mathf.Lerp(x1, x2, fx);
                    float v = Mathf.Lerp(y1, y2, fy);
                    var pick = PickAtUv(frame, u, v);
                    if (pick == null || pick.Status != Stage2PickStatus.Hit || string.IsNullOrEmpty(pick.SampleId)) continue;
                    if (!byId.ContainsKey(pick.SampleId))
                    {
                        byId[pick.SampleId] = pick;
                        probe.CandidateIds.Add(pick.SampleId);
                        if (probe.Pick == null)
                        {
                            probe.Pick = pick;
                            probe.U = u;
                            probe.V = v;
                        }
                    }
                }
            }
            return probe;
        }

        /// <summary>Deterministic evidence helper: run the production bbox refinement
        /// against the current EyeCamera frame. It does not start a task or move.</summary>
        public Stage5BboxProbe DebugProbeBbox(float[] bbox)
        {
            Resolve();
            if (_capture == null || !_capture.Resolve()) return null;
            if (_stage2 == null || !_stage2.Valid) _stage2 = Stage2PickDataLoader.Load();
            if (_stage2 == null || !_stage2.Valid) return null;
            var frame = _capture.Capture();
            if (frame == null) return null;
            return PickInsideBbox(frame, bbox);
        }

        /// <summary>
        /// Production bbox refinement is allowed only for a single identity inside
        /// the model-provided box. This helper is used by tests to prove that a box
        /// spanning both benches is not accepted.
        /// </summary>
        public Stage5BboxProbe DebugProbeBboxKnown(float[] bbox) { return DebugProbeBbox(bbox); }

        IEnumerator AimAt(int gen, Vector3 worldTarget)
        {
            _aimError = "";
            if (_gaze == null || _character == null) { _aimError = "missing_gaze_or_character"; yield break; }
            bool ok = false;
            for (int iteration = 0; iteration < 4; iteration++)
            {
                if (gen != _generation || _cancelRequested) yield break;
                Transform basis = _character.transform;
                Transform pivot = _gaze.YawPivot != null ? _gaze.YawPivot : _gaze.transform;
                Vector3 eyePosition = _capture.EyeCamera != null ? _capture.EyeCamera.transform.position : pivot.position;
                Vector3 local = basis.InverseTransformDirection(worldTarget - eyePosition);
                float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg - _gaze.BaseYaw - _gaze.HomeYaw;
                float horizontal = Mathf.Sqrt(local.x * local.x + local.z * local.z);
                float pitch = Mathf.Atan2(local.y, Mathf.Max(0.0001f, horizontal)) * Mathf.Rad2Deg;
                bool allowed = false;
                using (TaskAuthority.Enter(_owner))
                {
                    allowed = TaskAuthority.Allows(2);
                    if (allowed) _gaze.TurnRelative(yaw, pitch, 3f, true);
                }
                if (!allowed) { _aimError = "gaze_turn_rejected"; yield break; }
                LogEvent("aim_command", "iter=" + iteration + ";yaw=" + yaw.ToString("F2") + ";pitch=" + pitch.ToString("F2") +
                         ";target=" + VectorText(worldTarget));
                float deadline = Time.realtimeSinceStartup + Mathf.Max(1f, AimTimeoutSeconds);
                while (Time.realtimeSinceStartup < deadline && (_gaze.IsObservationSettling || _motor != null && _motor.IsTurning))
                {
                    if (gen != _generation || _cancelRequested) yield break;
                    yield return null;
                }
                yield return new WaitForSeconds(0.12f);
                if (gen != _generation || _cancelRequested) yield break;
                var eye = _capture.EyeCamera;
                if (eye != null)
                {
                    float angle = Vector3.Angle(eye.transform.forward, (worldTarget - eye.transform.position).normalized);
                    LogEvent("aim_check", "iter=" + iteration + ";angle=" + angle.ToString("F2") +
                             ";limited=" + _gaze.LastObservationLimited + ";body=" + _gaze.LastBodyAssistOutcome);
                    if (angle < 3.5f) { ok = true; break; }
                }
                else { _aimError = "missing_eye_camera"; yield break; }
            }
            if (!ok && string.IsNullOrEmpty(_aimError))
                _aimError = "aim_not_centered;limited=" + _gaze.LastObservationLimited + ";body=" + _gaze.LastBodyAssistOutcome;
        }

        IEnumerator CancelCleanup(Stage5Run task)
        {
            bool current = task != null && task == _task;
            // A stale coroutine may only release the authority it captured. It must
            // never touch the current task's owner, camera, pending intent or state.
            if (task != null && !string.IsNullOrEmpty(task.Owner))
            {
                TaskAuthority.Release(task.Owner);
                LogEvent("authority_released", "owner=" + task.Owner + ";stale=" + (!current));
                if (_owner == task.Owner) _owner = null;
            }
            if (task != null && task.Stage4Active && _seat != null) _seat.RequestCancel();
            if (!current)
            {
                LogEvent("stale_cleanup_ignored", "generation=" + (task != null ? task.Generation : -1) + ";current=" + _generation);
                yield break;
            }
            if (_seat != null && _seat.State == Stage4SeatController.SeatState.StandingUp) _seat.RequestCancel();
            _stage4Active = false;
            _routine = null;
            _task = null;
            ReleaseOwner();
            bool wasActive = IsActive;
            IsActive = false;
            State = TaskState.Cancelled;
            LastStatus = "cancelled";
            _keepReticle = false;
            SetEyePreview(false);
            RestoreIdleIfFree();
            if (wasActive)
            {
                LogEvent("task_end", "cancelled;error=" + Clean(LastError));
                Reply("已取消这次视觉/坐下任务，没有继续走动或坐下。");
            }
            if (_pendingIntent != null)
            {
                var next = _pendingIntent;
                int token = _queuedToken;
                bool replacementQueued = string.Equals(LastError, "replaced_by_new_task", StringComparison.Ordinal);
                // Keep the queued command globally visible during the handoff gap
                // so a Cancel/normal-chat/disable can invalidate its token.
                var beforeQueuedStart = Stage5TestHooks.BeforeQueuedStart;
                if (beforeQueuedStart != null) beforeQueuedStart();
                yield return null;
                if (_pendingIntent != next || token != _queuedToken ||
                    !isActiveAndEnabled || (_cancelRequested && !replacementQueued))
                {
                    LogEvent("queued_start_suppressed", "token=" + token + ";current=" + _queuedToken +
                             ";active=" + isActiveAndEnabled + ";cancel=" + _cancelRequested);
                    yield break;
                }
                _pendingIntent = null;
                _queuedToken++;
                StartTask(next, next.Original);
            }
            yield break;
        }

        void Fail(string code, string message)
        {
            LastError = string.IsNullOrEmpty(code) ? message : code + ": " + message;
            LastStatus = "failed";
            State = TaskState.Failed;
            ReleaseOwner();
            if (_stage4Active && _seat != null) _seat.RequestCancel();
            _stage4Active = false;
            IsActive = false;
            _keepReticle = false;
            SetEyePreview(false);
            RestoreIdleIfFree();
            LogEvent("task_end", "failed;code=" + code + ";message=" + Clean(message));
            Reply("没有完成：" + message);
        }

        void ReleaseOwner()
        {
            if (!string.IsNullOrEmpty(_owner))
            {
                TaskAuthority.Release(_owner);
                LogEvent("authority_released", "owner=" + _owner);
                _owner = null;
            }
        }

        void RestoreIdleIfFree()
        {
            if (_stage4Active || (_seat != null && _seat.State == Stage4SeatController.SeatState.Seated)) return;
            if (_character != null && TaskAuthority.Allows(1)) _character.PlayGesture("idle");
        }

        void ClearQueuedIntent(string reason)
        {
            if (_pendingIntent == null) return;
            _pendingIntent = null;
            _queuedToken++;
            LogEvent("queued_intent_cleared", Clean(reason));
        }

        void CancelQueuedIntent(string reason, bool notify)
        {
            if (_pendingIntent == null) return;
            ClearQueuedIntent(reason);
            LastStatus = "cancelled";
            LastError = reason ?? "queued_cancelled";
            State = TaskState.Cancelled;
            IsActive = false;
            SetEyePreview(false);
            if (notify) Reply("已取消待启动的替换任务。");
        }

        public void Cancel(string reason)
        {
            if (!IsActive)
            {
                if (_seat != null && _stage4Active) _seat.RequestCancel();
                _stage4Active = false;
                if (_pendingIntent != null) ClearQueuedIntent(reason ?? "cancelled");
                return;
            }
            _cancelRequested = true;
            _generation++;
            if (_seat != null && (_stage4Active || _seat.State == Stage4SeatController.SeatState.StandingUp))
                _seat.RequestCancel();
            _stage4Active = false;
            if (!string.Equals(reason, "replaced_by_new_task", StringComparison.Ordinal))
                ClearQueuedIntent(reason ?? "cancelled");
            LastStatus = "cancelling";
            LastError = reason ?? "cancelled";
            LogEvent("cancel_request", Clean(reason));
            if (_routine == null)
            {
                _routine = null;
                _task = null;
                ReleaseOwner();
                IsActive = false;
                State = TaskState.Cancelled;
                SetEyePreview(false);
                RestoreIdleIfFree();
            }
        }

        // ------------------------------------------------------------
        // Vision response helpers
        // ------------------------------------------------------------

        [Serializable]
        sealed class VisionInputRecord
        {
            public long frame_id;
            public string image_sha256;
            public string original_image_sha256;
            public int width, height;
            public float[] crop_top_left_uv;
            public Stage2SnapshotRecord snapshot;
        }

        void SaveVisionInput(Stage5EyeFrame frame, int attempt, byte[] image, int width, int height, string hash, Rect crop)
        {
            string prefix = Path.Combine(RunDirectory, "vision_input_" + attempt);
            if (crop.width < 1f || crop.height < 1f) File.WriteAllBytes(prefix + ".jpg", image);
            File.WriteAllText(prefix + ".json", JsonUtility.ToJson(new VisionInputRecord
            {
                frame_id = frame.FrameId,
                image_sha256 = hash,
                original_image_sha256 = frame.JpegSha256,
                width = width,
                height = height,
                crop_top_left_uv = new[] { crop.xMin, crop.yMin, crop.xMax, crop.yMax },
                snapshot = frame.Snapshot.ToRecord(),
            }, true));
        }

        static Stage5VisionRawReply MapLocationToFrame(Stage5VisionRawReply reply, Rect crop)
        {
            Vector2 point = Stage5EyeCapture.ToFrameUv(crop, reply.point_uv[0], reply.point_uv[1]);
            float[] bbox = null;
            if (reply.bbox_uv != null && reply.bbox_uv.Length == 4)
            {
                Vector2 first = Stage5EyeCapture.ToFrameUv(crop, reply.bbox_uv[0], reply.bbox_uv[1]);
                Vector2 second = Stage5EyeCapture.ToFrameUv(crop, reply.bbox_uv[2], reply.bbox_uv[3]);
                bbox = new[] { first.x, first.y, second.x, second.y };
            }
            return new Stage5VisionRawReply
            {
                status = reply.status,
                description = reply.description,
                error = reply.error,
                point_uv = new[] { point.x, point.y },
                bbox_uv = bbox,
            };
        }

        static string BuildInstruction(Stage5Intent intent, string feedback)
        {
            string dir = intent.Kind == Stage5IntentKind.SitBehind || intent.Direction == "behind" ? "画面中已经在用户身体后方的那把长椅" : "画面中的长椅";
            if (intent.Direction == "nearby") dir = "已经转头朝向的、画面中心附近那把长椅";
            string instruction = "用户原始指令：" + intent.Original + "。当前要找的是" + dir + "。" +
                                 "这是夜间第一人称画面；长椅是低矮的深色木板/条凳结构，可能出现在画面中下部、被路灯或阴影压暗。" +
                                 "请只根据这张真实第一人称截图指出长椅上可见的实心表面点，不要因为距离/夜间就直接说没有。";
            if (!string.IsNullOrEmpty(feedback)) instruction += " 上次失败原因：" + feedback + "。请换一个更可靠的可见木板点，不要只给 bbox 中心。";
            return instruction;
        }

        static string ValidateLocation(Stage5VisionRawReply reply)
        {
            if (reply == null) return "reply_null";
            if (reply.status != "located" && reply.status != "not_found" && reply.status != "ambiguous") return "invalid_status";
            if (reply.status != "located") return null;
            if (reply.point_uv == null || reply.point_uv.Length != 2) return "invalid_point_length";
            for (int i = 0; i < 2; i++)
                if (float.IsNaN(reply.point_uv[i]) || float.IsInfinity(reply.point_uv[i]) ||
                    reply.point_uv[i] < 0f || reply.point_uv[i] > 1f) return "point_out_of_range";
            if (reply.bbox_uv != null && reply.bbox_uv.Length > 0)
            {
                if (reply.bbox_uv.Length != 4) return "invalid_bbox_length";
                for (int i = 0; i < 4; i++)
                    if (float.IsNaN(reply.bbox_uv[i]) || float.IsInfinity(reply.bbox_uv[i]) ||
                        reply.bbox_uv[i] < 0f || reply.bbox_uv[i] > 1f) return "bbox_out_of_range";
                if (reply.bbox_uv[0] >= reply.bbox_uv[2] || reply.bbox_uv[1] >= reply.bbox_uv[3]) return "bad_bbox_order";
            }
            return null;
        }

        void SaveVisionResult(Stage5VisionResult result, string tag)
        {
            if (result == null) return;
            Directory.CreateDirectory(RunDirectory);
            string path = Path.Combine(RunDirectory, "vision_" + tag + "_" + (++_requestSerial) + ".json");
            var sb = new StringBuilder();
            sb.Append("{\"ok\":").Append(result.Ok ? "true" : "false")
              .Append(",\"model\":\"").Append(Escape(result.Model)).Append("\"")
              .Append(",\"http_status\":").Append(result.HttpStatus)
              .Append(",\"seconds\":").Append(result.Seconds.ToString("F3"))
              .Append(",\"prompt_tokens\":").Append(result.PromptTokens)
              .Append(",\"completion_tokens\":").Append(result.CompletionTokens)
              .Append(",\"image_sha256\":\"").Append(result.ImageSha256).Append("\"")
              .Append(",\"error\":\"").Append(Escape(result.Error)).Append("\"")
              .Append(",\"raw\":\"").Append(Escape(Truncate(result.Raw, 8000))).Append("\"}");
            File.WriteAllText(path, sb.ToString());
        }

        // ------------------------------------------------------------
        // Evidence
        // ------------------------------------------------------------

        void EnsureRunDirectory()
        {
            try
            {
                string root = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Captures", "Stage5");
                RunDirectory = Path.Combine(root, "run_" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(Path.Combine(RunDirectory, "raw"));
                File.WriteAllText(Path.Combine(RunDirectory, "run.json"),
                    "{\"stage\":\"5\",\"intent\":\"" + Escape(LastStatus) + "\",\"started\":\"" +
                    DateTime.Now.ToString("o") + "\"}");
            }
            catch (Exception e) { RunDirectory = ""; Debug.LogWarning("[Stage5] evidence dir failed: " + e.Message); }
        }

        void LogEvent(string type, string detail)
        {
            string line = "{\"at\":" + Time.realtimeSinceStartup.ToString("F3") +
                          ",\"generation\":" + _generation +
                          ",\"state\":\"" + State + "\"" +
                          ",\"type\":\"" + Escape(type) + "\"" +
                          ",\"detail\":\"" + Escape(detail ?? "") + "\"" +
                          ",\"selected\":\"" + Escape(_lastSelectedEntity ?? "") + "\"" +
                          ",\"frozen_id\":\"" + Escape(_frozenCandidateId ?? "") + "\"" +
                          ",\"snapshot\":\"" + Escape(_lastFrame != null && _lastFrame.Snapshot != null ? _lastFrame.Snapshot.SnapshotId : "") + "\"}";
            Debug.Log("[Stage5] " + type + " " + detail);
            if (!string.IsNullOrEmpty(RunDirectory))
            {
                try { File.AppendAllText(Path.Combine(RunDirectory, "events.jsonl"), line + "\n"); }
                catch (Exception) { }
            }
        }

        static string VectorText(Vector3 v) { return "(" + v.x.ToString("F3") + "," + v.y.ToString("F3") + "," + v.z.ToString("F3") + ")"; }
        static string UvText(float[] uv) { return uv == null ? "" : "[" + uv[0].ToString("F4") + "," + uv[1].ToString("F4") + "," + uv[2].ToString("F4") + "," + uv[3].ToString("F4") + "]"; }
        static string QuatText(Quaternion q) { return "(" + q.x.ToString("F3") + "," + q.y.ToString("F3") + "," + q.z.ToString("F3") + "," + q.w.ToString("F3") + ")"; }
        static string JoinIds(List<Stage5SeatCandidate> list) { var sb = new StringBuilder(); foreach (var c in list) { if (sb.Length > 0) sb.Append(','); sb.Append(c.EntityId); } return sb.ToString(); }
        static string Clean(string s) { return (s ?? "").Replace("\r", " ").Replace("\n", " "); }
        static string Truncate(string s, int max) { if (string.IsNullOrEmpty(s)) return ""; return s.Length <= max ? s : s.Substring(0, max) + "…"; }
        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------
        // Fixed center reticle (actual GameView, not the model image)
        // ------------------------------------------------------------
        void OnGUI()
        {
            if (!ShowReticle && !IsActive && !_keepReticle) return;
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            Color old = GUI.color;
            GUI.color = new Color(1f, 0.85f, 0.25f, 0.95f);
            GUI.DrawTexture(new Rect(cx - 1.5f, cy - 16f, 3f, 32f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 16f, cy - 1.5f, 32f, 3f), Texture2D.whiteTexture);
            GUI.color = new Color(0.03f, 0.04f, 0.06f, 0.86f);
            GUI.DrawTexture(new Rect(cx + 20f, cy - 14f, 360f, 28f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft, wordWrap = false };
            GUI.Label(new Rect(cx + 26f, cy - 14f, 350f, 28f), "Stage5 " + State + "  " + LastStatus +
                (string.IsNullOrEmpty(LastSelectedEntity) ? "" : "  " + LastSelectedEntity), style);
            GUI.color = old;
        }
    }
}
