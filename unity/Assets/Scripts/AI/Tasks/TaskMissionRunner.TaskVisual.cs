using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Yixuan.Pick;
using Yixuan.Vision;

namespace Yixuan.AI.Tasks
{
    /// <summary>
    /// Node A runtime: awaitable task-scoped visual selection, symbolic target
    /// slots and verified target handles, single-task camera/eye continuity and
    /// receipt-derived phase summary.
    /// </summary>
    public partial class TaskMissionRunner
    {
        public TaskSelectionLedger SelectionLedger { get; private set; }
        public TaskPhaseSummary PhaseSummary { get; private set; } = new TaskPhaseSummary();
        public string PhaseSummaryText { get { return PhaseSummary != null ? PhaseSummary.text : ""; } }
        public string SelectionLedgerSummary { get { return SelectionLedger != null ? SelectionLedger.Describe() : "no_target_bindings"; } }

        [Serializable]
        sealed class TaskObservationGroupRecord
        {
            public string schema = "task-observation-group.v1";
            public string group_id, mode, owner_slot, source = "frozen_eye_camera_frame";
            public long frame_id;
            public float captured_at, age_seconds;
            public Vector3 character_position, eye_position, eye_forward;
            public float body_yaw, eye_yaw, eye_pitch;
            public string policy = "immutable RGB+same-frame ID/depth+eye parameters; never replaced silently";
        }

        sealed class TaskObservationGroup
        {
            public string groupId = "";
            public string mode = "task_start_view";
            public string ownerSlot = "";
            public bool startGroup;
            public PickFrame frame;
            public long frameId = -1;
            public float capturedAt = -1f;
            public Vector3 position;
            public float bodyYaw, eyeYaw, eyePitch;
        }

        TaskObservationGroup _startObservation;
        TaskObservationGroup _afterObservation;
        // Two independent states (F3):
        //  - _startObservationInvalidated is IRREVERSIBLE for this task once an explicit
        //    body/camera step happens; capturing an after_motion group never revives it.
        //  - _afterObservationDirty only says the latest after_motion group predates the
        //    most recent explicit motion, so the next after_motion select captures a new
        //    explicit group.
        bool _startObservationInvalidated;
        bool _afterObservationDirty;
        int _observationGroupSeq;

        const float ObservationMaxAgeSeconds = 15f;
        const float ObservationMaxDriftMeters = 0.05f;

        float _selectionLivenessAt;
        int _lastObservedBrokerGeneration = -1;
        MissionStep _activeStep;
        int _selectVisibleCount;

        /// <summary>True while the global UI selection has not been superseded by an external intent.</summary>
        public bool SelectionIntegrationValid(out string reason)
        {
            reason = "";
            var ledger = SelectionLedger;
            if (ledger == null) return true;
            int generation = SelectionBroker.Generation;
            if (generation != _lastObservedBrokerGeneration)
            {
                _lastObservedBrokerGeneration = generation;
                if (!ledger.IsOwnedBrokerGeneration(generation))
                {
                    reason = "external_selection_or_visual_intent_changed generation=" + generation;
                    return false;
                }
            }
            if (TaskNow >= _selectionLivenessAt)
            {
                _selectionLivenessAt = TaskNow + 0.25f;
                if (!ledger.ValidateAllLive(out reason)) return false;
            }
            if (!string.IsNullOrEmpty(_runForcedEntityId))
            {
                TaskTargetBinding forced;
                if (!ledger.TryGetForcedBaseline(out forced) || !forced.valid || forced.entity_id != _runForcedEntityId)
                {
                    reason = "confirmed_selection_handle_invalid";
                    return false;
                }
            }
            return true;
        }

        /// <summary>Create the per-mission ledger and reset every task-scoped observation/lifecycle field.</summary>
        void InitTaskSelection()
        {
            SelectionLedger = new TaskSelectionLedger(_generation, Context != null ? Context.MissionId : "", Context != null ? Context.SceneVersion : "");
            _lastObservedBrokerGeneration = SelectionBroker.Generation;
            SelectionLedger.RegisterOwnedBrokerGeneration(SelectionBroker.Generation);
            _startObservation = null;
            _afterObservation = null;
            _startObservationInvalidated = false;
            _afterObservationDirty = false;
            _observationGroupSeq = 0;
            _activeStep = null;
            _selectVisibleCount = 0;
            _selectionLivenessAt = 0;
            if (!string.IsNullOrEmpty(_runForcedEntityId) && !SelectionLedger.TrySeedForcedBaseline(_runForcedEntityId, _runForcedAction))
                Debug.LogWarning("[Task] confirmed selection handoff could not be seeded: " + _runForcedEntityId);
            RebuildPhaseSummary();
        }

