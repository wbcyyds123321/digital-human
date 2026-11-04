using System.Collections.Generic;
using System.Text;

namespace Yixuan.AI
{
    /// <summary>
    /// 神态库（输出契约 v2 的 demeanor 字段）。
    ///
    /// 神态 = 视线 + 头部姿态 + 眨眼节奏 的组合预设 —— 不是表情（脸部形态键），也不是动作（骨骼动画）。
    /// 它是"同一个表情、同一句话"之间的差别：同样是微笑，害羞时低头侧倾、眨眼勤；自信时微微抬头、眨眼慢。
    ///
    /// 数据驱动：加一种神态只要往 Table 里加一行，prompt 会自动把它列进【可用神态】。
    ///
    /// 头部角度的方向（Director 在角色根节点坐标系里施加）：
    ///   Pitch &gt; 0 低头，&lt; 0 抬头；Roll &gt; 0 向角色左肩侧倾。
    /// </summary>
    public static class DemeanorCatalog
    {
        public class Demeanor
        {
            public string Id;
            public string Label;
            public string DefaultGaze;      // user / away / down / up
            public float Pitch;             // 头部俯仰（度）
            public float Roll;              // 头部侧倾（度）
            public float BlinkRate;         // 眨眼频率倍率：&gt;1 眨得勤（间隔变短），&lt;1 眨得少
            public string[] Emotions;       // 建议搭配的表情（只作 prompt 提示，不强制）

            public Demeanor(string id, string label, string gaze, float pitch, float roll, float blink, params string[] emotions)
            {
                Id = id; Label = label; DefaultGaze = gaze; Pitch = pitch; Roll = roll; BlinkRate = blink; Emotions = emotions;
            }
        }

        public const string DefaultId = "attentive";

        static readonly Demeanor[] Table =
        {
            new Demeanor("attentive", "专注倾听", "user",  0f,  3f, 1.0f, "neutral", "smile"),
            new Demeanor("shy",       "害羞",     "down",  8f,  6f, 1.4f, "shy", "smile"),
            new Demeanor("thinking",  "思考",     "up",   -6f,  8f, 0.7f, "serious", "troubled"),
            new Demeanor("confident", "自信",     "user", -4f,  0f, 0.8f, "smug", "smile"),
            new Demeanor("tired",     "疲惫",     "down",  6f,  0f, 0.6f, "troubled"),
            new Demeanor("happy",     "开心",     "user", -2f,  5f, 1.2f, "smile", "laugh"),
            new Demeanor("down",      "低落",     "down", 10f,  0f, 0.8f, "sad"),
            new Demeanor("surprised", "惊讶",     "user", -5f,  0f, 0.3f, "surprised", "shock"),
            new Demeanor("wary",      "警觉",     "away",  0f,  0f, 0.5f, "serious", "angry"),
            new Demeanor("relaxed",   "放松",     "away",  2f,  4f, 1.0f, "neutral", "smile"),
        };

        static Dictionary<string, Demeanor> _map;

        static void EnsureInit()
        {
            if (_map != null) return;
            _map = new Dictionary<string, Demeanor>();
            foreach (var d in Table) _map[d.Id] = d;
        }

        public static IList<Demeanor> All { get { return Table; } }

        public static bool Has(string id)
        {
            EnsureInit();
            return !string.IsNullOrEmpty(id) && _map.ContainsKey(id);
        }

        /// <summary>取神态；未知 id 返回 attentive（永不返回 null）</summary>
        public static Demeanor Get(string id)
        {
            EnsureInit();
            Demeanor d;
            if (!string.IsNullOrEmpty(id) && _map.TryGetValue(id, out d)) return d;
            return _map[DefaultId];
        }

        /// <summary>LLM 没给神态时（例如 RuleBrain 的离线回复），按表情反推一个最贴近的神态</summary>
        public static string ForEmotion(string emotion)
        {
            if (string.IsNullOrEmpty(emotion) || emotion == "neutral") return DefaultId;
            foreach (var d in Table)
                if (d.Emotions != null && d.Emotions.Length > 0 && d.Emotions[0] == emotion) return d.Id;
            foreach (var d in Table)
                if (d.Emotions != null && System.Array.IndexOf(d.Emotions, emotion) >= 0) return d.Id;
            return DefaultId;
        }

        /// <summary>给 prompt 用：每行「id 中文名（常配表情：…）」</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            foreach (var d in Table)
            {
                sb.Append(d.Id).Append(' ').Append(d.Label);
                if (d.Emotions != null && d.Emotions.Length > 0)
                    sb.Append("（常配表情：").Append(string.Join("/", d.Emotions)).Append('）');
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
    }
}
