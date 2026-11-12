using System;
using System.Collections.Generic;
using System.Text;

namespace Yixuan.AI.Tasks
{
    [Serializable]
    public sealed class TaskPhaseFact
    {
        public string id = "", kind = "", target = "", slot = "", status = "pending", detail = "";
        public int expected = 1, matched;
        public bool has_receipt;
    }

    /// <summary>
    /// Stage facts derived ONLY from the contract obligations and the REAL tool
    /// receipts, using the same obligation-instance/consumption matching pass as
    /// final completion evaluation. A succeeded receipt can complete at most one
    /// obligation occurrence (generation/branch/order/repeat/exact-angle aware), so
    /// the summary cannot report a not-yet-executed step as completed.
    /// </summary>
    [Serializable]
    public sealed class TaskPhaseSummary
    {
        public string schema = "task-phase-summary.v1";
        public string stage = "idle";
        public string current = "";
        public string completed = "";
        public string next = "";
        public string text = "";
        public string reason = "";
        public string source = "contract.obligations+tool_receipts(shared_match)";
        public int generation = -1;
        public float updated_at;
        public bool terminal;
        public List<TaskPhaseFact> facts = new List<TaskPhaseFact>();
    }

    public static class TaskPhaseSummaryBuilder
    {
        static readonly HashSet<string> TerminalPhases = new HashSet<string>(StringComparer.Ordinal)
        { "succeeded", "failed", "cancelled", "rejected", "blocked" };

        public static TaskPhaseSummary Build(TaskContract contract, List<MissionStepResult> receipts,
            MissionStep active, string phase, int generation, float now, bool terminal = false, string terminalReason = "")
        {
            var summary = new TaskPhaseSummary
            {
                generation = generation,
                updated_at = now,
                reason = terminal ? (terminalReason ?? phase ?? "") : (phase ?? ""),
                terminal = terminal || TerminalPhases.Contains(phase ?? ""),
            };
            if (contract == null || contract.obligations == null)
            {
                summary.stage = "no_contract";
                summary.text = "还没有可执行契约。";
                return summary;
            }
            var match = TaskContractRules.Match(contract, receipts, true, now, generation);
            var completed = new List<string>();
            var pending = new List<TaskObligation>();
            var runningObligation = new TaskObligation();
            foreach (var obligation in contract.obligations)
            {
                if (obligation == null) continue;
                var fact = new TaskPhaseFact
                {
                    id = obligation.id ?? "",
                    kind = obligation.kind ?? "",
                    expected = Math.Max(1, obligation.count),
                    slot = obligation.target_slot ?? "",
                    target = TargetLabel(contract, obligation),
                };
                var instances = InstancesFor(match, obligation);
                int matched = 0;
                foreach (var instance in instances) matched += instance.hits.Count;
                fact.matched = matched;
                fact.has_receipt = matched > 0;
                bool satisfied = matched >= fact.expected;
                bool running = false;
                if (!satisfied && active != null)
                {
                    foreach (var instance in instances)
                    {
                        if (instance.hits.Count >= instance.obligation.count) continue;
                        if (TaskContractRules.Matches(instance.obligation, active)) { running = true; runningObligation = obligation; break; }
                    }
                }
                if (satisfied)
                {
                    fact.status = "satisfied";
                    completed.Add("已" + Label(contract, obligation));
                }
                else if (running)
                {
                    fact.status = "running";
                    pending.Add(obligation);
                }
                else if (summary.terminal)
                {
                    fact.status = "not_executed";
                    pending.Add(obligation);
                }
                else
                {
                    fact.status = "pending";
                    pending.Add(obligation);
                }
                if (!satisfied && fact.expected > 1 && matched > 0)
                    fact.detail = "partial " + matched + "/" + fact.expected;
                summary.facts.Add(fact);
            }
            if (completed.Count > 0)
            {
                int take = Math.Min(completed.Count, 10);
                summary.completed = string.Join("，", completed.GetRange(0, take).ToArray());
            }
            if (active != null)
            {
                summary.stage = StageFor(active.kind);
                string activeLabel = Label(contract, runningObligation != null ? runningObligation : ObligationFor(contract, active));
                summary.current = string.IsNullOrEmpty(activeLabel) ? "正在执行 " + active.kind : "正在" + activeLabel;
                if (summary.terminal) summary.current = "";
            }
            else
            {
                summary.stage = summary.terminal ? (phase ?? "terminal") : PhaseStage(phase);
            }
            if (!summary.terminal)
            {
                TaskObligation nextObligation = null;
                foreach (var candidate in pending)
                {
                    if (active != null && IsSameObligation(candidate, active)) continue;
                    nextObligation = candidate;
                    break;
                }
                if (nextObligation != null) summary.next = "下一步：" + Label(contract, nextObligation);
            }
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(summary.completed)) parts.Add(summary.completed);
            if (!string.IsNullOrEmpty(summary.current)) parts.Add(summary.current);
            if (!string.IsNullOrEmpty(summary.next)) parts.Add(summary.next);
            if (summary.terminal)
            {
                string stop;
                if (string.Equals(phase, "succeeded", StringComparison.Ordinal)) stop = "任务已完成。";
                else if (string.IsNullOrEmpty(terminalReason)) stop = "任务已终止。";
                else stop = "任务已终止：" + terminalReason;
                if (parts.Count > 0) summary.text = string.Join("，", parts.ToArray()) + "；" + stop;
                else summary.text = stop;
            }
            else
            {
                if (parts.Count == 0) parts.Add("任务" + (phase ?? ""));
                summary.text = string.Join("，", parts.ToArray()) + "。";
            }
            return summary;
        }

