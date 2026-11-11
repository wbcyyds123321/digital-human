using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Yixuan.AI.Tasks;
using Yixuan.Stage4;
using Yixuan.Stage5;
using Yixuan.World;

namespace Yixuan.EditorTools
{
    public static class Stage5NearbyContractChecks
    {
        [Serializable]
        public sealed class Check
        {
            public string name;
            public bool pass;
            public string detail;
        }

        [Serializable]
        public sealed class Report
        {
            public int passed;
            public int failed;
            public List<Check> checks = new List<Check>();
        }

        public static Report RunAll(string output)
        {
            var report = new Report();
            Action<string, bool, string> check = (name, pass, detail) =>
            {
                report.checks.Add(new Check { name = name, pass = pass, detail = detail });
                if (pass) report.passed++; else report.failed++;
            };
            var cases = new[]
            {
                new[] { "去附近找一个椅子坐下", "SitNearby", "nearby" },
                new[] { "找一下附近的椅子坐下", "SitNearby", "nearby" },
                new[] { "你找一下你附近的凳子坐下", "SitNearby", "nearby" },
                new[] { "站的有点久了你去前面那椅子上面坐一会儿吧", "SitFront", "front" },
                new[] { "站得有点久了，你去前面那椅子上面坐一会儿吧。", "SitFront", "front" },
                new[] { "有点累了，你先找附近的长椅坐会儿。", "SitNearby", "nearby" },
                new[] { "请去前面的椅子上坐一坐。", "SitFront", "front" },
                new[] { "你去身后那张椅子坐几分钟。", "SitBehind", "behind" },
                new[] { "找把椅子坐下。", "SitNearby", "nearby" },
                new[] { "去周围找一张长椅坐下。", "SitNearby", "nearby" },
                new[] { "去你面前的那张椅子坐下。", "SitFront", "front" },
                new[] { "去身后附近的椅子坐下。", "SitBehind", "behind" },
                new[] { "看看前面的椅子，先别过去。", "LookOnly", "front" },
                new[] { "找一下附近的椅子，先别过去。", "LookOnly", "nearby" },
                new[] { "你面前椅子坐着的人是谁", "None", "" },
                new[] { "你在附近的椅子坐下了", "None", "" },
                new[] { "我刚在椅子上坐下了", "None", "" },
                new[] { "能不能去附近找椅子坐下？", "None", "" },
                new[] { "不要去附近找椅子坐下", "None", "" },
                new[] { "站得有点久了，你不要去前面那椅子坐一会儿。", "None", "" },
                new[] { "请你不要去附近找椅子坐一会儿。", "None", "" },
                new[] { "站得有点久了，你去前面那椅子上坐一会儿吗？", "None", "" },
                new[] { "他说你去前面那椅子上面坐一会儿吧", "None", "" },
                new[] { "我看见你去前面那椅子上坐一会儿", "None", "" },
                new[] { "请读出“你去椅子上坐一会儿”这句话", "None", "" },
                new[] { "你附近有没有椅子开眼地图", "None", "" },
                new[] { "你去前面那椅子坐一会儿，再走到公交站", "Unsupported", "" },
                new[] { "去前面的椅子坐下，再起身走到公交站", "Unsupported", "" },
                new[] { "起身。", "Stand", "" },
                new[] { "别做了站起来", "Stand", "" },
                new[] { "那是走啊起来啊", "Stand", "" },
                new[] { "你现在站起来吧", "Stand", "" },
                new[] { "别坐了，站起来。", "Stand", "" },
                new[] { "请你起身。", "Stand", "" },
                new[] { "起来啊", "Stand", "" },
                new[] { "我已经站起来了", "None", "" },
                new[] { "不要站起来", "None", "" },
                new[] { "你能站起来吗？", "None", "" },
                new[] { "他说站起来", "None", "" },
                new[] { "起身后去国立大学", "None", "" },
                new[] { "站起来再去国立大学", "None", "" },
                new[] { "停下。", "Cancel", "" },
            };
            foreach (var sample in cases)
            {
                var intent = Stage5IntentParser.Parse(sample[0]);
                check(sample[0], intent.Kind.ToString() == sample[1] && intent.Direction == sample[2], intent.ToString());
            }

            var navigationCases = new[]
            {
                new[] { "你现在去国立大学那边", "msu_main" },
                new[] { "去国立大学主楼。", "msu_main" },
                new[] { "前往msu_main", "msu_main" },
                new[] { "请你现在走到国立大学那边吧。", "msu_main" },
                new[] { "你现在去国立大学那边吗？", "" },
                new[] { "不要去国立大学", "" },
                new[] { "如果去国立大学", "" },
                new[] { "他说去国立大学", "" },
                new[] { "去“国立大学”这句话是什么意思", "" },
                new[] { "去国立大学，然后坐下", "" },
                new[] { "去国立大学再去公交站", "" },
                new[] { "去不存在的地方", "" },
                new[] { "向前走五米", "" },
            };
            foreach (var sample in navigationCases)
            {
                string anchor, label, error;
                bool found = NamedAnchorCommand.TryResolve(sample[0], out anchor, out label, out error);
                check("destination_" + sample[0], sample[1].Length == 0 ? !found : found && error == null && anchor == sample[1],
                    "found=" + found + ";anchor=" + anchor + ";error=" + error);
            }

            Rect crop = new Rect(0.25f, 0.125f, 0.5f, 0.75f);
            check("crop_top_left", Vector2.Distance(Stage5EyeCapture.ToFrameUv(crop, 0f, 0f), new Vector2(0.25f, 0.125f)) < 1e-6f, "");
            check("crop_bottom_right", Vector2.Distance(Stage5EyeCapture.ToFrameUv(crop, 1f, 1f), new Vector2(0.75f, 0.875f)) < 1e-6f, "");
            check("crop_center", Vector2.Distance(Stage5EyeCapture.ToFrameUv(crop, 0.5f, 0.5f), new Vector2(0.5f, 0.5f)) < 1e-6f, "");
            check("full_frame_identity", Vector2.Distance(Stage5EyeCapture.ToFrameUv(new Rect(0f, 0f, 1f, 1f), 0.3f, 0.7f), new Vector2(0.3f, 0.7f)) < 1e-6f, "");

            if (!Application.isPlaying) throw new InvalidOperationException("Play mode is required for navigation checks");
            var seat = Stage4SeatController.Instance;
            var method = typeof(Stage4SeatController).GetMethod("TryNavigationApproach",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            foreach (var entry in Stage4SeatData.Entries)
            {
                var candidate = Stage5SeatRegistry.Find(entry.entity_id);
                object[] arguments = { entry, Vector3.zero };
                bool valid = (bool)method.Invoke(seat, arguments);
                Vector3 stage4Target = (Vector3)arguments[1];
                var path = WorldData.FindPath(new Vector3(-2.453f, 0.155f, -54.275f), candidate.ApproachPoint);
                check("navigation_" + entry.entity_id,
                    valid && Vector3.Distance(candidate.ApproachPoint, stage4Target) < 0.0001f &&
                    WorldData.IsWalkable(candidate.ApproachPoint) && path != null && path.Count > 0,
                    candidate.ApproachPoint.ToString("F4"));
            }
            if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, JsonUtility.ToJson(report, true));
            return report;
        }
    }
}
