using System;
using System.Collections.Generic;

namespace Yixuan.AI.Tasks
{
    public static class MissionPlanValidator
    {
        public static string Validate(MissionPlan plan, MissionIntent intent, TaskTargetCatalog catalog)
        {
            if (intent == null || !string.IsNullOrEmpty(intent.error)) return "invalid_request";
            if (plan == null || plan.steps == null || plan.steps.Length < 1 || plan.steps.Length > 24) return "bad_step_count";
            var ids = new Dictionary<string, MissionStep>(); var destinations = new List<string>();
            string previousMove = null; int observations = 0, interactions = 0, speeches = 0, expressions = 0;
            string previousAction = null; int actionIndex = 0; var actionSteps = new List<MissionStep>();
            foreach (var s in plan.steps)
            {
                if (s == null || string.IsNullOrWhiteSpace(s.id) || ids.ContainsKey(s.id)) return "duplicate_or_missing_step_id";
                if (s.timeout < 0 || s.timeout > 180 || s.duration < 0 || s.duration > 15 || float.IsNaN(s.duration) || float.IsNaN(s.timeout)) return "invalid_step_duration";
                foreach (string dep in s.after ?? new string[0]) if (!ids.ContainsKey(dep)) return "dependency_not_prior_or_cycle";
                switch (s.kind)
                {
                    case "navigate": case "return":
                        if (s.kind == "return" && s.target_id != "origin" && s.target_id != "mission_origin") return "return_requires_origin";
                        if (s.kind == "navigate" && catalog.Get(s.target_id) == null) return "unknown_navigation_target";
                        if ((s.target_id == "origin" || s.target_id == "mission_origin") && !intent.return_to_origin) return "unrequested_return";
                        if (previousMove != null && !DependsOn(s, previousMove, ids)) return "navigation_order_not_enforced";
                        destinations.Add(s.target_id); previousMove = s.id; break;
                    case "look_at":
                        if(catalog.Get(s.target_id)==null) return "unknown_attention_target";
                        break;
                    case "observe":
                        if (!intent.observe) return "unrequested_observe";
                        if (!string.IsNullOrEmpty(s.target_id) && s.target_id != "origin" && !ArrayContains(intent.destination_ids, s.target_id)) return "unrequested_observation_target";
                        if (!string.IsNullOrEmpty(s.target_id) && s.target_id != "origin" && !HasNavigationDependency(s, s.target_id, ids)) return "observe_before_navigation";
                        observations++; break;
                    case "interact":
                        if (!intent.interact || !ArrayContains(intent.destination_ids, s.target_id)) return "unrequested_interaction";
                        var t = catalog.Get(s.target_id);
                        if (t == null || t.Interaction == null) return "interaction_not_registered";
                        if (!HasNavigationDependency(s, s.target_id, ids)) return "interaction_before_navigation";
                        interactions++; break;
                    case "gesture":
                        if (TaskGestureSkills.Get(s.action_id) == null) return "gesture_not_installed";
                        break;
                    case "speak": if (!intent.speak || string.IsNullOrWhiteSpace(s.text) || s.text.Length > 200) return "unrequested_or_invalid_speech"; speeches++; break;
                    case "express": if (!intent.express || s.emotion != "happy" && s.emotion != "neutral") return "unrequested_or_invalid_expression"; expressions++; break;
                    case "report": break;
                    default: return "unknown_skill";
                }
                if (s.kind == "navigate" || s.kind == "return" || s.kind == "observe" || s.kind == "interact" || s.kind == "gesture" || s.kind == "look_at")
                {
                    if (actionIndex >= intent.required_actions.Length) return "extra_task_action";
                    var required = intent.required_actions[actionIndex++]; actionSteps.Add(s);
                    if (s.kind != required.kind || (s.target_id ?? "") != (required.target_id ?? "")) return "requested_action_substitution_or_order";
                    if (s.kind == "look_at" && s.camera_only != required.camera_only) return "attention_body_permission_changed";
                    if (s.kind == "gesture" && s.action_id != required.action_id) return "requested_gesture_substitution";
                    if (previousAction != null && !DependsOn(s, previousAction, ids)) return "task_action_order_not_enforced";
                    previousAction = s.id;
                }
                if (s.kind == "report" && s != plan.steps[plan.steps.Length - 1]) return "report_must_be_last";
                ids.Add(s.id, s);
            }
            if (actionIndex != intent.required_actions.Length) return "requested_action_omitted";
            if (destinations.Count != intent.destination_ids.Length) return "missing_or_extra_destination";
            for (int i = 0; i < destinations.Count; i++) if (destinations[i] != intent.destination_ids[i]) return "destination_substitution_or_order";
            if (intent.observe && observations == 0) return "requested_observation_omitted";
            if (intent.interact && interactions == 0) return "requested_interaction_omitted";
            if (intent.speak && speeches == 0) return "requested_speech_omitted";
            if (intent.express && expressions == 0) return "requested_expression_omitted";
            foreach(var parallel in intent.parallel_requirements??new MissionParallelRequirement[0])
            {
                if(parallel.with_action<0||parallel.with_action>=actionSteps.Count) return "invalid_parallel_action_reference";
                var body=actionSteps[parallel.with_action]; bool matched=false;
                foreach(var side in plan.steps) if(side.kind==parallel.kind&&SameDependencies(side.after,body.after)) matched=true;
                if(!matched) return "explicit_parallel_dependency_not_shared_"+parallel.kind;
            }
            var report = plan.steps[plan.steps.Length - 1];
            if (report.kind != "report") return "report_required";
            foreach (var s in plan.steps) if (s != report && !DependsOn(report, s.id, ids)) return "report_before_completion";
            return null;
        }
        static bool SameDependencies(string[] a,string[] b)
        { var first=new HashSet<string>(a??new string[0]);return first.SetEquals(b??new string[0]); }
        static bool ArrayContains(string[] a, string x) { return Array.IndexOf(a ?? new string[0], x) >= 0; }
        static bool HasNavigationDependency(MissionStep s, string target, Dictionary<string, MissionStep> all)
        { foreach (var p in all.Values) if (p.kind == "navigate" && p.target_id == target && DependsOn(s, p.id, all)) return true; return false; }
        static bool DependsOn(MissionStep step, string id, Dictionary<string, MissionStep> all)
        {
            foreach (string dep in step.after ?? new string[0])
            { if (dep == id) return true; MissionStep p; if (all.TryGetValue(dep, out p) && DependsOn(p, id, all)) return true; }
            return false;
        }
        public static int Resources(string kind)
        {
            return TaskToolRegistry.Resources(kind);
        }
    }
}