        void ReleaseTaskSelection(string reason)
        {
            _startObservationInvalidated = true;
            _afterObservationDirty = true;
            _activeStep = null;
            if (SelectionLedger != null) SelectionLedger.InvalidateAll(reason);
            if (Catalog != null) Catalog.ClearTaskBoundTargets();
        }

        void MarkObservationStale()
        {
            // The frames are NOT thrown away or silently replaced. A later slot that
            // declared observe='task_start_view' must fail explicitly with
            // observation_expired; observe='after_motion' captures a NEW explicit group.
            // Starting-group invalidation is irreversible for the whole task (F3): a
            // later after_motion capture must NOT clear it.
            _startObservationInvalidated = true;
            _afterObservationDirty = true;
        }

        static bool StepChangesObservation(string kind)
        {
            return kind == "navigate" || kind == "return" || kind == "route_progress" || kind == "move_relative" ||
                   kind == "turn_body" || kind == "look_at" || kind == "look_direction" || kind == "observe";
        }

        void RecordActiveStep(MissionStep step)
        {
            _activeStep = step;
        }

        /// <summary>
        /// Register every still-valid binding in the live task target catalog.
        /// TaskTargetCatalog.Refresh re-adds registered task-bound targets, so a
        /// global refresh can never drop the in-task confirmed target A.
        /// </summary>
        void ReAddBoundTargetsToCatalog()
        {
            if (Catalog == null || SelectionLedger == null) return;
            foreach (var binding in SelectionLedger.Bindings)
            {
                if (binding == null || !binding.valid) continue;
                var entry = EntityPickRegistry.Get(binding.entity_id);
                if (entry == null || !entry.Selectable) continue;
                Catalog.RegisterTaskBoundTarget(entry.Id, entry.DisplayName);
            }
        }

        static string ObservationModeFor(TaskTargetSlot slot)
        {
            string observe = (slot != null ? slot.observe : null) ?? "";
            if (observe.IndexOf("after", StringComparison.OrdinalIgnoreCase) >= 0 ||
                observe.IndexOf("new", StringComparison.OrdinalIgnoreCase) >= 0 ||
                observe.IndexOf("current", StringComparison.OrdinalIgnoreCase) >= 0) return "after_motion";
            return "task_start_view";
        }

        bool ObservationGroupUnsafe(TaskObservationGroup group, out string reason)
        {
            reason = "";
            if (group == null) { reason = "missing_group"; return true; }
            if (group.frame == null || !group.frame.Ok) { reason = "frame_invalid"; return true; }
            if (TaskNow - group.capturedAt > ObservationMaxAgeSeconds)
            { reason = "age_exceeded:" + (TaskNow - group.capturedAt).ToString("F1") + "s>" + ObservationMaxAgeSeconds + "s"; return true; }
            if (Motor != null && TaskTargetCatalog.FlatDistance(Motor.transform.position, group.position) > ObservationMaxDriftMeters)
            { reason = "character_moved"; return true; }
            if (Motor != null && Mathf.Abs(Mathf.DeltaAngle(Motor.transform.eulerAngles.y, group.bodyYaw)) > 1.5f)
            { reason = "body_turned"; return true; }
            return false;
        }

