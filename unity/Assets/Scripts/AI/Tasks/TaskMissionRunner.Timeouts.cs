using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yixuan.AI.Tasks
{
    public partial class TaskMissionRunner
    {
        const float MaximumNavigationSeconds = 1800f, MaximumMissionSeconds = 3600f;
        float _automaticNavigationAllowance;
        float EffectiveCodeTimeout
        {
            get { return Mathf.Min(MaximumMissionSeconds, Mathf.Clamp(CodeTimeout, 1, 600) + (CodeTimeout >= 600 ? _automaticNavigationAllowance : 0)); }
        }
        static bool IsNavigationStep(string kind)
        { return kind == "navigate" || kind == "return" || kind == "route_progress"; }
        public static float NavigationTimeBudget(IList<Vector3> route, Vector3 start, float speed, float minimum)
        {
            float length = 0; var previous = start;
            foreach (var point in route) { length += TaskTargetCatalog.FlatDistance(previous, point); previous = point; }
            if (float.IsNaN(speed) || float.IsInfinity(speed) || speed <= 0) return -1;
            float estimate = length / speed * 1.5f + 15f;
            if (estimate > MaximumNavigationSeconds) return -1;
            return Mathf.Clamp(Mathf.Max(minimum, estimate), 1, MaximumNavigationSeconds);
        }
        bool ConfigureNavigationBudget(MissionStep step, MissionStepResult result)
        {
            var previous = result.start_position;
            foreach (var point in result.route) { result.planned_path_length += TaskTargetCatalog.FlatDistance(previous, point); previous = point; }
            if (step.timeout > 0) { result.timeout_seconds = step.timeout; return true; }
            float seconds = NavigationTimeBudget(result.route, result.start_position, Motor.WalkSpeed * Time.timeScale, NavigationTimeout);
            if (seconds < 0) { Fail(result, "navigation_time_budget_unavailable"); return false; }
            result.timeout_seconds = seconds;
            _automaticNavigationAllowance = Mathf.Min(MaximumMissionSeconds - 600, _automaticNavigationAllowance + Mathf.Max(0, seconds - 120));
            EmitRuntime("NavigationBudget", result.id, "", JsonUtility.ToJson(new NavigationBudgetRecord { path_length = result.planned_path_length, walk_speed = Motor.WalkSpeed, step_seconds = seconds, mission_seconds = EffectiveCodeTimeout }));
            return true;
        }
        [Serializable] sealed class NavigationBudgetRecord
        { public float path_length, walk_speed, step_seconds, mission_seconds; }
    }
}
