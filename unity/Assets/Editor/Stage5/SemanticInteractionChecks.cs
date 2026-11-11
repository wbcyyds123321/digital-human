using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Yixuan.AI;
using Yixuan.AI.Tasks;
using Yixuan.Stage5;
using Yixuan.World;

namespace Yixuan.EditorTools
{
    public static class SemanticInteractionChecks
    {
        public sealed class ControlledClient : ILLMClient
        {
            public TaskCompletionSource<string> Pending = new TaskCompletionSource<string>();
            public List<ChatMessage> Messages;
            public CancellationToken Token;
            public bool IsBusy { get { return !Pending.Task.IsCompleted; } }
            public string LastError { get { return ""; } }
            public Task<string> ChatAsync(List<ChatMessage> messages, CancellationToken cancellation)
            {
                Messages = new List<ChatMessage>(messages);
                Token = cancellation;
                return Pending.Task;
            }
        }

        public sealed class RecordingClient : ILLMClient
        {
            public ILLMClient Inner;
            public List<object> Responses = new List<object>();
            public bool IsBusy { get { return Inner.IsBusy; } }
            public string LastError { get { return Inner.LastError; } }
            public async Task<string> ChatAsync(List<ChatMessage> messages, CancellationToken cancellation)
            {
                string raw = await Inner.ChatAsync(messages, cancellation);
                Responses.Add(new { input = messages[messages.Count - 1].content, raw, cancelled = cancellation.IsCancellationRequested });
                return raw;
            }
        }

        public static Stage5NearbyContractChecks.Report RunSchema(string output)
        {
            WorldData.EnsureInit();
            var report = new Stage5NearbyContractChecks.Report();
            Action<string, bool> check = (name, pass) => {
                report.checks.Add(new Stage5NearbyContractChecks.Check { name = name, pass = pass });
                if (pass) report.passed++; else report.failed++;
            };
            foreach (string operation in new[] { "sit", "look", "stand", "cancel", "stand_then_navigate" })
            {
                var command = new SemanticInteractionCommand {
                    operation = operation, source = "指令", direction = operation == "sit" || operation == "look" ? "nearby" : "",
                    anchor_id = operation == "stand_then_navigate" ? "msu_main" : ""
                };
                check("valid_" + operation, SemanticInteractionRules.Validate(command, "这是指令", id => WorldData.GetAnchor(id) != null) == null);
            }
            check("missing_command", SemanticInteractionRules.Validate(null, "指令", id => true) != null);
            check("invented_source", SemanticInteractionRules.Validate(new SemanticInteractionCommand { operation = "stand", source = "不存在" }, "指令", id => true) != null);
            check("unknown_operation", SemanticInteractionRules.Validate(new SemanticInteractionCommand { operation = "teleport", source = "指令" }, "指令", id => true) != null);
            check("unknown_anchor", SemanticInteractionRules.Validate(new SemanticInteractionCommand { operation = "stand_then_navigate", source = "指令", anchor_id = "invented" }, "指令", id => WorldData.GetAnchor(id) != null) != null);
            check("invalid_direction", SemanticInteractionRules.Validate(new SemanticInteractionCommand { operation = "sit", source = "指令", direction = "anywhere" }, "指令", id => true) != null);
            check("missing_direction", SemanticInteractionRules.Validate(new SemanticInteractionCommand { operation = "sit", source = "指令" }, "指令", id => true) != null);
            check("unexpected_anchor", SemanticInteractionRules.Validate(new SemanticInteractionCommand { operation = "look", source = "指令", direction = "front", anchor_id = "msu_main" }, "指令", id => true) != null);
            check("unexpected_direction", SemanticInteractionRules.Validate(new SemanticInteractionCommand { operation = "stand", source = "指令", direction = "behind" }, "指令", id => true) != null);
            string validJson = "{\"route\":\"interaction\",\"command\":{\"operation\":\"stand\",\"direction\":\"\",\"anchor_id\":\"\",\"source\":\"指令\"}}";
            check("read_valid_command", SemanticInteractionRules.ReadCommand(validJson) != null);
            check("reject_extra_command_field", SemanticInteractionRules.ReadCommand(validJson.Replace("\"source\"", "\"lua\":\"run()\",\"source\"")) == null);
            check("reject_duplicate_command_field", SemanticInteractionRules.ReadCommand(validJson.Replace("\"source\"", "\"operation\":\"sit\",\"source\"")) == null);
            check("reject_missing_command_field", SemanticInteractionRules.ReadCommand(validJson.Replace("\"direction\":\"\",", "")) == null);
            check("reject_wrong_command_type", SemanticInteractionRules.ReadCommand(validJson.Replace("\"anchor_id\":\"\"", "\"anchor_id\":12")) == null);
            foreach (string direction in new[] { "front", "behind", "nearby" })
            {
                var command = new SemanticInteractionCommand { operation = "sit", direction = direction, source = "歇歇脚" };
                var intent = SemanticInteractionRules.ToIntent(command, "歇歇脚");
                check("intent_" + direction, intent.Kind == (direction == "front" ? Stage5IntentKind.SitFront : direction == "behind" ? Stage5IntentKind.SitBehind : Stage5IntentKind.SitNearby));
                command.operation = "look";
                check("look_never_sit_" + direction, SemanticInteractionRules.ToIntent(command, "歇歇脚").Kind == Stage5IntentKind.LookOnly);
            }
            File.WriteAllText(output, JsonUtility.ToJson(report, true));
            return report;
        }
    }
}
