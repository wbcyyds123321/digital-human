using System;
using System.Collections;
using UnityEngine;
using Yixuan.Stage4;
using Yixuan.Pick;

namespace Yixuan.AI.Tasks
{
    public partial class TaskMissionRunner
    {
        Stage4SeatController _ownedSeatTransition;
        string _ownedSeatEntity = "";

        string ValidateForcedSelectionPlan(MissionPlan plan)
        {
            if (plan == null || plan.steps == null) return "missing_plan_context";
            foreach (var step in plan.steps)
            {
                string error = ValidateConfirmedStep(step);
                if (error != null) return error;
            }
            return null;
        }

        string ValidateConfirmedStep(MissionStep step)
        {
            // A verified task-scoped handle is authorized by its declared contract
            // slot (checked by TaskContractRules.Policy/Matches before any effect).
            // It never widens authorization: it is only accepted when it resolves in
            // this mission's ledger and the slot matches.
            if (step != null && !string.IsNullOrEmpty(step.target_ref))
            {
                if (SelectionLedger == null) return "selection_ledger_missing";
                TaskTargetBinding binding;
                string handleError;
                if (!SelectionLedger.TryResolveHandle(step.target_ref, out binding, out handleError)) return "verified_target_handle_invalid: " + handleError;
                if (step.target_slot != binding.slot) return "verified_target_handle_slot_mismatch";
                if ((step.kind == "sit" || step.kind == "stand") && string.IsNullOrEmpty(binding.slot)) return "confirmed_selection_required";
                return null;
            }
            if (string.IsNullOrEmpty(_runForcedEntityId))
                return step != null && (step.kind == "sit" || step.kind == "stand") ? "confirmed_selection_required" : null;
            if (!SelectionTokenStillValid()) return "confirmed_selection_token_expired";
            if (step == null) return "missing_selection_step";
            string kind = step.kind;
            bool targetEffect = kind == "navigate" || kind == "look_at" || kind == "sit" || kind == "stand" ||
                kind == "board_vehicle" || kind == "interact" || kind == "query_dynamic" || kind == "route_progress";
            if (targetEffect && step.target_id != _runForcedEntityId) return "effect_target_not_confirmed:" + step.target_id;
            if (kind == "return" || kind == "move_relative" || kind == "drive_vehicle" || kind == "exit_vehicle" || kind == "set_flame")
                return "effect_outside_selection_action:" + kind;
            if (kind == "sit" && _runForcedAction != "sit" ||
                kind == "stand" && _runForcedAction != "stand" ||
                kind == "board_vehicle" && _runForcedAction != "board" ||
                kind == "interact" && _runForcedAction != "interact")
                return "effect_not_authorized_by_selection:" + kind;
            if ((kind == "navigate" || kind == "route_progress") && _runForcedAction == "stand")
                return "standing_does_not_authorize_navigation";
            return null;
        }

        string ValidateSelectionContract(TaskContract contract)
        {
            if (string.IsNullOrEmpty(_runForcedEntityId)) return null;
            if (contract == null || contract.obligations == null) return "missing_selection_contract";
            string required = _runForcedAction == "approach" ? "navigate" :
                _runForcedAction == "board" ? "board_vehicle" : _runForcedAction;
            bool found = false;
            foreach (var obligation in contract.obligations)
            {
                if (obligation == null) return "missing_selection_obligation";
                string error = ValidateConfirmedStep(new MissionStep { kind = obligation.kind, target_id = obligation.target_id });
                if (error != null) return error;
                if (obligation.kind == required && obligation.target_id == _runForcedEntityId) found = true;
            }
            return found ? null : "selection_action_missing_from_contract:" + required;
        }

        void CancelOwnedSeatTransition()
        {
            var seat = _ownedSeatTransition;
            _ownedSeatTransition = null;
            if (seat != null && seat.CurrentEntity == _ownedSeatEntity &&
                seat.State != Stage4SeatController.SeatState.Idle && seat.State != Stage4SeatController.SeatState.Failed)
                seat.RequestCancel();
            _ownedSeatEntity = "";
        }