        IEnumerator CaptureObservationGroup(MissionStepResult r, string mode, string ownerSlot, bool afterMotion, string failContext)
        {
            if (!PickService.EnsureRegistry())
            {
                Fail(r, failContext + ": selection_registry_unavailable: " + EntityPickRegistry.Error);
                yield break;
            }
            var frame = PickService.CaptureEyeFrame();
            if (frame == null || !frame.Ok)
            {
                Fail(r, failContext + ": observation_capture_failed: " + (PickService.LastError ?? "null"));
                yield break;
            }
            var group = new TaskObservationGroup
            {
                groupId = "obs_" + (++_observationGroupSeq),
                mode = mode,
                ownerSlot = ownerSlot ?? "",
                startGroup = !afterMotion,
                frame = frame,
                frameId = frame.FrameId,
                capturedAt = TaskNow,
                position = Motor != null ? Motor.transform.position : Vector3.zero,
                bodyYaw = Motor != null ? Motor.transform.eulerAngles.y : 0f,
                eyeYaw = Gimbal != null ? Gimbal.CurrentYaw : 0f,
                eyePitch = Gimbal != null ? Gimbal.CurrentPitch : 0f,
            };
            if (afterMotion) { _afterObservation = group; _afterObservationDirty = false; }
            else _startObservation = group;
            var record = new TaskObservationGroupRecord
            {
                group_id = group.groupId,
                mode = group.mode,
                owner_slot = group.ownerSlot,
                frame_id = group.frameId,
                captured_at = group.capturedAt,
                age_seconds = 0f,
                character_position = group.position,
                body_yaw = group.bodyYaw,
                eye_yaw = group.eyeYaw,
                eye_pitch = group.eyePitch,
                eye_position = frame.snapshot != null ? frame.snapshot.position : Vector3.zero,
                eye_forward = frame.snapshot != null ? frame.snapshot.Forward : Vector3.forward,
            };
            Write("observation_group_" + group.groupId + ".json", JsonUtility.ToJson(record, true));
            EmitRuntime("ObservationGroupCaptured", ownerSlot, mode, group.groupId + "|frame=" + group.frameId);
            r.observation_group_id = group.groupId;
            r.observation_mode = group.mode;
            r.locate_frame_id = group.frameId;
            r.observation_age_s = 0f;
            yield return null;
        }

        /// <summary>
        /// Resolve the immutable observation group for this slot. task_start_view is a
        /// single frozen group for the whole task and is NEVER silently replaced; if it
        /// is no longer safe the select fails with observation_expired. after_motion
        /// captures one explicit new group after the latest body/camera-changing step.
        /// </summary>
        IEnumerator ResolveObservationGroup(MissionStepResult r, TaskTargetSlot slot, Action<TaskObservationGroup> done)
        {
            done = done ?? (g => { });
            string mode = ObservationModeFor(slot);
            if (mode == "after_motion")
            {
                if (_afterObservation == null || _afterObservationDirty)
                {
                    yield return CaptureObservationGroup(r, mode, slot.id, true, "observation_group_new");
                    if (r.status != "running") yield break;
                }
                else
                {
                    string unsafeReason;
                    if (ObservationGroupUnsafe(_afterObservation, out unsafeReason))
                    {
                        Fail(r, "observation_expired: after_motion group " + _afterObservation.groupId + " is no longer safe (" + unsafeReason + "); declare a new observation explicitly");
                        yield break;
                    }
                }
                r.observation_group_id = _afterObservation.groupId;
                r.observation_mode = mode;
                r.locate_frame_id = _afterObservation.frameId;
                r.observation_age_s = TaskNow - _afterObservation.capturedAt;
                done(_afterObservation);
                yield break;
            }
            if (_startObservation == null)
            {
                if (_startObservationInvalidated)
                {
                    Fail(r, "observation_expired: task_start_view was never captured and a body/camera-changing step already ran; declare observe='after_motion' for a new observation group");
                    yield break;
                }
                yield return CaptureObservationGroup(r, mode, slot.id, false, "observation_group_start");
                if (r.status != "running") yield break;
            }
            else
            {
                string unsafeReason = "";
                bool unsafeGroup = _startObservationInvalidated || ObservationGroupUnsafe(_startObservation, out unsafeReason);
                if (_startObservationInvalidated && string.IsNullOrEmpty(unsafeReason)) unsafeReason = "explicit_body_or_camera_changed";
                if (unsafeGroup)
                {
                    Fail(r, "observation_expired: task_start_view group " + _startObservation.groupId + " is no longer the original view (" + unsafeReason + "); declare observe='after_motion' for a new observation group");
                    yield break;
                }
            }
            r.observation_group_id = _startObservation.groupId;
            r.observation_mode = mode;
            r.locate_frame_id = _startObservation.frameId;
            r.observation_age_s = TaskNow - _startObservation.capturedAt;
            done(_startObservation);
        }

