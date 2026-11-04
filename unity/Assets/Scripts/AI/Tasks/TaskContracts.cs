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
        public bool camera_only;
    }
    [Serializable] public class MissionStepResult
    {
        public string id, kind, target_id, target_path, action_id, clip_name, status = "pending", reason, observation, frame_path, frame_sha256;
        public string direction, text, emotion, reference;public float duration; public int generation; public long event_sequence;
        public float requested_yaw,requested_pitch,actual_yaw,actual_pitch,requested_distance;
        public string route_id; public float route_fraction,route_total,route_travelled; public bool route_done;
        public float started, ended, path_length, distance_error, facing_error;
        public int attempts;
        public bool camera_only, target_in_frame;
        public bool condition_known, condition_value, condition_evaluated;
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
}
