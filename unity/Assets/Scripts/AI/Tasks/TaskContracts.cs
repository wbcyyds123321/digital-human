using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.AI.Tasks
{
    [Serializable] public class MissionPlan { public string summary; public MissionStep[] steps; }
    [Serializable] public class MissionStep
    {
        public string id, kind, target_id, text, emotion, action_id, direction, reference;
        public float yaw, pitch, distance; public bool hold=true;
        public string route_id; public float route_fraction;
        public string[] after;
        public float duration, timeout;
        public bool camera_only, body_facing;
        // JsonUtility does not preserve nullable booleans; the companion flag
        // distinguishes an explicit extinguish request from a missing argument.
        public bool lit, lit_specified;
    }
    [Serializable] public class MissionStepResult
    {
        public string id, kind, target_id, target_path, action_id, clip_name, status = "pending", reason, observation, frame_path, frame_sha256;
        public string direction, text, emotion, reference;public float duration; public int generation; public long event_sequence;
        public float requested_yaw,requested_pitch,actual_yaw,actual_pitch,requested_distance;
        public string route_id; public float route_fraction,route_total,route_travelled; public bool route_done;
        public float started, ended, path_length, distance_error, facing_error;
        public float timeout_seconds, planned_path_length;
        public int attempts;
        public bool camera_only, body_facing, target_in_frame;
        public bool condition_known, condition_value, condition_evaluated;
        public bool lit, lit_specified, actual_lit, actual_lit_known;
        public float gaze_error, body_rotation, position_drift;
        public Vector3 focus_position, viewport_position;
        public string focus_source;
        public Vector3 start_position, end_position, target_position, approach_position;
        public List<Vector3> route = new List<Vector3>();
    }
    [Serializable] public class MissionEvidence
    {
        public string request, status, reason, report, navigation_backend = "WorldData A* + CharacterLocomotion";
        public string planner_model, vision_model;
        public string execution_backend = "Legacy JSON DAG", program_path;
        // Tool timestamps use TaskNow (active monotonic time); event timestamps use Unity real time.
        public string time_basis="active_monotonic_excluding_insertions";
        public float wall_started,wall_ended,wall_elapsed;
        public string parent_mission_id; public float paused_seconds; public List<TaskInsertionEvidence> insertions=new List<TaskInsertionEvidence>();
        public string request_id,mission_id,scene_version,contract_source;public int generation;
        public TaskContract contract;public TaskCompletion completion;
        public int code_slices, code_calls;
        public int llm_calls, navigation_retries;
        public float started, ended, elapsed, movement_seconds, actual_path_length;
        public Vector3 origin, task_origin, final_position;
        public float origin_yaw, task_origin_yaw, final_yaw;
        public MissionIntent intent;
        public MissionPlan plan;
        public List<MissionStepResult> results = new List<MissionStepResult>();
    }
    [Serializable] public class DestinationSnapshot
    {
        public string id, name, path, description;
        public string[] aliases;
        public Vector3 position;
        public bool interactable;
    }

    [Serializable] public sealed class DynamicTargetFact
    {
        public string id, name, kind, unavailable_reason, state;
        public string[] color_names;
        public Vector3 position;
        public float yaw;
        public bool available;
    }
    [Serializable] public sealed class SelectionFact
    {
        public bool confirmed;
        public string entity_id, name, cls, capabilities, source;
        // Immutable observation (publish-time) values:
        public Vector3 observed_position, surface_point;
        public long frame_id;
        public double time_since_startup;
        // Live re-sampled current values (R5): recomputed every query, never the
        // stale publish snapshot for a moving vehicle/character.
        public bool available;
        public string unavailable_reason, position_source;
        public Vector3 current_position;
        public float distance_now_m, bearing_now_deg;
        public double sampled_at, sampled_age_s;
        public float observed_to_current_m;
        public int generation;
        public string note;
    }

    [Serializable] public sealed class TaskDynamicSnapshot
    {
        public bool world_ready, actor_known, in_vehicle, vehicle_moving, flame_known, flame_lit;
        public string actor_mode, active_actor, current_vehicle_id, flame_id;
        // Vehicle occupancy and seat posture have different owners. OnFoot does
        // not imply standing: Stage4 may still hold a settled seated pose.
        public bool seat_known, seated;
        public string seat_state, current_seat_entity_id;
        public Vector3 actor_position;
        public float actor_yaw;
        public List<DynamicTargetFact> targets = new List<DynamicTargetFact>();
        public SelectionFact selection = new SelectionFact();
    }
    /// <summary>One read-only source for semantic routing, contract extraction and Lua state.</summary>
    public static class TaskDynamicFacts
    {
        public static TaskDynamicSnapshot Snapshot(Transform character)
        {
            var actor = Yixuan.Dynamic.DynamicActorSystem.Instance;
            var world = Yixuan.Dynamic.DynamicWorld.Instance;
            var seat = Yixuan.Stage4.Stage4SeatController.Instance;
            var facts = new TaskDynamicSnapshot
            {
                world_ready = world != null && world.Ready,
                actor_known = actor != null,
                actor_mode = actor != null ? actor.CurrentMode.ToString() : "unavailable",
                active_actor = actor != null ? actor.ActiveActor : "unavailable",
                current_vehicle_id = actor != null ? actor.CurrentVehicleId ?? "" : "",
                in_vehicle = actor != null && actor.InVehicle,
                vehicle_moving = actor != null && actor.VehicleMoving,
                seat_known = seat != null,
                seat_state = seat != null ? seat.State.ToString() : "unavailable",
                seated = seat != null && seat.State == Yixuan.Stage4.Stage4SeatController.SeatState.Seated,
                current_seat_entity_id = seat != null ? seat.CurrentEntity ?? "" : "",
                actor_position = character != null ? character.position : Vector3.zero,
                actor_yaw = character != null ? character.eulerAngles.y : 0f
            };
            var confirmed = Yixuan.Pick.SelectionBroker.Current;
            if (confirmed != null && confirmed.confirmed)
            {
                var live = Yixuan.Pick.PickService.SampleLive(confirmed, character);
                facts.selection = new SelectionFact
                {
                    confirmed = true,
                    entity_id = confirmed.entity_id,
                    name = confirmed.display_name,
                    cls = confirmed.cls,
                    capabilities = confirmed.capabilities,
                    source = confirmed.source,
                    observed_position = confirmed.entity_position,
                    surface_point = confirmed.surface_point,
                    frame_id = confirmed.frame_id,
                    time_since_startup = confirmed.time_since_startup,
                    available = live.available,
                    unavailable_reason = live.reason,
                    position_source = live.position_source,
                    current_position = live.current_position,
                    distance_now_m = live.distance_m,
                    bearing_now_deg = live.bearing_deg,
                    sampled_at = live.sampled_time,
                    sampled_age_s = live.age_seconds,
                    observed_to_current_m = live.observed_to_current_m,
                    generation = confirmed.generation,
                    note = confirmed.live_position_note,
                };
            }
            else
            {
                facts.selection = new SelectionFact { confirmed = false, note = "no_confirmed_selection" };
            }
            if (!facts.world_ready) return facts;
            foreach (var vehicle in world.Vehicles)
            {
                if (vehicle == null || vehicle.Spec == null || vehicle.Spec.@class == "tram") continue;
                facts.targets.Add(new DynamicTargetFact
                {
                    id = vehicle.Spec.entity_id, name = vehicle.Spec.display_name, kind = vehicle.Spec.@class,
                    color_names = vehicle.Spec.color_names,
                    position = vehicle.Center, yaw = vehicle.CurrentYaw, available = vehicle.Spec.available,
                    unavailable_reason = vehicle.Spec.available ? "" : vehicle.Spec.unavailable_reason,
                    state = vehicle.Spec.entity_id == facts.current_vehicle_id ?
                        (facts.vehicle_moving ? "occupied_moving" : "occupied_stopped") : "parked"
                });
            }
            if (world.Tram != null && world.Tram.Spec != null)
                facts.targets.Add(new DynamicTargetFact
                {
                    id = world.Tram.Spec.entity_id, name = world.Tram.Spec.display_name, kind = "tram",
                    position = world.Tram.Center, yaw = world.Tram.CurrentYaw, available = false,
                    unavailable_reason = "tram_boarding_and_driving_not_implemented", state = world.Tram.State
                });
            if (world.Flame != null && world.Flame.Spec != null)
            {
                facts.flame_known = true; facts.flame_lit = world.Flame.Lit; facts.flame_id = world.Flame.Spec.entity_id;
                var p = world.Flame.Spec.pos;
                facts.targets.Add(new DynamicTargetFact
                {
                    id = facts.flame_id, name = "圣火", kind = "eternal_flame", available = true,
                    position = p != null && p.Length >= 3 ? new Vector3(p[0], p[1], p[2]) : Vector3.zero,
                    state = facts.flame_lit ? "lit" : "extinguished"
                });
            }
            return facts;
        }
        public static string SnapshotJson(Transform character) { return JsonUtility.ToJson(Snapshot(character)); }
    }
}
