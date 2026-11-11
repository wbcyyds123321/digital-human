using System;
using System.Collections;
using System.IO;
using System.Threading;
using UnityEngine;
using Yixuan.Motion;
using Yixuan.Vision;

namespace Yixuan.AI.Tasks
{
    public partial class TaskMissionRunner
    {
        public bool IsAdoptedNavigation { get; private set; }
        public string AdoptedAnchor { get; private set; }

        public bool AdoptNavigation(string anchorId, string request)
        {
            if (!Application.isPlaying || string.IsNullOrWhiteSpace(anchorId)) return false;
            if (IsRunning || IsInsertion || IsSuspended) return false;
            ResolveRefs(); CaptureHome();
            if (Motor == null || Session == null) return false;
            if (!Motor.IsMoving || string.IsNullOrEmpty(Motor.TargetAnchor) || Motor.TargetAnchor != anchorId) return false;
            if (Session.InputMode != TaskInputMode.Auto || !Session.TaskPlanningEnabled) return false;

            _pausedSeconds = 0; _pausedAt = 0; LastInsertionReport = "";
            if (_cancel != null) _cancel.Dispose();
            _generation++; _cancel = new CancellationTokenSource(); _held = 0; _workers.Clear(); _guards.Clear();
            _insertions.Clear(); ActiveInsertion = null;
            Contract = null; _testContract = null; _testProgram = null;
            _codeReceipts.Clear(); _codeKeys.Clear(); _scriptRoutes.Clear(); _lastScriptRoute = null;
            LastProgram = ""; LastReport = "";

            string root = Path.IsPathRooted(EvidenceRoot)
                ? EvidenceRoot
                : Path.Combine(Application.dataPath, "..", EvidenceRoot);
            RunDirectory = Path.GetFullPath(Path.Combine(root, "mission_" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")));
            Directory.CreateDirectory(RunDirectory);

            Context = new TaskRuntimeContext
            {
                Generation = _generation,
                EvidenceDirectory = RunDirectory,
                SceneVersion = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path + "@" +
                               UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle
            };
            if (!TaskAuthority.Acquire(Context.MissionId, 1 | 2 | 8))
            {
                RuntimeNotice("AdoptionRejected", "resources_owned_by_another_run");
                if (_cancel != null) { _cancel.Dispose(); _cancel = null; }
                return false;
            }

            IsRunning = true; IsAdoptedNavigation = true; AdoptedAnchor = anchorId;
            Phase = "adopted_navigation";

            string text = string.IsNullOrWhiteSpace(request) ? "走到" + anchorId : request.Trim();
            Evidence = new MissionEvidence
            {
                request = text,
                status = "running",
                started = TaskNow,
                wall_started = Time.realtimeSinceStartup,
                origin = HomePosition,
                origin_yaw = HomeYaw,
                task_origin = Motor.transform.position,
                task_origin_yaw = Motor.transform.eulerAngles.y,
                vision_model = QwenVisionClient.DefaultModel,
                contract_source = "chat_navigation_handoff",
                execution_backend = "ChatSession navigation adoption (existing CharacterLocomotion route retained)",
                contract = null,
                completion = null,
                intent = null,
                plan = null
            };
            Evidence.request_id = Context.RequestId;
            Evidence.mission_id = Context.MissionId;
            Evidence.generation = _generation;
            Evidence.scene_version = Context.SceneVersion;
            Evidence.results.Add(new MissionStepResult
            {
                id = "s1", kind = "navigate", target_id = anchorId, target_path = anchorId,
                status = "running", started = TaskNow, generation = _generation,
                start_position = Motor.transform.position,
                target_position = Motor.Target,
                approach_position = Motor.Target
            });
            Write("request.txt", text);
            Save();
            EmitRuntime("MissionStarted", "", "running", "adopted chat navigation: " + text);
            EmitRuntime("ChatNavigationAdopted", "", "running", anchorId + "|" + Motor.TargetAnchor + "|" + Motor.Target.ToString("F2"));
            AcquireForAdoption();
            bool ownerHanded = false;
            if (Motor != null) using (TaskAuthority.Enter(Context.MissionId)) ownerHanded = Motor.HandOffMotionOwner(Context.MissionId);
            if (!ownerHanded)
            {
                using (TaskAuthority.Enter(Context.MissionId)) Release();
                TaskAuthority.Release(Context.MissionId);
                Context.Active = false;
                IsRunning = false; IsAdoptedNavigation = false; AdoptedAnchor = null; Phase = "handoff_rejected";
                Evidence.status = "rejected"; Evidence.reason = "motion_owner_handoff_failed";
                Save();
                RuntimeNotice("AdoptionRejected", "motion_owner_handoff_failed");
                return false;
            }
            _lastPosition = Motor.transform.position; _sampleAt = 0;
            _main = StartCoroutine(RunAdopted(_generation));
            return true;
        }

        void AcquireForAdoption()
        {
            ClearAttention();
            _leased = true; _oldArrivalRadius = Motor.ArriveRadius;
            if (Gimbal != null) _oldAutoRecenter = Gimbal.AutoRecenter;
            if (Session != null && Session.Director != null)
            {
                _brainWasProactive = Session.BrainProactivePreference;
                Debug.Log("[Task] 承接聊天导航：用户主动行为偏好=" + _brainWasProactive +
                          " known=" + Session.BrainProactiveKnown + " captured=" + Session.BrainProactiveCaptured);
                Session.Director.SuspendBrain(true);
            }
        }

        void AdoptedCompletion(bool succeeded, string reason)
        {
            Evidence.completion = new TaskCompletion
            {
                program_succeeded = succeeded,
                task_succeeded = succeeded,
                reason = reason
            };
            if (succeeded && !string.IsNullOrEmpty(AdoptedAnchor)) Evidence.completion.satisfied.Add("navigate:" + AdoptedAnchor);
            else if (!succeeded) Evidence.completion.missing.Add("navigate:" + (AdoptedAnchor ?? ""));
        }

        IEnumerator RunAdopted(int generation)
        {
            var leg = Evidence != null && Evidence.results.Count > 0 ? Evidence.results[0] : null;
            Vector3 previous = Motor.transform.position;
            while (generation == _generation)
            {
                if (IsSuspended) { yield return null; continue; }
                if (Motor == null) { AdoptedCompletion(false, "缺少移动控制器"); Finish("failed", "缺少移动控制器"); yield break; }
                Vector3 p = Motor.transform.position;
                if (leg != null)
                {
                    leg.path_length += TaskTargetCatalog.FlatDistance(previous, p);
                    leg.end_position = p;
                    leg.position_drift = TaskTargetCatalog.FlatDistance(leg.start_position, p);
                }
                previous = p;
                if (!Motor.WalkingEnabled)
                {
                    string offReason = Motor.LastError;
                    using (TaskAuthority.Enter(Context != null ? Context.MissionId : null)) Motor.Stop("走路已关闭", "cancelled");
                    if (leg != null) { leg.ended = TaskNow; leg.status = "cancelled"; leg.reason = "walking_disabled"; }
                    AdoptedCompletion(false, "walking_disabled: " + (offReason ?? ""));
                    Finish("cancelled", "walking_disabled: " + (offReason ?? ""));
                    yield break;
                }
                if (!Motor.IsMoving)
                {
                    string motorStatus = Motor.LastStatus;
                    if (leg != null)
                    {
                        leg.ended = TaskNow;
                        leg.distance_error = TaskTargetCatalog.FlatDistance(p, leg.target_position);
                        leg.status = motorStatus == "arrived" ? "succeeded" : motorStatus == "cancelled" ? "cancelled" : "failed";
                        leg.reason = motorStatus == "arrived"
                            ? "聊天启动的导航已被任务框架承接；到达原目标：" + AdoptedAnchor
                            : motorStatus + ": " + Motor.LastError;
                    }
                    if (motorStatus == "arrived")
                    {
                        LastReport = "聊天导航完成：" + AdoptedAnchor + "（原聊天导航已由任务框架承接，未重跑已走路段）";
                        AdoptedCompletion(true, "chat_navigation_arrived: " + AdoptedAnchor);
                        Finish("succeeded", "chat_navigation_arrived: " + AdoptedAnchor);
                    }
                    else if (motorStatus == "cancelled")
                    {
                        AdoptedCompletion(false, "chat_navigation_cancelled: " + (Motor.LastError ?? ""));
                        Finish("cancelled", "chat_navigation_cancelled: " + (Motor.LastError ?? ""));
                    }
                    else
                    {
                        AdoptedCompletion(false, "chat_navigation_stopped: " + motorStatus + ": " + (Motor.LastError ?? ""));
                        Finish("failed", "chat_navigation_stopped: " + motorStatus + ": " + (Motor.LastError ?? ""));
                    }
                    yield break;
                }
                yield return null;
            }
        }
    }
}