        static FpVisualRequest BuildTaskSelectRequest(TaskTargetSlot slot)
        {
            var request = new FpVisualRequest
            {
                // The complete slot description is the final visual instruction (it must
                // preserve colour/category/relative position). The raw constraints are
                // also carried so the instruction/evidence stay auditable; any_match only
                // means "choose any entity inside the described constrained set".
                original_text = slot.description,
                target_description = slot.description,
                raw_constraints = slot.constraints ?? "",
                effective_constraints = slot.constraints ?? "",
                select = true,
                current_view = true,
                provenance = "task_select_visible",
            };
            string constraints = slot.constraints ?? "";
            if (constraints.IndexOf("any_match", StringComparison.OrdinalIgnoreCase) >= 0) request.any_match = true;
            if (constraints.IndexOf("leftmost", StringComparison.OrdinalIgnoreCase) >= 0) request.leftmost = true;
            if (constraints.IndexOf("near_crosshair", StringComparison.OrdinalIgnoreCase) >= 0) request.near_crosshair = true;
            return request;
        }

        /// <summary>
        /// Awaitable visual selection tool. It reuses the production locate/bind/aim/
        /// new-frame-confirm pipeline through FirstPersonTargetingService, publishes
        /// only a real centre-confirmed record, and then stores an opaque task-scoped
        /// handle. It never submits a new runner and never returns an old selection.
        /// </summary>
        IEnumerator SelectVisible(MissionStep s, MissionStepResult r)
        {
            var ledger = SelectionLedger;
            if (ledger == null) { Fail(r, "selection_ledger_missing"); yield break; }
            if (Contract == null) { Fail(r, "select_visible_requires_contract"); yield break; }
            var slot = TaskContractRules.FindSlot(Contract, s.target_slot);
            if (slot == null) { Fail(r, "select_visible_unknown_target_slot: " + (s.target_slot ?? "")); yield break; }
            string declared = (slot.description ?? "").Trim();
            if (declared.Length == 0 || !string.Equals(declared, (s.text ?? "").Trim(), StringComparison.Ordinal))
            { Fail(r, "select_visible_description_mismatch"); yield break; }
            if (ledger.IsSlotBound(s.target_slot)) { Fail(r, "target_slot_already_bound: " + s.target_slot); yield break; }

            r.selection_description = declared;
            r.selection_source = "task_select_visible";
            r.body_yaw_before = Motor != null ? Motor.transform.eulerAngles.y : 0f;
            r.eye_yaw_before = Gimbal != null ? Gimbal.CurrentYaw : 0f;
            r.eye_pitch_before = Gimbal != null ? Gimbal.CurrentPitch : 0f;

            TaskObservationGroup group = null;
            yield return ResolveObservationGroup(r, slot, g => group = g);
            if (group == null || r.status != "running") yield break;

            var request = BuildTaskSelectRequest(slot);
            request.allow_body_assist = !(Contract != null && Contract.forbid_body_turn);

            int generation = _generation;
            int brokerToken = SelectionBroker.BeginIntent();
            ledger.RegisterOwnedBrokerGeneration(brokerToken);
            string evidenceDir = Path.Combine(RunDirectory, "select_" + (_selectVisibleCount++).ToString("00") + "_" + r.id);
            try { Directory.CreateDirectory(evidenceDir); } catch (Exception) { evidenceDir = RunDirectory; }

            FpVisualConfirmOutcome outcome = null;
            yield return FirstPersonTargetingService.RunLocateConfirmForTask(
                request, brokerToken, evidenceDir, group.frame,
                () => generation != _generation || !IsRunning || _cancel.IsCancellationRequested,
                o => outcome = o);
            if (generation != _generation) yield break;

            if (outcome == null || !outcome.Ok)
            {
                // A failed attempt must never surface the previous confirmed target as
                // this call's success. Cancel this pending intent generation so a late
                // result can never publish, and keep the generation owned by this task.
                SelectionBroker.CancelIntent();
                ledger.RegisterOwnedBrokerGeneration(SelectionBroker.Generation);
                string detail = outcome != null ? (outcome.Outcome + ": " + outcome.Error) : "no_confirm_outcome";
                Fail(r, "select_visible_failed: " + detail);
                Write(r.id + "_select_failed.json", detail);
                yield break;
            }

            var record = outcome.Record != null ? outcome.Record : SelectionBroker.Current;
            if (record == null || !record.confirmed || record.entity_id != outcome.EntityId)
            {
                SelectionBroker.CancelIntent();
                ledger.RegisterOwnedBrokerGeneration(SelectionBroker.Generation);
                Fail(r, "select_visible_publish_mismatch");
                yield break;
            }
            TaskTargetBinding binding;
            string error;
            if (!ledger.TryBindSlot(s.target_slot, declared, slot.source, record, brokerToken, outcome.LocateFrameId, out binding, out error))
            {
                Fail(r, "target_binding_rejected: " + error);
                yield break;
            }
            binding.observation_group_id = group.groupId;
            binding.observation_mode = group.mode;
            r.target_ref = binding.target_ref;
            r.target_slot = binding.slot;
            r.target_id = binding.entity_id;
            r.selection_generation = brokerToken;
            r.confirm_frame_id = outcome.ConfirmFrameId;
            r.locate_frame_id = outcome.LocateFrameId;
            r.observation_group_id = group.groupId;
            r.observation_mode = group.mode;
            r.observation_age_s = TaskNow - group.capturedAt;
            r.eye_yaw_after = Gimbal != null ? Gimbal.CurrentYaw : 0f;
            r.eye_pitch_after = Gimbal != null ? Gimbal.CurrentPitch : 0f;
            r.body_yaw_after = Motor != null ? Motor.transform.eulerAngles.y : 0f;
            r.status = "succeeded";
            r.reason = "task_scoped_visual_selection_confirmed: slot=" + binding.slot +
                       "; entity=" + binding.entity_id + "; broker_generation=" + brokerToken +
                       "; observation_group=" + group.groupId + "; locate_frame=" + outcome.LocateFrameId +
                       "; confirm_frame=" + outcome.ConfirmFrameId;
            ReAddBoundTargetsToCatalog();
            Write("selection_ledger.json", ledger.DescribeJson());
            Write(r.id + "_binding.json", JsonUtility.ToJson(binding, true));
            // The observation group stays valid intentionally: a following slot in the
            // SAME group locates against the same frozen RGB-ID-depth frame, while each
            // select still aims and confirms on its own NEW frame.
        }

