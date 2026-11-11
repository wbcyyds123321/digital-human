// Node-2 dynamic mission routines: board / drive / exit / stop / set_flame / query.
// These extend the existing contract -> Lua -> runner -> receipt pipeline; they never create a
// second authority system and every effect is reported through the normal MissionStepResult.
using System.Collections;
using UnityEngine;
using Yixuan.Dynamic;
using Yixuan.Stage4;
using Yixuan.World;

namespace Yixuan.AI.Tasks
{
    public partial class TaskMissionRunner
    {
        DynamicActorSystem _dynamicLeaseActor;
        DynamicActorSystem.TaskLease _dynamicLease;

        bool AcquireDynamicControl(DynamicActorSystem actor, out string status)
        {
            status = "dynamic_world_not_ready";
            if (actor == null || actor.World == null || !actor.World.Ready) return false;
            if (Context == null || !Context.Active) { status = "inactive_mission"; return false; }
            if (_dynamicLease != null)
            {
                if (_dynamicLeaseActor == actor && actor.OwnsTask(_dynamicLease) && _dynamicLease.Owner == Context.MissionId)
                    return true;
                status = "dynamic_task_lease_lost";
                return false;
            }
            var lease = actor.AcquireTaskLease(Context.MissionId);
            if (lease == null) { status = "dynamic_owned_by_another_task"; return false; }
            _dynamicLeaseActor = actor;
            _dynamicLease = lease;
            return true;
        }

        void ReleaseDynamicControl()
        {
            var actor = _dynamicLeaseActor;
            var lease = _dynamicLease;
            _dynamicLeaseActor = null;
            _dynamicLease = null;
            // Identity comparison in ReleaseTask prevents an old run stopping its replacement.
            if (actor != null && lease != null) actor.ReleaseTask(lease);
        }
        DynamicActorSystem DynamicActor
        {
            get
            {
                var actor = DynamicActorSystem.Instance;
                if (actor == null) actor = FindObjectOfType<DynamicActorSystem>();
                return actor;
            }
        }

        IEnumerator BoardVehicle(MissionStep s, MissionStepResult r)
        {
            var actor = DynamicActor;
            if (actor == null || actor.World == null || !actor.World.Ready) { Fail(r, "dynamic_world_not_ready"); yield break; }
            if (!AcquireDynamicControl(actor, out var controlStatus)) { Fail(r, controlStatus); yield break; }
            var lease = _dynamicLease;
            var vehicle = actor.World.GetVehicle(s.target_id);
            if (vehicle == null || !vehicle.Spec.available)
            { Fail(r, vehicle == null ? "unknown_vehicle:" + s.target_id : "vehicle_unavailable:" + vehicle.Spec.unavailable_reason); yield break; }
            var seat = FindObjectOfType<Stage4SeatController>();
            if (Stage4SeatController.BlocksNormalWalking)
            {
                if (seat == null) { Fail(r, "seated_but_seat_controller_missing"); yield break; }
                seat.RequestStand();
                float standUntil = TaskNow + 20f;
                while (Stage4SeatController.BlocksNormalWalking && TaskNow < standUntil) yield return null;
                if (Stage4SeatController.BlocksNormalWalking) { Fail(r, "stand_before_boarding_timeout"); yield break; }
            }
            if (Context == null || !Context.Active || !actor.OwnsTask(lease) ||
                !TaskAuthority.Acquire(Context.MissionId, 1|2|8))
            { Fail(r, "resources_owned_by_another_run"); yield break; }
            _seatAuthorityDeferred = false;
            AcquireMissionCamera();
            if (actor.InVehicle)
            {
                if (actor.CurrentVehicleId == s.target_id)
                {
                    r.status = "succeeded"; r.reason = "already_in_vehicle"; r.end_position = Motor.transform.position;
                    yield break;
                }
                Fail(r, "in_vehicle_other_vehicle_must_exit_first");
                yield break;
            }
            if (actor.CurrentMode == DynamicActorSystem.Mode.Boarding)
            {
                Fail(r, "already_boarding_another_vehicle");
                yield break;
            }
            var target = Catalog.Get(s.target_id);
            if (target != null) r.target_position = target.Position;
            string status;
            if (!actor.BeginBoard(s.target_id, out status)) { Fail(r, status); yield break; }
            int operation = actor.RequestGeneration;
            bool completed = false;
            r.observation = status;
            try
            {
                float until = TaskNow + (s.timeout > 0 ? s.timeout : 240f);
                while (actor.OwnsTask(lease) && actor.RequestGeneration == operation &&
                       actor.CurrentMode == DynamicActorSystem.Mode.Boarding && TaskNow < until) yield return null;
                if (actor.OwnsTask(lease) && actor.RequestGeneration == operation &&
                    actor.InVehicle && actor.CurrentVehicleId == s.target_id)
                {
                    completed = true;
                    r.status = "succeeded";
                    r.reason = "boarded_vehicle";
                    r.observation = actor.Describe();
                    r.end_position = Motor.transform.position;
                }
                else Fail(r, "board_failed: state=" + actor.LastStatus + " error=" + actor.LastError);
            }
            finally
            {
                if (!completed) actor.CancelOperation(lease, operation, out _);
            }
        }

