using System;
using Newtonsoft.Json.Linq;
using Yixuan.Stage5;

namespace Yixuan.AI.Tasks
{
    [Serializable]
    public sealed class SemanticInteractionCommand
    {
        public string operation;
        public string direction;
        public string anchor_id;
        public string source;
        // B: generic visual-target semantic extension. Only used when
        // operation == "visual_target"; kept as plain strings so the semantic
        // layer, not a sentence-keyword table, carries the target/action/constraints.
        public string target;       // natural-language target description from the user
        public string actions;      // CSV: select,query,approach,sit,stand,board
        public string constraints;  // CSV: leftmost,near_crosshair,no_move,no_board,no_sit,pronoun,current_view,any_match
    }

    public sealed class SemanticVisualTarget
    {
        public string target = "";
        public string actions = "";
        public string constraints = "";
        public string source = "";
        public string original = "";
    }

    public static class SemanticInteractionRules
    {
        static readonly string[] VisualActions = { "select", "query", "approach", "sit", "stand", "board" };
        static readonly string[] VisualConstraints = { "leftmost", "near_crosshair", "no_move", "no_board", "no_sit", "pronoun", "current_view", "any_match" };

        public static SemanticInteractionCommand ReadCommand(string json)
        {
            string ignored;
            return ReadCommand(json, out ignored);
        }

        public static SemanticInteractionCommand ReadCommand(string json, out string error)
        {
            error = null;
            try
            {
                var root = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                var command = root["command"] as JObject;
                if (command == null) { error = "missing_command_object"; return null; }
                bool visual = false;
                var op = command["operation"];
                if (op != null && op.Type == JTokenType.String) visual = (string)op == "visual_target";
                int allowed = visual ? 7 : 4;
                int count = 0;
                foreach (var property in command.Properties())
                {
                    if (visual)
                    {
                        if (property.Name != "operation" && property.Name != "direction" && property.Name != "anchor_id" &&
                            property.Name != "source" && property.Name != "target" && property.Name != "actions" &&
                            property.Name != "constraints") { error = "unexpected_command_field:" + property.Name; return null; }
                    }
                    else
                    {
                        if (property.Name != "operation" && property.Name != "direction" && property.Name != "anchor_id" &&
                            property.Name != "source") { error = "unexpected_command_field:" + property.Name; return null; }
                    }
                    if (property.Value.Type != JTokenType.String) { error = "command_field_not_string:" + property.Name; return null; }
                    count++;
                }
                if (count != allowed) { error = "command_field_count:expected=" + allowed + ",actual=" + count; return null; }
                return command.ToObject<SemanticInteractionCommand>();
            }
            catch (Exception exception) { error = "invalid_command_json:" + exception.GetType().Name; return null; }
        }

        public static string Validate(SemanticInteractionCommand command, string input, Func<string, bool> anchorExists)
        {
            if (command == null) return "missing_interaction_command";
            if (string.IsNullOrWhiteSpace(command.source) || command.source.Length > 1000 ||
                (input ?? "").IndexOf(command.source, StringComparison.Ordinal) < 0) return "source_not_in_request";
            if (command.operation == "visual_target")
            {
                if (string.IsNullOrWhiteSpace(command.target) || command.target.Length > 200) return "missing_visual_target";
                if (!string.IsNullOrEmpty(command.direction)) return "unexpected_visual_direction";
                if (!string.IsNullOrEmpty(command.anchor_id)) return "unexpected_visual_anchor";
                string invalidAction = ValidateCsv(command.actions, VisualActions, false);
                if (invalidAction != null) return invalidAction;
                string invalidConstraint = ValidateCsv(command.constraints, VisualConstraints, true);
                return invalidConstraint;
            }
            bool visual = command.operation == "sit" || command.operation == "look";
            if (!visual && command.operation != "stand" && command.operation != "stand_then_navigate" &&
                command.operation != "cancel") return "unknown_interaction_operation";
            if (visual && command.direction != "front" && command.direction != "behind" && command.direction != "nearby")
                return "invalid_interaction_direction";
            if (!visual && !string.IsNullOrEmpty(command.direction)) return "unexpected_interaction_direction";
            if (command.operation == "stand_then_navigate")
            {
                if (string.IsNullOrEmpty(command.anchor_id) || anchorExists == null || !anchorExists(command.anchor_id))
                    return "unknown_navigation_anchor";
            }
            else if (!string.IsNullOrEmpty(command.anchor_id)) return "unexpected_navigation_anchor";
            return null;
        }

        static string ValidateCsv(string csv, string[] allowed, bool allowEmpty)
        {
            if (string.IsNullOrEmpty(csv)) return allowEmpty ? null : "missing_visual_actions";
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var raw in csv.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = raw.Trim().ToLowerInvariant();
                if (token.Length == 0) continue;
                bool ok = false;
                foreach (var a in allowed) if (a == token) { ok = true; break; }
                if (!ok) return "invalid_visual_token:" + token;
                if (!seen.Add(token)) return "duplicate_visual_token:" + token;
            }
            if (seen.Count == 0) return allowEmpty ? null : "missing_visual_actions";
            return null;
        }

        public static SemanticVisualTarget ToVisualTarget(SemanticInteractionCommand command, string input)
        {
            return new SemanticVisualTarget
            {
                target = command.target ?? "",
                actions = command.actions ?? "",
                constraints = command.constraints ?? "",
                source = command.source ?? "",
                original = input ?? "",
            };
        }

        public static Stage5Intent ToIntent(SemanticInteractionCommand command, string input)
        {
            Stage5IntentKind kind = command.operation == "look" ? Stage5IntentKind.LookOnly :
                command.operation == "stand" ? Stage5IntentKind.Stand :
                command.operation == "cancel" ? Stage5IntentKind.Cancel :
                command.direction == "front" ? Stage5IntentKind.SitFront :
                command.direction == "behind" ? Stage5IntentKind.SitBehind : Stage5IntentKind.SitNearby;
            return new Stage5Intent { Kind = kind, Direction = command.direction ?? "", Original = input, Reason = "core_llm_semantics" };
        }
    }
}