        /// <summary>Resolve a task-scoped handle argument used by a target-bearing tool.</summary>
        bool TryResolveTargetHandle(string raw, MissionStep step, out string error)
        {
            error = "";
            if (!TaskSelectionLedger.LooksLikeHandle(raw)) { error = "invalid_target_handle"; return false; }
            if (SelectionLedger == null) { error = "selection_ledger_missing"; return false; }
            TaskTargetBinding binding;
            if (!SelectionLedger.TryResolveHandle(raw, out binding, out error)) return false;
            step.target_ref = raw;
            step.target_slot = binding.slot;
            step.target_id = binding.entity_id;
            return true;
        }

        /// <summary>Rebuild the phase summary from the contract + succeeded obligation instances.</summary>
        TaskPhaseSummary RebuildPhaseSummary()
        {
            bool terminal = Phase == "succeeded" || Phase == "failed" || Phase == "cancelled" || Phase == "rejected";
            string terminalReason = terminal && Evidence != null ? (Evidence.reason ?? "") : "";
            // Cancel()/Finish() advance _generation before the final summary is rebuilt, but
            // the completed receipts still belong to the mission generation recorded in
            // Evidence. Using the raw current _generation would drop every completed fact
            // from a cancelled/failed terminal summary.
            int summaryGeneration = Evidence != null ? Evidence.generation : _generation;
            PhaseSummary = TaskPhaseSummaryBuilder.Build(Contract, Evidence != null ? Evidence.results : null,
                _activeStep, Phase, summaryGeneration, TaskNow, terminal, terminalReason);
            if (Evidence != null)
            {
                Evidence.phase_summary = PhaseSummary;
                Evidence.target_bindings = SelectionLedger != null ? SelectionLedger.Count : 0;
            }
            return PhaseSummary;
        }

        void PublishPhaseSummary()
        {
            var summary = RebuildPhaseSummary();
            if (RunDirectory != null) Write("phase_summary.json", JsonUtility.ToJson(summary, true));
            EmitRuntime("PhaseSummaryUpdated", "", summary.stage, summary.text);
            Raise();
        }
    }
}
