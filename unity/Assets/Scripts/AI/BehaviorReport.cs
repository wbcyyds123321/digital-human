using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Yixuan.AI
{
    /// <summary>
    /// 一次演出的"要求 vs 实际"报告 —— 给 ChatUI 的行为面板显示，同时把库里缺的东西记到日志。
    ///
    /// 验证阶段动作库/表情库还不全：AI 想做的东西可能库里没有。
    /// 这里把每一项分成三种状态：
    ///   ✔ Ok         —— 要什么就做了什么
    ///   ~ Auto       —— AI 没指定，由 Director 自动补（或本句不做）
    ///   ✘ Missing    —— AI 要了，但库里没有，用了替代品
    /// Missing 的条目追加写进 Tools/AI/missing_requests.jsonl（打包后写到 persistentDataPath），
    /// 攒一段时间就知道该优先补哪些动作/表情。
    /// </summary>
    public class BehaviorReport
    {
        public enum Status { Ok, Auto, Missing }

        public class Row
        {
            public string Label;   // 神态 / 视线 / 表情 / 动作 / 姿态 / 想做
            public string Value;   // 显示文本
            public Status State;
        }

        public string Source;      // LLM / RuleBrain / 保底
        public string Feeling;
        public string Reply;
        public readonly List<Row> Rows = new List<Row>();
        public readonly List<string> MissingItems = new List<string>();
        public bool HasMissing { get { return MissingItems.Count > 0; } }

        void Add(string label, string value, Status st)
        {
            Rows.Add(new Row { Label = label, Value = value, State = st });
        }

        static string EmotionLabel(string id)
        {
            var d = Yixuan.Face.ExpressionLibrary.Get(id);
            return d != null ? d.Label + " " + id : id;
        }

        static string GestureLabel(string id)
        {
            var e = ActionCatalog.GetGesture(id);
            return e != null && !string.IsNullOrEmpty(e.Label) ? e.Label + " " + id : id;
        }

        public static BehaviorReport Build(BehaviorPlan plan, string playedGesture, string gestureNote, string demeanorApplied)
        {
            var r = new BehaviorReport();
            r.Source = plan.source ?? "LLM";
            r.Feeling = plan.feeling;
            r.Reply = plan.reply;

            // ---- 神态 ----
            var dem = DemeanorCatalog.Get(demeanorApplied);
            if (!string.IsNullOrEmpty(plan.askedDemeanor) && !DemeanorCatalog.Has(plan.askedDemeanor))
            {
                r.Add("神态", "要「" + plan.askedDemeanor + "」→ 库里没有，用 " + dem.Label, Status.Missing);
                r.MissingItems.Add("demeanor:" + plan.askedDemeanor);
            }
            else
            {
                r.Add("神态", dem.Label + " " + dem.Id, string.IsNullOrEmpty(plan.askedDemeanor) ? Status.Auto : Status.Ok);
            }

            r.Add("视线", GazeLabel(plan.gaze), Status.Ok);

            // ---- 表情 ----
            string em = string.IsNullOrEmpty(plan.emotion) ? "neutral" : plan.emotion;
            if (!string.IsNullOrEmpty(plan.askedEmotion) && plan.askedEmotion != em)
            {
                r.Add("表情", "要「" + plan.askedEmotion + "」→ 库里没有，用 " + EmotionLabel(em), Status.Missing);
                r.MissingItems.Add("emotion:" + plan.askedEmotion);
            }
            else
            {
                r.Add("表情", EmotionLabel(em) + "  强度 " + plan.intensity.ToString("F1"),
                      string.IsNullOrEmpty(plan.askedEmotion) ? Status.Auto : Status.Ok);
            }

            // ---- 动作 ----
            string played = string.IsNullOrEmpty(playedGesture) ? null : playedGesture;
            if (!string.IsNullOrEmpty(plan.askedGesture) && !ActionCatalog.HasGesture(plan.askedGesture))
            {
                r.Add("动作", "要「" + plan.askedGesture + "」→ 库里没有，" +
                      (played != null ? "改做 " + GestureLabel(played) : "不做动作"), Status.Missing);
                r.MissingItems.Add("gesture:" + plan.askedGesture);
            }
            else if (!string.IsNullOrEmpty(plan.askedGesture))
            {
                r.Add("动作", GestureLabel(plan.askedGesture) + (string.IsNullOrEmpty(gestureNote) ? "" : "（" + gestureNote + "）"), Status.Ok);
            }
            else
            {
                r.Add("动作", (played != null ? GestureLabel(played) + "  " : "") + (gestureNote ?? "本句不做动作"), Status.Auto);
            }

            if (!string.IsNullOrEmpty(plan.posture))
                r.Add("姿态", plan.posture, Status.Ok);

            // ---- 想做但库里没有（AI 自己说的）----
            if (!string.IsNullOrEmpty(plan.wish))
            {
                r.Add("想做", plan.wish + "（库里没有）", Status.Missing);
                r.MissingItems.Add("wish:" + plan.wish);
            }
            return r;
        }

        static string GazeLabel(string g)
        {
            switch (g)
            {
                case "away": return "移开视线 away";
                case "down": return "看下面 down";
                case "up": return "看上方 up";
                default: return "看着你 user";
            }
        }

        // ---------------- 缺失素材日志 ----------------

        public static string MissingLogPath
        {
            get
            {
#if UNITY_EDITOR
                return Path.Combine(Directory.GetCurrentDirectory(), "Tools/AI/missing_requests.jsonl");
#else
                return Path.Combine(Application.persistentDataPath, "missing_requests.jsonl");
#endif
            }
        }

        public void AppendToMissingLog()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"time\":\"").Append(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('"');
                sb.Append(",\"source\":\"").Append(Esc(Source)).Append('"');
                sb.Append(",\"missing\":[");
                for (int i = 0; i < MissingItems.Count; i++)
                    sb.Append(i > 0 ? "," : "").Append('"').Append(Esc(MissingItems[i])).Append('"');
                sb.Append("],\"feeling\":\"").Append(Esc(Feeling)).Append('"');
                sb.Append(",\"reply\":\"").Append(Esc(Reply)).Append("\"}");

                string path = MissingLogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, sb.ToString() + "\n", new UTF8Encoding(false));
                Debug.Log("[Director] 库里缺：" + string.Join("，", MissingItems.ToArray()) + "（已记到 " + path + "）");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Director] 写缺失日志失败：" + e.Message);
            }
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
        }
    }
}
