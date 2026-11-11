using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Yixuan.World;

namespace Yixuan.AI.Tasks
{
    public static class NamedAnchorCommand
    {
        static readonly Regex Command = new Regex(@"^(?:(?:请你|麻烦你|你|请|现在|先|那就|那)\s*)*(?:走到|走向|前往|去往|导航到|移动到|回到|去|到)\s*(.+?)\s*$");
        static readonly Regex Suffix = new Regex(@"(?:那边|那里|那儿|这边|这里|这儿|附近|一下|吧|啊|呀|啦|[。！!\s])+$");
        static readonly Regex NonCommand = new Regex(@"如果|假如|要是|能不能|可不可以|有没有|是否|是不是|吗|呢|[？?“”‘’""]|不要|不必|别|不用|我刚|刚才|昨天|他说|她说|我说|比如|例如");
        static readonly Regex MultipleActions = new Regex(@"[，,；;。].*\S|然后|接着|随后|之后|并且|同时|顺便|再(?:去|到|走|看|坐|起)|坐下|坐一|起身|站起来");

        public static bool TryResolve(string input, out string anchorId, out string label, out string error)
        {
            anchorId = label = error = null;
            string text = (input ?? "").Trim();
            if (NonCommand.IsMatch(text) || MultipleActions.IsMatch(text) || MissionIntent.LooksLikeOpenMovement(text)) return false;
            var command = Command.Match(text);
            if (!command.Success) return false;
            string target = Suffix.Replace(command.Groups[1].Value.Trim(), "");
            if (target.Length == 0) return false;
            WorldData.EnsureInit();
            var matches = new List<WorldAnchor>();
            foreach (var anchor in WorldData.Anchors)
            {
                if (anchor == null || string.IsNullOrEmpty(anchor.Id) || string.IsNullOrEmpty(anchor.Label)) continue;
                string shortName = anchor.Label.EndsWith("主楼", StringComparison.Ordinal)
                    ? anchor.Label.Substring(0, anchor.Label.Length - 2) : anchor.Label;
                if (string.Equals(target, anchor.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target, anchor.Label, StringComparison.Ordinal) ||
                    string.Equals(target, shortName, StringComparison.Ordinal)) matches.Add(anchor);
            }
            if (matches.Count == 0) return false;
            if (matches.Count != 1) { error = "这个地点名对应多个位置，请说完整名称。"; return true; }
            anchorId = matches[0].Id;
            label = matches[0].Label;
            return true;
        }
    }
}