        IEnumerator DriveVehicle(MissionStep s, MissionStepResult r)
        {
            var actor = DynamicActor;
            if (actor == null || !actor.InVehicle) { Fail(r, "not_in_vehicle"); yield break; }
            if (!AcquireDynamicControl(actor, out var controlStatus)) { Fail(r, controlStatus); yield break; }
            var lease = _dynamicLease;
            if (s.target_id == actor.CurrentVehicleId) { Fail(r, "drive_target_is_current_vehicle"); yield break; }
            var target = Catalog.Get(s.target_id);
            if (target == null) { Fail(r, "unknown_target: " + s.target_id); yield break; }
            Vector3 destination = target.Position;
            r.target_path = target.Path;
            r.target_position = destination;
            string status;
            if (!actor.DriveTo(new Vector2(destination.x, destination.z), out status))
            {
                Fail(r, status);
                yield break;
            }
            r.observation = status;
            int operation = actor.RequestGeneration;
            bool completed = false;
            try
            {
                float until = TaskNow + (s.timeout > 0 ? s.timeout : 600f);
                while (actor.OwnsTask(lease) && actor.RequestGeneration == operation && actor.VehicleMoving && TaskNow < until)
                    yield return null;
                if (!actor.OwnsTask(lease) || actor.RequestGeneration != operation)
                { Fail(r, "drive_operation_replaced_or_cancelled"); yield break; }
                if (actor.VehicleMoving)
                { Fail(r, "drive_timeout_stopped_in_vehicle"); yield break; }
                if (actor.LastStatus == "parked")
                {
                    completed = true;
                    r.status = "succeeded";
                    r.reason = "parked_vehicle";
                    r.end_position = Motor.transform.position;
                    r.path_length = actor.LastDriveDistance;
                    r.observation = "parked at " + actor.LastParkPoint.ToString("F1") +
                                    " moved=" + actor.LastDriveDistance.ToString("F1") + "m";
                }
                else Fail(r, "drive_failed: " + actor.LastStatus + " error=" + actor.LastError);
            }
            finally
            {
                if (!completed) actor.CancelOperation(lease, operation, out _);
            }
        }

        IEnumerator ExitVehicle(MissionStep s, MissionStepResult r)
        {
            var actor = DynamicActor;
            if (actor == null || !actor.InVehicle) { Fail(r, "not_in_vehicle"); yield break; }
            if (!AcquireDynamicControl(actor, out var controlStatus)) { Fail(r, controlStatus); yield break; }
            string status;
            if (actor.TryExitVehicle(out status))
            {
                r.status = "succeeded";
                r.reason = "exited_vehicle";
                r.end_position = Motor.transform.position;
                r.observation = "exit_point=" + actor.LastExitPoint.ToString("F1");
            }
            else
            {
                r.status = "failed";
                r.reason = status;
            }
            yield break;
        }

        IEnumerator StopVehicle(MissionStep s, MissionStepResult r)
        {
            var actor = DynamicActor;
            if (actor == null || !actor.InVehicle) { Fail(r, "not_in_vehicle"); yield break; }
            if (!AcquireDynamicControl(actor, out var controlStatus)) { Fail(r, controlStatus); yield break; }
            string status;
            bool ok = actor.StopCurrent(out status);
            r.status = ok ? "succeeded" : "failed";
            r.reason = status;
            r.observation = "mode=" + actor.CurrentMode + " moving=" + actor.VehicleMoving;
            yield break;
        }

        IEnumerator SetFlame(MissionStep s, MissionStepResult r)
        {
            var actor = DynamicActor;
            if (actor == null) { Fail(r, "dynamic_world_not_ready"); yield break; }
            if (!s.lit_specified) { Fail(r, "flame_lit_parameter_required"); yield break; }
            if (actor.World == null || actor.World.Flame == null || s.target_id != actor.World.Flame.Spec.entity_id)
            { Fail(r, "unknown_flame_target"); yield break; }
            if (!AcquireDynamicControl(actor, out var controlStatus)) { Fail(r, controlStatus); yield break; }
            r.lit_specified = s.lit_specified;
            r.lit = s.lit;
            bool wanted = s.lit;
            string status;
            bool ok = actor.SetFlame(wanted, out status);
            if (ok)
            {
                r.status = "succeeded";
                r.reason = status;
                r.observation = actor.DescribeFlame();
                r.actual_lit_known = true;
                r.actual_lit = actor.FlameLit;
            }
            else
            {
                r.status = "failed";
                r.reason = status;
            }
            yield break;
        }

        IEnumerator QueryDynamic(MissionStep s, MissionStepResult r)
        {
            var actor = DynamicActor;
            if (actor == null || actor.World == null || !actor.World.Ready)
            { Fail(r, "dynamic_world_not_ready"); yield break; }
            var entity = Yixuan.World.WorldEntityCatalog.Get(s.target_id);
            if (entity == null) { Fail(r, "unknown_target: " + s.target_id); yield break; }
            string observation;
            if (entity != null && entity.cls == "vehicle")
            {
                var spec = DynamicData.FindVehicle(s.target_id);
                if (spec == null)
                {
                    Fail(r, "vehicle_not_dynamic:" + s.target_id);
                    yield break;
                }
                else
                {
                    string colors = spec.color_names != null ? string.Join("/", spec.color_names) : "未知";
                    observation = "vehicle:" + spec.display_name + " class=" + spec.@class +
                                  " color=" + colors + " available=" + spec.available +
                                  (spec.available ? "" : " reason=" + spec.unavailable_reason) +
                                  " position=" + Yixuan.World.WorldEntityCatalog.WorldPosition(entity).ToString("F1") +
                                  " actor_mode=" + actor.CurrentMode;
                }
            }
            else if (entity != null && entity.cls == "eternal_flame")
            {
                if (actor.World.Flame == null || actor.World.Flame.Spec.entity_id != s.target_id)
                { Fail(r, "flame_not_loaded"); yield break; }
                observation = "flame:" + actor.DescribeFlame();
            }
            else
            {
                Fail(r, "unsupported_dynamic_target:" + s.target_id);
                yield break;
            }
            r.observation = observation;
            r.status = "succeeded";
            r.reason = "query";
            yield break;
        }
    }
}