        static List<TaskObligationMatch> InstancesFor(TaskContractMatch match, TaskObligation original)
        {
            var list = new List<TaskObligationMatch>();
            string prefix = (original.id ?? "") + "__iteration_";
            foreach (var instance in match.obligations)
            {
                if (instance == null || instance.obligation == null) continue;
                string id = instance.obligation.id ?? "";
                if (id == original.id || id.StartsWith(prefix, StringComparison.Ordinal)) list.Add(instance);
            }
            return list;
        }

        static TaskObligation ObligationFor(TaskContract contract, MissionStep active)
        {
            foreach (var obligation in contract.obligations)
            {
                if (obligation == null || obligation.kind != active.kind) continue;
                if (!string.IsNullOrEmpty(obligation.target_slot))
                {
                    if (obligation.target_slot == active.target_slot) return obligation;
                }
                else if (obligation.target_id == active.target_id) return obligation;
            }
            return null;
        }

        static bool IsSameObligation(TaskObligation candidate, MissionStep active)
        {
            if (candidate == null) return false;
            if (candidate.kind != active.kind) return false;
            if (!string.IsNullOrEmpty(candidate.target_slot)) return candidate.target_slot == active.target_slot;
            return candidate.target_id == active.target_id;
        }

        static string TargetLabel(TaskContract contract, TaskObligation obligation)
        {
            if (!string.IsNullOrEmpty(obligation.target_slot))
            {
                var slot = TaskContractRules.FindSlot(contract, obligation.target_slot);
                return slot != null ? (slot.description ?? "") : obligation.target_slot;
            }
            return obligation.target_id ?? "";
        }

        static string Label(TaskContract contract, TaskObligation obligation)
        {
            if (obligation == null) return "";
            string target = TargetLabel(contract, obligation);
            switch (obligation.kind)
            {
                case "select_visible": return "选中" + target;
                case "navigate": return "走近" + (string.IsNullOrEmpty(target) ? "目标" : target);
                case "return": return obligation.target_id == "mission_origin" ? "回到本次任务起点" : "回到原点";
                case "look_at": return "看向" + (string.IsNullOrEmpty(target) ? "目标" : target);
                case "look_direction": return DirectionLabel(obligation);
                case "turn_body": return "身体" + (obligation.yaw >= 0 ? "向右" : "向左") + "转" + Math.Abs(obligation.yaw).ToString("F0") + "度";
                case "gesture": return "做动作" + (obligation.action_id ?? "");
                case "interact": return "使用" + (string.IsNullOrEmpty(target) ? "目标" : target);
                case "sit": return "坐下";
                case "stand": return "站起来";
                case "observe": return "环视观察";
                case "check": return "检查画面";
                case "speak": return "说话";
                case "express": return "做表情";
                case "wait": return "等待";
                case "board_vehicle": return "上车";
                case "drive_vehicle": return "开车前往" + (string.IsNullOrEmpty(target) ? "目的地" : target);
                case "exit_vehicle": return "下车";
                case "stop_vehicle": return "停车";
                case "set_flame": return (obligation.lit ? "点燃" : "熄灭") + "圣火";
                case "query_dynamic": return "查看" + (string.IsNullOrEmpty(target) ? "目标" : target);
                case "move_relative": return "向" + obligation.direction + "移动" + obligation.distance.ToString("F1") + "米";
                default: return string.IsNullOrEmpty(obligation.kind) ? "" : obligation.kind;
            }
        }

        static string DirectionLabel(TaskObligation obligation)
        {
            string direction = obligation.direction == "up" ? "向上" : obligation.direction == "down" ? "向下" :
                obligation.direction == "left" ? "向左" : obligation.direction == "right" ? "向右" : "向前";
            if (obligation.angles_exact || Math.Abs(obligation.yaw) > 0.01f || Math.Abs(obligation.pitch) > 0.01f)
                return "眼睛" + direction + "看 " + Math.Abs(obligation.yaw).ToString("F0") + "度";
            return "眼睛" + direction + "看";
        }

        static string StageFor(string kind)
        {
            switch (kind)
            {
                case "select_visible": return "selecting";
                case "navigate": case "return": case "move_relative": case "route_progress": return "walking";
                case "turn_body": return "body_turn";
                case "look_direction": return "eye_turn";
                case "look_at": return "looking";
                case "observe": case "check": return "observing";
                case "gesture": case "express": return "performing";
                case "interact": return "interacting";
                case "sit": case "stand": return "posture";
                case "speak": return "speaking";
                default: return string.IsNullOrEmpty(kind) ? "running" : kind;
            }
        }

        static string PhaseStage(string phase)
        {
            if (string.IsNullOrEmpty(phase)) return "idle";
            if (phase == "succeeded" || phase == "cancelled" || phase == "failed" || phase == "rejected") return phase;
            if (phase.StartsWith("code_waiting")) return "executing";
            if (phase.StartsWith("code_")) return "planning";
            return phase;
        }
    }
}