        IEnumerator SitSelected(MissionStep step, MissionStepResult receipt)
        {
            string error = ValidateConfirmedStep(step);
            if (error != null) { Fail(receipt, error); yield break; }
            var seat = Stage4SeatController.Instance;
            if (seat == null) { Fail(receipt, "seat_owner_unavailable"); yield break; }
            TaskAuthority.Release(Context.MissionId, 3);
            _seatAuthorityDeferred = true;
            if (!seat.TrySit(step.target_id)) { Fail(receipt, "sit_rejected:" + seat.LastError); yield break; }
            _ownedSeatTransition = seat; _ownedSeatEntity = step.target_id;
            bool completed = false;
            try
            {
                float deadline = TaskNow + Mathf.Max(15f, NavigationTimeout);
                while (TaskNow < deadline)
                {
                    if (!SelectionTokenStillValid())
                    { Fail(receipt, "confirmed_selection_token_expired"); yield break; }
                    if (seat.State == Stage4SeatController.SeatState.Failed)
                    { Fail(receipt, "sit_failed:" + seat.LastError); yield break; }
                    if (seat.CurrentEntity != step.target_id)
                    { Fail(receipt, "seat_selection_changed"); yield break; }
                    if (seat.State == Stage4SeatController.SeatState.Seated) { completed = true; break; }
                    yield return null;
                }
                if (!completed) { Fail(receipt, "sit_timeout:" + seat.State); yield break; }
                receipt.status = "succeeded"; receipt.reason = "seated_confirmed:" + step.target_id;
            }
            finally
            {
                if (!completed) CancelOwnedSeatTransition();
                else { _ownedSeatTransition = null; _ownedSeatEntity = ""; }
            }
        }

        IEnumerator StandSelected(MissionStep step, MissionStepResult receipt)
        {
            string error = ValidateConfirmedStep(step);
            if (error != null) { Fail(receipt, error); yield break; }
            var seat = Stage4SeatController.Instance;
            if (seat == null || seat.State != Stage4SeatController.SeatState.Seated || seat.CurrentEntity != step.target_id)
            { Fail(receipt, "not_seated_on_confirmed_target"); yield break; }
            _ownedSeatTransition = seat; _ownedSeatEntity = step.target_id;
            bool completed = false;
            try
            {
                seat.RequestStand();
                float deadline = TaskNow + 20f;
                while (TaskNow < deadline)
                {
                    if (!SelectionTokenStillValid()) { Fail(receipt, "confirmed_selection_token_expired"); yield break; }
                    if (seat.State == Stage4SeatController.SeatState.Idle && seat.LastStatus == "stood")
                    { completed = true; break; }
                    if (seat.State == Stage4SeatController.SeatState.Failed)
                    { Fail(receipt, "stand_failed:" + seat.LastError); yield break; }
                    yield return null;
                }
                if (!completed) { Fail(receipt, "stand_timeout:" + seat.State); yield break; }
                receipt.status = "succeeded"; receipt.reason = "stood_confirmed:" + step.target_id;
                TaskAuthority.Acquire(Context.MissionId, 1 | 2 | 8);
                _seatAuthorityDeferred = false;
            }
            finally
            {
                if (!completed) CancelOwnedSeatTransition();
                else { _ownedSeatTransition = null; _ownedSeatEntity = ""; }
            }
        }

        bool SelectionTokenStillValid()
        {
            if (string.IsNullOrEmpty(_runForcedEntityId)) return true;
            if (SelectionLedger == null) return false;
            TaskTargetBinding binding;
            return SelectionLedger.TryGetForcedBaseline(out binding) &&
                   binding.entity_id == _runForcedEntityId && binding.valid;
        }
    }
}
